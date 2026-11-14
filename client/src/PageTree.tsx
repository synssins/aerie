import { ChevronRight, FileText, Folder } from "lucide-react";
import { byTreeOrder, type TreeNode } from "./api";
import { Link, to } from "./Link";

type Props = {
  nodes: TreeNode[];
  parentId: string | null;
  selectedId: string | null;
  open: Record<string, boolean>;
  onToggle: (id: string) => void;
  onSelect: (n: TreeNode) => void;
  /** A node's address, so it can be opened in a new tab; null for nodes with none (what is in the recycle bin). Pages by default. */
  hrefOf?: (n: TreeNode) => string | null;
};

const pageHref = (n: TreeNode) => to.page(n.id);

export function PageTree({ nodes, parentId, selectedId, open, onToggle, onSelect, hrefOf = pageHref }: Props) {
  const kids = nodes.filter((n) => n.parentId === parentId).sort(byTreeOrder);
  if (kids.length === 0) return parentId === null ? <div className="node empty"><span className="ic tw" /><span className="muted">No pages</span></div> : null;
  return (
    <>
      {kids.map((n) => {
        const href = hrefOf(n);
        const select = () => { if (n.kind === "Folder") onToggle(n.id); onSelect(n); };
        const props = {
          className: `node ${selectedId === n.id ? "cur" : ""} ${open[n.id] ? "open" : ""}`,
          "aria-expanded": n.kind === "Folder" ? !!open[n.id] : undefined,
          title: n.title,
        };
        const inner = (
          <>
            {n.kind === "Folder" ? <ChevronRight className="ic tw" aria-hidden /> : <span className="ic tw" />}
            {n.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}
            <span className="grow">{n.title}</span>
          </>
        );
        return (
          <div key={n.id}>
            {href
              ? <Link href={href} {...props} aria-current={selectedId === n.id ? "page" : undefined} onNav={select}>{inner}</Link>
              : <button type="button" {...props} onClick={select}>{inner}</button>}
            {n.kind === "Folder" && open[n.id] && (
              <div className="kids">
                <PageTree nodes={nodes} parentId={n.id} selectedId={selectedId} open={open} onToggle={onToggle} onSelect={onSelect} hrefOf={hrefOf} />
              </div>
            )}
          </div>
        );
      })}
    </>
  );
}
