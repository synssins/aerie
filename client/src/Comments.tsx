import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, ApiError, fmtDateTime } from "./api";
import { useDialogs } from "./Dialogs";

/** A comment as the server shows it to the caller (contracts/behaviour/comments.md). Author and text are null in a placeholder. */
export type Comment = {
  id: string; pageId: string; parentId: string | null; author: { type: string; id: string; name: string } | null; text: string | null;
  createdAt: string; editedAt: string | null; state: "visible" | "hidden" | "deleted"; canEdit: boolean; canDelete: boolean; canModerate: boolean; replies: Comment[];
};
export type CommentList = { enabled: boolean; canComment: boolean; canModerate: boolean; total: number; limit: number; offset: number; comments: Comment[] };

/** The server's reason for a refusal, in words for the person. */
function why(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 429) return "Too many comments. Wait a minute.";
    if (err.status === 400) return "A comment is 1 to 4000 characters.";
    if (err.body?.detail) return err.body.detail;
  }
  return "Not saved.";
}

/** Writes a comment or reply. Plain text only: line breaks are kept, nothing else is formatted. */
function Composer({ label, initial = "", ok, onSubmit, onCancel }: { label: string; initial?: string; ok: string; onSubmit: (text: string) => Promise<void>; onCancel?: () => void }) {
  const [text, setText] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try { await onSubmit(text); setText(""); }
    catch (err) { setError(why(err)); }
    finally { setBusy(false); }
  }
  return (
    <form className="comment-form" onSubmit={submit}>
      <textarea aria-label={label} value={text} maxLength={4000} rows={3} onChange={(e) => setText(e.target.value)} />
      <div className="actions">
        <button type="submit" className="tb primary" disabled={busy || text.trim().length === 0}>{ok}</button>
        {onCancel && <button type="button" className="tb" onClick={onCancel}>Cancel</button>}
        {error && <span className="form-error" role="alert">{error}</span>}
      </div>
    </form>
  );
}

function OneComment({ c, onChanged, onReply }: { c: Comment; onChanged: () => Promise<void>; onReply?: () => void }) {
  const dialogs = useDialogs();
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Your own comment goes for good; a moderator's delete of someone else's can be restored for 30 days.
  const mine = c.canEdit || (c.canDelete && !c.canModerate);
  async function remove() {
    const body = mine ? "The text is removed for good." : "Only the space's Supervisors and Owners will see it. It can be restored for 30 days.";
    if (!await dialogs.confirm({ title: "Delete this comment?", body, ok: "Delete", danger: true, focusCancel: true })) return;
    try { await api.del(`/comments/${c.id}`); await onChanged(); }
    catch (err) { setError(why(err)); }
  }
  async function moderate(what: "hide" | "restore") {
    try { await api.post(`/comments/${c.id}/${what}`); await onChanged(); }
    catch (err) { setError(why(err)); }
  }

  if (!c.author) return <div className="comment placeholder"><p className="muted">{c.state === "hidden" ? "This comment was hidden." : "This comment was deleted."}</p></div>;
  return (
    <div className={`comment ${c.state}`}>
      <div className="comment-head">
        <b>{c.author.name}</b>
        <span className="muted small">{fmtDateTime(c.createdAt)}{c.editedAt && " (edited)"}</span>
        {c.state === "hidden" && <span className="muted small">Hidden by a moderator</span>}
        {c.state === "deleted" && <span className="muted small">Deleted by a moderator</span>}
      </div>
      {editing ? (
        <Composer label="Edit comment" initial={c.text ?? ""} ok="Save" onCancel={() => setEditing(false)}
          onSubmit={async (text) => { await api.put(`/comments/${c.id}`, { text }); setEditing(false); await onChanged(); }} />
      ) : (
        <p className="comment-text">{c.text}</p>
      )}
      {!editing && (
        <div className="comment-tools">
          {onReply && <button type="button" className="link" onClick={onReply}>Reply</button>}
          {c.canEdit && <button type="button" className="link" onClick={() => setEditing(true)}>Edit</button>}
          {c.canModerate && c.state === "visible" && <button type="button" className="link" onClick={() => void moderate("hide")}>Hide</button>}
          {c.canModerate && c.state !== "visible" && <button type="button" className="link" onClick={() => void moderate("restore")}>Restore</button>}
          {c.canDelete && c.state !== "deleted" && <button type="button" className="link" onClick={() => void remove()}>Delete</button>}
        </div>
      )}
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
  );
}

/** The comments under a page: the list, replies one level deep, and the box to write one. */
export function PageComments({ pageId }: { pageId: string }) {
  const [list, setList] = useState<CommentList | null>(null);
  const [replyTo, setReplyTo] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { const l = await api.get<CommentList>(`/pages/${pageId}/comments`); setList(Array.isArray(l?.comments) ? l : null); }
    catch { setList(null); }
  }, [pageId]);
  useEffect(() => { setList(null); setReplyTo(null); void load(); }, [load]);
  if (!list || (!list.enabled && list.comments.length === 0)) return null;

  const post = async (text: string, parentId?: string) => {
    await api.post(`/pages/${pageId}/comments`, { text, parentId });
    setReplyTo(null);
    await load();
  };

  return (
    <section className="comments" aria-label="Comments">
      <h2 className="s">Comments{list.total > 0 && ` (${list.total})`}</h2>
      {!list.enabled && <p className="muted" role="status">Comments are off for this page. Only the space's Supervisors and Owners see these.</p>}
      {list.comments.map((c) => (
        <div key={c.id} className="comment-thread">
          <OneComment c={c} onChanged={load} onReply={list.canComment && c.state === "visible" && c.author ? () => setReplyTo(c.id) : undefined} />
          <div className="replies">
            {c.replies.map((r) => <OneComment key={r.id} c={r} onChanged={load} />)}
            {replyTo === c.id && <Composer label="Reply" ok="Reply" onCancel={() => setReplyTo(null)} onSubmit={(text) => post(text, c.id)} />}
          </div>
        </div>
      ))}
      {list.canComment && <Composer label="Add a comment" ok="Comment" onSubmit={(text) => post(text)} />}
    </section>
  );
}
