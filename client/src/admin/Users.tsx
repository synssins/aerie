import { fmtDateTime, type AdminUser } from "../api";

/** A global permission a loaded plugin declares: its full name ("<plugin id>:<key>") and the label for its column. */
export type PermissionColumn = { name: string; label: string };

/** Administration > Users: the table. One column per global plugin permission; an Admin holds every one. */
export function UsersTable({ users, permissions, pickedId, onPick, onAnnouncer, onPermission }: {
  users: AdminUser[]; permissions: PermissionColumn[]; pickedId: string | null;
  onPick: (u: AdminUser) => void; onAnnouncer: (u: AdminUser, enabled: boolean) => void; onPermission: (u: AdminUser, permission: string, granted: boolean) => void;
}) {
  return (
    <table className="grid-table">
      <thead>
        <tr>
          <th>Name</th><th>User name</th><th>Source</th><th>Groups</th><th>Role</th><th>Announcements</th>
          {permissions.map((p) => <th key={p.name}>{p.label}</th>)}
          <th>Created</th>
        </tr>
      </thead>
      <tbody>
        {users.map((u) => (
          <tr key={u.id} className={`row ${pickedId === u.id ? "sel" : ""} ${u.deactivatedAt ? "off" : ""}`} onClick={() => onPick(u)}>
            <td>{u.displayName}</td><td className="muted">{u.userName}</td><td className="muted">{u.source}</td>
            <td>{(u.groups ?? []).map((g) => <div key={g}>{g}</div>)}</td>
            <td>{u.isBreakGlass ? "Break-glass" : u.isAdmin ? "Administrator" : "User"}</td>
            <td onClick={(e) => e.stopPropagation()}>
              <input type="checkbox" className="chk" aria-label={`${u.displayName} can post announcements`} checked={u.isAdmin || u.canAnnounce} disabled={u.isAdmin} onChange={(e) => onAnnouncer(u, e.target.checked)} />
            </td>
            {permissions.map((p) => (
              <td key={p.name} onClick={(e) => e.stopPropagation()}>
                <input type="checkbox" className="chk" aria-label={`${u.displayName}: ${p.label}`} checked={u.isAdmin || (u.permissions ?? []).includes(p.name)} disabled={u.isAdmin}
                  onChange={(e) => onPermission(u, p.name, e.target.checked)} />
              </td>
            ))}
            <td className="muted">{fmtDateTime(u.createdAt)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
