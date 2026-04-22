# AutoCommerce

Modular dropshipping automation platform. Implements the *AutoCommerce Brain* orchestrator
plus the *Niche & Product Selection* and *Supplier Selection & Evaluation* modules from the
project brief. Further modules (storefront sync, payments, tracking, marketing, etc.) plug in
behind the same event/API contracts without touching what's already shipped.

## Layout

```
ecommence/
├── AutoCommerce.sln
├── src/
│   ├── AutoCommerce.Shared/             # DTOs, events, contracts shared across services
│   ├── AutoCommerce.Brain/              # Central orchestrator (.NET 8 Web API + EF Core + SQLite)
│   ├── AutoCommerce.ProductSelection/   # Niche & Product Selection module (.NET 8 Web API + BackgroundService)
│   └── AutoCommerce.SupplierSelection/  # Supplier Selection & Evaluation module (.NET 8 Web API + BackgroundService)
├── tests/
│   ├── AutoCommerce.Brain.Tests/              # Integration tests against the Brain HTTP API
│   ├── AutoCommerce.ProductSelection.Tests/   # Unit tests for scoring + orchestrator
│   └── AutoCommerce.SupplierSelection.Tests/  # Unit tests for evaluator, selector, fulfillment, worker
└── dashboard/                           # React + Vite + Tailwind dashboard UI
```

## Quick start

Prerequisites: .NET 8 SDK, Node 20+.

```bash
# 1. Run the Brain (listens on http://localhost:5080)
dotnet run --project src/AutoCommerce.Brain

# 2. Run the Product Selection module (listens on http://localhost:5090)
dotnet run --project src/AutoCommerce.ProductSelection

# 3. Run the Supplier Selection module (listens on http://localhost:5100)
dotnet run --project src/AutoCommerce.SupplierSelection

# 4. Run the dashboard
cd dashboard && npm install && npm run dev
```

Default master API key is `dev-master-key-change-me` (override via `ApiKey__Master`
env variable or `appsettings.json`). Paste it into the dashboard's top-right field,
click Save, and it persists in localStorage.

Open the dashboard, save the Brain API key in the top-right field, then go to
**Configuration** to set the recommendation provider/model and selection thresholds.
Those settings are applied to the next **Recommendations → Run scan & import** action.
Approved products will then appear in the Products tab with auto-applied pricing.

## Docker Compose

From the repository root:

```bash
docker compose up --build
```

Services:

- Dashboard: http://localhost:3000
- AutoCommerce Brain API + Swagger: http://localhost:5080 and http://localhost:5080/swagger
- Product Selection API + Swagger: http://localhost:5090 and http://localhost:5090/swagger

Compose defaults:

- The dashboard proxies `/brain-api/*` to the Brain and `/selection-api/*` to the Product Selection service, so the browser never needs container hostnames.
- The Brain SQLite database is persisted in the named Docker volume `brain-data`.
- The Product Selection runtime configuration is persisted in SQLite in the named Docker volume `product-selection-data`.
- Override the default API key with `AUTOCOMMERCE_MASTER_API_KEY`.
- Set `OPENAI_API_KEY`, `GEMINI_API_KEY`, or `GOOGLE_API_KEY` as initial fallbacks, or save provider credentials later from the dashboard Configuration tab.
- Enable the scheduled scanner with `AUTOCOMMERCE_SCANNER_ENABLED=true` if you want background scans in the compose stack.

## AutoCommerce Brain

**Port:** 5080 · **Swagger:** http://localhost:5080/swagger

The Brain is the single source of truth and the only service that talks to the
database. Modules interact via its HTTP API and a shared event bus.

### Responsibilities implemented

| Responsibility | Implementation |
|---|---|
| Event-driven orchestrator | `InMemoryEventBus` with pluggable `IEventBus` interface (swap for RabbitMQ/Service Bus by binding a different implementation). `EventRecorder` persists every event and subscribes to `supplier.price_changed` to re-run pricing. |
| Product engine | `Product` entity + `ProductService` + `/api/products` CRUD. `/api/products/import` is idempotent on `externalId`. |
| Pricing & margin engine | `PricingEngine` applies per-category rules (markup multiplier, min margin, price clamps). Pauses product + emits `margin.alert` when margin falls below threshold. |
| Order router | Shopify-compatible webhook at `/api/webhooks/shopify/order-created`, `/api/orders` CRUD, `/api/orders/{id}/tracking` for fulfillment updates. |
| Data store & analytics | EF Core + SQLite (file `autocommerce.db`). `/api/dashboard/metrics` returns 24h revenue/profit, margins, top products, recent events. |
| Dashboard UI | React + Tailwind SPA (`dashboard/`). Tabs: Dashboard, Products, Orders, Recommendations, Configuration. |
| Auth | API-key scheme (`X-Api-Key` header) with master key from config and per-module keys in `ApiKey` table. |

### Key endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/health` | Liveness probe (no auth) |
| GET/POST/PATCH/DELETE | `/api/products[/{id}]`, `/api/products/import` | Catalog CRUD + module import |
| GET/POST/PATCH | `/api/orders[/{id}][/tracking]`, `/api/orders/by-shop/{id}` | Order lifecycle |
| GET/PUT | `/api/pricing/rules`, `/api/pricing/price/{productId}` | Pricing rules + manual price override |
| GET/POST | `/api/events`, `/api/events/publish` | Event log query (supports `?type=`, `?since=`, `?includePayload=true`) and publish hook for modules |
| POST | `/api/products/{id}/assign-supplier` | Supplier assignment (used by Supplier Selection module); re-runs pricing and emits `supplier.selected` |
| GET | `/api/dashboard/metrics` | Aggregated KPIs |
| POST | `/api/webhooks/shopify/order-created`, `/api/webhooks/stripe/payment` | External platform hooks |

### Events emitted (on the bus, persisted to `EventLogs`)

`product.discovered`, `product.approved`, `product.paused`, `product.kill`,
`supplier.selected`, `supplier.price_changed`, `supplier.stock_changed`,
`price.updated`, `margin.alert`, `order.created`, `order.sent_to_supplier`,
`order.fulfilled`, `order.fulfillment_failed`, `payment.succeeded`, `payment.failed`.

Modules can subscribe in-process (inject `IEventBus`) or post externally via
`POST /api/events/publish`.

## Niche & Product Selection module

**Port:** 5090 · **Swagger:** http://localhost:5090/swagger

A standalone service that crawls candidate sources, scores each candidate,
filters top N per category, and imports approved products into the Brain.

### What's in place

- **Scoring engine** (`Scoring/ScoringEngine.cs`) — weighted combination of demand
  (reviews + rating + search volume), competition (inverse competitor count),
  shipping availability to target market, and gross margin. Fully unit-tested.
- **Filters** — min price/max price/min score/max shipping/category membership and
  `TopNPerCategoryFilter`.
- **Candidate source** — a real-time AI-backed `ICandidateSource` that uses
  live web search to find current product opportunities, estimate market signals,
  and return candidates in the same contract shape consumed by the existing
  scoring and import pipeline. OpenAI is used when an OpenAI key is saved;
  otherwise the service falls back to Gemini. If both are saved, a model name
  starting with `gemini` forces Gemini.
- **Orchestrator** — pulls from every source, scores, filters, and calls the Brain's
  `/api/products/import` endpoint for every approved candidate, then emits a
  `product.discovered` event via `/api/events/publish`.
- **Scheduled scanner** — `BackgroundService` that runs on a configurable interval
  (default 6h, disabled by default in dev — toggle `Scanner.Enabled`).
- **REST API** — `GET /recommendations` for the dashboard, `POST /scan` to manually
  trigger a discover-and-import cycle.

### Configuration (`appsettings.json`)

```jsonc
{
  "Brain": { "BaseUrl": "http://localhost:5080/", "ApiKey": "dev-master-key-change-me" },
  "Selection": {
    "TargetCategories": [ "electronics", "wellness", "home-decor", "kitchen", "fitness", "automotive" ],
    "MinPrice": 10,
    "MaxPrice": 150,
    "MinScore": 55,
    "TopNPerCategory": 3,
    "TargetMarket": "IE",
    "MaxShippingDays": 18
  },
  "Scanner": { "Enabled": false, "IntervalMinutes": 360, "StartupDelaySeconds": 15 }
}
```

Runtime updates from the dashboard are persisted separately from `appsettings.json` in
`App_Data/recommendation-settings.db` locally, or `/app/data/recommendation-settings.db`
inside Docker when using Compose. On first startup after upgrading, the service will
import any existing `recommendation-settings.json` file into SQLite automatically.

## Supplier Selection & Evaluation module

**Port:** 5100 · **Swagger:** http://localhost:5100/swagger

Standalone service that picks the best supplier for every product the Brain discovers,
assigns it back, and handles supplier-side fulfillment simulation.

### What's in place

- **Supplier catalog** (`Domain/SupplierCatalog.cs`) — seeded registry of known suppliers
  (AliExpress, CJ Dropshipping, Spocket, Zendrop, BigBuy, Printful, Amazon Prime, …) with
  regions and `BaseReliability` used in scoring and fulfillment success probability.
- **Supplier evaluator** (`Evaluation/SupplierEvaluator.cs`) — weighted score per listing:
  price (relative to candidate set), rating, shipping days vs target, stock, and registered
  reliability. Marks listings as non-viable when shipping exceeds max, stock below min, or
  score below threshold. Fully unit-tested.
- **Supplier selector** (`Evaluation/SupplierSelector.cs`) — picks the highest-scoring viable
  listing and returns a full evaluation breakdown for transparency.
- **Brain client** — polls `/api/events?type=product.discovered&since=…&includePayload=true`,
  calls `POST /api/products/{id}/assign-supplier` to commit the selection, and can publish
  `supplier.price_changed`, `supplier.stock_changed`, `order.sent_to_supplier`,
  `order.fulfillment_failed` events back through `/api/events/publish`.
- **ProductDiscoveredWorker** (`Services/ProductDiscoveredWorker.cs`) — `BackgroundService`
  that polls for new `product.discovered` events on a configurable interval and runs the
  evaluator + selector for each one.
- **Fulfillment service** — `POST /fulfill-order` accepts a `FulfillmentRequest`, simulates
  a supplier API call (success probability driven by supplier reliability or a forced
  override for tests), returns a `FulfillmentResult` with tracking info, and emits the
  appropriate `order.sent_to_supplier` or `order.fulfillment_failed` event.
- **Supplier price monitor** (`Services/SupplierPriceMonitor.cs`) — opt-in `BackgroundService`
  that randomly simulates supplier cost changes and publishes `supplier.price_changed` —
  which the Brain's `EventRecorder` already subscribes to in order to re-run pricing.

### Key endpoints

| Method | Path | Purpose |
|---|---|---|
| GET  | `/health` | Liveness probe |
| GET  | `/suppliers`, `/suppliers/{key}` | Registered supplier catalog |
| GET  | `/selection/{productId}/preview` | Evaluate suppliers without committing |
| POST | `/selection/{productId}/assign` | Evaluate, pick, and assign via the Brain |
| POST | `/fulfill-order` | Submit an order to the selected supplier (simulated) |

### Configuration (`appsettings.json`)

```jsonc
{
  "Brain": { "BaseUrl": "http://localhost:5080/", "ApiKey": "dev-master-key-change-me" },
  "Selection": { "MinScore": 40, "MaxShippingDays": 21, "MinStock": 10, "TargetMarket": "IE" },
  "Fulfillment": { "MinDeliveryDays": 4, "MaxDeliveryDays": 14, "RandomSeed": 0 },
  "DiscoveryWorker": { "Enabled": true, "IntervalSeconds": 30, "StartupDelaySeconds": 10 },
  "PriceMonitor": { "Enabled": false, "IntervalMinutes": 60, "ChangeProbability": 0.10, "MaxChangePercent": 0.15 }
}
```

## Dashboard

`dashboard/` is a React 19 + Vite + TypeScript + Tailwind 3 SPA.

- **Dashboard** tab — live KPIs (refreshes every 10s), top products, recent events.
- **Products** tab — list with filter by status, inline Pause/Activate/Kill actions.
- **Orders** tab — orders list with tracking links.
- **Recommendations** tab — pulls `GET /recommendations` from the selection
  module and allows manual *Run scan & import* to push approved ones into the Brain.
- **Configuration** tab — updates the recommendation provider key/model, stores
  OpenAI, Gemini, and other named secrets in SQLite, shows masked saved-key previews,
  and changes the selection thresholds used by the next preview/scan.

The API key is stored in `localStorage` and sent on every Brain request.

## Tests

```bash
dotnet test
```

- `AutoCommerce.Brain.Tests` — 11 integration tests over the real HTTP pipeline
  (auth, products CRUD, pricing rules, end-to-end order workflow, supplier
  assignment, events with payload).
- `AutoCommerce.ProductSelection.Tests` — unit tests for scoring/filter logic
  and the orchestrator's behavior with a stub brain client.
- `AutoCommerce.SupplierSelection.Tests` — 21 unit tests for the evaluator,
  selector, selection service, fulfillment service, and the discovered-event worker
  (all against a stub `IBrainClient`).

## Next modules (per the spec)

The event/API contracts are in place for the remaining modules. Each adds a
separate service that subscribes to the relevant events and/or exposes its own
endpoints — the Brain itself does not need to change:

3. Store Creation & Management — subscribe to `product.approved`, `price.updated`; expose `/sync-product`, `/sync-price`.
4. Product Content Generation — expose `/generate-content`; called by store module.
5. Payment & Checkout — handle Stripe webhooks, forward as `payment.*` events.
6. Order Fulfillment — subscribe to `order.created`, call supplier `/fulfill-order`.
7. Tracking & Delivery — subscribe to supplier fulfillment events, update orders.
8. Pricing & Margin Control (partially in place) — richer rules, promo codes.
9. Marketing & Advertising — subscribe to `product.approved`, `product.paused`.
10. Performance & Optimisation — consume metrics, emit `product.kill`, `ads.scale`.
11. Exception & Customer Support — consume `payment.failed`, `order.fulfillment_failed`.
12. Scaling & Expansion — multi-tenant configuration layer over the Brain.
