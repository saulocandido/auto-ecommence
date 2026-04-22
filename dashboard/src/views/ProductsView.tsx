import { useEffect, useState } from "react";
import { api, type ProductResponse } from "../api";
import { Badge, Card, statusTone } from "../components/Card";

export function ProductsView() {
  const [products, setProducts] = useState<ProductResponse[]>([]);
  const [status, setStatus] = useState<string>("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = () => api.products(status || undefined).then(setProducts).catch(e => setError(String(e)));
  useEffect(() => { load(); }, [status]);

  const setProductStatus = async (id: string, newStatus: string) => {
    setBusy(id);
    try {
      await api.updateProduct(id, { status: newStatus });
      await load();
    } catch (e) { setError(String(e)); }
    finally { setBusy(null); }
  };

  const reassignSupplier = async (id: string) => {
    setBusy(id);
    setError(null);
    try {
      const result = await api.supplierAssign(id);
      if (!result.chosenSupplierKey) {
        setError(`No viable supplier: ${result.rejectionReason ?? "unknown"}`);
      }
      await load();
    } catch (e) { setError(String(e)); }
    finally { setBusy(null); }
  };

  return (
    <Card title="Products" action={
      <select value={status} onChange={e => setStatus(e.target.value)} className="text-sm rounded border-slate-300">
        <option value="">All</option>
        <option value="Active">Active</option>
        <option value="Paused">Paused</option>
        <option value="Draft">Draft</option>
        <option value="Killed">Killed</option>
      </select>
    }>
      {error && <div className="text-red-600 text-sm mb-3">{error}</div>}
      <table className="w-full text-sm">
        <thead className="text-left text-slate-500 text-xs uppercase">
          <tr>
            <th className="py-2">Title</th>
            <th>Category</th>
            <th>Score</th>
            <th>Cost</th>
            <th>Price</th>
            <th>Margin</th>
            <th>Supplier</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {products.map(p => (
            <tr key={p.id} className="border-t border-slate-100">
              <td className="py-2 font-medium">{p.title}<div className="text-xs text-slate-400">{p.externalId}</div></td>
              <td>{p.category}</td>
              <td>{p.score.toFixed(1)}</td>
              <td>{p.cost?.toFixed(2) ?? "—"}</td>
              <td>{p.price?.toFixed(2) ?? "—"}</td>
              <td>{p.marginPercent ? `${p.marginPercent.toFixed(1)}%` : "—"}</td>
              <td>
                {p.supplierKey ? (
                  <Badge tone="brand">{p.supplierKey}</Badge>
                ) : (
                  <span className="text-xs text-slate-400">—</span>
                )}
                <div className="text-[10px] text-slate-400 mt-0.5">{p.suppliers.length} listings</div>
              </td>
              <td><Badge tone={statusTone(p.status)}>{p.status}</Badge></td>
              <td className="text-right">
                <button disabled={busy === p.id} onClick={() => reassignSupplier(p.id)}
                  className="text-xs text-brand-700 hover:underline mr-3">Re-select supplier</button>
                {p.status !== "Paused" && (
                  <button disabled={busy === p.id} onClick={() => setProductStatus(p.id, "Paused")}
                    className="text-xs text-amber-700 hover:underline mr-3">Pause</button>
                )}
                {p.status !== "Active" && (
                  <button disabled={busy === p.id} onClick={() => setProductStatus(p.id, "Active")}
                    className="text-xs text-emerald-700 hover:underline mr-3">Activate</button>
                )}
                {p.status !== "Killed" && (
                  <button disabled={busy === p.id} onClick={() => setProductStatus(p.id, "Killed")}
                    className="text-xs text-red-700 hover:underline">Kill</button>
                )}
              </td>
            </tr>
          ))}
          {products.length === 0 && (
            <tr><td colSpan={9} className="py-6 text-center text-slate-400">No products yet. Run a scan from the Recommendations tab.</td></tr>
          )}
        </tbody>
      </table>
    </Card>
  );
}
