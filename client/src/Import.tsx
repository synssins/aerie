import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type DragEvent } from "react";
import { EditorContent, useEditor } from "@tiptap/react";
import { ChevronDown, ChevronRight, FileUp, FolderUp, House, X } from "lucide-react";
import { Link, to } from "./Link";
import { api, ApiError, type ImportFile, type ImportItem, type ImportItemRow, type ImportList, type ImportSpace, type ImportStatus, type TreeNode } from "./api";
import { useDialogs } from "./Dialogs";
import { editorExtensions, fromEditor, makeExtensions, toEditor } from "./editor/extensions";
import { Toolbar } from "./PageView";
import { REVIEW_CHOICES } from "./PageDetails";
import { TagInput } from "./TagInput";

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const problem = (e: unknown, fallback: string) => (e instanceof ApiError && (e.body?.detail || (e.body?.errors && Object.values(e.body.errors).flat()[0]))) || fallback;
const STATUS: Record<ImportStatus, string> = {
  Queued: "Waiting", Working: "Drafting", Ready: "Ready to review", Failed: "Failed", Blocked: "Blocked", Accepted: "Accepted", Discarded: "Discarded", Rejected: "Rejected",
};
const fileUrl = (id: string) => `/api/v1/imports/files/${id.replace(/-/g, "")}`;
const seconds = (ms: number) => (ms < 60_000 ? `${Math.max(1, Math.round(ms / 1000))} s` : `${Math.floor(ms / 60_000)} min ${Math.round((ms % 60_000) / 1000)} s`);
const size = (n: number) => (n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${Math.round(n / 1024)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`);

/** What a row says of its state, in plain words; for a blocked or failed item, why. */
export function statusText(i: Pick<ImportItemRow, "status" | "error" | "updatedAt" | "draftMs">, now = Date.now()) {
  if (i.status === "Working" && i.updatedAt) return `Drafting · ${seconds(now - new Date(i.updatedAt).getTime())}`;
  if (i.status === "Ready" && i.draftMs) return `Ready to review · drafted in ${seconds(i.draftMs)}`;
  return i.error ? `${STATUS[i.status]}: ${i.error}` : STATUS[i.status];
}

type Picked = { path: string; file: File };

/** Every file under a dropped item, folders included, with its path inside what was dropped. */
async function walk(entry: FileSystemEntry, prefix = ""): Promise<Picked[]> {
  if (entry.isFile) return [{ path: prefix + entry.name, file: await new Promise<File>((ok, fail) => (entry as FileSystemFileEntry).file(ok, fail)) }];
  const reader = (entry as FileSystemDirectoryEntry).createReader();
  const all: FileSystemEntry[] = [];
  for (;;) {
    const batch = await new Promise<FileSystemEntry[]>((ok, fail) => reader.readEntries(ok, fail));
    if (batch.length === 0) break;
    all.push(...batch);
  }
  return (await Promise.all(all.map((e) => walk(e, `${prefix}${entry.name}/`)))).flat();
}

/** The import inbox: upload files (folders, zips) and the drafts that wait for review. Only the importer sees them. */
export function ImportView({ itemId, onOpenItem, onOpenPage, onHome }: {
  itemId: string | null; onOpenItem: (id: string | null) => void; onOpenPage: (id: string) => void; onHome: () => void;
}) {
  return itemId
    ? <ImportReview key={itemId} id={itemId} onBack={() => onOpenItem(null)} onOpenPage={onOpenPage} onHome={onHome} />
    : <ImportInbox onOpen={(id) => onOpenItem(id)} onHome={onHome} />;
}

function Crumbs({ onHome, item, onInbox }: { onHome: () => void; item?: string; onInbox: () => void }) {
  return (
    <nav className="crumb" aria-label="Breadcrumb">
      <Link href={to.home()} className="crumb-home" onNav={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></Link><ChevronRight className="ic sm" aria-hidden />
      {item ? <><Link href={to.imports()} className="crumb-link" onNav={onInbox}>Import inbox</Link><ChevronRight className="ic sm" aria-hidden /><b title={item}>{item}</b></> : <b>Import inbox</b>}
    </nav>
  );
}

function ImportInbox({ onOpen, onHome }: { onOpen: (id: string) => void; onHome: () => void }) {
  const dialogs = useDialogs();
  const [list, setList] = useState<ImportList | null>(null);
  const [target, setTarget] = useState("");
  const [queue, setQueue] = useState<Picked[]>([]);
  const [progress, setProgress] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [over, setOver] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const filesRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  // From the server every time this screen is shown (a link, Back, a new tab): never a copy kept in the page.
  const load = useCallback(() => api.get<ImportList>("/imports").then((l) => { setList(l); setError(null); }).catch(() => setError("The inbox could not be loaded.")), []);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    const again = () => { if (document.visibilityState === "visible") void load(); };
    window.addEventListener("pageshow", again);
    document.addEventListener("visibilitychange", again);
    return () => { window.removeEventListener("pageshow", again); document.removeEventListener("visibilitychange", again); };
  }, [load]);
  const busy = !!list && (list.batches.some((b) => b.started && !b.grouped && !b.error) || list.items.some((i) => i.status === "Queued" || i.status === "Working"));
  useEffect(() => { if (!busy) return; const t = setInterval(() => { void load(); setNow(Date.now()); }, 2000); return () => clearInterval(t); }, [busy, load]);
  useEffect(() => { folderRef.current?.setAttribute("webkitdirectory", ""); }, []);

  const add = (files: Picked[]) => { setQueue((q) => [...q, ...files.filter((f) => !q.some((x) => x.path === f.path))]); setError(null); };

  async function start() {
    if (queue.length === 0) return;
    setError(null);
    const failed: string[] = [];
    try {
      const batch = await api.post<{ id: string }>("/imports", { spaceId: target || null });
      for (let i = 0; i < queue.length; i++) {
        setProgress(`Uploading ${i + 1} of ${queue.length}`);
        const res = await fetch(`/api/v1/imports/${batch.id}/files`, {
          method: "POST", credentials: "same-origin", body: queue[i].file,
          headers: { "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(queue[i].path) },
        });
        if (!res.ok) { const b = await res.json().catch(() => null); failed.push(`${queue[i].path}: ${b?.detail ?? (b?.errors && Object.values(b.errors).flat()[0]) ?? "not uploaded"}`); }
      }
      await api.post(`/imports/${batch.id}/start`);
      setQueue([]);
      if (failed.length) setError(failed.join("\n"));
      await load();
    } catch (e) { setError(problem(e, "Not uploaded.")); }
    finally { setProgress(null); }
  }

  async function drop(e: DragEvent) {
    e.preventDefault();
    setOver(false);
    const entries = [...e.dataTransfer.items].map((i) => i.webkitGetAsEntry?.()).filter((x): x is FileSystemEntry => !!x);
    add(entries.length ? (await Promise.all(entries.map((x) => walk(x)))).flat() : [...e.dataTransfer.files].map((f) => ({ path: f.name, file: f })));
  }

  async function merge() {
    setError(null);
    try { await api.post("/imports/merge", { itemIds: [...picked] }); setPicked(new Set()); await load(); }
    catch (e) { setError(problem(e, "Not merged.")); }
  }

  async function discard(i: ImportItemRow) {
    if (!await dialogs.confirm({ title: `Discard “${i.title}”?`, body: "The draft and its original files are deleted permanently. This can't be undone.", ok: "Discard", danger: true, focusCancel: true })) return;
    try { await api.del(`/imports/items/${i.id}`); await load(); } catch (e) { setError(problem(e, "Not discarded.")); }
  }

  const spaces = list?.spaces ?? [];
  const items = list?.items.filter((i) => i.status !== "Accepted") ?? [];
  const mergeable = (s: ImportStatus) => s === "Ready" || s === "Failed" || s === "Queued" || s === "Rejected";
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} onInbox={() => {}} />
          <div className="titlebar"><h1>Import inbox</h1></div>
          {error && <p className="form-error pre" role="alert">{error}</p>}
          {list && !list.canImport ? <p className="muted">Import is not open to you.</p> : (
            <>
              <div className={`drop-zone ${over ? "over" : ""}`} role="region" aria-label="Drop files here"
                onDragEnter={(e) => { e.preventDefault(); setOver(true); }} onDragOver={(e) => { e.preventDefault(); setOver(true); }}
                onDragLeave={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false); }} onDrop={(e) => void drop(e)}>
                <FileUp className="ic lg" aria-hidden />
                <span className="drop-text">Drop files, folders or zips here, or{" "}
                  <button type="button" className="link" onClick={() => filesRef.current?.click()}>browse</button>
                </span>
                <button type="button" className="tb" onClick={() => folderRef.current?.click()}><FolderUp className="ic" aria-hidden />Folder</button>
                <input ref={filesRef} type="file" multiple hidden aria-label="Files" onChange={(e) => { add([...e.target.files ?? []].map((f) => ({ path: f.name, file: f }))); e.target.value = ""; }} />
                <input ref={folderRef} type="file" multiple hidden aria-label="Folder" onChange={(e) => { add([...e.target.files ?? []].map((f) => ({ path: f.webkitRelativePath || f.name, file: f }))); e.target.value = ""; }} />
              </div>
              {queue.length > 0 && (
                <section className="import-queue" aria-label="Files to import">
                  <div className="queue-head">
                    <b>{queue.length === 1 ? "1 file" : `${queue.length} files`}</b><span className="muted">{size(queue.reduce((n, f) => n + f.file.size, 0))}</span>
                    <label className="inline">Destination
                      <select className="sel" value={target} onChange={(e) => setTarget(e.target.value)}>
                        <option value="">Import inbox</option>
                        {spaces.map((s) => <option key={s.id} value={s.id}>{s.name}{s.isSensitive ? " (sensitive)" : ""}</option>)}
                      </select>
                    </label>
                    <span className="grow" />
                    <button type="button" className="tb" disabled={!!progress} onClick={() => setQueue([])}>Clear</button>
                    <button type="button" className="tb primary" disabled={!!progress} onClick={() => void start()}>{progress ?? "Start import"}</button>
                  </div>
                  <ul className="rows">
                    {queue.map((f) => (
                      <li key={f.path} className="row-line">
                        <span className="grow" title={f.path}>{f.path}</span><span className="muted small">{size(f.file.size)}</span>
                        <button type="button" className="tb icon" aria-label={`Remove ${f.path}`} title="Remove" disabled={!!progress}
                          onClick={() => setQueue((q) => q.filter((x) => x.path !== f.path))}><X className="ic" aria-hidden /></button>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </>
          )}
          {list?.batches.filter((b) => b.started && !b.grouped && !b.error).map((b) => (
            <p key={b.id} className="import-progress muted" role="status">Reading {b.files === 1 ? "1 file" : `${b.files} files`}…</p>
          ))}
          {list?.batches.filter((b) => b.error).map((b) => <p key={b.id} className="form-error" role="alert">{b.error}</p>)}
          <div className="bar-row">
            <h2 className="s">Drafts</h2>
            <span className="grow" />
            <button type="button" className="tb" disabled={picked.size < 2} onClick={() => void merge()}>Merge</button>
          </div>
          <ul className="rows import-rows" aria-label="Drafts">
            {items.map((i) => {
              const can = mergeable(i.status);
              const why = statusText(i, now);
              return (
                <li key={i.id} className={`row-line ${i.status === "Failed" || i.status === "Blocked" ? "bad" : ""}`}>
                  <input type="checkbox" className="chk" aria-label={`Select ${i.title}`} disabled={!can} title={can ? "Select to merge" : `Can't be merged: ${why}`}
                    checked={picked.has(i.id)} onChange={(e) => { const n = new Set(picked); if (e.target.checked) n.add(i.id); else n.delete(i.id); setPicked(n); }} />
                  <Link href={to.imports(i.id)} className="row-title" title={i.title} onNav={() => onOpen(i.id)}>{i.title}</Link>
                  <span className="row-meta muted" title={[i.source, why, i.suggestedSpace && `Suggested: ${i.suggestedSpace}`].filter(Boolean).join(" · ")}>
                    {i.source && <span>{i.source}</span>}
                    <span className={`st st-${i.status.toLowerCase()}`}>{why}</span>
                    {i.suggestedSpace && <span>→ {i.suggestedSpace}</span>}
                  </span>
                  <span className="muted small nowrap">{fmt(i.createdAt)}</span>
                  <button type="button" className="tb" disabled={i.status === "Working"} onClick={() => void discard(i)}>Discard</button>
                </li>
              );
            })}
            {list && items.length === 0 && <li className="row-line muted">No drafts.</li>}
          </ul>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Drafts</h3><div>{items.length}</div>
        {items.some((i) => i.status === "Queued" || i.status === "Working") && (
          <><h3>In progress</h3><div>{items.filter((i) => i.status === "Working").length} drafting · {items.filter((i) => i.status === "Queued").length} waiting</div></>
        )}
      </aside>
    </>
  );
}

type Draft = { key: number; title: string; json: string };

const TOP = "Top level";
/** The space's folders as paths ("Runbooks / DR"), in tree order: what the Folder box offers. */
export function folderPaths(nodes: TreeNode[]): { id: string; path: string }[] {
  const out: { id: string; path: string }[] = [];
  const walkFolders = (parent: string | null, prefix: string) => {
    for (const n of nodes.filter((x) => x.kind === "Folder" && x.parentId === parent).sort((a, b) => a.title.localeCompare(b.title, undefined, { numeric: true }))) {
      const path = prefix ? `${prefix} / ${n.title}` : n.title;
      out.push({ id: n.id, path });
      walkFolders(n.id, path);
    }
  };
  walkFolders(null, "");
  return out;
}

/** What is typed in the Folder box: an existing folder, the top level, or a new folder (under the folder its path starts with). */
export function resolveFolder(text: string, paths: { id: string; path: string }[]): { parentId: string | null; newFolder: string | null } {
  const t = text.trim();
  if (!t || t === TOP) return { parentId: null, newFolder: null };
  const hit = paths.find((p) => p.path === t);
  if (hit) return { parentId: hit.id, newFolder: null };
  const cut = t.lastIndexOf(" / ");
  const under = cut > 0 ? paths.find((p) => p.path === t.slice(0, cut)) : undefined;
  return under ? { parentId: under.id, newFolder: t.slice(cut + 3).trim() } : { parentId: null, newFolder: t };
}

// Drafts made before the wording changed carry the old last sentence.
const warning = (w: string) => w.replace("Check it says nothing new.", "Confirm the document is correct before saving.");

/** One proposed page, editable as in the page editor. */
function DraftEditor({ draft, onChange }: { draft: Draft; onChange: (json: string) => void }) {
  // A plugin's script may add to the editor after this draft was drawn (its block shows as unknown until then). The editor is made
  // again from the draft as it stands, but never under someone's hands: while it has the focus, that waits until it loses it.
  const added = useSyncExternalStore(editorExtensions.subscribe, editorExtensions.version);
  const [made, setMade] = useState(added);
  const extensions = useMemo(() => makeExtensions("plaintext"), [made]);
  const editor = useEditor({
    extensions, content: toEditor(draft.json, extensions), editable: true,
    editorProps: { attributes: { class: "prose", "aria-label": `Content of ${draft.title}` } },
    onUpdate: ({ editor: e }) => onChange(fromEditor(e)),
    onBlur: () => setMade(editorExtensions.version()),
  }, [extensions]);
  useEffect(() => { if (added !== made && !editor?.isFocused) setMade(added); }, [added, made, editor]);
  if (!editor) return null;
  return <div className="draft-editor"><Toolbar editor={editor} onImages={() => {}} /><EditorContent editor={editor} className="editing" /></div>;
}

/** Splits a page at its top-level headings: each heading starts a page named by it. */
export function splitAtHeadings(d: Draft, next: () => number): Draft[] {
  const doc = JSON.parse(d.json) as { content?: { type: string; attrs?: { level?: number }; content?: { text?: string }[] }[] };
  const out: Draft[] = [];
  let cur: typeof doc.content = [];
  let title = d.title;
  for (const n of doc.content ?? []) {
    if (n.type === "heading" && (n.attrs?.level ?? 1) <= 2 && cur.length > 0) {
      out.push({ key: next(), title, json: JSON.stringify({ type: "doc", content: cur }) });
      cur = [];
      title = (n.content ?? []).map((c) => c.text ?? "").join("") || title;
    } else if (n.type === "heading" && cur.length === 0) title = (n.content ?? []).map((c) => c.text ?? "").join("") || title;
    cur.push(n);
  }
  out.push({ key: next(), title, json: JSON.stringify({ type: "doc", content: cur }) });
  return out;
}

export function mergeDrafts(drafts: Draft[], key: number): Draft {
  const content = drafts.flatMap((d) => (JSON.parse(d.json) as { content?: unknown[] }).content ?? []);
  return { key, title: drafts[0].title, json: JSON.stringify({ type: "doc", content }) };
}

const KIND: Record<string, string> = {
  doc: "Word", docx: "Word", odt: "OpenDocument", rtf: "RTF", pdf: "PDF", txt: "text", md: "Markdown", log: "log", html: "web page", htm: "web page", mht: "web archive",
  eml: "mail", msg: "Outlook mail", xls: "Excel", xlsx: "Excel", xlsb: "Excel", ods: "spreadsheet", csv: "CSV", tsv: "TSV", ppt: "PowerPoint", pptx: "PowerPoint",
  json: "JSON", xml: "XML", yaml: "YAML", yml: "YAML", ini: "INI", one: "OneNote", zip: "zip", png: "picture", jpg: "picture", jpeg: "picture", gif: "picture", webp: "picture",
};
const kindOf = (f: ImportFile) => (f.kind === "Image" ? "picture" : KIND[f.name.split(".").pop()?.toLowerCase() ?? ""] ?? (f.name.includes(".") ? f.name.split(".").pop()!.toUpperCase() : "file"));

/** Facts about what was read, from the files and the draft: no prose of the model's, except the conflicts it noted, said as such. */
function Summary({ item }: { item: ImportItem }) {
  const files = item.files.filter((f) => !f.embedded);
  const pictures = item.files.filter((f) => f.embedded && f.kind === "Image").length;
  const kinds = Object.entries(files.reduce<Record<string, number>>((a, f) => ({ ...a, [kindOf(f)]: (a[kindOf(f)] ?? 0) + 1 }), {}));
  const found = item.proposal?.found;
  const sensitive = item.proposal?.sensitive ?? [];
  const facts: [string, string[]][] = found ? [["People", found.people], ["Servers", found.servers], ["Applications", found.applications], ["Tickets and cases", found.tickets], ["Versions", found.versions]] : [];
  return (
    <section className="import-summary" aria-label="Summary">
      <h2 className="s">Summary</h2>
      <p>{files.length === 1 ? "1 file" : `${files.length} files`}: {kinds.map(([k, n]) => `${n} ${k}`).join(", ")}{pictures > 0 ? `; ${pictures} picture${pictures === 1 ? "" : "s"} inside` : ""}.</p>
      <ul className="plain-list small">
        {files.map((f) => (
          <li key={f.id}><span className="clip" title={f.path}>{f.path}</span> <span className="muted">· {kindOf(f)}, {size(f.size)}{item.sources[f.id] ? `, ${item.sources[f.id].length.toLocaleString()} characters read` : ""}{f.note ? ` · ${f.note}` : ""}</span></li>
        ))}
      </ul>
      {facts.some(([, v]) => v.length > 0) && (
        <dl className="kv small">
          {facts.filter(([, v]) => v.length > 0).map(([k, v]) => <div key={k} className="contents"><dt>{k}</dt><dd>{v.join(", ")}</dd></div>)}
        </dl>
      )}
      {found && found.conflicts.length > 0 && (
        <div className="note-row" role="note"><span><b>Conflicts the model noted:</b> {found.conflicts.join(" ")}</span></div>
      )}
      {sensitive.length > 0 && <p className="small"><b>Sensitive data flagged:</b> {sensitive.map((s) => `${s.kind} (${s.sample})`).join(", ")}</p>}
    </section>
  );
}

function ImportReview({ id, onBack, onOpenPage, onHome }: { id: string; onBack: () => void; onOpenPage: (id: string) => void; onHome: () => void }) {
  const dialogs = useDialogs();
  const [item, setItem] = useState<ImportItem | null>(null);
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [redact, setRedact] = useState(true);
  const [shown, setShown] = useState<ImportFile | null>(null);
  const [sourceOpen, setSourceOpen] = useState(false);
  const [tags, setTags] = useState<string[]>([]);
  const [useApplies, setUseApplies] = useState(true);
  const [useOwner, setUseOwner] = useState(true);
  const [review, setReview] = useState<number | null>(null);
  const [reviewTouched, setReviewTouched] = useState(false);
  const [mode, setMode] = useState<string>("new");
  const [space, setSpace] = useState("");
  const [folder, setFolder] = useState(TOP);
  const [folders, setFolders] = useState<{ id: string; path: string }[]>([]);
  const [wantFolder, setWantFolder] = useState<string | null>(null);
  const [attach, setAttach] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const keyRef = useRef(0);
  const next = () => ++keyRef.current;

  const load = useCallback(() => api.get<ImportItem>(`/imports/items/${id}`).then((it) => {
    setItem(it);
    const p = it.proposal;
    setShown((s) => s ?? it.files.find((f) => f.kind === "Text" && !f.embedded) ?? it.files[0] ?? null);
    if (!p) { setDrafts([]); return; }
    setDrafts(p.pages.map((pg) => ({ key: next(), title: pg.title, json: pg.redactedJson })));
    setTags(p.tags);
    const sp = p.destination?.spaceId ?? "";
    setSpace(sp);
    setWantFolder(p.destination?.parentId ?? null);
    setReview(it.spaces?.find((s) => s.id === sp)?.reviewMonths ?? p.reviewMonths);
  }).catch(() => setError("This draft was not found.")), [id]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (item && ["Queued", "Working"].includes(item.status)) { const t = setTimeout(() => void load(), 2000); return () => clearTimeout(t); } }, [item, load]);
  useEffect(() => {
    if (!space) { setFolders([]); return; }
    void api.get<TreeNode[]>(`/spaces/${space}/tree`).then((t) => {
      const paths = folderPaths(t);
      setFolders(paths);
      if (wantFolder) { setFolder(paths.find((x) => x.id === wantFolder)?.path ?? TOP); setWantFolder(null); }
    }).catch(() => setFolders([]));
  }, [space, wantFolder]);

  const spaces: ImportSpace[] = item?.spaces ?? [];
  function pickSpace(sid: string, parent: string | null = null) {
    setSpace(sid);
    setFolder(TOP);
    setWantFolder(parent);
    // A space's own review cycle is where a page placed there starts, unless it was chosen here already.
    const d = spaces.find((s) => s.id === sid)?.reviewMonths;
    if (!reviewTouched && d != null) setReview(d);
  }

  function toggleRedact(on: boolean) {
    if (!item?.proposal) return;
    setRedact(on);
    setDrafts(item.proposal.pages.map((pg) => ({ key: next(), title: pg.title, json: on ? pg.redactedJson : pg.contentJson })));
  }

  async function accept() {
    if (!item?.proposal) return;
    setBusy(true);
    setError(null);
    const p = item.proposal;
    const update = mode.startsWith("update:") ? mode.slice(7) : null;
    try {
      const ids = await api.post<string[]>(`/imports/items/${id}/accept`, {
        pages: drafts.map((d) => ({ title: d.title, contentJson: d.json, tags })),
        spaceId: update ? null : space || null, updatePageId: update, ...(update ? { parentId: null } : resolveFolder(folder, folders)),
        details: {
          appliesTo: useApplies ? p.appliesTo.map((a) => ({ applicationId: a.applicationId, servers: a.servers })) : [],
          ownerType: useOwner && p.owner ? p.owner.type : null, ownerId: useOwner && p.owner ? p.owner.id : null, reviewMonths: review,
        },
        attachOriginals: attach,
      });
      onOpenPage(ids[0]);
    } catch (e) { setError(problem(e, "Not accepted.")); }
    finally { setBusy(false); }
  }

  async function act(path: string, fallback: string) {
    setError(null);
    try { await api.post(`/imports/items/${id}/${path}`); await load(); } catch (e) { setError(problem(e, fallback)); }
  }

  async function discard() {
    if (!item || !await dialogs.confirm({ title: `Discard “${item.title}”?`, body: "The draft and its original files are deleted permanently. This can't be undone.", ok: "Discard", danger: true, focusCancel: true })) return;
    try { await api.del(`/imports/items/${id}`); onBack(); } catch (e) { setError(problem(e, "Not discarded.")); }
  }

  if (!item) return <main className="doc wide"><div className="col"><Crumbs onHome={onHome} onInbox={onBack} />{error && <p className="form-error" role="alert">{error}</p>}</div></main>;
  const p = item.proposal;
  const ready = item.status === "Ready";
  const source = shown && item.sources[shown.id];
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} item={item.title} onInbox={onBack} />
          <div className="titlebar">
            <h1>{item.title}</h1>
            <div className="ptools">
              {(ready || item.status === "Failed") && <button className="tb" onClick={() => void act("reject", "Not rejected.")}>Reject</button>}
              {item.status !== "Working" && item.status !== "Accepted" && <button className="tb" onClick={() => void discard()}>Discard</button>}
              {["Ready", "Failed", "Rejected"].includes(item.status) && <button className="tb" onClick={() => void act("retry", "Not started.")}>Draft again</button>}
              {ready && <button className="tb primary" disabled={busy || drafts.length === 0 || (mode === "new" && !space)} onClick={() => void accept()}>{busy ? "Accepting…" : "Accept"}</button>}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          {!ready && <p className={item.status === "Failed" || item.status === "Blocked" ? "form-error" : "muted"} role="status">{statusText(item)}</p>}
          {p?.warnings.map((w) => <p key={w} className="note-row" role="status"><span>{warning(w)}</span></p>)}

          <Summary item={item} />

          {p && ready && (
            <section className="review-options" aria-label="Placement and details">
              <h3>Placement</h3>
              <div className="place-grid">
                <span />
                <span className="col-head" id="place-space">Space</span>
                <span className="col-head" id="place-folder">Folder</span>
                <label className="check"><input type="radio" name="dest" checked={mode === "new"} onChange={() => setMode("new")} />New page</label>
                <select className="sel" aria-labelledby="place-space" value={space} disabled={mode !== "new"} onChange={(e) => pickSpace(e.target.value)}>
                  <option value="">{spaces.length ? "" : "No space you can add pages to"}</option>
                  {spaces.map((s) => <option key={s.id} value={s.id}>{s.name}{s.isSensitive ? " (sensitive)" : ""}</option>)}
                </select>
                <span className="folder-pick">
                  <input type="text" className="sel" aria-labelledby="place-folder" list="place-folders" value={folder} maxLength={200} disabled={mode !== "new" || !space}
                    placeholder="New folder name" onFocus={(e) => e.target.select()} onChange={(e) => setFolder(e.target.value)} />
                  <datalist id="place-folders"><option value="" /><option value={TOP} />{folders.map((f) => <option key={f.id} value={f.path} />)}</datalist>
                  {space && resolveFolder(folder, folders).newFolder && <span className="chip">New folder</span>}
                </span>
              </div>
              <div className="place-row">
                {p.suggestedDestination && p.suggestedDestination.spaceId !== space && spaces.some((s) => s.id === p.suggestedDestination!.spaceId) && (
                  <button type="button" className="link small" onClick={() => { setMode("new"); pickSpace(p.suggestedDestination!.spaceId, p.suggestedDestination!.parentId); }}>
                    Suggested: {spaces.find((s) => s.id === p.suggestedDestination!.spaceId)?.name}
                  </button>
                )}
              </div>
              {p.duplicates.map((d) => (
                <div key={d.pageId} className="place-row">
                  <label className="check"><input type="radio" name="dest" checked={mode === `update:${d.pageId}`} disabled={drafts.length !== 1} onChange={() => setMode(`update:${d.pageId}`)} />Update existing page</label>
                  <a href={to.page(d.pageId)} className="link clip" title={d.title} target="_blank" rel="noopener">{d.title}</a>
                </div>
              ))}
              <h3>Tags</h3>
              <TagInput value={tags} onChange={setTags} />
              <h3>Review cycle</h3>
              <select className="sel" aria-label="Review cycle" value={review ?? ""} onChange={(e) => { setReviewTouched(true); setReview(e.target.value ? Number(e.target.value) : null); }}>
                <option value="">None</option>{REVIEW_CHOICES.map((m) => <option key={m} value={m}>{m === 1 ? "1 month" : `${m} months`}</option>)}
              </select>
              {p.appliesTo.length > 0 && (
                <>
                  <h3>Applies to</h3>
                  <label className="check"><input type="checkbox" className="chk" checked={useApplies} onChange={(e) => setUseApplies(e.target.checked)} />
                    {p.appliesTo.map((a) => `${a.name}${a.servers.length ? ` (${a.servers.join(", ")})` : ""}`).join("; ")}</label>
                </>
              )}
              {p.owner && (
                <>
                  <h3>Owner</h3>
                  <label className="check"><input type="checkbox" className="chk" checked={useOwner} onChange={(e) => setUseOwner(e.target.checked)} />{p.owner.name}</label>
                </>
              )}
              {p.sensitive.length > 0 && (
                <>
                  <h3>Sensitive data</h3>
                  <label className="check"><input type="checkbox" className="chk" checked={redact} onChange={(e) => toggleRedact(e.target.checked)} />Redact the {p.sensitive.length === 1 ? "value" : `${p.sensitive.length} values`} flagged</label>
                </>
              )}
              <h3>Originals</h3>
              <label className="check"><input type="checkbox" className="chk" checked={attach} onChange={(e) => setAttach(e.target.checked)} />Attach to the page</label>
            </section>
          )}

          <section className="review-source" aria-label="Source">
            <div className="bar-row">
              <h2 className="s">Source</h2>
              <span className="grow" />
              <button type="button" className="tb" aria-expanded={sourceOpen} onClick={() => setSourceOpen(!sourceOpen)}>
                {sourceOpen ? <ChevronDown className="ic" aria-hidden /> : <ChevronRight className="ic" aria-hidden />}{sourceOpen ? "Collapse" : "Expand"}
              </button>
            </div>
            {item.files.filter((f) => !f.embedded).length > 1 && (
              <div className="file-tabs" role="tablist" aria-label="Files">
                {item.files.filter((f) => !f.embedded).map((f) => (
                  <button key={f.id} type="button" role="tab" aria-selected={shown?.id === f.id} className={`chip ${shown?.id === f.id ? "on" : ""}`} title={f.path} onClick={() => setShown(f)}>{f.name}</button>
                ))}
              </div>
            )}
            {shown && (shown.kind === "Image"
              ? <img className={`review-image ${sourceOpen ? "open" : ""}`} src={fileUrl(shown.id)} alt={shown.name} />
              : source ? <pre className={`review-text ${sourceOpen ? "open" : ""}`} tabIndex={0}>{source}</pre>
              : <p className="muted">{shown.note ?? "No text was read from this file."}</p>)}
          </section>

          <section className="review-draft" aria-label="Proposed pages">
            <div className="bar-row">
              <h2 className="s">Proposed {drafts.length === 1 ? "page" : `pages (${drafts.length})`}</h2>
              <span className="grow" />
              {drafts.length > 1 && <button type="button" className="tb" onClick={() => setDrafts([mergeDrafts(drafts, next())])}>Merge pages</button>}
              {drafts.length === 1 && <button type="button" className="tb" onClick={() => setDrafts(splitAtHeadings(drafts[0], next))}>Split at headings</button>}
            </div>
            {drafts.map((d, n) => (
              <div key={d.key} className="draft">
                <input type="text" className="title-input" aria-label={`Title of page ${n + 1}`} value={d.title} maxLength={200}
                  onChange={(e) => setDrafts(drafts.map((x) => (x.key === d.key ? { ...x, title: e.target.value } : x)))} />
                <DraftEditor draft={d} onChange={(json) => setDrafts((all) => all.map((x) => (x.key === d.key ? { ...x, json } : x)))} />
              </div>
            ))}
            {drafts.length === 0 && <p className="muted">No proposed page.</p>}
          </section>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Files</h3><div>{item.files.filter((f) => !f.embedded).length}</div>
        {p && <><h3>Drafted by</h3><div className="clip" title={p.model}>{p.model}</div></>}
        {p?.ms ? <><h3>Drafting took</h3><div>{seconds(p.ms)}</div></> : null}
      </aside>
    </>
  );
}
