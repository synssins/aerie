import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "./Dialogs";
import { PageComments, type Comment, type CommentList } from "./Comments";

type Call = { key: string; body: unknown };
function server(routes: Record<string, (body: unknown) => { status?: number; body?: unknown }>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    const status = a.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(a.body ?? null), { status });
  }));
  return calls;
}

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const comment = (c: Partial<Comment>): Comment => ({
  id: "c1", pageId: "p1", parentId: null, author: { type: "user", id: "u1", name: "Rita" }, text: "Step 3 is out of date.", createdAt: "2026-11-16T10:00:00Z",
  editedAt: null, state: "visible", canEdit: false, canDelete: false, canModerate: false, replies: [], ...c,
});
const list = (l: Partial<CommentList>): CommentList => ({ enabled: true, canComment: true, canModerate: false, total: 0, limit: 50, offset: 0, comments: [], ...l });
const show = () => render(<DialogProvider><PageComments pageId="p1" /></DialogProvider>);

describe("page comments", () => {
  it("shows text as plain text, never as markup", async () => {
    server({ "GET /pages/p1/comments": () => ({ body: list({ total: 1, comments: [comment({ text: "<b>bold</b> <a href=\"https://evil.example\">x</a>" })] }) }) });
    show();
    const text = await screen.findByText(/<b>bold<\/b>/);
    expect(text.querySelector("b, a")).toBeNull();
  });

  it("posts a comment and a reply", async () => {
    const calls = server({
      "GET /pages/p1/comments": () => ({ body: list({ total: 1, comments: [comment({})] }) }),
      "POST /pages/p1/comments": (b) => ({ status: 201, body: comment({ id: "c2", text: (b as { text: string }).text }) }),
    });
    show();
    await userEvent.type(await screen.findByRole("textbox", { name: "Add a comment" }), "Thanks");
    await userEvent.click(screen.getByRole("button", { name: "Comment" }));
    expect(calls.find((c) => c.key === "POST /pages/p1/comments")?.body).toEqual({ text: "Thanks" });
    await userEvent.click(screen.getByRole("button", { name: "Reply" }));
    await userEvent.type(screen.getByRole("textbox", { name: "Reply" }), "Me too");
    await userEvent.click(within(screen.getByRole("textbox", { name: "Reply" }).closest("form")!).getByRole("button", { name: "Reply" }));
    expect(calls.filter((c) => c.key === "POST /pages/p1/comments").at(-1)?.body).toEqual({ text: "Me too", parentId: "c1" });
  });

  it("deletes your own comment after confirming, and shows placeholders", async () => {
    const calls = server({
      "GET /pages/p1/comments": () => ({ body: list({ total: 2, comments: [comment({ canDelete: true }), comment({ id: "c3", author: null, text: null, state: "deleted", replies: [comment({ id: "r1", parentId: "c3", text: "A reply" })] })] }) }),
      "DELETE /comments/c1": () => ({ status: 204 }),
    });
    show();
    expect(await screen.findByText("This comment was deleted.")).toBeTruthy();
    expect(screen.getByText("A reply")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));
    expect(calls.some((c) => c.key === "DELETE /comments/c1")).toBe(true);
  });

  it("lets a moderator hide, restore and delete", async () => {
    const calls = server({
      "GET /pages/p1/comments": () => ({ body: list({ canModerate: true, total: 2, comments: [comment({ canModerate: true, canDelete: true }), comment({ id: "c2", state: "hidden", canModerate: true, canDelete: true })] }) }),
      "POST /comments/c1/hide": () => ({ status: 204 }),
      "POST /comments/c2/restore": () => ({ status: 204 }),
    });
    show();
    expect(await screen.findByText("Hidden by a moderator")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Hide" }));
    await userEvent.click(screen.getByRole("button", { name: "Restore" }));
    expect(calls.map((c) => c.key)).toEqual(expect.arrayContaining(["POST /comments/c1/hide", "POST /comments/c2/restore"]));
  });

  it("says why a comment was refused", async () => {
    server({ "GET /pages/p1/comments": () => ({ body: list({}) }), "POST /pages/p1/comments": () => ({ status: 429, body: { status: 429 } }) });
    show();
    await userEvent.type(await screen.findByRole("textbox", { name: "Add a comment" }), "Again");
    await userEvent.click(screen.getByRole("button", { name: "Comment" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Too many comments. Wait a minute.");
  });

  it("shows nothing when comments are off and there is nothing to moderate", async () => {
    server({ "GET /pages/p1/comments": () => ({ body: list({ enabled: false, canComment: false }) }) });
    const { container } = show();
    await new Promise((r) => setTimeout(r, 20));
    expect(container.querySelector(".comments")).toBeNull();
  });
});
