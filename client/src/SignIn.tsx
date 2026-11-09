import { useState, type FormEvent } from "react";
import { api, ApiError, type Brand, type Me } from "./api";

export function SignIn({ brand, onSignedIn }: { brand: Brand; onSignedIn: (me: Me) => void }) {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.post<Me>("/auth/local/signin", { userName, password }));
    } catch (err) {
      setError(err instanceof ApiError && err.status === 403
        ? "Local sign-in is turned off. Use single sign-on."
        : "That user name and password did not match. After 5 failed attempts the account locks for 15 minutes.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="signin">
      <form className="signin-card" onSubmit={submit} aria-labelledby="signin-title">
        <div className="signin-brand">
          <div className="logo">LOGO</div>
          <span>{brand.productName}</span>
        </div>
        <h1 id="signin-title">Sign in</h1>
        <label>
          User name
          <input autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} required />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="btn primary" type="submit" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
        <p className="muted small">Single sign-on appears here once an administrator configures it.</p>
      </form>
    </main>
  );
}
