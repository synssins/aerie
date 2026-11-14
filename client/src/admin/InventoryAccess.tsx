import { useEffect, useState } from "react";
import { ChevronRight, House } from "lucide-react";
import { api, ApiError, type InvRule, type OwnerRef, type RoleDef, type Space } from "../api";
import { PrincipalPicker } from "../Inventory";

type Access = { view: InvRule[]; edit: InvRule[] };
const KINDS = [["Everyone", "Everyone signed in"], ["Space", "Members of a space"], ["Role", "Holders of a role"], ["Person", "A person or a group"]] as const;
const label = (r: InvRule) => r.kind === "Everyone" ? "Everyone signed in"
  : `${r.kind === "Space" ? "Members of" : r.kind === "Role" ? "Holders of" : r.kind === "Group" ? "Group" : "Person"} ${r.name ?? r.id}`;

/** Administration › Security › Inventory access: who views and who edits the application inventory. Admins always do both. */
export function InventoryAccess({ area, onHome }: { area: string; onHome: () => void }) {
  const [access, setAccess] = useState<Access | null>(null);
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [roles, setRoles] = useState<RoleDef[]>([]);
  const [status, setStatus] = useState<string | null>(null);
  useEffect(() => {
    void api.get<Access>("/admin/inventory/access").then(setAccess);
    void api.get<Space[]>("/admin/spaces").then(setSpaces).catch(() => setSpaces([]));
    void api.get<RoleDef[]>("/roles").then(setRoles).catch(() => setRoles([]));
  }, []);

  async function save(next: Access) {
    setStatus(null);
    try { setAccess(await api.put<Access>("/admin/inventory/access", next)); setStatus("Saved."); }
    catch (e) { setStatus(e instanceof ApiError && e.body?.errors ? Object.values(e.body.errors).flat()[0] ?? "Not saved." : "Not saved."); }
  }

  if (!access) return null;
  return (
    <main className="doc wide">
      <div className="col">
        <nav className="crumb" aria-label="Breadcrumb">
          <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>{area}</span>
          <ChevronRight className="ic sm" aria-hidden /><span>Security</span>
        </nav>
        <div className="titlebar"><h1>Inventory access</h1></div>
        {status && <p className="muted" role="status">{status}</p>}
        {(["view", "edit"] as const).map((which) => (
          <section key={which}>
            <h2 className="s">{which === "view" ? "Can view" : "Can edit"}</h2>
            <ul className="plain-list">
              {access[which].map((r, i) => (
                <li key={`${r.kind}:${r.id}`}>{label(r)}{" "}
                  <button type="button" className="tb" onClick={() => void save({ ...access, [which]: access[which].filter((_, n) => n !== i) })}>Remove</button>
                </li>
              ))}
              {access[which].length === 0 && <li className="muted">Admins only</li>}
            </ul>
            <AddRule spaces={spaces} roles={roles} onAdd={(r) => void save({ ...access, [which]: [...access[which], r] })} which={which} />
          </section>
        ))}
      </div>
    </main>
  );
}

function AddRule({ spaces, roles, onAdd, which }: { spaces: Space[]; roles: RoleDef[]; onAdd: (r: InvRule) => void; which: string }) {
  const [kind, setKind] = useState<(typeof KINDS)[number][0]>("Everyone");
  const [target, setTarget] = useState("");
  const [who, setWho] = useState<OwnerRef | null>(null);
  const ready = kind === "Everyone" || (kind === "Person" ? !!who : !!target);
  function add() {
    if (kind === "Person" && who) onAdd({ kind: who.type, id: who.id, name: who.name });
    else if (kind !== "Person") onAdd({ kind, id: kind === "Everyone" ? null : target });
    setTarget(""); setWho(null);
  }
  return (
    <div className="actions member-add">
      <select className="sel" aria-label={`Add to ${which}`} value={kind} onChange={(e) => { setKind(e.target.value as typeof kind); setTarget(""); }}>
        {KINDS.map(([k, t]) => <option key={k} value={k}>{t}</option>)}
      </select>
      {kind === "Space" && (
        <select className="sel" aria-label="Space" value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value="" />{spaces.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        </select>
      )}
      {kind === "Role" && (
        <select className="sel" aria-label="Role" value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value="" />{roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
      )}
      {kind === "Person" && <PrincipalPicker label="Person or group" value={who} onChange={setWho} />}
      <button type="button" className="tb primary" disabled={!ready} onClick={add}>Add</button>
    </div>
  );
}
