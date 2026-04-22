import { useEffect, useState, type FormEvent } from "react";
import {
  api,
  apiKeyStorage,
  type RecommendationSettingsResponse,
  type RecommendationSettingsUpdate
} from "../api";
import { Badge, Card } from "../components/Card";

/* ── Types ── */

type ProviderEntry = {
  id: string;
  provider: "openai" | "gemini" | "groq" | "openrouter";
  model: string;
  apiKey: string;
  isPrimary: boolean;
  hasSavedKey: boolean;
  preview: string | null;
  clearKey: boolean;
};

type FormState = {
  providers: ProviderEntry[];
  reasoningEffort: string;
  maxCandidatesEnabled: boolean;
  maxCandidates: string;
  requestTimeoutSeconds: string;
  targetCategories: string;
  minPriceEnabled: boolean;
  minPrice: string;
  maxPriceEnabled: boolean;
  maxPrice: string;
  minScoreEnabled: boolean;
  minScore: string;
  topNPerCategoryEnabled: boolean;
  topNPerCategory: string;
  targetMarket: string;
  maxShippingDaysEnabled: boolean;
  maxShippingDays: string;
};

/* ── Helpers ── */

function uid() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

function toFormState(settings: RecommendationSettingsResponse): FormState {
  const providers: ProviderEntry[] = [];

  const isGeminiPrimary = settings.provider.effectiveProvider === "Gemini";
  const isOpenAiPrimary = settings.provider.effectiveProvider === "OpenAI";

  if (settings.credentials.hasOpenAiApiKey) {
    providers.push({
      id: uid(),
      provider: "openai",
      model: isOpenAiPrimary ? settings.provider.model : "gpt-5",
      apiKey: "",
      isPrimary: isOpenAiPrimary || (!isGeminiPrimary && !settings.credentials.hasGeminiApiKey),
      hasSavedKey: true,
      preview: settings.credentials.openAiApiKeyPreview ?? null,
      clearKey: false,
    });
  }

  if (settings.credentials.hasGeminiApiKey) {
    providers.push({
      id: uid(),
      provider: "gemini",
      model: isGeminiPrimary ? settings.provider.model : "gemini-2.5-flash",
      apiKey: "",
      isPrimary: isGeminiPrimary,
      hasSavedKey: true,
      preview: settings.credentials.geminiApiKeyPreview ?? null,
      clearKey: false,
    });
  }

  if (providers.length === 0) {
    providers.push({
      id: uid(), provider: "openai", model: "gpt-5", apiKey: "",
      isPrimary: true, hasSavedKey: false, preview: null, clearKey: false,
    });
  }

  // Load Groq / OpenRouter from additional secrets
  const groqSecret = settings.credentials.additionalSecrets.find(s => s.name === "GROQ_API_KEY");
  if (groqSecret?.hasValue) {
    providers.push({
      id: uid(), provider: "groq", model: "llama-3.3-70b-versatile", apiKey: "",
      isPrimary: false, hasSavedKey: true, preview: groqSecret.preview ?? null, clearKey: false,
    });
  }
  const openRouterSecret = settings.credentials.additionalSecrets.find(s => s.name === "OPENROUTER_API_KEY");
  if (openRouterSecret?.hasValue) {
    providers.push({
      id: uid(), provider: "openrouter", model: "meta-llama/llama-3.3-70b-instruct:free", apiKey: "",
      isPrimary: false, hasSavedKey: true, preview: openRouterSecret.preview ?? null, clearKey: false,
    });
  }

  return {
    providers,
    reasoningEffort: settings.provider.reasoningEffort,
    maxCandidatesEnabled: true,
    maxCandidates: String(settings.provider.maxCandidates),
    requestTimeoutSeconds: String(settings.provider.requestTimeoutSeconds),
    targetCategories: settings.selection.targetCategories.join(", "),
    minPriceEnabled: settings.selection.minPrice != null,
    minPrice: settings.selection.minPrice != null ? String(settings.selection.minPrice) : "10",
    maxPriceEnabled: settings.selection.maxPrice != null,
    maxPrice: settings.selection.maxPrice != null ? String(settings.selection.maxPrice) : "150",
    minScoreEnabled: settings.selection.minScore != null,
    minScore: settings.selection.minScore != null ? String(settings.selection.minScore) : "55",
    topNPerCategoryEnabled: true,
    topNPerCategory: String(settings.selection.topNPerCategory),
    targetMarket: settings.selection.targetMarket,
    maxShippingDaysEnabled: settings.selection.maxShippingDays != null,
    maxShippingDays: settings.selection.maxShippingDays != null ? String(settings.selection.maxShippingDays) : "18",
  };
}

function parseNumber(value: string, label: string) {
  const parsed = Number(value);
  if (Number.isNaN(parsed)) throw new Error(`${label} must be a valid number.`);
  return parsed;
}

function buildAdditionalSecret(form: FormState, providerType: string, secretName: string) {
  const entry = form.providers.find(p => p.provider === providerType);
  if (!entry) return [];
  if (entry.clearKey) return [{ name: secretName, value: null, clear: true }];
  if (entry.apiKey.trim()) return [{ name: secretName, value: entry.apiKey.trim(), clear: false }];
  return [];
}

function toPayload(form: FormState): RecommendationSettingsUpdate {
  const primary = form.providers.find(p => p.isPrimary) ?? form.providers[0];
  const openai = form.providers.find(p => p.provider === "openai");
  const gemini = form.providers.find(p => p.provider === "gemini");

  return {
    provider: {
      apiKey: null,
      clearApiKey: false,
      model: primary?.model.trim() ?? "gpt-5",
      reasoningEffort: form.reasoningEffort.trim(),
      maxCandidates: parseNumber(form.maxCandidates, "Max candidates"),
      requestTimeoutSeconds: parseNumber(form.requestTimeoutSeconds, "Request timeout"),
    },
    credentials: {
      openAiApiKey: openai?.apiKey.trim() || null,
      clearOpenAiApiKey: openai?.clearKey ?? (!openai ? false : false),
      geminiApiKey: gemini?.apiKey.trim() || null,
      clearGeminiApiKey: gemini?.clearKey ?? false,
      additionalSecrets: [
        ...buildAdditionalSecret(form, "groq", "GROQ_API_KEY"),
        ...buildAdditionalSecret(form, "openrouter", "OPENROUTER_API_KEY"),
      ],
    },
    selection: {
      targetCategories: form.targetCategories.split(/[\n,]/).map(v => v.trim()).filter(Boolean),
      minPrice: form.minPriceEnabled ? parseNumber(form.minPrice, "Minimum price") : null,
      maxPrice: form.maxPriceEnabled ? parseNumber(form.maxPrice, "Maximum price") : null,
      minScore: form.minScoreEnabled ? parseNumber(form.minScore, "Minimum score") : null,
      topNPerCategory: parseNumber(form.topNPerCategory, "Top N per category"),
      targetMarket: form.targetMarket.trim(),
      maxShippingDays: form.maxShippingDaysEnabled ? parseNumber(form.maxShippingDays, "Max shipping days") : null,
    },
  };
}

/* ── Shared field components ── */

type FieldProps = {
  label: string;
  value: string;
  onChange: (v: string) => void;
  type?: string;
  placeholder?: string;
  help?: string;
  disabled?: boolean;
};

function Field({ label, value, onChange, type = "text", placeholder, help, disabled = false }: FieldProps) {
  return (
    <label className="block">
      <span className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</span>
      <input
        type={type} value={value} disabled={disabled}
        onChange={e => onChange(e.target.value)} placeholder={placeholder}
        className="mt-1 block w-full rounded-lg border-slate-300 text-sm shadow-sm focus:border-brand-500 focus:ring-brand-500 disabled:bg-slate-100 disabled:text-slate-500"
      />
      {help && <span className="mt-1 block text-xs text-slate-400">{help}</span>}
    </label>
  );
}

function SelectField({ label, value, onChange, options, help }: {
  label: string; value: string; onChange: (v: string) => void;
  options: { value: string; label: string }[]; help?: string;
}) {
  return (
    <label className="block">
      <span className="text-xs font-medium uppercase tracking-wide text-slate-500">{label}</span>
      <select value={value} onChange={e => onChange(e.target.value)}
        className="mt-1 block w-full rounded-lg border-slate-300 text-sm shadow-sm focus:border-brand-500 focus:ring-brand-500">
        {options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
      {help && <span className="mt-1 block text-xs text-slate-400">{help}</span>}
    </label>
  );
}

function SectionHeading({ title, description }: { title: string; description?: string }) {
  return (
    <div className="pb-2 border-b border-slate-200 mb-4">
      <h3 className="text-sm font-semibold text-slate-800">{title}</h3>
      {description && <p className="text-xs text-slate-500 mt-0.5">{description}</p>}
    </div>
  );
}

function Toggle({ enabled, onChange, label }: { enabled: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button type="button" onClick={() => onChange(!enabled)}
      className="flex items-center gap-2 text-sm select-none group">
      <span className={`relative inline-flex h-5 w-9 shrink-0 rounded-full border-2 border-transparent transition-colors ${
        enabled ? "bg-brand-600" : "bg-slate-300"
      }`}>
        <span className={`pointer-events-none inline-block h-4 w-4 rounded-full bg-white shadow transition-transform ${
          enabled ? "translate-x-4" : "translate-x-0"
        }`} />
      </span>
      <span className={`text-xs font-medium ${enabled ? "text-slate-700" : "text-slate-400"}`}>{label}</span>
    </button>
  );
}

const defaultModels: Record<string, string> = {
  openai: "gpt-5",
  gemini: "gemini-2.5-flash",
  groq: "llama-3.3-70b-versatile",
  openrouter: "meta-llama/llama-3.3-70b-instruct:free",
};

const modelSuggestions: Record<string, { value: string; label: string }[]> = {
  openai: [
    { value: "gpt-5", label: "GPT-5" },
    { value: "gpt-4.1", label: "GPT-4.1" },
    { value: "gpt-4.1-mini", label: "GPT-4.1 Mini" },
  ],
  gemini: [
    { value: "gemini-2.5-flash", label: "Gemini 2.5 Flash" },
    { value: "gemini-2.5-flash-lite", label: "Gemini 2.5 Flash Lite" },
    { value: "gemini-2.5-pro", label: "Gemini 2.5 Pro" },
  ],
  groq: [
    { value: "llama-3.3-70b-versatile", label: "Llama 3.3 70B" },
    { value: "llama-3.1-8b-instant", label: "Llama 3.1 8B Instant" },
    { value: "mixtral-8x7b-32768", label: "Mixtral 8x7B" },
  ],
  openrouter: [
    { value: "meta-llama/llama-3.3-70b-instruct:free", label: "Llama 3.3 70B (Free)" },
    { value: "mistralai/mistral-7b-instruct:free", label: "Mistral 7B (Free)" },
    { value: "google/gemma-2-9b-it:free", label: "Gemma 2 9B (Free)" },
  ],
};

/* ══════════════════════════════════════════
   Selection Rules sub-page
   ══════════════════════════════════════════ */

function SelectionRulesTab({
  form, updateField, saving, saved, error, onSave
}: {
  form: FormState;
  updateField: <K extends keyof FormState>(k: K, v: FormState[K]) => void;
  saving: boolean;
  saved: string | null;
  error: string | null;
  onSave: (e: FormEvent<HTMLFormElement>) => void;
}) {
  return (
    <form className="space-y-8" onSubmit={onSave}>
      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}

      <Card title="Results & Quantity"
        action={
          <div className="flex items-center gap-2">
            {saved && <span className="text-xs text-emerald-600">{saved}</span>}
            <button type="submit" disabled={saving}
              className="rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-700 disabled:opacity-50 transition-colors">
              {saving ? "Saving…" : "Save all settings"}
            </button>
          </div>
        }>
        <SectionHeading title="How many products to return" description="Control the volume of recommendations per scan." />
        <div className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <div className="space-y-2">
            <Toggle enabled={form.maxCandidatesEnabled} label="Limit max candidates"
              onChange={v => updateField("maxCandidatesEnabled", v)} />
            <Field label="Max Candidates from AI" type="number" value={form.maxCandidates}
              onChange={v => updateField("maxCandidates", v)}
              disabled={!form.maxCandidatesEnabled}
              help={form.maxCandidatesEnabled ? "How many raw candidates to request from the AI provider." : "Disabled — using default (48)."} />
          </div>
          <div className="space-y-2">
            <Toggle enabled={form.topNPerCategoryEnabled} label="Limit top N per category"
              onChange={v => updateField("topNPerCategoryEnabled", v)} />
            <Field label="Top N per Category" type="number" value={form.topNPerCategory}
              onChange={v => updateField("topNPerCategory", v)}
              disabled={!form.topNPerCategoryEnabled}
              help={form.topNPerCategoryEnabled ? "Keep the best N products per category after scoring." : "Disabled — using default (3)."} />
          </div>
          <div className="space-y-2">
            <Toggle enabled={form.minScoreEnabled} label="Enforce minimum score"
              onChange={v => updateField("minScoreEnabled", v)} />
            <Field label="Minimum Score (0–100)" type="number" value={form.minScore}
              onChange={v => updateField("minScore", v)}
              disabled={!form.minScoreEnabled}
              help={form.minScoreEnabled ? "Products scoring below this are rejected." : "Disabled — all scores accepted."} />
          </div>
        </div>
      </Card>

      <Card title="Market & Shipping">
        <SectionHeading title="Target market settings" description="Define where you sell and delivery constraints." />
        <div className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <Field label="Target Market" value={form.targetMarket}
            onChange={v => updateField("targetMarket", v)}
            help="Two-letter country code: IE, US, GB, DE …" />
          <div className="space-y-2">
            <Toggle enabled={form.maxShippingDaysEnabled} label="Limit shipping days"
              onChange={v => updateField("maxShippingDaysEnabled", v)} />
            <Field label="Max Shipping Days" type="number" value={form.maxShippingDays}
              onChange={v => updateField("maxShippingDays", v)}
              disabled={!form.maxShippingDaysEnabled}
              help={form.maxShippingDaysEnabled ? "Reject products that take longer." : "Disabled — no shipping limit."} />
          </div>
        </div>
      </Card>

      <Card title="Pricing Filters">
        <SectionHeading title="Price range" description="Toggle each limit on or off. When off, the AI returns products at any price." />
        <div className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <div className="space-y-2">
            <Toggle enabled={form.minPriceEnabled} label="Enforce minimum price"
              onChange={v => updateField("minPriceEnabled", v)} />
            <Field label="Minimum Price" type="number" value={form.minPrice}
              onChange={v => updateField("minPrice", v)}
              disabled={!form.minPriceEnabled}
              help={form.minPriceEnabled ? undefined : "Disabled — no minimum."} />
          </div>
          <div className="space-y-2">
            <Toggle enabled={form.maxPriceEnabled} label="Enforce maximum price"
              onChange={v => updateField("maxPriceEnabled", v)} />
            <Field label="Maximum Price" type="number" value={form.maxPrice}
              onChange={v => updateField("maxPrice", v)}
              disabled={!form.maxPriceEnabled}
              help={form.maxPriceEnabled ? undefined : "Disabled — no maximum."} />
          </div>
        </div>
      </Card>

      <Card title="Product Categories">
        <SectionHeading title="Target categories" description="Leave empty to scan all categories. Separate with commas or new lines." />
        <label className="block">
          <textarea
            value={form.targetCategories}
            onChange={e => updateField("targetCategories", e.target.value)}
            rows={4}
            className="block w-full rounded-lg border-slate-300 text-sm shadow-sm focus:border-brand-500 focus:ring-brand-500"
            placeholder="electronics, wellness, home-decor, pet-supplies"
          />
        </label>
      </Card>
    </form>
  );
}

/* ══════════════════════════════════════════
   AI & Credentials sub-page
   ══════════════════════════════════════════ */

function AiCredentialsTab({
  form, setForm, saving, saved, error, onSave
}: {
  form: FormState;
  setForm: React.Dispatch<React.SetStateAction<FormState | null>>;
  saving: boolean;
  saved: string | null;
  error: string | null;
  onSave: (e: FormEvent<HTMLFormElement>) => void;
}) {
  const [brainKey, setBrainKey] = useState(apiKeyStorage.get());
  const [brainSaved, setBrainSaved] = useState(false);

  const updateProvider = (id: string, patch: Partial<ProviderEntry>) => {
    setForm(c => c ? {
      ...c,
      providers: c.providers.map(p => p.id === id ? { ...p, ...patch } : p),
    } : c);
  };

  const setPrimary = (id: string) => {
    setForm(c => c ? {
      ...c,
      providers: c.providers.map(p => ({ ...p, isPrimary: p.id === id })),
    } : c);
  };

  const removeProvider = (id: string) => {
    setForm(c => {
      if (!c) return c;
      const target = c.providers.find(p => p.id === id);
      if (!target) return c;

      if (target.hasSavedKey) {
        const updated = c.providers.map(p => p.id === id ? { ...p, clearKey: true, apiKey: "" } : p);
        // if we removed the primary, set another as primary
        const wasPrimary = target.isPrimary;
        const remaining = updated.filter(p => !p.clearKey);
        if (wasPrimary && remaining.length > 0) {
          remaining[0].isPrimary = true;
          return { ...c, providers: updated.map(p => p.id === remaining[0].id ? remaining[0] : (p.id === id ? { ...p, clearKey: true, apiKey: "", isPrimary: false } : p)) };
        }
        return { ...c, providers: updated };
      }

      const remaining = c.providers.filter(p => p.id !== id);
      if (target.isPrimary && remaining.length > 0) remaining[0].isPrimary = true;
      return { ...c, providers: remaining };
    });
  };

  const addProvider = () => {
    setForm(c => {
      if (!c) return c;
      const active = c.providers.filter(p => !p.clearKey);
      const has = (t: string) => active.some(p => p.provider === t);
      let providerType: "openai" | "gemini" | "groq" | "openrouter" = "openai";
      if (has("openai") && !has("gemini")) providerType = "gemini";
      else if (has("openai") && has("gemini") && !has("groq")) providerType = "groq";
      else if (has("openai") && has("gemini") && has("groq") && !has("openrouter")) providerType = "openrouter";
      else if (!has("openai")) providerType = "openai";
      const isFirst = active.length === 0;
      return {
        ...c,
        providers: [...c.providers, {
          id: uid(),
          provider: providerType,
          model: defaultModels[providerType],
          apiKey: "",
          isPrimary: isFirst,
          hasSavedKey: false,
          preview: null,
          clearKey: false,
        }],
      };
    });
  };

  const updateField = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm(c => c ? { ...c, [key]: value } : c);

  const activeProviders = form.providers.filter(p => !p.clearKey);
  const primary = activeProviders.find(p => p.isPrimary);
  const effectiveProvider = primary ? (primary.provider === "openai" ? "OpenAI" : "Gemini") : "None";

  return (
    <form className="space-y-8" onSubmit={onSave}>
      {error && <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div>}

      {/* Status badges */}
      <div className="flex flex-wrap items-center gap-3 text-sm">
        <Badge tone={activeProviders.length > 0 ? "green" : "amber"}>
          {activeProviders.length} provider{activeProviders.length !== 1 ? "s" : ""} configured
        </Badge>
        <Badge tone={effectiveProvider === "None" ? "amber" : "green"}>Primary: {effectiveProvider}</Badge>
      </div>

      {/* AI Providers */}
      <Card title="AI Providers"
        action={
          <div className="flex items-center gap-2">
            {saved && <span className="text-xs text-emerald-600">{saved}</span>}
            <button type="submit" disabled={saving}
              className="rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-700 disabled:opacity-50 transition-colors">
              {saving ? "Saving…" : "Save all settings"}
            </button>
          </div>
        }>
        <SectionHeading title="Configured providers" description="Add API keys for each AI provider you want to use. Set one as the primary provider for all scans." />

        <div className="space-y-4">
          {form.providers.map(entry => (
            <div key={entry.id}
              className={`rounded-xl border-2 p-5 transition-colors ${
                entry.clearKey ? "border-red-200 bg-red-50/50 opacity-60" :
                entry.isPrimary ? "border-brand-300 bg-brand-50/30" : "border-slate-200 bg-slate-50"
              }`}>

              {entry.clearKey ? (
                <div className="flex items-center justify-between">
                  <div className="flex items-center gap-2">
                    <Badge tone="amber">Will be removed on save</Badge>
                    <span className="text-sm text-slate-500 capitalize">{entry.provider}</span>
                  </div>
                  <button type="button"
                    onClick={() => updateProvider(entry.id, { clearKey: false })}
                    className="text-xs text-brand-600 hover:underline">Undo</button>
                </div>
              ) : (
                <>
                  <div className="flex items-center justify-between mb-4">
                    <div className="flex items-center gap-3">
                      <button type="button" onClick={() => setPrimary(entry.id)}
                        className={`w-5 h-5 rounded-full border-2 flex items-center justify-center transition-colors ${
                          entry.isPrimary ? "border-brand-600 bg-brand-600" : "border-slate-300 hover:border-brand-400"
                        }`}>
                        {entry.isPrimary && (
                          <svg className="w-3 h-3 text-white" viewBox="0 0 12 12" fill="currentColor">
                            <circle cx="6" cy="6" r="3" />
                          </svg>
                        )}
                      </button>
                      <span className="text-sm font-medium text-slate-700">
                        {entry.isPrimary ? "Primary provider" : "Click circle to set as primary"}
                      </span>
                      {entry.hasSavedKey && <Badge tone="green">Key saved</Badge>}
                    </div>
                    <button type="button" onClick={() => removeProvider(entry.id)}
                      className="rounded-lg border border-slate-300 px-3 py-1 text-xs text-slate-500 hover:bg-white hover:text-red-600 transition-colors">
                      Remove
                    </button>
                  </div>

                  <div className="grid gap-4 sm:grid-cols-3">
                    <SelectField label="AI Provider" value={entry.provider}
                      onChange={v => {
                        const pv = v as "openai" | "gemini" | "groq" | "openrouter";
                        updateProvider(entry.id, {
                          provider: pv,
                          model: defaultModels[pv],
                        });
                      }}
                      options={[
                        { value: "openai", label: "OpenAI" },
                        { value: "gemini", label: "Google Gemini" },
                        { value: "groq", label: "Groq (Free)" },
                        { value: "openrouter", label: "OpenRouter (Free)" },
                      ]} />
                    <SelectField label="Model" value={entry.model}
                      onChange={v => updateProvider(entry.id, { model: v })}
                      options={modelSuggestions[entry.provider]} />
                    <Field label="API Key" type="password" value={entry.apiKey}
                      placeholder={entry.hasSavedKey ? "Stored — enter new key to replace" : "Enter API key"}
                      onChange={v => updateProvider(entry.id, { apiKey: v })} />
                  </div>
                  {entry.hasSavedKey && entry.preview && (
                    <div className="mt-2 text-xs text-slate-500">Saved key: {entry.preview}</div>
                  )}
                </>
              )}
            </div>
          ))}

          <button type="button" onClick={addProvider}
            className="w-full rounded-xl border-2 border-dashed border-slate-300 px-4 py-3 text-sm text-slate-500 hover:border-brand-400 hover:text-brand-600 transition-colors">
            + Add AI provider
          </button>
        </div>
      </Card>

      {/* Global AI Settings */}
      <Card title="Global AI Settings">
        <SectionHeading title="Shared settings" description="These apply to whichever provider is set as primary." />
        <div className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          <SelectField label="Reasoning Effort" value={form.reasoningEffort}
            onChange={v => updateField("reasoningEffort", v)}
            options={[
              { value: "low", label: "Low — fast & cheap" },
              { value: "medium", label: "Medium — balanced" },
              { value: "high", label: "High — thorough" },
            ]}
            help="Higher effort = slower but more accurate results." />
          <Field label="Request Timeout (sec)" type="number" value={form.requestTimeoutSeconds}
            onChange={v => updateField("requestTimeoutSeconds", v)}
            help="Max seconds to wait for the AI response." />
        </div>
      </Card>

      {/* Brain Connection */}
      <Card title="Brain Service Connection">
        <SectionHeading title="API key" description="Authenticate with the Brain orchestrator service. Saved in your browser." />
        <div className="flex items-end gap-3 max-w-md">
          <Field label="Brain API Key" type="password" value={brainKey}
            placeholder="dev-master-key-change-me"
            onChange={v => setBrainKey(v)} />
          <button type="button"
            onClick={() => { apiKeyStorage.set(brainKey); setBrainSaved(true); setTimeout(() => setBrainSaved(false), 2000); }}
            className="shrink-0 rounded-lg bg-slate-800 px-4 py-2 text-sm text-white hover:bg-slate-700 transition-colors">
            {brainSaved ? "✓ Saved" : "Save"}
          </button>
        </div>
      </Card>
    </form>
  );
}

/* ══════════════════════════════════════════
   Main ConfigurationView
   ══════════════════════════════════════════ */

export function ConfigurationView({ activeTab }: { activeTab: "selection" | "ai" }) {
  const [form, setForm] = useState<FormState | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const applyResponse = (r: RecommendationSettingsResponse) => {
    setForm(toFormState(r));
  };

  useEffect(() => {
    setLoading(true);
    setError(null);
    api.recommendationSettings().then(applyResponse).catch(e => setError(String(e))).finally(() => setLoading(false));
  }, []);

  const updateField = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm(c => c ? { ...c, [key]: value } : c);

  const onSave = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!form) return;
    setSaving(true); setError(null); setSaved(null);
    try {
      const r = await api.updateRecommendationSettings(toPayload(form));
      applyResponse(r);
      setSaved(`Saved ${new Date().toLocaleTimeString()}`);
    } catch (err) {
      setError(String(err));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <Card title="Configuration"><div className="text-sm text-slate-400">Loading settings…</div></Card>;
  if (!form) return <Card title="Configuration"><div className="text-sm text-red-600">{error ?? "Failed to load settings."}</div></Card>;

  return activeTab === "ai" ? (
    <AiCredentialsTab
      form={form} setForm={setForm}
      saving={saving} saved={saved} error={error} onSave={onSave}
    />
  ) : (
    <SelectionRulesTab
      form={form} updateField={updateField}
      saving={saving} saved={saved} error={error} onSave={onSave}
    />
  );
}
