import { useState, type FormEvent } from "react";
import { api, ApiError, type Brand } from "./api";
import { BrandLogo } from "./Branding";

/** The account and the token of a password link: /set-password/<user id>#<token>. Null for any other address. */
export function passwordLinkAt(pathname: string, hash: string): { userId: string; token: string } | null {
  const m = /^\/set-password\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/?$/i.exec(pathname);
  if (!m) return null;
  let token = hash.replace(/^#/, "");
  try { token = decodeURIComponent(token); } catch { /* as it is */ }
  return { userId: m[1], token };
}

/** Where a person sets their own password with the link an Admin handed over. Nobody is signed in here. */
export function SetPassword({ brand, userId, token, onDone }: { brand: Brand; userId: string; token: string; onDone: () => void }) {
  const [password, setPassword] = useState("");
  const [repeat, setRepeat] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (password !== repeat) { setError("The passwords differ."); return; }
    setBusy(true);
    setError(null);
    try {
      await api.post("/auth/local/password", { userId, token, password });
      onDone();
    } catch (err) {
      const body = err instanceof ApiError ? err.body : null;
      setError(body?.errors?.password?.[0] ?? body?.detail ?? (err instanceof ApiError && err.status === 429 ? "Too many tries. Wait a minute." : "Not set. Server unavailable; try again."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="signin">
      <form className="signin-card" onSubmit={submit} aria-labelledby="setpw-title">
        <div className="signin-brand">
          <BrandLogo brand={brand} background={document.documentElement.dataset.theme === "dark" ? "dark" : "light"} />
          <span>{brand.productName}</span>
        </div>
        <h1 id="setpw-title">Set password</h1>
        <label>
          New password
          <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required autoFocus />
        </label>
        <label>
          Repeat password
          <input type="password" autoComplete="new-password" value={repeat} onChange={(e) => setRepeat(e.target.value)} required />
        </label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="btn primary" type="submit" disabled={busy}>{busy ? "Setting…" : "Set password"}</button>
      </form>
    </main>
  );
}
