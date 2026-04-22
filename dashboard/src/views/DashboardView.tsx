import { useEffect, useState } from "react";
import { api, type DashboardMetrics } from "../api";
import { Card, Stat } from "../components/Card";

const fmt = (n: number) => new Intl.NumberFormat("en-IE", { style: "currency", currency: "EUR" }).format(n);

export function DashboardView() {
  const [data, setData] = useState<DashboardMetrics | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const load = () => api.metrics().then(m => active && setData(m)).catch(e => active && setError(String(e)));
    load();
    const id = setInterval(load, 10_000);
    return () => { active = false; clearInterval(id); };
  }, []);

  if (error) return <Card><div className="text-red-600 text-sm">{error}</div></Card>;
  if (!data) return <Card><div className="text-slate-500">Loading…</div></Card>;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <Stat label="Revenue 24h" value={fmt(data.revenueLast24h)} />
        <Stat label="Profit 24h" value={fmt(data.profitLast24h)} />
        <Stat label="Avg Margin" value={`${data.avgMarginPercent.toFixed(1)}%`} />
        <Stat label="Total Orders" value={data.totalOrders} hint={`${data.pendingOrders} pending · ${data.fulfilledOrders} fulfilled`} />
      </div>
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <Stat label="Products" value={data.totalProducts} />
        <Stat label="Active" value={data.activeProducts} />
        <Stat label="Paused" value={data.pausedProducts} />
        <Stat label="Top sellers" value={data.topProducts.length} />
      </div>

      <Card title="Top products">
        {data.topProducts.length === 0 ? (
          <div className="text-slate-400 text-sm">No sales yet.</div>
        ) : (
          <table className="w-full text-sm">
            <thead className="text-left text-slate-500">
              <tr><th className="py-1">Product</th><th>Orders</th><th>Revenue</th></tr>
            </thead>
            <tbody>
              {data.topProducts.map(p => (
                <tr key={p.id} className="border-t border-slate-100">
                  <td className="py-2">{p.title}</td>
                  <td>{p.orderCount}</td>
                  <td>{fmt(p.revenue)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      <Card title="Recent events">
        <ul className="text-sm divide-y divide-slate-100">
          {data.recentEvents.map(e => (
            <li key={e.id} className="py-2 flex items-center justify-between">
              <span className="font-mono text-xs text-slate-500">{e.type}</span>
              <span className="text-slate-400 text-xs">{new Date(e.occurredAt).toLocaleTimeString()}</span>
            </li>
          ))}
          {data.recentEvents.length === 0 && <li className="py-2 text-slate-400 text-sm">No events yet.</li>}
        </ul>
      </Card>
    </div>
  );
}
