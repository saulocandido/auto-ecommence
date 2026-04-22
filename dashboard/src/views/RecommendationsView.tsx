import { useEffect, useMemo, useState } from "react";
import { api, type RecommendationResponse, type ScoredCandidate } from "../api";
import { Badge, Card } from "../components/Card";

type SortKey = "score" | "price" | "rating" | "shipping" | "searches" | "competitors";
type Filter = "all" | "approved" | "rejected";

const SORT_OPTIONS: { value: SortKey; label: string }[] = [
  { value: "score", label: "Score" },
  { value: "price", label: "Price" },
  { value: "rating", label: "Rating" },
  { value: "shipping", label: "Shipping" },
  { value: "searches", label: "Searches" },
  { value: "competitors", label: "Competitors" },
];

function sortValue(r: ScoredCandidate, key: SortKey): number {
  switch (key) {
    case "score": return r.score;
    case "price": return r.candidate.price;
    case "rating": return r.candidate.rating;
    case "shipping": return r.candidate.shippingDaysToTarget;
    case "searches": return r.candidate.estimatedMonthlySearches ?? 0;
    case "competitors": return r.candidate.competitorCount ?? 0;
  }
}

function ScoreBadge({ score }: { score: number }) {
  const tone = score >= 70 ? "text-emerald-700 bg-emerald-50 border-emerald-200"
    : score >= 40 ? "text-amber-700 bg-amber-50 border-amber-200"
    : "text-red-700 bg-red-50 border-red-200";
  return <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-bold ${tone}`}>{score.toFixed(0)}</span>;
}

function ProductImage({ urls, title }: { urls: string[]; title: string }) {
  const [err, setErr] = useState(false);
  const src = urls?.[0];
  if (!src || err) {
    return (
      <div className="w-16 h-16 rounded-lg bg-slate-100 flex items-center justify-center text-slate-300 shrink-0">
        <svg className="w-7 h-7" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
          <path strokeLinecap="round" strokeLinejoin="round" d="m2.25 15.75 5.159-5.159a2.25 2.25 0 0 1 3.182 0l5.159 5.159m-1.5-1.5 1.409-1.409a2.25 2.25 0 0 1 3.182 0l2.909 2.909M3.75 21h16.5A2.25 2.25 0 0 0 22.5 18.75V5.25A2.25 2.25 0 0 0 20.25 3H3.75A2.25 2.25 0 0 0 1.5 5.25v13.5A2.25 2.25 0 0 0 3.75 21Z" />
        </svg>
      </div>
    );
  }
  return (
    <img src={src} alt={title} referrerPolicy="no-referrer" crossOrigin="anonymous"
      onError={() => setErr(true)}
      className="w-16 h-16 rounded-lg object-cover shrink-0 border border-slate-200" />
  );
}

function BreakdownBar({ breakdown }: { breakdown: Record<string, number> }) {
  const entries = Object.entries(breakdown);
  const max = Math.max(...entries.map(([, v]) => v), 1);
  return (
    <div className="flex flex-wrap gap-x-3 gap-y-1">
      {entries.map(([k, v]) => (
        <div key={k} className="flex items-center gap-1.5 min-w-[80px]">
          <div className="w-12 h-1.5 rounded-full bg-slate-200 overflow-hidden">
            <div className="h-full rounded-full bg-brand-500" style={{ width: `${(v / max) * 100}%` }} />
          </div>
          <span className="text-[10px] text-slate-500 whitespace-nowrap">{k} {v.toFixed(0)}</span>
        </div>
      ))}
    </div>
  );
}

export function RecommendationsView() {
  const [data, setData] = useState<RecommendationResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [scanResult, setScanResult] = useState<{ imported: number; total: number; approved: number } | null>(null);
  const [sortKey, setSortKey] = useState<SortKey>("score");
  const [sortAsc, setSortAsc] = useState(false);
  const [filter, setFilter] = useState<Filter>("all");
  const [expanded, setExpanded] = useState<string | null>(null);
  const [findingSuppliers, setFindingSuppliers] = useState(false);
  const [supplierResult, setSupplierResult] = useState<{ assigned: number; failed: number } | null>(null);

  const load = () => api.recommendations().then(setData).catch(e => setError(String(e)));
  useEffect(() => { load(); }, []);

  const runScan = async () => {
    setScanning(true);
    setScanResult(null);
    try {
      const r = await api.scan();
      setScanResult(r);
      await load();
    } catch (e) { setError(String(e)); }
    finally { setScanning(false); }
  };

  const findSuppliersForAll = async () => {
    setFindingSuppliers(true);
    setSupplierResult(null);
    setError(null);
    try {
      const products = await api.products();
      let assigned = 0, failed = 0;
      for (const p of products) {
        try {
          await api.supplierAssign(p.id);
          assigned++;
        } catch {
          failed++;
        }
      }
      setSupplierResult({ assigned, failed });
    } catch (e) {
      setError(`Error finding suppliers: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setFindingSuppliers(false);
    }
  };

  const sorted = useMemo(() => {
    if (!data) return [];
    let list = data.recommendations.slice();
    if (filter === "approved") list = list.filter(r => r.approved);
    if (filter === "rejected") list = list.filter(r => !r.approved);
    list.sort((a, b) => {
      const va = sortValue(a, sortKey), vb = sortValue(b, sortKey);
      return sortAsc ? va - vb : vb - va;
    });
    return list;
  }, [data, sortKey, sortAsc, filter]);

  const approvedCount = data?.recommendations.filter(r => r.approved).length ?? 0;
  const rejectedCount = data?.recommendations.filter(r => !r.approved).length ?? 0;

  const toggleSort = (key: SortKey) => {
    if (sortKey === key) setSortAsc(!sortAsc);
    else { setSortKey(key); setSortAsc(false); }
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <Card title="Product Recommendations" action={
        <div className="flex items-center gap-2">
          <button onClick={findSuppliersForAll} disabled={findingSuppliers || scanning}
            className="rounded-lg bg-emerald-600 px-4 py-2 text-sm font-medium text-white hover:bg-emerald-700 disabled:opacity-50 transition-colors flex items-center gap-2">
            {findingSuppliers && <span className="inline-block w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />}
            {findingSuppliers ? "Finding…" : "Find Suppliers"}
          </button>
          <button onClick={runScan} disabled={scanning || findingSuppliers}
            className="rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-700 disabled:opacity-50 transition-colors flex items-center gap-2">
            {scanning && <span className="inline-block w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />}
            {scanning ? "Scanning…" : "Run scan & import"}
          </button>
        </div>
      }>
        {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 mb-4">{error}</div>}
        {supplierResult !== null && (
          <div className={`rounded-lg border px-4 py-3 text-sm mb-4 ${
            supplierResult.assigned > 0
              ? "border-emerald-200 bg-emerald-50 text-emerald-700"
              : "border-amber-200 bg-amber-50 text-amber-700"
          }`}>
            ✓ Found suppliers for {supplierResult.assigned} products
            {supplierResult.failed > 0 && ` (${supplierResult.failed} failed or already assigned)`}
          </div>
        )}
        {scanResult !== null && (
          <div className={`rounded-lg border px-4 py-3 text-sm mb-4 ${
            scanResult.imported > 0
              ? "border-emerald-200 bg-emerald-50 text-emerald-700"
              : scanResult.total > 0
                ? "border-amber-200 bg-amber-50 text-amber-700"
                : "border-red-200 bg-red-50 text-red-700"
          }`}>
            {scanResult.total === 0
              ? "⚠ AI returned no candidates. Try again or check your API key."
              : scanResult.imported > 0
                ? `✓ Imported ${scanResult.imported} of ${scanResult.approved} approved products (${scanResult.total} candidates scanned).`
                : `⚠ ${scanResult.total} candidates found but none passed your filters (${scanResult.total - scanResult.approved} rejected). Lower your minimum score or adjust filters.`}
          </div>
        )}


        {data && (
          <div className="flex flex-wrap items-center gap-3">
            <div className="text-xs text-slate-500">
              Generated {new Date(data.generatedAt).toLocaleString()} · Market {data.config.targetMarket}
            </div>
            <div className="flex items-center gap-2">
              <Badge tone="green">{approvedCount} approved</Badge>
              <Badge tone="amber">{rejectedCount} rejected</Badge>
              <Badge tone="slate">{data.recommendations.length} total</Badge>
            </div>
          </div>
        )}
      </Card>

      {/* Toolbar */}
      {data && data.recommendations.length > 0 && (
        <div className="flex flex-wrap items-center gap-3 px-1">
          {/* Filter tabs */}
          <div className="flex rounded-lg border border-slate-200 overflow-hidden text-xs">
            {(["all", "approved", "rejected"] as Filter[]).map(f => (
              <button key={f} onClick={() => setFilter(f)}
                className={`px-3 py-1.5 capitalize transition-colors ${
                  filter === f ? "bg-brand-600 text-white" : "bg-white text-slate-600 hover:bg-slate-50"
                }`}>{f}</button>
            ))}
          </div>

          {/* Sort buttons */}
          <div className="flex items-center gap-1 ml-auto">
            <span className="text-xs text-slate-400 mr-1">Sort:</span>
            {SORT_OPTIONS.map(opt => (
              <button key={opt.value} onClick={() => toggleSort(opt.value)}
                className={`px-2 py-1 rounded text-xs transition-colors ${
                  sortKey === opt.value
                    ? "bg-brand-100 text-brand-700 font-medium"
                    : "text-slate-500 hover:bg-slate-100"
                }`}>
                {opt.label}
                {sortKey === opt.value && <span className="ml-0.5">{sortAsc ? "↑" : "↓"}</span>}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* Product cards */}
      {!data ? (
        <Card title=""><div className="text-slate-400 text-sm py-8 text-center">Loading…</div></Card>
      ) : sorted.length === 0 ? (
        <Card title=""><div className="text-slate-400 text-sm py-8 text-center">No candidates match the current filter.</div></Card>
      ) : (
        <div className="space-y-3">
          {sorted.map((r, idx) => {
            const c = r.candidate;
            const isExpanded = expanded === c.externalId;

            return (
              <div key={c.externalId}
                className={`rounded-xl border bg-white shadow-sm transition-all hover:shadow-md ${
                  r.approved ? "border-slate-200" : "border-slate-200 opacity-75"
                }`}>
                {/* Main row */}
                <div className="flex items-start gap-4 p-4 cursor-pointer" onClick={() => setExpanded(isExpanded ? null : c.externalId)}>
                  {/* Rank */}
                  <div className="text-lg font-bold text-slate-300 w-6 text-center shrink-0 pt-1">#{idx + 1}</div>

                  {/* Image */}
                  <ProductImage urls={c.imageUrls} title={c.title} />

                  {/* Info */}
                  <div className="flex-1 min-w-0">
                    <div className="flex items-start justify-between gap-3">
                      <div className="min-w-0">
                        <h3 className="text-sm font-semibold text-slate-800 truncate">{c.title}</h3>
                        <div className="flex flex-wrap items-center gap-2 mt-1">
                          <span className="text-xs text-slate-500 bg-slate-100 rounded px-1.5 py-0.5">{c.category}</span>
                          <span className="text-xs text-slate-400">{c.source}</span>
                          {r.approved
                            ? <Badge tone="green">Approved</Badge>
                            : <Badge tone="amber">{r.rejectionReason ?? "Rejected"}</Badge>}
                        </div>
                      </div>
                      <ScoreBadge score={r.score} />
                    </div>

                    {/* Stats row */}
                    <div className="flex flex-wrap items-center gap-4 mt-2 text-xs text-slate-500">
                      <span className="font-medium text-slate-700">${c.price?.toFixed(2)}</span>
                      <span>⭐ {c.rating?.toFixed(1)} ({c.reviewCount ?? 0})</span>
                      <span>📦 {c.shippingDaysToTarget}d</span>
                      {c.estimatedMonthlySearches != null && <span>🔍 {c.estimatedMonthlySearches.toLocaleString()}/mo</span>}
                      {c.competitorCount != null && <span>🏪 {c.competitorCount} competitors</span>}
                    </div>
                  </div>

                  {/* Expand chevron */}
                  <svg className={`w-5 h-5 text-slate-400 shrink-0 transition-transform ${isExpanded ? "rotate-180" : ""}`}
                    fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="m19.5 8.25-7.5 7.5-7.5-7.5" />
                  </svg>
                </div>

                {/* Expanded details */}
                {isExpanded && (
                  <div className="border-t border-slate-100 px-4 py-4 space-y-4 bg-slate-50/50 rounded-b-xl">
                    {c.description && <p className="text-sm text-slate-600">{c.description}</p>}

                    {/* Score breakdown */}
                    <div>
                      <h4 className="text-xs font-semibold text-slate-500 uppercase mb-2">Score Breakdown</h4>
                      <BreakdownBar breakdown={r.breakdown} />
                    </div>

                    {/* Tags */}
                    {c.tags && c.tags.length > 0 && (
                      <div>
                        <h4 className="text-xs font-semibold text-slate-500 uppercase mb-2">Tags</h4>
                        <div className="flex flex-wrap gap-1.5">
                          {c.tags.map(t => <span key={t} className="text-xs bg-slate-200 text-slate-600 rounded-full px-2 py-0.5">{t}</span>)}
                        </div>
                      </div>
                    )}

                    {/* Suppliers */}
                    {c.supplierCandidates && c.supplierCandidates.length > 0 && (
                      <div>
                        <h4 className="text-xs font-semibold text-slate-500 uppercase mb-2">Suppliers ({c.supplierCandidates.length})</h4>
                        <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
                          {c.supplierCandidates.map((s, i) => (
                            <div key={i} className="rounded-lg border border-slate-200 bg-white p-3 text-xs">
                              <div className="flex items-center justify-between mb-1">
                                <div className="font-medium text-slate-700">{s.supplierKey}</div>
                                {s.url && (
                                  <a href={s.url} target="_blank" rel="noopener noreferrer"
                                    className="text-brand-600 hover:text-brand-700 underline text-[10px]">
                                    ↗
                                  </a>
                                )}
                              </div>
                              <div className="flex flex-wrap gap-x-3 gap-y-0.5 text-slate-500">
                                <span>Cost: ${s.cost?.toFixed(2)}</span>
                                <span>{s.shippingDays}d shipping</span>
                                <span>⭐ {s.rating?.toFixed(1)}</span>
                                <span>Stock: {s.stockAvailable}</span>
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
