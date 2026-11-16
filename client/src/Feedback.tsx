import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";

/** The comment and rating switches at site level (contracts/behaviour/comments.md). */
export type FeedbackSettings = { commentsEnabled: boolean; ratingsEnabled: boolean };
/** One feature's switch at each level; effective only if all three are on. */
export type FeedbackLevels = { site: boolean; space: boolean; page: boolean; effective: boolean };
export type PageFeedback = { comments: FeedbackLevels; ratings: FeedbackLevels };

/** Admin: the site switches. Off here turns the feature off on every page. */
export function FeedbackSettingsPanel() {
  const [s, setS] = useState<FeedbackSettings | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  useEffect(() => { void api.get<FeedbackSettings>("/admin/feedback").then(setS); }, []);
  if (!s) return null;

  async function save(e: FormEvent) {
    e.preventDefault();
    try { setS(await api.put<FeedbackSettings>("/admin/feedback", s)); setStatus("Saved."); }
    catch (err) { setStatus(err instanceof ApiError && err.body?.detail ? err.body.detail : "Not saved."); }
  }

  return (
    <form className="form" aria-label="Comments and ratings" onSubmit={save}>
      <p className="muted">Off here turns the feature off on every page. Spaces and pages can only turn it off for themselves.</p>
      <span className="lbl">Comments</span>
      <label className="check"><input type="checkbox" checked={s.commentsEnabled} onChange={(e) => { setS({ ...s, commentsEnabled: e.target.checked }); setStatus(null); }} />Allow comments on pages</label>
      <span className="lbl">Ratings</span>
      <label className="check"><input type="checkbox" checked={s.ratingsEnabled} onChange={(e) => { setS({ ...s, ratingsEnabled: e.target.checked }); setStatus(null); }} />Allow ratings on pages</label>
      <div className="actions">
        <button type="submit" className="tb primary">Save</button>
        {status && <span className="muted" role="status">{status}</span>}
      </div>
    </form>
  );
}

/** Why a feature is off above the page, or null when only the page's own switch decides. */
function offAbove(l: FeedbackLevels): string | null {
  return !l.site ? "Off for the whole site." : !l.space ? "Off for this space." : null;
}

/** The page's own switches, for the space's Supervisors and Owners. A page can only turn a feature off, never back on over a higher level. */
export function PageFeedbackSwitches({ pageId }: { pageId: string }) {
  const [f, setF] = useState<PageFeedback | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setF(null); void api.get<PageFeedback>(`/pages/${pageId}/feedback`).then((x) => setF(x?.comments && x.ratings ? x : null)).catch(() => setF(null)); }, [pageId]);
  if (!f) return null;

  async function toggle(key: "commentsEnabled" | "ratingsEnabled", on: boolean) {
    setError(null);
    try { setF(await api.put<PageFeedback>(`/pages/${pageId}/feedback`, { [key]: on })); }
    catch (err) { setError(err instanceof ApiError && err.status === 403 ? "Only the space's Supervisors and Owners change this." : "Not saved."); }
  }

  const row = (key: "commentsEnabled" | "ratingsEnabled", label: string, l: FeedbackLevels) => (
    <div className="feedback-switch">
      <label className="check"><input type="checkbox" checked={l.page} onChange={(e) => void toggle(key, e.target.checked)} />{label}</label>
      {offAbove(l) && <div className="muted small">{offAbove(l)}</div>}
    </div>
  );

  return (
    <>
      <h3>Feedback</h3>
      {row("commentsEnabled", "Comments", f.comments)}
      {row("ratingsEnabled", "Ratings", f.ratings)}
      {error && <p className="form-error" role="alert">{error}</p>}
    </>
  );
}
