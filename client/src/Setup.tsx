import { useState, type FormEvent } from "react";
import { api, ApiError, type Brand, type Me } from "./api";
import { BrandLogo } from "./Branding";

export type SetupStatus = { required: boolean; codeRequired: boolean };

export function Setup({ brand, codeRequired, onDone }: { brand: Brand; codeRequired: boolean; onDone: (me: Me) => void }) {
  const [displayName, setDisplayName] = useState("");
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (password !== confirm) { setError("Passwords don't match."); return; }
    setBusy(true);
    setError(null);
    try {
      onDone(await api.post<Me>("/setup", { userName, displayName, password, code: codeRequired ? code : null }));
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) setError("Setup code is wrong.");
      else if (err instanceof ApiError && err.status === 409) location.reload();
      else setError("Password must be at least 15 characters.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="signin">
      <form className="signin-card" onSubmit={submit} aria-labelledby="setup-title">
        <div className="signin-brand">
          <BrandLogo brand={brand} background={document.documentElement.dataset.theme === "dark" ? "dark" : "light"} />
          <span>{brand.productName}</span>
        </div>
        <h1 id="setup-title">Create the first administrator</h1>
        <label>
          Display name
          <input autoComplete="name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
        </label>
        <label>
          User name
          <input autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} required />
        </label>
        <label>
          Password (15+ characters)
          <input type="password" autoComplete="new-password" minLength={15} value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        <label>
          Confirm password
          <input type="password" autoComplete="new-password" minLength={15} value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
        </label>
        {codeRequired && (
          <label>
            Setup code
            <input autoComplete="off" spellCheck={false} value={code} onChange={(e) => setCode(e.target.value)} required />
            <span className="muted small">In App_Data\setup-code.txt on the server.</span>
          </label>
        )}
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="btn primary" type="submit" disabled={busy}>{busy ? "Creating…" : "Create and sign in"}</button>
      </form>
    </main>
  );
}
