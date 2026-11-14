import { useEffect, useState, type FormEvent } from "react";
import QRCode from "qrcode";
import { api, ApiError, type Me } from "./api";

/** The next sign-in step the server asked for after the password: "code" or "enroll" (409 with { step }). */
export function stepOf(err: unknown): "code" | "enroll" | null {
  const step = err instanceof ApiError && err.status === 409 ? (err.body as { step?: string } | null)?.step : undefined;
  return step === "code" || step === "enroll" ? step : null;
}

/** The key as an app shows it: groups of four. */
export const grouped = (key: string) => key.match(/.{1,4}/g)?.join(" ") ?? key;

/** Second step of sign-in: a code from the app or a recovery code. */
export function CodeStep({ onSignedIn, className = "" }: { onSignedIn: (me: Me) => void; className?: string }) {
  const [code, setCode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await api.post<Me>("/auth/local/code", { code }));
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? "Too many tries. Wait a minute." : "Wrong code.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className={className} onSubmit={submit} aria-labelledby="code-title">
      <h1 id="code-title">Code</h1>
      <label>
        Code or recovery code
        <input autoComplete="one-time-code" spellCheck={false} autoFocus value={code} onChange={(e) => setCode(e.target.value)} required />
      </label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <button className="btn primary" type="submit" disabled={busy}>{busy ? "Checking…" : "Sign in"}</button>
    </form>
  );
}

/**
 * Setting up codes: a new key (QR and text), one code to confirm it, then the recovery codes, shown once.
 * Used after the password at sign-in and from the person's own settings.
 */
export function CodeSetup({ onDone, onCancel, className = "" }: { onDone: (me: Me) => void; onCancel?: () => void; className?: string }) {
  const [key, setKey] = useState<{ key: string; uri: string } | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [done, setDone] = useState<{ recoveryCodes: string[]; me: Me } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    api.post<{ key: string; uri: string }>("/auth/codes/key")
      .then(async (k) => { if (!live) return; setKey(k); const url = await QRCode.toDataURL(k.uri, { margin: 1, width: 200 }); if (live) setQr(url); })
      .catch(() => { if (live) setError("Could not make a key."); });
    return () => { live = false; };
  }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setDone(await api.post<{ recoveryCodes: string[]; me: Me }>("/auth/codes", { code }));
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401 ? "Too many tries. Sign in again later." : "Wrong code.");
    } finally {
      setBusy(false);
    }
  }

  if (done) {
    return (
      <div className={className}>
        <h1>Recovery codes</h1>
        <ul className="recovery-codes" aria-label="Recovery codes">{done.recoveryCodes.map((c) => <li key={c}><code>{c}</code></li>)}</ul>
        <button className="btn primary" type="button" onClick={() => onDone(done.me)}>Continue</button>
      </div>
    );
  }

  return (
    <form className={className} onSubmit={submit} aria-labelledby="codes-title">
      <h1 id="codes-title">Set up codes</h1>
      {qr && <img className="code-qr" src={qr} alt="QR code" width={200} height={200} />}
      {key && <div className="code-key"><span className="lbl">Key</span><code>{grouped(key.key)}</code></div>}
      <label>
        Code
        <input autoComplete="one-time-code" inputMode="numeric" spellCheck={false} value={code} onChange={(e) => setCode(e.target.value)} required />
      </label>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="code-actions">
        <button className="btn primary" type="submit" disabled={busy || !key}>{busy ? "Checking…" : "Confirm"}</button>
        {onCancel && <button className="btn" type="button" onClick={onCancel}>Cancel</button>}
      </div>
    </form>
  );
}
