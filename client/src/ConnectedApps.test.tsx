import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "./Dialogs";
import { ConnectedAppsPanel } from "./ConnectedApps";

type Call = { key: string };
function server(routes: Record<string, () => { status?: number; body?: unknown }>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    calls.push({ key });
    const a = routes[key]?.() ?? { status: 404 };
    const status = a.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(a.body ?? null), { status });
  }));
  return calls;
}

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Connected applications", () => {
  it("lists the applications and disconnects one after confirming", async () => {
    let gone = false;
    const calls = server({
      "GET /me/oauth/grants": () => ({ body: gone ? [] : [{ id: "g1", clientName: "Desk app", scopes: ["READ", "WRITE"], grantedAt: "2026-11-15T10:00:00Z", lastUsedAt: null }] }),
      "DELETE /me/oauth/grants/g1": () => { gone = true; return { status: 204 }; },
    });
    render(<DialogProvider><ConnectedAppsPanel /></DialogProvider>);
    const row = (await screen.findAllByRole("row"))[1];
    expect(within(row).getAllByRole("cell").slice(0, 2).map((c) => c.textContent)).toEqual(["Desk app", "READ, WRITE"]);
    expect(within(row).getByText("Never")).toBeTruthy();
    await userEvent.click(within(row).getByRole("button", { name: "Disconnect" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(calls.some((c) => c.key.startsWith("DELETE"))).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Disconnect" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Disconnect" }));
    expect(await screen.findByText("No connected applications.")).toBeTruthy();
  });
});
