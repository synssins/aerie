import { useCallback, useEffect, useState } from "react";
import { api, ApiError, ownerGrade, type Principal, type RoleAssignment, type RoleDef } from "../api";
const problem = (e: unknown, fallback: string) => (e instanceof ApiError && e.status === 403 ? "Not allowed." : (e instanceof ApiError && (e.body?.detail || e.body?.error)) || fallback);

/**
 * A space's Members tab: who holds which role, people and groups. An Admin gives and changes every role; an Owner gives the
 * roles that do not manage members and settings, and a row with such a role (its own too) is an Admin's to change.
 */
export function SpaceMembers({ spaceId, isAdmin }: { spaceId: string; isAdmin: boolean }) {
  const [rows, setRows] = useState<RoleAssignment[]>([]);
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<Principal[]>([]);
  const [pick, setPick] = useState<Principal | null>(null);
  const [all, setAll] = useState<RoleDef[]>([]);
  const [role, setRole] = useState("");
  const [error, setError] = useState<string | null>(null);
  const roles = isAdmin ? all : all.filter((r) => !ownerGrade(r));
  const locked = (r: RoleAssignment) => !isAdmin && ownerGrade(all.find((x) => x.id === r.roleId));
  useEffect(() => { void api.get<RoleDef[]>("/roles").then((list) => { setAll(list); setRole((cur) => cur || list[0]?.id || ""); }).catch(() => setAll([])); }, []);

  const load = useCallback(async () => { setRows(await api.get<RoleAssignment[]>(`/spaces/${spaceId}/roles`)); }, [spaceId]);
  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    const q = query.trim();
    if (!q || pick?.name === q) { setFound([]); return; }
    const t = setTimeout(() => void api.get<Principal[]>(`/principals?q=${encodeURIComponent(q)}`).then(setFound).catch(() => setFound([])), 200);
    return () => clearTimeout(t);
  }, [query, pick]);

  async function grant(type: "User" | "Group", id: string, roleId: string) {
    setError(null);
    try { await api.put(`/spaces/${spaceId}/roles`, { principalType: type, principalId: id, roleId }); await load(); return true; }
    catch (e) { setError(problem(e, "Not saved.")); return false; }
  }
  async function add() {
    if (pick && role && await grant(pick.type, pick.id, role)) { setPick(null); setQuery(""); }
  }
  async function remove(r: RoleAssignment) {
    setError(null);
    try { await api.del(`/spaces/${spaceId}/roles/${r.id}`); await load(); }
    catch (e) { setError(problem(e, "Not removed.")); }
  }

  return (
    <>
      <div className="actions member-add">
        <label className="lbl" htmlFor="mb-who">Add</label>
        <div className="menu-wrap">
          <input id="mb-who" type="text" autoComplete="off" value={query} maxLength={100} onChange={(e) => { setQuery(e.target.value); setPick(null); }} />
          {found.length > 0 && (
            <div className="menu" role="listbox" aria-label="Matches">
              {found.map((p) => (
                <button key={`${p.type}:${p.id}`} type="button" role="option" aria-selected={false} onClick={() => { setPick(p); setQuery(p.name); setFound([]); }}>
                  <span className="menu-two">{p.name}<small>{p.type === "Group" ? `Group · ${p.detail}` : p.detail}</small></span>
                </button>
              ))}
            </div>
          )}
        </div>
        <select className="sel" aria-label="Role" value={role} onChange={(e) => setRole(e.target.value)}>
          {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
        <button type="button" className="tb primary" disabled={!pick} onClick={() => void add()}>Add</button>
      </div>
      {error && <p className="form-error" role="alert">{error}</p>}
      <table className="grid-table">
        <thead><tr><th>Name</th><th>Type</th><th>Role</th><th /></tr></thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td>{r.name ?? r.principalId}</td>
              <td className="muted">{r.principalType === "Group" ? "Group" : "Person"}</td>
              <td>
                {locked(r) ? r.roleName ?? r.role : (
                  <select className="sel" aria-label={`Role of ${r.name ?? r.principalId}`} value={r.roleId} onChange={(e) => void grant(r.principalType, r.principalId, e.target.value)}>
                    {roles.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
                  </select>
                )}
              </td>
              <td>{!locked(r) && <button type="button" className="tb" onClick={() => void remove(r)}>Remove</button>}</td>
            </tr>
          ))}
          {rows.length === 0 && <tr><td colSpan={4} className="muted">No members.</td></tr>}
        </tbody>
      </table>
    </>
  );
}
