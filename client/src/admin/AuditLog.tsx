import { useEffect, useState } from "react";
import { api, ApiError, type AdminUser } from "../api";

export type AuditRow = { sequence: number; at: string; action: string; actor: string | null; targetType: string | null; targetId: string | null; via: "Web" | "API"; token: string | null };
type TokenRow = { id: string; name: string; owner: string };
type Filter = { action: string; actor: string; token: string; from: string; to: string };

const SIZES = [10, 25, 50, 100] as const;
const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const NONE: Filter = { action: "", actor: "", token: "", from: "", to: "" };

/** The query for one page of the log: only the filters that are set. */
export function auditQuery(f: Filter, take: number, offset: number) {
  const p = new URLSearchParams({ take: String(take), offset: String(offset) });
  if (f.action.trim()) p.set("action", f.action.trim());
  if (f.actor) p.set("actor", f.actor);
  if (f.token) p.set("token", f.token);
  if (f.from) p.set("from", f.from);
  if (f.to) p.set("to", f.to);
  return `/admin/audit?${p}`;
}

/** Administration › Security › Audit log: a page at a time, filtered by action, person, API token and dates. */
export function AuditLog({ reloadKey = 0 }: { reloadKey?: number }) {
  const [rows, setRows] = useState<AuditRow[]>([]);
  const [filter, setFilter] = useState<Filter>(NONE);
  const [size, setSize] = useState<(typeof SIZES)[number]>(25);
  const [offset, setOffset] = useState(0);
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [tokens, setTokens] = useState<TokenRow[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void api.get<AdminUser[]>("/admin/users").then(setUsers).catch(() => setUsers([]));
    void api.get<TokenRow[]>("/admin/tokens").then(setTokens).catch(() => setTokens([]));
  }, []);
  useEffect(() => {
    let live = true;
    setError(null);
    // One more than a page tells whether there is a next one.
    api.get<AuditRow[]>(auditQuery(filter, size + 1, offset))
      .then((r) => { if (live) setRows(r); })
      .catch((e) => { if (live) { setRows([]); setError(e instanceof ApiError && e.status === 400 ? "From is after To." : "Not loaded."); } });
    return () => { live = false; };
  }, [filter, size, offset, reloadKey]);

  const set = <K extends keyof Filter>(k: K, v: Filter[K]) => { setFilter((f) => ({ ...f, [k]: v })); setOffset(0); };
  const shown = rows.slice(0, size);

  return (
    <>
      <div className="audit-filters">
        <label>Action<input type="text" value={filter.action} maxLength={100} onChange={(e) => set("action", e.target.value)} /></label>
        <label>Person
          <select className="sel" value={filter.actor} onChange={(e) => set("actor", e.target.value)}>
            <option value="">Everyone</option>
            {users.map((u) => <option key={u.id} value={u.id}>{u.displayName}</option>)}
          </select>
        </label>
        <label>API token
          <select className="sel" value={filter.token} onChange={(e) => set("token", e.target.value)}>
            <option value="">Any</option>
            {tokens.map((t) => <option key={t.id} value={t.id}>{t.name} · {t.owner}</option>)}
          </select>
        </label>
        <label>From<input type="date" value={filter.from} onChange={(e) => set("from", e.target.value)} /></label>
        <label>To<input type="date" value={filter.to} onChange={(e) => set("to", e.target.value)} /></label>
        <label>Per page
          <select className="sel" value={size} onChange={(e) => { setSize(Number(e.target.value) as (typeof SIZES)[number]); setOffset(0); }}>
            {SIZES.map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
      </div>
      {error && <p className="form-error" role="alert">{error}</p>}
      <table className="grid-table">
        <thead><tr><th>#</th><th>Time</th><th>Action</th><th>By</th><th>Via</th><th>API token</th><th>Target</th></tr></thead>
        <tbody>
          {shown.map((a) => (
            <tr key={a.sequence}>
              <td className="muted">{a.sequence}</td><td className="muted">{fmt(a.at)}</td><td className="mono">{a.action}</td>
              <td>{a.actor ?? "System"}</td><td>{a.via}</td><td>{a.token ?? ""}</td>
              <td className="muted mono">{a.targetType ? `${a.targetType} ${a.targetId ?? ""}` : ""}</td>
            </tr>
          ))}
          {shown.length === 0 && <tr><td colSpan={7} className="muted">No entries.</td></tr>}
        </tbody>
      </table>
      <div className="bar-row">
        <button className="tb" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - size))}>Previous</button>
        <span className="muted small">{shown.length ? `${offset + 1}–${offset + shown.length}` : ""}</span>
        <button className="tb" disabled={rows.length <= size} onClick={() => setOffset(offset + size)}>Next</button>
      </div>
    </>
  );
}
