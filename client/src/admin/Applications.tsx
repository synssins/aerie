import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Plus } from "lucide-react";
import { api, ApiError, fmtDateTime } from "../api";
import { useDialogs } from "../Dialogs";

export type OAuthClient = {
  id: string; clientId: string; name: string; type: "confidential" | "public"; redirectUris: string[]; scopes: string[];
  requirePkce: boolean; enabled: boolean; activeGrants: number; createdBy: string; createdAt: string; secretRotatedAt: string | null;
};
type Created = { clientSecret: string | null; client: OAuthClient };
export type CompatSettings = {
  basicPasswordEnabled: boolean; allowHttpRedirects: boolean; codeSeconds: number; accessTokenSeconds: number; refreshTokenDays: number; grantMaxDays: number | null;
};

const SCOPES = ["READ", "WRITE", "ADMIN", "SYSTEM_ADMIN"] as const;

/**
 * Administration › Security › Applications: the OAuth 2.0 clients that may act for users on the compatible surface.
 * A client's secret is on screen once, when it is made or rotated, and never again.
 */
export function ApplicationsPanel() {
  const dialogs = useDialogs();
  const [clients, setClients] = useState<OAuthClient[] | null>(null);
  const [editing, setEditing] = useState<OAuthClient | "new" | null>(null);
  const [secret, setSecret] = useState<{ name: string; clientId: string; secret: string } | null>(null);

  const load = useCallback(async () => setClients(await api.get<OAuthClient[]>("/admin/oauth/clients")), []);
  useEffect(() => { void load(); }, [load]);

  async function rotate(c: OAuthClient) {
    if (!await dialogs.confirm({ title: `New secret for ${c.name}?`, body: "The client must use the new secret from now on. Tokens it already has keep working.", ok: "Rotate" })) return;
    const r = await api.post<Created>(`/admin/oauth/clients/${c.id}/secret`, {});
    setSecret({ name: c.name, clientId: c.clientId, secret: r.clientSecret ?? "" });
    await load();
  }

  async function remove(c: OAuthClient) {
    if (!await dialogs.confirm({ title: `Delete ${c.name}?`, body: "Every grant and token issued to it ends at once. This cannot be undone.", ok: "Delete", danger: true })) return;
    await api.del(`/admin/oauth/clients/${c.id}`);
    await load();
  }

  async function toggle(c: OAuthClient) {
    await api.put(`/admin/oauth/clients/${c.id}`, { enabled: !c.enabled });
    await load();
  }

  if (!clients) return null;
  return (
    <>
      {secret && (
        <div className="form made" role="region" aria-label="Client secret">
          <span className="lbl">Client</span><span className="mono">{secret.name} · {secret.clientId}</span>
          <label className="lbl" htmlFor="oa-secret">Client secret</label>
          <span className="unit grow">
            <input id="oa-secret" type="text" className="mono" readOnly value={secret.secret} onFocus={(e) => e.target.select()} />
            <button type="button" className="tb" onClick={() => void navigator.clipboard?.writeText(secret.secret)}>Copy</button>
            <button type="button" className="tb primary" onClick={() => setSecret(null)}>Done</button>
          </span>
          <p className="muted wide">You will not see this secret again. Copy it now; if it is lost, rotate it.</p>
        </div>
      )}

      {editing ? (
        <ClientForm client={editing === "new" ? null : editing} onCancel={() => setEditing(null)}
          onSaved={async (r) => {
            setEditing(null);
            if (r.clientSecret) setSecret({ name: r.client.name, clientId: r.client.clientId, secret: r.clientSecret });
            await load();
          }} />
      ) : (
        <>
          <div className="ptools row">
            <button type="button" className="tb" onClick={() => setEditing("new")}><Plus className="ic" aria-hidden />Add application</button>
          </div>
          <table className="grid-table">
            <thead><tr><th>Name</th><th>Client id</th><th>Type</th><th>Scopes</th><th>Users</th><th>Secret rotated</th><th>State</th><th /></tr></thead>
            <tbody>
              {clients.map((c) => (
                <tr key={c.id} className={c.enabled ? "" : "off"}>
                  <td>{c.name}</td>
                  <td className="mono">{c.clientId}</td>
                  <td>{c.type === "public" ? "Public" : "Confidential"}</td>
                  <td>{c.scopes.join(", ")}</td>
                  <td>{c.activeGrants}</td>
                  <td className="muted">{c.secretRotatedAt ? fmtDateTime(c.secretRotatedAt) : "Never"}</td>
                  <td>{c.enabled ? "Enabled" : "Disabled"}</td>
                  <td className="actions">
                    <button type="button" className="tb" onClick={() => setEditing(c)}>Edit</button>
                    {c.type === "confidential" && <button type="button" className="tb" onClick={() => void rotate(c)}>Rotate secret</button>}
                    <button type="button" className="tb" onClick={() => void toggle(c)}>{c.enabled ? "Disable" : "Enable"}</button>
                    <button type="button" className="tb danger" onClick={() => void remove(c)}>Delete</button>
                  </td>
                </tr>
              ))}
              {clients.length === 0 && <tr><td colSpan={8} className="muted">No applications.</td></tr>}
            </tbody>
          </table>
        </>
      )}
    </>
  );
}

function ClientForm({ client, onCancel, onSaved }: { client: OAuthClient | null; onCancel: () => void; onSaved: (r: Created) => Promise<void> }) {
  const [name, setName] = useState(client?.name ?? "");
  const [type, setType] = useState<OAuthClient["type"]>(client?.type ?? "confidential");
  const [uris, setUris] = useState((client?.redirectUris ?? []).join("\n"));
  const [scopes, setScopes] = useState<string[]>(client?.scopes ?? ["READ"]);
  const [pkce, setPkce] = useState(client?.requirePkce ?? true);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [busy, setBusy] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErrors({});
    const body = { name, ...(client ? {} : { type }), redirectUris: uris.split("\n").map((u) => u.trim()).filter(Boolean), scopes, requirePkce: type === "public" ? true : pkce };
    try {
      await onSaved(client
        ? { clientSecret: null, client: await api.put<OAuthClient>(`/admin/oauth/clients/${client.id}`, body) }
        : await api.post<Created>("/admin/oauth/clients", body));
    } catch (err) {
      setErrors(err instanceof ApiError && err.body?.errors ? err.body.errors : { form: ["Not saved."] });
      setBusy(false);
    }
  }

  const wrong = (key: string) => errors[key] && <p className="form-error wide" role="alert">{errors[key][0]}</p>;
  return (
    <form className="form" aria-label={client ? `Edit ${client.name}` : "New application"} onSubmit={save}>
      <label className="lbl" htmlFor="oa-name">Name</label>
      <input id="oa-name" type="text" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      {wrong("name")}
      <span className="lbl">Type</span>
      {client ? <span>{client.type === "public" ? "Public" : "Confidential"}</span> : (
        <div className="seg" role="radiogroup" aria-label="Type">
          {([["confidential", "Confidential (has a secret)"], ["public", "Public (no secret)"]] as const).map(([v, label]) => (
            <button key={v} type="button" role="radio" aria-checked={type === v} className={type === v ? "on" : ""} onClick={() => setType(v)}>{label}</button>
          ))}
        </div>
      )}
      {wrong("type")}
      <label className="lbl top" htmlFor="oa-uris">Redirect URIs</label>
      <textarea id="oa-uris" rows={3} value={uris} placeholder="https://app.example/callback" onChange={(e) => setUris(e.target.value)} />
      {wrong("redirectUris")}
      <span className="lbl top">Scopes</span>
      <div className="scopes" role="group" aria-label="Scopes">
        {SCOPES.map((s) => (
          <label key={s} className="check">
            <input type="checkbox" checked={scopes.includes(s)} onChange={(e) => setScopes((all) => e.target.checked ? [...all, s] : all.filter((x) => x !== s))} />{s}
          </label>
        ))}
      </div>
      {wrong("scopes")}
      {type === "confidential" && (
        <>
          <span className="lbl">PKCE</span>
          <label className="check"><input type="checkbox" checked={pkce} onChange={(e) => setPkce(e.target.checked)} />Require PKCE (turn off only for an older integration that cannot send it)</label>
          {wrong("requirePkce")}
        </>
      )}
      {wrong("form")}
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || !name.trim() || scopes.length === 0}>Save</button>
        <button type="button" className="tb" disabled={busy} onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

/** Administration › Security › Sign-in: password Basic, plain http redirect URIs for test installs, and OAuth 2.0 lifetimes. */
export function CompatSettingsPanel() {
  const [s, setS] = useState<CompatSettings | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  useEffect(() => { void api.get<CompatSettings>("/admin/auth/compat").then(setS); }, []);
  if (!s) return null;

  const num = (k: keyof CompatSettings, label: string, min: number, max: number, unit: string, optional = false) => (
    <>
      <label className="lbl" htmlFor={`cs-${k}`}>{label}</label>
      <div className="unit">
        <input id={`cs-${k}`} type="number" min={min} max={max} step={1} value={(s[k] as number | null) ?? ""} placeholder={optional ? "None" : undefined}
          onChange={(e) => { setS({ ...s, [k]: e.target.value === "" && optional ? null : Number(e.target.value) }); setStatus(null); }} />
        <span>{unit}</span>
      </div>
    </>
  );

  async function save(e: FormEvent) {
    e.preventDefault();
    try { setS(await api.put<CompatSettings>("/admin/auth/compat", s)); setStatus("Saved."); }
    catch (err) { setStatus(err instanceof ApiError && err.body?.detail ? err.body.detail : "Not saved."); }
  }

  return (
    <form className="form below" aria-label="Compatible API sign-in" onSubmit={save}>
      <span className="lbl">Password Basic</span>
      <label className="check"><input type="checkbox" checked={s.basicPasswordEnabled} onChange={(e) => setS({ ...s, basicPasswordEnabled: e.target.checked })} />Allow a password in HTTP Basic (tokens always work)</label>
      <span className="lbl">Plain http</span>
      <label className="check"><input type="checkbox" checked={s.allowHttpRedirects} onChange={(e) => setS({ ...s, allowHttpRedirects: e.target.checked })} />Allow plain http redirect URIs (test installs only)</label>
      {num("codeSeconds", "Authorization code", 60, 600, "seconds")}
      {num("accessTokenSeconds", "Access token", 300, 7200, "seconds")}
      {num("refreshTokenDays", "Refresh token, idle", 1, 365, "days")}
      {num("grantMaxDays", "Consent, at most", 1, 730, "days", true)}
      <div className="actions">
        <button type="submit" className="tb primary">Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}
