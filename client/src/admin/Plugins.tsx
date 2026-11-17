import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { ChevronRight, House, Settings, Upload } from "lucide-react";
import { api, ApiError, fmtDateTime, upload, type PluginHealth, type PluginInfo, type PluginPermissions, type PluginSetting } from "../api";
import { useDialogs } from "../Dialogs";
import { PluginPanels } from "../kit/SlotViews";

type PluginList = { plugins: PluginInfo[]; safeMode: boolean };
type SettingsData = { values: Record<string, string | null>; secretsSet: string[] };

/** The server's own words for a refusal; otherwise the short fallback. */
const problem = (e: unknown, fallback: string) => (e instanceof ApiError && (e.body?.detail || e.body?.error)) || fallback;
const stateText = (p: PluginInfo) => p.removed ? "Removed, data kept" : p.state === "DisabledByCore" ? "Disabled by core" : p.state;
const isSecret = (s: PluginSetting) => s.secret || s.type === "secret";

/**
 * Administration › Plugins: what is installed, and for one plugin its page (opened by the gear left of its name, or a click on its row):
 * its state, its settings, what it asks for, and the panels its own script adds.
 * Every change restarts the app; the screen says so and comes back by itself.
 */
export function Plugins({ area, onHome }: { area: string; onHome: () => void }) {
  const [data, setData] = useState<PluginList | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [restarting, setRestarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [removing, setRemoving] = useState(false);
  const file = useRef<HTMLInputElement>(null);
  const { confirm } = useDialogs();

  const [health, setHealth] = useState<PluginHealth | null>(null);

  const load = useCallback(async () => {
    setData(await api.get<PluginList>("/admin/plugins"));
    // Health is extra: the list is shown also when it cannot be read.
    setHealth(await api.get<PluginHealth>("/admin/plugins/health").catch(() => null));
  }, []);
  useEffect(() => { void load(); }, [load]);

  // While the app restarts: ask every 2 seconds, and read the list again when it answers.
  useEffect(() => {
    if (!restarting) return;
    const t = setInterval(() => {
      void fetch("/health/ready").then(async (r) => {
        if (!r.ok) return;
        setRestarting(false);
        await load();
      }).catch(() => undefined);
    }, 2000);
    return () => clearInterval(t);
  }, [restarting, load]);

  const selected = data?.plugins.find((p) => p.id === selectedId) ?? null;
  const selectedHealth = health?.plugins.find((p) => p.id === selectedId) ?? null;

  async function change(work: () => Promise<unknown>, failed: string) {
    setError(null);
    try { await work(); setRestarting(true); } catch (e) { setError(problem(e, failed)); }
  }

  /** The tables of a removed plugin are dropped. It cannot be undone, so it asks first. */
  async function deleteData(p: PluginInfo) {
    if (!await confirm({ title: `Delete the data of ${p.name}?`, ok: "Delete data", danger: true })) return;
    await change(async () => { await api.del(`/admin/plugins/${p.id}/data`); setSelectedId(null); }, "Not deleted.");
  }

  async function install(f: File) {
    setError(null);
    try { await upload("/admin/plugins", f); setRestarting(true); }
    catch (e) { setError(e instanceof ApiError && e.status === 413 ? "Larger than the web server allows." : problem(e, "Not installed.")); }
  }

  const list = (items: string[]) => items.length ? items.map((x) => <div key={x} className="mono">{x}</div>) : <div className="muted">None</div>;

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
            {selected && <><ChevronRight className="ic sm" aria-hidden /><button className="crumb-link" onClick={() => { setSelectedId(null); setError(null); }}>Plugins</button><ChevronRight className="ic sm" aria-hidden /><b>{selected.name}</b></>}
          </nav>
          <div className="titlebar">
            <h1>{selected?.name ?? "Plugins"}</h1>
            <div className="ptools">
              {restarting && <span className="muted" role="status">Restarting</span>}
              {!selected && (
                <>
                  <input ref={file} type="file" hidden aria-label="Plugin package" accept=".kbplugin,.zip"
                    onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ""; if (f) void install(f); }} />
                  <button type="button" className="tb" disabled={restarting} onClick={() => file.current?.click()}><Upload className="ic" aria-hidden />Upload</button>
                </>
              )}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}

          {data && !selected && (
            <table className="grid-table">
              <thead><tr><th>Name</th><th>Version</th><th>Publisher</th><th>State</th></tr></thead>
              <tbody>
                {data.plugins.map((p) => (
                  <tr key={p.id} className={`row ${p.state === "Enabled" ? "" : "off"}`} onClick={() => { setSelectedId(p.id); setError(null); }}>
                    <td>
                      <span className="plugin-name">
                        <button type="button" className="tb icon gear" aria-label={`Settings of ${p.name}`} title={`Settings of ${p.name}`}
                          onClick={(e) => { e.stopPropagation(); setSelectedId(p.id); setError(null); }}>
                          <Settings className="ic" aria-hidden />
                        </button>
                        {p.name}
                      </span>
                    </td>
                    <td>{p.version}</td><td className="muted">{p.publisher}</td><td>{stateText(p)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          {selected && (
            <>
              {selected.description && <p className="plugin-desc">{selected.description}</p>}
              <p className="state-line">{selected.state === "DisabledByCore" ? `Disabled by core: ${selected.reason ?? ""}` : stateText(selected)}</p>
              {selected.removed && (
                <div className="ptools row">
                  <button type="button" className="tb danger" disabled={restarting} onClick={() => void deleteData(selected)}>Delete data</button>
                </div>
              )}
              {!selected.removed && <div className="ptools row">
                {selected.state === "Enabled"
                  ? <button type="button" className="tb" disabled={restarting} onClick={() => void change(() => api.put(`/admin/plugins/${selected.id}/state`, { enabled: false }), "Not disabled.")}>Disable</button>
                  : <button type="button" className="tb" disabled={restarting} onClick={() => void change(() => api.put(`/admin/plugins/${selected.id}/state`, { enabled: true }), "Not enabled.")}>Enable</button>}
                {selected.origin !== "BuiltIn" && <button type="button" className="tb" disabled={restarting} onClick={() => setRemoving(true)}>Remove</button>}
                {selected.locked && <button type="button" className="tb" disabled={restarting} onClick={() => void change(() => api.post(`/admin/plugins/${selected.id}/release-lock`), "Not released.")}>Release lock</button>}
              </div>}
              {(selected.signInMethods ?? []).map((m) => (
                <section key={m.id} className="plugin-panel" aria-label={m.name}>
                  <h2>{m.name}</h2>
                  <p>Register this app with the provider using this redirect address, then set the client ID and secret below.</p>
                  <input className="mono wide" readOnly value={m.redirectAddress} aria-label={`Redirect address of ${m.name}`} onFocus={(e) => e.currentTarget.select()} />
                </section>
              ))}
              {selected.settings.length > 0 && <SettingsForm key={selected.id} plugin={selected} onRestart={() => setRestarting(true)} />}
              {!selected.removed && selected.permissions.some((p) => p.scope === "space") && <PermissionsForm key={`${selected.id}-permissions`} plugin={selected} />}
              {selected.loaded && <PluginPanels key={`${selected.id}-panels`} plugin={{ id: selected.id, version: selected.version }} />}
            </>
          )}
        </div>
      </main>

      <aside className="side" aria-label="Details">
        {selected ? (
          <>
            <h3>Id</h3><div className="mono">{selected.id}</div>
            <h3>Version</h3><div>{selected.version}</div>
            <h3>Publisher</h3><div>{selected.publisher}</div>
            <h3>Source</h3><div>{selected.source}</div>
            <h3>Provides</h3>{list(selected.extensionPoints)}
            <h3>Reads for import</h3>{list(selected.importFileKinds)}
            <h3>Sign-in methods</h3>{list((selected.signInMethods ?? []).map((m) => m.name))}
            <h3>Requires</h3>{list(selected.requires)}
            <h3>Required by</h3>{list(selected.requiredBy)}
            <h3>Outbound hosts</h3>{list(selected.outboundHosts)}
            <h3>Permissions</h3>{list(selected.permissions.map((p) => p.label))}
            <h3>Enforcing</h3><div>{selected.enforcing ? "Yes" : "No"}</div>
            <h3>Replaces</h3>{list(selected.replaces)}
            {selectedHealth && selectedHealth.checks.length > 0 && (
              <>
                <h3>Health</h3>
                {selectedHealth.checks.map((c) => (
                  <div key={c.name}>
                    <div className={c.status === "Healthy" ? undefined : "form-error"}>{c.name}: {c.status}</div>
                    {c.text && <div className="muted">{c.text}</div>}
                  </div>
                ))}
              </>
            )}
            {selectedHealth && selectedHealth.jobs.length > 0 && (
              <>
                <h3>Jobs</h3>
                {selectedHealth.jobs.map((j) => (
                  <div key={j.name}>
                    <div>{j.name}, every {j.everyMinutes} min</div>
                    <div className="muted">{j.lastRunAt ? `Last run ${fmtDateTime(j.lastRunAt)}` : "Not run yet"}</div>
                    {j.nextRunAt && <div className="muted">Next run {fmtDateTime(j.nextRunAt)}</div>}
                    {j.lastError && <div className="form-error">{j.lastError}</div>}
                  </div>
                ))}
              </>
            )}
          </>
        ) : data?.safeMode ? (
          <><h3>Safe mode</h3><div>On</div></>
        ) : null}
      </aside>

      {removing && selected && (
        <RemoveDialog name={selected.name} onClose={() => setRemoving(false)}
          onRemove={async (deleteData) => {
            setRemoving(false);
            const id = selected.id;
            await change(async () => { await api.del(`/admin/plugins/${id}?deleteData=${deleteData}`); setSelectedId(null); }, "Not removed.");
          }} />
      )}
    </>
  );
}

function RemoveDialog({ name, onClose, onRemove }: { name: string; onClose: () => void; onRemove: (deleteData: boolean) => Promise<void> }) {
  const [deleteData, setDeleteData] = useState(false);
  return (
    <dialog ref={(d) => { if (d && !d.open) d.showModal(); }} className="dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="rm-title">
      <form onSubmit={(e) => { e.preventDefault(); void onRemove(deleteData); }}>
        <h2 id="rm-title">Remove {name}?</h2>
        <label className="check"><input type="checkbox" className="chk" checked={deleteData} onChange={(e) => setDeleteData(e.target.checked)} />Delete data</label>
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose}>Cancel</button>
          <button type="submit" className="tb danger">Remove</button>
        </div>
      </form>
    </dialog>
  );
}

const ROLES = ["Reader", "Contributor", "Supervisor", "Owner"];

/** The least role that holds each space permission of a plugin. A global permission is given per person, on the Users screen. */
function PermissionsForm({ plugin }: { plugin: PluginInfo }) {
  const [roles, setRoles] = useState<Record<string, string> | null>(null);
  const [labels, setLabels] = useState<Record<string, string>>({});
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void api.get<PluginPermissions>(`/admin/plugins/${plugin.id}/permissions`).then((r) => {
      const space = r.permissions.filter((p) => p.scope === "space");
      setRoles(Object.fromEntries(space.map((p) => [p.key, p.role ?? p.defaultRole])));
      setLabels(Object.fromEntries(space.map((p) => [p.key, p.label])));
    });
  }, [plugin]);

  if (!roles) return null;

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    try { await api.put(`/admin/plugins/${plugin.id}/permissions`, { roles }); setStatus("Saved."); }
    catch (err) { setStatus(problem(err, "Not saved.")); }
    finally { setBusy(false); }
  }

  return (
    <form className="form" onSubmit={save}>
      {Object.keys(roles).map((key) => (
        <FieldRow key={key} label={labels[key]} id={`pp-${key}`} error={null}>
          <select id={`pp-${key}`} className="sel" value={roles[key]} onChange={(e) => { setRoles((all) => ({ ...all, [key]: e.target.value })); setStatus(null); }}>
            {ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </FieldRow>
      ))}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy}>Save permissions</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

/** A plugin's settings, drawn from its definitions. A secret that is set shows as dots; its value is never loaded. */
function SettingsForm({ plugin, onRestart }: { plugin: PluginInfo; onRestart: () => void }) {
  const [values, setValues] = useState<Record<string, string> | null>(null);
  // null: the secret is set and stays as it is. A text: what to store instead (empty clears it).
  const [secrets, setSecrets] = useState<Record<string, string | null>>({});
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void api.get<SettingsData>(`/admin/plugins/${plugin.id}/settings`).then((s) => {
      setValues(Object.fromEntries(plugin.settings.filter((d) => !isSecret(d)).map((d) => [d.key, s.values[d.key] ?? ""])));
      setSecrets(Object.fromEntries(plugin.settings.filter(isSecret).map((d) => [d.key, s.secretsSet.includes(d.key) ? null : ""])));
    });
  }, [plugin]);

  if (!values) return null;
  const set = (key: string, v: string) => { setValues((all) => ({ ...all, [key]: v })); setStatus(null); };

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErrors({});
    setStatus(null);
    try {
      const r = await api.put<{ restart: boolean }>(`/admin/plugins/${plugin.id}/settings`, { values: Object.fromEntries(plugin.settings.map((d) => [d.key, isSecret(d) ? secrets[d.key] : values![d.key]])) });
      setSecrets((all) => Object.fromEntries(Object.entries(all).map(([k, v]) => [k, v ? null : v])));
      setStatus("Saved.");
      if (r.restart) onRestart();
    } catch (err) {
      if (err instanceof ApiError && err.body?.errors) setErrors(err.body.errors); else setStatus(problem(err, "Not saved."));
    } finally { setBusy(false); }
  }

  const control = (d: PluginSetting): ReactNode => {
    const id = `ps-${d.key}`;
    if (isSecret(d)) {
      return secrets[d.key] === null
        ? <span className="unit"><span>••••••••</span><button type="button" className="tb" onClick={() => setSecrets((all) => ({ ...all, [d.key]: "" }))}>Change</button></span>
        : <input id={id} type="password" autoComplete="new-password" value={secrets[d.key] ?? ""} onChange={(e) => { setSecrets((all) => ({ ...all, [d.key]: e.target.value })); setStatus(null); }} />;
    }
    const v = values[d.key] ?? "";
    // Nothing saved: the plugin gets the default, so the field shows it (as the placeholder of a text, the state of a toggle).
    const shown = d.default ?? "";
    const help = d.help ? `${id}-h` : undefined;
    const optionLabel = (o: string) => d.optionLabels?.[o] ?? o;
    const withUnit = (field: ReactNode) => d.unit ? <div className="unit">{field}<span>{d.unit}</span></div> : field;
    switch (d.type) {
      case "toggle": {
        const on = (v || shown) === "true";
        return <button id={id} type="button" role="switch" aria-checked={on} aria-labelledby={`${id}-l`} aria-describedby={help} className={`sw ${on ? "on" : ""}`} onClick={() => set(d.key, on ? "false" : "true")} />;
      }
      case "select": return withUnit(
        <select id={id} className="sel" value={v} aria-describedby={help} onChange={(e) => set(d.key, e.target.value)}>
          {!(d.options ?? []).includes("") && <option value="">{d.default ? `Default (${optionLabel(d.default)})` : ""}</option>}
          {(d.options ?? []).map((o) => <option key={o} value={o}>{optionLabel(o)}</option>)}
        </select>,
      );
      case "multiline": return <textarea id={id} value={v} placeholder={shown || undefined} aria-describedby={help} onChange={(e) => set(d.key, e.target.value)} />;
      case "number": return (
        <div className="unit">
          <input id={id} type="number" value={v} placeholder={shown || undefined} min={d.min ?? undefined} max={d.max ?? undefined} aria-describedby={help} onChange={(e) => set(d.key, e.target.value)} />
          {d.unit && <span>{d.unit}</span>}
        </div>
      );
      default: return withUnit(<input id={id} type="text" value={v} placeholder={shown || undefined} spellCheck={false} aria-describedby={help} onChange={(e) => set(d.key, e.target.value)} />);
    }
  };

  // noValidate: the app checks the bounds and says so in its own words, under the field.
  return (
    <form className="form" noValidate onSubmit={save}>
      {plugin.settings.map((d) => {
        const dots = isSecret(d) && secrets[d.key] === null;
        return (
          <FieldRow key={d.key} label={d.label} id={dots ? undefined : `ps-${d.key}`} labelId={d.type === "toggle" ? `ps-${d.key}-l` : undefined} top={d.type === "multiline"}
            error={errors[d.key] && <p className="form-error wide" role="alert">{errors[d.key][0]}</p>}>
            {control(d)}
            {d.help && <p className="hint" id={`ps-${d.key}-h`}>{d.help}</p>}
          </FieldRow>
        );
      })}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy}>Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

function FieldRow({ label, id, labelId, top, error, children }: { label: string; id?: string; labelId?: string; top?: boolean; error: ReactNode; children: ReactNode }) {
  const cls = `lbl ${top ? "top" : ""}`;
  return (
    <>
      {labelId ? <span className={cls} id={labelId}>{label}</span> : id ? <label className={cls} htmlFor={id}>{label}</label> : <span className={cls}>{label}</span>}
      {children}
      {error}
    </>
  );
}
