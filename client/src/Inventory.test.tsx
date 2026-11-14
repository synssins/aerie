import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { InvApp, PageDetails } from "./api";
import { DialogProvider } from "./Dialogs";
import { InventoryView } from "./Inventory";
import { detailsBody, detailsChanged, PageDetailsPanel } from "./PageDetails";
import { InventoryAccess } from "./admin/InventoryAccess";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const erp: InvApp = {
  id: "a1", name: "Acme ERP", vendor: "Acme", owner: { type: "Group", id: "local:g1", name: "ERP Support Team" }, notes: "Finance",
  installs: [{ id: "i1", server: "ERP01", version: "4.3", changedAt: "2026-11-14T10:00:00Z", changedBy: "CSV import (Ann)",
    history: [{ at: "2026-11-14T10:00:00Z", by: "CSV import (Ann)", kind: "Changed", from: "4.2", to: "4.3" }] }],
  pages: [{ id: "p1", title: "ERP error 42", spaceName: "Ops", servers: [] }], canEdit: true,
};

type Call = { key: string; body: unknown };
function server(routes: Record<string, (body: unknown) => unknown>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const match = Object.keys(routes).find((k) => key === k || (k.endsWith("*") && key.startsWith(k.slice(0, -1))));
    return match ? new Response(JSON.stringify(routes[match](body) ?? null), { status: 200 }) : new Response(null, { status: 404 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("inventory", () => {
  it("lists applications, filters by server, and offers New only to editors", async () => {
    const calls = server({
      "GET /inventory/apps*": () => [{ id: "a1", name: "Acme ERP", vendor: "Acme", owner: null, installs: 2 }],
      "GET /inventory/servers": () => ["ERP01", "ERP02"],
      "GET /inventory/me": () => ({ canView: true, canEdit: false }),
    });
    const onOpenApp = vi.fn();
    render(<DialogProvider><InventoryView appId={null} onOpenApp={onOpenApp} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "Acme ERP" }));
    expect(onOpenApp).toHaveBeenCalledWith("a1");
    expect(screen.queryByRole("button", { name: "New" })).toBeNull();
    await userEvent.selectOptions(screen.getByLabelText("Server"), "ERP02");
    await waitFor(() => expect(calls.map((c) => c.key)).toContain("GET /inventory/apps?server=ERP02"));
  });

  it("CSV import previews first and applies after", async () => {
    const result = { added: 1, changed: 1, unchanged: 3, removed: 0, newApplications: 0,
      changes: [{ application: "Acme ERP", server: "ERP01", kind: "Changed", from: "4.2", to: "4.3" }] };
    const calls = server({
      "GET /inventory/apps*": () => [], "GET /inventory/servers": () => [], "GET /inventory/me": () => ({ canView: true, canEdit: true }),
      "POST /inventory/import": () => result,
    });
    render(<DialogProvider><InventoryView appId={null} onOpenApp={() => {}} onOpenPage={() => {}} onHome={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "New" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "CSV import" }));
    const dialog = screen.getByRole("dialog");
    await userEvent.upload(within(dialog).getByLabelText("File"), new File(["Application,Server,Version\nAcme ERP,ERP01,4.3\n"], "inv.csv", { type: "text/csv" }));
    expect((within(dialog).getByRole("button", { name: "Apply" }) as HTMLButtonElement).disabled).toBe(true);
    await waitFor(() => expect((within(dialog).getByRole("button", { name: "Preview" }) as HTMLButtonElement).disabled).toBe(false));
    await userEvent.click(within(dialog).getByRole("button", { name: "Preview" }));
    expect((await within(dialog).findByRole("status")).textContent).toContain("1 new, 1 changed");
    expect(within(dialog).getByText("4.2 → 4.3")).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "Apply" }));
    await waitFor(() => expect(calls.filter((c) => c.key === "POST /inventory/import").map((c) => (c.body as { apply: boolean }).apply)).toEqual([false, true]));
  });

  it("an application shows its servers with their history and the pages that apply to it", async () => {
    const calls = server({ "GET /inventory/apps/a1": () => erp, "PUT /inventory/apps/a1/installs": () => erp });
    const onOpenPage = vi.fn();
    render(<DialogProvider><InventoryView appId="a1" onOpenApp={() => {}} onOpenPage={onOpenPage} onHome={() => {}} /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "ERP01" }));
    expect(screen.getByText("4.2 → 4.3")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "ERP error 42" }));
    expect(onOpenPage).toHaveBeenCalledWith("p1");
    await userEvent.type(screen.getByLabelText("Server"), "ERP02");
    await userEvent.type(screen.getByLabelText("Version"), "4.3");
    await userEvent.click(screen.getByRole("button", { name: "Set version" }));
    await waitFor(() => expect(calls).toContainEqual({ key: "PUT /inventory/apps/a1/installs", body: { server: "ERP02", version: "4.3" } }));
  });
});

describe("page details", () => {
  const details: PageDetails = { appliesTo: [{ applicationId: "a1", name: "Acme ERP", servers: ["ERP01"] }], owner: { type: "Group", id: "local:g1", name: "ERP Support Team" }, reviewMonths: 6 };

  it("shows what the page applies to, its owner and review cycle, and opens the application", async () => {
    const onOpenApp = vi.fn();
    render(<PageDetailsPanel details={details} updatedAt="2026-01-15T10:00:00Z" editing={false} value={details} onChange={() => {}} onOpenApp={onOpenApp} />);
    expect(screen.getByText("ERP Support Team")).toBeTruthy();
    expect(screen.getByText(/^6 months · /)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Acme ERP · ERP01" }));
    expect(onOpenApp).toHaveBeenCalledWith("a1");
  });

  it("shows nothing when there is nothing to show", () => {
    const { container } = render(<PageDetailsPanel details={null} updatedAt="2026-01-15T10:00:00Z" editing={false} value={details} onChange={() => {}} />);
    expect(container.textContent).toBe("");
  });

  it("is edited with the page and sent by ids", async () => {
    server({ "GET /inventory/apps*": () => [] });
    const onChange = vi.fn();
    render(<PageDetailsPanel details={details} updatedAt="2026-01-15T10:00:00Z" editing value={details} onChange={onChange} />);
    await userEvent.click(screen.getByRole("button", { name: "Remove Acme ERP" }));
    expect(onChange).toHaveBeenCalledWith({ ...details, appliesTo: [] });
    await userEvent.selectOptions(screen.getByLabelText("Review cycle"), "12");
    expect(onChange).toHaveBeenLastCalledWith({ ...details, reviewMonths: 12 });
    expect(detailsBody(details)).toEqual({ appliesTo: [{ applicationId: "a1", servers: ["ERP01"] }], ownerType: "Group", ownerId: "local:g1", reviewMonths: 6 });
    expect(detailsChanged(null, { appliesTo: [], owner: null, reviewMonths: null })).toBe(false);
    expect(detailsChanged(details, { ...details, reviewMonths: 3 })).toBe(true);
  });
});

describe("inventory access", () => {
  it("adds and removes who views and who edits", async () => {
    const calls = server({
      "GET /admin/inventory/access": () => ({ view: [{ kind: "Everyone", id: null }], edit: [] }),
      "PUT /admin/inventory/access": (b) => b,
      "GET /admin/spaces": () => [{ id: "s1", name: "Ops" }],
      "GET /roles": () => [{ id: "r2", name: "Contributor", permissions: ["Read"], builtIn: "Contributor", inUse: 0 }],
    });
    render(<InventoryAccess area="Administration" onHome={() => {}} />);
    expect(await screen.findByText("Admins only")).toBeTruthy();
    await userEvent.selectOptions(screen.getByLabelText("Add to edit"), "Holders of a role");
    await userEvent.selectOptions(await screen.findByLabelText("Role"), "Contributor");
    const addButtons = screen.getAllByRole("button", { name: "Add" });
    await userEvent.click(addButtons[1]);
    await waitFor(() => expect(calls).toContainEqual({ key: "PUT /admin/inventory/access", body: { view: [{ kind: "Everyone", id: null }], edit: [{ kind: "Role", id: "r2" }] } }));
  });
});
