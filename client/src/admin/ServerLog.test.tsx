import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ServerLog, serverLogQuery, type ServerLogRow } from "./ServerLog";

const row = (n: number, r: Partial<ServerLogRow> = {}): ServerLogRow =>
  ({ id: n, at: "2026-11-21T10:00:00Z", level: "Warning", category: "Kb.Web.Import.ImportTools", message: `line ${n}`, exception: null, ...r });

function serve(entries: ServerLogRow[]) {
  const urls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    urls.push(url.replace("/api/v1", ""));
    const q = new URL(url, "https://x").searchParams;
    const take = Number(q.get("take")), offset = Number(q.get("offset"));
    return new Response(JSON.stringify(entries.slice(offset, offset + take)), { status: 200 });
  }));
  return urls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("ServerLog", () => {
  it("builds the query from only the filters that are set", () => {
    expect(serverLogQuery("", "  ", 101, 0)).toBe("/admin/server-log?take=101&offset=0");
    expect(serverLogQuery("Error", " timeout ", 101, 100)).toBe("/admin/server-log?take=101&offset=100&level=Error&text=timeout");
  });

  it("shows the newest first, and loads more until there is no more", async () => {
    const urls = serve(Array.from({ length: 150 }, (_, i) => row(150 - i)));
    render(<ServerLog />);
    expect(await screen.findByText("line 150")).toBeTruthy();
    expect(screen.getAllByRole("row")).toHaveLength(101); // header and 100
    await userEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(await screen.findByText("line 1")).toBeTruthy();
    expect(screen.getAllByRole("row")).toHaveLength(151);
    expect(urls).toContain("/admin/server-log?take=101&offset=100");
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });

  it("filters by level and text", async () => {
    const urls = serve([row(1)]);
    render(<ServerLog />);
    await screen.findByText("line 1");
    await userEvent.selectOptions(screen.getByLabelText("Level"), "Errors");
    await userEvent.type(screen.getByLabelText("Text"), "x");
    await waitFor(() => expect(urls).toContain("/admin/server-log?take=101&offset=0&level=Error&text=x"));
  });

  it("opens an exception to its full text", async () => {
    serve([row(1, { level: "Error", exception: "System.InvalidOperationException: It broke.\n   at Kb.Web.Thing()" })]);
    render(<ServerLog />);
    const summary = await screen.findByText("Exception");
    const details = summary.closest("details")!;
    expect(details.open).toBe(false);
    await userEvent.click(summary);
    expect(details.open).toBe(true);
    expect(details.querySelector("pre")!.textContent).toContain("at Kb.Web.Thing()");
  });
});
