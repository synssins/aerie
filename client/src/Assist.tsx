import { useEffect, useMemo, useRef, useState, type ClipboardEvent, type DragEvent } from "react";
import { EditorContent, useEditor } from "@tiptap/react";
import { ImagePlus, X } from "lucide-react";
import { api, ApiError, type AppliesTo, type DiffLine } from "./api";
import { makeExtensions, toEditor } from "./editor/extensions";

/** What AI assist answers: the page as it would read, its changed lines, and what the server found in it. */
export type AssistResult = {
  title: string; contentJson: string; diff: DiffLine[];
  removed: { units: number[]; text: string; sameAs: string }[]; changed: { from: string; to: string }[];
  invented: string[]; warnings: string[]; tags: string[]; appliesTo: AppliesTo[];
};

type Node = { type: string; attrs?: Record<string, unknown>; content?: Node[]; text?: string };
type Section = { heading: string; nodes: Node[] };
const textOf = (n: Node): string => n.text ?? (n.content ?? []).map(textOf).join("");

/** A page cut at its top-level headings; what comes before the first heading is a section with no heading. */
export function sections(json: string): Section[] {
  const doc = JSON.parse(json) as Node;
  const out: Section[] = [{ heading: "", nodes: [] }];
  for (const n of doc.content ?? []) {
    if (n.type === "heading") out.push({ heading: textOf(n).trim(), nodes: [n] });
    else out[out.length - 1].nodes.push(n);
  }
  return out.filter((s, i) => i > 0 || s.nodes.length > 0);
}

/**
 * The page with only some sections of the proposal: a proposed section not accepted is the page's own section of that heading,
 * or nothing when the page had none. A section of the page the proposal has no heading for stays at the end.
 */
export function combine(current: string, proposed: string, accepted: Set<number>): string {
  const mine = sections(current);
  const theirs = sections(proposed);
  const used = new Set<number>();
  const nodes: Node[] = [];
  theirs.forEach((s, i) => {
    const own = mine.findIndex((m, j) => !used.has(j) && m.heading.toLowerCase() === s.heading.toLowerCase());
    if (own >= 0) used.add(own);
    if (accepted.has(i)) nodes.push(...s.nodes);
    else if (own >= 0) nodes.push(...mine[own].nodes);
  });
  mine.forEach((m, j) => { if (!used.has(j)) nodes.push(...m.nodes); });
  return JSON.stringify({ type: "doc", content: nodes });
}

const asBase64 = (f: File) => new Promise<string>((ok, fail) => {
  const r = new FileReader();
  r.onload = () => ok(String(r.result).split(",")[1] ?? "");
  r.onerror = () => fail(r.error);
  r.readAsDataURL(f);
});

function Rendered({ json }: { json: string }) {
  const extensions = useMemo(() => makeExtensions("plaintext"), []);
  const editor = useEditor({ extensions, content: toEditor(json, extensions), editable: false, editorProps: { attributes: { class: "prose", "aria-label": "Proposed page" } } });
  useEffect(() => { editor?.commands.setContent(toEditor(json, extensions)); }, [editor, json, extensions]);
  return editor ? <EditorContent editor={editor} /> : null;
}

/**
 * AI assist: paste or drop material (text, screenshots, files), and the page is built from it, or it is merged into the page.
 * Nothing changes until the preview is accepted, whole or by section; accepting puts it in the editor, and Save saves.
 */
export function AssistDialog({ spaceId, pageId, title, contentJson, onApply, onClose }: {
  spaceId: string; pageId: string | null; title: string; contentJson: string;
  onApply: (r: { contentJson: string; title: string; tags: string[]; appliesTo: AppliesTo[] }) => void; onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); }, []);
  const [text, setText] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<AssistResult | null>(null);
  const [view, setView] = useState<"page" | "changes">("page");
  const [accepted, setAccepted] = useState<Set<number>>(new Set());
  const [tags, setTags] = useState<Set<string>>(new Set());
  const [applies, setApplies] = useState<Set<string>>(new Set());
  const picker = useRef<HTMLInputElement>(null);

  const add = (list: FileList | File[]) => setFiles((f) => [...f, ...[...list].filter((x) => !f.some((y) => y.name === x.name && y.size === x.size))]);
  function paste(e: ClipboardEvent) { if (e.clipboardData.files.length) { e.preventDefault(); add(e.clipboardData.files); } }
  function drop(e: DragEvent) { e.preventDefault(); if (e.dataTransfer.files.length) add(e.dataTransfer.files); }

  async function run() {
    setBusy(true);
    setError(null);
    try {
      const payload = await Promise.all(files.map(async (f) => ({ name: f.name, data: await asBase64(f) })));
      const r = await api.post<AssistResult>(`/spaces/${spaceId}/assist`, { pageId, title, contentJson, text, files: payload });
      setResult(r);
      setAccepted(new Set(sections(r.contentJson).map((_, i) => i)));
      setTags(new Set(r.tags));
      setApplies(new Set(r.appliesTo.map((a) => a.applicationId)));
    } catch (e) { setError(e instanceof ApiError && (e.body?.detail || (e.body?.errors && Object.values(e.body.errors).flat()[0])) || "Not done."); }
    finally { setBusy(false); }
  }

  function apply(all: boolean) {
    if (!result) return;
    const parts = sections(result.contentJson);
    const json = all || accepted.size === parts.length ? result.contentJson : combine(contentJson, result.contentJson, accepted);
    onApply({ contentJson: json, title: result.title || title, tags: result.tags.filter((t) => tags.has(t)), appliesTo: result.appliesTo.filter((a) => applies.has(a.applicationId)) });
  }

  const parts = result ? sections(result.contentJson) : [];
  return (
    <dialog ref={ref} className="dlg wide format-dlg" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="assist-title">
      <h2 id="assist-title">AI assist</h2>
      {!result ? (
        <>
          <div className="assist-drop" onDragOver={(e) => e.preventDefault()} onDrop={drop}>
            <label className="assist-label">Pasted material
              <textarea value={text} onChange={(e) => setText(e.target.value)} onPaste={paste} rows={10} />
            </label>
            <div className="bar-row">
              <button type="button" className="tb" onClick={() => picker.current?.click()}><ImagePlus className="ic" aria-hidden />Add files</button>
              <input ref={picker} type="file" multiple hidden aria-label="Files" onChange={(e) => { if (e.target.files) add(e.target.files); e.target.value = ""; }} />
              {files.map((f) => (
                <span key={f.name + f.size} className="chip">{f.name}<button type="button" aria-label={`Remove ${f.name}`} onClick={() => setFiles((all) => all.filter((x) => x !== f))}><X className="ic sm" aria-hidden /></button></span>
              ))}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          <div className="dlg-actions">
            <button type="button" className="tb" onClick={onClose}>Cancel</button>
            <button type="button" className="tb primary" disabled={busy} onClick={() => void run()}>
              {busy ? "Working…" : text.trim() || files.length ? "Build page" : "Organize page"}
            </button>
          </div>
        </>
      ) : (
        <>
          <div className="seg" role="tablist" aria-label="View">
            <button type="button" role="tab" aria-selected={view === "page"} className={view === "page" ? "on" : ""} onClick={() => setView("page")}>Proposed page</button>
            <button type="button" role="tab" aria-selected={view === "changes"} className={view === "changes" ? "on" : ""} onClick={() => setView("changes")}>Changes</button>
          </div>
          {result.warnings.map((w) => <p key={w} className="note-row" role="status"><span>{w}</span></p>)}
          {(result.removed.length > 0 || result.changed.length > 0) && (
            <ul className="removed-list" aria-label="What changed in the page">
              {result.removed.map((r) => <li key={`r${r.units.join(",")}`}><span className="grow"><b>Removed duplicate:</b> {r.text} <span className="muted">· same as {r.sameAs}</span></span></li>)}
              {result.changed.map((c, i) => <li key={`c${i}`}><span className="grow"><b>Changed:</b> {c.from} <span className="muted">→ {c.to}</span></span></li>)}
            </ul>
          )}
          <div className="format-view">
            {view === "page" ? <Rendered json={result.contentJson} />
              : <pre className="diff-lines">{result.diff.map((l, i) => <div key={i} className={`diff-${l.op.toLowerCase()}`}>{l.op === "Add" ? "+" : l.op === "Del" ? "−" : " "} {l.text}</div>)}</pre>}
          </div>
          {parts.length > 1 && (
            <fieldset className="assist-sections">
              <legend>Sections to take</legend>
              {parts.map((s, i) => (
                <label key={i} className="check"><input type="checkbox" className="chk" checked={accepted.has(i)}
                  onChange={(e) => { const n = new Set(accepted); if (e.target.checked) n.add(i); else n.delete(i); setAccepted(n); }} />{s.heading || "Top of the page"}</label>
              ))}
            </fieldset>
          )}
          {(result.tags.length > 0 || result.appliesTo.length > 0) && (
            <fieldset className="assist-sections">
              <legend>Suggestions</legend>
              {result.tags.map((t) => (
                <label key={t} className="check"><input type="checkbox" className="chk" checked={tags.has(t)}
                  onChange={(e) => { const n = new Set(tags); if (e.target.checked) n.add(t); else n.delete(t); setTags(n); }} />Tag {t}</label>
              ))}
              {result.appliesTo.map((a) => (
                <label key={a.applicationId} className="check"><input type="checkbox" className="chk" checked={applies.has(a.applicationId)}
                  onChange={(e) => { const n = new Set(applies); if (e.target.checked) n.add(a.applicationId); else n.delete(a.applicationId); setApplies(n); }} />
                  Applies to {a.name}{a.servers.length ? ` (${a.servers.join(", ")})` : ""}</label>
              ))}
            </fieldset>
          )}
          <div className="dlg-actions">
            <button type="button" className="tb" onClick={onClose}>Discard</button>
            {parts.length > 1 && <button type="button" className="tb" disabled={accepted.size === 0} onClick={() => apply(false)}>Accept selected sections</button>}
            <button type="button" className="tb primary" onClick={() => apply(true)} autoFocus>Accept all</button>
          </div>
        </>
      )}
    </dialog>
  );
}
