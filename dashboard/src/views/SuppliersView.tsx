import { useEffect, useState } from "react";
import {
  api,
  type ProductResponse,
  type SupplierProfile,
  type SupplierSelectionResult,
  type SupplierEvaluation
} from "../api";
import { Badge, Card } from "../components/Card";

function ScoreBadge({ score }: { score: number }) {
  const tone =
    score >= 70
      ? "text-emerald-700 bg-emerald-50 border-emerald-200"
      : score >= 40
      ? "text-amber-700 bg-amber-50 border-amber-200"
      : "text-red-700 bg-red-50 border-red-200";
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-bold ${tone}`}>
      {score.toFixed(1)}
    </span>
  );
}

function ReliabilityBar({ value }: { value: number }) {
  const pct = Math.max(0, Math.min(1, value)) * 100;
  return (
    <div className="flex items-center gap-2 min-w-[110px]">
      <div className="flex-1 h-1.5 rounded-full bg-slate-200 overflow-hidden">
        <div className="h-full rounded-full bg-brand-500" style={{ width: `${pct}%` }} />
      </div>
      <span className="text-xs text-slate-500 tabular-nums">{(value * 100).toFixed(0)}%</span>
    </div>
  );
}

function EvaluationRow({ ev, chosen }: { ev: SupplierEvaluation; chosen: boolean }) {
  return (
    <tr className={chosen ? "bg-emerald-50" : ""}>
      <td className="px-3 py-2 text-sm font-medium text-slate-900">
        {ev.supplierKey}
        {chosen && <Badge tone="green"> chosen</Badge>}
      </td>
      <td className="px-3 py-2"><ScoreBadge score={ev.score} /></td>
      <td className="px-3 py-2 text-sm text-slate-600 tabular-nums">
        {ev.cost.toFixed(2)} {ev.currency}
      </td>
      <td className="px-3 py-2 text-sm text-slate-600 tabular-nums">{ev.shippingDays}d</td>
      <td className="px-3 py-2 text-sm text-slate-600 tabular-nums">{ev.stockAvailable}</td>
      <td className="px-3 py-2 text-xs text-slate-500">
        <div className="flex flex-wrap gap-1">
          <span>price {ev.priceScore.toFixed(0)}</span>
          <span>ship {ev.shippingScore.toFixed(0)}</span>
          <span>rate {ev.ratingScore.toFixed(0)}</span>
          <span>stock {ev.stockScore.toFixed(0)}</span>
          <span>rel {ev.reliabilityScore.toFixed(0)}</span>
        </div>
      </td>
      <td className="px-3 py-2">
        {ev.viable ? (
          <Badge tone="green">viable</Badge>
        ) : (
          <span title={ev.rejectionReason ?? undefined}>
            <Badge tone="red">rejected</Badge>
          </span>
        )}
      </td>
    </tr>
  );
}

export function SuppliersView() {
  const [suppliers, setSuppliers] = useState<SupplierProfile[]>([]);
  const [products, setProducts] = useState<ProductResponse[]>([]);
  const [selectedProductId, setSelectedProductId] = useState<string>("");
  const [preview, setPreview] = useState<SupplierSelectionResult | null>(null);
  const [loading, setLoading] = useState<"idle" | "preview" | "assign">("idle");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [expandSupplier, setExpandSupplier] = useState<string | null>(null);
  const [quickAssigningProduct, setQuickAssigningProduct] = useState<string | null>(null);
  const [quickMessage, setQuickMessage] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const sup = await api.suppliers();
        setSuppliers(sup);
      } catch (e) {
        setError(`Suppliers: ${e instanceof Error ? e.message : String(e)}`);
      }
    })();
  }, []);

  useEffect(() => {
    (async () => {
      try {
        const prod = await api.products();
        setProducts(prod);
        if (prod.length > 0) setSelectedProductId(prod[0].id);
      } catch (e) {
        setError(`Products (set your API key in AI & Credentials): ${e instanceof Error ? e.message : String(e)}`);
      }
    })();
  }, []);

  async function handlePreview() {
    if (!selectedProductId) return;
    setLoading("preview");
    setError(null);
    setMessage(null);
    try {
      const result = await api.supplierPreview(selectedProductId);
      setPreview(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading("idle");
    }
  }

  async function handleAssign() {
    if (!selectedProductId) return;
    setLoading("assign");
    setError(null);
    setMessage(null);
    try {
      const result = await api.supplierAssign(selectedProductId);
      setPreview(result);
      if (result.chosenSupplierKey) {
        setMessage(`Assigned ${result.chosenSupplierKey} (score ${result.score?.toFixed(1)}) to ${result.externalId}`);
        const refreshed = await api.products();
        setProducts(refreshed);
      } else {
        setMessage(`No viable supplier: ${result.rejectionReason ?? "unknown"}`);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading("idle");
    }
  }

  async function handleQuickAssign(productId: string) {
    setQuickAssigningProduct(productId);
    setQuickMessage(null);
    try {
      const result = await api.supplierAssign(productId);
      if (result.chosenSupplierKey) {
        setQuickMessage(`✓ Assigned ${result.chosenSupplierKey} (${result.score?.toFixed(1)})`);
        const refreshed = await api.products();
        setProducts(refreshed);
        setTimeout(() => setQuickMessage(null), 3000);
      } else {
        setQuickMessage(`✗ No viable supplier`);
      }
    } catch (e) {
      setQuickMessage(`✗ Error: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setQuickAssigningProduct(null);
    }
  }

  const selectedProduct = products.find(p => p.id === selectedProductId);

  return (
    <div className="space-y-6">
      <Card title="Supplier Catalog">
        {suppliers.length === 0 ? (
          <p className="text-sm text-slate-500">Loading suppliers…</p>
        ) : (
          <div className="space-y-2">
            {suppliers.map(s => {
              const productsWithSupplier = products.filter(p => p.supplierKey === s.supplierKey);
              const isExpanded = expandSupplier === s.supplierKey;
              return (
                <div key={s.supplierKey}>
                  <button
                    onClick={() => setExpandSupplier(isExpanded ? null : s.supplierKey)}
                    className="w-full text-left p-3 rounded-lg border border-slate-200 hover:bg-slate-50 transition-colors"
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex-1">
                        <div className="text-sm font-semibold text-slate-900">{s.name}</div>
                        <div className="text-xs text-slate-500">{s.supplierKey} • {s.region}</div>
                      </div>
                      <div className="flex items-center gap-4">
                        <div className="text-right">
                          <div className="text-xs text-slate-500">Reliability</div>
                          <ReliabilityBar value={s.baseReliability} />
                        </div>
                        <div className="text-xs text-slate-400 font-medium">
                          {productsWithSupplier.length} product{productsWithSupplier.length !== 1 ? 's' : ''}
                        </div>
                        <span className="text-xs text-slate-400">{isExpanded ? '▼' : '▶'}</span>
                      </div>
                    </div>
                    {s.notes && <div className="mt-1 text-xs text-slate-500 italic">{s.notes}</div>}
                  </button>

                  {isExpanded && productsWithSupplier.length > 0 && (
                    <div className="mt-2 ml-3 pl-3 border-l-2 border-emerald-300 space-y-2">
                      {quickMessage && <div className="text-xs font-medium text-emerald-700">{quickMessage}</div>}
                      {productsWithSupplier.map(p => (
                        <div key={p.id} className="text-xs py-2 border-b border-slate-100 last:border-0">
                          <div className="flex items-start justify-between gap-2">
                            <div className="flex-1 min-w-0">
                              <div className="font-medium text-slate-700 truncate">{p.title}</div>
                              <div className="text-slate-500 mt-0.5">
                                Cost: {p.cost?.toFixed(2)} · Price: {p.price?.toFixed(2)} · Margin: {p.marginPercent?.toFixed(0)}%
                              </div>
                              <span className="mt-1 inline-block bg-emerald-100 text-emerald-700 px-1.5 py-0.5 rounded text-[10px] font-medium">assigned</span>
                            </div>
                            <button
                              onClick={() => handleQuickAssign(p.id)}
                              disabled={quickAssigningProduct === p.id || loading !== "idle"}
                              className="shrink-0 text-xs px-2 py-1 rounded bg-slate-600 text-white hover:bg-slate-700 disabled:opacity-50 whitespace-nowrap"
                            >
                              {quickAssigningProduct === p.id ? "..." : "Re-eval"}
                            </button>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}

                  {isExpanded && productsWithSupplier.length === 0 && (
                    <div className="mt-2 ml-3 text-xs text-slate-400 py-2">No products assigned to this supplier yet</div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </Card>

      {products.length > 0 && (
        <Card title="Imported Products Ready for Supplier Assignment">
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-slate-200">
              <thead>
                <tr className="text-left text-[11px] uppercase tracking-wide text-slate-500">
                  <th className="px-3 py-2 font-semibold">Product</th>
                  <th className="px-3 py-2 font-semibold">Current Supplier</th>
                  <th className="px-3 py-2 font-semibold">Cost</th>
                  <th className="px-3 py-2 font-semibold">Available Suppliers</th>
                  <th className="px-3 py-2 font-semibold">Action</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {products.map(p => (
                  <tr key={p.id} className="hover:bg-slate-50">
                    <td className="px-3 py-2">
                      <div className="text-sm font-medium text-slate-900">{p.title}</div>
                      <div className="text-xs text-slate-500">{p.externalId}</div>
                    </td>
                    <td className="px-3 py-2">
                      {p.supplierKey ? (
                        <Badge tone="brand">{p.supplierKey}</Badge>
                      ) : (
                        <span className="text-xs text-slate-400">—</span>
                      )}
                    </td>
                    <td className="px-3 py-2 text-sm text-slate-600 tabular-nums">{p.cost?.toFixed(2)}</td>
                    <td className="px-3 py-2 text-xs">
                      <div className="flex flex-wrap gap-1">
                        {p.suppliers.map(sup => (
                          <span key={sup.supplierKey} className="px-1.5 py-0.5 bg-slate-100 text-slate-600 rounded text-[10px]">
                            {sup.supplierKey}
                          </span>
                        ))}
                      </div>
                    </td>
                    <td className="px-3 py-2">
                      <button
                        onClick={() => handleQuickAssign(p.id)}
                        disabled={quickAssigningProduct === p.id}
                        className="text-xs px-2 py-1 rounded bg-brand-600 text-white hover:bg-brand-700 disabled:opacity-50"
                      >
                        {quickAssigningProduct === p.id ? "..." : "Re-select"}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      <Card
        title="Supplier Selection"
        action={
          products.length > 0 && (
            <div className="flex items-center gap-2">
              <select
                value={selectedProductId}
                onChange={e => { setSelectedProductId(e.target.value); setPreview(null); setMessage(null); }}
                className="text-sm border border-slate-300 rounded-md px-2 py-1 max-w-[260px]"
              >
                {products.map(p => (
                  <option key={p.id} value={p.id}>{p.title} ({p.externalId})</option>
                ))}
              </select>
              <button
                onClick={handlePreview}
                disabled={!selectedProductId || loading !== "idle"}
                className="text-sm px-3 py-1 rounded-md border border-slate-300 hover:bg-slate-50 disabled:opacity-50"
              >
                {loading === "preview" ? "…" : "Preview"}
              </button>
              <button
                onClick={handleAssign}
                disabled={!selectedProductId || loading !== "idle"}
                className="text-sm px-3 py-1 rounded-md bg-brand-600 text-white hover:bg-brand-700 disabled:opacity-50"
              >
                {loading === "assign" ? "Assigning…" : "Assign"}
              </button>
            </div>
          )
        }
      >
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-3 mb-3 text-sm text-red-700">
            {error}
            {error.includes("401") && (
              <div className="mt-2 text-xs">
                💡 Set your API key in <b>AI & Credentials</b> (or use default: <code className="bg-red-100 px-1 rounded">dev-master-key-change-me</code>)
              </div>
            )}
          </div>
        )}
        {message && <div className="bg-emerald-50 border border-emerald-200 rounded-lg p-3 mb-3 text-sm text-emerald-700">{message}</div>}

        {products.length === 0 ? (
          <p className="text-sm text-slate-500">
            <span className="text-amber-700 font-medium">⚠ No products loaded.</span>{" "}
            Set your Brain API key in <b>AI & Credentials</b> to see products here. Or use the default dev key: <code className="bg-amber-100 px-1 rounded text-xs">dev-master-key-change-me</code>
          </p>
        ) : (
          <>
            {selectedProduct && (
              <div className="text-xs text-slate-500 mb-3">
                Current supplier:{" "}
                <span className="font-medium text-slate-700">
                  {selectedProduct.supplierKey ?? "—"}
                </span>{" · cost "}{selectedProduct.cost ?? "—"}{" "}
                · price {selectedProduct.price ?? "—"} · {selectedProduct.suppliers.length} listings
              </div>
            )}

            {!preview && (
              <p className="text-sm text-slate-500">
                Pick a product and click <b>Preview</b> to evaluate all its supplier listings,
                or <b>Assign</b> to apply the highest-scoring one to the Brain.
              </p>
            )}
          </>
        )}

        {preview && (
          <>
            <div className="mb-3 flex items-center gap-3 text-sm">
              <span className="text-slate-500">Chosen:</span>
              {preview.chosenSupplierKey ? (
                <>
                  <span className="font-semibold text-slate-900">{preview.chosenSupplierKey}</span>
                  <ScoreBadge score={preview.score ?? 0} />
                  <span className="text-slate-500">
                    {preview.chosenCost?.toFixed(2)} {preview.currency}
                  </span>
                </>
              ) : (
                <span className="text-red-600 text-sm">{preview.rejectionReason ?? "none viable"}</span>
              )}
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200">
                <thead>
                  <tr className="text-left text-[11px] uppercase tracking-wide text-slate-500">
                    <th className="px-3 py-2 font-semibold">Supplier</th>
                    <th className="px-3 py-2 font-semibold">Score</th>
                    <th className="px-3 py-2 font-semibold">Cost</th>
                    <th className="px-3 py-2 font-semibold">Ship</th>
                    <th className="px-3 py-2 font-semibold">Stock</th>
                    <th className="px-3 py-2 font-semibold">Breakdown</th>
                    <th className="px-3 py-2 font-semibold">Viable</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {preview.evaluations.map(ev => (
                    <EvaluationRow
                      key={ev.supplierKey}
                      ev={ev}
                      chosen={ev.supplierKey === preview.chosenSupplierKey}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </Card>
    </div>
  );
}
