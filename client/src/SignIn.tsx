import { useState, type FormEvent } from "react";
import { api, ApiError, type Brand, type Me } from "./api";
import { BrandLogo } from "./Branding";
import { CodeSetup, CodeStep, stepOf } from "./Codes";

export function SignIn({ brand, onSignedIn }: { brand: Brand; onSignedIn: (me: Me) => void }) {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [step, setStep] = useState<"code" | "enroll" | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.post<Me>("/auth/local/signin", { userName, password }));
    } catch (err) {
      const next = stepOf(err);
      if (next) { setStep(next); return; }
      setError(err instanceof ApiError && err.status === 403
        ? "Local sign-in is off. Use single sign-on."
        : "Wrong user name or password.");
    } finally {
      setBusy(false);
    }
  }

  if (step) return (
    <main className="signin">
      {step === "code" ? <CodeStep className="signin-card" onSignedIn={onSignedIn} /> : <CodeSetup className="signin-card" onDone={onSignedIn} />}
    </main>
  );

  return (
    <main className="signin">
      <form className="signin-card" onSubmit={submit} aria-labelledby="signin-title">
        <div className="signin-brand">
          <BrandLogo brand={brand} background={document.documentElement.dataset.theme === "dark" ? "dark" : "light"} />
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
        {brand.signInNote && <p className="muted small">{brand.signInNote}</p>}
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="btn primary" type="submit" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
      </form>
    </main>
  );
}
