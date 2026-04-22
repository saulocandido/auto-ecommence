# AutoCommerce host-side tools

## `capture-shopify-session.mjs` — one-shot Shopify login capture

The dashboard's **Upload Session JSON** flow accepts a Playwright storage-state
blob, but generating that blob manually is painful (you have to get the full
set of `admin.shopify.com`-scoped cookies, not just the 3 generic ones your
browser shows you). This script does it automatically.

### First time

```bash
cd tools
npm install
npx playwright install chromium
```

### Every time

```bash
node capture-shopify-session.mjs
# or, if the backend is on a non-default URL:
node capture-shopify-session.mjs http://localhost:5110
```

### What it does

1. Reads `Find Products URL` from `GET /api/shopify/automation/config`.
2. Opens a visible Chromium window on your machine.
3. Waits (up to 10 minutes) for you to log into Shopify admin. You'll see
   each page load printed in the terminal so you know progress.
4. When it detects you're on the target app page with the search input
   visible, it captures the full storage state (all cookies + localStorage
   + sessionStorage for every origin Shopify touched).
5. Prints a summary (how many admin-scoped cookies it got, whether the
   critical `_secure_admin_session_*` cookie is present).
6. POSTs the storage state to `POST /api/shopify/automation/session/upload`,
   which saves it to the container volume and immediately validates it.
7. Exits `0` if the backend reports `connected`; non-zero otherwise.

You don't need to copy any JSON anywhere.

### Exit codes

| Code | Meaning |
| ---- | ------- |
| 0    | Success — session saved and validated as `connected` |
| 1    | Unexpected error (printed to stderr) |
| 2    | No `findProductsUrl` in backend config — set it in the dashboard first |
| 3    | Timed out waiting for you to reach the target page while logged in |
| 4    | Backend rejected the state — see printed `backend response` |

### Troubleshooting

- **"Cannot find module 'playwright'"** — run `npm install` inside the
  `tools/` directory.
- **"playwright browsers are missing"** — run
  `npx playwright install chromium`.
- **Backend unreachable** — check the store-management container is up
  (`docker compose ps`) and exposed on the port you passed.
