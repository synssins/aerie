import { afterEach, describe, expect, it, vi } from "vitest";
import { Editor, Node, type JSONContent } from "@tiptap/react";
import { editorExtensions, makeExtensions, schemaOf } from "./extensions";
import { unwrapUnknown, wrapUnknown } from "./unknown";

// A plugin's block, a plugin's inline node, a plugin's mark, and a plugin's block inside a list.
const DOC: JSONContent = {
  type: "doc",
  content: [
    { type: "paragraph", content: [{ type: "text", text: "Before" }] },
    { type: "probeBox", attrs: { label: "Warning", extra: { n: 1 } }, content: [{ type: "paragraph", content: [{ type: "text", text: "inside the box" }] }] },
    { type: "paragraph", content: [
      { type: "text", text: "Ask " },
      { type: "probeMention", attrs: { name: "gail" } },
      { type: "text", text: " about " },
      { type: "text", text: "this", marks: [{ type: "probeHighlight", attrs: { color: "red" } }] },
    ] },
    { type: "bulletList", content: [{ type: "listItem", content: [
      { type: "paragraph", content: [{ type: "text", text: "item" }] },
      { type: "probeBox", attrs: { label: "In a list" } },
    ] }] },
  ],
};

const editors: Editor[] = [];
function open(doc: JSONContent): Editor {
  const extensions = makeExtensions("plaintext");
  const editor = new Editor({ extensions, content: wrapUnknown(doc, schemaOf(extensions)) });
  editors.push(editor);
  return editor;
}
const out = (editor: Editor) => unwrapUnknown(editor.getJSON());

afterEach(() => { editors.splice(0).forEach((e) => e.destroy()); editorExtensions.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("unknown editor content", () => {
  it("round-trips unchanged: a block, an inline node, a mark, and a block inside a list", () => {
    expect(out(open(DOC))).toEqual(DOC);
  });

  it("without the wrapping the editor would have emptied the page", () => {
    // What this guards against: given a node it does not know, the editor falls back to an empty document, and a save would store that.
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const editor = new Editor({ extensions: makeExtensions("plaintext"), content: DOC });
    editors.push(editor);
    expect(JSON.stringify(editor.getJSON())).not.toContain("inside the box");
  });

  it("an unknown node shows a box with its type name", () => {
    const editor = open(DOC);
    const boxes = [...editor.view.dom.querySelectorAll("[data-kb-unknown]")].map((b) => `${b.tagName.toLowerCase()}:${b.textContent}`);
    expect(boxes).toEqual(["div:probeBox", "span:probeMention", "div:probeBox"]);
    expect(editor.view.dom.textContent).not.toContain("inside the box"); // carried, not shown as editable text
  });

  it("editing text around an unknown node keeps the node", () => {
    const editor = open(DOC);
    editor.commands.insertContentAt(1, "Typed ");

    const saved = out(editor);
    expect(saved.content?.[0]).toEqual({ type: "paragraph", content: [{ type: "text", text: "Typed Before" }] });
    expect(saved.content?.slice(1, 4)).toEqual(DOC.content?.slice(1)); // after an edit the editor itself ends a page that ends in a list with an empty paragraph
  });

  it("where a box cannot stand, the nearest part that can be carried is carried whole", () => {
    // A list item must start with a paragraph and a code block holds only text: a box fits in neither place.
    const doc: JSONContent = {
      type: "doc",
      content: [
        { type: "bulletList", content: [{ type: "listItem", content: [{ type: "probeBox", attrs: { label: "First in an item" } }] }] },
        { type: "codeBlock", attrs: { language: "plaintext" }, content: [{ type: "text", text: "x = ", marks: [{ type: "probeHighlight" }] }] },
        { type: "paragraph", content: [{ type: "text", text: "Kept editable" }] },
      ],
    };
    const editor = open(doc);

    expect(out(editor)).toEqual(doc);
    expect([...editor.view.dom.querySelectorAll("[data-kb-unknown]")].map((b) => b.textContent)).toEqual(["bulletList", "codeBlock"]);
  });

  it("two unknown marks on one text both survive", () => {
    const doc: JSONContent = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "both", marks: [{ type: "probeHighlight", attrs: { color: "red" } }, { type: "probeNote", attrs: { id: 7 } }] }] }] };
    expect(out(open(doc))).toEqual(doc);
  });

  it("a carrier that holds nothing (pasted from elsewhere) is left out of what is saved", () => {
    const editor = open({ type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Kept" }] }] });
    editor.commands.insertContentAt(editor.state.doc.content.size, '<div data-kb-unknown></div><p>Tail <span data-kb-unknown></span><span data-kb-unknown-mark>marked</span></p>');

    expect(editor.view.dom.querySelectorAll("[data-kb-unknown]")).toHaveLength(2);
    expect(out(editor).content).toEqual([
      { type: "paragraph", content: [{ type: "text", text: "Kept" }] },
      { type: "paragraph", content: [{ type: "text", text: "Tail " }, { type: "text", text: "marked" }] },
    ]);
  });

  it("a plugin node is editable when its extension is loaded", () => {
    editorExtensions.add("kb.test.probe", Node.create({
      name: "probeBox", group: "block", content: "block*",
      addAttributes: () => ({ label: { default: "" }, extra: { default: null } }),
      parseHTML: () => [{ tag: "div[data-probe-box]" }],
      renderHTML: () => ["div", { "data-probe-box": "" }, 0],
    }));
    // The editor writes every attribute of a node it knows, so the second box gets the one it lacked.
    const doc = structuredClone(DOC);
    doc.content![3].content![0].content![1].attrs = { label: "In a list", extra: null };
    const editor = open(doc);

    // The box is the plugin's own node now, and the text inside it can be edited. What no extension knows is still carried.
    expect(editor.view.dom.querySelector("[data-probe-box]")?.textContent).toBe("inside the box");
    expect([...editor.view.dom.querySelectorAll("[data-kb-unknown]")].map((b) => b.textContent)).toEqual(["probeMention"]);
    expect(out(editor)).toEqual(doc);
  });

  it("a plugin extension that breaks the editor, or takes a name of the editor's own, is left out and reported", () => {
    const calls: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => { calls.push(JSON.parse(String(init?.body))); return new Response(null, { status: 204 }); }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    editorExtensions.add("kb.bad", Node.create({ name: "paragraph", group: "block", content: "text*" }));
    editorExtensions.add("kb.bad", Node.create({ name: "badBox", group: "block", content: "noSuchNode+" }));
    editorExtensions.add("kb.good", Node.create({ name: "goodBox", group: "block", content: "block*", renderHTML: () => ["div", 0] }));

    const names = makeExtensions("plaintext").map((e) => e.name);

    expect(names).toContain("goodBox");
    expect(names).not.toContain("badBox");
    expect(names.filter((n) => n === "paragraph")).toHaveLength(0); // the editor's own paragraph comes with the starter kit, not by this name in the list
    expect(calls).toEqual([
      { plugin: "kb.bad", where: "editor", message: "paragraph is a part of the editor." },
      expect.objectContaining({ plugin: "kb.bad", where: "editor" }),
    ]);
    expect(out(open(DOC))).toEqual(DOC);
  });
});
