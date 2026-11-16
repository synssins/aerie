import { detailsBody, detailsChanged, EMPTY_DETAILS, PageDetailsPanel } from "./PageDetails";
import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { AssistDialog } from "./Assist";
import { Link, to } from "./Link";
import { EditorContent, useEditor, useEditorState, type Editor } from "@tiptap/react";
import type { EditorView } from "@tiptap/pm/view";
import {
  Bold, ChevronRight, House, Code, Download, Ellipsis, FolderInput, History, ImagePlus, Heading2, Heading3, Italic, Link as LinkIcon, List, ListOrdered, ListTodo, Minus,
  Paperclip, Pencil, Quote, Redo2, SquareCode, Strikethrough, Table, Trash2, Underline, Undo2, X,
} from "lucide-react";
import { api, ApiError, fmtSize, upload, type Attachment, type Draft, type DiffLine, type Page, type PageDetails } from "./api";
import { editorExtensions, fromEditor, makeExtensions, toEditor } from "./editor/extensions";
import { PluginBoundary, SlotSections, SlotTools } from "./kit/SlotViews";
import { reportUiError } from "./kit/errors";
import { useSlot, type ActionItem } from "./kit/slots";
import { useDialogs } from "./Dialogs";
import { TagInput } from "./TagInput";
import { MenuButton, type MenuItem } from "./ui";
import { PageFeedbackSwitches } from "./Feedback";
import { PageComments } from "./Comments";
import { PageRating } from "./Ratings";

export const LANGUAGES = ["plaintext", "powershell", "bash", "shell", "sql", "json", "yaml", "xml", "ini", "csharp", "javascript", "typescript", "python", "go", "java", "css", "markdown", "diff"];

const MAX_IMAGE_BYTES = 20 * 1024 * 1024;
const NEW_DRAFT_KEY = "kb.draft.new";
const EMPTY_DOC = '{"type":"doc","content":[]}';

type NewDraft = { spaceId: string; parentId: string | null; title: string; tags: string[]; contentJson: string };

function newDraftFor(page: Page): NewDraft | null {
  try {
    const d = JSON.parse(localStorage.getItem(NEW_DRAFT_KEY) ?? "null") as NewDraft | null;
    return d && d.spaceId === page.spaceId && d.parentId === page.parentId ? d : null;
  } catch { return null; }
}
function clearNewDraft() { try { localStorage.removeItem(NEW_DRAFT_KEY); } catch { /* storage blocked */ } }

/** Images pasted from another site are copied into the space; ones that can't be fetched are dropped. Returns how many were dropped. */
async function importRemoteImages(view: EditorView, spaceId: string): Promise<number> {
  const tried = new Set<string>();
  let failed = 0;
  for (;;) {
    let src: string | null = null;
    view.state.doc.descendants((n) => {
      const s = n.type.name === "image" ? String(n.attrs.src ?? "") : "";
      if (!src && /^https?:/i.test(s) && !tried.has(s)) src = s;
    });
    if (!src) return failed;
    tried.add(src);
    const copied = await api.post<{ url: string }>(`/spaces/${spaceId}/images/import`, { url: src }).catch(() => null);
    if (!copied) failed++;
    // The document may have changed while the copy ran: find the images again.
    const hits: number[] = [];
    view.state.doc.descendants((n, pos) => { if (n.type.name === "image" && n.attrs.src === src) hits.push(pos); });
    let tr = view.state.tr;
    for (const pos of hits.reverse()) {
      const node = tr.doc.nodeAt(pos);
      if (!node) continue;
      tr = copied ? tr.setNodeMarkup(pos, undefined, { ...node.attrs, src: copied.url }) : tr.delete(pos, pos + node.nodeSize);
    }
    view.dispatch(tr);
  }
}

/** Tables and code blocks in a page, for the Export menu. */
function exportables(json: string): { tables: number; code: string[] } {
  const out = { tables: 0, code: [] as string[] };
  type N = { type?: string; attrs?: { language?: string }; content?: N[] };
  const walk = (n: N) => {
    if (n.type === "table") out.tables++;
    if (n.type === "codeBlock") out.code.push(n.attrs?.language ?? "plaintext");
    n.content?.forEach(walk);
  };
  try { walk(JSON.parse(json || EMPTY_DOC) as N); } catch { /* not a document */ }
  return out;
}

const imagesIn = (list: FileList | null | undefined) => Array.from(list ?? []).filter((f) => f.type.startsWith("image/"));

/** Uploads each image to the space and inserts it at pos (or the cursor). Returns an error message, or null. */
async function insertImages(view: EditorView, spaceId: string, files: File[], pos?: number): Promise<string | null> {
  for (const f of files) {
    if (f.size > MAX_IMAGE_BYTES) return `${f.name}: over 20 MB.`;
    const r = await fetch(`/api/v1/spaces/${spaceId}/images`, {
      method: "POST", body: f, credentials: "same-origin",
      headers: { "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(f.name) },
    }).catch(() => null);
    if (!r?.ok) return r?.status === 400 ? `${f.name}: PNG, JPEG, GIF or WebP only.` : `${f.name}: not uploaded.`;
    const { url } = await r.json() as { url: string };
    const node = view.state.schema.nodes.image.create({ src: url, alt: f.name });
    const tr = pos === undefined ? view.state.tr.replaceSelectionWith(node) : view.state.tr.insert(Math.min(pos, view.state.doc.content.size), node);
    view.dispatch(tr);
    if (pos !== undefined) pos += node.nodeSize;
  }
  return null;
}

type Heading = { level: number; text: string };

function headingsOf(json: string): Heading[] {
  try {
    const doc = JSON.parse(json) as { content?: { type: string; attrs?: { level?: number }; content?: { text?: string }[] }[] };
    return (doc.content ?? [])
      .filter((n) => n.type === "heading" && (n.attrs?.level ?? 9) <= 3)
      .map((n) => ({ level: n.attrs?.level ?? 2, text: (n.content ?? []).map((c) => c.text ?? "").join("") }))
      .filter((h) => h.text);
  } catch { return []; }
}

const fmt = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });

export function PageView({ page, spaceName, role, onSaved, onOpen, onHome, onDirty, onCancelNew, onHistory, onMove, onDelete, autoEdit = false, codeLanguage = "plaintext", guest = false, onTag, onOpenApp, canFormat = false, canAssist = false }: {
  /** AI assist: material pasted in is built into the page or merged into it; shown only in the editor, applied only by accepting. */
  canAssist?: boolean;
  /** The AI Format button: a preview of the same words with better structure, applied only if accepted, saved only with Save. */
  canFormat?: boolean;
  /** An application the page applies to was clicked: open it in the inventory. */
  onOpenApp?: (id: string) => void;
  /** Someone not signed in: no history and no export, which need sign-in. */
  guest?: boolean;
  /** A tag was clicked: list every page with it. */
  onTag?: (tag: string) => void;
  page: Page;
  onHistory: () => void;
  onMove: () => void;
  onDelete: () => void;
  spaceName: string;
  onSaved: (p: Page) => void;
  onOpen: (id: string | null) => void;
  onDirty: (dirty: boolean) => void;
  autoEdit?: boolean;
  /** Called when a new, never-saved page is cancelled. */
  onCancelNew?: () => void;
  role: string | null;
  codeLanguage?: string;
  onHome: () => void;
}) {
  const isNew = !page.id;
  const stored = useMemo(() => (isNew ? newDraftFor(page) : null), [isNew, page]);
  const [editing, setEditing] = useState(autoEdit);
  const [title, setTitle] = useState(stored?.title ?? page.title);
  const [tags, setTags] = useState<string[]>(stored?.tags ?? page.tags);
  const [details, setDetails] = useState<PageDetails>(page.details ?? EMPTY_DETAILS);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [base, setBase] = useState(page.version);
  const [hasDraft, setHasDraft] = useState(!!page.draft?.mine);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [files, setFiles] = useState<Attachment[]>(page.attachments);
  const filePicker = useRef<HTMLInputElement>(null);
  const dialogs = useDialogs();
  const live = useRef({ editing, title, tags, base, lock: false });
  live.current = { ...live.current, editing, title, tags, base };

  // A plugin's script may add to the editor after this page was drawn. The editor is made again then, but never under someone's hands.
  const added = useSyncExternalStore(editorExtensions.subscribe, editorExtensions.version);
  const madeWith = useRef(added);
  if (!editing) madeWith.current = added;
  const made = madeWith.current;
  const extensions = useMemo(() => makeExtensions(codeLanguage), [codeLanguage, made]);

  const editor = useEditor({
    extensions,
    content: toEditor(stored?.contentJson || page.contentJson || EMPTY_DOC, extensions),
    editable: false,
    editorProps: {
      attributes: { class: "prose", "aria-label": "Page content" },
      handlePaste: (view, e) => {
        const files = imagesIn(e.clipboardData?.files);
        // Word and web pages put a picture of the copied text on the clipboard too; paste the text then.
        const html = e.clipboardData?.getData("text/html") ?? "";
        const hasText = !!html && (new DOMParser().parseFromString(html, "text/html").body.textContent ?? "").replace(/ /g, "").trim().length > 0;
        if (!files.length || hasText) {
          // Let the paste land, then copy in any pictures it linked from another site.
          if (html) window.setTimeout(() => void importRemoteImages(view, page.spaceId).then((n) => { if (n) setError(n === 1 ? "1 image could not be copied." : `${n} images could not be copied.`); }), 0);
          return false;
        }
        void insertImages(view, page.spaceId, files).then(setError);
        return true;
      },
      handleDrop: (view, e, _slice, moved) => {
        const files = imagesIn(e.dataTransfer?.files);
        if (moved || !files.length) return false;
        void insertImages(view, page.spaceId, files, view.posAtCoords({ left: e.clientX, top: e.clientY })?.pos).then(setError);
        return true;
      },
    },
  }, [page.id, page.version, extensions]);

  useEffect(() => { editor?.setEditable(editing); }, [editor, editing]);
  // Said after every drawing, not only when it changes: the shell switches its guard off when "Discard?" is answered yes, and when
  // that led back to this same page the editor is still open and must count as an open edit again.
  useEffect(() => { onDirty(editing); });
  useEffect(() => {
    if (!editing) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener("beforeunload", warn);
    return () => window.removeEventListener("beforeunload", warn);
  }, [editing]);

  const headings = useMemo(() => headingsOf(page.contentJson), [page.contentJson]);
  const exports = useMemo(() => exportables(page.contentJson), [page.contentJson]);

  // Autosave: a few seconds after the last change. New pages have no id yet, so their draft stays in this browser.
  const queue = useRef<() => void>(() => undefined);
  useEffect(() => {
    if (!editing || !editor) return;
    let timer: number | undefined;
    const run = async () => {
      const s = live.current;
      const contentJson = fromEditor(editor);
      if (isNew) {
        try { localStorage.setItem(NEW_DRAFT_KEY, JSON.stringify({ spaceId: page.spaceId, parentId: page.parentId, title: s.title, tags: s.tags, contentJson } satisfies NewDraft)); } catch { /* storage blocked */ }
        return;
      }
      if (s.lock) return;
      try {
        const r = await api.put<{ updatedAt: string }>(`/pages/${page.id}/draft`, { title: s.title, contentJson, baseVersion: s.base, tags: s.tags });
        setSavedAt(r.updatedAt);
      } catch (e) {
        if (e instanceof ApiError && e.status === 409) live.current.lock = true; // someone else holds the draft
      }
    };
    queue.current = () => { window.clearTimeout(timer); timer = window.setTimeout(() => void run(), 3000); };
    editor.on("update", queue.current);
    return () => { editor.off("update", queue.current); window.clearTimeout(timer); queue.current = () => undefined; };
  }, [editing, editor, isNew, page.id, page.spaceId, page.parentId]);
  useEffect(() => { if (editing) queue.current(); }, [editing, title, tags]);

  // Leaving mid-edit after choosing Discard: the draft goes too.
  useEffect(() => () => {
    if (!live.current.editing) return;
    if (page.id) void api.del(`/pages/${page.id}/draft`).catch(() => undefined); else clearNewDraft();
  }, [page.id]);

  function startEdit() { setTitle(page.title); setTags(page.tags); setDetails(page.details ?? EMPTY_DETAILS); setBase(page.version); setError(null); setEditing(true); editor?.commands.focus("end"); }

  async function resumeDraft() {
    const d = await api.get<Draft>(`/pages/${page.id}/draft`).catch(() => null);
    setHasDraft(false);
    if (!d) return;
    setTitle(d.title);
    setTags(d.tags);
    setBase(d.baseVersion);
    editor?.commands.setContent(toEditor(d.contentJson, extensions));
    setError(null);
    setEditing(true);
  }

  async function discardDraft() {
    await api.del(`/pages/${page.id}/draft`).catch(() => undefined);
    setHasDraft(false);
  }

  function cancel() {
    if (isNew) { clearNewDraft(); live.current.editing = false; onDirty(false); onCancelNew?.(); return; }
    void api.del(`/pages/${page.id}/draft`).catch(() => undefined);
    editor?.commands.setContent(toEditor(page.contentJson || EMPTY_DOC, extensions));
    setEditing(false);
    setSavedAt(null);
    setError(null);
  }

  async function attach(list: FileList | null) {
    setError(null);
    for (const f of Array.from(list ?? [])) {
      if (f.size > page.maxAttachmentBytes) { setError(`${f.name}: over ${fmtSize(page.maxAttachmentBytes)}.`); continue; }
      try { const a = await upload<Attachment>(`/pages/${page.id}/attachments`, f); setFiles((all) => [...all, a].sort((x, y) => x.name.localeCompare(y.name))); }
      catch (e) { setError(e instanceof ApiError && e.status === 413 ? `${f.name}: larger than the web server allows.` : `${f.name}: not attached.`); }
    }
  }

  async function detach(a: Attachment) {
    if (!await dialogs.confirm({ title: `Remove “${a.name}”?`, ok: "Remove", danger: true })) return;
    await api.del(a.url.replace("/api/v1", ""));
    setFiles((all) => all.filter((x) => x.id !== a.id));
  }

  const exportItems: MenuItem[] = [
    { label: "PDF", href: `/api/v1/pages/${page.id}/export?format=pdf` },
    { label: "Word", href: `/api/v1/pages/${page.id}/export?format=docx` },
    ...(exports.tables ? [{ group: "Tables" } as MenuItem] : []),
    ...Array.from({ length: exports.tables }, (_, i): MenuItem[] => [
      { label: `Table ${i + 1} · CSV`, href: `/api/v1/pages/${page.id}/export/table/${i + 1}?format=csv` },
      { label: `Table ${i + 1} · Excel`, href: `/api/v1/pages/${page.id}/export/table/${i + 1}?format=xlsx` },
    ]).flat(),
    ...(exports.code.length ? [{ group: "Code" } as MenuItem] : []),
    ...exports.code.map((lang, i): MenuItem => ({ label: `Code ${i + 1} · ${lang}`, href: `/api/v1/pages/${page.id}/export/code/${i + 1}` })),
  ];

  const [assisting, setAssisting] = useState(false);
  const [aiFlag, setAiFlag] = useState(page.aiFlag ?? null);
  useEffect(() => { setAiFlag(page.aiFlag ?? null); }, [page.aiFlag]);
  async function markReviewed() {
    try { const p = await api.post<Page>(`/pages/${page.id}/ai-review`); setAiFlag(p.aiFlag ?? null); }
    catch (e) { setError(e instanceof ApiError && e.body?.detail ? e.body.detail : "Not marked."); }
  }
  const [formatting, setFormatting] = useState(false);
  const [formatted, setFormatted] = useState<{ before: string; result: FormatResult } | null>(null);
  async function runFormat() {
    if (!editor) return;
    setFormatting(true);
    setError(null);
    const before = fromEditor(editor);
    try { setFormatted({ before, result: await api.post<FormatResult>(`/pages/${page.id}/format`, { contentJson: before }) }); }
    catch (e) { setError(e instanceof ApiError && e.body?.detail ? e.body.detail : "Not formatted."); }
    finally { setFormatting(false); }
  }

  async function save() {
    if (!editor) return;
    setBusy(true);
    setError(null);
    try {
      const contentJson = fromEditor(editor);
      // A new page is created on its first save, as version 1.0.
      const saved = isNew
        ? await api.post<Page>(`/spaces/${page.spaceId}/pages`, { parentId: page.parentId, kind: "Page", title, contentJson, tags })
        : await api.put<Page>(`/pages/${page.id}`, { title, contentJson, baseVersion: base, tags });
      if (isNew) clearNewDraft();
      live.current.editing = false;
      onDirty(false);
      setEditing(false);
      setSavedAt(null);
      // The details are not part of a version: they are saved beside it, once the page exists.
      const done = detailsChanged(page.details, details) ? { ...saved, details: await api.put<PageDetails>(`/pages/${saved.id}/details`, detailsBody(details)) } : saved;
      onSaved(done);
    } catch (e) {
      const s = e instanceof ApiError ? e.status : 0;
      const detail = e instanceof ApiError ? e.body?.detail : undefined;
      setError(
        (s === 422 || s === 423) && detail ? detail
        : s === 409 ? "Someone saved a newer version. Copy your changes, then reload."
        : s === 400 ? "Not saved. Check the title and tags."
        : s === 401 ? "Signed out. Copy your changes, then sign in again."
        : s === 403 ? "You can't edit this page."
        : "Not saved. Server unavailable; try again.");
    } finally {
      setBusy(false);
    }
  }

  useEffect(() => {
    if (!editing) return;
    const onKey = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === "s") { e.preventDefault(); void save(); } };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });

  function scrollTo(i: number) {
    document.querySelectorAll(".prose h2, .prose h3")[i]?.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  return (
    <>
    <main className="doc">
      <div className="col">
        <nav className="crumb" aria-label="Breadcrumb">
          <Link href={to.home()} className="crumb-home" onNav={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></Link><ChevronRight className="ic sm" aria-hidden />
          <Link href={to.space(page.spaceId)} className="crumb-link" title={spaceName} onNav={() => onOpen(null)}>{spaceName}</Link>
          {page.path.map((c) => (
            <span key={c.id} className="crumb-part"><ChevronRight className="ic sm" aria-hidden /><Link href={to.page(c.id)} className="crumb-link" title={c.title} onNav={() => onOpen(c.id)}>{c.title}</Link></span>
          ))}
          {!isNew && <><ChevronRight className="ic sm" aria-hidden /><b title={page.title}>{page.title}</b></>}
        </nav>
        <div className="titlebar">
          {editing
            ? <input className="title-input" value={title} onChange={(e) => setTitle(e.target.value)} aria-label="Title" placeholder="Title" maxLength={200} autoFocus={isNew} />
            : <h1>{page.title}</h1>}
          <div className="ptools">
            {editing ? (
              <>
                {savedAt && <span className="muted small draft-at" role="status">Draft saved {new Date(savedAt).toLocaleTimeString(undefined, { timeStyle: "short" })}</span>}
                {canAssist && <button className="tb" onClick={() => setAssisting(true)} disabled={busy}>AI assist</button>}
                {canFormat && !isNew && <button className="tb" onClick={() => void runFormat()} disabled={busy || formatting}>{formatting ? "Formatting…" : "Format"}</button>}
                <button className="tb" onClick={cancel} disabled={busy}>Cancel</button>
                <button className="tb primary" onClick={save} disabled={busy || !title.trim()}>{busy ? "Saving…" : "Save"}</button>
              </>
            ) : (
              <>
                {page.canEdit && <button className="tb" onClick={startEdit}><Pencil className="ic" aria-hidden />Edit</button>}
                {!guest && <button className="tb icon" aria-label="History" title="History" onClick={onHistory}><History className="ic" aria-hidden /></button>}
                <button className="tb icon" aria-label="Copy link" title="Copy link" onClick={() => navigator.clipboard?.writeText(`${location.origin}/p/${page.id}`)}><LinkIcon className="ic" aria-hidden /></button>
                <SlotTools slot="page.tools" ctx={{ page }} />
                {!guest && <MenuButton label="Export" icon={<Download className="ic" aria-hidden />} items={exportItems} />}
                {page.canDelete && (
                  <MenuButton label="More" iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
                    { label: "Move", icon: <FolderInput className="ic" aria-hidden />, onClick: onMove },
                    { label: "Delete", icon: <Trash2 className="ic" aria-hidden />, onClick: onDelete, danger: true },
                  ]} />
                )}
              </>
            )}
          </div>
        </div>
        {aiFlag && (
          <div className="note-row" role="status">
            <span>This page holds text addressed to an AI ({aiFlag}). AI features leave it alone until the text is gone or an Owner marks the page reviewed.</span>
            {role === "Owner" && !isNew && <button type="button" className="tb" onClick={() => void markReviewed()}>Mark reviewed</button>}
          </div>
        )}
        {assisting && editor && (
          <AssistDialog spaceId={page.spaceId} pageId={isNew ? null : page.id} title={title} contentJson={fromEditor(editor)} onClose={() => setAssisting(false)}
            onApply={(r) => {
              editor.commands.setContent(toEditor(r.contentJson, extensions));
              if (!title.trim() && r.title) setTitle(r.title);
              if (r.tags.length) setTags((t) => [...new Set([...t, ...r.tags])]);
              if (r.appliesTo.length) setDetails((d) => ({ ...d, appliesTo: [...d.appliesTo, ...r.appliesTo.filter((a) => !d.appliesTo.some((x) => x.applicationId === a.applicationId))] }));
              setAssisting(false);
            }} />
        )}
        {formatted && editor && (
          <FormatDialog pageId={page.id} before={formatted.before} first={formatted.result} extensions={extensions} onClose={() => setFormatted(null)}
            onAccept={(json) => { editor.commands.setContent(toEditor(json, extensions)); setFormatted(null); }} />
        )}
        <PageDetailsPanel details={page.details} updatedAt={page.updatedAt} editing={editing} value={details} onChange={setDetails} onOpenApp={onOpenApp} />
        {error && <p className="form-error" role="alert">{error}</p>}
        {!editing && hasDraft && page.draft && page.canEdit && (
          <div className="note-row" role="status">
            <span>Unsaved draft · {new Date(page.draft.updatedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}</span>
            <button className="tb" onClick={() => void resumeDraft()}>Resume</button>
            <button className="tb" onClick={() => void discardDraft()}>Discard</button>
          </div>
        )}
        {page.draft && !page.draft.mine && <div className="note-row" role="status"><span>Being edited by {page.draft.by}</span></div>}
        {editing && editor && <Toolbar editor={editor} onImages={(files) => void insertImages(editor.view, page.spaceId, files).then(setError)} />}
        <EditorContent editor={editor} className={editing ? "editing" : ""} />
        {!editing && page.history.length > 0 && (
          <section className="history" aria-label="History">
            <h2 className="s">History</h2>
            <table>
              <tbody>
                {page.history.map((h) => (
                  <tr key={h.version}><td>{h.version}</td><td>{fmt(h.at)}</td><td>{h.author}</td></tr>
                ))}
              </tbody>
            </table>
          </section>
        )}
        {!editing && !guest && !isNew && page.kind === "Page" && <PageRating pageId={page.id} />}
        {!editing && !guest && !isNew && page.kind === "Page" && <PageComments pageId={page.id} />}
      </div>
      {!editing && headings.length > 0 && (
        <aside className="toc" aria-label="On this page">
          <b>On this page</b>
          {headings.map((h, i) => (
            <button key={i} className={`toc-link l${h.level}`} onClick={() => scrollTo(i)}>{h.text}</button>
          ))}
        </aside>
      )}
    </main>
    <aside className="side" aria-label="Details">
      <h3>Tags</h3>
      {editing
        ? <TagInput value={tags} onChange={setTags} />
        : page.tags.length ? <div className="tags">{page.tags.map((t) => (onTag ? <Link key={t} href={to.tag(t)} className="chip" title={t} onNav={() => onTag(t)}>{t}</Link> : <span key={t} className="chip" title={t}>{t}</span>))}</div> : <div className="muted">None</div>}
      {!isNew && (
        <>
          <h3>Attachments</h3>
          {files.map((a) => (
            <div key={a.id} className="file">
              <a href={a.url} download title={a.name}><Paperclip className="ic sm" aria-hidden />{a.name}</a>
              <span className="muted small">{fmtSize(a.size)}</span>
              {page.canEdit && <button className="file-x" aria-label={`Remove ${a.name}`} title="Remove" onClick={() => void detach(a)}><X className="ic sm" aria-hidden /></button>}
            </div>
          ))}
          {files.length === 0 && <div className="muted">None</div>}
          {page.canEdit && (
            <>
              <input ref={filePicker} type="file" multiple hidden onChange={(e) => { void attach(e.target.files); e.target.value = ""; }} />
              <button className="tb attach" onClick={() => filePicker.current?.click()}><Paperclip className="ic" aria-hidden />Attach</button>
            </>
          )}
        </>
      )}
      {!isNew && (
        <>
          <h3>Version</h3>
          <div>{page.version}</div>
          <h3>Last updated</h3>
          <div>{page.updatedBy}</div>
          <div className="muted small">{fmt(page.updatedAt)}</div>
        </>
      )}
      <h3>Location</h3>
      <div>{[spaceName, ...page.path.map((c) => c.title)].join(" / ")}</div>
      <h3>Your access</h3>
      <div>{role ?? "None"}</div>
      {!isNew && !guest && page.kind === "Page" && (role === "Supervisor" || role === "Owner") && <PageFeedbackSwitches pageId={page.id} />}
      {!isNew && <SlotSections slot="page.panel" ctx={{ page }} />}
    </aside>
    </>
  );
}

/** A plugin's toolbar entry. Its label may be a function of the plugin's, so it is worked out here, inside the entry's own error boundary. */
function ToolbarEntry({ item, source, ctx }: { item: ActionItem<{ editor: Editor }>; source: string; ctx: { editor: Editor } }) {
  return (
    <button type="button" className="etb text" onMouseDown={(ev) => ev.preventDefault()}
      onClick={() => { try { item.onClick?.(ctx); } catch (err) { reportUiError(source, "editor.toolbar", err); } }}>
      {typeof item.label === "function" ? item.label(ctx) : item.label}
    </button>
  );
}

export function Toolbar({ editor, onImages }: { editor: Editor; onImages: (files: File[]) => void }) {
  const dialogs = useDialogs();
  const picker = useRef<HTMLInputElement>(null);
  const ctx = useMemo(() => ({ editor }), [editor]);
  const extra = useSlot<ActionItem<{ editor: Editor }>>("editor.toolbar", ctx);
  const s = useEditorState({
    editor,
    selector: ({ editor: e }) => ({
      bold: e.isActive("bold"), italic: e.isActive("italic"), underline: e.isActive("underline"), strike: e.isActive("strike"), code: e.isActive("code"),
      h2: e.isActive("heading", { level: 2 }), h3: e.isActive("heading", { level: 3 }),
      bullet: e.isActive("bulletList"), ordered: e.isActive("orderedList"), task: e.isActive("taskList"), quote: e.isActive("blockquote"),
      codeBlock: e.isActive("codeBlock"), language: (e.getAttributes("codeBlock").language as string | undefined) ?? "plaintext",
      table: e.isActive("table"), link: e.isActive("link"),
      undo: e.can().undo(), redo: e.can().redo(),
    }),
  });
  const c = () => editor.chain().focus();
  const B = ({ on, label, run, children }: { on?: boolean; label: string; run: () => void; children: React.ReactNode }) => (
    <button type="button" className={`etb ${on ? "on" : ""}`} aria-label={label} title={label} aria-pressed={on} onMouseDown={(e) => e.preventDefault()} onClick={run}>{children}</button>
  );

  async function link() {
    if (s.link) { c().extendMarkRange("link").unsetLink().run(); return; }
    const url = await dialogs.ask({
      title: "Insert link", label: "URL", initial: "https://", ok: "Insert",
      validate: (v) => /^(https?:\/\/\S+|mailto:\S+)$/i.test(v) ? null : "Use an http, https or mailto address.",
    });
    if (url) c().extendMarkRange("link").setLink({ href: url }).run();
  }

  function tableAction(v: string) {
    const ch = c();
    ({
      row: () => ch.addRowAfter(), col: () => ch.addColumnAfter(), delrow: () => ch.deleteRow(), delcol: () => ch.deleteColumn(),
      header: () => ch.toggleHeaderRow(), del: () => ch.deleteTable(),
    } as Record<string, () => typeof ch>)[v]?.().run();
  }

  return (
    <div className="editor-toolbar" role="toolbar" aria-label="Formatting">
      <B on={s.bold} label="Bold" run={() => c().toggleBold().run()}><Bold className="ic" /></B>
      <B on={s.italic} label="Italic" run={() => c().toggleItalic().run()}><Italic className="ic" /></B>
      <B on={s.underline} label="Underline" run={() => c().toggleUnderline().run()}><Underline className="ic" /></B>
      <B on={s.strike} label="Strikethrough" run={() => c().toggleStrike().run()}><Strikethrough className="ic" /></B>
      <B on={s.code} label="Inline code" run={() => c().toggleCode().run()}><Code className="ic" /></B>
      <span className="sep" />
      <B on={s.h2} label="Heading" run={() => c().toggleHeading({ level: 2 }).run()}><Heading2 className="ic" /></B>
      <B on={s.h3} label="Subheading" run={() => c().toggleHeading({ level: 3 }).run()}><Heading3 className="ic" /></B>
      <span className="sep" />
      <B on={s.bullet} label="Bulleted list" run={() => c().toggleBulletList().run()}><List className="ic" /></B>
      <B on={s.ordered} label="Numbered list" run={() => c().toggleOrderedList().run()}><ListOrdered className="ic" /></B>
      <B on={s.task} label="Checklist" run={() => c().toggleTaskList().run()}><ListTodo className="ic" /></B>
      <B on={s.quote} label="Quote" run={() => c().toggleBlockquote().run()}><Quote className="ic" /></B>
      <B label="Divider" run={() => c().setHorizontalRule().run()}><Minus className="ic" /></B>
      <span className="sep" />
      <B on={s.codeBlock} label="Code block" run={() => c().toggleCodeBlock().run()}><SquareCode className="ic" /></B>
      {s.codeBlock && (
        <select className="etb-select" aria-label="Code language" value={s.language} onChange={(e) => c().updateAttributes("codeBlock", { language: e.target.value }).run()}>
          {LANGUAGES.map((l) => <option key={l} value={l}>{l}</option>)}
        </select>
      )}
      <B on={s.table} label="Insert table" run={() => c().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()}><Table className="ic" /></B>
      {s.table && (
        <select className="etb-select" aria-label="Table" value="" onChange={(e) => tableAction(e.target.value)}>
          <option value="" disabled>Table</option>
          <option value="row">Add row</option>
          <option value="col">Add column</option>
          <option value="delrow">Delete row</option>
          <option value="delcol">Delete column</option>
          <option value="header">Toggle header row</option>
          <option value="del">Delete table</option>
        </select>
      )}
      <B on={s.link} label={s.link ? "Remove link" : "Link"} run={() => void link()}><LinkIcon className="ic" /></B>
      <B label="Image" run={() => picker.current?.click()}><ImagePlus className="ic" /></B>
      <input ref={picker} type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden
        onChange={(e) => { const f = imagesIn(e.target.files); e.target.value = ""; if (f.length) onImages(f); }} />
      {extra.map((e) => (
        <PluginBoundary key={`${e.source}:${e.item.id}`} source={e.source} where="editor.toolbar"><ToolbarEntry item={e.item} source={e.source} ctx={ctx} /></PluginBoundary>
      ))}
      <span className="sep" />
      <B label="Undo" run={() => c().undo().run()}><Undo2 className="ic" /></B>
      <B label="Redo" run={() => c().redo().run()}><Redo2 className="ic" /></B>
    </div>
  );
}

/** What Format answers: the proposed page, its changed lines, the duplicates it took out, and the roles to build it again. */
type FormatResult = { contentJson: string; diff: DiffLine[]; removed: { units: number[]; text: string; sameAs: string }[]; roles: { n: number; role: string }[] };

/** A page's content drawn as the page draws it, not to edit. */
function Rendered({ json, extensions }: { json: string; extensions: ReturnType<typeof makeExtensions> }) {
  const editor = useEditor({ extensions, content: toEditor(json, extensions), editable: false, editorProps: { attributes: { class: "prose", "aria-label": "Proposed page" } } });
  useEffect(() => { editor?.commands.setContent(toEditor(json, extensions)); }, [editor, json, extensions]);
  return editor ? <EditorContent editor={editor} /> : null;
}

/**
 * The formatting the AI proposes: the page as it would read, or the changed lines; and each duplicate it took out, which the
 * person can keep (built again from the same roles, without the model). Accepting changes the editor only; Save saves.
 */
function FormatDialog({ pageId, before, first, extensions, onAccept, onClose }: {
  pageId: string; before: string; first: FormatResult; extensions: ReturnType<typeof makeExtensions>; onAccept: (json: string) => void; onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); }, []);
  const [result, setResult] = useState(first);
  const [view, setView] = useState<"page" | "changes">("page");
  const [keep, setKeep] = useState<number[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mark = (op: DiffLine["op"]) => (op === "Add" ? "+" : op === "Del" ? "−" : " ");

  async function toggle(units: number[], kept: boolean) {
    const next = kept ? [...new Set([...keep, ...units])] : keep.filter((n) => !units.includes(n));
    setBusy(true);
    setError(null);
    try { setResult(await api.post<FormatResult>(`/pages/${pageId}/format`, { contentJson: before, roles: first.roles, keep: next })); setKeep(next); }
    catch (e) { setError(e instanceof ApiError && e.body?.detail ? e.body.detail : "Not built again."); }
    finally { setBusy(false); }
  }

  return (
    <dialog ref={ref} className="dlg wide format-dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="fmt-title">
      <h2 id="fmt-title">Format</h2>
      <div className="seg" role="tablist" aria-label="View">
        <button type="button" role="tab" aria-selected={view === "page"} className={view === "page" ? "on" : ""} onClick={() => setView("page")}>Proposed page</button>
        <button type="button" role="tab" aria-selected={view === "changes"} className={view === "changes" ? "on" : ""} onClick={() => setView("changes")}>Changes</button>
      </div>
      {first.removed.length > 0 && (
        <ul className="removed-list" aria-label="Duplicates">
          {first.removed.map((r) => {
            const kept = r.units.every((n) => keep.includes(n));
            return (
              <li key={r.units.join(",")}>
                <span className="grow"><b>{kept ? "Kept duplicate" : "Removed duplicate"}:</b> {r.text} <span className="muted">· same as {r.sameAs}</span></span>
                <button type="button" className="tb" disabled={busy} onClick={() => void toggle(r.units, !kept)}>{kept ? "Remove" : "Keep"}</button>
              </li>
            );
          })}
        </ul>
      )}
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="format-view">
        {view === "page"
          ? <Rendered json={result.contentJson} extensions={extensions} />
          : <pre className="diff-lines">{result.diff.map((l, i) => <div key={i} className={`diff-${l.op.toLowerCase()}`}>{mark(l.op)} {l.text}</div>)}</pre>}
      </div>
      <div className="dlg-actions">
        <button type="button" className="tb" onClick={onClose}>Reject</button>
        <button type="button" className="tb primary" disabled={busy} onClick={() => onAccept(result.contentJson)} autoFocus>Accept</button>
      </div>
    </dialog>
  );
}
