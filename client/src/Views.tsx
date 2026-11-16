import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from "react";
import { AskBox } from "./AiSearch";
import { ChevronRight, FileText, Folder, House, Megaphone, RotateCcw, Trash2, X } from "lucide-react";
import { Link, to } from "./Link";
import { api, fmtDateTime, type Announcement, type BinItem, type DiffLine, type Home, type Me, type Page, type PageVersion, type SearchResponse, type Space } from "./api";
import { useDialogs } from "./Dialogs";

const fmtDate = (iso: string) => new Date(iso).toLocaleDateString(undefined, { dateStyle: "medium" });

function Crumbs({ onHome, children }: { onHome: () => void; children?: ReactNode }) {
  return (
    <nav className="crumb" aria-label="Breadcrumb">
      <Link href={to.home()} className="crumb-home" onNav={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></Link>
      {children}
    </nav>
  );
}
const Sep = () => <ChevronRight className="ic sm" aria-hidden />;

/** Recycle bin table. spaceId null = every space (administrators). */
export function BinTable({ spaceId, canEmpty, reloadKey, onRestored, onOpen, onEmptied }: { spaceId: string | null; canEmpty: boolean; reloadKey?: number; onRestored: (spaceId: string) => void; onOpen?: (id: string) => void; onEmptied?: () => void }) {
  const dialogs = useDialogs();
  const [items, setItems] = useState<BinItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => api.get<BinItem[]>(spaceId ? `/spaces/${spaceId}/bin` : "/admin/bin").then(setItems).catch(() => setItems([])), [spaceId]);
  useEffect(() => { void load(); }, [load, reloadKey]);

  async function restore(it: BinItem) {
    setError(null);
    try { await api.post(`/pages/${it.id}/restore`); onRestored(it.spaceId); await load(); }
    catch { setError("Not restored."); }
  }

  async function empty() {
    if (!spaceId || !await dialogs.confirm({ title: "Empty the recycle bin?", ok: "Empty", danger: true })) return;
    await api.post(`/spaces/${spaceId}/bin/empty`);
    onEmptied?.();
    await load();
  }

  if (!items) return null;
  return (
    <>
      {canEmpty && items.length > 0 && <div className="bar-row"><button className="tb" onClick={() => void empty()}><Trash2 className="ic" aria-hidden />Empty bin</button></div>}
      {error && <p className="form-error" role="alert">{error}</p>}
      <table className="grid-table">
        <thead><tr><th>Name</th><th>Location</th><th>Items</th><th>Deleted</th><th>By</th>{!spaceId && <th>Space bin</th>}<th /></tr></thead>
        <tbody>
          {items.map((it) => (
            <tr key={it.id}>
              <td><span className="cell-ic">{it.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}{onOpen ? <button className="cell-link" onClick={() => onOpen(it.id)}>{it.title}</button> : it.title}</span></td>
              <td className="muted">{it.location}</td>
              <td className="muted">{it.items}</td>
              <td className="muted">{fmtDateTime(it.deletedAt)}</td>
              <td className="muted">{it.deletedBy}</td>
              {!spaceId && <td className="muted">{it.hidden ? "Emptied" : ""}</td>}
              <td className="actions"><button className="tb" onClick={() => void restore(it)}><RotateCcw className="ic" aria-hidden />Restore</button></td>
            </tr>
          ))}
          {items.length === 0 && <tr><td colSpan={spaceId ? 6 : 7} className="muted">Empty.</td></tr>}
        </tbody>
      </table>
    </>
  );
}

export function BinView({ space, onHome, onOpenSpace, onRestored, onOpen, onEmptied }: { space: Space; onHome: () => void; onOpenSpace: () => void; onRestored: (spaceId: string) => void; onOpen?: (id: string) => void; onEmptied?: () => void }) {
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome}><Sep /><button className="crumb-link" onClick={onOpenSpace}>{space.name}</button><Sep /><b>Recycle bin</b></Crumbs>
          <div className="titlebar"><h1>Recycle bin</h1></div>
          <BinTable spaceId={space.id} canEmpty onRestored={onRestored} onOpen={onOpen} onEmptied={onEmptied} />
        </div>
      </main>
      <aside className="side" aria-label="Details"><h3>Space</h3><div>{space.name}</div></aside>
    </>
  );
}

/** tag: every page with that tag (in the spaces the person may read), instead of a text search. */
export function SearchView({ query, tag = null, onHome, onOpen, onTag, aiSearch = false }: { query: string; tag?: string | null; onHome: () => void; onOpen: (id: string) => void; onTag?: (tag: string) => void; aiSearch?: boolean }) {
  const [result, setResult] = useState<SearchResponse | null>(null);
  const [offset, setOffset] = useState(0);
  useEffect(() => { setOffset(0); }, [query, tag]);
  useEffect(() => {
    let live = true;
    setResult(null);
    void api.get<SearchResponse>(`/search?q=${encodeURIComponent(query)}${tag ? `&tag=${encodeURIComponent(tag)}` : ""}&limit=20&offset=${offset}`).then((r) => { if (live) setResult(r); }).catch(() => { if (live) setResult({ query, total: 0, limit: 20, offset, hits: [] }); });
    return () => { live = false; };
  }, [query, tag, offset]);

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome}><Sep /><b>Search</b></Crumbs>
          <div className="titlebar"><h1>{tag ? <>Tag <span className="chip">{tag}</span></> : `“${query}”`}</h1></div>
          {aiSearch && !tag && <AskBox key={query} initial={query} onOpen={onOpen} />}
          {result && <p className="muted">{result.total === 1 ? "1 result" : `${result.total} results`}</p>}
          <div className="results">
            {result?.hits.map((h) => (
              // The title opens the page; a tag opens the list of pages with that tag. Two separate buttons, so neither click is the other's.
              <div key={h.id} className="result">
                <Link href={to.page(h.id)} className="result-title" onNav={() => onOpen(h.id)}>{h.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}{h.title}</Link>
                <span className="muted small">{[h.spaceName, ...h.path].join(" / ")} · {fmtDate(h.updatedAt)}</span>
                {h.snippet && <span className="result-snippet">{h.snippet}</span>}
                {h.tags.length > 0 && <span className="tags flat">{h.tags.map((t) => (onTag ? <Link key={t} href={to.tag(t)} className="chip" title={t} onNav={() => onTag(t)}>{t}</Link> : <span key={t} className="chip">{t}</span>))}</span>}
              </div>
            ))}
          </div>
          {result && result.total > result.limit && (
            <div className="bar-row">
              <button className="tb" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 20))}>Previous</button>
              <span className="muted small">{offset + 1}–{Math.min(offset + 20, result.total)} of {result.total}</span>
              <button className="tb" disabled={offset + 20 >= result.total} onClick={() => setOffset(offset + 20)}>Next</button>
            </div>
          )}
        </div>
      </main>
      <aside className="side" aria-label="Details" />
    </>
  );
}

type SideRow = { left: DiffLine | null; right: DiffLine | null };

/** Pairs removed lines with the added lines that replaced them, for the two-column view. */
function sideBySide(lines: DiffLine[]): SideRow[] {
  const rows: SideRow[] = [];
  for (let i = 0; i < lines.length;) {
    if (lines[i].op === "Same") { rows.push({ left: lines[i], right: lines[i] }); i++; continue; }
    const dels: DiffLine[] = [], adds: DiffLine[] = [];
    while (i < lines.length && lines[i].op === "Del") dels.push(lines[i++]);
    while (i < lines.length && lines[i].op === "Add") adds.push(lines[i++]);
    for (let k = 0; k < Math.max(dels.length, adds.length); k++) rows.push({ left: dels[k] ?? null, right: adds[k] ?? null });
  }
  return rows;
}

export function HistoryView({ page, spaceName, onHome, onOpenSpace, onBack, onRestored }: {
  page: Page; spaceName: string; onHome: () => void; onOpenSpace: () => void; onBack: () => void; onRestored: (p: Page) => void;
}) {
  const dialogs = useDialogs();
  const [versions, setVersions] = useState<PageVersion[]>([]);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [mode, setMode] = useState<"inline" | "side">("inline");
  const [lines, setLines] = useState<DiffLine[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void api.get<PageVersion[]>(`/pages/${page.id}/versions`).then((v) => {
      setVersions(v);
      setTo(v[0]?.version ?? "");
      setFrom(v[1]?.version ?? v[0]?.version ?? "");
    });
  }, [page.id, page.version]);
  useEffect(() => {
    if (!from || !to) return;
    let live = true;
    void api.get<{ lines: DiffLine[] }>(`/pages/${page.id}/diff?from=${from}&to=${to}`).then((d) => { if (live) setLines(d.lines); });
    return () => { live = false; };
  }, [page.id, from, to]);

  async function restore(version: string) {
    if (!await dialogs.confirm({ title: `Restore version ${version}?`, ok: "Restore" })) return;
    setError(null);
    try { onRestored(await api.post<Page>(`/pages/${page.id}/versions/${version}/restore`)); }
    catch { setError("Not restored."); }
  }

  const added = lines?.filter((l) => l.op === "Add").length ?? 0;
  const removed = lines?.filter((l) => l.op === "Del").length ?? 0;
  const rows = useMemo(() => (lines ? sideBySide(lines) : []), [lines]);
  const mark = (op: DiffLine["op"]) => (op === "Add" ? "+" : op === "Del" ? "−" : " ");

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome}>
            <Sep /><button className="crumb-link" onClick={onOpenSpace}>{spaceName}</button>
            <Sep /><button className="crumb-link" onClick={onBack}>{page.title}</button>
            <Sep /><b>History</b>
          </Crumbs>
          <div className="titlebar"><h1>History</h1></div>
          {error && <p className="form-error" role="alert">{error}</p>}

          <div className="bar-row">
            <label className="inline">Compare
              <select className="sel" value={from} onChange={(e) => setFrom(e.target.value)}>{versions.map((v) => <option key={v.version} value={v.version}>{v.version}</option>)}</select>
            </label>
            <label className="inline">with
              <select className="sel" value={to} onChange={(e) => setTo(e.target.value)}>{versions.map((v) => <option key={v.version} value={v.version}>{v.version}</option>)}</select>
            </label>
            <div className="seg" role="group" aria-label="Diff layout">
              <button className={mode === "inline" ? "on" : ""} aria-pressed={mode === "inline"} onClick={() => setMode("inline")}>Inline</button>
              <button className={mode === "side" ? "on" : ""} aria-pressed={mode === "side"} onClick={() => setMode("side")}>Side by side</button>
            </div>
          </div>

          {lines && (from === to || (added === 0 && removed === 0)
            ? <p className="muted">No differences.</p>
            : mode === "inline" ? (
              <div className="diff" role="table" aria-label={`Changes from ${from} to ${to}`}>
                {lines.map((l, i) => <div key={i} role="row" className={`dl ${l.op.toLowerCase()}`}><span className="dm" aria-label={l.op === "Add" ? "Added" : l.op === "Del" ? "Removed" : undefined}>{mark(l.op)}</span><span className="dt">{l.text || " "}</span></div>)}
              </div>
            ) : (
              <div className="diff side" role="table" aria-label={`Changes from ${from} to ${to}`}>
                {rows.map((r, i) => (
                  <div key={i} role="row" className="dr">
                    <div className={`dl ${r.left ? r.left.op.toLowerCase() : "none"}`}><span className="dm">{r.left ? mark(r.left.op) : ""}</span><span className="dt">{r.left?.text || " "}</span></div>
                    <div className={`dl ${r.right ? r.right.op.toLowerCase() : "none"}`}><span className="dm">{r.right ? mark(r.right.op) : ""}</span><span className="dt">{r.right?.text || " "}</span></div>
                  </div>
                ))}
              </div>
            ))}

          <h2 className="s">Versions</h2>
          <table className="grid-table">
            <thead><tr><th>Version</th><th>Date</th><th>By</th><th>Note</th><th /></tr></thead>
            <tbody>
              {versions.map((v, i) => (
                <tr key={v.version}>
                  <td>{v.version}</td><td className="muted">{fmtDateTime(v.at)}</td><td>{v.author}</td><td className="muted">{v.note}</td>
                  <td className="actions">{page.canEdit && i > 0 && <button className="tb" onClick={() => void restore(v.version)}><RotateCcw className="ic" aria-hidden />Restore</button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Current version</h3>
        <div>{page.version}</div>
        <h3>Versions</h3>
        <div>{versions.length}</div>
        {lines && from !== to && (
          <>
            <h3>Changes {from} → {to}</h3>
            <div>{added} added · {removed} removed</div>
          </>
        )}
      </aside>
    </>
  );
}

function AnnouncementDialog({ onPosted, onClose }: { onPosted: () => void; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [title, setTitle] = useState("");
  const [body, setBody] = useState("");
  const [ends, setEnds] = useState("");
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { ref.current?.showModal(); }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    try {
      // The notice stays up through the end of the chosen day.
      await api.post("/announcements", { title, body, expiresAt: ends ? new Date(`${ends}T23:59:59`).toISOString() : null });
      onPosted();
    } catch { setError("Not posted. Check the end date."); }
  }

  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="ann-title">
      <form onSubmit={submit}>
        <h2 id="ann-title">New announcement</h2>
        <label>Title<input autoFocus value={title} maxLength={120} onChange={(e) => setTitle(e.target.value)} required /></label>
        <label>Text<textarea value={body} maxLength={2000} rows={4} onChange={(e) => setBody(e.target.value)} /></label>
        <label className="narrow">Ends<input type="date" value={ends} onChange={(e) => setEnds(e.target.value)} /></label>
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="dlg-actions">
          <button type="button" className="tb" onClick={onClose}>Cancel</button>
          <button type="submit" className="tb primary" disabled={!title.trim()}>Post</button>
        </div>
      </form>
    </dialog>
  );
}

export type RecentView = { id: string; title: string };

export function HomeView({ me, spaces, viewed, onOpenSpace, onOpenPage }: {
  me: Me | null; spaces: Space[]; viewed: RecentView[]; onOpenSpace: (s: Space) => void; onOpenPage: (id: string) => void;
}) {
  const dialogs = useDialogs();
  const [home, setHome] = useState<Home | null>(null);
  const [posting, setPosting] = useState(false);
  const load = useCallback(() => api.get<Home>("/home").then(setHome).catch(() => setHome({ announcements: [], recent: [] })), []);
  useEffect(() => { void load(); }, [load]);

  async function remove(a: Announcement) {
    if (!await dialogs.confirm({ title: `Remove “${a.title}”?`, ok: "Remove", danger: true })) return;
    await api.del(`/announcements/${a.id}`);
    await load();
  }

  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb"><span className="crumb-home"><House className="ic" aria-hidden /></span></nav>
          <div className="titlebar">
            <h1>{me ? `Welcome, ${me.displayName}` : "Welcome"}</h1>
            <div className="ptools">{me?.canAnnounce && <button className="tb" onClick={() => setPosting(true)}><Megaphone className="ic" aria-hidden />New announcement</button>}</div>
          </div>

          {home?.announcements.map((a) => (
            <section key={a.id} className="announce" aria-label="Announcement">
              <Megaphone className="ic" aria-hidden />
              <div className="grow">
                <b>{a.title}</b>
                {a.body && <p>{a.body}</p>}
                <div className="muted small">{a.by} · {fmtDate(a.createdAt)}</div>
              </div>
              {me?.canAnnounce && <button className="tb icon" aria-label={`Remove ${a.title}`} title="Remove" onClick={() => void remove(a)}><X className="ic" aria-hidden /></button>}
            </section>
          ))}

          <div className="home-grid">
            <section>
              <h2 className="s">Your spaces</h2>
              <div className="listing">
                {spaces.map((s) => <Link key={s.id} href={to.space(s.id)} className="node" title={s.name} onNav={() => onOpenSpace(s)}><Folder className="ic ft" aria-hidden /><span className="grow">{s.name}</span></Link>)}
                {spaces.length === 0 && <p className="muted">No spaces.</p>}
              </div>
            </section>
            {viewed.length > 0 && (
              <section>
                <h2 className="s">Recently viewed</h2>
                <div className="listing">
                  {viewed.map((v) => <Link key={v.id} href={to.page(v.id)} className="node" title={v.title} onNav={() => onOpenPage(v.id)}><FileText className="ic pg" aria-hidden /><span className="grow">{v.title}</span></Link>)}
                </div>
              </section>
            )}
            {home && home.recent.length > 0 && (
              <section>
                <h2 className="s">Recently updated</h2>
                <div className="listing">
                  {home.recent.map((r) => (
                    <Link key={r.id} href={to.page(r.id)} className="node two" title={r.title} onNav={() => onOpenPage(r.id)}>
                      <FileText className="ic pg" aria-hidden />
                      <span className="grow">{r.title}<small>{r.spaceName} · {r.updatedBy} · {fmtDate(r.updatedAt)}</small></span>
                    </Link>
                  ))}
                </div>
              </section>
            )}
          </div>
        </div>
      </main>
      <aside className="side" aria-label="Details" />
      {posting && <AnnouncementDialog onClose={() => setPosting(false)} onPosted={() => { setPosting(false); void load(); }} />}
    </>
  );
}
