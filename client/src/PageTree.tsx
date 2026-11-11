import { ChevronRight, FileText, Folder } from "lucide-react";
import { byTreeOrder, type TreeNode } from "./api";

type Props = {
  nodes: TreeNode[];
  parentId: string | null;
  selectedId: string | null;
  open: Record<string, boolean>;
  onToggle: (id: string) => void;
  onSelect: (n: TreeNode) => void;
};

export function PageTree({ nodes, parentId, selectedId, open, onToggle, onSelect }: Props) {
  const kids = nodes.filter((n) => n.parentId === parentId).sort(byTreeOrder);
  if (kids.length === 0) return parentId === null ? <div className="node empty"><span className="ic tw" /><span className="muted">No pages</span></div> : null;
  return (
    <>
      {kids.map((n) => (
        <div key={n.id}>
          <button
            className={`node ${selectedId === n.id ? "cur" : ""} ${open[n.id] ? "open" : ""}`}
            onClick={() => { if (n.kind === "Folder") onToggle(n.id); onSelect(n); }}
            aria-expanded={n.kind === "Folder" ? !!open[n.id] : undefined}
          >
            {n.kind === "Folder" ? <ChevronRight className="ic tw" aria-hidden /> : <span className="ic tw" />}
            {n.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}
            <span className="grow">{n.title}</span>
          </button>
          {n.kind === "Folder" && open[n.id] && (
            <div className="kids">
              <PageTree nodes={nodes} parentId={n.id} selectedId={selectedId} open={open} onToggle={onToggle} onSelect={onSelect} />
            </div>
          )}
        </div>
      ))}
    </>
  );
}
