import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PageRating, type RatingSummary } from "./Ratings";

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

const summary = (s: Partial<RatingSummary>): RatingSummary => ({ enabled: true, canRate: true, canSeeAll: false, positive: 0, solved: 0, mine: null, ...s });

describe("page rating", () => {
  it("rates positive and says it solved the problem", async () => {
    const calls = server({ "GET /pages/p1/ratings": () => ({ body: summary({ positive: 3, solved: 1 }) }), "PUT /pages/p1/ratings/mine": () => ({ body: summary({ positive: 4, solved: 2 }) }) });
    render(<PageRating pageId="p1" />);
    expect(await screen.findByText("3 found this helpful; it solved the problem for 1.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "This solved my problem" }));
    expect(calls.find((c) => c.key === "PUT /pages/p1/ratings/mine")?.body).toEqual({ value: "positive", solved: true });
  });

  it("rates negative with a reason, and removes the rating", async () => {
    const mine = { value: "negative" as const, solved: false, reason: "Old port", version: "1.0", createdAt: "2026-11-16T10:00:00Z", updatedAt: "2026-11-16T10:00:00Z" };
    const calls = server({
      "GET /pages/p1/ratings": () => ({ body: summary({}) }),
      "PUT /pages/p1/ratings/mine": () => ({ body: summary({ mine }) }),
      "DELETE /pages/p1/ratings/mine": () => ({ status: 204 }),
    });
    render(<PageRating pageId="p1" />);
    await userEvent.click(await screen.findByRole("button", { name: "No" }));
    await userEvent.type(screen.getByRole("textbox"), "Old port");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(calls.find((c) => c.key === "PUT /pages/p1/ratings/mine")?.body).toEqual({ value: "negative", reason: "Old port" });
    expect(await screen.findByText(/Your rating: not helpful/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    expect(calls.some((c) => c.key === "DELETE /pages/p1/ratings/mine")).toBe(true);
  });

  it("shows Supervisors the negative count and every rating", async () => {
    const entry = { user: { type: "user", id: "u1", name: "Rita" }, value: "negative", solved: false, reason: "Step 4 fails", version: "1.0", createdAt: "2026-11-16T10:00:00Z", updatedAt: "2026-11-16T10:00:00Z" };
    server({
      "GET /pages/p1/ratings": () => ({ body: summary({ canSeeAll: true, negative: 1 }) }),
      "GET /pages/p1/ratings/all": () => ({ body: { total: 1, limit: 50, offset: 0, ratings: [entry] } }),
    });
    render(<PageRating pageId="p1" />);
    expect(await screen.findByText(/1 found it not helpful/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Show ratings" }));
    expect(await screen.findByText("Step 4 fails")).toBeTruthy();
  });

  it("shows nothing when ratings are off and you have no rating", async () => {
    server({ "GET /pages/p1/ratings": () => ({ body: summary({ enabled: false, canRate: false }) }) });
    const { container } = render(<PageRating pageId="p1" />);
    await new Promise((r) => setTimeout(r, 20));
    expect(container.querySelector(".rating")).toBeNull();
  });
});
