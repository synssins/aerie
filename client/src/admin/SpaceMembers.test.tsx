import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import type { RoleAssignment } from "../api";
import { SpaceMembers } from "./SpaceMembers";

const rows: RoleAssignment[] = [
  { id: "a1", principalType: "User", principalId: "u1", role: "Owner", name: "Olga" },
  { id: "a2", principalType: "Group", principalId: "local:g1", role: "Contributor", name: "Desk Team" },
];

function serve() {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify(rows), { status: 200, headers: { "content-type": "application/json" } })));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("SpaceMembers", () => {
  it("lets an Owner change everyone but an Owner, and never offers Owner", async () => {
    serve();
    render(<SpaceMembers spaceId="s1" isAdmin={false} />);
    const olga = (await screen.findByText("Olga")).closest("tr")!;
    expect(within(olga).queryByRole("combobox")).toBeNull();
    expect(within(olga).queryByRole("button", { name: "Remove" })).toBeNull();
    const team = screen.getByText("Desk Team").closest("tr")!;
    expect(within(team).getByRole("combobox")).toBeTruthy();
    expect(within(screen.getByRole("combobox", { name: "Role" })).queryByRole("option", { name: "Owner" })).toBeNull();
  });

  it("lets an Admin change an Owner row and give Owner", async () => {
    serve();
    render(<SpaceMembers spaceId="s1" isAdmin />);
    const olga = (await screen.findByText("Olga")).closest("tr")!;
    expect(within(olga).getByRole("button", { name: "Remove" })).toBeTruthy();
    expect(within(screen.getByRole("combobox", { name: "Role" })).getByRole("option", { name: "Owner" })).toBeTruthy();
  });
});
