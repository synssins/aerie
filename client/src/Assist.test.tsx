import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AssistDialog, combine, sections, type AssistResult } from "./Assist";
import { DialogProvider } from "./Dialogs";
import { PageView } from "./PageView";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };
window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;

const doc = (...b: [string, string][]) => JSON.stringify({ type: "doc", content: b.map(([t, x]) => (t === "h"
  ? { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: x }] }
  : { type: "paragraph", content: [{ type: "text", text: x }] })) });

function server(answer: (body: unknown) => unknown) {
  const calls: { key: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    return new Response(JSON.stringify(answer(body) ?? {}), { status: 200 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("AI assist", () => {
  const current = doc(["p", "Intro"], ["h", "Steps"], ["p", "Old step"], ["h", "Notes"], ["p", "Note"]);
  const proposed = doc(["p", "Intro"], ["h", "Steps"], ["p", "New step"], ["h", "Notes"], ["p", "Note, updated"], ["h", "Rollback"], ["p", "Undo it"]);

  it("takes some sections: one not taken is the page's own, a new one not taken is left out", () => {
    expect(sections(proposed).map((s) => s.heading)).toEqual(["", "Steps", "Notes", "Rollback"]);
    const json = JSON.parse(combine(current, proposed, new Set([0, 1]))) as { content: { content?: { text: string }[] }[] };
    expect(json.content.map((n) => n.content?.[0].text)).toEqual(["Intro", "Steps", "New step", "Notes", "Note"]);
  });

  it("sends the pasted material with the page, previews the result, and applies only the sections and suggestions kept", async () => {
    const result: AssistResult = {
      title: "Restart", contentJson: proposed, diff: [{ op: "Add", text: "New step" }], removed: [{ units: [3], text: "Note", sameAs: "Note" }],
      changed: [{ from: "Old step", to: "New step" }], invented: ["zebra"], warnings: ["Words in neither the page nor the pasted material: zebra."], tags: ["vpn"],
      appliesTo: [{ applicationId: "a1", name: "Acme ERP", servers: [] }],
    };
    const calls = server(() => result);
    const onApply = vi.fn();
    render(<DialogProvider><AssistDialog spaceId="s1" pageId="p1" title="Restart" contentJson={current} onApply={onApply} onClose={() => {}} /></DialogProvider>);
    await userEvent.type(screen.getByLabelText("Pasted material"), "New step");
    await userEvent.upload(screen.getByLabelText("Files"), new File(["x"], "shot.png", { type: "image/png" }));
    await userEvent.click(screen.getByRole("button", { name: "Build page" }));
    await waitFor(() => expect(calls[0]?.key).toBe("POST /spaces/s1/assist"));
    expect(calls[0].body).toMatchObject({ pageId: "p1", title: "Restart", contentJson: current, text: "New step", files: [{ name: "shot.png" }] });

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByRole("heading", { name: "Rollback" })).toBeTruthy(); // the page as it would read
    expect(within(dialog).getByText(/zebra/)).toBeTruthy();
    expect(within(dialog).getByText(/Removed duplicate:/)).toBeTruthy();
    expect(within(dialog).getByText(/Changed:/)).toBeTruthy();
    await userEvent.click(within(dialog).getByLabelText("Notes"));
    await userEvent.click(within(dialog).getByLabelText("Rollback"));
    await userEvent.click(within(dialog).getByLabelText("Applies to Acme ERP"));
    await userEvent.click(within(dialog).getByRole("button", { name: "Accept selected sections" }));
    const applied = onApply.mock.calls[0][0] as { contentJson: string; tags: string[]; appliesTo: unknown[] };
    expect(JSON.parse(applied.contentJson).content.map((n: { content?: { text: string }[] }) => n.content?.[0].text)).toEqual(["Intro", "Steps", "New step", "Notes", "Note"]);
    expect(applied.tags).toEqual(["vpn"]);
    expect(applied.appliesTo).toEqual([]);
  });

  it("with nothing pasted, the button organizes the page", async () => {
    server(() => ({}));
    render(<DialogProvider><AssistDialog spaceId="s1" pageId="p1" title="Restart" contentJson={current} onApply={() => {}} onClose={() => {}} /></DialogProvider>);
    expect(screen.getByRole("button", { name: "Organize page" })).toBeTruthy();
    await userEvent.type(screen.getByLabelText("Pasted material"), "x");
    expect(screen.getByRole("button", { name: "Build page" })).toBeTruthy();
  });

  it("a flagged page says so, and an Owner can mark it reviewed", async () => {
    const calls = server(() => ({ aiFlag: null }));
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "Notes", contentJson: doc(["p", "x"]), version: "1.0", updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat",
      path: [], history: [], canEdit: true, tags: [], canDelete: true, draft: null, attachments: [], maxAttachmentBytes: 1000, aiFlag: "\"ignore all previous instructions\" in an HTML comment" };
    render(<DialogProvider><PageView page={page as never} spaceName="Ops" role="Owner" onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    expect(screen.getByText(/holds text addressed to an AI/).textContent).toContain("in an HTML comment");
    await userEvent.click(screen.getByRole("button", { name: "Mark reviewed" }));
    await waitFor(() => expect(calls.some((c) => c.key === "POST /pages/p1/ai-review")).toBe(true));
    await waitFor(() => expect(screen.queryByText(/holds text addressed to an AI/)).toBeNull());
  });
});
