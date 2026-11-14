import { useCallback, useEffect, useState } from "react";
import { ChevronRight, Ellipsis, House, Plus, Trash2 } from "lucide-react";
import { api, ApiError, PERMISSIONS, type Permission, type RoleDef } from "../api";
import { useDialogs } from "../Dialogs";
import { MenuButton } from "../ui";

const problem = (e: unknown, fallback: string) =>
  (e instanceof ApiError && (e.body?.detail || (e.body?.errors && Object.values(e.body.errors).flat()[0]))) || fallback;

/**
 * Administration › Security › Roles: one list of roles used in every space. A row per permission, a column per role; a change
 * saves at once. The built-in roles are renamed and edited, never deleted. Read is part of every role.
 */
export function Roles({ area, onHome }: { area: string; onHome: () => void }) {
  const dialogs = useDialogs();
  const [roles, setRoles] = useState<RoleDef[]>([]);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api.get<RoleDef[]>("/roles").then(setRoles), []);
  useEffect(() => { void load(); }, [load]);

  async function save(r: RoleDef, name: string, permissions: Permission[]) {
    setError(null);
    try { await api.put(`/admin/roles/${r.id}`, { name, permissions }); await load(); }
    catch (e) { setError(problem(e, "Not saved.")); }
  }

  function toggle(r: RoleDef, p: Permission, on: boolean) {
    const next = on ? [...r.permissions, p] : r.permissions.filter((x) => x !== p);
    void save(r, r.name, PERMISSIONS.map(([k]) => k).filter((k) => next.includes(k)));
  }

  async function create() {
    setError(null);
    const name = await dialogs.ask({ title: "New role", label: "Name", ok: "Add", validate: (v) => (v.trim() ? null : "Required.") });
    if (!name) return;
    try { await api.post("/admin/roles", { name: name.trim(), permissions: ["Read"] }); await load(); }
    catch (e) { setError(problem(e, "Not added.")); }
  }

  async function rename(r: RoleDef) {
    const name = await dialogs.ask({ title: `Rename “${r.name}”`, label: "Name", initial: r.name, ok: "Rename", validate: (v) => (v.trim() ? null : "Required.") });
    if (name && name.trim() !== r.name) await save(r, name.trim(), r.permissions);
  }

  async function remove(r: RoleDef) {
    setError(null);
    if (!await dialogs.confirm({ title: `Delete role “${r.name}”?`, ok: "Delete", danger: true })) return;
    try { await api.del(`/admin/roles/${r.id}`); await load(); }
    catch (e) { setError(problem(e, "Not deleted.")); }
  }

  return (
    <main className="doc wide">
      <div className="col">
        <nav className="crumb" aria-label="Breadcrumb">
          <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
          <ChevronRight className="ic sm" aria-hidden /><span>Security</span>
        </nav>
        <div className="titlebar">
          <h1>Roles</h1>
          <div className="ptools">
            <MenuButton label="New" icon={<Plus className="ic" aria-hidden />} items={[{ label: "Role", onClick: () => void create() }]} />
          </div>
        </div>
        {error && <p className="form-error" role="alert">{error}</p>}
        <table className="grid-table compact roles-table">
          <thead>
            <tr>
              <th>Permission</th>
              {roles.map((r) => (
                <th key={r.id} className="c">
                  <span className="role-head">
                    {r.name}
                    <MenuButton label={`${r.name}: more`} iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
                      { label: "Rename", onClick: () => void rename(r) },
                      ...(r.builtIn ? [] : [{ label: "Delete role", icon: <Trash2 className="ic" aria-hidden />, onClick: () => void remove(r), danger: true }]),
                    ]} />
                  </span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {PERMISSIONS.map(([key, label]) => (
              <tr key={key}>
                <td>{label}</td>
                {roles.map((r) => (
                  <td key={r.id} className="c">
                    <input type="checkbox" className="chk" aria-label={`${r.name}: ${label}`} checked={r.permissions.includes(key)} disabled={key === "Read"}
                      onChange={(e) => toggle(r, key, e.target.checked)} />
                  </td>
                ))}
              </tr>
            ))}
            <tr>
              <td className="muted">In use</td>
              {roles.map((r) => <td key={r.id} className="c muted">{r.inUse}</td>)}
            </tr>
          </tbody>
        </table>
      </div>
    </main>
  );
}
