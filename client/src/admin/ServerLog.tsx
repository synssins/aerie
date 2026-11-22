import { useCallback, useEffect, useState } from "react";
import { api } from "../api";

export type ServerLogRow = { id: number; at: string; level: "Information" | "Warning" | "Error" | "Critical"; category: string; message: string; exception: string | null };

const PAGE = 100;
const LEVELS = [["", "All"], ["Warning", "Warnings and errors"], ["Error", "Errors"], ["Critical", "Critical only"]] as const;
const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "medium" });

/** The query for one page of the log: only the filters that are set. */
export function serverLogQuery(level: string, text: string, take: number, offset: number) {
  const p = new URLSearchParams({ take: String(take), offset: String(offset) });
  if (level) p.set("level", level);
  if (text.trim()) p.set("text", text.trim());
  return `/admin/server-log?${p}`;
}

/** Administration › Security › Server log: the server's own log, newest first, filtered by level and text, a page more at a time. */
export function ServerLog() {
  const [rows, setRows] = useState<ServerLogRow[]>([]);
  const [more, setMore] = useState(false);
  const [level, setLevel] = useState("");
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  // One more than a page tells whether there is another.
  const load = useCallback(async (offset: number) => {
    const r = await api.get<ServerLogRow[]>(serverLogQuery(level, text, PAGE + 1, offset));
    setMore(r.length > PAGE);
    return r.slice(0, PAGE);
  }, [level, text]);

  useEffect(() => {
    let live = true;
    setError(null);
    load(0).then((r) => { if (live) setRows(r); }).catch(() => { if (live) { setRows([]); setMore(false); setError("Not loaded."); } });
    return () => { live = false; };
  }, [load]);

  async function refresh() {
    setError(null);
    try { setRows(await load(0)); } catch { setError("Not loaded."); }
  }
  async function loadMore() {
    try { const next = await load(rows.length); setRows((r) => [...r, ...next]); } catch { setError("Not loaded."); }
  }

  return (
    <>
      <p className="muted small">The server's own log: warnings and errors, and what the app itself reports. Kept 30 days, at most 50,000 lines.</p>
      <div className="audit-filters">
        <label>Level
          <select className="sel" value={level} onChange={(e) => setLevel(e.target.value)}>
            {LEVELS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
          </select>
        </label>
        <label>Text<input type="text" value={text} maxLength={200} onChange={(e) => setText(e.target.value)} /></label>
        <button className="tb" onClick={() => void refresh()}>Refresh</button>
      </div>
      {error && <p className="form-error" role="alert">{error}</p>}
      <table className="grid-table server-log">
        <thead><tr><th>Time</th><th>Level</th><th>Category</th><th>Message</th></tr></thead>
        <tbody>
          {rows.map((e) => (
            <tr key={e.id}>
              <td className="muted nowrap">{fmt(e.at)}</td>
              <td>{e.level}</td>
              <td className="muted mono">{e.category}</td>
              <td>
                <span className="log-message">{e.message}</span>
                {e.exception && (
                  <details>
                    <summary>Exception</summary>
                    <pre className="mono">{e.exception}</pre>
                  </details>
                )}
              </td>
            </tr>
          ))}
          {rows.length === 0 && <tr><td colSpan={4} className="muted">No entries.</td></tr>}
        </tbody>
      </table>
      {more && <div className="bar-row"><button className="tb" onClick={() => void loadMore()}>Load more</button></div>}
    </>
  );
}
