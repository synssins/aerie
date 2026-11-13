import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "./Dialogs";
import type { ApiToken, Space } from "./api";
import { TokensPanel } from "./Tokens";

const spaces = [{ id: "s1", name: "Desk" }, { id: "s2", name: "Ops" }] as Space[];
const token = (t: Partial<ApiToken>): ApiToken => ({
  id: "t1", name: "Agent", kind: "Personal", prefix: "kbt_AbCdEfGh", scopes: [{ spaceId: null, access: "Read" }, { spaceId: "s1", access: "Write" }], ownerId: "u1", owner: "Pat",
  createdAt: "2026-11-12T10:00:00Z", expiresAt: "2027-02-10T10:00:00Z", revokedAt: null, lastUsedAt: null, state: "Active", ...t,
});

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

const show = (admin = false) => render(<DialogProvider><TokensPanel admin={admin} spaces={spaces} /></DialogProvider>);

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("API tokens", () => {
  it("lists tokens with their scopes and state", async () => {
    server({ "GET /tokens": () => ({ body: [token({}), token({ id: "t2", name: "Old", state: "Revoked", scopes: [{ spaceId: "gone", access: "Read" }] })] }) });
    show();
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(within(rows[0]).getAllByRole("cell").slice(0, 3).map((c) => c.textContent)).toEqual(["Agent", "kbt_AbCdEfGh…", "All spaces: Read, Desk: Write"]);
    expect(within(rows[0]).getByText("Active")).toBeTruthy();
    expect(within(rows[0]).getByRole("button", { name: "Revoke" })).toBeTruthy();
    expect(within(rows[1]).getByText("Revoked")).toBeTruthy();
    expect(within(rows[1]).queryByRole("button", { name: "Revoke" })).toBeNull(); // nothing left to revoke
  });

  it("adds a token with its scopes and shows the text until Done", async () => {
    const calls = server({
      "GET /tokens": () => ({ body: [] }),
      "POST /tokens": () => ({ status: 201, body: { token: "kbt_THE-TEXT_abc123", info: token({}) } }),
    });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Personal token" }));
    await userEvent.type(screen.getByLabelText("Name"), "Agent");
    await userEvent.clear(screen.getByLabelText("Days"));
    await userEvent.type(screen.getByLabelText("Days"), "30");
    await userEvent.selectOptions(screen.getByLabelText("Space"), "s1");
    await userEvent.click(screen.getByRole("radio", { name: "Write" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(calls.find((c) => c.key === "POST /tokens")?.body).toEqual({ name: "Agent", kind: "Personal", days: 30, scopes: [{ spaceId: "s1", access: "Write" }] });
    expect((await screen.findByLabelText("New token, shown once") as HTMLInputElement).value).toBe("kbt_THE-TEXT_abc123");
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByDisplayValue("kbt_THE-TEXT_abc123")).toBeNull();
  });

  it("shows the server's words for a wrong field", async () => {
    server({ "GET /tokens": () => ({ body: [] }), "POST /tokens": () => ({ status: 400, body: { errors: { days: ["1 to 7."] } } }) });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Personal token" }));
    await userEvent.type(screen.getByLabelText("Name"), "Agent");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("1 to 7.")).toBeTruthy();
  });

  it("Revoke asks in an in-app dialog", async () => {
    const native = vi.fn(() => true);
    vi.stubGlobal("confirm", native);
    const calls = server({ "GET /tokens": () => ({ body: [token({})] }), "DELETE /tokens/t1": () => ({ status: 204 }) });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Revoke" }));
    expect(calls.some((c) => c.key === "DELETE /tokens/t1")).toBe(false);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Revoke" }));
    expect(calls.some((c) => c.key === "DELETE /tokens/t1")).toBe(true);
    expect(native).not.toHaveBeenCalled();
  });

  it("an Admin sees every token, sets the longest life, and adds a service token for an existing account", async () => {
    const calls = server({
      "GET /admin/tokens": () => ({ body: [token({}), token({ id: "t3", name: "Desk link", kind: "Service", ownerId: "svc1", owner: "Service desk" })] }),
      "GET /admin/tokens/settings": () => ({ body: { maxDays: 365 } }),
      "PUT /admin/tokens/settings": (b) => ({ body: b }),
      "POST /tokens": () => ({ status: 201, body: { token: "kbt_SVC", info: token({ kind: "Service" }) } }),
    });
    show(true);
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(within(rows[1]).getByText("Service desk")).toBeTruthy();
    expect(within(rows[1]).getByText("Service")).toBeTruthy();

    await userEvent.clear(screen.getByLabelText("Longest life"));
    await userEvent.type(screen.getByLabelText("Longest life"), "30");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /admin/tokens/settings")?.body).toEqual({ maxDays: 30 });

    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Service token" }));
    await userEvent.type(screen.getByLabelText("Name"), "Second");
    await userEvent.selectOptions(screen.getByLabelText("Account"), "svc1");
    await userEvent.click(within(screen.getByRole("form", { name: "Service token" })).getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "POST /tokens")?.body).toEqual({ name: "Second", kind: "Service", days: 90, accountId: "svc1", scopes: [{ spaceId: null, access: "Read" }] });
  });
});
