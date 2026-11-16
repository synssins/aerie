import { useState, type FormEvent } from "react";
import { api, ApiError } from "./api";
import { Link, to } from "./Link";

/** The answer to a question (contracts/behaviour/ai-search.md, The answer). The text is plain text; the only links are aerie's own. */
export type SearchAnswer = {
  outcome: "answered" | "no_answer" | "nothing_found"; answer: string;
  sources: { n: number; pageId: string; title: string; spaceId: string; spaceName: string; url: string }[];
};

function why(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 429) return "Too many questions. Wait a minute.";
    if (err.status === 503) return "The AI did not answer. Try again later, or use ordinary search.";
    if (err.status === 400) return "Ask in 3 to 500 characters.";
    if (err.status === 403) return "AI search is not open to you.";
    if (err.status === 422 && err.body?.detail) return err.body.detail;
  }
  return "No answer. Try again later.";
}

/** The "ask" box on the search page, for people AI search is open to (MeResponse.aiSearch). */
export function AskBox({ initial = "", onOpen }: { initial?: string; onOpen: (id: string) => void }) {
  const [question, setQuestion] = useState(initial);
  const [answer, setAnswer] = useState<SearchAnswer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function ask(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setAnswer(null);
    try { setAnswer(await api.post<SearchAnswer>("/search/answer", { question })); }
    catch (err) { setError(why(err)); }
    finally { setBusy(false); }
  }

  return (
    <section className="ask" aria-label="Ask">
      <form className="ask-form" onSubmit={ask}>
        <label className="lbl" htmlFor="ask-q">Ask a question. The answer comes from the pages you can read.</label>
        <div className="ask-row">
          <input id="ask-q" type="text" value={question} maxLength={500} onChange={(e) => setQuestion(e.target.value)} />
          <button type="submit" className="tb primary" disabled={busy || question.trim().length < 3}>{busy ? "Asking…" : "Ask"}</button>
        </div>
      </form>
      {error && <p className="form-error" role="alert">{error}</p>}
      {answer && (
        <div className="ask-answer" role="status">
          <p className="comment-text">{answer.answer}</p>
          {answer.sources.length > 0 && (
            <ol className="ask-sources" aria-label="Sources">
              {answer.sources.map((s) => (
                <li key={s.n} value={s.n}>
                  <Link href={to.page(s.pageId)} onNav={() => onOpen(s.pageId)}>{s.title}</Link> <span className="muted small">{s.spaceName}</span>
                </li>
              ))}
            </ol>
          )}
        </div>
      )}
    </section>
  );
}
