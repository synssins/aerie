import { useCallback, useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { ChevronRight, Folder } from "lucide-react";
import { api, byTreeOrder, EDIT_ROLES, type Page, type Space, type TreeNode } from "./api";

/** Closes a popover on a click outside it or Escape. */
export function useDismiss(ref: RefObject<HTMLElement | null>, open: boolean, close: () => void) {
  useEffect(() => {
    if (!open) return;
    const h = (e: Event) => {
      if (e instanceof KeyboardEvent ? e.key === "Escape" : !ref.current?.contains(e.target as Node)) close();
    };
    document.addEventListener("pointerdown", h);
    document.addEventListener("keydown", h);
    return () => { document.removeEventListener("pointerdown", h); document.removeEventListener("keydown", h); };
  }, [ref, open, close]);
}

export type MenuItem = { label: string; icon?: ReactNode; onClick?: () => void; href?: string; danger?: boolean } | "sep" | { group: string };

/** A page-tool button that opens a dropdown menu. Link items download a file. */
export function MenuButton({ label, icon, items, iconOnly = false }: { label: string; icon: ReactNode; items: MenuItem[]; iconOnly?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useDismiss(ref, open, useCallback(() => setOpen(false), []));
  return (
    <div className="menu-wrap" ref={ref}>
      <button className={`tb ${iconOnly ? "icon" : ""}`} onClick={() => setOpen(!open)} aria-haspopup="menu" aria-expanded={open} aria-label={iconOnly ? label : undefined} title={iconOnly ? label : undefined}>
        {icon}{!iconOnly && label}
      </button>
      {open && (
        <div className="menu right page-menu" role="menu">
          {items.map((it, i) =>
            it === "sep" ? <div key={i} className="menu-sep" />
            : "group" in it ? <div key={i} className="menu-grp">{it.group}</div>
            : it.href ? <a key={i} role="menuitem" href={it.href} download onClick={() => setOpen(false)}>{it.icon}{it.label}</a>
            : <button key={i} role="menuitem" className={it.danger ? "danger" : ""} onClick={() => { setOpen(false); it.onClick?.(); }}>{it.icon}{it.label}</button>)}
        </div>
      )}
    </div>
  );
}

/** Pick where an item goes: a space, then a folder in it (or the top of the space). */
export function MoveDialog({ item, spaces, onMoved, onClose }: { item: Page; spaces: Space[]; onMoved: (p: Page) => void; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const targets = spaces.filter((s) => s.myRole && EDIT_ROLES.includes(s.myRole));
  const [spaceId, setSpaceId] = useState(item.spaceId);
  const [nodes, setNodes] = useState<TreeNode[]>([]);
  const [parentId, setParentId] = useState<string | null>(item.parentId);
  const [open, setOpen] = useState<Record<string, boolean>>(() => Object.fromEntries(item.path.map((c) => [c.id, true])));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { ref.current?.showModal(); }, []);
  useEffect(() => {
    void api.get<TreeNode[]>(`/spaces/${spaceId}/tree`).then((t) => setNodes(t.filter((n) => n.kind === "Folder")));
  }, [spaceId]);

  // A folder can't go inside itself.
  const blocked = new Set<string>([item.id]);
  for (let grew = true; grew;) {
    grew = false;
    for (const n of nodes) if (n.parentId && blocked.has(n.parentId) && !blocked.has(n.id)) { blocked.add(n.id); grew = true; }
  }

  async function move() {
    setBusy(true);
    setError(null);
    try { onMoved(await api.post<Page>(`/pages/${item.id}/move`, { spaceId, parentId })); }
    catch { setError("Not moved."); setBusy(false); }
  }

  const rows = (parent: string | null, depth: number): ReactNode =>
    nodes.filter((n) => n.parentId === parent && !blocked.has(n.id)).sort(byTreeOrder).map((n) => {
      const hasKids = nodes.some((k) => k.parentId === n.id && !blocked.has(k.id));
      return (
        <div key={n.id}>
          <div className={`node split ${parentId === n.id ? "cur" : ""}`} style={{ paddingLeft: depth * 16 }}>
            {hasKids
              ? <button type="button" className={`twb ${open[n.id] ? "open" : ""}`} aria-label={open[n.id] ? "Collapse" : "Expand"} onClick={() => setOpen((o) => ({ ...o, [n.id]: !o[n.id] }))}><ChevronRight className="ic tw" aria-hidden /></button>
              : <span className="twb" />}
            <button type="button" className="grow node-label" onClick={() => setParentId(n.id)}><Folder className="ic ft" aria-hidden />{n.title}</button>
          </div>
          {open[n.id] && rows(n.id, depth + 1)}
        </div>
      );
    });

  const same = spaceId === item.spaceId && parentId === item.parentId;
  return (
    <dialog ref={ref} className="dlg wide" onCancel={(e) => { e.preventDefault(); onClose(); }} aria-labelledby="move-title">
      <h2 id="move-title">Move “{item.title}”</h2>
      {targets.length > 1 && (
        <label>
          Space
          <select className="sel" value={spaceId} onChange={(e) => { setSpaceId(e.target.value); setParentId(null); }}>
            {targets.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>
      )}
      <div className="picker" role="tree" aria-label="Folder">
        <button type="button" className={`node ${parentId === null ? "cur" : ""}`} onClick={() => setParentId(null)}>
          <Folder className="ic ft" aria-hidden /><span className="grow">{targets.find((s) => s.id === spaceId)?.name ?? "Space"}</span>
        </button>
        <div className="kids">{rows(null, 0)}</div>
      </div>
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="dlg-actions">
        <button type="button" className="tb" onClick={onClose}>Cancel</button>
        <button type="button" className="tb primary" disabled={busy || same} onClick={() => void move()}>Move</button>
      </div>
    </dialog>
  );
}
