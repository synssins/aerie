import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { Node as TiptapNode } from "@tiptap/react";
import userEvent from "@testing-library/user-event";
import type { ImportItem } from "./api";
import { DialogProvider } from "./Dialogs";
import { editorExtensions } from "./editor/extensions";
import { folderPaths, ImportView, mergeDrafts, resolveFolder, splitAtHeadings, statusText } from "./Import";
import { PageView } from "./PageView";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const spaces = [{ id: "s1", name: "IAM", isSensitive: false, reviewMonths: 12 }, { id: "s2", name: "Vault", isSensitive: true, reviewMonths: null }];
const doc = (...texts: [string, string][]) => JSON.stringify({ type: "doc", content: texts.map(([t, x]) => t === "h" ? { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: x }] } : { type: "paragraph", content: [{ type: "text", text: x }] }) });

type Call = { key: string; body: unknown; headers?: Record<string, string> };
function server(routes: Record<string, (body: unknown) => unknown>) {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : init?.body;
    calls.push({ key, body, headers: init?.headers as Record<string, string> | undefined });
    const match = Object.keys(routes).find((k) => key === k || (k.endsWith("*") && key.startsWith(k.slice(0, -1))));
    return match ? new Response(JSON.stringify(routes[match](body) ?? null), { status: 200 }) : new Response(null, { status: 404 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); editorExtensions.clear(); });

const row = (o: Record<string, unknown>) => ({ batchId: "b1", error: null, files: 1, createdAt: "2026-11-14T10:00:00Z", pageId: null, source: null, suggestedSpace: null, updatedAt: null, draftMs: null, ...o });

describe("import inbox", () => {
  it("queues dropped or browsed files, lists them before the start, then uploads each with its path", async () => {
    const list = { batches: [], items: [row({ id: "i1", title: "Reset MFA", status: "Ready" })], canImport: true, canFormat: true, spaces };
    const calls = server({ "GET /imports": () => list, "POST /imports": () => ({ id: "b1" }), "POST /imports/b1/files": () => ({}), "POST /imports/b1/start": () => ({}) });
    render(<DialogProvider><ImportView itemId={null} onOpenItem={() => {}} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    expect(await screen.findByRole("region", { name: "Drop files here" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "browse" })).toBeTruthy();
    await userEvent.upload(screen.getByLabelText("Files"), [new File(["# Reset"], "reset.md"), new File(["x"], "shot.png")]);
    const queue = screen.getByRole("region", { name: "Files to import" });
    expect(within(queue).getByText("reset.md")).toBeTruthy();
    expect(calls.some((c) => c.key === "POST /imports")).toBe(false); // nothing is sent before Start
    await userEvent.click(within(queue).getByRole("button", { name: "Remove shot.png" }));
    await userEvent.selectOptions(within(queue).getByLabelText("Destination"), "IAM");
    await userEvent.click(within(queue).getByRole("button", { name: "Start import" }));
    await waitFor(() => expect(calls.map((c) => c.key)).toContain("POST /imports/b1/start"));
    expect(calls.find((c) => c.key === "POST /imports")?.body).toEqual({ spaceId: "s1" });
    expect(calls.filter((c) => c.key === "POST /imports/b1/files").map((c) => c.headers?.["X-File-Name"])).toEqual(["reset.md"]);
  });

  it("shows each draft as one line with its facts, as a link, and says why a box cannot be ticked", async () => {
    const list = {
      batches: [], canImport: true, canFormat: true, spaces,
      items: [
        row({ id: "i1", title: "Reset MFA", status: "Ready", source: "reset.md +1", suggestedSpace: "IAM", draftMs: 14000 }),
        row({ id: "i2", title: "incident-report", status: "Blocked", error: "It holds text addressed to the AI, so it was not sent.", source: "incident-report.docx" }),
      ],
    };
    server({ "GET /imports": () => list });
    const onOpenItem = vi.fn();
    render(<DialogProvider><ImportView itemId={null} onOpenItem={onOpenItem} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    const link = await screen.findByRole("link", { name: "Reset MFA" });
    expect(link.getAttribute("href")).toBe("/import/i1");
    expect(screen.getByText("reset.md +1")).toBeTruthy();
    expect(screen.getByText("Ready to review · drafted in 14 s")).toBeTruthy();
    expect(screen.getByText("→ IAM")).toBeTruthy();
    const blocked = screen.getByLabelText("Select incident-report") as HTMLInputElement;
    expect(blocked.disabled).toBe(true);
    expect(blocked.title).toBe("Can't be merged: Blocked: It holds text addressed to the AI, so it was not sent.");
    expect(screen.getByText("Blocked: It holds text addressed to the AI, so it was not sent.")).toBeTruthy();
    await userEvent.click(link);
    expect(onOpenItem).toHaveBeenCalledWith("i1");
  });

  it("says how long a draft has been drafting", () => {
    expect(statusText({ status: "Working", error: null, updatedAt: "2026-11-14T10:00:00Z", draftMs: null }, Date.parse("2026-11-14T10:00:42Z"))).toBe("Drafting · 42 s");
    expect(statusText({ status: "Failed", error: "The model did not answer within 120 seconds.", updatedAt: null, draftMs: null })).toBe("Failed: The model did not answer within 120 seconds.");
  });
});

describe("import review", () => {
  const item: ImportItem = {
    id: "i1", title: "Reset MFA", status: "Ready", error: null, pageId: null, spaces,
    files: [{ id: "f1", name: "reset.md", path: "reset.md", size: 10, kind: "Text", note: null, embedded: false, sha256: "a" }],
    sources: { f1: "# Reset MFA\npassword: Hunter2" },
    proposal: {
      pages: [{ title: "Reset MFA", contentJson: doc(["p", "password: Hunter2"]), redactedJson: doc(["p", "password: [REDACTED]"]) }],
      tags: ["mfa"], appliesTo: [{ applicationId: "a1", name: "Acme ERP", servers: ["ERP01"] }], owner: { type: "Group", id: "local:g1", name: "ERP Support Team" }, reviewMonths: 6,
      destination: null, suggestedDestination: { spaceId: "s1", parentId: null }, sensitive: [{ kind: "Password", sample: "Hu•••r2" }],
      duplicates: [{ pageId: "p9", title: "Reset MFA (old)", score: 0.6, spaceId: "s1" }], warnings: ["The draft uses words that are not in the source (vpn). Check it says nothing new."], model: "stub", ms: 9000,
      found: { people: ["Lena Kowalski"], servers: ["ERP01"], applications: [], tickets: ["CHG0041234"], versions: ["4.2"], conflicts: ["4.1 and 4.2 are both named."] },
    },
  };

  it("in order: summary, placement filled with the writable spaces, details, source above the proposed page; originals not attached", async () => {
    const calls = server({ "GET /imports/items/i1": () => item, "GET /spaces/s1/tree": () => [{ id: "f9", kind: "Folder", title: "Runbooks", parentId: null }, { id: "f8", kind: "Folder", title: "DR", parentId: "f9" }], "POST /imports/items/i1/accept": () => ["p1"] });
    const onOpenPage = vi.fn();
    render(<DialogProvider><ImportView itemId="i1" onOpenItem={() => {}} onOpenPage={onOpenPage} onHome={() => {}} /></DialogProvider>);
    const summary = await screen.findByRole("region", { name: "Summary" });
    expect(within(summary).getByText("Lena Kowalski")).toBeTruthy();
    expect(within(summary).getByText("CHG0041234")).toBeTruthy();
    expect(within(summary).getByText(/4.1 and 4.2 are both named/)).toBeTruthy();
    const order = ["Summary", "Placement and details", "Source", "Proposed pages"].map((n) => screen.getByRole("region", { name: n }));
    for (let k = 1; k < order.length; k++) expect(order[k - 1].compareDocumentPosition(order[k]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual(expect.arrayContaining(["IAM", "Vault (sensitive)"]));
    expect((screen.getByLabelText("Attach to the page") as HTMLInputElement).checked).toBe(false);
    expect(await screen.findByText(/\[REDACTED\]/)).toBeTruthy();
    expect(screen.getByText(/password: Hunter2/, { selector: "pre" })).toBeTruthy(); // the source, unredacted
    // Picking the suggested space takes its review cycle (12) over the model's (6).
    await userEvent.click(screen.getByRole("button", { name: "Suggested: IAM" }));
    expect((screen.getByLabelText("Review cycle") as HTMLSelectElement).value).toBe("12");
    expect(screen.getByText("The draft uses words that are not in the source (vpn). Confirm the document is correct before saving.")).toBeTruthy();
    const folderBox = screen.getByLabelText("Folder") as HTMLInputElement;
    expect(folderBox.value).toBe("Top level");
    await waitFor(() => expect([...document.querySelectorAll("#place-folders option")].map((o) => (o as HTMLOptionElement).value)).toEqual(["", "Top level", "Runbooks", "Runbooks / DR"]));
    await userEvent.clear(folderBox);
    await userEvent.type(folderBox, "Runbooks / MFA");
    expect(screen.getByText("New folder")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(onOpenPage).toHaveBeenCalledWith("p1"));
    const body = calls.find((c) => c.key === "POST /imports/items/i1/accept")!.body as Record<string, unknown>;
    expect(body).toMatchObject({ spaceId: "s1", parentId: "f9", newFolder: "MFA", updatePageId: null, attachOriginals: false, details: { reviewMonths: 12, ownerType: "Group", appliesTo: [{ applicationId: "a1" }] } });
    expect(JSON.stringify(body.pages)).not.toContain("Hunter2");
  });

  it("a plugin's block in a draft shows as the plugin's once its plugin adds it, and not while someone is in the editor", async () => {
    const box = { type: "probeBox", attrs: { label: "" }, content: [{ type: "paragraph", content: [{ type: "text", text: "inside the box" }] }] };
    const json = JSON.stringify({ type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Before" }] }, box] });
    const calls = server({ "GET /imports/items/i1": () => ({ ...item, proposal: { ...item.proposal!, pages: [{ title: "Reset MFA", contentJson: json, redactedJson: json }] } }), "GET /spaces/s1/tree": () => [], "POST /imports/items/i1/accept": () => ["p1"] });
    render(<DialogProvider><ImportView itemId="i1" onOpenItem={() => {}} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    await waitFor(() => expect(document.querySelector(".draft-editor [data-kb-unknown]")?.textContent).toBe("probeBox"));
    const editor = document.querySelector(".draft-editor .ProseMirror") as HTMLElement;
    act(() => editor.focus());
    act(() => editorExtensions.add("kb.test.probe", TiptapNode.create({
      name: "probeBox", group: "block", content: "block*", addAttributes: () => ({ label: { default: "" } }),
      parseHTML: () => [{ tag: "div[data-probe-box]" }], renderHTML: () => ["div", { "data-probe-box": "" }, 0],
    })));
    expect(document.querySelector(".draft-editor [data-probe-box]")).toBeNull(); // not under someone's hands
    act(() => editor.blur());
    await waitFor(() => expect(document.querySelector(".draft-editor [data-probe-box]")?.textContent).toBe("inside the box"));
    expect(document.querySelector(".draft-editor [data-kb-unknown]")).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Suggested: IAM" }));
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(calls.some((c) => c.key === "POST /imports/items/i1/accept")).toBe(true));
    expect(JSON.stringify((calls.find((c) => c.key === "POST /imports/items/i1/accept")!.body as { pages: unknown }).pages)).toContain("probeBox");
  });

  it("Reject keeps the draft for Draft again; Discard asks first, with Cancel as the default", async () => {
    let status = "Ready";
    const calls = server({ "GET /imports/items/i1": () => ({ ...item, status }), "POST /imports/items/i1/reject": () => { status = "Rejected"; return null; }, "DELETE /imports/items/i1": () => null });
    const onOpenItem = vi.fn();
    render(<DialogProvider><ImportView itemId="i1" onOpenItem={onOpenItem} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "Reject" }));
    expect(await screen.findByRole("button", { name: "Draft again" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/deleted permanently/)).toBeTruthy();
    expect(document.activeElement?.textContent).toBe("Cancel");
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(calls.some((c) => c.key === "DELETE /imports/items/i1")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(calls.some((c) => c.key === "DELETE /imports/items/i1")).toBe(true));
    expect(onOpenItem).toHaveBeenCalledWith(null);
  });

  it("the Folder box: an existing folder by its path, the top level, or a new folder under the folder its path starts with", () => {
    const paths = folderPaths([{ id: "a", kind: "Folder", title: "Runbooks", parentId: null }, { id: "b", kind: "Folder", title: "DR", parentId: "a" }, { id: "p", kind: "Page", title: "x", parentId: null }] as never);
    expect(paths).toEqual([{ id: "a", path: "Runbooks" }, { id: "b", path: "Runbooks / DR" }]);
    expect(resolveFolder("Runbooks / DR", paths)).toEqual({ parentId: "b", newFolder: null });
    expect(resolveFolder("Top level", paths)).toEqual({ parentId: null, newFolder: null });
    expect(resolveFolder("", paths)).toEqual({ parentId: null, newFolder: null });
    expect(resolveFolder("Runbooks / DR / Exchange", paths)).toEqual({ parentId: "b", newFolder: "Exchange" });
    expect(resolveFolder("Network", paths)).toEqual({ parentId: null, newFolder: "Network" });
  });

  it("splits a page at its headings and merges pages back", () => {
    let k = 0;
    const parts = splitAtHeadings({ key: 0, title: "All", json: doc(["h", "Part one"], ["p", "a"], ["h", "Part two"], ["p", "b"]) }, () => ++k);
    expect(parts.map((p) => p.title)).toEqual(["Part one", "Part two"]);
    const back = mergeDrafts(parts, 9);
    expect(JSON.parse(back.json).content).toHaveLength(4);
  });
});

describe("format", () => {
  it("shows the proposed page as a page, the changed lines on request, each duplicate removed with Keep, and changes the editor only when accepted", async () => {
    window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;
    const formatted = doc(["h", "Restart steps"], ["p", "Stop then start"]);
    const withCopy = doc(["h", "Restart steps"], ["p", "Stop then start"], ["p", "Stop then start."]);
    const roles = [{ n: 0, role: "heading2" }, { n: 1, role: "keep" }, { n: 2, role: "duplicate" }];
    const calls = server({
      "POST /pages/p1/format": (body) => (body as { keep?: number[] }).keep?.includes(2)
        ? { contentJson: withCopy, diff: [], removed: [], roles }
        : { contentJson: formatted, diff: [{ op: "Del", text: "Restart steps" }, { op: "Add", text: "## Restart steps" }, { op: "Del", text: "Stop then start." }], removed: [{ units: [2], text: "Stop then start.", sameAs: "Stop then start" }], roles },
    });
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "Restart", contentJson: doc(["p", "Restart steps"], ["p", "Stop then start"], ["p", "Stop then start."]), version: "1.0",
      updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat", path: [], history: [], canEdit: true, tags: [], canDelete: true, draft: null, attachments: [], maxAttachmentBytes: 1000 };
    render(<DialogProvider><PageView page={page as never} spaceName="IAM" role="Owner" autoEdit canFormat onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "Format" }));
    const dialog = await screen.findByRole("dialog");
    // The page as it would read: a heading, not "## Restart steps".
    expect(await within(dialog).findByRole("heading", { level: 2, name: "Restart steps" })).toBeTruthy();
    expect(within(dialog).queryByText("+ ## Restart steps")).toBeNull();
    expect(within(dialog).getByText(/Removed duplicate:/).parentElement!.textContent).toContain("Stop then start. · same as Stop then start");
    await userEvent.click(within(dialog).getByRole("tab", { name: "Changes" }));
    expect(within(dialog).getByText("+ ## Restart steps")).toBeTruthy();

    // Keep: built again from the same roles, with the duplicate kept.
    await userEvent.click(within(dialog).getByRole("button", { name: "Keep" }));
    await waitFor(() => expect(calls.filter((c) => c.key === "POST /pages/p1/format").at(-1)?.body).toMatchObject({ roles, keep: [2] }));
    expect(await within(dialog).findByText(/Kept duplicate:/)).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("tab", { name: "Proposed page" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(screen.getByRole("heading", { level: 2, name: "Restart steps" })).toBeTruthy());
    expect(screen.getByText("Stop then start.")).toBeTruthy(); // the kept copy is in the editor
    expect(calls.filter((c) => c.key === "PUT /pages/p1")).toHaveLength(0); // nothing saved until Save
  });

  it("a page with a 180-character unbroken title keeps it whole in the tooltip of its breadcrumb", async () => {
    window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;
    server({});
    const long = "a".repeat(180);
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: long, contentJson: doc(["p", "x"]), version: "1.0", updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat",
      path: [{ id: "f1", title: "b".repeat(180) }], history: [], canEdit: true, tags: ["c".repeat(180)], canDelete: true, draft: null, attachments: [], maxAttachmentBytes: 1000 };
    render(<DialogProvider><PageView page={page as never} spaceName="IAM" role="Owner" onTag={() => {}} onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    const crumb = screen.getByRole("navigation", { name: "Breadcrumb" });
    expect(within(crumb).getByText(long).getAttribute("title")).toBe(long);
    expect(within(crumb).getByRole("link", { name: "b".repeat(180) }).getAttribute("title")).toBe("b".repeat(180));
    expect(screen.getByRole("link", { name: "c".repeat(180) }).getAttribute("href")).toBe(`/search?tag=${"c".repeat(180)}`);
  });
});
