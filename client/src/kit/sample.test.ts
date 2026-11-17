import { afterEach, describe, expect, it, vi } from "vitest";
import { Editor, type AnyExtension } from "@tiptap/react";
import { editorExtensions, makeExtensions, toEditor } from "../editor/extensions";
import type { KbHost } from "./host";
import { slots, type ActionItem } from "./slots";

// The sample plugin's script as it is shipped (plugins/Kb.Plugin.Sample/ui/index.js; "kb-sample-ui" in vitest.config.ts).
// The test runner serves "/shared/v1/..." from the app's own packages, as the app does in the browser.

afterEach(() => { slots.clear(); editorExtensions.clear(); vi.unstubAllGlobals(); document.head.querySelectorAll("link").forEach((l) => l.remove()); });

async function start() {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ count: 0 }), { status: 200 })));
  const module = await import("kb-sample-ui");
  const kb = {
    plugin: { id: "kb.sample", version: "0.0.0" },
    slots: { add: (slot: string, item: ActionItem) => slots.add(slot, item, "kb.sample") },
    editor: { extend: (...extensions: AnyExtension[]) => editorExtensions.add("kb.sample", ...extensions) },
    api: { get: async () => ({ count: 0 }), post: async () => ({}) },
  } as unknown as KbHost;
  module.default(kb);
}

describe("the sample plugin's script", () => {
  it("adds its page parts, its editor block, its toolbar entry and its stylesheet", async () => {
    await start();

    expect(slots.names()).toEqual(["editor.toolbar", "page.panel", "page.tools", "plugin.settings"]);
    expect(makeExtensions("plaintext").map((e) => e.name)).toContain("sampleNotice");
    expect(document.head.querySelector("link[rel=stylesheet]")?.getAttribute("href")).toMatch(/\/ui\/notice\.css$/);
  });

  it("the notice block stays a notice when it is pasted, and the toolbar entry turns a line into one and back", async () => {
    await start();
    const extensions = makeExtensions("plaintext");
    const editor = new Editor({ extensions, content: toEditor('{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Line"}]}]}', extensions) });
    // The editor itself ends a page that ends in another block with an empty paragraph; the first two blocks are what is tested.
    const types = () => editor.getJSON().content?.slice(0, 2).map((n) => n.type);
    try {
      // What the clipboard holds after a copy is the block's own HTML.
      editor.commands.insertContentAt(editor.state.doc.content.size, '<p data-sample-notice class="sample-notice">Acknowledge by Friday</p>');
      expect(types()).toEqual(["paragraph", "sampleNotice"]);

      const entry = slots.list<ActionItem<{ editor: Editor }>>("editor.toolbar", { editor })[0];
      editor.commands.setTextSelection(2);
      entry.onClick?.({ editor });
      expect(types()).toEqual(["sampleNotice", "sampleNotice"]);
      entry.onClick?.({ editor });
      expect(types()).toEqual(["paragraph", "sampleNotice"]);
    } finally { editor.destroy(); }
  });
});
