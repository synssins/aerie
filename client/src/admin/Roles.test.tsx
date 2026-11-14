import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { RoleDef } from "../api";
import { DialogProvider } from "../Dialogs";
import { Roles } from "./Roles";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const start = (): RoleDef[] => [
  { id: "r1", name: "Reader", permissions: ["Read"], builtIn: "Reader", inUse: 2 },
  { id: "r8", name: "Editor", permissions: ["Read", "CreateEdit"], builtIn: null, inUse: 3 },
];

type Call = { key: string; body: unknown };
function server(answer: (key: string, body: unknown) => { status?: number; body?: unknown } | undefined) {
  const calls: Call[] = [];
  let roles = start();
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = answer(key, body);
    if (a) return new Response(a.status === 204 ? null : JSON.stringify(a.body ?? null), { status: a.status ?? 200 });
    if (key === "GET /roles") return new Response(JSON.stringify(roles), { status: 200 });
    if (key.startsWith("PUT /admin/roles/")) {
      const b = body as { name: string; permissions: RoleDef["permissions"] };
      roles = roles.map((r) => (key.endsWith(r.id) ? { ...r, ...b } : r));
      return new Response(JSON.stringify(roles[0]), { status: 200 });
    }
    return new Response(null, { status: 404 });
  }));
  return calls;
}

const show = () => render(<DialogProvider><Roles area="Administration" onHome={() => {}} /></DialogProvider>);

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Roles", () => {
  it("shows each role's permissions, Read always on, and saves a change at once", async () => {
    const calls = server(() => undefined);
    show();
    const read = await screen.findByRole("checkbox", { name: "Editor: Read" });
    expect((read as HTMLInputElement).checked && (read as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole("checkbox", { name: "Editor: Create and edit" }) as HTMLInputElement).checked).toBe(true);
    await userEvent.click(screen.getByRole("checkbox", { name: "Editor: Move and delete own" }));
    await waitFor(() => expect(calls).toContainEqual({ key: "PUT /admin/roles/r8", body: { name: "Editor", permissions: ["Read", "CreateEdit", "MoveDeleteOwn"] } }));
    expect((screen.getByRole("checkbox", { name: "Editor: Move and delete own" }) as HTMLInputElement).checked).toBe(true);
  });

  it("a built-in role has no Delete; deleting one in use says why it was refused", async () => {
    server((key) => (key === "DELETE /admin/roles/r8" ? { status: 409, body: { detail: "Used in 3 places." } } : undefined));
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Reader: more" }));
    expect(screen.queryByRole("menuitem", { name: "Delete role" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Editor: more" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete role" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Used in 3 places.");
  });

  it("New › Role asks for the name in the app and adds the role with Read", async () => {
    const calls = server((key) => (key === "POST /admin/roles" ? { status: 201, body: {} } : undefined));
    show();
    await screen.findByRole("checkbox", { name: "Editor: Read" });
    await userEvent.click(screen.getByRole("button", { name: "New" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Role" }));
    await userEvent.type(within(screen.getByRole("dialog")).getByLabelText("Name"), "Auditor");
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Add" }));
    await waitFor(() => expect(calls).toContainEqual({ key: "POST /admin/roles", body: { name: "Auditor", permissions: ["Read"] } }));
  });
});
