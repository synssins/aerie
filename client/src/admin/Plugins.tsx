import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { ChevronRight, House, Plus, Settings, Upload } from "lucide-react";
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
  // Kept while the list is the same: the settings form reads its values again when its plugin changes.
  const own = useMemo(() => selected ? pluginOwn(selected) : null, [selected]);

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
              {(selected.signInMethods ?? []).filter((m) => !m.byAdmin).map((m) => (
                <section key={m.id} className="plugin-panel" aria-label={m.name}>
                  <h2>{m.name}</h2>
                  <p>Register this app with the provider using this redirect address, then set the client ID and secret below.</p>
                  <input className="mono wide" readOnly value={m.redirectAddress} aria-label={`Redirect address of ${m.name}`} onFocus={(e) => e.currentTarget.select()} />
                </section>
              ))}
              {selected.signInProviders && selected.loaded && <SignInProviders key={`${selected.id}-providers`} plugin={selected} onChanged={load} onRestart={() => setRestarting(true)} />}
              {own && own.settings.length > 0 && <SettingsForm key={selected.id} plugin={own} onRestart={() => setRestarting(true)} />}
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
            {(selected.importTools ?? []).length > 0 && (
              <>
                <h3>Tools for the AI in imports</h3>
                {(selected.importTools ?? []).map((t) => (
                  <details key={t.name}>
                    <summary><span className="mono">{t.name}</span>{t.notOffered ? " (not offered)" : ""}</summary>
                    <div>{t.description}</div>
                    {t.notOffered && <div className="muted">Not offered: {t.notOffered}</div>}
                    <pre className="mono">{t.inputSchema}</pre>
                  </details>
                ))}
              </>
            )}
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

type Provider = NonNullable<PluginInfo["signInMethods"]>[number];
const providerId = (m: Provider) => m.id.slice(m.id.indexOf("/") + 1);
const providerKeys = (p: PluginInfo) => new Set((p.signInMethods ?? []).filter((m) => m.byAdmin).map((m) => `signIn.${providerId(m)}.`));
/** The plugin with its own settings only: each provider's are under its gear. */
const pluginOwn = (p: PluginInfo): PluginInfo => {
  const keys = providerKeys(p);
  return keys.size === 0 ? p : { ...p, settings: p.settings.filter((s) => ![...keys].some((k) => s.key.startsWith(k))) };
};

/** The provider's keys, by their last part: "signIn.<id>.issuer" is "issuer". */
const keyOf = (m: Provider, part: string) => `signIn.${providerId(m)}.${part}`;
/** Shown first, in this order; the rest is under Advanced. New accounts stays in view: it is a real decision. */
const MAIN = ["issuer", "clientId", "clientSecret", "newAccounts"];
const REQUIRED = ["issuer", "clientId"];

/** Why a provider has no button on the sign-in screen, from its saved values; null when it has one. */
function missingText(values: Record<string, string | null | undefined>, m: Provider): string | null {
  const missing = [!values[keyOf(m, "issuer")]?.trim() && "issuer", !values[keyOf(m, "clientId")]?.trim() && "client ID"].filter(Boolean);
  return missing.length === 0 ? null : `Not shown on the sign-in screen: ${missing.join(" and ")} missing.`;
}

/**
 * Sign-in providers an Admin adds (contract 0.9.0): a list with Add new; each has its own gear that opens its title, its redirect address,
 * and its settings. Each is a button on the sign-in screen once its issuer and client ID are set; the list says when one is not.
 */
function SignInProviders({ plugin, onChanged, onRestart }: { plugin: PluginInfo; onChanged: () => Promise<void>; onRestart: () => void }) {
  const { ask, confirm } = useDialogs();
  const [open, setOpen] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<Record<string, string | null>>({});
  const providers = (plugin.signInMethods ?? []).filter((m) => m.byAdmin);
  const shown = providers.find((m) => m.id === open) ?? null;
  const validate = (v: string) => v.trim().length === 0 || v.trim().length > 60 ? "A title is 1 to 60 characters." : null;

  const loadSaved = useCallback(async () => {
    setSaved((await api.get<SettingsData>(`/admin/plugins/${plugin.id}/settings`).catch(() => null))?.values ?? {});
  }, [plugin.id]);
  useEffect(() => { void loadSaved(); }, [loadSaved, plugin]);

  async function run(work: () => Promise<unknown>, failed: string) {
    setError(null);
    try { await work(); await onChanged(); } catch (e) { setError(problem(e, failed)); }
  }

  async function add() {
    const title = await ask({ title: "New sign-in provider", label: "Title (the text of its button)", ok: "Add", validate });
    if (title === null) return;
    await run(async () => { const made = await api.post<{ id: string }>(`/admin/plugins/${plugin.id}/signin-providers`, { title: title.trim() }); setOpen(`${plugin.id}/${made.id}`); }, "Not added.");
  }

  async function rename(m: Provider) {
    const title = await ask({ title: `Rename ${m.name}`, label: "Title", initial: m.name, ok: "Rename", validate });
    if (title === null || title.trim() === m.name) return;
    await run(() => api.put(`/admin/plugins/${plugin.id}/signin-providers/${providerId(m)}`, { title: title.trim() }), "Not renamed.");
  }

  async function remove(m: Provider) {
    const linked = m.linked ?? 0;
    const body = linked === 0 ? "Its button leaves the sign-in screen and its settings are deleted."
      : `${linked === 1 ? "1 account is" : `${linked} accounts are`} linked to this provider's issuer. They keep the link and have no password, so they cannot sign in until a provider with the same issuer is set up again. To give one of them another way in, delete the account and make it again.`;
    if (!await confirm({ title: `Remove ${m.name}?`, body, ok: "Remove", danger: true })) return;
    await run(async () => { await api.del(`/admin/plugins/${plugin.id}/signin-providers/${providerId(m)}`); setOpen(null); }, "Not removed.");
  }

  return (
    <section className="plugin-panel" aria-label="Sign-in providers">
      <h2>Sign-in providers</h2>
      <p>Each provider is a button on the sign-in screen, through any OpenID Connect provider. It shows once its issuer and client ID are set.</p>
      {error && <p className="form-error" role="alert">{error}</p>}
      {providers.length > 0 && (
        <table className="grid-table">
          <thead><tr><th>Title</th><th>Redirect address</th></tr></thead>
          <tbody>
            {providers.map((m) => (
              <tr key={m.id}>
                <td>
                  <span className="plugin-name">
                    <button type="button" className="tb icon gear" aria-label={`Settings of ${m.name}`} title={`Settings of ${m.name}`} onClick={() => { setError(null); setOpen(m.id); }}>
                      <Settings className="ic" aria-hidden />
                    </button>
                    {m.name}
                  </span>
                  {missingText(saved, m) && <div className="form-error">{missingText(saved, m)}</div>}
                </td>
                <td className="mono">{m.redirectAddress}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div className="ptools row"><button type="button" className="tb" onClick={() => void add()}><Plus className="ic" aria-hidden />Add new</button></div>
      {shown && (
        <ProviderDialog key={shown.id} plugin={plugin} provider={shown} onClose={() => setOpen(null)} onRename={() => void rename(shown)} onRemove={() => void remove(shown)}
          onSaved={() => void loadSaved()} onRestart={onRestart} />
      )}
    </section>
  );
}

/**
 * One provider's gear: its redirect address (with Copy), the settings it cannot do without, then the rest under Advanced, closed.
 * Save and Close stay at the foot while the body scrolls; Close asks before it drops changed fields.
 */
function ProviderDialog({ plugin, provider: m, onClose, onRename, onRemove, onSaved, onRestart }: {
  plugin: PluginInfo; provider: Provider; onClose: () => void; onRename: () => void; onRemove: () => void; onSaved: () => void; onRestart: () => void;
}) {
  const { confirm } = useDialogs();
  const defs = useMemo(() => plugin.settings.filter((d) => d.key.startsWith(keyOf(m, ""))), [plugin, m]);
  const s = useSettings(defs, plugin.id, onRestart);
  const [advanced, setAdvanced] = useState(false);
  const [copied, setCopied] = useState(false);
  const part = (d: PluginSetting) => d.key.slice(keyOf(m, "").length);
  const main = MAIN.map((k) => defs.find((d) => part(d) === k)).filter((d): d is PluginSetting => !!d);
  const rest = defs.filter((d) => !MAIN.includes(part(d)));
  const missing = s.saved && missingText(s.saved, m);

  // An address in Discovery address with Issuer empty is nearly always the issuer pasted in the wrong field.
  const check = (v: Record<string, string>) => v[keyOf(m, "discovery")]?.trim() && !v[keyOf(m, "issuer")]?.trim()
    ? { [keyOf(m, "discovery")]: ["Put the provider's address in Issuer. Discovery address is only for a provider whose document is not at the issuer's /.well-known/openid-configuration."] }
    : null;

  async function save() {
    const ok = await s.save(check);
    if (!ok && check(s.values ?? {})) setAdvanced(true);
    if (ok) onSaved();
  }

  async function close() {
    if (s.dirty && !await confirm({ title: "Discard unsaved changes?", ok: "Discard", danger: true })) return;
    onClose();
  }

  const field = (d: PluginSetting) => <SettingField key={d.key} d={d} s={s} required={REQUIRED.includes(part(d))} />;

  return (
    <dialog ref={(d) => { if (d && !d.open) d.showModal(); }} className="dlg wide sheet" onCancel={(e) => { e.preventDefault(); void close(); }} aria-labelledby="sp-title">
      <div className="dlg-body">
        <h2 id="sp-title">{m.name}</h2>
        {missing && <p className="form-error" role="status">{missing}</p>}
        <p>Register this app at the provider with this redirect address. It stays the same when you rename the provider.</p>
        <div className="copy-row">
          <input className="mono" readOnly value={m.redirectAddress} aria-label={`Redirect address of ${m.name}`} onFocus={(e) => e.currentTarget.select()} />
          <button type="button" className="tb" onClick={() => void navigator.clipboard?.writeText(m.redirectAddress).then(() => setCopied(true))}>Copy</button>
          {copied && <span className="muted small" role="status">Copied</span>}
        </div>
        {s.values && (
          <form className="form stacked" noValidate onSubmit={(e) => { e.preventDefault(); void save(); }}>
            {main.map(field)}
            <details className="advanced" open={advanced} onToggle={(e) => setAdvanced(e.currentTarget.open)}>
              <summary>Advanced</summary>
              <div className="form stacked">{rest.map(field)}</div>
            </details>
            <button type="submit" hidden />
          </form>
        )}
      </div>
      <div className="dlg-foot">
        <button type="button" className="tb danger" onClick={onRemove}>Remove</button>
        <button type="button" className="tb" onClick={onRename}>Rename</button>
        <span className="grow" />
        {s.status && <span className="muted" role="status">{s.status}</span>}
        <button type="button" className="tb" onClick={() => void close()}>Close</button>
        <button type="button" className="tb primary" disabled={s.busy || !s.values} onClick={() => void save()}>Save</button>
      </div>
    </dialog>
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

type Settings = ReturnType<typeof useSettings>;

/**
 * The values of some of a plugin's settings, as saved and as edited. A secret that is set is null (shown as dots); its value is never loaded.
 * Save sends these settings only, so others of the plugin are left as they are.
 */
function useSettings(defs: PluginSetting[], pluginId: string, onRestart: () => void) {
  const [saved, setSaved] = useState<Record<string, string | null> | null>(null);
  const [values, setValues] = useState<Record<string, string> | null>(null);
  // null: the secret is set and stays as it is. A text: what to store instead (empty clears it).
  const [secrets, setSecrets] = useState<Record<string, string | null>>({});
  const [first, setFirst] = useState<{ values: Record<string, string>; secrets: Record<string, string | null> } | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void api.get<SettingsData>(`/admin/plugins/${pluginId}/settings`).then((r) => {
      const v = Object.fromEntries(defs.filter((d) => !isSecret(d)).map((d) => [d.key, r.values[d.key] ?? ""]));
      const sc = Object.fromEntries(defs.filter(isSecret).map((d) => [d.key, r.secretsSet.includes(d.key) ? null : ""]));
      setSaved(r.values); setValues(v); setSecrets(sc); setFirst({ values: v, secrets: sc });
    });
  }, [defs, pluginId]);

  const dirty = !!first && !!values && (defs.some((d) => isSecret(d) ? (secrets[d.key] ?? null) !== (first.secrets[d.key] ?? null) : values[d.key] !== first.values[d.key]));
  const set = (key: string, v: string) => { setValues((all) => ({ ...all, [key]: v })); setStatus(null); };
  const setSecret = (key: string, v: string) => { setSecrets((all) => ({ ...all, [key]: v })); setStatus(null); };

  /** True when saved. <paramref name="check"/> may refuse first, with messages under the fields, as the server's own are shown. */
  async function save(check?: (values: Record<string, string>) => Record<string, string[]> | null): Promise<boolean> {
    if (!values) return false;
    setErrors({});
    setStatus(null);
    const refused = check?.(values);
    if (refused) { setErrors(refused); return false; }
    setBusy(true);
    try {
      const r = await api.put<{ restart: boolean }>(`/admin/plugins/${pluginId}/settings`, { values: Object.fromEntries(defs.map((d) => [d.key, isSecret(d) ? secrets[d.key] : values[d.key]])) });
      const kept = Object.fromEntries(Object.entries(secrets).map(([k, v]) => [k, v ? null : v]));
      setSecrets(kept);
      setFirst({ values, secrets: kept });
      setSaved((all) => ({ ...all, ...values }));
      setStatus("Saved.");
      if (r.restart) onRestart();
      return true;
    } catch (err) {
      if (err instanceof ApiError && err.body?.errors) setErrors(err.body.errors); else setStatus(problem(err, "Not saved."));
      return false;
    } finally { setBusy(false); }
  }

  return { saved, values, secrets, errors, status, busy, dirty, set, setSecret, setSecrets, save };
}

/** One setting: its label, its control, its help, and what the server said about it. */
function SettingField({ d, s, required = false }: { d: PluginSetting; s: Settings; required?: boolean }) {
  const id = `ps-${d.key}`;
  const values = s.values ?? {};
  const dots = isSecret(d) && s.secrets[d.key] === null;
  const help = d.help ? `${id}-h` : undefined;
  const label = required ? `${d.label} (required)` : d.label;
  const control = (): ReactNode => {
    if (isSecret(d)) {
      return dots
        ? <span className="unit"><span>••••••••</span><button type="button" className="tb" onClick={() => s.setSecrets((all) => ({ ...all, [d.key]: "" }))}>Change</button></span>
        : <input id={id} type="password" autoComplete="new-password" aria-required={required || undefined} value={s.secrets[d.key] ?? ""} onChange={(e) => s.setSecret(d.key, e.target.value)} />;
    }
    const v = values[d.key] ?? "";
    // Nothing saved: the plugin gets the default, so the field shows it (as the placeholder of a text, the state of a toggle).
    const shown = d.default ?? "";
    const optionLabel = (o: string) => d.optionLabels?.[o] ?? o;
    const withUnit = (field: ReactNode) => d.unit ? <div className="unit">{field}<span>{d.unit}</span></div> : field;
    switch (d.type) {
      case "toggle": {
        const on = (v || shown) === "true";
        return <button id={id} type="button" role="switch" aria-checked={on} aria-labelledby={`${id}-l`} aria-describedby={help} className={`sw ${on ? "on" : ""}`} onClick={() => s.set(d.key, on ? "false" : "true")} />;
      }
      case "select": return withUnit(
        <select id={id} className="sel" value={v} aria-describedby={help} onChange={(e) => s.set(d.key, e.target.value)}>
          {!(d.options ?? []).includes("") && <option value="">{d.default ? `Default (${optionLabel(d.default)})` : ""}</option>}
          {(d.options ?? []).map((o) => <option key={o} value={o}>{optionLabel(o)}</option>)}
        </select>,
      );
      case "multiline": return <textarea id={id} value={v} placeholder={shown || undefined} aria-describedby={help} onChange={(e) => s.set(d.key, e.target.value)} />;
      case "number": return (
        <div className="unit">
          <input id={id} type="number" value={v} placeholder={shown || undefined} min={d.min ?? undefined} max={d.max ?? undefined} aria-describedby={help} onChange={(e) => s.set(d.key, e.target.value)} />
          {d.unit && <span>{d.unit}</span>}
        </div>
      );
      default: return withUnit(<input id={id} type="text" value={v} placeholder={shown || undefined} spellCheck={false} aria-required={required || undefined} aria-describedby={help} onChange={(e) => s.set(d.key, e.target.value)} />);
    }
  };
  return (
    <FieldRow label={label} id={dots ? undefined : id} labelId={d.type === "toggle" ? `${id}-l` : undefined} top={d.type === "multiline"}
      error={s.errors[d.key] && <p className="form-error wide" role="alert">{s.errors[d.key][0]}</p>}>
      {control()}
      {d.help && <p className="hint" id={help}>{d.help}</p>}
    </FieldRow>
  );
}

/** A plugin's settings, drawn from its definitions. */
function SettingsForm({ plugin, onRestart }: { plugin: PluginInfo; onRestart: () => void }) {
  const s = useSettings(plugin.settings, plugin.id, onRestart);
  if (!s.values) return null;
  // noValidate: the app checks the bounds and says so in its own words, under the field.
  return (
    <form className="form" noValidate onSubmit={(e) => { e.preventDefault(); void s.save(); }}>
      {plugin.settings.map((d) => <SettingField key={d.key} d={d} s={s} />)}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={s.busy}>Save</button>
        {s.status && <span className="muted" role="status">{s.status}</span>}
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
