import { useEffect, useRef, useState } from "react";
import { api, ApiError, type Brand, type Me } from "./api";

type Pending = { id: string; clientName: string; scopes: string[]; alreadyGranted: string[]; expiresAt: string };

/** The id in a consent link, `/oauth/consent/{id}`, or null. */
export function consentAt(pathname: string): string | null {
  const m = /^\/oauth\/consent\/([0-9a-f-]{36})\/?$/i.exec(pathname);
  return m ? m[1] : null;
}

/** What each scope lets the application do, in plain words (identity.md, Scopes). */
const WORDS: Record<string, string> = {
  READ: "See what you can see, and your profile",
  WRITE: "Create, change and delete what you can change",
  ADMIN: "Administer the site, if you are an administrator",
  SYSTEM_ADMIN: "Administer the site, if you are an administrator",
};

/**
 * The consent page an OAuth 2.0 application sends the user to. It names the application, what it asks for and the account it will act as.
 * Allow or Deny sends the browser back to the application. A request already covered by an earlier consent is allowed without asking again.
 */
export function Consent({ id, me, brand }: { id: string; me: Me; brand: Brand }) {
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const decided = useRef(false);

  async function decide(allow: boolean) {
    if (decided.current) return;
    decided.current = true;
    setBusy(true);
    try {
      const r = await api.post<{ redirectTo: string }>(`/oauth/authorize-requests/${id}/decision`, { allow });
      window.location.assign(r.redirectTo);
    } catch {
      setError("This request has expired or was already answered. Start again from the application.");
      setBusy(false);
    }
  }

  useEffect(() => {
    api.get<Pending>(`/oauth/authorize-requests/${id}`).then((p) => {
      setPending(p);
      // Consent is remembered per application: nothing new to approve, nothing to ask.
      if (p.scopes.every((s) => p.alreadyGranted.includes(s))) void decide(true);
    }).catch((e) => setError(e instanceof ApiError && e.status === 404
      ? "This request has expired, was already answered, or is not yours. Start again from the application."
      : "The request could not be read."));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  const words = [...new Set((pending?.scopes ?? []).map((s) => WORDS[s] ?? s))];
  return (
    <main className="signin consent" aria-labelledby="consent-title">
      <div className="card">
        <h1 id="consent-title">{pending ? `${pending.clientName} wants to use your ${brand.productName} account` : brand.productName}</h1>
        {error && <p className="form-error" role="alert">{error}</p>}
        {pending && !error && (
          <>
            <p>Signed in as <strong>{me.displayName}</strong> ({me.userName}). If you allow it, {pending.clientName} can:</p>
            <ul>{words.map((w) => <li key={w}>{w}</li>)}</ul>
            <p className="muted">It can never do more than you can. You can disconnect it at any time under Settings › Connected applications.</p>
            <div className="actions">
              <button type="button" className="tb primary" disabled={busy} onClick={() => void decide(true)}>Allow</button>
              <button type="button" className="tb" disabled={busy} onClick={() => void decide(false)}>Deny</button>
            </div>
          </>
        )}
      </div>
    </main>
  );
}
