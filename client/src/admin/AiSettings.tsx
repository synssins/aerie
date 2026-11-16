import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "../api";
import { useDialogs } from "../Dialogs";
import { aiChanged } from "../aiFeatures";

export type AiAccess = "Off" | "Admins" | "Everyone";
export type AiSettings = {
  enabled: boolean; baseUrl: string; model: string; timeoutSeconds: number; maxTokens: number; temperature: number; reasoning: boolean;
  ruleset: string; maxInputChars: number; userPerMinute: number; tokenPerMinute: number; importConcurrency?: number; features: Record<string, AiAccess>;
};
type Dto = { settings: AiSettings; apiKeySet: boolean; rulesetIsDefault: boolean; defaultRuleset: string };
type TestResult = { ok: boolean; model: string | null; milliseconds: number; reasoningEmpty: boolean; error: string | null };

const FEATURES: [string, string][] = [["import", "Import"], ["format", "Formatting"], ["assist", "Editor assist"], ["search", "In-depth search"]];
const ACCESS: [AiAccess, string][] = [["Off", "Off"], ["Admins", "Admins"], ["Everyone", "Everyone with access"]];

function Switch({ on, label, onChange }: { on: boolean; label: string; onChange: (v: boolean) => void }) {
  return <button type="button" role="switch" aria-checked={on} aria-label={label} className={`sw ${on ? "on" : ""}`} onClick={() => onChange(!on)} />;
}

/** Administration › AI: the model server, its limits, the ruleset the model is held to, and who may use each feature. */
export function AiPanel() {
  const dialogs = useDialogs();
  const [dto, setDto] = useState<Dto | null>(null);
  const [form, setForm] = useState<AiSettings | null>(null);
  const [key, setKey] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [test, setTest] = useState<TestResult | null>(null);
  const [busy, setBusy] = useState(false);
  // The models the server at the address lists (GET {address}/v1/models, fetched by the server): null until asked.
  const [models, setModels] = useState<{ ids: string[] | null; error: string | null; loading: boolean; url: string }>({ ids: null, error: null, loading: false, url: "" });
  async function loadModels(url: string, apiKey: string | null) {
    if (!url.trim()) { setModels({ ids: null, error: null, loading: false, url: "" }); return; }
    setModels((m) => ({ ...m, loading: true, error: null, url }));
    try { setModels({ ids: await api.post<string[]>("/admin/ai/models", { baseUrl: url, apiKey: apiKey || null }), error: null, loading: false, url }); }
    catch (err) {
      const detail = err instanceof ApiError ? err.body?.detail ?? (err.body?.errors ? Object.values(err.body.errors).flat()[0] : null) : null;
      setModels({ ids: null, error: `Models could not be listed: ${detail ?? "no answer from the server."}`, loading: false, url });
    }
  }
  useEffect(() => {
    void api.get<Dto>("/admin/ai").then((d) => { setDto(d); setForm(d.settings); void loadModels(d.settings.baseUrl, null); });
  }, []);
  if (!dto || !form) return null;

  const set = <K extends keyof AiSettings>(k: K, v: AiSettings[K]) => { setForm({ ...form, [k]: v }); setStatus(null); };
  const dirty = JSON.stringify(form) !== JSON.stringify(dto.settings) || key !== null;

  async function save(e?: FormEvent, settings: AiSettings = form!) {
    e?.preventDefault();
    setBusy(true);
    setStatus(null);
    try {
      const d = await api.put<Dto>("/admin/ai", { settings, apiKey: key });
      setDto(d); setForm(d.settings); setKey(null); setStatus("Saved."); aiChanged();
    } catch (err) {
      setStatus(err instanceof ApiError && err.body?.errors ? Object.values(err.body.errors).flat()[0] ?? "Not saved." : "Not saved.");
    } finally { setBusy(false); }
  }

  // Puts the hard default prompt back and saves it at once (audited like any change), after asking.
  async function restore() {
    if (!form || !dto || !await dialogs.confirm({ title: "Restore the default prompt?", ok: "Restore to default", danger: true })) return;
    await save(undefined, { ...form, ruleset: dto.defaultRuleset });
  }

  async function runTest() {
    setTest(null);
    setBusy(true);
    try { setTest(await api.post<TestResult>("/admin/ai/test")); }
    catch { setTest({ ok: false, model: null, milliseconds: 0, reasoningEmpty: false, error: "The test could not run." }); }
    finally { setBusy(false); }
  }

  const num = (k: "timeoutSeconds" | "maxTokens" | "maxInputChars" | "userPerMinute" | "tokenPerMinute" | "temperature" | "importConcurrency", label: string, step = 1) => (
    <>
      <label className="lbl" htmlFor={`ai-${k}`}>{label}</label>
      <input id={`ai-${k}`} type="number" step={step} value={form[k] ?? ""} onChange={(e) => set(k, Number(e.target.value))} className="narrow" />
    </>
  );

  return (
    <>
      <form className="form" onSubmit={save}>
        <span className="lbl">AI</span><Switch on={form.enabled} label="AI" onChange={(v) => set("enabled", v)} />
        <label className="lbl" htmlFor="ai-url">Server address</label>
        <input id="ai-url" type="text" value={form.baseUrl} maxLength={500} onChange={(e) => set("baseUrl", e.target.value)}
          onBlur={() => { if (form.baseUrl.trim() !== models.url) void loadModels(form.baseUrl, key); }} />
        <label className="lbl" htmlFor="ai-model">Model</label>
        <span className="unit">
          <select id="ai-model" value={form.model} onChange={(e) => set("model", e.target.value)} disabled={models.loading}>
            {!form.model && <option value="">{models.ids ? "Choose a model" : "No models listed"}</option>}
            {form.model && !models.ids?.includes(form.model) && <option value={form.model}>{form.model} (not currently listed)</option>}
            {models.ids?.map((id) => <option key={id} value={id}>{id}</option>)}
          </select>
          <button type="button" className="tb" disabled={models.loading || !form.baseUrl.trim()} onClick={() => void loadModels(form.baseUrl, key)}>
            {models.loading ? "Refreshing…" : "Refresh"}
          </button>
        </span>
        {models.error && <><span /><p className="form-error" role="alert">{models.error}</p></>}
        <label className="lbl" htmlFor="ai-key">API key</label>
        <span className="unit">
          <input id="ai-key" type="password" autoComplete="new-password" value={key ?? ""} maxLength={2000} onChange={(e) => setKey(e.target.value)} />
          <span>{key === "" ? "Cleared" : dto.apiKeySet ? "Set" : "Not set"}</span>
          {dto.apiKeySet && key !== "" && <button type="button" className="tb" onClick={() => setKey("")}>Clear</button>}
        </span>
        <span className="lbl">Reasoning</span><Switch on={form.reasoning} label="Reasoning" onChange={(v) => set("reasoning", v)} />
        {num("temperature", "Temperature", 0.1)}
        {num("maxTokens", "Max tokens")}
        {num("timeoutSeconds", "Timeout (seconds)")}
        {num("maxInputChars", "Max input (characters)")}
        {num("userPerMinute", "Calls per person per minute")}
        {num("tokenPerMinute", "Calls per API token per minute")}
        {num("importConcurrency", "Import drafts at once")}
        {FEATURES.map(([f, label]) => (
          <span key={f} className="contents">
            <span className="lbl">{label}</span>
            <div className="seg" role="radiogroup" aria-label={label}>
              {ACCESS.map(([v, t]) => (
                <button key={v} type="button" role="radio" aria-checked={form.features[f] === v} className={form.features[f] === v ? "on" : ""}
                  onClick={() => set("features", { ...form.features, [f]: v })}>{t}</button>
              ))}
            </div>
          </span>
        ))}
        <label className="lbl top" htmlFor="ai-rules">Prompt</label>
        <div className="prompt-edit">
          <textarea id="ai-rules" value={form.ruleset} maxLength={20000} onChange={(e) => set("ruleset", e.target.value)} />
          <span className="unit">
            <span role="status" aria-label="Prompt state">{form.ruleset.trim() === dto.defaultRuleset.trim() ? "Default" : "Customized"}</span>
            {form.ruleset.trim() !== dto.defaultRuleset.trim() && <button type="button" className="tb" disabled={busy} onClick={() => void restore()}>Restore to default</button>}
          </span>
        </div>
        <div className="actions">
          <button type="submit" className="tb primary" disabled={busy || !dirty}>{busy ? "Saving…" : "Save"}</button>
          <button type="button" className="tb" disabled={busy || !dirty} onClick={() => { setForm(dto.settings); setKey(null); setStatus(null); }}>Cancel</button>
          <button type="button" className="tb" disabled={busy || dirty} onClick={() => void runTest()}>Test connection</button>
          {status && <span className="muted" role="status">{status}</span>}
        </div>
      </form>
      {test && (
        <p className={test.ok ? "" : "form-error"} role="alert">
          {test.ok ? `Connected · ${test.model} · ${test.milliseconds} ms · reasoning ${test.reasoningEmpty ? "empty" : "returned"}` : test.error}
        </p>
      )}
    </>
  );
}
