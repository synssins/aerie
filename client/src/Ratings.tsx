import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "./api";

/** The caller's own rating and what they see of the page's ratings (contracts/behaviour/ratings.md). Readers never see negatives. */
export type MyRating = { value: "positive" | "negative"; solved: boolean; reason: string | null; version: string; createdAt: string; updatedAt: string };
export type RatingSummary = { enabled: boolean; canRate: boolean; canSeeAll: boolean; positive: number; solved: number; negative?: number; mine: MyRating | null };

/** Under a page: rate it positive or negative (with an optional reason), or say it solved your problem. */
export function PageRating({ pageId }: { pageId: string }) {
  const [s, setS] = useState<RatingSummary | null>(null);
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { const r = await api.get<RatingSummary>(`/pages/${pageId}/ratings`); setS(typeof r?.positive === "number" ? r : null); }
    catch { setS(null); }
  }, [pageId]);
  useEffect(() => { setS(null); setAsking(false); setReason(""); void load(); }, [load]);
  if (!s || (!s.enabled && !s.mine)) return null;

  async function save(body: { value: "positive" | "negative"; solved?: boolean; reason?: string }) {
    setError(null);
    try { setS(await api.put<RatingSummary>(`/pages/${pageId}/ratings/mine`, body)); setAsking(false); }
    catch (err) { setError(err instanceof ApiError && err.status === 429 ? "Too many changes. Wait a minute." : err instanceof ApiError && err.body?.detail ? err.body.detail : "Not saved."); }
  }
  async function remove() {
    setError(null);
    try { await api.del(`/pages/${pageId}/ratings/mine`); await load(); }
    catch { setError("Not removed."); }
  }

  const mine = s.mine;
  return (
    <section className="rating" aria-label="Rate this page">
      {s.canRate && (
        <div className="rating-row">
          <span>Was this page helpful?</span>
          <button type="button" className={`tb ${mine?.value === "positive" && !mine.solved ? "on" : ""}`} aria-pressed={mine?.value === "positive" && !mine.solved} onClick={() => void save({ value: "positive" })}>Yes</button>
          <button type="button" className={`tb ${mine?.value === "negative" ? "on" : ""}`} aria-pressed={mine?.value === "negative"} onClick={() => { setAsking(true); setReason(mine?.reason ?? ""); }}>No</button>
          <button type="button" className={`tb ${mine?.solved ? "on" : ""}`} aria-pressed={!!mine?.solved} onClick={() => void save({ value: "positive", solved: true })}>This solved my problem</button>
        </div>
      )}
      {asking && (
        <form className="rating-reason" onSubmit={(e) => { e.preventDefault(); void save({ value: "negative", reason }); }}>
          <label className="lbl" htmlFor="rating-reason">What was missing or wrong? (optional, seen only by the space's Supervisors and Owners)</label>
          <textarea id="rating-reason" value={reason} maxLength={280} rows={2} onChange={(e) => setReason(e.target.value)} />
          <div className="actions">
            <button type="submit" className="tb primary">Send</button>
            <button type="button" className="tb" onClick={() => setAsking(false)}>Cancel</button>
          </div>
        </form>
      )}
      {(s.positive > 0 || s.solved > 0) && (
        <p className="muted small">{s.positive} found this helpful{s.solved > 0 && `; it solved the problem for ${s.solved}`}.</p>
      )}
      {mine && <p className="muted small">Your rating: {mine.value === "negative" ? "not helpful" : mine.solved ? "solved my problem" : "helpful"}. <button type="button" className="link" onClick={() => void remove()}>Remove</button></p>}
      {error && <p className="form-error" role="alert">{error}</p>}
    </section>
  );
}
