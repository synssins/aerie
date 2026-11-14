import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { ChevronRight, Ellipsis, FileUp, House, Plus, Trash2 } from "lucide-react";
import { api, ApiError, type InvApp, type InvAppRow, type OwnerRef, type Principal, type SyncResult } from "./api";
import { useDialogs } from "./Dialogs";
import { MenuButton } from "./ui";

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const fmtDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { dateStyle: "medium" });
const problem = (e: unknown, fallback: string) =>
  (e instanceof ApiError && (e.body?.detail || (e.body?.errors && Object.values(e.body.errors).flat()[0]))) || fallback;

/** A person or a group, found by name. */
export function PrincipalPicker({ value, onChange, label }: { value: OwnerRef | null; onChange: (v: OwnerRef | null) => void; label: string }) {
  const [query, setQuery] = useState(value?.name ?? "");
  const [found, setFound] = useState<Principal[]>([]);
  useEffect(() => { setQuery(value?.name ?? ""); }, [value]);
  useEffect(() => {
    const q = query.trim();
    if (!q || q === value?.name) { setFound([]); return; }
    const t = setTimeout(() => void api.get<Principal[]>(`/principals?q=${encodeURIComponent(q)}`).then(setFound).catch(() => setFound([])), 200);
    return () => clearTimeout(t);
  }, [query, value]);
  return (
    <div className="menu-wrap picker">
      <input type="text" aria-label={label} autoComplete="off" value={query} maxLength={100}
        onChange={(e) => { setQuery(e.target.value); if (!e.target.value.trim()) onChange(null); }} />
      {found.length > 0 && (
        <div className="menu" role="listbox" aria-label="Matches">
          {found.map((p) => (
            <button key={`${p.type}:${p.id}`} type="button" role="option" aria-selected={false}
              onClick={() => { onChange({ type: p.type, id: p.id, name: p.name }); setFound([]); }}>
              <span className="menu-two">{p.name}<small>{p.type === "Group" ? `Group · ${p.detail}` : p.detail}</small></span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function Crumbs({ onHome, app, onList }: { onHome: () => void; app?: string; onList: () => void }) {
  return (
    <nav className="crumb" aria-label="Breadcrumb">
      <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden />
      {app ? <><button className="crumb-link" onClick={onList}>Inventory</button><ChevronRight className="ic sm" aria-hidden /><b>{app}</b></> : <b>Inventory</b>}
    </nav>
  );
}

/** The application inventory: the list, or one application. */
export function InventoryView({ appId, onOpenApp, onOpenPage, onHome }: {
  appId: string | null; onOpenApp: (id: string | null) => void; onOpenPage: (id: string) => void; onHome: () => void;
}) {
  return appId
    ? <AppDetail key={appId} id={appId} onList={() => onOpenApp(null)} onOpenPage={onOpenPage} onHome={onHome} onGone={() => onOpenApp(null)} />
    : <AppList onOpen={(id) => onOpenApp(id)} onHome={onHome} />;
}

function AppList({ onOpen, onHome }: { onOpen: (id: string) => void; onHome: () => void }) {
  const dialogs = useDialogs();
  const [rows, setRows] = useState<InvAppRow[] | null>(null);
  const [servers, setServers] = useState<string[]>([]);
  const [q, setQ] = useState("");
  const [server, setServer] = useState("");
  const [canEdit, setCanEdit] = useState(false);
  const [importing, setImporting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    const p = new URLSearchParams();
    if (q.trim()) p.set("q", q.trim());
    if (server) p.set("server", server);
    return api.get<InvAppRow[]>(`/inventory/apps?${p}`).then(setRows).catch(() => setRows([]));
  }, [q, server]);
  useEffect(() => { const t = setTimeout(() => void load(), 200); return () => clearTimeout(t); }, [load]);
  useEffect(() => {
    void api.get<string[]>("/inventory/servers").then(setServers).catch(() => setServers([]));
    void api.get<{ canEdit: boolean }>("/inventory/me").then((m) => setCanEdit(m.canEdit)).catch(() => setCanEdit(false));
  }, []);

  async function create() {
    setError(null);
    const name = await dialogs.ask({ title: "New application", label: "Name", ok: "Add", validate: (v) => (v.trim() ? null : "Required.") });
    if (!name) return;
    try { onOpen((await api.post<InvApp>("/inventory/apps", { name: name.trim() })).id); }
    catch (e) { setError(problem(e, "Not added.")); }
  }

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} onList={() => {}} />
          <div className="titlebar">
            <h1>Inventory</h1>
            {canEdit && (
              <div className="ptools">
                <MenuButton label="New" icon={<Plus className="ic" aria-hidden />} items={[
                  { label: "Application", onClick: () => void create() },
                  { label: "CSV import", icon: <FileUp className="ic" aria-hidden />, onClick: () => setImporting(true) },
                ]} />
              </div>
            )}
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          <div className="audit-filters">
            <label>Name or vendor<input type="text" value={q} maxLength={200} onChange={(e) => setQ(e.target.value)} /></label>
            <label>Server
              <select className="sel" value={server} onChange={(e) => setServer(e.target.value)}>
                <option value="">All</option>
                {servers.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </label>
          </div>
          <table className="grid-table">
            <thead><tr><th>Application</th><th>Vendor</th><th>Owner</th><th>Servers</th></tr></thead>
            <tbody>
              {rows?.map((a) => (
                <tr key={a.id} className="row" onClick={() => onOpen(a.id)}>
                  <td><button type="button" className="link" onClick={(e) => { e.stopPropagation(); onOpen(a.id); }}>{a.name}</button></td>
                  <td className="muted">{a.vendor ?? ""}</td><td>{a.owner?.name ?? ""}</td><td className="muted">{a.installs}</td>
                </tr>
              ))}
              {rows?.length === 0 && <tr><td colSpan={4} className="muted">No applications.</td></tr>}
            </tbody>
          </table>
          {importing && <ImportDialog onClose={() => setImporting(false)} onApplied={() => { setImporting(false); void load(); void api.get<string[]>("/inventory/servers").then(setServers); }} />}
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Applications</h3><div>{rows?.length ?? ""}</div>
        <h3>Servers</h3><div>{servers.length}</div>
      </aside>
    </>
  );
}

/** CSV import: the file, a preview of what would change, then Apply. */
export function ImportDialog({ onClose, onApplied }: { onClose: () => void; onApplied: (r: SyncResult) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [csv, setCsv] = useState("");
  const [removeMissing, setRemoveMissing] = useState(false);
  const [preview, setPreview] = useState<SyncResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { ref.current?.showModal(); }, []);

  async function run(apply: boolean) {
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<SyncResult>("/inventory/import", { csv, removeMissing, apply });
      if (apply) onApplied(r); else setPreview(r);
    } catch (e) { setError(problem(e, "Not read.")); }
    finally { setBusy(false); }
  }

  const changes = preview?.changes.filter((c) => c.kind !== "Unchanged") ?? [];
  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="imp-title">
      <h2 id="imp-title">CSV import</h2>
      <label>File<input type="file" accept=".csv,text/csv" onChange={(e) => { setPreview(null); void e.target.files?.[0]?.text().then(setCsv); }} /></label>
      <label className="check"><input type="checkbox" className="chk" checked={removeMissing} onChange={(e) => { setRemoveMissing(e.target.checked); setPreview(null); }} />Remove installs on these servers that are not in the file</label>
      {error && <p className="form-error" role="alert">{error}</p>}
      {preview && (
        <>
          <p role="status">{preview.added} new, {preview.changed} changed, {preview.removed} removed, {preview.unchanged} unchanged · {preview.newApplications} new applications</p>
          {changes.length > 0 && (
            <div className="import-preview">
              <table className="grid-table compact">
                <thead><tr><th>Application</th><th>Server</th><th>Change</th><th>Version</th></tr></thead>
                <tbody>{changes.slice(0, 500).map((c, i) => <tr key={i}><td>{c.application}</td><td>{c.server}</td><td>{c.kind}</td><td>{[c.from, c.to].filter(Boolean).join(" → ")}</td></tr>)}</tbody>
              </table>
            </div>
          )}
        </>
      )}
      <div className="dlg-actions">
        <button type="button" className="tb" onClick={onClose}>Cancel</button>
        <button type="button" className="tb" disabled={busy || !csv} onClick={() => void run(false)}>Preview</button>
        <button type="button" className="tb primary" disabled={busy || !preview} onClick={() => void run(true)}>Apply</button>
      </div>
    </dialog>
  );
}

function AppDetail({ id, onList, onOpenPage, onHome, onGone }: { id: string; onList: () => void; onOpenPage: (id: string) => void; onHome: () => void; onGone: () => void }) {
  const dialogs = useDialogs();
  const [app, setApp] = useState<InvApp | null>(null);
  const [editing, setEditing] = useState(false);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [server, setServer] = useState("");
  const [version, setVersion] = useState("");
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api.get<InvApp>(`/inventory/apps/${id}`).then(setApp).catch(() => setError("Not found.")), [id]);
  useEffect(() => { void load(); }, [load]);

  async function setInstall(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try { setApp(await api.put<InvApp>(`/inventory/apps/${id}/installs`, { server: server.trim(), version: version.trim() })); setServer(""); setVersion(""); }
    catch (err) { setError(problem(err, "Not saved.")); }
  }
  async function removeInstall(installId: string, name: string) {
    if (!await dialogs.confirm({ title: `Remove from ${name}?`, ok: "Remove", danger: true })) return;
    try { await api.del(`/inventory/installs/${installId}`); await load(); } catch (err) { setError(problem(err, "Not removed.")); }
  }
  async function remove() {
    if (!app || !await dialogs.confirm({ title: `Delete “${app.name}”?`, ok: "Delete", danger: true })) return;
    try { await api.del(`/inventory/apps/${id}`); onGone(); } catch (err) { setError(problem(err, "Not deleted.")); }
  }

  if (!app) return <main className="doc wide"><div className="col"><Crumbs onHome={onHome} onList={onList} />{error && <p className="form-error" role="alert">{error}</p>}</div></main>;
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} app={app.name} onList={onList} />
          <div className="titlebar">
            <h1>{app.name}</h1>
            {app.canEdit && (
              <div className="ptools">
                <button className="tb" onClick={() => setEditing(true)}>Edit</button>
                <MenuButton label="More" iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
                  { label: "Delete application", icon: <Trash2 className="ic" aria-hidden />, onClick: () => void remove(), danger: true },
                ]} />
              </div>
            )}
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          <div className="form">
            <span className="lbl">Vendor</span><span>{app.vendor ?? ""}</span>
            <span className="lbl">Owner</span><span>{app.owner?.name ?? ""}</span>
            <span className="lbl top">Notes</span><span className="pre">{app.notes ?? ""}</span>
          </div>
          <h2 className="s">Servers</h2>
          <table className="grid-table">
            <thead><tr><th>Server</th><th>Version</th><th>Changed</th><th>By</th><th /></tr></thead>
            <tbody>
              {app.installs.map((i) => (
                <InstallRows key={i.id} install={i} open={!!open[i.id]} onToggle={() => setOpen((o) => ({ ...o, [i.id]: !o[i.id] }))}
                  onRemove={app.canEdit ? () => void removeInstall(i.id, i.server) : undefined} />
              ))}
              {app.installs.length === 0 && <tr><td colSpan={5} className="muted">No servers.</td></tr>}
            </tbody>
          </table>
          {app.canEdit && (
            <form className="actions member-add" onSubmit={setInstall}>
              <input type="text" aria-label="Server" value={server} maxLength={200} onChange={(e) => setServer(e.target.value)} />
              <input type="text" aria-label="Version" value={version} maxLength={100} onChange={(e) => setVersion(e.target.value)} />
              <button type="submit" className="tb primary" disabled={!server.trim() || !version.trim()}>Set version</button>
            </form>
          )}
          <h2 className="s">Pages</h2>
          {app.pages.length === 0 ? <p className="muted">None</p> : (
            <ul className="plain-list">
              {app.pages.map((p) => (
                <li key={p.id}><button type="button" className="link" onClick={() => onOpenPage(p.id)}>{p.title}</button>
                  <span className="muted small"> · {p.spaceName}{p.servers.length ? ` · ${p.servers.join(", ")}` : ""}</span></li>
              ))}
            </ul>
          )}
          {editing && <AppDialog app={app} onClose={() => setEditing(false)} onSaved={(a) => { setApp(a); setEditing(false); }} />}
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Servers</h3><div>{app.installs.length}</div>
        <h3>Pages</h3><div>{app.pages.length}</div>
      </aside>
    </>
  );
}

function InstallRows({ install: i, open, onToggle, onRemove }: { install: InvApp["installs"][number]; open: boolean; onToggle: () => void; onRemove?: () => void }) {
  return (
    <>
      <tr>
        <td><button type="button" className="link" aria-expanded={open} onClick={onToggle}>{i.server}</button></td>
        <td className="mono">{i.version}</td><td className="muted">{fmtDate(i.changedAt)}</td><td className="muted">{i.changedBy}</td>
        <td>{onRemove && <button type="button" className="tb" onClick={onRemove}>Remove</button>}</td>
      </tr>
      {open && i.history.map((h, n) => (
        <tr key={n} className="history-row">
          <td className="muted small">{h.kind}</td><td className="mono small">{[h.from, h.to].filter(Boolean).join(" → ")}</td>
          <td className="muted small">{fmt(h.at)}</td><td className="muted small" colSpan={2}>{h.by}</td>
        </tr>
      ))}
    </>
  );
}

function AppDialog({ app, onClose, onSaved }: { app: InvApp; onClose: () => void; onSaved: (a: InvApp) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [form, setForm] = useState({ name: app.name, vendor: app.vendor ?? "", notes: app.notes ?? "" });
  const [owner, setOwner] = useState<OwnerRef | null>(app.owner);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { ref.current?.showModal(); }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try { onSaved(await api.put<InvApp>(`/inventory/apps/${app.id}`, { ...form, ownerType: owner?.type ?? null, ownerId: owner?.id ?? null })); }
    catch (err) { setError(problem(err, "Not saved.")); }
  }
  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="app-title">
      <h2 id="app-title">{app.name}</h2>
      <form onSubmit={submit}>
        <label>Name<input value={form.name} maxLength={200} onChange={(e) => setForm({ ...form, name: e.target.value })} required /></label>
        <label>Vendor<input value={form.vendor} maxLength={200} onChange={(e) => setForm({ ...form, vendor: e.target.value })} /></label>
        <div className="field"><span className="field-label">Owner</span><PrincipalPicker label="Owner" value={owner} onChange={setOwner} /></div>
        <label>Notes<textarea value={form.notes} maxLength={4000} onChange={(e) => setForm({ ...form, notes: e.target.value })} /></label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose}>Cancel</button>
          <button type="submit" className="tb primary" disabled={!form.name.trim()}>Save</button>
        </div>
      </form>
    </dialog>
  );
}
