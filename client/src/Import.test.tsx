import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ImportItem, Space } from "./api";
import { DialogProvider } from "./Dialogs";
import { ImportView, mergeDrafts, splitAtHeadings } from "./Import";
import { PageView } from "./PageView";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const space = { id: "s1", key: "iam", name: "IAM", description: null, isSensitive: false, visibility: "Private", myRole: "Contributor", commentsEnabled: true, agingDays: 180, agingAudience: "Everyone", recycleDays: null, createdAt: "2026-11-01T00:00:00Z" } as Space;
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

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("import", () => {
  it("uploads files into one batch, each with its path, then starts it", async () => {
    const list = { batches: [], items: [{ id: "i1", batchId: "b1", title: "Reset MFA", status: "Ready", error: null, files: 2, createdAt: "2026-11-14T10:00:00Z", pageId: null }], canImport: true, canFormat: true };
    const calls = server({ "GET /imports": () => list, "POST /imports": () => ({ id: "b1" }), "POST /imports/b1/files": () => ({}), "POST /imports/b1/start": () => ({}) });
    const onOpenItem = vi.fn();
    render(<DialogProvider><ImportView itemId={null} spaces={[space]} onOpenItem={onOpenItem} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    await userEvent.selectOptions(await screen.findByLabelText("Destination"), "IAM");
    await userEvent.upload(screen.getByLabelText("Files"), [new File(["# Reset"], "reset.md"), new File(["x"], "shot.png")]);
    await waitFor(() => expect(calls.map((c) => c.key)).toContain("POST /imports/b1/start"));
    expect(calls.find((c) => c.key === "POST /imports")?.body).toEqual({ spaceId: "s1" });
    expect(calls.filter((c) => c.key === "POST /imports/b1/files").map((c) => c.headers?.["X-File-Name"])).toEqual(["reset.md", "shot.png"]);
    await userEvent.click(screen.getByRole("button", { name: "Reset MFA" }));
    expect(onOpenItem).toHaveBeenCalledWith("i1");
  });

  it("reviews a draft: redacted by default, source beside it, accepted with the chosen place and metadata", async () => {
    const item: ImportItem = {
      id: "i1", title: "Reset MFA", status: "Ready", error: null, pageId: null,
      files: [{ id: "f1", name: "reset.md", path: "reset.md", size: 10, kind: "Text", note: null, embedded: false, sha256: "a" }],
      sources: { f1: "# Reset MFA\npassword: Hunter2" },
      proposal: {
        pages: [{ title: "Reset MFA", contentJson: doc(["p", "password: Hunter2"]), redactedJson: doc(["p", "password: [REDACTED]"]) }],
        tags: ["mfa"], appliesTo: [{ applicationId: "a1", name: "Acme ERP", servers: ["ERP01"] }], owner: { type: "Group", id: "local:g1", name: "ERP Support Team" }, reviewMonths: 6,
        destination: { spaceId: "s1", parentId: null }, suggestedDestination: null, sensitive: [{ kind: "Password", sample: "Hu•••r2" }],
        duplicates: [{ pageId: "p9", title: "Reset MFA (old)", score: 0.6, spaceId: "s1" }], warnings: [], model: "stub",
      },
    };
    const calls = server({ "GET /imports/items/i1": () => item, "GET /spaces/s1/tree": () => [], "POST /imports/items/i1/accept": () => ["p1"] });
    const onOpenPage = vi.fn();
    render(<DialogProvider><ImportView itemId="i1" spaces={[space]} onOpenItem={() => {}} onOpenPage={onOpenPage} onHome={() => {}} /></DialogProvider>);
    expect(await screen.findByText(/\[REDACTED\]/)).toBeTruthy();
    expect(screen.getByText(/password: Hunter2/, { selector: "pre" })).toBeTruthy(); // the source, unredacted, on the left
    expect(screen.getByLabelText("Update “Reset MFA (old)”")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(onOpenPage).toHaveBeenCalledWith("p1"));
    const body = calls.find((c) => c.key === "POST /imports/items/i1/accept")!.body as Record<string, unknown>;
    expect(body).toMatchObject({ spaceId: "s1", updatePageId: null, attachOriginals: true, details: { ownerType: "Group", ownerId: "local:g1", reviewMonths: 6, appliesTo: [{ applicationId: "a1", servers: ["ERP01"] }] } });
    expect(JSON.stringify(body.pages)).toContain("[REDACTED]");
    expect(JSON.stringify(body.pages)).not.toContain("Hunter2");
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
  it("shows the changed lines and puts the formatting in the editor only when accepted", async () => {
    window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;
    const formatted = doc(["h", "Restart steps"], ["p", "Stop then start"]);
    const calls = server({ "POST /pages/p1/format": () => ({ contentJson: formatted, diff: [{ op: "Del", text: "Restart steps" }, { op: "Add", text: "## Restart steps" }] }) });
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "Restart", contentJson: doc(["p", "Restart steps"], ["p", "Stop then start"]), version: "1.0",
      updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat", path: [], history: [], canEdit: true, tags: [], canDelete: true, draft: null, attachments: [], maxAttachmentBytes: 1000 };
    render(<DialogProvider><PageView page={page as never} spaceName="IAM" role="Owner" autoEdit canFormat onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "Format" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("+ ## Restart steps")).toBeTruthy();
    expect(calls.some((c) => c.key.startsWith("PUT /pages/p1 "))).toBe(false);
    await userEvent.click(within(dialog).getByRole("button", { name: "Accept" }));
    await waitFor(() => expect(screen.getByRole("heading", { level: 2, name: "Restart steps" })).toBeTruthy());
    expect(calls.filter((c) => c.key === "PUT /pages/p1")).toHaveLength(0); // nothing saved until Save
  });
});
