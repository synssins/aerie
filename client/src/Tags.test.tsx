import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "./Dialogs";
import { PageView } from "./PageView";
import { SearchView } from "./Views";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("tags", () => {
  it("a tag on a page is a button that opens the list of pages with it", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 200 })));
    const onTag = vi.fn();
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "VPN", contentJson: JSON.stringify({ type: "doc", content: [] }), version: "1.0",
      updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat", path: [], history: [], canEdit: false, tags: ["vpn", "windows"], canDelete: false, draft: null, attachments: [], maxAttachmentBytes: 1000 };
    render(<DialogProvider><PageView page={page as never} spaceName="Desk" role="Reader" onTag={onTag} onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    await userEvent.click(screen.getByRole("button", { name: "windows" }));
    expect(onTag).toHaveBeenCalledWith("windows");
  });

  it("the list asks for the pages with the tag and names the tag", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify({ query: "", total: 1, limit: 20, offset: 0, hits: [{ id: "p1", kind: "Page", title: "Reset VPN", spaceName: "Desk", path: [], updatedAt: "2026-11-12T10:00:00Z", snippet: null, tags: ["vpn"] }] }), { status: 200 });
    }));
    const onOpen = vi.fn();
    render(<SearchView query="" tag="vpn" onHome={() => {}} onOpen={onOpen} />);
    await userEvent.click(await screen.findByRole("button", { name: /Reset VPN/ }));
    expect(onOpen).toHaveBeenCalledWith("p1");
    expect(urls[0]).toBe("/api/v1/search?q=&tag=vpn&limit=20&offset=0");
    await waitFor(() => expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Tag vpn"));
  });

  it("a tag under a result opens that tag's list, not the page", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ query: "", total: 1, limit: 20, offset: 0,
      hits: [{ id: "p1", kind: "Page", title: "Reset MFA", spaceName: "IAM", path: [], updatedAt: "2026-11-12T10:00:00Z", snippet: null, tags: ["identity", "mfa"] }] }), { status: 200 })));
    const onOpen = vi.fn();
    const onTag = vi.fn();
    render(<SearchView query="" tag="identity" onHome={() => {}} onOpen={onOpen} onTag={onTag} />);
    await userEvent.click(await screen.findByRole("button", { name: "mfa" }));
    expect(onTag).toHaveBeenCalledWith("mfa");
    expect(onOpen).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Reset MFA" }));
    expect(onOpen).toHaveBeenCalledWith("p1");
    expect(onTag).toHaveBeenCalledTimes(1);
  });
});
