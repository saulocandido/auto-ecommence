import type { ReactNode } from "react";

export function Card({ title, children, action }: { title?: string; children: ReactNode; action?: ReactNode }) {
  return (
    <div className="bg-white rounded-xl shadow-sm border border-slate-200 p-5">
      {title && (
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-sm font-semibold text-slate-500 uppercase tracking-wide">{title}</h2>
          {action}
        </div>
      )}
      {children}
    </div>
  );
}

export function Stat({ label, value, hint }: { label: string; value: string | number; hint?: string }) {
  return (
    <div className="bg-white rounded-xl shadow-sm border border-slate-200 p-5">
      <div className="text-xs font-medium text-slate-500 uppercase tracking-wide">{label}</div>
      <div className="mt-1 text-2xl font-semibold text-slate-900">{value}</div>
      {hint && <div className="mt-1 text-xs text-slate-400">{hint}</div>}
    </div>
  );
}

export function Badge({ children, tone = "slate" }: { children: ReactNode; tone?: "slate" | "green" | "amber" | "red" | "brand" }) {
  const colors: Record<string, string> = {
    slate: "bg-slate-100 text-slate-700",
    green: "bg-emerald-100 text-emerald-700",
    amber: "bg-amber-100 text-amber-700",
    red: "bg-red-100 text-red-700",
    brand: "bg-brand-50 text-brand-700"
  };
  return <span className={`inline-flex text-xs font-medium px-2 py-0.5 rounded-full ${colors[tone]}`}>{children}</span>;
}

export function statusTone(status: string): "green" | "amber" | "red" | "slate" {
  switch (status) {
    case "Active":
    case "Fulfilled":
    case "SentToSupplier":
      return "green";
    case "Paused":
    case "Pending":
      return "amber";
    case "Killed":
    case "Failed":
    case "Cancelled":
      return "red";
    default:
      return "slate";
  }
}
