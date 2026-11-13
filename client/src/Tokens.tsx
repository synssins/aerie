import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Plus, X } from "lucide-react";
import { api, ApiError, fmtDateTime, type ApiToken, type Space, type TokenScope } from "./api";
import { useDialogs } from "./Dialogs";
import { MenuButton } from "./ui";

const fmtDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { dateStyle: "medium" });

/**
 * API tokens. In Settings: a person's own tokens. In Administration: every token, service tokens, and the longest life a token may have.
 * A token's text is on screen once, right after it is made.
 */
export function TokensPanel({ admin, spaces }: { admin: boolean; spaces: Space[] }) {
  const dialogs = useDialogs();
  const [tokens, setTokens] = useState<ApiToken[] | null>(null);
  const [maxDays, setMaxDays] = useState<number | null>(null);
  const [adding, setAdding] = useState<ApiToken["kind"] | null>(null);
  const [made, setMade] = useState<string | null>(null);

  const load = useCallback(async () => {
    setTokens(await api.get<ApiToken[]>(admin ? "/admin/tokens" : "/tokens"));
    if (admin) setMaxDays((await api.get<{ maxDays: number }>("/admin/tokens/settings")).maxDays);
  }, [admin]);
  useEffect(() => { void load(); }, [load]);

  const spaceName = (id: string | null) => id === null ? "All spaces" : spaces.find((s) => s.id === id)?.name ?? "Unknown space";
  const accounts = [...new Map((tokens ?? []).filter((t) => t.kind === "Service").map((t) => [t.ownerId, t.owner])).entries()];

  async function revoke(t: ApiToken) {
    if (!await dialogs.confirm({ title: `Revoke ${t.name}?`, ok: "Revoke", danger: true })) return;
    await api.del(`/tokens/${t.id}`);
    await load();
  }

  if (!tokens) return null;
  return (
    <>
      {made && (
        <div className="form made">
          <label className="lbl" htmlFor="tk-made">New token, shown once</label>
          <span className="unit grow">
            <input id="tk-made" type="text" className="mono" readOnly value={made} onFocus={(e) => e.target.select()} />
            <button type="button" className="tb" onClick={() => void navigator.clipboard?.writeText(made)}>Copy</button>
            <button type="button" className="tb primary" onClick={() => setMade(null)}>Done</button>
          </span>
        </div>
      )}

      {adding ? (
        <TokenForm kind={adding} spaces={spaces} accounts={accounts} onCancel={() => setAdding(null)}
          onMade={async (text) => { setMade(text); setAdding(null); await load(); }} />
      ) : (
        <>
          <div className="ptools row">
            <MenuButton label="Add" icon={<Plus className="ic" aria-hidden />} items={[
              { label: "Personal token", onClick: () => setAdding("Personal") },
              ...(admin ? [{ label: "Service token", onClick: () => setAdding("Service") }] : []),
            ]} />
          </div>
          <table className="grid-table">
            <thead><tr><th>Name</th><th>Token</th><th>Scopes</th>{admin && <><th>Kind</th><th>Owner</th></>}<th>Expires</th><th>Last used</th><th>State</th><th /></tr></thead>
            <tbody>
              {tokens.map((t) => (
                <tr key={t.id} className={t.state === "Active" ? "" : "off"}>
                  <td>{t.name}</td>
                  <td className="mono">{t.prefix}…</td>
                  <td>{t.scopes.map((s) => `${spaceName(s.spaceId)}: ${s.access}`).join(", ")}</td>
                  {admin && <><td>{t.kind}</td><td>{t.owner}</td></>}
                  <td className="muted">{fmtDate(t.expiresAt)}</td>
                  <td className="muted">{t.lastUsedAt ? fmtDateTime(t.lastUsedAt) : "Never"}</td>
                  <td>{t.state}</td>
                  <td className="actions">{t.state === "Active" && <button type="button" className="tb" onClick={() => void revoke(t)}>Revoke</button>}</td>
                </tr>
              ))}
              {tokens.length === 0 && <tr><td colSpan={admin ? 9 : 7} className="muted">No tokens.</td></tr>}
            </tbody>
          </table>
          {admin && maxDays !== null && <MaxDaysForm key={maxDays} maxDays={maxDays} onSaved={setMaxDays} />}
        </>
      )}
    </>
  );
}

function MaxDaysForm({ maxDays, onSaved }: { maxDays: number; onSaved: (n: number) => void }) {
  const [days, setDays] = useState(String(maxDays));
  const [status, setStatus] = useState<string | null>(null);
  const n = Number(days);
  const valid = Number.isInteger(n) && n >= 1 && n <= 3650;

  async function save(e: FormEvent) {
    e.preventDefault();
    try { onSaved((await api.put<{ maxDays: number }>("/admin/tokens/settings", { maxDays: n })).maxDays); setStatus("Saved."); } catch { setStatus("Not saved."); }
  }

  return (
    <form className="form below" onSubmit={save}>
      <label className="lbl" htmlFor="tk-max">Longest life</label>
      <div className="unit">
        <input id="tk-max" type="number" min={1} max={3650} step={1} value={days} onChange={(e) => { setDays(e.target.value); setStatus(null); }} />
        <span>days</span>
      </div>
      <div className="actions">
        <button type="submit" className="tb primary" disabled={!valid || n === maxDays}>Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

function TokenForm({ kind, spaces, accounts, onCancel, onMade }: {
  kind: ApiToken["kind"]; spaces: Space[]; accounts: [string, string][]; onCancel: () => void; onMade: (text: string) => Promise<void>;
}) {
  const [name, setName] = useState("");
  const [days, setDays] = useState("90");
  const [account, setAccount] = useState("");
  const [scopes, setScopes] = useState<TokenScope[]>([{ spaceId: null, access: "Read" }]);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);
  const title = kind === "Service" ? "Service token" : "Personal token";

  const setScope = (i: number, change: Partial<TokenScope>) => setScopes((all) => all.map((s, j) => (j === i ? { ...s, ...change } : s)));

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErrors({});
    try {
      const r = await api.post<{ token: string }>("/tokens", { name, kind, days: Number(days), ...(kind === "Service" && account ? { accountId: account } : {}), scopes });
      await onMade(r.token);
    } catch (err) {
      setErrors(err instanceof ApiError && err.body?.errors ? err.body.errors : { form: ["Not saved."] });
      setBusy(false);
    }
  }

  const wrong = (key: string) => errors[key] && <p className="form-error wide" role="alert">{errors[key][0]}</p>;

  return (
    <form className="form" aria-label={title} onSubmit={save}>
      <span className="lbl">Kind</span><span>{title}</span>
      <label className="lbl" htmlFor="tk-name">Name</label>
      <input id="tk-name" type="text" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      {wrong("name")}
      {kind === "Service" && (
        <>
          <label className="lbl" htmlFor="tk-account">Account</label>
          <select id="tk-account" className="sel" value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">New account</option>
            {accounts.map(([id, label]) => <option key={id} value={id}>{label}</option>)}
          </select>
          {wrong("accountId")}
        </>
      )}
      <label className="lbl" htmlFor="tk-days">Days</label>
      <div className="unit"><input id="tk-days" type="number" min={1} step={1} value={days} onChange={(e) => setDays(e.target.value)} /><span>days</span></div>
      {wrong("days")}
      <span className="lbl top">Scopes</span>
      <div className="scopes">
        {scopes.map((s, i) => (
          <div key={i} className="scope">
            <select className="sel" aria-label="Space" value={s.spaceId ?? ""} onChange={(e) => setScope(i, { spaceId: e.target.value || null })}>
              <option value="">All spaces</option>
              {spaces.map((sp) => <option key={sp.id} value={sp.id}>{sp.name}</option>)}
            </select>
            <div className="seg" role="radiogroup" aria-label="Access">
              {(["Read", "Write"] as const).map((a) => (
                <button key={a} type="button" role="radio" aria-checked={s.access === a} className={s.access === a ? "on" : ""} onClick={() => setScope(i, { access: a })}>{a}</button>
              ))}
            </div>
            {scopes.length > 1 && <button type="button" className="tb icon" aria-label="Remove scope" title="Remove scope" onClick={() => setScopes((all) => all.filter((_, j) => j !== i))}><X className="ic" aria-hidden /></button>}
          </div>
        ))}
        <button type="button" className="tb" onClick={() => setScopes((all) => [...all, { spaceId: null, access: "Read" }])}>Add scope</button>
      </div>
      {wrong("scopes")}
      {wrong("form")}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || !name.trim()}>Save</button>
        <button type="button" className="tb" disabled={busy} onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
