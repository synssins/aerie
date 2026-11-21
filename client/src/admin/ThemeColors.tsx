import { useMemo, useRef, useState, type CSSProperties } from "react";
import { Download, RotateCcw, Upload } from "lucide-react";
import { api, ApiError, type Brand } from "../api";
import { useDialogs } from "../Dialogs";
import {
  clean, EMPTY_THEME, lowContrast, normalizeHex, PRESETS, resolved, THEME_GROUPS, THEME_TOKENS, valueOf,
  type Mode, type ThemeOverrides,
} from "../themeTokens";

const LABEL = new Map(THEME_TOKENS.map((t) => [t.name, t.label]));
const MODES: [Mode, string][] = [["day", "Day"], ["night", "Night"]];
const same = (a: ThemeOverrides, b: ThemeOverrides) => JSON.stringify(clean(a)) === JSON.stringify(clean(b));

/** One color of one mode: a picker and its hex value, and Reset when it differs from the default. */
function ColorCell({ name, mode, theme, onSet }: { name: string; mode: Mode; theme: ThemeOverrides; onSet: (value: string | null) => void }) {
  const value = valueOf(theme, mode, name);
  const [typed, setTyped] = useState<string | null>(null);
  const label = `${LABEL.get(name)}, ${mode === "day" ? "Day" : "Night"}`;
  const bad = typed !== null && normalizeHex(typed) === null;
  return (
    <div className="color-cell">
      <input type="color" aria-label={`${label} color`} value={value} onChange={(e) => { setTyped(null); onSet(e.target.value); }} />
      <input type="text" className={`hex ${bad ? "bad" : ""}`} aria-label={`${label} hex`} aria-invalid={bad} maxLength={7} spellCheck={false}
        value={typed ?? value} onChange={(e) => { setTyped(e.target.value); const v = normalizeHex(e.target.value); if (v) onSet(v); }}
        onBlur={() => setTyped(null)} title={bad ? "A color as #rgb or #rrggbb" : undefined} />
      {theme[mode][name]
        ? <button type="button" className="tb icon" aria-label={`Reset ${label}`} title="Reset to default" onClick={() => { setTyped(null); onSet(null); }}><RotateCcw className="ic" aria-hidden /></button>
        : <span className="reset-space" />}
    </div>
  );
}

/** A small app in the colors of one mode: what the theme looks like before it is saved. */
function Preview({ theme, mode, productName }: { theme: ThemeOverrides; mode: Mode; productName: string }) {
  const vars = resolved(theme, mode) as CSSProperties;
  return (
    <figure className="theme-preview" style={vars} aria-label={`Preview, ${mode === "day" ? "Day" : "Night"}`}>
      <figcaption>{mode === "day" ? "Day" : "Night"}</figcaption>
      <div className="tp-stage">
        <div className="tp-bar">
          <span className="tp-logo">{productName}</span>
          <span className="tp-search">Search <kbd>Ctrl K</kbd></span>
          <span className="tp-grow" />
          <span className="tp-ib">New</span>
          <span className="tp-ib on">⚙<span className="tp-dot">3</span></span>
          <span className="tp-av">AK</span>
        </div>
        <div className="tp-body">
          <div className="tp-tree">
            <div className="tp-node"><span className="tp-folder">▸</span> Runbooks</div>
            <div className="tp-node cur">Reset MFA</div>
            <div className="tp-node hover">Printers</div>
          </div>
          <div className="tp-page">
            <div className="tp-h">Reset MFA</div>
            <div className="tp-text">Open the <span className="tp-link">portal</span>, then follow the steps.</div>
            <div className="tp-quiet">Updated by Pat · version 1.2</div>
            <div className="tp-tags"><span className="tp-tag">mfa</span><span className="tp-tag">identity</span></div>
            <div className="tp-notice">Do not reboot during business hours.</div>
            <pre className="tp-code"><span className="tp-c-k">Restart-Service</span> <span className="tp-c-s">"Spooler"</span> <span className="tp-c-c"># {mode === "day" ? "day" : "night"}</span></pre>
            <div className="tp-buttons"><span className="tp-btn primary">Save</span><span className="tp-btn">Cancel</span><span className="tp-btn danger">Delete</span></div>
            <div className="tp-diff"><span className="add">+ added</span><span className="del">− removed</span></div>
          </div>
        </div>
      </div>
    </figure>
  );
}

/** Administration › Branding › Theme colors: every color of the app, Day and Night, with presets, a preview and a contrast check. */
export function ThemeColors({ brand, onChanged }: { brand: Brand; onChanged: () => Promise<void> }) {
  const dialogs = useDialogs();
  const saved = useMemo(() => clean(brand.theme ?? EMPTY_THEME), [brand.theme]);
  const [draft, setDraft] = useState<ThemeOverrides>(saved);
  const [preset, setPreset] = useState(PRESETS[0].name);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);
  const dirty = !same(draft, saved);

  const set = (mode: Mode, name: string, value: string | null) => {
    setStatus(null);
    setDraft((d) => {
      const next = { ...d[mode] };
      if (value === null) delete next[name]; else next[name] = value;
      return { ...d, [mode]: next };
    });
  };

  async function put(theme: ThemeOverrides, done: string) {
    setBusy(true);
    setStatus(null);
    try { await api.put("/admin/brand/theme", clean(theme)); await onChanged(); setStatus(done); }
    catch (e) { setStatus(e instanceof ApiError && e.body?.errors ? Object.values(e.body.errors).flat()[0] ?? "Not saved." : "Not saved."); }
    finally { setBusy(false); }
  }

  async function resetAll() {
    if (!await dialogs.confirm({ title: "Reset every color to the default?", body: "Day and Night go back to the Aerie (default) theme for everyone.", ok: "Reset all", danger: true })) return;
    setDraft(EMPTY_THEME);
    await put(EMPTY_THEME, "Reset to the default.");
  }

  function exportTheme() {
    const blob = new Blob([JSON.stringify({ name: `${brand.productName} theme`, ...clean(draft) }, null, 2)], { type: "application/json" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = "theme.json";
    a.click();
    URL.revokeObjectURL(a.href);
  }

  async function importTheme(file: File) {
    try {
      const parsed = JSON.parse(await file.text()) as Partial<ThemeOverrides>;
      const theme = clean(parsed);
      setDraft(theme);
      setStatus(`Imported ${Object.keys(theme.day).length + Object.keys(theme.night).length} colors. Save to use them.`);
    } catch { setStatus("Not a theme file."); }
  }

  const warnings = MODES.flatMap(([mode, name]) => lowContrast(resolved(draft, mode)).map((w) => ({ ...w, mode: name })));
  return (
    <section className="theme-colors" aria-label="Theme colors">
      <h2 className="s">Theme colors</h2>
      <div className="bar-row">
        <label className="inline">Preset
          <select className="sel" value={preset} onChange={(e) => setPreset(e.target.value)}>
            {PRESETS.map((p) => <option key={p.name} value={p.name}>{p.name}</option>)}
          </select>
        </label>
        <button type="button" className="tb" onClick={() => { setDraft(clean(PRESETS.find((p) => p.name === preset)!.theme)); setStatus(`${preset} applied. Save to use it.`); }}>Apply</button>
        <span className="grow" />
        <button type="button" className="tb" onClick={exportTheme}><Download className="ic" aria-hidden />Export</button>
        <button type="button" className="tb" onClick={() => fileRef.current?.click()}><Upload className="ic" aria-hidden />Import</button>
        <input ref={fileRef} type="file" accept="application/json,.json" hidden aria-label="Theme file" onChange={(e) => { const f = e.target.files?.[0]; if (f) void importTheme(f); e.target.value = ""; }} />
      </div>

      <div className="theme-previews">
        {MODES.map(([mode]) => <Preview key={mode} theme={draft} mode={mode} productName={brand.productName} />)}
      </div>

      {warnings.length > 0 && (
        <div className="note-row" role="status">
          <span><b>Low contrast</b> (4.5:1 is recommended for text): {warnings.map((w) => `${w.mode}: ${LABEL.get(w.text)} on ${LABEL.get(w.background)} ${w.ratio.toFixed(1)}:1`).join("; ")}.</span>
        </div>
      )}

      <div className="actions">
        <button type="button" className="tb primary" disabled={busy || !dirty} onClick={() => void put(draft, "Saved.")}>{busy ? "Saving…" : "Save"}</button>
        <button type="button" className="tb" disabled={busy || !dirty} onClick={() => { setDraft(saved); setStatus(null); }}>Cancel</button>
        <button type="button" className="tb" disabled={busy || (Object.keys(saved.day).length + Object.keys(saved.night).length === 0 && !dirty)} onClick={() => void resetAll()}>Reset all to default</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>

      {THEME_GROUPS.map((group) => (
        <table key={group} className="grid-table theme-table">
          <thead><tr><th>{group}</th>{MODES.map(([m, name]) => <th key={m}>{name}</th>)}</tr></thead>
          <tbody>
            {THEME_TOKENS.filter((t) => t.group === group).map((t) => (
              <tr key={t.name}>
                <td>{t.label}{t.from && <span className="muted small"> · follows {LABEL.get(t.from)}{t.night ? " by Day" : ""}</span>}</td>
                {MODES.map(([m]) => <td key={m}><ColorCell name={t.name} mode={m} theme={draft} onSet={(v) => set(m, t.name, v)} /></td>)}
              </tr>
            ))}
          </tbody>
        </table>
      ))}
    </section>
  );
}
