import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FeedbackSettingsPanel, PageFeedbackSwitches, type FeedbackLevels } from "./Feedback";

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

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const on: FeedbackLevels = { site: true, space: true, page: true, effective: true };

describe("feedback switches", () => {
  it("saves the site switches", async () => {
    const calls = server({ "GET /admin/feedback": () => ({ body: { commentsEnabled: true, ratingsEnabled: true } }), "PUT /admin/feedback": (b) => ({ body: b }) });
    render(<FeedbackSettingsPanel />);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Allow ratings on pages" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /admin/feedback")?.body).toEqual({ commentsEnabled: true, ratingsEnabled: false });
    expect((await screen.findByRole("status")).textContent).toBe("Saved.");
  });

  it("turns a page's comments off, and says when a level above has it off", async () => {
    const calls = server({
      "GET /pages/p1/feedback": () => ({ body: { comments: on, ratings: { ...on, space: false, effective: false } } }),
      "PUT /pages/p1/feedback": () => ({ body: { comments: { ...on, page: false, effective: false }, ratings: { ...on, space: false, effective: false } } }),
    });
    render(<PageFeedbackSwitches pageId="p1" />);
    expect(await screen.findByText("Off for this space.")).toBeTruthy();
    await userEvent.click(screen.getByRole("checkbox", { name: "Comments" }));
    expect(calls.find((c) => c.key === "PUT /pages/p1/feedback")?.body).toEqual({ commentsEnabled: false });
    expect((screen.getByRole("checkbox", { name: "Comments" }) as HTMLInputElement).checked).toBe(false);
  });

  it("shows why a change was refused", async () => {
    server({ "GET /pages/p1/feedback": () => ({ body: { comments: on, ratings: on } }), "PUT /pages/p1/feedback": () => ({ status: 403, body: { status: 403 } }) });
    render(<PageFeedbackSwitches pageId="p1" />);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Ratings" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Only the space's Supervisors and Owners change this.");
  });
});
