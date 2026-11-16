import { useCallback, useEffect, useState } from "react";
import { api, fmtDateTime } from "./api";
import { useDialogs } from "./Dialogs";

export type OAuthGrant = { id: string; clientName: string; scopes: string[]; grantedAt: string; lastUsedAt: string | null };

/**
 * Settings › Connected applications: the OAuth 2.0 applications this person allowed to act for them. Disconnecting one ends its access at once;
 * it has to ask again to come back.
 */
export function ConnectedAppsPanel() {
  const dialogs = useDialogs();
  const [grants, setGrants] = useState<OAuthGrant[] | null>(null);
  const load = useCallback(async () => setGrants(await api.get<OAuthGrant[]>("/me/oauth/grants")), []);
  useEffect(() => { void load(); }, [load]);

  async function disconnect(g: OAuthGrant) {
    if (!await dialogs.confirm({ title: `Disconnect ${g.clientName}?`, body: "It loses access at once. To use it again, you allow it again.", ok: "Disconnect", danger: true })) return;
    await api.del(`/me/oauth/grants/${g.id}`);
    await load();
  }

  if (!grants) return null;
  return (
    <table className="grid-table">
      <thead><tr><th>Application</th><th>Can</th><th>Allowed</th><th>Last used</th><th /></tr></thead>
      <tbody>
        {grants.map((g) => (
          <tr key={g.id}>
            <td>{g.clientName}</td>
            <td>{g.scopes.join(", ")}</td>
            <td className="muted">{fmtDateTime(g.grantedAt)}</td>
            <td className="muted">{g.lastUsedAt ? fmtDateTime(g.lastUsedAt) : "Never"}</td>
            <td className="actions"><button type="button" className="tb" onClick={() => void disconnect(g)}>Disconnect</button></td>
          </tr>
        ))}
        {grants.length === 0 && <tr><td colSpan={5} className="muted">No connected applications.</td></tr>}
      </tbody>
    </table>
  );
}
