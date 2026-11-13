import { Mark, Node, type JSONContent } from "@tiptap/react";
import type { NodeType, Schema } from "@tiptap/pm/model";

/**
 * Content the editor does not know is carried, never dropped. A page can hold a node or a mark of a plugin that is not loaded
 * here. Given such a document as it is, the editor would fall back to an empty one, and the next save would store that.
 * So before a document goes into the editor, every node and mark its schema lacks is swapped for one of the three below,
 * which hold the original JSON; on the way out each is swapped back.
 */
const BLOCK = "kbUnknownBlock", INLINE = "kbUnknownInline", MARK = "kbUnknownMark";

const typeOf = (original: string): string => {
  try { return String((JSON.parse(original) as { type?: unknown }).type ?? ""); } catch { return ""; }
};

const original = { default: "", parseHTML: (el: HTMLElement) => el.getAttribute("data-original") ?? "", renderHTML: (a: { original: string }) => ({ "data-original": a.original }) };

/** A box with the type's name. It can be moved or deleted, not edited. */
const box = (name: string, inline: boolean) => Node.create({
  name,
  group: inline ? "inline" : "block",
  inline,
  atom: true,
  selectable: true,
  draggable: true,
  addAttributes: () => ({ original }),
  parseHTML: () => [{ tag: `${inline ? "span" : "div"}[data-kb-unknown]` }],
  renderHTML: ({ node, HTMLAttributes }) => [inline ? "span" : "div", { ...HTMLAttributes, "data-kb-unknown": "", class: "kb-unknown", contenteditable: "false" }, typeOf(String(node.attrs.original))],
});

export const UnknownBlock = box(BLOCK, false);
export const UnknownInline = box(INLINE, true);
export const UnknownMark = Mark.create({
  name: MARK,
  excludes: "", // several unknown marks can sit on the same text
  inclusive: false,
  addAttributes: () => ({ original }),
  parseHTML: () => [{ tag: "span[data-kb-unknown-mark]" }],
  renderHTML: ({ HTMLAttributes }) => ["span", { ...HTMLAttributes, "data-kb-unknown-mark": "" }, 0],
});
export const unknownExtensions = [UnknownBlock, UnknownInline, UnknownMark];

const carry = (node: JSONContent, inline: boolean): JSONContent => ({ type: inline ? INLINE : BLOCK, attrs: { original: JSON.stringify(node) } });

/** Whether these children, in this order, are content the node type takes. */
function takes(type: NodeType, kids: JSONContent[], schema: Schema): boolean {
  let match: typeof type.contentMatch | null = type.contentMatch;
  for (const k of kids) {
    match = match?.matchType(schema.nodes[k.type ?? ""]) ?? null;
    if (!match) return false;
  }
  return match.validEnd;
}

/** The node as the editor can hold it. `changed` tells that it, or something under it, was swapped for a carrier. */
function wrapNode(node: JSONContent, parent: NodeType, schema: Schema): { node: JSONContent; changed: boolean } {
  const whole = () => ({ node: carry(node, parent.inlineContent), changed: true });
  const type = schema.nodes[node.type ?? ""];
  if (!type) return whole();

  let changed = false;
  let next = node;
  if (node.marks?.some((m) => !schema.marks[m.type])) {
    // A place that takes no marks (a code block) cannot hold the carrier mark. The carrier that comes back does not fit there either, so that place is carried whole.
    if (!parent.allowsMarkType(schema.marks[MARK])) return whole();
    changed = true;
    next = { ...next, marks: node.marks.map((m) => (schema.marks[m.type] ? m : { type: MARK, attrs: { original: JSON.stringify(m) } })) };
  }
  if (node.content) {
    const kids = node.content.map((k) => wrapNode(k, type, schema));
    if (kids.some((k) => k.changed)) {
      changed = true;
      next = { ...next, content: kids.map((k) => k.node) };
      // A carrier cannot stand everywhere an unknown node stood (first in a list item, inside a table row): then this node is carried whole.
      if (!takes(type, next.content!, schema)) return whole();
    }
  }
  return { node: next, changed };
}

/** The document as the editor can hold it. */
export function wrapUnknown(doc: JSONContent, schema: Schema): JSONContent {
  return doc.content ? { ...doc, content: doc.content.map((n) => wrapNode(n, schema.topNodeType, schema).node) } : doc;
}

const held = <T>(original: unknown): T | null => {
  try { const v = JSON.parse(String(original)) as T | null; return v && typeof v === "object" ? v : null; } catch { return null; }
};

/** The node as it is stored; null for a carrier that holds nothing (one pasted in from elsewhere). */
function unwrapNode(node: JSONContent): JSONContent | null {
  if (node.type === BLOCK || node.type === INLINE) return held<JSONContent>(node.attrs?.original);
  let next = node;
  if (node.marks?.some((m) => m.type === MARK)) {
    const marks = node.marks.map((m) => (m.type === MARK ? held<{ type: string }>(m.attrs?.original) : m)).filter((m) => m !== null);
    next = { ...next, marks };
    if (marks.length === 0) delete next.marks;
  }
  if (node.content) next = { ...next, content: node.content.map(unwrapNode).filter((n) => n !== null) };
  return next;
}

/** The document as it is stored: every carrier swapped back for what it holds. */
export function unwrapUnknown(doc: JSONContent): JSONContent {
  return unwrapNode(doc) ?? doc;
}
