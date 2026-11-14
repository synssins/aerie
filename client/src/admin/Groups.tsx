import { useCallback, useEffect, useState } from "react";
import { ChevronRight, Ellipsis, House, Plus, Trash2 } from "lucide-react";
import { api, ApiError, type AdminUser, type Group, type GroupMember } from "../api";
import { useDialogs } from "../Dialogs";
import { MenuButton } from "../ui";

const problem = (e: unknown, fallback: string) => (e instanceof ApiError && (e.body?.detail || e.body?.error)) || fallback;
const nameRule = (v: string) => (v.trim() ? (v.trim().length > 200 ? "200 characters at most." : null) : "Name is required.");

/**
 * Administration › Security › Groups. A local group is made and filled here. A directory group comes from its plugin and is
 * shown read-only: its members are the directory's. Center: the groups, or one group's members. Right: the group's details.
 */
export function Groups({ area, groupId, onSelect, onHome }: { area: string; groupId: string | null; onSelect: (id: string | null) => void; onHome: () => void }) {
  const dialogs = useDialogs();
  const [groups, setGroups] = useState<Group[]>([]);
  const [members, setMembers] = useState<GroupMember[]>([]);
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [adding, setAdding] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => { setGroups(await api.get<Group[]>("/admin/groups")); }, []);
  useEffect(() => { void load(); }, [load]);
  const group = groups.find((g) => g.id === groupId) ?? null;

  const loadMembers = useCallback(async () => {
    if (!groupId) return;
    setMembers(await api.get<GroupMember[]>(`/admin/groups/${groupId}/members`));
  }, [groupId]);
  useEffect(() => { setError(null); setAdding(""); setMembers([]); void loadMembers(); }, [loadMembers]);
  useEffect(() => { if (group?.isLocal) void api.get<AdminUser[]>("/admin/users").then(setUsers); }, [group?.isLocal]);

  async function create() {
    const name = await dialogs.ask({ title: "New group", label: "Name", ok: "Create", validate: nameRule });
    if (name === null) return;
    try { const g = await api.post<Group>("/admin/groups", { name: name.trim() }); await load(); onSelect(g.id); }
    catch (e) { setError(problem(e, "Not created.")); }
  }
  async function rename(g: Group) {
    const name = await dialogs.ask({ title: "Rename group", label: "Name", initial: g.name, ok: "Rename", validate: nameRule });
    if (name === null) return;
    try { await api.patch(`/admin/groups/${g.id}`, { name: name.trim() }); await load(); }
    catch (e) { setError(problem(e, "Not renamed.")); }
  }
  async function remove(g: Group) {
    if (!await dialogs.confirm({ title: `Delete ${g.name}?`, body: "Its roles in every space go with it.", ok: "Delete", danger: true })) return;
    try { await api.del(`/admin/groups/${g.id}`); onSelect(null); await load(); }
    catch (e) { setError(problem(e, "Not deleted.")); }
  }
  async function addMember() {
    if (!group || !adding) return;
    try { await api.put(`/admin/groups/${group.id}/members/${adding}`); setAdding(""); await loadMembers(); await load(); }
    catch (e) { setError(problem(e, "Not added.")); }
  }
  async function removeMember(m: GroupMember) {
    if (!group) return;
    try { await api.del(`/admin/groups/${group.id}/members/${m.userId}`); await loadMembers(); await load(); }
    catch (e) { setError(problem(e, "Not removed.")); }
  }

  const candidates = users.filter((u) => !u.isBreakGlass && !u.deactivatedAt && !members.some((m) => m.userId === u.id));

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
            <ChevronRight className="ic sm" aria-hidden /><span>Security</span>
            {group && <><ChevronRight className="ic sm" aria-hidden /><button className="crumb-link" onClick={() => onSelect(null)}>Groups</button><ChevronRight className="ic sm" aria-hidden /><b>{group.name}</b></>}
          </nav>
          <div className="titlebar">
            <h1>{group?.name ?? "Groups"}</h1>
            <div className="ptools">
              {!group && <MenuButton label="New" icon={<Plus className="ic" aria-hidden />} items={[{ label: "Local group", onClick: () => void create() }]} />}
              {group?.isLocal && (
                <MenuButton label="More" iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
                  { label: "Rename", onClick: () => void rename(group) },
                  { label: "Delete group", icon: <Trash2 className="ic" aria-hidden />, onClick: () => void remove(group), danger: true },
                ]} />
              )}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}

          {!group && (
            <table className="grid-table">
              <thead><tr><th>Name</th><th>Source</th><th>Members</th></tr></thead>
              <tbody>
                {groups.map((g) => (
                  <tr key={g.id} className="row" onClick={() => onSelect(g.id)}>
                    <td>{g.name}</td><td className="muted">{g.isLocal ? "Local" : "Directory"}</td><td className="muted">{g.members}</td>
                  </tr>
                ))}
                {groups.length === 0 && <tr><td colSpan={3} className="muted">No groups.</td></tr>}
              </tbody>
            </table>
          )}

          {group && (
            <>
              {group.isLocal && (
                <div className="actions">
                  <label className="lbl" htmlFor="gm-add">Add member</label>
                  <select id="gm-add" className="sel" value={adding} onChange={(e) => setAdding(e.target.value)}>
                    <option value="" />
                    {candidates.map((u) => <option key={u.id} value={u.id}>{u.displayName} ({u.userName})</option>)}
                  </select>
                  <button type="button" className="tb primary" disabled={!adding} onClick={() => void addMember()}>Add</button>
                </div>
              )}
              <table className="grid-table">
                <thead><tr><th>Name</th><th>User name</th><th>Source</th>{group.isLocal && <th />}</tr></thead>
                <tbody>
                  {members.map((m) => (
                    <tr key={m.userId}>
                      <td>{m.displayName}</td><td className="muted">{m.userName}</td><td className="muted">{m.source}</td>
                      {group.isLocal && <td><button type="button" className="tb" onClick={() => void removeMember(m)}>Remove</button></td>}
                    </tr>
                  ))}
                  {members.length === 0 && <tr><td colSpan={4} className="muted">No members.</td></tr>}
                </tbody>
              </table>
            </>
          )}
        </div>
      </main>
      <aside className="side" aria-label="Details">
        {group && (
          <>
            <h3>Group</h3><div>{group.name}</div>
            <h3>Source</h3><div>{group.isLocal ? "Local" : "Directory"}</div>
            <h3>Members</h3><div>{group.members}</div>
            <h3>Key</h3><div className="mono small">{group.key}</div>
          </>
        )}
      </aside>
    </>
  );
}
