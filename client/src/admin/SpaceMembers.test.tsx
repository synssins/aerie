import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { RoleAssignment, RoleDef } from "../api";
import { SpaceMembers } from "./SpaceMembers";

const roles: RoleDef[] = [
  { id: "r1", name: "Reader", permissions: ["Read"], builtIn: "Reader", inUse: 0 },
  { id: "r2", name: "Contributor", permissions: ["Read", "CreateEdit", "MoveDeleteOwn"], builtIn: "Contributor", inUse: 1 },
  { id: "r4", name: "Owner", permissions: ["Read", "ManageRolesSettingsSensitive"], builtIn: "Owner", inUse: 1 },
  { id: "r9", name: "Steward", permissions: ["Read", "ManageRolesSettingsSensitive"], builtIn: null, inUse: 0 },
  { id: "r8", name: "Editor", permissions: ["Read", "CreateEdit"], builtIn: null, inUse: 0 },
];
const rows: RoleAssignment[] = [
  { id: "a1", principalType: "User", principalId: "u1", role: "Owner", name: "Olga", roleId: "r4", roleName: "Owner" },
  { id: "a2", principalType: "Group", principalId: "local:g1", role: "Contributor", name: "Desk Team", roleId: "r2", roleName: "Contributor" },
];

function serve() {
  const calls: { key: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    calls.push({ key, body: init?.body ? JSON.parse(init.body as string) : undefined });
    const body = key === "GET /roles" ? roles : key.startsWith("GET /spaces/s1/roles") ? rows : null;
    return new Response(body === null ? null : JSON.stringify(body), { status: body === null ? 204 : 200 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("SpaceMembers", () => {
  it("lets an Owner change everyone but an Owner, and never offers a role that manages members", async () => {
    serve();
    render(<SpaceMembers spaceId="s1" isAdmin={false} />);
    const picker = screen.getByRole("combobox", { name: "Role" });
    expect(await within(picker).findByRole("option", { name: "Editor" })).toBeTruthy();
    expect(within(picker).queryByRole("option", { name: "Owner" })).toBeNull();
    expect(within(picker).queryByRole("option", { name: "Steward" })).toBeNull();
    const olga = (await screen.findByText("Olga")).closest("tr")!;
    expect(within(olga).queryByRole("combobox")).toBeNull();
    expect(within(olga).queryByRole("button", { name: "Remove" })).toBeNull();
    const team = screen.getByText("Desk Team").closest("tr")!;
    expect(within(team).getByRole("combobox")).toBeTruthy();
  });

  it("lets an Admin change an Owner row and give any role, by its id", async () => {
    const calls = serve();
    render(<SpaceMembers spaceId="s1" isAdmin />);
    expect(await within(screen.getByRole("combobox", { name: "Role" })).findByRole("option", { name: "Steward" })).toBeTruthy();
    const olga = (await screen.findByText("Olga")).closest("tr")!;
    expect(within(olga).getByRole("button", { name: "Remove" })).toBeTruthy();
    await userEvent.selectOptions(within(olga).getByRole("combobox"), "Steward");
    await waitFor(() => expect(calls).toContainEqual({ key: "PUT /spaces/s1/roles", body: { principalType: "User", principalId: "u1", roleId: "r9" } }));
  });
});
