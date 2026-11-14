import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { EditorContent, useEditor } from "@tiptap/react";
import { Link, to } from "./Link";
import { ChevronRight, FileText, Folder, House, Lock, RotateCcw, Trash2 } from "lucide-react";
import { api, byTreeOrder, fmtDateTime, type TreeNode } from "./api";
import { editorExtensions, makeExtensions, toEditor } from "./editor/extensions";
import { PageTree } from "./PageTree";

type BinNode = TreeNode & { spaceId: string };
type DeletedSpace = { id: string; name: string; isSensitive: boolean };
type BinPage = {
  id: string; spaceId: string; kind: "Folder" | "Page"; title: string; contentJson: string; path: string[];
  updatedAt: string; updatedBy: string; deletedAt: string; deletedBy: string; restoreId: string | null; children: BinNode[];
};

const Empty = () => <div className="node empty"><span className="ic tw" /><span className="muted">Empty</span></div>;

/**
 * A recycle bin, browsed like the knowledge base: folders open, an item is picked and shown in the middle. With a space, that
 * space's bin. Without one, the admin bin: every space that has something in its bin, and every deleted space with all that was
 * in it. A deleted sensitive space is named and cannot be opened.
 */
export function BinTree({ spaceId, spaces = [], selectedId, reloadKey, onSelect }: {
  spaceId: string | null; spaces?: { id: string; name: string; isSensitive?: boolean }[]; selectedId: string | null; reloadKey?: number; onSelect: (id: string) => void;
}) {
  const [nodes, setNodes] = useState<BinNode[] | null>(null);
  const [gone, setGone] = useState<DeletedSpace[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  useEffect(() => {
    let live = true;
    // Both lists are taken together and shown together: the tree is never drawn from one new list and one old one.
    void Promise.all([
      api.get<BinNode[]>(spaceId ? `/spaces/${spaceId}/bin/tree` : "/admin/bin/tree"),
      spaceId ? Promise.resolve<DeletedSpace[]>([]) : api.get<DeletedSpace[]>("/admin/spaces/deleted"),
    ]).then(([n, g]) => { if (live) { setNodes(n); setGone(g); } }).catch(() => { if (live) { setNodes([]); setGone([]); } });
    return () => { live = false; };
  }, [spaceId, reloadKey]);

  if (!nodes) return null;
  const toggle = (id: string) => setOpen((o) => ({ ...o, [id]: !o[id] }));
  const tree = (of: BinNode[]) => <PageTree nodes={of} parentId={null} selectedId={selectedId} open={open} onToggle={toggle} onSelect={(n) => onSelect(n.id)} hrefOf={() => null} />;
  if (spaceId) return nodes.length > 0 ? tree(nodes) : <Empty />;

  const groups = [
    ...gone.map((g) => ({ ...g, deleted: true })),
    // A space the caller's own list still (or already) has as live is shown once: as deleted, which is what the server just said.
    ...spaces.filter((s) => !gone.some((g) => g.id === s.id) && nodes.some((n) => n.spaceId === s.id)).map((s) => ({ id: s.id, name: s.name, isSensitive: !!s.isSensitive, deleted: false })),
  ];
  if (groups.length === 0) return <Empty />;
  return (
    <>
      {groups.map((g) => {
        const of = nodes.filter((n) => n.spaceId === g.id);
        return (
          <div key={g.id}>
            <button className={`node ${open[g.id] ? "open" : ""}`} aria-expanded={!!open[g.id]} disabled={of.length === 0} onClick={() => toggle(g.id)}>
              <ChevronRight className="ic tw" aria-hidden /><Folder className="ic ft" aria-hidden /><span className="grow">{g.name}</span>
              {g.deleted && <Trash2 className="ic sm muted" aria-label="Deleted space" />}
              {g.isSensitive && <Lock className="ic sm muted" aria-label="Sensitive" />}
            </button>
            {open[g.id] && <div className="kids">{tree(of)}</div>}
          </div>
        );
      })}
    </>
  );
}

/** One item of a bin, to read: the page as it was, never to edit. The right panel says who deleted it, when, and where it was. */
export function BinPageView({ id, onHome, onBin, onOpen, onRestored }: { id: string; onHome: () => void; onBin: () => void; onOpen: (id: string) => void; onRestored: (spaceId: string) => void }) {
  const [page, setPage] = useState<BinPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const back = useRef(onBin);
  back.current = onBin;
  useEffect(() => {
    let live = true;
    setPage(null);
    setError(null);
    // An item that is not there (restored meanwhile, or not this person's to see): back to the bin.
    api.get<BinPage>(`/bin/pages/${id}`).then((p) => { if (live) setPage(p); }).catch(() => { if (live) back.current(); });
    return () => { live = false; };
  }, [id]);

  async function restore(p: BinPage) {
    setError(null);
    try { await api.post(`/pages/${p.restoreId}/restore`); onRestored(p.spaceId); }
    catch { setError("Not restored."); }
  }

  if (!page) return <><main className="doc" /><aside className="side" aria-label="Details" /></>;
  return (
    <>
      <main className="doc">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <Link href={to.home()} className="crumb-home" onNav={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></Link><ChevronRight className="ic sm" aria-hidden />
            <button className="crumb-link" onClick={onBin}>Recycle bin</button>
            {page.path.map((t, i) => <span key={i} className="crumb-part"><ChevronRight className="ic sm" aria-hidden /><span>{t}</span></span>)}
            <ChevronRight className="ic sm" aria-hidden /><b>{page.title}</b>
          </nav>
          <div className="titlebar">
            <h1>{page.title}</h1>
            <div className="ptools">
              {page.restoreId && <button className="tb" onClick={() => void restore(page)}><RotateCcw className="ic" aria-hidden />Restore</button>}
            </div>
          </div>
          {error && <p className="form-error" role="alert">{error}</p>}
          {page.kind === "Page" ? <BinDoc key={page.id} json={page.contentJson} /> : (
            <div className="listing">
              {[...page.children].sort(byTreeOrder).map((n) => (
                <button key={n.id} className="node" onClick={() => onOpen(n.id)}>
                  {n.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}
                  <span className="grow">{n.title}</span>
                </button>
              ))}
              {page.children.length === 0 && <p className="muted">Empty.</p>}
            </div>
          )}
        </div>
      </main>
      <aside className="side" aria-label="Details">
        <h3>Deleted</h3>
        <div>{page.deletedBy}</div>
        <div className="muted small">{fmtDateTime(page.deletedAt)}</div>
        <h3>Last updated</h3>
        <div>{page.updatedBy}</div>
        <div className="muted small">{fmtDateTime(page.updatedAt)}</div>
        <h3>Location</h3>
        <div>{page.path.join(" / ")}</div>
      </aside>
    </>
  );
}

function BinDoc({ json }: { json: string }) {
  const added = useSyncExternalStore(editorExtensions.subscribe, editorExtensions.version);
  // A plugin's script may add to the editor later; the document is drawn again then.
  const extensions = useMemo(() => makeExtensions("plaintext"), [added]);
  const editor = useEditor({
    extensions,
    content: toEditor(json || '{"type":"doc","content":[]}', extensions),
    editable: false,
    editorProps: { attributes: { class: "prose", "aria-label": "Page content" } },
  }, [extensions]);
  return <EditorContent editor={editor} />;
}
