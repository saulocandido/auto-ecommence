const BRAIN_URL = (import.meta.env.VITE_BRAIN_URL as string) ?? "http://localhost:5080";
const SELECTION_URL = (import.meta.env.VITE_SELECTION_URL as string) ?? "http://localhost:5090";
const SUPPLIER_URL = (import.meta.env.VITE_SUPPLIER_URL as string) ?? "http://localhost:5100";
const STORE_URL = (import.meta.env.VITE_STORE_URL as string) ?? "http://localhost:5110";
const CONFIG_URL = (import.meta.env.VITE_CONFIG_URL as string) ?? "http://localhost:5120";

export const apiKeyStorage = {
  get: () => localStorage.getItem("brain-api-key") ?? "",
  set: (v: string) => localStorage.setItem("brain-api-key", v),
  clear: () => localStorage.removeItem("brain-api-key")
};

async function brain<T>(path: string, init?: RequestInit): Promise<T> {
  const key = apiKeyStorage.get();
  const resp = await fetch(`${BRAIN_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      "X-Api-Key": key,
      ...(init?.headers ?? {})
    }
  });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  if (resp.status === 204) return undefined as T;
  return resp.json() as Promise<T>;
}

async function selection<T>(path: string, init?: RequestInit): Promise<T> {
  const key = apiKeyStorage.get();
  const resp = await fetch(`${SELECTION_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      "X-Api-Key": key,
      ...(init?.headers ?? {})
    }
  });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.json() as Promise<T>;
}

async function supplier<T>(path: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(`${SUPPLIER_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {})
    }
  });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.json() as Promise<T>;
}

async function store<T>(path: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(`${STORE_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {})
    }
  });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.json() as Promise<T>;
}

async function config<T>(path: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(`${CONFIG_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {})
    }
  });
  if (!resp.ok) throw new Error(`${resp.status} ${resp.statusText}: ${await resp.text()}`);
  return resp.json() as Promise<T>;
}

export type SupplierProfile = {
  supplierKey: string;
  name: string;
  region: string;
  baseReliability: number;
  notes?: string;
};

export type SupplierEvaluation = {
  supplierKey: string;
  score: number;
  priceScore: number;
  ratingScore: number;
  shippingScore: number;
  stockScore: number;
  reliabilityScore: number;
  cost: number;
  currency: string;
  shippingDays: number;
  stockAvailable: number;
  viable: boolean;
  rejectionReason?: string;
};

export type SupplierSelectionResult = {
  productId: string;
  externalId: string;
  chosenSupplierKey?: string;
  chosenCost?: number;
  currency?: string;
  score?: number;
  evaluations: SupplierEvaluation[];
  rejectionReason?: string;
};

export type ProductResponse = {
  id: string;
  externalId: string;
  title: string;
  category: string;
  description?: string;
  imageUrls: string[];
  tags: string[];
  targetMarket: string;
  score: number;
  cost?: number;
  price?: number;
  marginPercent?: number;
  status: string;
  supplierKey?: string;
  suppliers: SupplierListing[];
  createdAt: string;
  updatedAt: string;
};

export type SupplierListing = {
  supplierKey: string;
  externalProductId: string;
  cost: number;
  currency: string;
  shippingDays: number;
  rating: number;
  stockAvailable: number;
  url?: string;
};

export type OrderResponse = {
  id: string;
  shopOrderId: string;
  customerEmail: string;
  customerName?: string;
  shippingCountry: string;
  status: string;
  trackingNumber?: string;
  trackingUrl?: string;
  total: number;
  lines: { productId: string; quantity: number; unitPrice: number }[];
  createdAt: string;
  updatedAt: string;
};

export type DashboardMetrics = {
  totalProducts: number;
  activeProducts: number;
  pausedProducts: number;
  totalOrders: number;
  pendingOrders: number;
  fulfilledOrders: number;
  revenueLast24h: number;
  profitLast24h: number;
  avgMarginPercent: number;
  topProducts: { id: string; title: string; orderCount: number; revenue: number }[];
  recentEvents: { id: string; type: string; source: string; occurredAt: string }[];
};

export type RecommendationResponse = {
  generatedAt: string;
  config: SelectionConfig;
  recommendations: ScoredCandidate[];
};

export type SelectionConfig = {
  targetCategories: string[];
  minPrice: number | null;
  maxPrice: number | null;
  minScore: number | null;
  topNPerCategory: number;
  targetMarket: string;
  maxShippingDays: number | null;
};

export type RecommendationProviderSettings = {
  hasApiKey: boolean;
  model: string;
  reasoningEffort: string;
  maxCandidates: number;
  requestTimeoutSeconds: number;
  effectiveProvider: string;
};

export type RecommendationNamedCredentialStatus = {
  name: string;
  hasValue: boolean;
  preview?: string | null;
};

export type RecommendationCredentialsSettings = {
  hasOpenAiApiKey: boolean;
  hasGeminiApiKey: boolean;
  openAiApiKeyPreview?: string | null;
  geminiApiKeyPreview?: string | null;
  additionalSecrets: RecommendationNamedCredentialStatus[];
};

export type RecommendationProviderSettingsUpdate = {
  apiKey?: string | null;
  clearApiKey: boolean;
  model: string;
  reasoningEffort: string;
  maxCandidates: number;
  requestTimeoutSeconds: number;
};

export type RecommendationNamedCredentialUpdate = {
  name: string;
  value?: string | null;
  clear: boolean;
};

export type RecommendationCredentialsUpdate = {
  openAiApiKey?: string | null;
  clearOpenAiApiKey: boolean;
  geminiApiKey?: string | null;
  clearGeminiApiKey: boolean;
  additionalSecrets: RecommendationNamedCredentialUpdate[];
};

export type RecommendationSettingsResponse = {
  provider: RecommendationProviderSettings;
  credentials: RecommendationCredentialsSettings;
  selection: SelectionConfig;
};

export type RecommendationSettingsUpdate = {
  provider: RecommendationProviderSettingsUpdate;
  credentials: RecommendationCredentialsUpdate;
  selection: SelectionConfig;
};

export type ScoredCandidate = {
  candidate: {
    externalId: string;
    source: string;
    title: string;
    category: string;
    description?: string;
    imageUrls: string[];
    tags: string[];
    price: number;
    currency: string;
    rating: number;
    reviewCount: number;
    estimatedMonthlySearches: number;
    competitorCount: number;
    shippingDaysToTarget: number;
    supplierCandidates: SupplierListing[];
  };
  score: number;
  breakdown: Record<string, number>;
  approved: boolean;
  rejectionReason?: string;
};

export type LinkValidationResult = {
  externalId: string;
  supplierKey: string;
  originalUrl: string;
  correctedUrl?: string;
  status: 'Verified' | 'Corrected' | 'Invalid' | 'Skipped';
  detail?: string;
};

export type LinkValidationReport = {
  startedAt: string;
  completedAt: string;
  total: number;
  verified: number;
  corrected: number;
  invalid: number;
  skipped: number;
  results: LinkValidationResult[];
};

export type SyncProductRequest = {
  brainProductId: string;
  title: string;
  description: string;
  price: number;
  imageUrl?: string;
};

export type UpdatePriceRequest = {
  brainProductId: string;
  newPrice: number;
};

export type ShopifyThemeConfig = {
  themeName?: string | null;
  homepageHeading?: string | null;
  homepageSubheading?: string | null;
  primaryColor?: string | null;
  logoUrl?: string | null;
};

export type ShopifyPage = {
  id: number;
  title: string;
  handle: string;
  bodyHtml: string;
};

export type ShopifySyncResult = {
  brainProductId: string;
  shopifyProductId?: number | null;
  status: string;
  error?: string | null;
};

export type ShopifyHealthResult = {
  connected: boolean;
  managedProductCount: number;
  pendingCount: number;
  failedCount: number;
  metrics: Record<string, number>;
};

export type ShopifyProductSyncRow = {
  id: string;
  brainProductId: string;
  shopifyProductId: number;
  title: string;
  price: number;
  supplierKey?: string | null;
  sourceSupplierUrl?: string | null;
  syncStatus: string;
  syncedAt: string;
  lastSyncAt: string;
  lastSyncError?: string | null;
  managedBySystem: boolean;
  publicationStatus: string;
  lastKnownStock: number;
};

export type ShopifyAdminConfigView = {
  id: string;
  shopDomain: string;
  hasAccessToken: boolean;
  hasWebhookSecret: boolean;
  defaultPublicationStatus: string;
  archiveBehaviour: string;
  autoArchiveOnZeroStock: boolean;
  managedTag: string;
  metafieldNamespace: string;
  maxRetryAttempts: number;
  retryBaseDelayMs: number;
  conflictStrategy: string;
  salesChannelsJson: string;
  collectionMappingJson: string;
  updatedAt: string;
};

export type ShopifyAdminConfigUpdate = Partial<{
  shopDomain: string;
  accessToken: string;
  webhookSecret: string;
  defaultPublicationStatus: string;
  archiveBehaviour: string;
  autoArchiveOnZeroStock: boolean;
  managedTag: string;
  metafieldNamespace: string;
  maxRetryAttempts: number;
  retryBaseDelayMs: number;
  conflictStrategy: string;
  salesChannelsJson: string;
  collectionMappingJson: string;
}>;

// ── Shopify Automation types ──

export type AutomationSessionStatusDto = {
  state: "connected" | "login_required" | "unknown" | "error";
  lastValidatedAt: string | null;
  lastLoggedInAt: string | null;
  storageStateExists: boolean;
  message: string | null;
};

export type AutomationConfigDto = {
  shopifyStoreUrl: string;
  findProductsUrl: string;
  importListUrl: string;
  appUrl: string;
  shopifyApiKey: string;
  shopifyHost: string;
  defaultSearch: string;
  authMode: string;
  maxRetries: number;
  matchConfidenceThreshold: number;
  headlessMode: boolean;
  useApiFirst: boolean;
  sessionCookie?: string | null;
  authToken?: string | null;
};

export type AutomationConfigUpdateDto = Partial<{
  shopifyStoreUrl: string;
  findProductsUrl: string;
  importListUrl: string;
  appUrl: string;
  shopifyApiKey: string;
  shopifyHost: string;
  defaultSearch: string;
  authMode: string;
  maxRetries: number;
  matchConfidenceThreshold: number;
  headlessMode: boolean;
  useApiFirst: boolean;
  sessionCookie: string;
  authToken: string;
}>;

export type AutomationRunDto = {
  id: string;
  status: string;
  totalProducts: number;
  processedCount: number;
  importedCount: number;
  pushedCount: number;
  failedCount: number;
  startedAt: string;
  completedAt?: string | null;
  error?: string | null;
};

export type AutomationProductDto = {
  id: string;
  runId: string;
  brainProductId: string;
  productName: string;
  supplierKey?: string | null;
  status: string;
  currentStep: string;
  matchedResultTitle?: string | null;
  confidence: number;
  errorReason?: string | null;
  shopifyProductId?: number | null;
  updatedAt: string;
};

export type AutomationLogDto = {
  id: string;
  runId: string;
  productId?: string | null;
  level: string;
  message: string;
  details?: string | null;
  timestamp: string;
};

export type AutomationMetricsDto = {
  total: number;
  processing: number;
  imported: number;
  pushed: number;
  failed: number;
  manualReview: number;
};

export const api = {
  metrics: () => brain<DashboardMetrics>("/api/dashboard/metrics"),
  products: (status?: string) =>
    brain<ProductResponse[]>(`/api/products${status ? `?status=${status}` : ""}`),
  updateProduct: (id: string, body: Partial<ProductResponse> & { status?: string; price?: number }) =>
    brain<ProductResponse>(`/api/products/${id}`, { method: "PATCH", body: JSON.stringify(body) }),
  orders: () => brain<OrderResponse[]>("/api/orders"),
  rules: () => brain<unknown[]>("/api/pricing/rules"),
  upsertRule: (rule: { category: string; markupMultiplier: number; minMarginPercent: number; minPrice: number; maxPrice: number }) =>
    brain("/api/pricing/rules", { method: "PUT", body: JSON.stringify(rule) }),
  recommendations: () => selection<RecommendationResponse>("/recommendations"),
  scan: () => selection<{ imported: number; total: number; approved: number }>("/scan", { method: "POST", body: "null" }),
  validateLinks: () => selection<LinkValidationReport>("/links/validate", { method: "POST" }),
  recommendationSettings: () => config<RecommendationSettingsResponse>("/configuration/recommendations"),
  updateRecommendationSettings: (body: RecommendationSettingsUpdate) =>
    config<RecommendationSettingsResponse>("/configuration/recommendations", {
      method: "PUT",
      body: JSON.stringify(body)
    }),
  suppliers: () => supplier<SupplierProfile[]>("/suppliers"),
  supplierPreview: (productId: string) =>
    supplier<SupplierSelectionResult>(`/selection/${productId}/preview`),
  supplierAssign: (productId: string) =>
    supplier<SupplierSelectionResult>(`/selection/${productId}/assign`, { method: "POST" }),
  storeInitialize: () =>
    store<{ success: boolean; message: string }>("/api/store/initialize", { method: "POST" }),
  storeProducts: (status?: string) =>
    store<ProductResponse[]>(`/api/store/products${status ? `?status=${status}` : ""}`),
  storeSyncProduct: (body: SyncProductRequest) =>
    store<{ success: boolean; message: string }>("/api/store/sync-product", { method: "POST", body: JSON.stringify(body) }),
  storeUpdatePrice: (body: UpdatePriceRequest) =>
    store<{ success: boolean; message: string }>("/api/store/sync-price", { method: "POST", body: JSON.stringify(body) }),
  storeUpdateStatus: (brainProductId: string, status: string) =>
    store<{ success: boolean; message: string }>("/api/store/sync-status", {
      method: "POST",
      body: JSON.stringify({ brainProductId, status })
    }),
  storeUpdateStock: (brainProductId: string, quantity: number) =>
    store<{ success: boolean; message: string }>("/api/store/sync-stock", {
      method: "POST",
      body: JSON.stringify({ brainProductId, quantity })
    }),
  storeGetTheme: () => store<ShopifyThemeConfig>("/api/store/theme"),
  storeUpdateTheme: (config: ShopifyThemeConfig) =>
    store<{ success: boolean; theme: { id: number; name: string; role: string } }>("/api/store/theme", {
      method: "PUT",
      body: JSON.stringify(config)
    }),
  storeListPages: () => store<ShopifyPage[]>("/api/store/pages"),
  storeUpsertPage: (title: string, handle: string, bodyHtml: string) =>
    store<{ success: boolean; page: ShopifyPage }>("/api/store/pages", {
      method: "PUT",
      body: JSON.stringify({ title, handle, bodyHtml })
    }),

  // Production Shopify module
  shopifySyncProduct: (brainProductId: string) =>
    store<ShopifySyncResult>("/api/shopify/sync-product", {
      method: "POST", body: JSON.stringify({ brainProductId })
    }),
  shopifyBulkSync: (brainProductIds?: string[]) =>
    store<{ count: number; results: ShopifySyncResult[] }>("/api/shopify/sync-products/bulk", {
      method: "POST", body: JSON.stringify({ brainProductIds: brainProductIds ?? null })
    }),
  shopifySyncPrice: (brainProductId: string, newPrice: number) =>
    store<ShopifySyncResult>("/api/shopify/sync-price", {
      method: "POST", body: JSON.stringify({ brainProductId, newPrice })
    }),
  shopifySyncStock: (brainProductId: string, quantity: number) =>
    store<ShopifySyncResult>("/api/shopify/sync-stock", {
      method: "POST", body: JSON.stringify({ brainProductId, quantity })
    }),
  shopifyArchive: (brainProductId: string, reason: string) =>
    store<ShopifySyncResult>("/api/shopify/archive-product", {
      method: "POST", body: JSON.stringify({ brainProductId, reason })
    }),
  shopifyPublish: (brainProductId: string) =>
    store<ShopifySyncResult>("/api/shopify/publish-product", {
      method: "POST", body: JSON.stringify({ brainProductId })
    }),
  shopifySyncStatus: (brainProductId: string) =>
    store<ShopifyProductSyncRow | { productId: string; status: string }>(`/api/shopify/sync-status/${brainProductId}`),
  shopifyHealth: () => store<ShopifyHealthResult>("/api/shopify/health"),
  shopifyMetrics: () => store<Record<string, number>>("/api/shopify/metrics"),
  shopifyAdminConfigGet: () => store<ShopifyAdminConfigView>("/api/shopify/admin/config"),
  shopifyAdminConfigPut: (body: ShopifyAdminConfigUpdate) =>
    store<ShopifyAdminConfigView>("/api/shopify/admin/config", {
      method: "PUT", body: JSON.stringify(body)
    }),
  shopifySyncStatusList: (status?: string) =>
    store<ShopifyProductSyncRow[]>(`/api/shopify/admin/sync-status${status ? `?status=${status}` : ""}`),
  shopifyDeadLetters: () =>
    store<Array<{ id: string; operation: string; error: string; createdAt: string; attemptCount: number }>>(
      "/api/shopify/admin/dead-letters"),

  // ── Shopify Automation ──
  automationConfigGet: () =>
    store<AutomationConfigDto>("/api/shopify/automation/config"),
  automationConfigPut: (body: AutomationConfigUpdateDto) =>
    store<AutomationConfigDto>("/api/shopify/automation/config", {
      method: "PUT", body: JSON.stringify(body)
    }),
  automationStart: () =>
    store<AutomationRunDto>("/api/shopify/automation/run", { method: "POST" }),
  automationStop: (runId: string) =>
    store<AutomationRunDto>(`/api/shopify/automation/run/${runId}/stop`, { method: "POST" }),
  automationRetry: (runId: string) =>
    store<AutomationRunDto>(`/api/shopify/automation/run/${runId}/retry`, { method: "POST" }),
  automationResume: (runId: string) =>
    store<AutomationRunDto>(`/api/shopify/automation/run/${runId}/resume`, { method: "POST" }),
  automationSessionGet: () =>
    store<AutomationSessionStatusDto>("/api/shopify/automation/session"),
  automationSessionValidate: () =>
    store<AutomationSessionStatusDto>("/api/shopify/automation/session/validate", { method: "POST" }),
  automationSessionConnect: () =>
    store<AutomationSessionStatusDto>("/api/shopify/automation/session/connect", { method: "POST" }),
  automationSessionUpload: (storageState: string) =>
    store<AutomationSessionStatusDto>("/api/shopify/automation/session/upload", {
      method: "POST", body: JSON.stringify({ storageState })
    }),
  automationActiveRun: () =>
    store<AutomationRunDto>("/api/shopify/automation/run/active").catch(() => null),
  automationRuns: (take = 20) =>
    store<AutomationRunDto[]>(`/api/shopify/automation/runs?take=${take}`),
  automationRun: (runId: string) =>
    store<AutomationRunDto>(`/api/shopify/automation/run/${runId}`),
  automationProducts: (runId: string) =>
    store<AutomationProductDto[]>(`/api/shopify/automation/run/${runId}/products`),
  automationLogs: (runId: string, take = 100) =>
    store<AutomationLogDto[]>(`/api/shopify/automation/run/${runId}/logs?take=${take}`),
  automationMetrics: (runId: string) =>
    store<AutomationMetricsDto>(`/api/shopify/automation/run/${runId}/metrics`),
};
