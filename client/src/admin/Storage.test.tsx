import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import type { StorageInfo, StorageMove, StoragePlace } from "../api";
import { Storage } from "./Storage";

const kinds = [{ kind: "folder", label: "Folder or network share", fields: [
  { key: "path", label: "Path", type: "text" as const },
  { key: "userName", label: "User name", type: "text" as const },
  { key: "password", label: "Password", type: "secret" as const },
] }];
const web: StoragePlace = { id: "p1", name: "Web server folder", kind: "folder", settings: { path: "App_Data/files" }, secretsSet: [], role: "Active", createdAt: "2026-11-12T10:00:00Z" };
const share: StoragePlace = { id: "p2", name: "File share", kind: "folder", settings: { path: "\\\\nas\\kb", userName: "CORP\\svc" }, secretsSet: ["password"], role: "Old", createdAt: "2026-11-12T11:00:00Z" };
const move = (m: Partial<StorageMove>): StorageMove => ({
  id: "m1", fromId: "p1", toId: "p2", state: "Copying", total: 340, copied: 120, checked: 0, failed: 0, lastError: null, preview: false,
  startedAt: "2026-11-12T12:00:00Z", finishedAt: null, ...m,
});
const info = (m: StorageMove | null = null, places = [web, share]): StorageInfo => ({ kinds, places, move: m });

type Answer = { status?: number; body?: unknown };
type Call = { key: string; body: unknown };

/** Answers the screen's requests. A key is "METHOD path" under /api/v1/admin/storage. */
function server(routes: Record<string, (body: unknown) => Answer>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1/admin/storage", "") || "/"}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    const status = a.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(a.body ?? null), { status });
  }));
  return calls;
}

function show(placeId: string | null = null) {
  const onSelect = vi.fn();
  render(<DialogProvider><Storage area="Administration" placeId={placeId} onSelect={onSelect} onHome={() => {}} /></DialogProvider>);
  return onSelect;
}

// jsdom has the dialog element but not showModal.
HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("Storage", () => {
  it("lists places with their role", async () => {
    server({ "GET /": () => ({ body: info() }) });
    const onSelect = show();
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(rows.map((r) => within(r).getAllByRole("cell").map((c) => c.textContent))).toEqual([
      ["Web server folder", "Folder or network share", "App_Data/files", "Active"],
      ["File share", "Folder or network share", "\\\\nas\\kb", "Old"],
    ]);
    await userEvent.click(rows[1]);
    expect(onSelect).toHaveBeenCalledWith("p2");
  });

  it("draws the form from the kind's fields and never shows a secret", async () => {
    const calls = server({ "GET /": () => ({ body: info() }), "PUT /places/p2": () => ({ body: share }) });
    show("p2");
    expect(await screen.findByLabelText("Path")).toHaveProperty("value", "\\\\nas\\kb");
    expect(screen.getByLabelText("User name")).toHaveProperty("value", "CORP\\svc");
    // The password is set: dots and a Change button, no input that could hold it.
    expect(screen.queryByLabelText("Password")).toBeNull();
    expect(screen.getByText("••••••••")).toBeTruthy();

    await userEvent.type(screen.getByLabelText("Name"), " 2");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /places/p2")?.body).toEqual({
      name: "File share 2", settings: { path: "\\\\nas\\kb", userName: "CORP\\svc" }, secrets: { password: null }, // null: kept
    });

    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    const password = screen.getByLabelText("Password") as HTMLInputElement;
    expect([password.type, password.value]).toEqual(["password", ""]);
  });

  it("Test shows Works or the error text", async () => {
    let ok = true;
    server({ "GET /": () => ({ body: info() }), "POST /places/p2/test": () => ({ body: ok ? { ok: true, error: null } : { ok: false, error: "Access is denied." } }) });
    show("p2");
    await userEvent.click(await screen.findByRole("button", { name: "Test" }));
    expect(await screen.findByText("Works")).toBeTruthy();
    ok = false;
    await userEvent.click(screen.getByRole("button", { name: "Test" }));
    expect(await screen.findByText("Access is denied.")).toBeTruthy();
    expect(screen.queryByText("Works")).toBeNull();
  });

  it("shows copy progress and refreshes while copying", async () => {
    vi.useFakeTimers();
    let current = move({});
    const calls = server({ "GET /": () => ({ body: info(current) }) });
    const tick = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));
    show();
    await tick(0);
    expect(screen.getByText("Copied 120 of 340")).toBeTruthy();
    expect(screen.getByText("Checked 0 of 340")).toBeTruthy();

    current = move({ state: "Copied", copied: 340, checked: 340 });
    await tick(3000);
    expect(screen.getByText("Copied 340 of 340")).toBeTruthy();
    expect(screen.getByText("Checked 340 of 340")).toBeTruthy();

    const asked = calls.length;
    await tick(9000); // no longer copying: it stops asking
    expect(calls.length).toBe(asked);
  });

  it("names a place of the move that was removed since", async () => {
    server({ "GET /": () => ({ body: info(move({ state: "Switched", copied: 340, checked: 340, finishedAt: "2026-11-12T12:05:00Z" }), [share]) }) });
    show();
    const panel = await screen.findByRole("region", { name: "Move" });
    expect(panel.textContent).toContain("Removed placeFile shareSwitched");
  });

  it("Switch is disabled until everything is copied and checked", async () => {
    let current = move({});
    server({ "GET /": () => ({ body: info(current) }) });
    const sw = () => screen.findByRole("button", { name: "Switch" }) as Promise<HTMLButtonElement>;

    show();
    expect((await sw()).disabled).toBe(true); // still copying
    cleanup();

    current = move({ state: "Copied", copied: 339, checked: 339, failed: 1, lastError: "0f8fad5b-d9cb-469f-a165-70867728950e: Not found." });
    show();
    expect((await sw()).disabled).toBe(true); // one file failed
    expect(screen.getByText(/0f8fad5b-d9cb-469f-a165-70867728950e: Not found\./)).toBeTruthy();
    cleanup();

    current = move({ state: "Copied", copied: 340, checked: 340 });
    show();
    expect((await sw()).disabled).toBe(false);
  });

  it("Switch and Cancel ask in an in-app dialog", async () => {
    const native = vi.fn(() => true);
    vi.stubGlobal("confirm", native);
    const calls = server({
      "GET /": () => ({ body: info(move({ state: "Copied", copied: 340, checked: 340 })) }),
      "POST /move/switch": () => ({ status: 204 }),
      "POST /move/cancel": () => ({ status: 204 }),
    });
    const sent = (key: string) => calls.filter((c) => c.key === key).length;
    show();

    await userEvent.click(await screen.findByRole("button", { name: "Switch" }));
    let dialog = screen.getByRole("dialog");
    expect(sent("POST /move/switch")).toBe(0); // nothing happens before the answer
    await userEvent.click(within(dialog).getByRole("button", { name: "Back" }));
    expect(sent("POST /move/switch")).toBe(0);

    await userEvent.click(screen.getByRole("button", { name: "Switch" }));
    dialog = screen.getByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Switch" }));
    expect(sent("POST /move/switch")).toBe(1);

    await userEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    dialog = screen.getByRole("dialog");
    expect(sent("POST /move/cancel")).toBe(0);
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel move" }));
    expect(sent("POST /move/cancel")).toBe(1);
    expect(native).not.toHaveBeenCalled();
  });
});
