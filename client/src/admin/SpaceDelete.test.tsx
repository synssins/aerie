import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DeletedSpaces, DeleteSpaceDialog } from "./SpaceDelete";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

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

const space = { id: "s1", key: "desk", name: "Service Desk" };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("DeleteSpaceDialog", () => {
  it("asks three times: Delete, Yes I'm sure, then a box to tick and a phrase to type", async () => {
    const calls = server({ "POST /admin/spaces/s1/delete": () => ({ status: 204 }) });
    const onDeleted = vi.fn();
    render(<DeleteSpaceDialog space={space} onClose={() => {}} onDeleted={onDeleted} />);

    expect(screen.getByRole("heading", { name: "Delete “Service Desk”?" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(screen.getByRole("heading", { name: "Are you sure?" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Yes, I'm sure" }));

    const last = screen.getByRole("button", { name: "Delete space" }) as HTMLButtonElement;
    expect(last.disabled).toBe(true);
    await userEvent.type(screen.getByLabelText("Type “delete desk”"), "delete desk");
    expect(last.disabled).toBe(true); // the box is not ticked
    await userEvent.click(screen.getByLabelText("Delete the space and everything in it"));
    expect(last.disabled).toBe(false);
    expect(calls).toEqual([]); // nothing was sent before the last step

    await userEvent.click(last);

    await waitFor(() => expect(onDeleted).toHaveBeenCalled());
    expect(calls).toEqual([{ key: "POST /admin/spaces/s1/delete", body: { confirm: "delete desk" } }]);
  });

  it("the last button stays off while the phrase is not the phrase", async () => {
    server({});
    render(<DeleteSpaceDialog space={space} onClose={() => {}} onDeleted={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(screen.getByRole("button", { name: "Yes, I'm sure" }));
    await userEvent.click(screen.getByLabelText("Delete the space and everything in it"));
    await userEvent.type(screen.getByLabelText("Type “delete desk”"), "delete des");
    expect((screen.getByRole("button", { name: "Delete space" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("Cancel at any step sends nothing", async () => {
    const calls = server({});
    const onClose = vi.fn();
    render(<DeleteSpaceDialog space={space} onClose={onClose} onDeleted={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(onClose).toHaveBeenCalled();
    expect(calls).toEqual([]);
  });

  it("shows why the server refused", async () => {
    server({ "POST /admin/spaces/s1/delete": () => ({ status: 400, body: { errors: { confirm: ["Type: delete desk"] } } }) });
    render(<DeleteSpaceDialog space={space} onClose={() => {}} onDeleted={() => {}} />);
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(screen.getByRole("button", { name: "Yes, I'm sure" }));
    await userEvent.click(screen.getByLabelText("Delete the space and everything in it"));
    await userEvent.type(screen.getByLabelText("Type “delete desk”"), "DELETE DESK");
    await userEvent.click(screen.getByRole("button", { name: "Delete space" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Type: delete desk");
  });
});

describe("DeletedSpaces", () => {
  const deleted = [{ id: "s1", key: "desk", name: "Service Desk", isSensitive: false, deletedAt: "2026-11-13T10:00:00Z", deletedBy: "Pat", pages: 12 }];

  it("draws nothing when no space is deleted", async () => {
    const calls = server({ "GET /admin/spaces/deleted": () => ({ body: [] }) });
    const { container } = render(<DeletedSpaces onRestored={() => {}} />);
    await waitFor(() => expect(calls).toHaveLength(1));
    expect(container.querySelector("table")).toBeNull();
  });

  it("shows no page count for a sensitive space", async () => {
    server({ "GET /admin/spaces/deleted": () => ({ body: [{ ...deleted[0], name: "Vault", isSensitive: true, pages: null }] }) });
    render(<DeletedSpaces onRestored={() => {}} />);
    const row = (await screen.findByText("Vault")).closest("tr")!;
    expect(row.querySelectorAll("td")[1].textContent).toBe("");
  });

  it("lists the deleted spaces and restores one", async () => {
    let list = deleted;
    const calls = server({ "GET /admin/spaces/deleted": () => ({ body: list }), "POST /admin/spaces/s1/restore": () => { list = []; return { body: {} }; } });
    const onRestored = vi.fn();
    render(<DeletedSpaces onRestored={onRestored} />);

    const row = (await screen.findByText("Service Desk")).closest("tr")!;
    expect(within(row).getByText("12")).toBeTruthy();
    expect(within(row).getByText("Pat")).toBeTruthy();
    await userEvent.click(within(row).getByRole("button", { name: "Restore" }));

    await waitFor(() => expect(onRestored).toHaveBeenCalled());
    expect(calls.map((c) => c.key)).toEqual(["GET /admin/spaces/deleted", "POST /admin/spaces/s1/restore", "GET /admin/spaces/deleted"]);
  });
});
