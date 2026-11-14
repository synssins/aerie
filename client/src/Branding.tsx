import { useRef, useState, type FormEvent } from "react";
import { Trash2, Upload } from "lucide-react";
import { api, ApiError, type Brand } from "./api";

/** Aerie's own icon: the logo until an Admin uploads one. It reads on light and dark alike. */
export const DEFAULT_LOGO = "/aerie-icon.svg";

/** The company logo for a background. The top bar is always dark, so it asks for "dark". */
export function BrandLogo({ brand, background }: { brand: Brand; background: "light" | "dark" }) {
  const src = (background === "dark" ? brand.logoDarkUrl ?? brand.logoLightUrl : brand.logoLightUrl ?? brand.logoDarkUrl) ?? DEFAULT_LOGO;
  return <img className="logo-img" src={src} alt={`${brand.productName} logo`} />;
}

function LogoField({ variant, label, url, onChanged }: { variant: "light" | "dark"; label: string; url: string | null; onChanged: () => Promise<void> }) {
  const input = useRef<HTMLInputElement>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function upload(file: File) {
    setBusy(true);
    setError(null);
    try {
      const r = await fetch(`/api/v1/admin/brand/logo/${variant}`, { method: "PUT", body: file, credentials: "same-origin", headers: { "Content-Type": file.type || "application/octet-stream" } });
      if (!r.ok) throw new ApiError(r.status, "upload");
      await onChanged();
    } catch (e) {
      setError(e instanceof ApiError && e.status === 400 ? "PNG, JPEG, WebP or SVG, up to 5 MB." : "Not uploaded.");
    } finally {
      setBusy(false);
      if (input.current) input.current.value = "";
    }
  }

  async function remove() {
    setBusy(true);
    try { await api.del(`/admin/brand/logo/${variant}`); await onChanged(); } finally { setBusy(false); }
  }

  return (
    <>
      <span className="lbl">{label}</span>
      <div className="logo-field">
        <div className={`logo-swatch ${variant}`}><img src={url ?? DEFAULT_LOGO} alt="" /></div>
        <input ref={input} type="file" accept="image/png,image/jpeg,image/webp,image/svg+xml" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); }} />
        <button type="button" className="tb" disabled={busy} onClick={() => input.current?.click()}><Upload className="ic" aria-hidden />Upload</button>
        {url && <button type="button" className="tb icon" disabled={busy} aria-label={`Remove ${label}`} title="Remove" onClick={() => void remove()}><Trash2 className="ic" aria-hidden /></button>}
        {error && <span className="form-error" role="alert">{error}</span>}
      </div>
    </>
  );
}

/** Administration › Branding: product name and logos. */
export function BrandingPanel({ brand, onChanged }: { brand: Brand; onChanged: () => Promise<void> }) {
  const [name, setName] = useState(brand.productName);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setStatus(null);
    try { await api.put("/admin/brand", { productName: name }); await onChanged(); setStatus("Saved."); }
    catch { setStatus("1-60 characters."); }
    finally { setBusy(false); }
  }

  return (
    <form className="form" onSubmit={save}>
      <label className="lbl" htmlFor="brand-name">Product name</label>
      <input id="brand-name" type="text" value={name} maxLength={60} onChange={(e) => { setName(e.target.value); setStatus(null); }} />
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || !name.trim() || name === brand.productName}>Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
      <LogoField variant="light" label="Logo, light background" url={brand.logoLightUrl} onChanged={onChanged} />
      <LogoField variant="dark" label="Logo, dark background" url={brand.logoDarkUrl} onChanged={onChanged} />
    </form>
  );
}
