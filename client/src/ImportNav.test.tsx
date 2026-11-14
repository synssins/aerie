import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { DialogProvider } from "./Dialogs";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };
window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;

const ITEM = "11111111-1111-1111-1111-111111111111";
const list = {
  batches: [], canImport: true, canFormat: true,
  items: [{ id: ITEM, batchId: "b1", title: "Reset MFA", status: "Ready", error: null, files: 1, createdAt: "2026-11-14T10:00:00Z", pageId: null, source: "reset.md", suggestedSpace: null }],
};
const item = {
  id: ITEM, title: "Reset MFA", status: "Ready", error: null, pageId: null, sources: { f1: "# Reset" },
  files: [{ id: "f1", name: "reset.md", path: "reset.md", size: 7, kind: "Text", note: null, embedded: false, sha256: "a" }],
  proposal: { pages: [{ title: "Reset MFA", contentJson: '{"type":"doc","content":[]}', redactedJson: '{"type":"doc","content":[]}' }], tags: [], appliesTo: [], owner: null, reviewMonths: null,
    destination: null, suggestedDestination: null, sensitive: [], duplicates: [], warnings: [], model: "m", summary: [] },
};

const SPACE = "22222222-2222-2222-2222-222222222222";
const PAGE = "33333333-3333-3333-3333-333333333333";
const space = { id: SPACE, key: "iam", name: "IAM", description: null, isSensitive: false, visibility: "Private", myRole: "Owner", commentsEnabled: true, agingDays: 180, agingAudience: "Everyone", recycleDays: null, createdAt: "2026-11-01T00:00:00Z" };
const tree = [{ id: PAGE, parentId: null, kind: "Page", title: "Reset VPN", position: 0 }];

function server() {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    calls.push(key);
    const body: Record<string, unknown> = {
      "GET /brand": { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" },
      "GET /setup/status": { required: false, codeRequired: false },
      "GET /me": { id: "u1", userName: "chris", displayName: "Chris", isAdmin: true, isBreakGlass: false, canAnnounce: true },
      "GET /spaces": [space], [`GET /spaces/${SPACE}/tree`]: tree, "GET /imports": list, "GET /me/settings": {}, "GET /home": { recent: [], announcements: [] }, [`GET /imports/items/${ITEM}`]: item, "GET /notifications/count": { unread: 0 },
    };
    return key in body ? new Response(JSON.stringify(body[key]), { status: 200 }) : new Response(null, { status: 404 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); history.replaceState(null, "", "/"); });

describe("import navigation", () => {
  it("the inbox lists drafts as links, opens one in the app, and lists them again after Back", async () => {
    history.replaceState(null, "", "/import");
    const calls = server();
    render(<DialogProvider><App /></DialogProvider>);
    const link = await screen.findByRole("link", { name: "Reset MFA" });
    expect(link.getAttribute("href")).toBe(`/import/${ITEM}`);
    await userEvent.click(link);
    await waitFor(() => expect(location.pathname).toBe(`/import/${ITEM}`));
    expect(await screen.findByRole("region", { name: "Proposed pages" })).toBeTruthy();
    const before = calls.filter((c) => c === "GET /imports").length;
    act(() => { history.back(); });
    expect(await screen.findByRole("link", { name: "Reset MFA" })).toBeTruthy();
    expect(calls.filter((c) => c === "GET /imports").length).toBeGreaterThan(before); // fetched again, not kept
  });

  it("a draft opens directly from its address, as in a new tab", async () => {
    history.replaceState(null, "", `/import/${ITEM}`);
    server();
    render(<DialogProvider><App /></DialogProvider>);
    expect(await screen.findByRole("region", { name: "Proposed pages" })).toBeTruthy();
  });

  it("the space tree is links: each space and page has its address, and Ctrl-click leaves it to the browser", async () => {
    history.replaceState(null, "", `/s/${SPACE}`);
    server();
    render(<DialogProvider><App /></DialogProvider>);
    const nav = await screen.findByRole("navigation", { name: "Spaces and pages" });
    const spaceLink = await within(nav).findByRole("link", { name: /IAM/ });
    expect(spaceLink.getAttribute("href")).toBe(`/s/${SPACE}`);
    const pageLink = await within(nav).findByRole("link", { name: "Reset VPN" });
    expect(pageLink.getAttribute("href")).toBe(`/p/${PAGE}`);
    expect(within(nav).getByRole("link", { name: "Import inbox" }).getAttribute("href")).toBe("/import");
    // A modified click is the browser's (a new tab): the app does not move.
    const click = new MouseEvent("click", { bubbles: true, cancelable: true, ctrlKey: true });
    pageLink.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(false);
    expect(location.pathname).toBe(`/s/${SPACE}`);
  });
});
