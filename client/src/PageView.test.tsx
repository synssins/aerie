import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Node, type Editor } from "@tiptap/react";
import { DialogProvider } from "./Dialogs";
import type { Page } from "./api";
import { editorExtensions } from "./editor/extensions";
import { slots, type ActionItem } from "./kit/slots";
import { PageView } from "./PageView";

const BOX = { type: "probeBox", attrs: { label: "Warning" }, content: [{ type: "paragraph", content: [{ type: "text", text: "inside the box" }] }] };
const DOC = { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Before" }] }, BOX] };

const page = (p: Partial<Page> = {}): Page => ({
  id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "Boxes", contentJson: JSON.stringify(DOC), version: "1.0",
  updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat", path: [], history: [], canEdit: true, tags: [], canDelete: true, draft: null, attachments: [], maxAttachmentBytes: 1000, ...p,
});

type Call = { key: string; body: { contentJson?: string } };
function server(): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
    calls.push({ key: `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`, body });
    return new Response(JSON.stringify(init?.method === "PUT" && !url.endsWith("/draft") ? page({ version: "1.1", contentJson: body.contentJson }) : { updatedAt: "2026-11-12T10:00:05Z" }), { status: 200 });
  }));
  return calls;
}

function show(p: Page, autoEdit = false) {
  const onSaved = vi.fn();
  render(<DialogProvider><PageView page={p} spaceName="Docs" role="Owner" autoEdit={autoEdit} onSaved={onSaved} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
  return onSaved;
}

afterEach(() => { cleanup(); slots.clear(); editorExtensions.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe("PageView and content of a plugin that is not loaded", () => {
  it("shows a box with the node's type, and the rest of the page", async () => {
    server();
    show(page());

    expect(await screen.findByText("Before")).toBeTruthy();
    expect(document.querySelector(".prose [data-kb-unknown]")?.textContent).toBe("probeBox");
  });

  it("a save sends the node back as it was stored", async () => {
    const calls = server();
    const onSaved = show(page(), true);
    await screen.findByText("Before");

    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    const sent = JSON.parse(calls.find((c) => c.key === "PUT /pages/p1")!.body.contentJson!) as typeof DOC;
    expect(sent.content[1]).toEqual(BOX);
    expect(sent.content[0]).toEqual(DOC.content[0]);
  });

  it("when its plugin's extension arrives, the page that is not being edited shows the plugin's node", async () => {
    server();
    show(page());
    await screen.findByText("Before");
    expect(document.querySelector("[data-probe-box]")).toBeNull();

    act(() => editorExtensions.add("kb.test.probe", Node.create({
      name: "probeBox", group: "block", content: "block*", addAttributes: () => ({ label: { default: "" } }),
      parseHTML: () => [{ tag: "div[data-probe-box]" }], renderHTML: () => ["div", { "data-probe-box": "" }, 0],
    })));

    await waitFor(() => expect(document.querySelector("[data-probe-box]")?.textContent).toBe("inside the box"));
    expect(document.querySelector("[data-kb-unknown]")).toBeNull();
  });

  it("a stored page of an odd shape opens instead of taking the app down", async () => {
    server();
    show(page({ contentJson: '{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Before"}]},null,{"type":"paragraph","content":"oops"}]}' }));

    expect(await screen.findByText("Before")).toBeTruthy();
    expect(document.querySelectorAll(".prose [data-kb-unknown]")).toHaveLength(2);
  });
});

describe("PageView and a draft", () => {
  it("offers Resume and Discard only to someone who may edit the page", async () => {
    server();
    show(page({ draft: { mine: true, by: "cara", updatedAt: "2026-11-12T10:00:00Z" } }));
    expect(await screen.findByText(/Unsaved draft/)).toBeTruthy();
    cleanup();

    // Her role was lowered to Reader after she wrote the draft: the server would refuse both, so neither is offered.
    show(page({ canEdit: false, draft: { mine: true, by: "cara", updatedAt: "2026-11-12T10:00:00Z" } }));
    await screen.findByText("Before");
    expect(screen.queryByText(/Unsaved draft/)).toBeNull();
  });

  it("says again that an edit is open when the same page is shown anew, so leaving still asks", async () => {
    server();
    const onDirty = vi.fn();
    const props = { spaceName: "Docs", role: "Owner", autoEdit: true, onSaved: () => {}, onOpen: () => {}, onHome: () => {}, onDirty, onHistory: () => {}, onMove: () => {}, onDelete: () => {} };
    const { rerender } = render(<DialogProvider><PageView page={page()} {...props} /></DialogProvider>);
    await screen.findByText("Before");
    onDirty.mockClear();

    // The shell answered a "Discard?" with yes and switched its guard off, but the address led to the same page: the editor stays open.
    rerender(<DialogProvider><PageView page={page()} {...props} /></DialogProvider>);

    expect(onDirty).toHaveBeenLastCalledWith(true);
  });
});

describe("the editor's toolbar", () => {
  it("an entry whose label throws is left out and reported; the page and the other entries stay", async () => {
    const calls = server();
    vi.spyOn(console, "error").mockImplementation(() => {});
    slots.add("editor.toolbar", { id: "bad", label: () => { throw new Error("No label."); } } satisfies ActionItem, "kb.bad");
    slots.add("editor.toolbar", { id: "good", label: "Good" } satisfies ActionItem, "kb.good");
    show(page(), true);

    expect(await screen.findByText("Before")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Good" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Bold" })).toBeTruthy();
    await waitFor(() => expect(calls.some((c) => c.key === "POST /ui/errors")).toBe(true));
  });

  it("draws the entries of the editor.toolbar slot and hands them the editor", async () => {
    server();
    let got: Editor | null = null;
    slots.add("editor.toolbar", { id: "box", label: "Box", onClick: (c: { editor: Editor }) => { got = c.editor; } } satisfies ActionItem<{ editor: Editor }>, "kb.test.probe");
    show(page(), true);
    await screen.findByText("Before");

    await userEvent.click(screen.getByRole("button", { name: "Box" }));

    expect(got).not.toBeNull();
    expect(got!.isEditable).toBe(true);
    // The core's own entries are still there, before the plugin's.
    const names = [...document.querySelectorAll(".editor-toolbar button")].map((b) => b.getAttribute("aria-label") ?? b.textContent);
    expect(names.indexOf("Bold")).toBeLessThan(names.indexOf("Box"));
  });
});
