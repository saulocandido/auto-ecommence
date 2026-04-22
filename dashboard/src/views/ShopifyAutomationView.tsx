import { useEffect, useState, useRef } from "react";
import {
  api,
  type AutomationConfigDto,
  type AutomationConfigUpdateDto,
  type AutomationRunDto,
  type AutomationProductDto,
  type AutomationLogDto,
  type AutomationMetricsDto,
  type AutomationSessionStatusDto,
} from "../api";
import { Badge, Card } from "../components/Card";

export function ShopifyAutomationView() {
  const [cfg, setCfg] = useState<AutomationConfigDto | null>(null);
  const [cfgDraft, setCfgDraft] = useState<AutomationConfigUpdateDto>({});
  const [run, setRun] = useState<AutomationRunDto | null>(null);
  const [products, setProducts] = useState<AutomationProductDto[]>([]);
  const [logs, setLogs] = useState<AutomationLogDto[]>([]);
  const [metrics, setMetrics] = useState<AutomationMetricsDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [tab, setTab] = useState<"config" | "run">("config");
  const [session, setSession] = useState<AutomationSessionStatusDto | null>(null);
  const [sessionBusy, setSessionBusy] = useState<null | "validate" | "connect" | "upload">(null);
  const [showUploadModal, setShowUploadModal] = useState(false);
  const [showLoginWizard, setShowLoginWizard] = useState(false);
  const [loginStep, setLoginStep] = useState<1 | 2 | 3>(1);
  const [uploadStateJson, setUploadStateJson] = useState("");
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Load config + active run + session status on mount
  useEffect(() => {
    (async () => {
      try {
        const [config, active, sess] = await Promise.all([
          api.automationConfigGet(),
          api.automationActiveRun(),
          api.automationSessionGet().catch(() => null)
        ]);
        setCfg(config);
        setSession(sess);
        if (active) {
          setRun(active);
          setTab("run");
        }
      } catch (e) {
        setError(`Load failed: ${e instanceof Error ? e.message : String(e)}`);
      }
    })();
  }, []);

  // Poll when run is active
  useEffect(() => {
    if (run && (run.status === "Running" || run.status === "Pending")) {
      pollRef.current = setInterval(async () => {
        try {
          const [updatedRun, prods, lg, met] = await Promise.all([
            api.automationRun(run.id),
            api.automationProducts(run.id),
            api.automationLogs(run.id, 200),
            api.automationMetrics(run.id)
          ]);
          setRun(updatedRun);
          setProducts(prods);
          setLogs(lg);
          setMetrics(met);
          if (updatedRun.status !== "Running" && updatedRun.status !== "Pending") {
            if (pollRef.current) clearInterval(pollRef.current);
          }
        } catch { /* ignore poll errors */ }
      }, 2000);
      return () => { if (pollRef.current) clearInterval(pollRef.current); };
    }
  }, [run?.id, run?.status]);

  const saveConfig = async () => {
    setError(null); setMessage(null);
    try {
      const saved = await api.automationConfigPut(cfgDraft);
      setCfg(saved);
      setCfgDraft({});
      setMessage("Configuration saved");
      setTimeout(() => setMessage(null), 3000);
    } catch (e) {
      setError(`Save failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const startRun = async () => {
    setLoading(true); setError(null); setMessage(null);
    try {
      const newRun = await api.automationStart();
      setRun(newRun);
      setTab("run");
      setMessage("Automation started!");
    } catch (e) {
      setError(`Start failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally {
      setLoading(false);
    }
  };

  const stopRun = async () => {
    if (!run) return;
    try {
      const stopped = await api.automationStop(run.id);
      setRun(stopped);
    } catch (e) {
      setError(`Stop failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const retryFailed = async () => {
    if (!run) return;
    setError(null);
    try {
      const retried = await api.automationRetry(run.id);
      setRun(retried);
      setMessage("Retrying failed products...");
    } catch (e) {
      setError(`Retry failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const resumeRun = async () => {
    if (!run) return;
    setError(null);
    try {
      const resumed = await api.automationResume(run.id);
      setRun(resumed);
      setMessage("Run resumed after login");
    } catch (e) {
      setError(`Resume failed: ${e instanceof Error ? e.message : String(e)}`);
    }
  };

  const validateSession = async () => {
    setSessionBusy("validate"); setError(null); setMessage(null);
    try {
      const s = await api.automationSessionValidate();
      setSession(s);
      setMessage(`Session: ${s.state}${s.message ? " — " + s.message : ""}`);
    } catch (e) {
      setError(`Validate failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally { setSessionBusy(null); }
  };

  const uploadSession = async () => {
    setSessionBusy("upload"); setError(null); setMessage(null);
    try {
      // Clean the pasted text: strip label prefixes, surrounding quotes, etc.
      let raw = uploadStateJson.trim();
      // Strip common prefixes like "EXTRACTED_VALUES_JSON:" or "STRING_JSON:"
      raw = raw.replace(/^[A-Z_]+:\s*/i, "");
      // Strip surrounding single quotes
      if (raw.startsWith("'") && raw.endsWith("'")) raw = raw.slice(1, -1);

      // Try to auto-fix common JSON issues before parsing
      // Fix missing closing braces/brackets
      const openBraces = (raw.match(/{/g) || []).length;
      const closeBraces = (raw.match(/}/g) || []).length;
      if (openBraces > closeBraces) {
        raw = raw + "}".repeat(openBraces - closeBraces);
      }
      const openBrackets = (raw.match(/\[/g) || []).length;
      const closeBrackets = (raw.match(/]/g) || []).length;
      if (openBrackets > closeBrackets) {
        raw = raw + "]".repeat(openBrackets - closeBrackets);
      }

      // Try to parse and detect what kind of payload this is
      try {
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {

          // ── Case 1: Full storage-state with cookies + origins/sessionStorage ──
          // Contains both session cookies AND config embedded in localStorage/sessionStorage.
          // Extract config automatically, then upload the cookies as session.
          if (parsed.cookies && (parsed.origins || parsed.sessionStorageByOrigin)) {
            const configUpdate: Record<string, string> = {};

            // Extract config from sessionStorage app-bridge-config
            const ssEntries: {name: string; value: string}[] =
              Object.values(parsed.sessionStorageByOrigin || {}).flat() as {name: string; value: string}[];
            const appBridge = ssEntries.find((e: {name: string}) => e.name === "app-bridge-config");
            if (appBridge) {
              try {
                const abc = JSON.parse(appBridge.value);
                if (abc.apiKey) configUpdate.shopifyApiKey = abc.apiKey;
                if (abc.host) configUpdate.shopifyHost = abc.host;
                if (abc.shop) configUpdate.shopifyStoreUrl = `https://admin.shopify.com/store/${abc.shop.replace(".myshopify.com", "")}`;
              } catch { /* ignore */ }
            }

            // Extract default search from localStorage searchParam
            const lsEntries: {name: string; value: string}[] =
              (parsed.origins || []).flatMap((o: {localStorage?: {name: string; value: string}[]}) => o.localStorage || []);
            const searchParam = lsEntries.find((e: {name: string}) => e.name === "searchParam");
            if (searchParam) {
              try {
                const sp = JSON.parse(searchParam.value);
                if (sp.keywords) configUpdate.defaultSearch = sp.keywords;
              } catch { /* ignore */ }
            }

            // Extract app URL from origins
            const origins = (parsed.origins || []).map((o: {origin?: string}) => o.origin).filter(Boolean);
            const appOrigin = origins.find((u: string) => !u.includes("shopify"));
            if (appOrigin) configUpdate.appUrl = appOrigin;

            // Save config if we found anything
            if (Object.keys(configUpdate).length > 0) {
              await api.automationConfigPut(configUpdate);
              const newCfg = await api.automationConfigGet();
              setCfg(newCfg);
            }

            // Now upload the full payload as session data (cookies)
            const s = await api.automationSessionUpload(raw);
            setSession(s);
            setShowUploadModal(false);
            setShowLoginWizard(false);
            setUploadStateJson("");
            const configMsg = Object.keys(configUpdate).length > 0
              ? `\n✅ Also extracted config: ${Object.keys(configUpdate).join(", ")}`
              : "";
            if (s.state === "error") setError(s.message ?? "Upload failed");
            else setMessage(`✅ Session uploaded with ${parsed.cookies.length} cookies.${configMsg}`);
            setSessionBusy(null);
            return;
          }

          // ── Case 2: Extracted config JSON (shopifyStore/shopifyAdmin keys) ──
          if (parsed.shopifyStore || parsed.shopifyAdmin || parsed.dropshippingApp || parsed.shopifyApiKey) {
            const configUpdate: Record<string, string> = {};
            if (parsed.shopifyStore) configUpdate.shopifyStoreUrl = parsed.shopifyAdmin || `https://${parsed.shopifyStore}`;
            if (parsed.shopifyAdmin) configUpdate.shopifyStoreUrl = parsed.shopifyAdmin;
            if (parsed.dropshippingApp) configUpdate.appUrl = parsed.dropshippingApp;
            if (parsed.shopifyApiKey) configUpdate.shopifyApiKey = parsed.shopifyApiKey;
            if (parsed.shopifyHost) configUpdate.shopifyHost = parsed.shopifyHost;
            if (parsed.lastSearch) configUpdate.defaultSearch = parsed.lastSearch;
            else if (parsed.rawSearchParam?.keywords) configUpdate.defaultSearch = parsed.rawSearchParam.keywords;

            await api.automationConfigPut(configUpdate);
            const newCfg = await api.automationConfigGet();
            setCfg(newCfg);

            // If the payload also contains cookies, upload them as session too
            if (parsed.cookies && Array.isArray(parsed.cookies) && parsed.cookies.length > 0) {
              const sessionPayload = JSON.stringify({ cookies: parsed.cookies, origins: parsed.origins || [] });
              const s = await api.automationSessionUpload(sessionPayload);
              setSession(s);
              setShowUploadModal(false);
              setShowLoginWizard(false);
              setUploadStateJson("");
              const configMsg = Object.keys(configUpdate).length > 0
                ? ` + config: ${Object.keys(configUpdate).join(", ")}`
                : "";
              if (s.state === "error") setError(s.message ?? "Upload failed");
              else setMessage(`✅ Session uploaded with ${parsed.cookies.length} cookies${configMsg}`);
              setSessionBusy(null);
              return;
            }

            setShowUploadModal(false);
            setShowLoginWizard(false);
            setUploadStateJson("");
            const fields = Object.keys(configUpdate);
            setMessage(`✅ Store config saved successfully (${fields.join(", ")}). To enable browser automation, use Cookie-Editor to export and paste your Shopify session cookies.`);
            setSessionBusy(null);
            return;
          }
        }
      } catch { /* not valid JSON yet — the backend will handle further cleanup */ }

      const s = await api.automationSessionUpload(raw);
      setSession(s);
      setShowUploadModal(false);
      setUploadStateJson("");
      if (s.state === "error") setError(s.message ?? "Upload failed");
      else setMessage("Session uploaded — click 'Test Session' to validate");
    } catch (e) {
      setError(`Upload failed: ${e instanceof Error ? e.message : String(e)}`);
    } finally { setSessionBusy(null); }
  };

  const sessionTone = (state?: string): "green" | "amber" | "red" | "slate" => {
    switch (state) {
      case "connected": return "green";
      case "login_required": return "amber";
      case "error": return "red";
      default: return "slate";
    }
  };

  const isRunning = run?.status === "Running" || run?.status === "Pending";

  const statusColor = (s: string): "green" | "amber" | "red" | "slate" => {
    switch (s) {
      case "Pushed": return "green";
      case "Imported": return "green";
      case "Processing": return "amber";
      case "Failed": return "red";
      case "ManualReview": return "amber";
      default: return "slate";
    }
  };

  const logColor = (level: string) => {
    switch (level) {
      case "error": return "text-red-600";
      case "warn": return "text-amber-600";
      default: return "text-slate-600";
    }
  };

  return (
    <div className="space-y-6">
      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-lg text-sm">
          {error}
          <button onClick={() => setError(null)} className="ml-3 underline text-xs">dismiss</button>
        </div>
      )}
      {message && (
        <div className="bg-green-50 border border-green-200 text-green-700 px-4 py-3 rounded-lg text-sm">
          {message}
        </div>
      )}

      {/* ── Shopify Session panel ── */}
      <Card title="Shopify Session">
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span className="text-slate-500">Status:</span>
          <Badge tone={sessionTone(session?.state)}>
            {session?.state ?? "unknown"}
          </Badge>
          {session?.lastValidatedAt && (
            <span className="text-xs text-slate-500">
              validated {new Date(session.lastValidatedAt).toLocaleString()}
            </span>
          )}
          {session?.lastLoggedInAt && (
            <span className="text-xs text-slate-500">
              · login {new Date(session.lastLoggedInAt).toLocaleString()}
            </span>
          )}
          {session?.message && (
            <span className="text-xs text-slate-400 italic ml-2">"{session.message}"</span>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-2 mt-3">
          <button onClick={() => { setShowLoginWizard(true); setLoginStep(1); }}
            disabled={sessionBusy !== null}
            className="px-3 py-1.5 bg-indigo-600 text-white text-xs font-medium rounded hover:bg-indigo-700 disabled:opacity-50">
            🔑 Login to Shopify
          </button>
          <button onClick={validateSession} disabled={sessionBusy !== null}
            className="px-3 py-1.5 bg-emerald-600 text-white text-xs font-medium rounded hover:bg-emerald-700 disabled:opacity-50">
            {sessionBusy === "validate" ? "Checking…" : "✓ Test Session"}
          </button>
          <button onClick={() => setShowUploadModal(true)} disabled={sessionBusy !== null}
            className="px-3 py-1.5 bg-white border border-slate-300 text-slate-700 text-xs font-medium rounded hover:bg-slate-50 disabled:opacity-50">
            📋 Paste Cookies Directly
          </button>
        </div>
        <p className="text-xs text-slate-400 mt-2 leading-relaxed">
          Click <strong>Login to Shopify</strong> — it opens Shopify in your browser, you log in,
          then copy cookies back so the automation can reuse your session.
        </p>
      </Card>

      {/* ── Login Wizard modal ── */}
      {showLoginWizard && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50"
             onClick={() => !sessionBusy && setShowLoginWizard(false)}>
          <div className="bg-white rounded-xl shadow-2xl p-6 w-[680px] max-w-[95vw] max-h-[90vh] overflow-y-auto"
               onClick={e => e.stopPropagation()}>
            <h3 className="text-lg font-bold mb-1">Login to Shopify</h3>
            <p className="text-xs text-slate-500 mb-4">Follow these 3 steps to connect your Shopify session</p>

            {/* Step indicators */}
            <div className="flex items-center gap-2 mb-5">
              {[1, 2, 3].map(s => (
                <div key={s} className="flex items-center gap-2">
                  <div className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-bold ${
                    loginStep === s ? "bg-indigo-600 text-white" :
                    loginStep > s ? "bg-green-500 text-white" : "bg-slate-200 text-slate-500"
                  }`}>{loginStep > s ? "✓" : s}</div>
                  <span className={`text-xs font-medium ${
                    loginStep === s ? "text-indigo-600" : "text-slate-400"
                  }`}>
                    {s === 1 ? "Open Shopify" : s === 2 ? "Copy Cookies" : "Paste & Save"}
                  </span>
                  {s < 3 && <div className="w-8 h-px bg-slate-300" />}
                </div>
              ))}
            </div>

            {/* Step 1: Open Shopify */}
            {loginStep === 1 && (
              <div className="space-y-3">
                <div className="bg-indigo-50 border border-indigo-200 rounded-lg p-4">
                  <p className="text-sm font-medium text-indigo-800 mb-2">Step 1: Open Shopify & Login</p>
                  <p className="text-xs text-indigo-700 mb-3">
                    Click the button below to open your Shopify admin in a new tab.
                    Log in with your Shopify credentials. Once you're logged in and can see the admin dashboard, come back here.
                  </p>
                  <button
                    onClick={() => {
                      const url = cfg?.appUrl || cfg?.shopifyStoreUrl || cfg?.findProductsUrl || "https://accounts.shopify.com/lookup";
                      window.open(url, "_blank", "noopener");
                    }}
                    className="px-4 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700">
                    🌐 Open Shopify in New Tab
                  </button>
                </div>
                <div className="flex justify-end">
                  <button onClick={() => setLoginStep(2)}
                    className="px-4 py-2 bg-slate-800 text-white text-sm font-medium rounded-lg hover:bg-slate-900">
                    I'm logged in → Next
                  </button>
                </div>
              </div>
            )}

            {/* Step 2: Copy Cookies */}
            {loginStep === 2 && (
              <div className="space-y-3">
                <div className="bg-amber-50 border border-amber-200 rounded-lg p-4">
                  <p className="text-sm font-medium text-amber-800 mb-2">Step 2: Copy Your Cookies</p>
                  <p className="text-xs text-amber-700 mb-3">
                    Now that you're logged in, you need to copy the cookies from your Shopify session.
                    Use <strong>one of these methods</strong>:
                  </p>

                  <div className="space-y-3">
                    <div className="bg-red-50 border border-red-200 rounded-lg p-3 mb-1">
                      <p className="text-xs font-bold text-red-700">⚠️ Important: <code>document.cookie</code> cannot see HttpOnly auth cookies.</p>
                      <p className="text-[11px] text-red-600">Use Method A (Cookie-Editor extension) or Method B (DevTools Application tab) to get ALL cookies including the auth ones.</p>
                    </div>

                    {/* Method A: Extension (BEST) */}
                    <div className="bg-white rounded border-2 border-green-400 p-3">
                      <p className="text-xs font-bold text-slate-700 mb-1">✅ Method A — Cookie-Editor Extension (recommended)</p>
                      <ol className="text-xs text-slate-600 space-y-1 list-decimal list-inside mb-2">
                        <li>Install <a href="https://chromewebstore.google.com/detail/cookie-editor/hlkenndednhfkekhgcdicdfddnkalmdm" target="_blank" rel="noopener" className="text-indigo-600 underline font-semibold">Cookie-Editor</a> from the Chrome Web Store</li>
                        <li>Go to your Shopify / dropshipping app tab (make sure you're logged in)</li>
                        <li>Click the Cookie-Editor icon in the toolbar</li>
                        <li>Click <strong>Export → JSON</strong> (copies to clipboard)</li>
                        <li>Come back here and paste in Step 3</li>
                      </ol>
                      <p className="text-[10px] text-green-700 font-medium">This captures ALL cookies including HttpOnly auth cookies ✓</p>
                    </div>

                    {/* Method B: DevTools Application tab */}
                    <div className="bg-white rounded border border-amber-200 p-3">
                      <p className="text-xs font-bold text-slate-700 mb-1">Method B — DevTools Application Tab</p>
                      <ol className="text-xs text-slate-600 space-y-1 list-decimal list-inside">
                        <li>On the Shopify tab, press <kbd className="bg-slate-100 px-1 rounded font-mono">F12</kbd> to open DevTools</li>
                        <li>Go to <strong>Application</strong> → <strong>Cookies</strong> in the left panel</li>
                        <li>Go to <strong>Console</strong> tab and paste this to extract all visible cookies:</li>
                      </ol>
                      <div className="mt-1 relative">
                        <code className="block bg-slate-900 text-green-400 p-2 rounded text-[11px] font-mono break-all">
                          {`(async()=>{const cs=await cookieStore.getAll();console.log(JSON.stringify({cookies:cs.map(c=>({name:c.name,value:c.value,domain:c.domain||location.hostname,path:c.path||'/',secure:c.secure,httpOnly:false})),origins:[]},null,2))})()`}
                        </code>
                        <button onClick={() => {
                          navigator.clipboard.writeText(`(async()=>{const cs=await cookieStore.getAll();console.log(JSON.stringify({cookies:cs.map(c=>({name:c.name,value:c.value,domain:c.domain||location.hostname,path:c.path||'/',secure:c.secure,httpOnly:false})),origins:[]},null,2))})()`);
                          setMessage("Console command copied!");
                          setTimeout(() => setMessage(null), 2000);
                        }}
                          className="absolute top-1 right-1 px-2 py-0.5 bg-slate-700 text-white text-[10px] rounded hover:bg-slate-600">
                          Copy
                        </button>
                      </div>
                      <p className="text-[10px] text-slate-400 mt-1">⚠️ cookieStore API may still miss some HttpOnly cookies — Method A is more reliable</p>
                    </div>

                    {/* Method C: Bookmarklet (limited) */}
                    <div className="bg-white rounded border border-slate-200 p-3 opacity-70">
                      <p className="text-xs font-bold text-slate-500 mb-1">Method C — Console document.cookie (limited)</p>
                      <p className="text-xs text-slate-500">
                        <code>document.cookie</code> only sees non-HttpOnly cookies — usually just analytics trackers.
                        Only use this if Methods A/B aren't available.
                      </p>
                    </div>
                  </div>
                </div>
                <div className="flex justify-between">
                  <button onClick={() => setLoginStep(1)} className="px-3 py-1.5 text-sm text-slate-600 hover:text-slate-800">← Back</button>
                  <button onClick={() => setLoginStep(3)}
                    className="px-4 py-2 bg-slate-800 text-white text-sm font-medium rounded-lg hover:bg-slate-900">
                    I've copied cookies → Next
                  </button>
                </div>
              </div>
            )}

            {/* Step 3: Paste & Save */}
            {loginStep === 3 && (
              <div className="space-y-3">
                <div className="bg-green-50 border border-green-200 rounded-lg p-4">
                  <p className="text-sm font-medium text-green-800 mb-2">Step 3: Paste Cookies & Save</p>
                  <p className="text-xs text-green-700 mb-3">
                    Paste the JSON you copied from the previous step. This can be a Playwright storage-state,
                    a cookies array, or any JSON with cookie data.
                  </p>
                  <textarea value={uploadStateJson} onChange={e => setUploadStateJson(e.target.value)}
                    rows={10}
                    placeholder='{"cookies": [{"name": "_shopify_s", "value": "...", ...}], "origins": []}'
                    className="w-full border border-green-300 rounded p-2 font-mono text-xs bg-white" />
                </div>
                <div className="flex justify-between items-center">
                  <button onClick={() => setLoginStep(2)} className="px-3 py-1.5 text-sm text-slate-600 hover:text-slate-800">← Back</button>
                  <div className="flex gap-2">
                    <button onClick={() => { setShowLoginWizard(false); setUploadStateJson(""); }}
                      disabled={sessionBusy !== null}
                      className="px-3 py-1.5 text-sm rounded border border-slate-300 hover:bg-slate-50 disabled:opacity-50">
                      Cancel
                    </button>
                    <button onClick={async () => {
                      await uploadSession();
                      if (!error) {
                        setShowLoginWizard(false);
                        setLoginStep(1);
                      }
                    }} disabled={sessionBusy !== null || !uploadStateJson.trim()}
                      className="px-4 py-2 bg-green-600 text-white text-sm font-medium rounded-lg hover:bg-green-700 disabled:opacity-50">
                      {sessionBusy === "upload" ? "Saving…" : "✅ Save & Connect"}
                    </button>
                  </div>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* ── Direct Upload modal (advanced) ── */}
      {showUploadModal && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50"
             onClick={() => !sessionBusy && setShowUploadModal(false)}>
          <div className="bg-white rounded-lg shadow-xl p-5 w-[600px] max-w-[90vw]"
               onClick={e => e.stopPropagation()}>
            <h3 className="text-lg font-semibold mb-2">Paste Cookies / Session JSON</h3>
            <p className="text-xs text-slate-500 mb-3">
              Paste a Playwright storage-state JSON or a cookies array.
              Need help? Use the <button onClick={() => { setShowUploadModal(false); setShowLoginWizard(true); setLoginStep(1); }} className="text-indigo-600 underline">Login Wizard</button> instead.
            </p>
            <textarea value={uploadStateJson} onChange={e => setUploadStateJson(e.target.value)}
              rows={12}
              placeholder='{"cookies": [...], "origins": [...]}'
              className="w-full border border-slate-300 rounded p-2 font-mono text-xs" />
            <div className="flex justify-end gap-2 mt-3">
              <button onClick={() => setShowUploadModal(false)} disabled={sessionBusy !== null}
                className="px-3 py-1.5 text-sm rounded border border-slate-300 hover:bg-slate-50 disabled:opacity-50">
                Cancel
              </button>
              <button onClick={uploadSession} disabled={sessionBusy !== null || !uploadStateJson.trim()}
                className="px-3 py-1.5 text-sm bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50">
                {sessionBusy === "upload" ? "Saving…" : "Save Session"}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Tab bar */}
      <div className="flex gap-2 border-b border-slate-200 pb-1">
        <button onClick={() => setTab("config")}
          className={`px-4 py-2 text-sm font-medium rounded-t-lg ${tab === "config" ? "bg-white border border-b-white border-slate-200 -mb-px" : "text-slate-500 hover:text-slate-700"}`}>
          Configuration
        </button>
        <button onClick={() => setTab("run")}
          className={`px-4 py-2 text-sm font-medium rounded-t-lg ${tab === "run" ? "bg-white border border-b-white border-slate-200 -mb-px" : "text-slate-500 hover:text-slate-700"}`}>
          Automation Run
        </button>
      </div>

      {/* ── Config Tab ── */}
      {tab === "config" && cfg && (
        <Card title="Shopify Automation Configuration">
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Shopify Store URL</label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder="https://admin.shopify.com/store/your-store"
                defaultValue={cfg.shopifyStoreUrl}
                onChange={e => setCfgDraft(d => ({ ...d, shopifyStoreUrl: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">
                Find Products URL <span className="text-red-500">*</span>
              </label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder="https://admin.shopify.com/store/.../apps/dropshipper-ai/app/find-products"
                defaultValue={cfg.findProductsUrl}
                onChange={e => setCfgDraft(d => ({ ...d, findProductsUrl: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">
                Import List URL <span className="text-red-500">*</span>
              </label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder="https://admin.shopify.com/store/.../apps/dropshipper-ai/app/import-list"
                defaultValue={cfg.importListUrl}
                onChange={e => setCfgDraft(d => ({ ...d, importListUrl: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Dropshipping App URL</label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder="https://app.dropshiping.ai"
                defaultValue={cfg.appUrl}
                onChange={e => setCfgDraft(d => ({ ...d, appUrl: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Shopify API Key</label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm font-mono"
                placeholder="36a86a25ff0c6d4958653adb9ba54e11"
                defaultValue={cfg.shopifyApiKey}
                onChange={e => setCfgDraft(d => ({ ...d, shopifyApiKey: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Shopify Host (embedded context)</label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm font-mono"
                placeholder="YWRtaW4uc2hvcGlmeS5jb20vc3RvcmUv..."
                defaultValue={cfg.shopifyHost}
                onChange={e => setCfgDraft(d => ({ ...d, shopifyHost: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Default Search Query</label>
              <input className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder="Dog Grooming Glove"
                defaultValue={cfg.defaultSearch}
                onChange={e => setCfgDraft(d => ({ ...d, defaultSearch: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Auth Mode</label>
              <select className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                defaultValue={cfg.authMode}
                onChange={e => setCfgDraft(d => ({ ...d, authMode: e.target.value }))}>
                <option value="session">Session</option>
                <option value="token">Token</option>
                <option value="cookie">Cookie</option>
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Max Retries</label>
              <input type="number" className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                defaultValue={cfg.maxRetries}
                onChange={e => setCfgDraft(d => ({ ...d, maxRetries: Number(e.target.value) }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Match Confidence Threshold</label>
              <input type="number" step="0.05" min="0" max="1"
                className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                defaultValue={cfg.matchConfidenceThreshold}
                onChange={e => setCfgDraft(d => ({ ...d, matchConfidenceThreshold: Number(e.target.value) }))} />
            </div>
            <div className="flex items-center gap-4 pt-5">
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" defaultChecked={cfg.headlessMode}
                  onChange={e => setCfgDraft(d => ({ ...d, headlessMode: e.target.checked }))} />
                Headless Mode
              </label>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" defaultChecked={cfg.useApiFirst}
                  onChange={e => setCfgDraft(d => ({ ...d, useApiFirst: e.target.checked }))} />
                API-First
              </label>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Session Cookie</label>
              <input type="password" className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder={cfg.sessionCookie ? "••••••••" : "Not set"}
                onChange={e => setCfgDraft(d => ({ ...d, sessionCookie: e.target.value }))} />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-600 mb-1">Auth Token</label>
              <input type="password" className="w-full border border-slate-300 rounded px-3 py-2 text-sm"
                placeholder={cfg.authToken ? "••••••••" : "Not set"}
                onChange={e => setCfgDraft(d => ({ ...d, authToken: e.target.value }))} />
            </div>
          </div>
          <div className="flex items-center gap-3 mt-6">
            <button onClick={saveConfig}
              className="px-4 py-2 bg-indigo-600 text-white text-sm font-medium rounded-lg hover:bg-indigo-700">
              Save Configuration
            </button>
            <button onClick={startRun} disabled={loading || isRunning || !cfg.findProductsUrl || !cfg.importListUrl}
              className="px-4 py-2 bg-green-600 text-white text-sm font-medium rounded-lg hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed">
              {loading ? "Starting…" : "▶ Run Automation"}
            </button>
          </div>
        </Card>
      )}

      {/* ── Run Tab ── */}
      {tab === "run" && (
        <>
          {/* Controls */}
          <div className="flex items-center gap-3">
            <button onClick={startRun} disabled={loading || isRunning}
              className="px-4 py-2 bg-green-600 text-white text-sm font-medium rounded-lg hover:bg-green-700 disabled:opacity-50">
              ▶ Run Automation
            </button>
            <button onClick={stopRun} disabled={!isRunning}
              className="px-4 py-2 bg-red-600 text-white text-sm font-medium rounded-lg hover:bg-red-700 disabled:opacity-50">
              ⬛ Stop
            </button>
            <button onClick={retryFailed} disabled={isRunning || !run || run.failedCount === 0}
              className="px-4 py-2 bg-amber-600 text-white text-sm font-medium rounded-lg hover:bg-amber-700 disabled:opacity-50">
              🔄 Retry Failed
            </button>
            <button onClick={resumeRun} disabled={!run || run.status !== "LoginRequired"}
              className="px-4 py-2 bg-blue-600 text-white text-sm font-medium rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed">
              ▶ Resume Run
            </button>
            {run && (
              <span className="ml-auto text-sm text-slate-500">
                Run: <span className="font-mono">{run.id.slice(0, 8)}</span>
                {" · "}
                <Badge tone={
                  run.status === "Completed" ? "green" :
                  run.status === "Failed" ? "red" :
                  run.status === "LoginRequired" ? "amber" : "slate"}>
                  {run.status}
                </Badge>
              </span>
            )}
          </div>

          {/* Metrics */}
          {(metrics || run) && (
            <div className="grid grid-cols-2 md:grid-cols-6 gap-3">
              {[
                { label: "Total", value: metrics?.total ?? run?.totalProducts ?? 0, color: "bg-slate-100" },
                { label: "Processing", value: metrics?.processing ?? 0, color: "bg-blue-50" },
                { label: "Imported", value: metrics?.imported ?? run?.importedCount ?? 0, color: "bg-cyan-50" },
                { label: "Pushed", value: metrics?.pushed ?? run?.pushedCount ?? 0, color: "bg-green-50" },
                { label: "Failed", value: metrics?.failed ?? run?.failedCount ?? 0, color: "bg-red-50" },
                { label: "Manual Review", value: metrics?.manualReview ?? 0, color: "bg-amber-50" },
              ].map(m => (
                <div key={m.label} className={`${m.color} rounded-lg p-3 text-center`}>
                  <div className="text-2xl font-bold">{m.value}</div>
                  <div className="text-xs text-slate-500">{m.label}</div>
                </div>
              ))}
            </div>
          )}

          {/* Products table */}
          {products.length > 0 && (
            <Card title="Products">
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-slate-200 text-left text-xs text-slate-500 uppercase">
                      <th className="pb-2 pr-3">Product</th>
                      <th className="pb-2 pr-3">Supplier</th>
                      <th className="pb-2 pr-3">Status</th>
                      <th className="pb-2 pr-3">Step</th>
                      <th className="pb-2 pr-3">Match</th>
                      <th className="pb-2 pr-3">Confidence</th>
                      <th className="pb-2">Error</th>
                    </tr>
                  </thead>
                  <tbody>
                    {products.map(p => (
                      <tr key={p.id} className="border-b border-slate-100 hover:bg-slate-50">
                        <td className="py-2 pr-3 font-medium max-w-[200px] truncate">{p.productName}</td>
                        <td className="py-2 pr-3 text-slate-500">{p.supplierKey ?? "—"}</td>
                        <td className="py-2 pr-3">
                          <Badge tone={statusColor(p.status) as "green" | "amber" | "red" | "slate"}>{p.status}</Badge>
                        </td>
                        <td className="py-2 pr-3 text-xs text-slate-500">{p.currentStep}</td>
                        <td className="py-2 pr-3 text-xs max-w-[150px] truncate">{p.matchedResultTitle ?? "—"}</td>
                        <td className="py-2 pr-3">
                          {p.confidence > 0
                            ? <span className={`text-xs font-medium ${p.confidence >= 0.7 ? "text-green-600" : p.confidence >= 0.4 ? "text-amber-600" : "text-red-600"}`}>
                                {(p.confidence * 100).toFixed(0)}%
                              </span>
                            : "—"}
                        </td>
                        <td className="py-2 text-xs text-red-500 max-w-[200px] truncate">{p.errorReason ?? ""}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </Card>
          )}

          {/* Logs panel */}
          {logs.length > 0 && (
            <Card title="Automation Logs">
              <div className="max-h-80 overflow-y-auto space-y-1 font-mono text-xs">
                {logs.map(l => (
                  <div key={l.id} className={`flex gap-2 ${logColor(l.level)}`}>
                    <span className="text-slate-400 shrink-0">
                      {new Date(l.timestamp).toLocaleTimeString()}
                    </span>
                    <span className={`shrink-0 uppercase font-bold w-10 ${logColor(l.level)}`}>
                      {l.level}
                    </span>
                    <span>{l.message}</span>
                  </div>
                ))}
              </div>
            </Card>
          )}

          {!run && (
            <div className="text-center py-12 text-slate-400">
              No automation run yet. Configure YL and click "Run Automation" to start.
            </div>
          )}
        </>
      )}
    </div>
  );
}
