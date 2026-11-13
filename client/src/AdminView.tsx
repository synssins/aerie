import { useCallback, useEffect, useState, type FormEvent } from "react";
import { ArrowLeft, ChevronRight, FolderTree, HardDrive, House, KeyRound, Lock, Logs, Palette, Paperclip, Plug, Plus, ScrollText, Search, Trash2, Users } from "lucide-react";
import { BinTable } from "./Views";
import { BrandingPanel } from "./Branding";
import { LogShipping } from "./admin/LogShipping";
import { Storage } from "./admin/Storage";
import { TokensPanel } from "./Tokens";
import { api, ApiError, fmtSize, type Brand, type Space, type TreeNode } from "./api";

export type AdminSection = "spaces" | "users" | "branding" | "search" | "uploads" | "storage" | "tokens" | "logs" | "bin" | "plugins" | "audit";
export const ADMIN_SECTIONS: AdminSection[] = ["spaces", "users", "branding", "search", "uploads", "storage", "tokens", "logs", "bin", "plugins", "audit"];
type UploadSettings = { attachmentMb: number; serverLimitBytes: number | null };

type AdminUser = { id: string; userName: string; displayName: string; source: string; isAdmin: boolean; isBreakGlass: boolean; createdAt: string; deactivatedAt: string | null; canAnnounce: boolean };
type Plugin = { id: string; version: string; publisher: string; origin: string; enabled: boolean; extensionPoints: string[]; outboundHosts: string[] };
type Refused = { path: string; reason: string };
type AuditRow = { sequence: number; at: string; action: string; actor: string | null; targetType: string | null; targetId: string | null };
type Picked = { kind: "user"; item: AdminUser } | { kind: "plugin"; item: Plugin } | null;

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const fmtDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { dateStyle: "medium" });

/**
 * Administration (admins: everything) and Manage (space Owners: their spaces only).
 * Left: sections, Spaces expands to its spaces. Center: the section or space. Right: the selected item's metadata.
 */
export function AdminView({ isAdmin, section, spaceId, mySpaces, onNavigate, onExit, onSpacesChanged, onHome, brand, onBrandChanged }: {
  isAdmin: boolean;
  section: AdminSection;
  spaceId: string | null;
  mySpaces: Space[];
  onNavigate: (section: AdminSection, spaceId?: string) => void;
  onExit: () => void;
  onSpacesChanged: () => Promise<unknown>;
  onHome: () => void;
  brand: Brand;
  onBrandChanged: () => Promise<void>;
}) {
  const [allSpaces, setAllSpaces] = useState<Space[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({ spaces: true, appearance: section === "branding" });
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [plugins, setPlugins] = useState<{ loaded: Plugin[]; refused: Refused[] }>({ loaded: [], refused: [] });
  const [audit, setAudit] = useState<AuditRow[]>([]);
  const [verify, setVerify] = useState<string | null>(null);
  const [picked, setPicked] = useState<Picked>(null);
  const [newSpace, setNewSpace] = useState(false);
  const [uploads, setUploads] = useState<UploadSettings | null>(null);

  const loadSpaces = useCallback(async () => { if (isAdmin) setAllSpaces(await api.get<Space[]>("/admin/spaces")); }, [isAdmin]);
  useEffect(() => { void loadSpaces(); }, [loadSpaces]);

  const spaces = isAdmin ? allSpaces : mySpaces.filter((s) => s.myRole === "Owner");
  const space = spaceId ? spaces.find((s) => s.id === spaceId) ?? null : null;

  useEffect(() => {
    setPicked(null);
    if (!isAdmin) return;
    if (section === "users") void api.get<AdminUser[]>("/admin/users").then(setUsers);
    if (section === "uploads") void api.get<UploadSettings>("/admin/uploads").then(setUploads);
    if (section === "plugins") void api.get<{ loaded: Plugin[]; refused: Refused[] }>("/admin/plugins").then(setPlugins);
    if (section === "audit") { setVerify(null); void api.get<AuditRow[]>("/admin/audit?take=200").then(setAudit); }
  }, [section, isAdmin]);

  async function changed() { await loadSpaces(); await onSpacesChanged(); }

  async function runVerify() {
    const r = await api.get<{ valid: boolean; firstBrokenSequence: number | null }>("/admin/audit/verify");
    setVerify(r.valid ? "Chain intact." : `Chain broken at entry ${r.firstBrokenSequence}.`);
  }

  const title = space?.name ?? ({ spaces: "Spaces", users: "Users", branding: "Branding", search: "Search", uploads: "Uploads", storage: "Storage", tokens: "API tokens", logs: "Log shipping", bin: "Recycle bin", plugins: "Plugins", audit: "Audit log" } as const)[section];

  async function setAnnouncer(u: AdminUser, enabled: boolean) {
    await api.put(`/admin/users/${u.id}/announcer`, { enabled });
    setUsers((all) => all.map((x) => (x.id === u.id ? { ...x, canAnnounce: enabled } : x)));
  }
  const area = isAdmin ? "Administration" : "Manage";

  return (
    <>
      <nav className="tree" aria-label={area}>
        <button className="node" onClick={onExit}><ArrowLeft className="ic" aria-hidden /><span className="grow">Back to spaces</span></button>
        <div className="tree-head admin-head">{area}</div>
        <div>
          <button className={`node ${section === "spaces" && !space ? "cur" : ""}`} aria-expanded={!!open.spaces}
            onClick={() => {
              // Already on the Spaces list: the click collapses or expands; elsewhere it opens the list.
              if (section === "spaces" && !space) setOpen((o) => ({ ...o, spaces: !o.spaces }));
              else { setOpen((o) => ({ ...o, spaces: true })); onNavigate("spaces"); }
            }}>
            <FolderTree className="ic" aria-hidden /><span className="grow">Spaces</span>
          </button>
          {open.spaces && (
            <div className="kids sub">
              {spaces.map((s) => (
                <button key={s.id} className={`node ${space?.id === s.id ? "cur" : ""}`} onClick={() => onNavigate("spaces", s.id)}>
                  <span className="grow">{s.name}</span>{s.isSensitive && <Lock className="ic sm muted" aria-label="Sensitive" />}
                </button>
              ))}
            </div>
          )}
        </div>
        {isAdmin && (
          <>
            <button className={`node ${section === "users" ? "cur" : ""}`} onClick={() => onNavigate("users")}><Users className="ic" aria-hidden /><span className="grow">Users</span></button>
            <div>
              <button className="node" aria-expanded={!!open.appearance} onClick={() => setOpen((o) => ({ ...o, appearance: !o.appearance }))}>
                <Palette className="ic" aria-hidden /><span className="grow">Appearance</span>
              </button>
              {open.appearance && (
                <div className="kids sub">
                  <button className={`node ${section === "branding" ? "cur" : ""}`} onClick={() => onNavigate("branding")}><span className="grow">Branding</span></button>
                </div>
              )}
            </div>
            <button className={`node ${section === "search" ? "cur" : ""}`} onClick={() => onNavigate("search")}><Search className="ic" aria-hidden /><span className="grow">Search</span></button>
            <button className={`node ${section === "uploads" ? "cur" : ""}`} onClick={() => onNavigate("uploads")}><Paperclip className="ic" aria-hidden /><span className="grow">Uploads</span></button>
            <button className={`node ${section === "storage" ? "cur" : ""}`} onClick={() => onNavigate("storage")}><HardDrive className="ic" aria-hidden /><span className="grow">Storage</span></button>
            <button className={`node ${section === "tokens" ? "cur" : ""}`} onClick={() => onNavigate("tokens")}><KeyRound className="ic" aria-hidden /><span className="grow">API tokens</span></button>
            <button className={`node ${section === "bin" ? "cur" : ""}`} onClick={() => onNavigate("bin")}><Trash2 className="ic" aria-hidden /><span className="grow">Recycle bin</span></button>
            <button className={`node ${section === "plugins" ? "cur" : ""}`} onClick={() => onNavigate("plugins")}><Plug className="ic" aria-hidden /><span className="grow">Plugins</span></button>
            <button className={`node ${section === "audit" ? "cur" : ""}`} onClick={() => onNavigate("audit")}><ScrollText className="ic" aria-hidden /><span className="grow">Audit log</span></button>
            <button className={`node ${section === "logs" ? "cur" : ""}`} onClick={() => onNavigate("logs")}><Logs className="ic" aria-hidden /><span className="grow">Log shipping</span></button>
          </>
        )}
      </nav>

      {space ? (
        <SpaceEditor key={space.id} space={space} isAdmin={isAdmin} area={area} onBack={() => onNavigate("spaces")} onSaved={changed} onHome={onHome} />
      ) : section === "logs" && isAdmin ? (
        <LogShipping area={area} targetId={spaceId} onSelect={(id) => onNavigate("logs", id ?? undefined)} onHome={onHome} />
      ) : section === "storage" && isAdmin ? (
        <Storage area={area} placeId={spaceId} onSelect={(id) => onNavigate("storage", id ?? undefined)} onHome={onHome} />
      ) : (
        <>
          <main className="doc wide">
            <div className="col">
              <nav className="crumb" aria-label="Breadcrumb"><button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>{section === "branding" && <><ChevronRight className="ic sm" aria-hidden /><span>Appearance</span></>}</nav>
              <div className="titlebar">
                <h1>{title}</h1>
                <div className="ptools">
                  {section === "spaces" && isAdmin && <button className="tb primary" onClick={() => setNewSpace(true)}><Plus className="ic" aria-hidden />New space</button>}
                  {section === "audit" && <button className="tb" onClick={() => void runVerify()}>Verify chain</button>}
                </div>
              </div>
              {verify && <p className="muted" role="status">{verify}</p>}

              {section === "spaces" && (
                <table className="grid-table">
                  <thead><tr><th>Name</th><th>Description</th><th>Created</th></tr></thead>
                  <tbody>
                    {spaces.map((s) => (
                      <tr key={s.id} className="row" onClick={() => onNavigate("spaces", s.id)}>
                        <td>{s.name}{s.isSensitive && <Lock className="ic sm muted" aria-label="Sensitive" />}</td>
                        <td className="muted">{s.description}</td>
                        <td className="muted">{fmtDate(s.createdAt)}</td>
                      </tr>
                    ))}
                    {spaces.length === 0 && <tr><td colSpan={3} className="muted">No spaces.</td></tr>}
                  </tbody>
                </table>
              )}

              {section === "branding" && isAdmin && <BrandingPanel brand={brand} onChanged={onBrandChanged} />}
              {section === "search" && isAdmin && <SearchPanel />}
              {section === "tokens" && isAdmin && <TokensPanel admin spaces={allSpaces} />}
              {section === "uploads" && isAdmin && uploads && <UploadsPanel key={uploads.attachmentMb} settings={uploads} onSaved={setUploads} />}
              {section === "bin" && isAdmin && <BinTable spaceId={null} canEmpty={false} onRestored={() => void onSpacesChanged()} />}

              {section === "users" && isAdmin && (
                <table className="grid-table">
                  <thead><tr><th>Name</th><th>User name</th><th>Source</th><th>Role</th><th>Announcements</th><th>Created</th></tr></thead>
                  <tbody>
                    {users.map((u) => (
                      <tr key={u.id} className={`row ${picked?.kind === "user" && picked.item.id === u.id ? "sel" : ""} ${u.deactivatedAt ? "off" : ""}`} onClick={() => setPicked({ kind: "user", item: u })}>
                        <td>{u.displayName}</td><td className="muted">{u.userName}</td><td className="muted">{u.source}</td>
                        <td>{u.isBreakGlass ? "Break-glass" : u.isAdmin ? "Administrator" : "User"}</td>
                        <td onClick={(e) => e.stopPropagation()}>
                          <input type="checkbox" className="chk" aria-label={`${u.displayName} can post announcements`} checked={u.isAdmin || u.canAnnounce} disabled={u.isAdmin} onChange={(e) => void setAnnouncer(u, e.target.checked)} />
                        </td>
                        <td className="muted">{fmt(u.createdAt)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}

              {section === "plugins" && isAdmin && (
                <>
                  <table className="grid-table">
                    <thead><tr><th>Plugin</th><th>Version</th><th>Publisher</th><th>Origin</th><th>Status</th></tr></thead>
                    <tbody>
                      {plugins.loaded.map((p) => (
                        <tr key={p.id} className={`row ${picked?.kind === "plugin" && picked.item.id === p.id ? "sel" : ""}`} onClick={() => setPicked({ kind: "plugin", item: p })}>
                          <td className="mono">{p.id}</td><td>{p.version}</td><td className="muted">{p.publisher}</td><td className="muted">{p.origin}</td><td>{p.enabled ? "Enabled" : "Disabled"}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {plugins.refused.length > 0 && (
                    <>
                      <h2 className="s">Refused</h2>
                      <table className="grid-table">
                        <thead><tr><th>Path</th><th>Reason</th></tr></thead>
                        <tbody>{plugins.refused.map((r) => <tr key={r.path}><td className="mono">{r.path}</td><td>{r.reason}</td></tr>)}</tbody>
                      </table>
                    </>
                  )}
                </>
              )}

              {section === "audit" && isAdmin && (
                <table className="grid-table">
                  <thead><tr><th>#</th><th>Time</th><th>Action</th><th>By</th><th>Target</th></tr></thead>
                  <tbody>
                    {audit.map((a) => (
                      <tr key={a.sequence}>
                        <td className="muted">{a.sequence}</td><td className="muted">{fmt(a.at)}</td><td className="mono">{a.action}</td>
                        <td>{a.actor ?? "System"}</td><td className="muted mono">{a.targetType ? `${a.targetType} ${a.targetId ?? ""}` : ""}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </main>

          <aside className="side" aria-label="Details">
            {picked?.kind === "user" && (
              <>
                <h3>User</h3><div>{picked.item.displayName}</div><div className="muted small">{picked.item.userName}</div>
                <h3>Source</h3><div>{picked.item.source}</div>
                <h3>Role</h3><div>{picked.item.isBreakGlass ? "Break-glass" : picked.item.isAdmin ? "Administrator" : "User"}</div>
                <h3>Created</h3><div>{fmt(picked.item.createdAt)}</div>
                {picked.item.deactivatedAt && <><h3>Deactivated</h3><div>{fmt(picked.item.deactivatedAt)}</div></>}
              </>
            )}
            {section === "uploads" && uploads && (
              <>
                <h3>Web server limit</h3>
                <div>{uploads.serverLimitBytes === null ? "None" : fmtSize(uploads.serverLimitBytes)}</div>
                <h3>Image limit</h3>
                <div>20 MB</div>
              </>
            )}
            {picked?.kind === "plugin" && (
              <>
                <h3>Plugin</h3><div className="mono">{picked.item.id}</div>
                <h3>Extension points</h3>{picked.item.extensionPoints.map((x) => <div key={x}>{x}</div>)}
                <h3>Outbound hosts</h3>
                {picked.item.outboundHosts.length ? picked.item.outboundHosts.map((h) => <div key={h} className="mono">{h}</div>) : <div className="muted">None</div>}
              </>
            )}
          </aside>
        </>
      )}

      {newSpace && <NewSpaceDialog onClose={() => setNewSpace(false)} onCreated={async (s) => { setNewSpace(false); await changed(); onNavigate("spaces", s.id); }} />}
    </>
  );
}

/** Administration › Uploads: the app's own attachment size limit. The web server's limit, when lower, still wins. */
function UploadsPanel({ settings, onSaved }: { settings: UploadSettings; onSaved: (s: UploadSettings) => void }) {
  const [mb, setMb] = useState(String(settings.attachmentMb));
  const [status, setStatus] = useState<string | null>(null);
  const n = Number(mb);
  const valid = Number.isInteger(n) && n >= 1 && n <= 4096;
  const over = valid && settings.serverLimitBytes !== null && n * 1048576 > settings.serverLimitBytes;

  async function save(e: FormEvent) {
    e.preventDefault();
    try { onSaved(await api.put<UploadSettings>("/admin/uploads", { attachmentMb: n })); setStatus("Saved."); } catch { setStatus("Not saved."); }
  }

  return (
    <form className="form" onSubmit={save}>
      <label className="lbl" htmlFor="att-mb">Attachment size limit</label>
      <div className="unit">
        <input id="att-mb" type="number" min={1} max={4096} step={1} value={mb} onChange={(e) => { setMb(e.target.value); setStatus(null); }} />
        <span>MB</span>
      </div>
      {over && <p className="form-error wide" role="alert">Above the web server limit of {fmtSize(settings.serverLimitBytes!)}.</p>}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={!valid || n === settings.attachmentMb}>Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

/** Administration › Search: synonym groups and a rebuild of the index. */
function SearchPanel() {
  const [text, setText] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  useEffect(() => { void api.get<{ text: string }>("/admin/search/synonyms").then((r) => setText(r.text)); }, []);

  async function save(e: FormEvent) {
    e.preventDefault();
    try { await api.put("/admin/search/synonyms", { text }); setStatus("Saved."); } catch { setStatus("Not saved."); }
  }

  async function rebuild() {
    setStatus("Rebuilding…");
    try { const r = await api.post<{ pages: number }>("/admin/search/reindex"); setStatus(`${r.pages} pages indexed.`); } catch { setStatus("Not rebuilt."); }
  }

  if (text === null) return null;
  return (
    <form className="form" onSubmit={save}>
      <label className="lbl top" htmlFor="syn">Synonyms</label>
      <textarea id="syn" className="mono" rows={12} spellCheck={false} value={text} placeholder="password, pw, passcode" onChange={(e) => { setText(e.target.value); setStatus(null); }} />
      <div className="actions">
        <button type="submit" className="tb primary">Save</button>
        <button type="button" className="tb" onClick={() => void rebuild()}>Rebuild index</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

function SpaceEditor({ space, isAdmin, area, onBack, onSaved, onHome }: { space: Space; isAdmin: boolean; area: string; onBack: () => void; onSaved: () => Promise<void>; onHome: () => void }) {
  const [tab, setTab] = useState<"details" | "settings">("details");
  const [form, setForm] = useState(space);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [counts, setCounts] = useState<{ folders: number; pages: number } | null>(null);

  useEffect(() => {
    api.get<TreeNode[]>(`/spaces/${space.id}/tree`)
      .then((t) => setCounts({ folders: t.filter((n) => n.kind === "Folder").length, pages: t.filter((n) => n.kind === "Page").length }))
      .catch(() => setCounts(null)); // admins have no content access to sensitive spaces
  }, [space.id]);

  const set = <K extends keyof Space>(k: K, v: Space[K]) => { setForm((f) => ({ ...f, [k]: v })); setStatus(null); };
  const dirty = JSON.stringify(form) !== JSON.stringify(space);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    const body = tab === "details"
      ? { description: form.description ?? "", ...(isAdmin ? { name: form.name } : {}) }
      : {
          commentsEnabled: form.commentsEnabled, agingDays: form.agingDays, agingAudience: form.agingAudience,
          recycleDays: form.recycleDays ?? undefined, ...(isAdmin ? { isSensitive: form.isSensitive } : {}),
        };
    try {
      await api.patch(isAdmin ? `/admin/spaces/${space.id}` : `/spaces/${space.id}`, body);
      await onSaved();
      setStatus("Saved.");
    } catch (err) {
      setStatus(err instanceof ApiError && err.status === 400 ? "Days must be 1-3650." : "Not saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span><ChevronRight className="ic sm" aria-hidden />
            <button className="crumb-link" onClick={onBack}>Spaces</button><ChevronRight className="ic sm" aria-hidden /><b>{space.name}</b>
          </nav>
          <div className="titlebar"><h1>{space.name}</h1></div>
          <div className="tabs" role="tablist">
            <button role="tab" aria-selected={tab === "details"} className={tab === "details" ? "on" : ""} onClick={() => setTab("details")}>Details</button>
            <button role="tab" aria-selected={tab === "settings"} className={tab === "settings" ? "on" : ""} onClick={() => setTab("settings")}>Space settings</button>
          </div>
          <form className="form" onSubmit={save}>
            {tab === "details" ? (
              <>
                <label className="lbl" htmlFor="sp-name">Name</label>
                <input id="sp-name" type="text" value={form.name} readOnly={!isAdmin} maxLength={200} onChange={(e) => set("name", e.target.value)} />
                <label className="lbl" htmlFor="sp-desc">Description</label>
                <textarea id="sp-desc" value={form.description ?? ""} maxLength={500} onChange={(e) => set("description", e.target.value)} />
              </>
            ) : (
              <>
                <span className="lbl">Comments</span>
                <button type="button" role="switch" aria-checked={form.commentsEnabled} aria-label="Comments" className={`sw ${form.commentsEnabled ? "on" : ""}`} onClick={() => set("commentsEnabled", !form.commentsEnabled)} />
                <label className="lbl" htmlFor="sp-aging">Aging limit</label>
                <span className="days"><input id="sp-aging" type="number" min={1} max={3650} value={form.agingDays} onChange={(e) => set("agingDays", Number(e.target.value))} />days</span>
                <span className="lbl">Aging notice shown to</span>
                <div className="seg" role="radiogroup" aria-label="Aging notice shown to">
                  {([["Everyone", "Everyone"], ["Supervisors", "Supervisors and up"]] as const).map(([v, t]) => (
                    <button key={v} type="button" role="radio" aria-checked={form.agingAudience === v} className={form.agingAudience === v ? "on" : ""} onClick={() => set("agingAudience", v)}>{t}</button>
                  ))}
                </div>
                <label className="lbl" htmlFor="sp-recycle">Recycle bin retention</label>
                <span className="days"><input id="sp-recycle" type="number" min={1} max={3650} placeholder="90" value={form.recycleDays ?? ""} onChange={(e) => set("recycleDays", e.target.value ? Number(e.target.value) : null)} />days</span>
                {isAdmin && (
                  <>
                    <span className="lbl">Sensitive space</span>
                    <button type="button" role="switch" aria-checked={form.isSensitive} aria-label="Sensitive space" className={`sw ${form.isSensitive ? "on" : ""}`} onClick={() => set("isSensitive", !form.isSensitive)} />
                  </>
                )}
              </>
            )}
            <div className="actions">
              <button type="submit" className="tb primary" disabled={busy || !dirty}>{busy ? "Saving…" : "Save"}</button>
              <button type="button" className="tb" disabled={busy || !dirty} onClick={() => { setForm(space); setStatus(null); }}>Cancel</button>
              {status && <span className="muted" role="status">{status}</span>}
            </div>
          </form>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Space</h3><div>{space.name}</div>
        <h3>Key</h3><div className="mono">{space.key}</div>
        {counts && <><h3>Contents</h3><div>{counts.folders} folders · {counts.pages} pages</div></>}
        <h3>Created</h3><div>{fmtDate(space.createdAt)}</div>
        {space.isSensitive && <><h3>Sensitive</h3><div>Yes</div></>}
        <h3>Your access</h3><div>{isAdmin ? "Administrator" : "Owner"}</div>
      </aside>
    </>
  );
}

function NewSpaceDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (s: Space) => Promise<void> }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!name.trim()) { setError("Name is required."); return; }
    setBusy(true);
    try { await onCreated(await api.post<Space>("/spaces", { name: name.trim(), description: description.trim() || null })); }
    catch { setError("Not created."); setBusy(false); }
  }

  return (
    <dialog ref={(d) => { if (d && !d.open) d.showModal(); }} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="ns-title">
      <form onSubmit={submit}>
        <h2 id="ns-title">New space</h2>
        <label className="field"><span className="field-label">Name</span>
          <input autoFocus value={name} maxLength={200} onChange={(e) => { setName(e.target.value); setError(null); }} />
        </label>
        <label className="field"><span className="field-label">Description</span>
          <input value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} />
        </label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="tb primary" disabled={busy}>{busy ? "Creating…" : "Create space"}</button>
        </div>
      </form>
    </dialog>
  );
}
