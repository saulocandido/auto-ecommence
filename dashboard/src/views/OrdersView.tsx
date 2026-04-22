import { useEffect, useState } from "react";
import { api, type OrderResponse } from "../api";
import { Badge, Card, statusTone } from "../components/Card";

export function OrdersView() {
  const [orders, setOrders] = useState<OrderResponse[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.orders().then(setOrders).catch(e => setError(String(e))); }, []);

  return (
    <Card title="Orders">
      {error && <div className="text-red-600 text-sm mb-3">{error}</div>}
      <table className="w-full text-sm">
        <thead className="text-left text-slate-500 text-xs uppercase">
          <tr>
            <th className="py-2">Shop order</th>
            <th>Customer</th>
            <th>Country</th>
            <th>Total</th>
            <th>Status</th>
            <th>Tracking</th>
          </tr>
        </thead>
        <tbody>
          {orders.map(o => (
            <tr key={o.id} className="border-t border-slate-100">
              <td className="py-2 font-medium">{o.shopOrderId}</td>
              <td>{o.customerEmail}</td>
              <td>{o.shippingCountry}</td>
              <td>{o.total.toFixed(2)}</td>
              <td><Badge tone={statusTone(o.status)}>{o.status}</Badge></td>
              <td>{o.trackingNumber ? <a className="text-brand-700 hover:underline" href={o.trackingUrl}>{o.trackingNumber}</a> : "—"}</td>
            </tr>
          ))}
          {orders.length === 0 && <tr><td colSpan={6} className="py-6 text-center text-slate-400">No orders yet.</td></tr>}
        </tbody>
      </table>
    </Card>
  );
}
