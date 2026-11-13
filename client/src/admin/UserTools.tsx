import { useEffect, useRef, useState, type FormEvent } from "react";
import { Ellipsis, KeyRound, Plus, Trash2 } from "lucide-react";
import { api, ApiError, type AdminUser } from "../api";
import { useDialogs } from "../Dialogs";
import { MenuButton } from "../ui";

type Made = { id: string; setPasswordToken: string };
type Link = { name: string; url: string };

/** The address with which a person sets their own password. The token sits after "#", which a browser never sends to a server. */
const linkFor = (m: Made) => `${location.origin}/set-password/${m.id}#${encodeURIComponent(m.setPasswordToken)}`;

/**
 * Administration > Users: the tools. New user, and for the selected local user a new password link or Delete.
 * An account of a directory, and the break-glass account, are not managed here.
 */
export function UserTools({ picked, onChanged, onError }: { picked: AdminUser | null; onChanged: () => void; onError: (text: string | null) => void }) {
  const dialogs = useDialogs();
  const [adding, setAdding] = useState(false);
  const [link, setLink] = useState<Link | null>(null);
  const local = picked && picked.source === "Local" && !picked.isBreakGlass ? picked : null;

  async function newLink(u: AdminUser) {
    onError(null);
    try { setLink({ name: u.displayName, url: linkFor(await api.post<Made>(`/admin/users/${u.id}/password-link`)) }); }
    catch (e) { onError(e instanceof ApiError && e.body?.detail ? e.body.detail : "No link was made."); }
  }

  async function remove(u: AdminUser) {
    onError(null);
    if (!await dialogs.confirm({ title: `Delete “${u.displayName}”?`, ok: "Delete", danger: true })) return;
    try { await api.del(`/admin/users/${u.id}`); onChanged(); }
    catch (e) { onError(e instanceof ApiError && e.body?.detail ? e.body.detail : "Not deleted."); }
  }

  return (
    <>
      {local && (
        <MenuButton label="More" iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
          { label: "Password link", icon: <KeyRound className="ic" aria-hidden />, onClick: () => void newLink(local) },
          { label: "Delete", icon: <Trash2 className="ic" aria-hidden />, onClick: () => void remove(local), danger: true },
        ]} />
      )}
      <button className="tb primary" onClick={() => { onError(null); setAdding(true); }}><Plus className="ic" aria-hidden />New user</button>
      {adding && <NewUserDialog onClose={() => setAdding(false)} onMade={(name, made) => { setAdding(false); setLink({ name, url: linkFor(made) }); onChanged(); }} />}
      {link && <PasswordLinkDialog link={link} onClose={() => setLink(null)} />}
    </>
  );
}

function NewUserDialog({ onClose, onMade }: { onClose: () => void; onMade: (name: string, made: Made) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [form, setForm] = useState({ userName: "", displayName: "", email: "", isAdmin: false });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { ref.current?.showModal(); }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const made = await api.post<Made>("/admin/users/invite", { userName: form.userName.trim(), displayName: form.displayName.trim(), email: form.email.trim() || null, isAdmin: form.isAdmin });
      onMade(form.displayName.trim(), made);
    } catch (err) {
      const fields = err instanceof ApiError ? err.body?.errors : undefined;
      setError(fields ? Object.values(fields).flat()[0] ?? "Not added." : "Not added.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <dialog ref={ref} className="dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="new-user-title">
      <h2 id="new-user-title">New user</h2>
      <form onSubmit={submit}>
        <label>User name<input value={form.userName} onChange={(e) => setForm({ ...form, userName: e.target.value })} maxLength={64} required autoFocus autoComplete="off" /></label>
        <label>Name<input value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} maxLength={100} required autoComplete="off" /></label>
        <label>Email<input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} maxLength={200} autoComplete="off" /></label>
        <label className="check"><input type="checkbox" className="chk" checked={form.isAdmin} onChange={(e) => setForm({ ...form, isAdmin: e.target.checked })} />Administrator</label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose}>Cancel</button>
          <button type="submit" className="tb primary" disabled={busy || !form.userName.trim() || !form.displayName.trim()}>Add</button>
        </div>
      </form>
    </dialog>
  );
}

/** Shows the link once. It is not kept anywhere: closing the dialog is the end of it here. */
function PasswordLinkDialog({ link, onClose }: { link: Link; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => { ref.current?.showModal(); }, []);
  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="pw-link-title">
      <h2 id="pw-link-title">Password link: {link.name}</h2>
      <label>Link<input readOnly value={link.url} onFocus={(e) => e.target.select()} /></label>
      <div className="dlg-actions">
        {copied && <span className="muted small" role="status">Copied</span>}
        <button type="button" className="tb" onClick={() => void navigator.clipboard?.writeText(link.url).then(() => setCopied(true))}>Copy</button>
        <button type="button" className="tb primary" onClick={onClose} autoFocus>Done</button>
      </div>
    </dialog>
  );
}
