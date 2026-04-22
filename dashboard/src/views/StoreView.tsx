import { useEffect, useState } from "react";
import {
  api,
  type ProductResponse,
  type ShopifyPage,
  type ShopifyThemeConfig,
  type SyncProductRequest,
  type ShopifyAdminConfigView,
  type ShopifyAdminConfigUpdate,
  type ShopifyHealthResult,
  type ShopifyProductSyncRow
} from "../api";
import { Badge, Card } from "../components/Card";

export function StoreView() {
  const [products, setProducts] = useState<ProductResponse[]>([]);
  const [syncedProducts, setSyncedProducts] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState<"idle" | "syncing" | "initializing" | "theme">("idle");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [syncingProductId, setSyncingProductId] = useState<string | null>(null);
  const [theme, setTheme] = useState<ShopifyThemeConfig>({ themeName: "", homepageHeading: "", homepageSubheading: "", primaryColor: "#2563eb", logoUrl: "" });
  const [pages, setPages] = useState<ShopifyPage[]>([]);
  const [adminCfg, setAdminCfg] = useState<ShopifyAdminConfigView | null>(null);
  const [adminDraft, setAdminDraft] = useState<ShopifyAdminConfigUpdate>({});
  const [health, setHealth] = useState<ShopifyHealthResult | null>(null);
  const [syncRows, setSyncRows] = useState<ShopifyProductSyncRow[]>([]);

  useEffect(() => {
    (async () => {
      try {
        setError(null);
        const [prods, themeCfg, pagesList] = await Promise.all([
          api.storeProducts("active"),
          api.storeGetTheme().catch(() => null),
          api.storeListPages().catch(() => [] as ShopifyPage[])
        ]);
        setProducts(prods);
        const synced = new Set(prods.filter(p => p.supplierKey).map(p => p.id));
        setSyncedProducts(synced);
        if (themeCfg) setTheme(themeCfg);
        setPages(pagesList);
        try { setAdminCfg(await api.shopifyAdminConfigGet()); } catch { /* ignore */ }
        try { setHealth(await api.shopifyHealth()); } catch { /* ignore */ }
        try { setSyncRows(await api.shopifySyncStatusList()); } catch { /* ignore */ }
      } catch (e) {
        setError(`Failed to load products: ${e instanceof Error ? e.message : String(e)}`);
        setProducts([]);
      }
    })();
  }, []);

  const handleSaveTheme = async () => {
    setLoading("theme");
    setError(null);
    setMessage(null);
    try {
      const res = await api.storeUpdateTheme(theme);
      if (res.success) {
        setMessage("Theme updated");
        setTimeout(() => setMessage(null), 3000);
      }
      const refreshed = await api.storeListPages();
      setPages(refreshed);
    } catch (e) {
      setError(`Theme update failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setLoading("idle");
    }
  };

  const handleInitializeStore = async () => {
    setLoading("initializing");
    setMessage(null);
    setError(null);
    try {
      const res = await api.storeInitialize();
      if (res.success) {
        setMessage("Store initialized successfully");
      }
    } catch (e) {
      setError(`Initialize failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setLoading("idle");
    }
  };

  const handleSyncProduct = async (product: ProductResponse) => {
    setSyncingProductId(product.id);
    try {
      const req: SyncProductRequest = {
        brainProductId: product.id,
        title: product.title,
        description: product.description || "",
        price: product.price || 0,
        imageUrl: product.imageUrls?.[0]
      };
      const res = await api.storeSyncProduct(req);
      if (res.success) {
        setSyncedProducts(prev => new Set([...prev, product.id]));
        setMessage(`Synced "${product.title}" to Shopify`);
        setTimeout(() => setMessage(null), 3000);
      }
    } catch (e) {
      setError(`Sync failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setSyncingProductId(null);
    }
  };

  const handleUpdatePrice = async (product: ProductResponse) => {
    if (!product.price) {
      setError("Product has no price set");
      return;
    }
    setSyncingProductId(product.id);
    try {
      const res = await api.storeUpdatePrice({
        brainProductId: product.id,
        newPrice: product.price
      });
      if (res.success) {
        setMessage(`Updated price for "${product.title}"`);
        setTimeout(() => setMessage(null), 3000);
      }
    } catch (e) {
      setError(`Price update failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setSyncingProductId(null);
    }
  };

  const handleSaveAdmin = async () => {
    setError(null); setMessage(null);
    try {
      const saved = await api.shopifyAdminConfigPut(adminDraft);
      setAdminCfg(saved);
      setAdminDraft({});
      setMessage("Shopify admin config saved");
      setTimeout(() => setMessage(null), 3000);
    } catch (e) {
      setError(`Save config failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleRefreshHealth = async () => {
    try {
      setHealth(await api.shopifyHealth());
      setSyncRows(await api.shopifySyncStatusList());
    } catch (e) {
      setError(`Health refresh failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const handleProductionBulkSync = async () => {
    setLoading("syncing"); setError(null); setMessage(null);
    try {
      const res = await api.shopifyBulkSync();
      setMessage(`Bulk sync: ${res.count} processed (${res.results.filter(r => !r.error).length} ok)`);
      setSyncRows(await api.shopifySyncStatusList());
      setHealth(await api.shopifyHealth());
    } catch (e) {
      setError(`Bulk sync failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setLoading("idle");
    }
  };

  const handleBulkSync = async () => {
    setLoading("syncing");
    setMessage(null);
    setError(null);
    let syncedCount = 0;
    try {
      for (const product of products) {
        try {
          const req: SyncProductRequest = {
            brainProductId: product.id,
            title: product.title,
            description: product.description || "",
            price: product.price || 0,
            imageUrl: product.imageUrls?.[0]
          };
          await api.storeSyncProduct(req);
          setSyncedProducts(prev => new Set([...prev, product.id]));
          syncedCount++;
        } catch (e) {
          console.error(`Failed to sync ${product.id}:`, e);
        }
      }
      setMessage(`Synced ${syncedCount} products to Shopify`);
    } catch (e) {
      setError(`Bulk sync failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setLoading("idle");
    }
  };

  return (
    <div className="space-y-6">
      {/* Store Status Card */}
      <Card>
        <div className="flex items-center justify-between">
          <div>
            <h3 className="font-semibold text-slate-900">Shopify Store Connection</h3>
            <p className="text-sm text-slate-600 mt-1">Manage product synchronization to your Shopify store</p>
          </div>
          <button
            onClick={handleInitializeStore}
            disabled={loading === "initializing"}
            className="px-4 py-2 bg-brand-500 hover:bg-brand-600 disabled:opacity-50 text-white rounded-lg text-sm font-medium transition-colors">
            {loading === "initializing" ? "Initializing..." : "Initialize Store"}
          </button>
        </div>
      </Card>

      {/* Messages */}
      {error && (
        <div className="p-4 bg-red-50 border border-red-200 rounded-lg text-sm text-red-700">
          {error}
        </div>
      )}
      {message && (
        <div className="p-4 bg-emerald-50 border border-emerald-200 rounded-lg text-sm text-emerald-700">
          {message}
        </div>
      )}

      {/* Bulk Sync */}
      <Card>
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-semibold text-slate-900">Bulk Sync Products</h3>
          <button
            onClick={handleBulkSync}
            disabled={loading === "syncing" || products.length === 0}
            className="px-4 py-2 bg-emerald-500 hover:bg-emerald-600 disabled:opacity-50 text-white rounded-lg text-sm font-medium transition-colors">
            {loading === "syncing" ? "Syncing..." : `Sync All (${products.length})`}
          </button>
        </div>
        <p className="text-sm text-slate-600">
          Sync all active products to your Shopify store at once. Already synced products will be updated.
        </p>
      </Card>

      {/* Products Table */}
      <Card>
        <div className="overflow-x-auto">
          <table className="w-full">
            <thead>
              <tr className="border-b border-slate-200">
                <th className="text-left px-4 py-3 text-xs font-semibold text-slate-600 uppercase">Product</th>
                <th className="text-left px-4 py-3 text-xs font-semibold text-slate-600 uppercase">Price</th>
                <th className="text-left px-4 py-3 text-xs font-semibold text-slate-600 uppercase">Supplier</th>
                <th className="text-left px-4 py-3 text-xs font-semibold text-slate-600 uppercase">Status</th>
                <th className="text-left px-4 py-3 text-xs font-semibold text-slate-600 uppercase">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200">
              {products.length === 0 ? (
                <tr>
                  <td colSpan={5} className="px-4 py-8 text-center text-slate-500">
                    No products found. Import products from recommendations first.
                  </td>
                </tr>
              ) : (
                products.map(product => (
                  <tr key={product.id} className={syncedProducts.has(product.id) ? "bg-emerald-50" : ""}>
                    <td className="px-4 py-3">
                      <div>
                        <p className="text-sm font-medium text-slate-900">{product.title}</p>
                        <p className="text-xs text-slate-500">{product.id.substring(0, 8)}...</p>
                      </div>
                    </td>
                    <td className="px-4 py-3 text-sm tabular-nums text-slate-900">
                      {product.price ? `$${product.price.toFixed(2)}` : "—"}
                    </td>
                    <td className="px-4 py-3 text-sm text-slate-600">
                      {product.supplierKey || "—"}
                    </td>
                    <td className="px-4 py-3">
                      {syncedProducts.has(product.id) ? (
                        <Badge tone="green">synced</Badge>
                      ) : (
                        <Badge tone="slate">pending</Badge>
                      )}
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => handleSyncProduct(product)}
                          disabled={syncingProductId === product.id || !product.supplierKey || !product.price}
                          title={!product.supplierKey ? "Assign supplier first" : !product.price ? "Set price first" : ""}
                          className="px-3 py-1 text-xs bg-slate-200 hover:bg-slate-300 disabled:opacity-50 text-slate-700 rounded transition-colors">
                          {syncingProductId === product.id ? "..." : "Sync"}
                        </button>
                        <button
                          onClick={() => handleUpdatePrice(product)}
                          disabled={syncingProductId === product.id || !syncedProducts.has(product.id) || !product.price}
                          className="px-3 py-1 text-xs bg-blue-100 hover:bg-blue-200 disabled:opacity-50 text-blue-700 rounded transition-colors">
                          Update Price
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Card>

      {/* Theme Customization */}
      <Card>
        <div className="space-y-3">
          <h3 className="font-semibold text-slate-900">Storefront Theme</h3>
          <p className="text-sm text-slate-600">Customise homepage content and branding.</p>
          <div className="grid grid-cols-2 gap-3">
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Theme name</span>
              <input value={theme.themeName ?? ""} onChange={(e) => setTheme({ ...theme, themeName: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Primary colour</span>
              <input type="color" value={theme.primaryColor ?? "#2563eb"} onChange={(e) => setTheme({ ...theme, primaryColor: e.target.value })}
                className="h-10 w-20 border border-slate-300 rounded-lg" />
            </label>
            <label className="text-sm text-slate-700 col-span-2">
              <span className="block mb-1 font-medium">Homepage heading</span>
              <input value={theme.homepageHeading ?? ""} onChange={(e) => setTheme({ ...theme, homepageHeading: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700 col-span-2">
              <span className="block mb-1 font-medium">Homepage subheading</span>
              <input value={theme.homepageSubheading ?? ""} onChange={(e) => setTheme({ ...theme, homepageSubheading: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700 col-span-2">
              <span className="block mb-1 font-medium">Logo URL</span>
              <input value={theme.logoUrl ?? ""} onChange={(e) => setTheme({ ...theme, logoUrl: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
          </div>
          <div>
            <button onClick={handleSaveTheme} disabled={loading === "theme"}
              className="px-4 py-2 bg-brand-500 hover:bg-brand-600 disabled:opacity-50 text-white rounded-lg text-sm font-medium transition-colors">
              {loading === "theme" ? "Saving..." : "Save Theme"}
            </button>
          </div>
        </div>
      </Card>

      {/* Store Pages */}
      <Card>
        <h3 className="font-semibold text-slate-900 mb-3">Store Pages</h3>
        {pages.length === 0 ? (
          <p className="text-sm text-slate-500">No pages yet — click Initialize Store to create the default legal pages.</p>
        ) : (
          <ul className="divide-y divide-slate-200">
            {pages.map(p => (
              <li key={p.id} className="py-2 flex items-center justify-between">
                <span className="text-sm text-slate-900">{p.title}</span>
                <code className="text-xs text-slate-500">/{p.handle}</code>
              </li>
            ))}
          </ul>
        )}
      </Card>

      {/* Production Shopify Admin Panel */}
      <Card>
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-semibold text-slate-900">Shopify Admin (production)</h3>
          <button onClick={handleRefreshHealth}
            className="px-3 py-1 text-xs bg-slate-200 hover:bg-slate-300 text-slate-700 rounded transition-colors">
            Refresh
          </button>
        </div>
        {health && (
          <div className="grid grid-cols-4 gap-3 mb-4 text-sm">
            <div>
              <div className="text-xs text-slate-500">Connected</div>
              <div className="font-medium">{health.connected ? "yes" : "no"}</div>
            </div>
            <div>
              <div className="text-xs text-slate-500">Managed</div>
              <div className="font-medium tabular-nums">{health.managedProductCount}</div>
            </div>
            <div>
              <div className="text-xs text-slate-500">Pending</div>
              <div className="font-medium tabular-nums">{health.pendingCount}</div>
            </div>
            <div>
              <div className="text-xs text-slate-500">Failed</div>
              <div className="font-medium tabular-nums text-red-700">{health.failedCount}</div>
            </div>
          </div>
        )}
        {adminCfg && (
          <div className="grid grid-cols-2 gap-3">
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Shop domain</span>
              <input defaultValue={adminCfg.shopDomain}
                onChange={(e) => setAdminDraft({ ...adminDraft, shopDomain: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Managed tag</span>
              <input defaultValue={adminCfg.managedTag}
                onChange={(e) => setAdminDraft({ ...adminDraft, managedTag: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Access token {adminCfg.hasAccessToken ? "(set)" : "(not set)"}</span>
              <input type="password" placeholder="Leave blank to keep"
                onChange={(e) => setAdminDraft({ ...adminDraft, accessToken: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Webhook secret {adminCfg.hasWebhookSecret ? "(set)" : "(not set)"}</span>
              <input type="password" placeholder="Leave blank to keep"
                onChange={(e) => setAdminDraft({ ...adminDraft, webhookSecret: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm" />
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Default publication status</span>
              <select defaultValue={adminCfg.defaultPublicationStatus}
                onChange={(e) => setAdminDraft({ ...adminDraft, defaultPublicationStatus: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm">
                <option value="active">active</option>
                <option value="draft">draft</option>
              </select>
            </label>
            <label className="text-sm text-slate-700">
              <span className="block mb-1 font-medium">Archive behaviour</span>
              <select defaultValue={adminCfg.archiveBehaviour}
                onChange={(e) => setAdminDraft({ ...adminDraft, archiveBehaviour: e.target.value })}
                className="w-full px-3 py-2 border border-slate-300 rounded-lg text-sm">
                <option value="archive">archive</option>
                <option value="unpublish">unpublish</option>
              </select>
            </label>
            <label className="text-sm text-slate-700 flex items-center gap-2 col-span-2">
              <input type="checkbox" defaultChecked={adminCfg.autoArchiveOnZeroStock}
                onChange={(e) => setAdminDraft({ ...adminDraft, autoArchiveOnZeroStock: e.target.checked })} />
              <span>Auto-archive on zero stock</span>
            </label>
            <div className="col-span-2 flex items-center gap-3">
              <button onClick={handleSaveAdmin}
                className="px-4 py-2 bg-brand-500 hover:bg-brand-600 text-white rounded-lg text-sm font-medium">
                Save config
              </button>
              <button onClick={handleProductionBulkSync} disabled={loading === "syncing"}
                className="px-4 py-2 bg-emerald-500 hover:bg-emerald-600 disabled:opacity-50 text-white rounded-lg text-sm font-medium">
                {loading === "syncing" ? "Syncing..." : "Bulk sync via production pipeline"}
              </button>
            </div>
          </div>
        )}
      </Card>

      {/* Sync Status List */}
      <Card>
        <h3 className="font-semibold text-slate-900 mb-3">Product sync status</h3>
        {syncRows.length === 0 ? (
          <p className="text-sm text-slate-500">No sync rows yet.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full">
              <thead>
                <tr className="border-b border-slate-200">
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Brain ID</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Shopify ID</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Title</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Status</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Pub</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Stock</th>
                  <th className="text-left px-3 py-2 text-xs font-semibold text-slate-600 uppercase">Last sync</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-200">
                {syncRows.map(r => (
                  <tr key={r.id}>
                    <td className="px-3 py-2 text-xs font-mono text-slate-700">{r.brainProductId.substring(0, 8)}</td>
                    <td className="px-3 py-2 text-xs text-slate-700 tabular-nums">{r.shopifyProductId}</td>
                    <td className="px-3 py-2 text-sm text-slate-900">{r.title}</td>
                    <td className="px-3 py-2"><Badge tone={r.syncStatus === "Synced" ? "green" : r.syncStatus === "Failed" ? "red" : "slate"}>{r.syncStatus}</Badge></td>
                    <td className="px-3 py-2 text-xs text-slate-600">{r.publicationStatus}</td>
                    <td className="px-3 py-2 text-xs text-slate-600 tabular-nums">{r.lastKnownStock}</td>
                    <td className="px-3 py-2 text-xs text-slate-500">{new Date(r.lastSyncAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {/* Info */}
      <div className="bg-slate-50 rounded-xl shadow-sm border border-slate-200 p-5">
        <h4 className="font-semibold text-slate-900 mb-2">How it works</h4>
        <ul className="space-y-2 text-sm text-slate-600">
          <li>• Products must have a supplier assigned before syncing</li>
          <li>• Click <strong>Sync</strong> to create the product on Shopify</li>
          <li>• Use <strong>Update Price</strong> to change pricing on synced products</li>
          <li>• Use <strong>Sync All</strong> to bulk-sync all products at once</li>
          <li>• Synced products appear with a green badge</li>
        </ul>
      </div>
    </div>
  );
}
