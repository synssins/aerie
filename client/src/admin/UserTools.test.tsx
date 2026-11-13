import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AdminUser } from "../api";
import { DialogProvider } from "../Dialogs";
import { UserTools } from "./UserTools";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const user = (u: Partial<AdminUser> = {}): AdminUser => ({
  id: "u1", userName: "gail", displayName: "Gail", source: "Local", isAdmin: false, isBreakGlass: false, createdAt: "2026-11-01T10:00:00Z", deactivatedAt: null, canAnnounce: false, permissions: [], ...u,
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

function show(picked: AdminUser | null) {
  const p = { onChanged: vi.fn(), onError: vi.fn() };
  render(<DialogProvider><UserTools picked={picked} {...p} /></DialogProvider>);
  return p;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("UserTools", () => {
  it("New user makes the account and shows the link with which its password is set", async () => {
    const calls = server({ "POST /admin/users/invite": () => ({ body: { id: "u9", setPasswordToken: "tok+1/2=" } }) });
    const p = show(null);

    await userEvent.click(screen.getByRole("button", { name: "New user" }));
    await userEvent.type(screen.getByLabelText("User name"), "rita");
    await userEvent.type(screen.getByLabelText("Name"), "Rita Ray");
    await userEvent.click(screen.getByLabelText("Administrator"));
    await userEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(p.onChanged).toHaveBeenCalled());
    expect(calls[0]).toEqual({ key: "POST /admin/users/invite", body: { userName: "rita", displayName: "Rita Ray", email: null, isAdmin: true } });
    // The token is in the part of the address that is never sent to a server.
    expect((screen.getByLabelText("Link") as HTMLInputElement).value).toBe(`${location.origin}/set-password/u9#tok%2B1%2F2%3D`);
    expect(screen.getByRole("heading", { name: "Password link: Rita Ray" })).toBeTruthy();
  });

  it("New user shows why an account was not made", async () => {
    server({ "POST /admin/users/invite": () => ({ status: 400, body: { errors: { user: ["Username 'rita' is already taken."] } } }) });
    show(null);
    await userEvent.click(screen.getByRole("button", { name: "New user" }));
    await userEvent.type(screen.getByLabelText("User name"), "rita");
    await userEvent.type(screen.getByLabelText("Name"), "Rita");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));

    expect((await screen.findByRole("alert")).textContent).toBe("Username 'rita' is already taken.");
  });

  it("offers Password link and Delete for a local user only", async () => {
    server({});
    show(null);
    expect(screen.queryByRole("button", { name: "More" })).toBeNull();
    cleanup();
    show(user({ isBreakGlass: true }));
    expect(screen.queryByRole("button", { name: "More" })).toBeNull();
    cleanup();
    show(user({ source: "Entra" }));
    expect(screen.queryByRole("button", { name: "More" })).toBeNull();
    cleanup();
    show(user());
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Password link", "Delete"]);
  });

  it("Password link asks the server for a new link and shows it", async () => {
    const calls = server({ "POST /admin/users/u1/password-link": () => ({ body: { id: "u1", setPasswordToken: "abc" } }) });
    show(user());
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Password link" }));

    expect(((await screen.findByLabelText("Link")) as HTMLInputElement).value).toBe(`${location.origin}/set-password/u1#abc`);
    expect(calls.map((c) => c.key)).toEqual(["POST /admin/users/u1/password-link"]);
  });

  it("Delete asks first, in the app, and then deletes", async () => {
    const calls = server({ "DELETE /admin/users/u1": () => ({ status: 204 }) });
    const p = show(user());
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete" }));

    expect(screen.getByText("Delete “Gail”?")).toBeTruthy();
    expect(calls).toEqual([]); // nothing yet
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(p.onChanged).toHaveBeenCalled());
    expect(calls.map((c) => c.key)).toEqual(["DELETE /admin/users/u1"]);
  });

  it("Delete says why the server refused", async () => {
    server({ "DELETE /admin/users/u1": () => ({ status: 409, body: { detail: "The last administrator can't be deleted." } }) });
    const p = show(user({ isAdmin: true }));
    await userEvent.click(screen.getByRole("button", { name: "More" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete" }));
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(p.onError).toHaveBeenLastCalledWith("The last administrator can't be deleted."));
    expect(p.onChanged).not.toHaveBeenCalled();
  });
});
