import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import type { LogsInfo, LogTarget } from "../api";
import { LogShipping } from "./LogShipping";

const kinds = [
  { kind: "file", label: "Log file", fields: [{ key: "path", label: "Folder", type: "text" as const, options: null }, { key: "days", label: "Days kept", type: "text" as const, options: null }] },
  { kind: "syslog", label: "Syslog", fields: [
    { key: "host", label: "Host", type: "text" as const, options: null }, { key: "port", label: "Port", type: "text" as const, options: null },
    { key: "protocol", label: "Protocol", type: "select" as const, options: ["udp", "tcp", "tls"] },
  ] },
  { kind: "https", label: "HTTPS collector", fields: [
    { key: "url", label: "Address", type: "text" as const, options: null }, { key: "headerName", label: "Header name", type: "text" as const, options: null },
    { key: "headerValue", label: "Header value", type: "secret" as const, options: null },
  ] },
];
const target = (t: Partial<LogTarget>): LogTarget => ({
  id: "t1", name: "Local log file", kind: "file", settings: { path: "App_Data/logs", days: "90" }, secretsSet: [], enabled: true, lastSequence: 15,
  lastShippedAt: "2026-11-12T12:00:00Z", lastError: null, lastErrorAt: null, createdAt: "2026-11-12T10:00:00Z", ...t,
});
const siem = target({ id: "t2", name: "SIEM", kind: "https", settings: { url: "https://siem.example/in", headerName: "X-Key" }, secretsSet: ["headerValue"], lastSequence: 12, lastError: "The collector answered HTTP 503." });
const off = target({ id: "t3", name: "Old syslog", kind: "syslog", settings: { host: "10.0.0.5", port: "514", protocol: "udp" }, enabled: false, lastSequence: 3 });
const info = (targets = [target({}), siem, off]): LogsInfo => ({ kinds, targets, head: 15 });

type Call = { key: string; body: unknown };
function server(routes: Record<string, (body: unknown) => { status?: number; body?: unknown }>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1/admin/logs", "") || "/"}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    const status = a.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(a.body ?? null), { status });
  }));
  return calls;
}

function show(targetId: string | null = null) {
  const onSelect = vi.fn();
  render(<DialogProvider><LogShipping area="Administration" targetId={targetId} onSelect={onSelect} onHome={() => {}} /></DialogProvider>);
  return onSelect;
}

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Log shipping", () => {
  it("lists targets with where they send and how far they are", async () => {
    server({ "GET /": () => ({ body: info() }) });
    const onSelect = show();
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(rows.map((r) => within(r).getAllByRole("cell").map((c) => c.textContent))).toEqual([
      ["Local log file", "Log file", "App_Data/logs", "Up to date"],
      ["SIEM", "HTTPS collector", "https://siem.example/in", "3 behind: The collector answered HTTP 503."],
      ["Old syslog", "Syslog", "10.0.0.5", "Off"],
    ]);
    await userEvent.click(rows[1]);
    expect(onSelect).toHaveBeenCalledWith("t2");
  });

  it("draws the form from the kind's fields, with a choice list and never a secret", async () => {
    const calls = server({ "GET /": () => ({ body: info() }), "PUT /targets/t2": () => ({ body: siem }), "PUT /targets/t3": () => ({ body: off }) });
    show("t2");
    expect(await screen.findByLabelText("Address")).toHaveProperty("value", "https://siem.example/in");
    expect(screen.queryByLabelText("Header value")).toBeNull(); // set: dots and Change
    expect(screen.getByText("••••••••")).toBeTruthy();
    await userEvent.click(screen.getByRole("switch", { name: "Enabled" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /targets/t2")?.body).toEqual({
      name: "SIEM", enabled: false, settings: { url: "https://siem.example/in", headerName: "X-Key" }, secrets: { headerValue: null },
    });
    cleanup();

    show("t3");
    await userEvent.selectOptions(await screen.findByLabelText("Protocol"), "tls");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect((calls.find((c) => c.key === "PUT /targets/t3")?.body as { settings: unknown }).settings).toEqual({ host: "10.0.0.5", port: "514", protocol: "tls" });
  });

  it("Test shows Works or the error text", async () => {
    let ok = true;
    server({ "GET /": () => ({ body: info() }), "POST /targets/t2/test": () => ({ body: ok ? { ok: true, error: null } : { ok: false, error: "No answer from siem.example:443." } }) });
    show("t2");
    await userEvent.click(await screen.findByRole("button", { name: "Test" }));
    expect(await screen.findByText("Works")).toBeTruthy();
    ok = false;
    await userEvent.click(screen.getByRole("button", { name: "Test" }));
    expect(await screen.findByText("No answer from siem.example:443.")).toBeTruthy();
  });

  it("adds a target of a chosen kind, and Remove asks in an in-app dialog", async () => {
    const native = vi.fn(() => true);
    vi.stubGlobal("confirm", native);
    const calls = server({
      "GET /": () => ({ body: info() }),
      "POST /targets": () => ({ status: 400, body: { errors: { host: ["Required."] } } }),
      "DELETE /targets/t2": () => ({ status: 204 }),
    });
    const onSelect = show();
    await userEvent.click(await screen.findByRole("button", { name: "Add" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Syslog" }));
    await userEvent.type(screen.getByLabelText("Name"), "Collector");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect((calls.find((c) => c.key === "POST /targets")?.body as { kind: string }).kind).toBe("syslog");
    expect(await screen.findByText("Required.")).toBeTruthy();
    cleanup();

    show("t2");
    await userEvent.click(await screen.findByRole("button", { name: "Remove" }));
    expect(calls.some((c) => c.key === "DELETE /targets/t2")).toBe(false);
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Remove" }));
    expect(calls.some((c) => c.key === "DELETE /targets/t2")).toBe(true);
    expect(native).not.toHaveBeenCalled();
    void onSelect;
  });
});
