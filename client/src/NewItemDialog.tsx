import { useEffect, useRef, useState, type FormEvent } from "react";
import { ChevronRight } from "lucide-react";
import { TagInput } from "./TagInput";

export type NewItem = { kind: "Page" | "Folder"; spaceName: string; path: string[] };

/** New page / new folder form. The location is where the user is in the tree. */
export function NewItemDialog({ item, onCreate, onClose }: {
  item: NewItem;
  onCreate: (title: string, tags: string[]) => Promise<void>;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [title, setTitle] = useState("");
  const [tags, setTags] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { ref.current?.showModal(); }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!title.trim()) { setError("Title is required."); return; }
    setBusy(true);
    try { await onCreate(title.trim(), tags); }
    catch { setError("Not created. Check the title and tags."); setBusy(false); }
  }

  const isPage = item.kind === "Page";
  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="new-title">
      <form onSubmit={submit}>
        <h2 id="new-title">{isPage ? "New page" : "New folder"}</h2>
        <div className="field">
          <span className="field-label">Location</span>
          <div className="location">
            {[item.spaceName, ...item.path].map((p, i) => (
              <span key={i} className="crumb-part">{i > 0 && <ChevronRight className="ic sm" aria-hidden />}{p}</span>
            ))}
          </div>
        </div>
        <label className="field">
          <span className="field-label">{isPage ? "Title" : "Name"}</span>
          <input autoFocus value={title} maxLength={200} onChange={(e) => { setTitle(e.target.value); setError(null); }} />
        </label>
        {isPage && (
          <div className="field">
            <label className="field-label" htmlFor="new-tags">Tags</label>
            <TagInput id="new-tags" value={tags} onChange={setTags} />
          </div>
        )}
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="tb primary" disabled={busy}>{busy ? "Creating…" : isPage ? "Create and edit" : "Create"}</button>
        </div>
      </form>
    </dialog>
  );
}
