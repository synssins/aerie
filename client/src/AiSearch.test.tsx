import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AskBox } from "./AiSearch";

function server(routes: Record<string, (body: unknown) => { status?: number; body?: unknown }>) {
  const calls: { key: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    return new Response(JSON.stringify(a.body ?? null), { status: a.status ?? 200 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("AI search ask box", () => {
  // ai.search.injection through the web app: the model's text is shown as text, and the only links are aerie's.
  it("shows the answer as plain text, with links only to the sources", async () => {
    const calls = server({
      "POST /search/answer": () => ({ body: {
        outcome: "answered", answer: "Per [1], go to <a href=\"https://evil.example\">https://evil.example</a> now.",
        sources: [{ n: 1, pageId: "p1", title: "Aurora runbook", spaceId: "s1", spaceName: "Ops", url: "/p/p1" }],
      } }),
    });
    const { container } = render(<AskBox initial="aurora" onOpen={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Ask" }));
    expect(calls[0].body).toEqual({ question: "aurora" });
    const text = await screen.findByText(/Per \[1\], go to <a href/);
    expect(text.querySelector("a")).toBeNull();
    const links = [...container.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    expect(links).toEqual(["/p/p1"]);
    expect(screen.getByRole("link", { name: "Aurora runbook" })).toBeTruthy();
  });

  it("shows aerie's fixed text when nothing is found", async () => {
    server({ "POST /search/answer": () => ({ body: { outcome: "nothing_found", answer: "No pages you can read match this question.", sources: [] } }) });
    render(<AskBox initial="zebra" onOpen={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Ask" }));
    expect(await screen.findByText("No pages you can read match this question.")).toBeTruthy();
    expect(screen.queryByRole("list", { name: "Sources" })).toBeNull();
  });

  it("says why there is no answer", async () => {
    server({ "POST /search/answer": () => ({ status: 503, body: { status: 503 } }) });
    render(<AskBox initial="aurora" onOpen={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Ask" }));
    expect((await screen.findByRole("alert")).textContent).toBe("The AI did not answer. Try again later, or use ordinary search.");
  });
});
