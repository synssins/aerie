import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AdminUser } from "../api";
import { UsersTable } from "./Users";

const user = (u: Partial<AdminUser>): AdminUser => ({
  id: "u1", userName: "gail", displayName: "Gail", source: "Local", isAdmin: false, isBreakGlass: false, createdAt: "2026-11-01T10:00:00Z", deactivatedAt: null, canAnnounce: false, permissions: [], ...u,
});
const columns = [{ name: "kb.sample:run", label: "Run reports" }, { name: "kb.other:export", label: "Export everything" }];

afterEach(cleanup);

describe("UsersTable", () => {
  it("shows a checkbox column per global permission", async () => {
    const onPermission = vi.fn();
    render(<UsersTable users={[user({}), user({ id: "u2", userName: "boss", displayName: "Boss", isAdmin: true, permissions: ["kb.sample:run", "kb.other:export"] }), user({ id: "u3", userName: "rita", displayName: "Rita", permissions: ["kb.sample:run"] })]}
      permissions={columns} pickedId={null} onPick={() => {}} onAnnouncer={() => {}} onPermission={onPermission} />);

    expect(screen.getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Name", "User name", "Source", "Groups", "Role", "Announcements", "Run reports", "Export everything", "Created"]);
    const gail = within(screen.getByText("Gail").closest("tr")!);
    const box = gail.getByLabelText("Gail: Run reports") as HTMLInputElement;
    expect(box.checked).toBe(false);
    expect((within(screen.getByText("Rita").closest("tr")!).getByLabelText("Rita: Run reports") as HTMLInputElement).checked).toBe(true);
    // An Admin holds every permission: ticked, and not to be changed.
    const boss = within(screen.getByText("Boss").closest("tr")!).getByLabelText("Boss: Export everything") as HTMLInputElement;
    expect([boss.checked, boss.disabled]).toEqual([true, true]);

    await userEvent.click(box);
    expect(onPermission).toHaveBeenCalledWith(expect.objectContaining({ id: "u1" }), "kb.sample:run", true);
  });

  it("has no extra column when no plugin declares a global permission", () => {
    render(<UsersTable users={[user({})]} permissions={[]} pickedId={null} onPick={() => {}} onAnnouncer={() => {}} onPermission={() => {}} />);
    expect(screen.getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Name", "User name", "Source", "Groups", "Role", "Announcements", "Created"]);
  });
});

describe("UsersTable groups", () => {
  it("lists every group a person is in, one under another", () => {
    render(<UsersTable users={[user({ groups: ["KB-DESK-Owner-P", "KB-DESK-Supervisor-P"] })]} permissions={[]} pickedId={null} onPick={() => {}} onAnnouncer={() => {}} onPermission={() => {}} />);
    expect(screen.getByText("KB-DESK-Owner-P")).toBeTruthy();
    expect(screen.getByText("KB-DESK-Supervisor-P")).toBeTruthy();
  });
});
