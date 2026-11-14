import { useEffect, useState } from "react";
import { ChevronRight, House } from "lucide-react";
import { api, ApiError, type InvRule, type OwnerRef, type RoleDef, type Space } from "../api";
import { PrincipalPicker } from "../Inventory";

type Access = { view: InvRule[]; edit: InvRule[] };
type TokenRow = { id: string; name: string; owner: string; state: string };
type AddKind = "Everyone" | "Space" | "Role" | "Person" | "Token";
const KINDS: [AddKind, string][] = [["Everyone", "Everyone signed in"], ["Space", "Members of a space"], ["Role", "Holders of a role"], ["Person", "A person or group"], ["Token", "An API token"]];
const SECTIONS = [
  { which: "view", title: "Can view", line: "People who can see the inventory." },
  { which: "edit", title: "Can edit", line: "People who can add and change entries, by hand, CSV or API token." },
] as const;

/** One rule as a line of text: "Everyone signed in", "Group: Network Engineering", "User: Jenna Ortiz". */
export function ruleText(r: InvRule) {
  if (r.kind === "Everyone") return "Everyone signed in";
  const what = { Space: "Space", Role: "Role", User: "User", Group: "Group", Token: "API token" }[r.kind];
  return `${what}: ${r.name ?? r.id}`;
}

/** Administration › Security › Inventory access: who views and who edits the application inventory. Admins always do both. */
export function InventoryAccess({ area, onHome }: { area: string; onHome: () => void }) {
  const [access, setAccess] = useState<Access | null>(null);
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [roles, setRoles] = useState<RoleDef[]>([]);
  const [tokens, setTokens] = useState<TokenRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    void api.get<Access>("/admin/inventory/access").then(setAccess);
    void api.get<Space[]>("/admin/spaces").then(setSpaces).catch(() => setSpaces([]));
    void api.get<RoleDef[]>("/roles").then(setRoles).catch(() => setRoles([]));
    void api.get<TokenRow[]>("/admin/tokens").then((t) => setTokens(t.filter((x) => x.state === "Active"))).catch(() => setTokens([]));
  }, []);

  async function save(next: Access) {
    setError(null);
    try { setAccess(await api.put<Access>("/admin/inventory/access", next)); }
    catch (e) { setError(e instanceof ApiError && e.body?.errors ? Object.values(e.body.errors).flat()[0] ?? "Not saved." : "Not saved."); }
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
        {error && <p className="form-error" role="alert">{error}</p>}
        {SECTIONS.map(({ which, title, line }) => (
          <section key={which} className="rule-section" aria-label={title}>
            <h2 className="s">{title}</h2>
            <p className="muted small">{line}</p>
            <ul className="rule-list">
              {access[which].map((r, i) => (
                <li key={`${r.kind}:${r.id}`} className="rule-row">
                  <span>{ruleText(r)}</span>
                  <button type="button" className="tb" onClick={() => void save({ ...access, [which]: access[which].filter((_, n) => n !== i) })}>Remove</button>
                </li>
              ))}
              {access[which].length === 0 && <li className="rule-row muted">Admins only</li>}
            </ul>
            <AddRule which={title} spaces={spaces} roles={roles} tokens={tokens} onAdd={(r) => void save({ ...access, [which]: [...access[which], r] })} />
          </section>
        ))}
      </div>
    </main>
  );
}

function AddRule({ which, spaces, roles, tokens, onAdd }: { which: string; spaces: Space[]; roles: RoleDef[]; tokens: TokenRow[]; onAdd: (r: InvRule) => void }) {
  const [kind, setKind] = useState<AddKind>("Everyone");
  const [target, setTarget] = useState("");
  const [who, setWho] = useState<OwnerRef | null>(null);
  const ready = kind === "Everyone" || (kind === "Person" ? !!who : !!target);

  function add() {
    if (kind === "Person") { if (who) onAdd({ kind: who.type, id: who.id, name: who.name }); }
    else onAdd({ kind, id: kind === "Everyone" ? null : target });
    setTarget(""); setWho(null);
  }

  const options = kind === "Space" ? spaces.map((s) => [s.id, s.name]) : kind === "Role" ? roles.map((r) => [r.id, r.name]) : kind === "Token" ? tokens.map((t) => [t.id, `${t.name} · ${t.owner}`]) : null;
  return (
    <div className="rule-add">
      <select className="sel" aria-label={`${which}: kind`} value={kind} onChange={(e) => { setKind(e.target.value as AddKind); setTarget(""); setWho(null); }}>
        {KINDS.map(([k, t]) => <option key={k} value={k}>{t}</option>)}
      </select>
      {options && (
        <select className="sel" aria-label={`${which}: ${kind === "Token" ? "API token" : kind.toLowerCase()}`} value={target} onChange={(e) => setTarget(e.target.value)}>
          <option value="" />{options.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
        </select>
      )}
      {kind === "Person" && <PrincipalPicker label={`${which}: person or group`} value={who} onChange={setWho} />}
      <button type="button" className="tb primary" disabled={!ready} onClick={add}>Add</button>
    </div>
  );
}
