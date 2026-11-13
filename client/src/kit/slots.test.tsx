import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { slots, useSlot, type ActionItem, type SectionItem } from "./slots";
import { SlotSections, SlotTools } from "./SlotViews";

afterEach(() => { cleanup(); slots.clear(); vi.unstubAllGlobals(); });

const ids = (slot: string, ctx: unknown = {}) => slots.list<ActionItem>(slot, ctx).map((i) => i.id);

describe("slots", () => {
  it("gives core and plugin items in order", () => {
    slots.add("page.tools", { id: "edit", order: 100, label: "Edit" });
    slots.add("page.tools", { id: "export", order: 300, label: "Export" });
    slots.add("page.tools", { id: "ack", order: 200, label: "Acknowledge" }, "kb.sample");
    slots.add("page.tools", { id: "late", label: "Late" }, "kb.sample");   // no order: after everything that has one
    slots.add("page.tools", { id: "later", label: "Later" }, "kb.other");  // and in the order they were added
    expect(ids("page.tools")).toEqual(["edit", "ack", "export", "late", "later"]);
    expect(slots.names()).toEqual(["page.tools"]);
  });

  it("hide removes an item, including a core one; move reorders", () => {
    slots.add("s", { id: "a", order: 100, label: "A" });
    slots.add("s", { id: "b", order: 200, label: "B" });
    slots.add("s", { id: "c", order: 300, label: "C" }, "kb.sample");
    slots.hide("s", "a");
    slots.move("s", "c", 150);
    expect(ids("s")).toEqual(["c", "b"]);
  });

  it("when filters by context, and an item whose when throws is left out", () => {
    slots.add("s", { id: "always", label: "Always" });
    slots.add("s", { id: "editors", label: "Editors", when: (c: { canEdit: boolean }) => c.canEdit });
    slots.add("s", { id: "broken", label: "Broken", when: () => { throw new Error("no"); } }, "kb.sample");
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));
    vi.spyOn(console, "error").mockImplementation(() => {});
    expect(ids("s", { canEdit: false })).toEqual(["always"]);
    expect(ids("s", { canEdit: true })).toEqual(["always", "editors"]);
  });

  it("a when that throws is reported once, however often the slot is drawn", async () => {
    const calls: unknown[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => { calls.push(JSON.parse(String(init?.body))); return new Response(null, { status: 204 }); }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    slots.add("s", { id: "broken", label: "Broken", when: () => { throw new Error("no"); } }, "kb.sample");

    for (let i = 0; i < 20; i++) expect(ids("s")).toEqual([]);

    expect(calls).toEqual([{ plugin: "kb.sample", where: "s", message: "no" }]);
  });

  it("hide holds for an item that is added later", () => {
    slots.hide("s", "late");
    slots.add("s", { id: "late", label: "Late" }, "kb.other");
    slots.add("s", { id: "kept", label: "Kept" }, "kb.other");
    expect(ids("s")).toEqual(["kept"]);
  });

  it("adding an item again under the same id replaces it", () => {
    slots.add("s", { id: "a", label: "First" }, "kb.sample");
    slots.add("s", { id: "a", label: "Second" }, "kb.sample");
    expect(slots.list<ActionItem>("s", {}).map((i) => i.label)).toEqual(["Second"]);
  });

  it("useSlot re-renders when an item is added", () => {
    function Count() { return <p>{useSlot("s", {}).length} items</p>; }
    render(<Count />);
    expect(screen.getByText("0 items")).toBeTruthy();
    act(() => slots.add("s", { id: "a", label: "A" }));
    expect(screen.getByText("1 items")).toBeTruthy();
  });
});

describe("SlotTools and SlotSections", () => {
  it("SlotTools draws a button per item and hands it the context", async () => {
    const clicked = vi.fn();
    slots.add("page.tools", { id: "ack", label: (c: { title: string }) => `Acknowledge ${c.title}`, onClick: clicked } satisfies ActionItem<{ title: string }>, "kb.sample");
    render(<SlotTools slot="page.tools" ctx={{ title: "Reset" }} />);
    await userEvent.click(screen.getByRole("button", { name: "Acknowledge Reset" }));
    expect(clicked).toHaveBeenCalledWith({ title: "Reset" });
  });

  it("a plugin item that throws is left out and reported; the others render", async () => {
    const calls: { url: string; body: unknown }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => { calls.push({ url, body: JSON.parse(String(init?.body)) }); return new Response(null, { status: 204 }); }));
    vi.spyOn(console, "error").mockImplementation(() => {});
    slots.add("page.panel", { id: "good", title: "Acknowledged", render: () => <p>3 people</p> } satisfies SectionItem, "kb.sample");
    slots.add("page.panel", { id: "bad", title: "Broken", render: () => { throw new Error("It threw."); } } satisfies SectionItem, "kb.broken");
    slots.add("page.panel", { id: "core", title: "Core", render: () => <p>core part</p> } satisfies SectionItem);

    render(<SlotSections slot="page.panel" ctx={{}} />);

    expect(screen.getByText("3 people")).toBeTruthy();
    expect(screen.getByText("core part")).toBeTruthy();
    expect(screen.queryByText("Broken")).toBeNull();
    await vi.waitFor(() => expect(calls).toHaveLength(1));
    expect(calls[0]).toEqual({ url: "/api/v1/ui/errors", body: { plugin: "kb.broken", where: "page.panel", message: "It threw." } });
  });
});
