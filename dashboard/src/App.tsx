import { useState } from "react";
import { DashboardView } from "./views/DashboardView";
import { ProductsView } from "./views/ProductsView";
import { OrdersView } from "./views/OrdersView";
import { RecommendationsView } from "./views/RecommendationsView";
import { ConfigurationView } from "./views/ConfigurationView";
import { SuppliersView } from "./views/SuppliersView";
import { StoreView } from "./views/StoreView";
import { ShopifyAutomationView } from "./views/ShopifyAutomationView";

type Page =
  | "dashboard"
  | "products"
  | "orders"
  | "recommendations"
  | "suppliers"
  | "store"
  | "automation"
  | "config-recommendations"
  | "config-ai";

/* ── SVG icon helpers (inline, no extra deps) ── */
function Icon({ d, className = "" }: { d: string; className?: string }) {
  return (
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none"
      stroke="currentColor" strokeWidth={1.8} strokeLinecap="round" strokeLinejoin="round"
      className={`w-5 h-5 shrink-0 ${className}`}>
      <path d={d} />
    </svg>
  );
}
const icons = {
  dashboard: "M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-4 0h4",
  products: "M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4",
  orders: "M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2",
  recommendations: "M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z",
  suppliers: "M3 7h18M3 12h18M3 17h18 M7 3v18",
  store: "M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5s-3 1.34-3 3 1.34 3 3 3zm1 6H9v-2c0-2 4-3.1 4-3.1s4 1.1 4 3.1v2z M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2z",
  configRec: "M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z M15 12a3 3 0 11-6 0 3 3 0 016 0z",
  configAi: "M15 7a2 2 0 012 2m4 0a6 6 0 01-7.743 5.743L11 17H9v2H7v2H4a1 1 0 01-1-1v-2.586a1 1 0 01.293-.707l5.964-5.964A6 6 0 1121 9z",
};

type NavItem = { id: Page; label: string; icon: string; group?: string };

const navItems: NavItem[] = [
  { id: "dashboard", label: "Dashboard", icon: icons.dashboard },
  { id: "products", label: "Products", icon: icons.products },
  { id: "orders", label: "Orders", icon: icons.orders },
  { id: "recommendations", label: "Recommendations", icon: icons.recommendations },
  { id: "suppliers", label: "Suppliers", icon: icons.suppliers },
  { id: "store", label: "Shopify Store", icon: icons.store },
  { id: "automation", label: "Shopify Automation", icon: icons.store, group: "Automation" },
  { id: "config-recommendations", label: "Selection Rules", icon: icons.configRec, group: "Configuration" },
  { id: "config-ai", label: "AI & Credentials", icon: icons.configAi, group: "Configuration" },
];

export default function App() {
  const [page, setPage] = useState<Page>("dashboard");

  let lastGroup: string | undefined;

  return (
    <div className="flex min-h-screen bg-slate-100">
      {/* ── Left sidebar ── */}
      <aside className="w-56 shrink-0 bg-slate-900 text-slate-300 flex flex-col">
        <div className="px-5 py-5 border-b border-slate-700/60">
          <h1 className="text-base font-bold text-white tracking-tight">AutoCommerce</h1>
          <p className="text-[11px] text-slate-500 mt-0.5">Automated dropshipping</p>
        </div>

        <nav className="flex-1 py-3 space-y-0.5 overflow-y-auto">
          {navItems.map(item => {
            const showGroup = item.group && item.group !== lastGroup;
            lastGroup = item.group;
            return (
              <div key={item.id}>
                {showGroup && (
                  <div className="px-5 pt-5 pb-1.5 text-[10px] font-semibold uppercase tracking-widest text-slate-500">
                    {item.group}
                  </div>
                )}
                <button
                  onClick={() => setPage(item.id)}
                  className={`w-full flex items-center gap-3 px-5 py-2 text-sm transition-colors ${
                    page === item.id
                      ? "bg-slate-800 text-white font-medium border-l-[3px] border-brand-500 pl-[17px]"
                      : "hover:bg-slate-800/60 hover:text-white"
                  }`}>
                  <Icon d={item.icon} />
                  {item.label}
                </button>
              </div>
            );
          })}
        </nav>
      </aside>

      {/* ── Main content ── */}
      <div className="flex-1 flex flex-col min-w-0">
        <header className="bg-white border-b border-slate-200 px-8 py-4 flex items-center justify-between">
          <h2 className="text-lg font-semibold text-slate-900">
            {navItems.find(n => n.id === page)?.label}
          </h2>
        </header>

        <main className="flex-1 px-8 py-6 overflow-y-auto">
          <div className="max-w-5xl">
            {page === "dashboard" && <DashboardView />}
            {page === "products" && <ProductsView />}
            {page === "orders" && <OrdersView />}
            {page === "recommendations" && <RecommendationsView />}
            {page === "suppliers" && <SuppliersView />}
            {page === "store" && <StoreView />}
            {page === "automation" && <ShopifyAutomationView />}
            {(page === "config-recommendations" || page === "config-ai") && (
              <ConfigurationView activeTab={page === "config-ai" ? "ai" : "selection"} />
            )}
          </div>
        </main>
      </div>
    </div>
  );
}
