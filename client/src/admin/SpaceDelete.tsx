import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Lock, RotateCcw } from "lucide-react";
import { api, ApiError, fmtDateTime } from "../api";

/**
 * Deleting a space takes three steps, so that it cannot happen by one slip: Delete, then "Yes, I'm sure", then a box to tick and
 * a phrase to type. The server asks for the phrase again; this dialog is a courtesy, not the control.
 */
export function DeleteSpaceDialog({ space, onClose, onDeleted }: { space: { id: string; key: string; name: string }; onClose: () => void; onDeleted: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [ticked, setTicked] = useState(false);
  const [typed, setTyped] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { ref.current?.showModal(); }, []);
  const phrase = `delete ${space.key}`;
  const ready = ticked && typed.trim().toLowerCase() === phrase.toLowerCase();

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready) return;
    setBusy(true);
    setError(null);
    try {
      await api.post(`/admin/spaces/${space.id}/delete`, { confirm: typed.trim() });
      onDeleted();
    } catch (err) {
      const body = err instanceof ApiError ? err.body : null;
      setError(body?.errors?.confirm?.[0] ?? body?.detail ?? "Not deleted.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <dialog ref={ref} className="dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="del-space-title">
      {step === 1 && (
        <>
          <h2 id="del-space-title">Delete “{space.name}”?</h2>
          <div className="dlg-actions">
            <button type="button" className="tb" onClick={onClose} autoFocus>Cancel</button>
            <button type="button" className="tb danger" onClick={() => setStep(2)}>Delete</button>
          </div>
        </>
      )}
      {step === 2 && (
        <>
          <h2 id="del-space-title">Are you sure?</h2>
          <div className="dlg-actions">
            <button type="button" className="tb" onClick={onClose} autoFocus>Cancel</button>
            <button type="button" className="tb danger" onClick={() => setStep(3)}>Yes, I'm sure</button>
          </div>
        </>
      )}
      {step === 3 && (
        <form onSubmit={submit}>
          <h2 id="del-space-title">Delete “{space.name}”</h2>
          <label className="check"><input type="checkbox" className="chk" checked={ticked} onChange={(e) => setTicked(e.target.checked)} />Delete the space and everything in it</label>
          <label>Type “{phrase}”<input value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} autoFocus /></label>
          {error && <p className="form-error" role="alert">{error}</p>}
          <div className="dlg-actions">
            <button type="button" className="tb" onClick={onClose}>Cancel</button>
            <button type="submit" className="tb danger" disabled={!ready || busy}>Delete space</button>
          </div>
        </form>
      )}
    </dialog>
  );
}

type DeletedSpace = { id: string; key: string; name: string; isSensitive: boolean; deletedAt: string; deletedBy: string; pages: number };

/** Administration > Recycle bin: the deleted spaces, above the deleted pages. Nothing is drawn when there is none. */
export function DeletedSpaces({ onRestored }: { onRestored: () => void }) {
  const [items, setItems] = useState<DeletedSpace[]>([]);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api.get<DeletedSpace[]>("/admin/spaces/deleted").then(setItems).catch(() => setItems([])), []);
  useEffect(() => { void load(); }, [load]);

  async function restore(s: DeletedSpace) {
    setError(null);
    try { await api.post(`/admin/spaces/${s.id}/restore`); onRestored(); await load(); }
    catch { setError("Not restored."); }
  }

  if (items.length === 0) return null;
  return (
    <>
      <h2 className="s">Spaces</h2>
      {error && <p className="form-error" role="alert">{error}</p>}
      <table className="grid-table">
        <thead><tr><th>Name</th><th>Pages</th><th>Deleted</th><th>By</th><th /></tr></thead>
        <tbody>
          {items.map((s) => (
            <tr key={s.id}>
              <td>{s.name}{s.isSensitive && <Lock className="ic sm muted" aria-label="Sensitive" />}</td>
              <td className="muted">{s.pages}</td>
              <td className="muted">{fmtDateTime(s.deletedAt)}</td>
              <td className="muted">{s.deletedBy}</td>
              <td className="actions"><button className="tb" onClick={() => void restore(s)}><RotateCcw className="ic" aria-hidden />Restore</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <h2 className="s">Pages</h2>
    </>
  );
}
