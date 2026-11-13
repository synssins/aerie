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
// Set on the one carrier that holds a whole document whose body could not be read at all.
const top = { default: false, rendered: false };

/** A box with the type's name. It can be moved or deleted, not edited. */
const box = (name: string, inline: boolean) => Node.create({
  name,
  group: inline ? "inline" : "block",
  inline,
  atom: true,
  selectable: true,
  draggable: true,
  addAttributes: () => (inline ? { original } : { original, top }),
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

// What is carried may be anything a page can hold where a node should be: a node of a plugin, or a null, a number, a text.
const carry = (node: unknown, inline: boolean): JSONContent => ({ type: inline ? INLINE : BLOCK, attrs: { original: JSON.stringify(node) ?? "null" } });
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

/** Whether these children, in this order, are content the node type takes. */
function takes(type: NodeType, kids: JSONContent[], schema: Schema): boolean {
  let match: typeof type.contentMatch | null = type.contentMatch;
  for (const k of kids) {
    match = match?.matchType(schema.nodes[k.type ?? ""]) ?? null;
    if (!match) return false;
  }
  return match.validEnd;
}

/**
 * The node as the editor can hold it. `changed` tells that it, or something under it, was swapped for a carrier.
 * The server stores a page as its writer sent it, so a node here can be of any shape: whatever is not a node the editor's
 * schema can make is carried whole.
 */
function wrapNode(node: unknown, parent: NodeType, schema: Schema): { node: JSONContent; changed: boolean } {
  const whole = () => ({ node: carry(node, parent.inlineContent), changed: true });
  if (!isObject(node) || typeof node.type !== "string") return whole();
  const type = schema.nodes[node.type];
  if (!type) return whole();
  if (type.isText && (typeof node.text !== "string" || node.text.length === 0)) return whole();
  if (node.attrs !== undefined && !isObject(node.attrs)) return whole();

  let changed = false;
  let next = node as JSONContent;
  if (node.marks !== undefined) {
    if (!Array.isArray(node.marks) || node.marks.some((m) => !isObject(m) || typeof m.type !== "string" || (m.attrs !== undefined && !isObject(m.attrs)))) return whole();
    const marks = node.marks as { type: string; attrs?: Record<string, unknown> }[];
    if (marks.some((m) => !schema.marks[m.type])) {
      // A place that takes no marks (a code block) cannot hold the carrier mark. The carrier that comes back does not fit there either, so that place is carried whole.
      if (!parent.allowsMarkType(schema.marks[MARK])) return whole();
      changed = true;
      next = { ...next, marks: marks.map((m) => (schema.marks[m.type] ? m : { type: MARK, attrs: { original: JSON.stringify(m) } })) };
    }
  }
  if (node.content !== undefined) {
    if (!Array.isArray(node.content)) return whole();
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
  const all = (): JSONContent => ({ type: schema.topNodeType.name, content: [{ type: BLOCK, attrs: { original: JSON.stringify(doc) ?? "null", top: true } }] });
  if (!isObject(doc) || doc.type !== schema.topNodeType.name) return all();
  if (doc.content === undefined) return doc;
  if (!Array.isArray(doc.content)) return all();
  try { return { ...doc, content: doc.content.map((n) => wrapNode(n, schema.topNodeType, schema).node) }; }
  catch { return all(); } // whatever else is wrong with it: the page opens, as one box, and is saved as it was
}

type Held = { value: unknown } | null;
/** What a carrier holds; null for one that holds nothing readable (one pasted in from elsewhere). */
const held = (text: unknown): Held => {
  if (typeof text !== "string" || text.length === 0) return null;
  try { return { value: JSON.parse(text) as unknown }; } catch { return null; }
};

/** The node as it is stored; `drop` for a carrier that holds nothing. */
function unwrapNode(node: JSONContent): { drop: true } | { drop: false; value: unknown } {
  if (node.type === BLOCK || node.type === INLINE) {
    const h = held(node.attrs?.original);
    return h ? { drop: false, value: h.value } : { drop: true };
  }
  let next = node;
  if (node.marks?.some((m) => m.type === MARK)) {
    const marks = node.marks.flatMap((m) => {
      if (m.type !== MARK) return [m];
      const h = held(m.attrs?.original);
      return h ? [h.value as { type: string }] : [];
    });
    next = { ...next, marks };
    if (marks.length === 0) delete next.marks;
  }
  if (node.content) next = { ...next, content: node.content.flatMap((n) => { const u = unwrapNode(n); return u.drop ? [] : [u.value as JSONContent]; }) };
  return { drop: false, value: next };
}

/** The document as it is stored: every carrier swapped back for what it holds. */
export function unwrapUnknown(doc: JSONContent): JSONContent {
  // A document that was carried as one box, and is still just that box, is itself again.
  if (doc.content?.length === 1 && doc.content[0].type === BLOCK && doc.content[0].attrs?.top === true) {
    const all = held(doc.content[0].attrs.original);
    if (all) return all.value as JSONContent;
  }
  const u = unwrapNode(doc);
  return u.drop ? doc : u.value as JSONContent;
}
