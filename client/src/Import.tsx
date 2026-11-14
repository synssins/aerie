import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import { EditorContent, useEditor } from "@tiptap/react";
import { ChevronRight, FileUp, FolderUp, House } from "lucide-react";
import { api, ApiError, type ImportFile, type ImportItem, type ImportList, type Space, type TreeNode } from "./api";
import { useDialogs } from "./Dialogs";
import { fromEditor, makeExtensions, toEditor } from "./editor/extensions";
import { Toolbar } from "./PageView";
import { REVIEW_CHOICES } from "./PageDetails";
import { TagInput } from "./TagInput";

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
const problem = (e: unknown, fallback: string) => (e instanceof ApiError && (e.body?.detail || (e.body?.errors && Object.values(e.body.errors).flat()[0]))) || fallback;
const WRITERS = ["Contributor", "Supervisor", "Owner"];
const STATUS: Record<string, string> = { Queued: "Waiting", Working: "Drafting", Ready: "Ready to review", Failed: "Failed", Blocked: "Blocked", Accepted: "Accepted" };
const fileUrl = (id: string) => `/api/v1/imports/files/${id.replace(/-/g, "")}`;

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
export function ImportView({ itemId, spaces, onOpenItem, onOpenPage, onHome }: {
  itemId: string | null; spaces: Space[]; onOpenItem: (id: string | null) => void; onOpenPage: (id: string) => void; onHome: () => void;
}) {
  return itemId
    ? <ImportReview key={itemId} id={itemId} spaces={spaces} onBack={() => onOpenItem(null)} onOpenPage={onOpenPage} onHome={onHome} />
    : <ImportInbox spaces={spaces} onOpen={(id) => onOpenItem(id)} onHome={onHome} />;
}

function Crumbs({ onHome, item, onInbox }: { onHome: () => void; item?: string; onInbox: () => void }) {
  return (
    <nav className="crumb" aria-label="Breadcrumb">
      <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden />
      {item ? <><button className="crumb-link" onClick={onInbox}>Import inbox</button><ChevronRight className="ic sm" aria-hidden /><b>{item}</b></> : <b>Import inbox</b>}
    </nav>
  );
}

function ImportInbox({ spaces, onOpen, onHome }: { spaces: Space[]; onOpen: (id: string) => void; onHome: () => void }) {
  const dialogs = useDialogs();
  const [list, setList] = useState<ImportList | null>(null);
  const [target, setTarget] = useState("");
  const [progress, setProgress] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [over, setOver] = useState(false);
  const filesRef = useRef<HTMLInputElement>(null);
  const folderRef = useRef<HTMLInputElement>(null);
  const load = useCallback(() => api.get<ImportList>("/imports").then(setList).catch(() => setError("Not loaded.")), []);
  useEffect(() => { void load(); }, [load]);
  const busy = !!list?.batches.some((b) => b.started && (!b.grouped || b.queued > 0 || b.working > 0));
  useEffect(() => { if (!busy) return; const t = setInterval(() => void load(), 3000); return () => clearInterval(t); }, [busy, load]);
  useEffect(() => { folderRef.current?.setAttribute("webkitdirectory", ""); }, []);

  async function upload(files: Picked[]) {
    if (files.length === 0) return;
    setError(null);
    try {
      const batch = await api.post<{ id: string }>("/imports", { spaceId: target || null });
      for (let i = 0; i < files.length; i++) {
        setProgress(`Uploading ${i + 1} of ${files.length}`);
        const res = await fetch(`/api/v1/imports/${batch.id}/files`, {
          method: "POST", credentials: "same-origin", body: files[i].file,
          headers: { "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(files[i].path) },
        });
        if (!res.ok) { const b = await res.json().catch(() => null); setError(`${files[i].path}: ${b?.detail ?? (b?.errors && Object.values(b.errors).flat()[0]) ?? "not uploaded"}`); }
      }
      await api.post(`/imports/${batch.id}/start`);
      await load();
    } catch (e) { setError(problem(e, "Not uploaded.")); }
    finally { setProgress(null); }
  }

  async function drop(e: DragEvent) {
    e.preventDefault();
    setOver(false);
    const entries = [...e.dataTransfer.items].map((i) => i.webkitGetAsEntry()).filter((x): x is FileSystemEntry => !!x);
    await upload(entries.length ? (await Promise.all(entries.map((x) => walk(x)))).flat() : [...e.dataTransfer.files].map((f) => ({ path: f.name, file: f })));
  }

  async function merge() {
    setError(null);
    try { await api.post("/imports/merge", { itemIds: [...picked] }); setPicked(new Set()); await load(); }
    catch (e) { setError(problem(e, "Not merged.")); }
  }

  async function discard(id: string) {
    if (!await dialogs.confirm({ title: "Discard this draft?", ok: "Discard", danger: true })) return;
    try { await api.del(`/imports/items/${id}`); await load(); } catch (e) { setError(problem(e, "Not discarded.")); }
  }

  const writable = spaces.filter((s) => s.myRole && WRITERS.includes(s.myRole));
  const items = list?.items.filter((i) => i.status !== "Accepted") ?? [];
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} onInbox={() => {}} />
          <div className="titlebar"><h1>Import inbox</h1></div>
          {error && <p className="form-error" role="alert">{error}</p>}
          {list && !list.canImport ? <p className="muted">Import is not open to you.</p> : (
            <div className={`drop-zone ${over ? "over" : ""}`} onDragOver={(e) => { e.preventDefault(); setOver(true); }} onDragLeave={() => setOver(false)} onDrop={(e) => void drop(e)}>
              <label className="inline">Destination
                <select className="sel" value={target} onChange={(e) => setTarget(e.target.value)}>
                  <option value="">Import inbox</option>
                  {writable.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                </select>
              </label>
              <button type="button" className="tb primary" disabled={!!progress} onClick={() => filesRef.current?.click()}><FileUp className="ic" aria-hidden />Files</button>
              <button type="button" className="tb" disabled={!!progress} onClick={() => folderRef.current?.click()}><FolderUp className="ic" aria-hidden />Folder</button>
              <input ref={filesRef} type="file" multiple hidden aria-label="Files" onChange={(e) => { void upload([...e.target.files ?? []].map((f) => ({ path: f.name, file: f }))); e.target.value = ""; }} />
              <input ref={folderRef} type="file" multiple hidden aria-label="Folder" onChange={(e) => { void upload([...e.target.files ?? []].map((f) => ({ path: f.webkitRelativePath || f.name, file: f }))); e.target.value = ""; }} />
              {progress && <span className="muted" role="status">{progress}</span>}
            </div>
          )}
          {list?.batches.filter((b) => b.started && (!b.grouped || b.queued + b.working > 0)).map((b) => {
            const total = b.queued + b.working + b.ready + b.failed + b.blocked + b.done;
            const done = total - b.queued - b.working;
            return (
              <div key={b.id} className="import-progress" role="status">
                <span>{b.grouped ? `Drafting ${done} of ${total}` : `Reading ${b.files} files`}</span>
                <progress max={total || 1} value={b.grouped ? done : 0} aria-label="Import progress" />
              </div>
            );
          })}
          <div className="bar-row">
            <button type="button" className="tb" disabled={picked.size < 2} onClick={() => void merge()}>Merge</button>
          </div>
          <table className="grid-table">
            <thead><tr><th /><th>Draft</th><th>Files</th><th>Status</th><th>Added</th><th /></tr></thead>
            <tbody>
              {items.map((i) => (
                <tr key={i.id}>
                  <td><input type="checkbox" className="chk" aria-label={`Select ${i.title}`} disabled={!["Ready", "Failed", "Queued"].includes(i.status)} checked={picked.has(i.id)}
                    onChange={(e) => { const n = new Set(picked); if (e.target.checked) n.add(i.id); else n.delete(i.id); setPicked(n); }} /></td>
                  <td><button type="button" className="link" onClick={() => onOpen(i.id)}>{i.title}</button></td>
                  <td className="muted">{i.files}</td>
                  <td>{STATUS[i.status] ?? i.status}{i.error && <span className="muted small"> · {i.error}</span>}</td>
                  <td className="muted">{fmt(i.createdAt)}</td>
                  <td><button type="button" className="tb" onClick={() => void discard(i.id)}>Discard</button></td>
                </tr>
              ))}
              {items.length === 0 && <tr><td colSpan={6} className="muted">No drafts.</td></tr>}
            </tbody>
          </table>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Drafts</h3><div>{items.length}</div>
      </aside>
    </>
  );
}

type Draft = { key: number; title: string; json: string };

/** One proposed page, editable as in the page editor. */
function DraftEditor({ draft, onChange }: { draft: Draft; onChange: (json: string) => void }) {
  const extensions = useMemo(() => makeExtensions("plaintext"), []);
  const editor = useEditor({
    extensions, content: toEditor(draft.json, extensions), editable: true,
    editorProps: { attributes: { class: "prose", "aria-label": `Content of ${draft.title}` } },
    onUpdate: ({ editor: e }) => onChange(fromEditor(e)),
  });
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

function ImportReview({ id, spaces, onBack, onOpenPage, onHome }: { id: string; spaces: Space[]; onBack: () => void; onOpenPage: (id: string) => void; onHome: () => void }) {
  const [item, setItem] = useState<ImportItem | null>(null);
  const [drafts, setDrafts] = useState<Draft[]>([]);
  const [redact, setRedact] = useState(true);
  const [shown, setShown] = useState<ImportFile | null>(null);
  const [tags, setTags] = useState<string[]>([]);
  const [useApplies, setUseApplies] = useState(true);
  const [useOwner, setUseOwner] = useState(true);
  const [review, setReview] = useState<number | null>(null);
  const [mode, setMode] = useState<string>("new");
  const [space, setSpace] = useState("");
  const [folder, setFolder] = useState("");
  const [folders, setFolders] = useState<TreeNode[]>([]);
  const [attach, setAttach] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const keyRef = useRef(0);
  const next = () => ++keyRef.current;

  const load = useCallback(() => api.get<ImportItem>(`/imports/items/${id}`).then((it) => {
    setItem(it);
    const p = it.proposal;
    if (!p) return;
    setDrafts(p.pages.map((pg) => ({ key: next(), title: pg.title, json: pg.redactedJson })));
    setTags(p.tags);
    setReview(p.reviewMonths);
    setSpace(p.destination?.spaceId ?? "");
    setFolder(p.destination?.parentId ?? "");
    setShown(it.files.find((f) => f.kind === "Text") ?? it.files[0] ?? null);
  }).catch(() => setError("Not found.")), [id]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (item && ["Queued", "Working"].includes(item.status)) { const t = setTimeout(() => void load(), 3000); return () => clearTimeout(t); } }, [item, load]);
  useEffect(() => { if (!space) { setFolders([]); return; } void api.get<TreeNode[]>(`/spaces/${space}/tree`).then((t) => setFolders(t.filter((n) => n.kind === "Folder"))).catch(() => setFolders([])); }, [space]);

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
        spaceId: update ? null : space || null, parentId: update ? null : folder || null, updatePageId: update,
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

  async function retry() {
    try { await api.post(`/imports/items/${id}/retry`); await load(); } catch (e) { setError(problem(e, "Not started.")); }
  }

  const writable = spaces.filter((s) => s.myRole && WRITERS.includes(s.myRole));
  if (!item) return <main className="doc wide"><div className="col"><Crumbs onHome={onHome} onInbox={onBack} />{error && <p className="form-error" role="alert">{error}</p>}</div></main>;
  const p = item.proposal;
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <Crumbs onHome={onHome} item={item.title} onInbox={onBack} />
          <div className="titlebar">
            <h1>{item.title}</h1>
            <div className="ptools">
              {["Ready", "Failed"].includes(item.status) && <button className="tb" onClick={() => void retry()}>Draft again</button>}
              {item.status === "Ready" && <button className="tb primary" disabled={busy || drafts.length === 0 || (mode === "new" && !space)} onClick={() => void accept()}>{busy ? "Accepting…" : "Accept"}</button>}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          {item.status !== "Ready" && <p className={item.status === "Failed" || item.status === "Blocked" ? "form-error" : "muted"} role="status">{STATUS[item.status] ?? item.status}{item.error ? ` · ${item.error}` : ""}</p>}
          {p?.warnings.map((w) => <p key={w} className="note-row" role="status">{w}</p>)}
          {p && item.status === "Ready" && (
            <section className="review-options" aria-label="Review">

            {p.sensitive.length > 0 && (
              <>
                <h3>Sensitive data</h3>
                <ul className="plain-list small">{p.sensitive.map((s, i) => <li key={i}>{s.kind}: <code>{s.sample}</code></li>)}</ul>
                <label className="check"><input type="checkbox" className="chk" checked={redact} onChange={(e) => toggleRedact(e.target.checked)} />Redact</label>
              </>
            )}
            <h3>Where</h3>
            <label className="check"><input type="radio" name="dest" checked={mode === "new"} onChange={() => setMode("new")} />New page</label>
            {mode === "new" && (
              <div className="field">
                <select className="sel" aria-label="Space" value={space} onChange={(e) => { setSpace(e.target.value); setFolder(""); }}>
                  <option value="" />{writable.map((s) => <option key={s.id} value={s.id}>{s.name}{s.isSensitive ? " (sensitive)" : ""}</option>)}
                </select>
                <select className="sel" aria-label="Folder" value={folder} onChange={(e) => setFolder(e.target.value)}>
                  <option value="">Top level</option>{folders.map((f) => <option key={f.id} value={f.id}>{f.title}</option>)}
                </select>
                {p.suggestedDestination && p.suggestedDestination.spaceId !== space && (
                  <button type="button" className="link small" onClick={() => { setSpace(p.suggestedDestination!.spaceId); setFolder(p.suggestedDestination!.parentId ?? ""); }}>
                    Suggested: {spaces.find((s) => s.id === p.suggestedDestination!.spaceId)?.name}
                  </button>
                )}
              </div>
            )}
            {p.duplicates.map((d) => (
              <label key={d.pageId} className="check"><input type="radio" name="dest" checked={mode === `update:${d.pageId}`} disabled={drafts.length !== 1} onChange={() => setMode(`update:${d.pageId}`)} />Update “{d.title}”</label>
            ))}
            <h3>Tags</h3>
            <TagInput value={tags} onChange={setTags} />
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
            <h3>Review cycle</h3>
            <select className="sel" aria-label="Review cycle" value={review ?? ""} onChange={(e) => setReview(e.target.value ? Number(e.target.value) : null)}>
              <option value="">None</option>{REVIEW_CHOICES.map((m) => <option key={m} value={m}>{m} months</option>)}
            </select>
            <h3>Originals</h3>
            <label className="check"><input type="checkbox" className="chk" checked={attach} onChange={(e) => setAttach(e.target.checked)} />Attach to the page</label>
            </section>
          )}
          <div className="review-split">
            <section className="review-source" aria-label="Source">
              <h2 className="s">Source</h2>
              <ul className="plain-list">
                {item.files.filter((f) => !f.embedded).map((f) => (
                  <li key={f.id}>
                    <button type="button" className={`link ${shown?.id === f.id ? "cur" : ""}`} onClick={() => setShown(f)}>{f.path}</button>
                    <span className="muted small"> · {f.kind === "Blocked" ? "blocked" : f.kind === "Unsupported" ? "not read" : f.kind.toLowerCase()}{f.note ? ` · ${f.note}` : ""}</span>
                  </li>
                ))}
              </ul>
              {shown && (shown.kind === "Image"
                ? <img className="review-image" src={fileUrl(shown.id)} alt={shown.name} />
                : item.sources[shown.id] && <pre className="review-text">{item.sources[shown.id]}</pre>)}
            </section>
            <section className="review-draft" aria-label="Proposed pages">
              <h2 className="s">Proposed {drafts.length === 1 ? "page" : `pages (${drafts.length})`}</h2>
              {p && (
                <div className="bar-row">
                  {drafts.length > 1 && <button type="button" className="tb" onClick={() => setDrafts([mergeDrafts(drafts, next())])}>Merge pages</button>}
                  {drafts.length === 1 && <button type="button" className="tb" onClick={() => setDrafts(splitAtHeadings(drafts[0], next))}>Split at headings</button>}
                </div>
              )}
              {drafts.map((d, n) => (
                <div key={d.key} className="draft">
                  <input type="text" className="title-input" aria-label={`Title of page ${n + 1}`} value={d.title} maxLength={200}
                    onChange={(e) => setDrafts(drafts.map((x) => (x.key === d.key ? { ...x, title: e.target.value } : x)))} />
                  <DraftEditor draft={d} onChange={(json) => setDrafts((all) => all.map((x) => (x.key === d.key ? { ...x, json } : x)))} />
                </div>
              ))}
            </section>
          </div>
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Files</h3><div>{item.files.filter((f) => !f.embedded).length}</div>
        {p && <><h3>Drafted by</h3><div>{p.model}</div></>}
      </aside>
    </>
  );
}
