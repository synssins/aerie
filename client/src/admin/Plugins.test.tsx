import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import type { PluginInfo, PluginSetting } from "../api";
import { Plugins } from "./Plugins";

const setting = (s: Partial<PluginSetting> & { key: string; label: string }): PluginSetting => ({ secret: false, required: false, type: "text", options: null, restart: false, ...s });
const plugin = (p: Partial<PluginInfo>): PluginInfo => ({
  id: "kb.email.smtp", name: "Email (SMTP)", version: "1.0.0", publisher: "Kb first-party", description: null, origin: "BuiltIn", state: "Enabled", reason: null,
  locked: false, loaded: true, source: "built-in", requires: [], requiredBy: [], extensionPoints: ["email-sender"], outboundHosts: [], permissions: [], enforcing: false,
  settings: [], replaces: [], removed: false, ...p,
});
const probe = plugin({
  id: "kb.test.probe", name: "Test probe", origin: "Uploaded", source: "upload", publisher: "Kb tests", requires: ["kb.test.lib"], outboundHosts: ["api.example.com"],
  settings: [
    setting({ key: "Greeting", label: "Greeting" }),
    setting({ key: "Token", label: "Token", secret: true, type: "secret" }),
    setting({ key: "Count", label: "Count", type: "number" }),
    setting({ key: "On", label: "On", type: "toggle" }),
    setting({ key: "Mode", label: "Mode", type: "select", options: ["slow", "fast"], restart: true }),
    setting({ key: "Notes", label: "Notes", type: "multiline" }),
  ],
});
const broken = plugin({ id: "kb.broken", name: "Broken", origin: "Uploaded", source: "upload", state: "DisabledByCore", reason: "page.save hook: The remote system said no.", locked: true, loaded: false });

type Answer = { status?: number; body?: unknown };
type Call = { key: string; body: unknown };
function server(routes: Record<string, (body: unknown) => Answer>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1/admin", "")}`;
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : init?.body;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    const status = a.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(a.body ?? null), { status });
  }));
  return calls;
}
const list = (plugins: PluginInfo[], safeMode = false) => () => ({ body: { plugins, safeMode } });
const show = () => render(<DialogProvider><Plugins area="Administration" onHome={() => {}} /></DialogProvider>);

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("Plugins", () => {
  it("lists plugins with their state", async () => {
    server({ "GET /plugins": list([plugin({}), probe, broken], true) });
    show();
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(rows.map((r) => within(r).getAllByRole("cell").map((c) => c.textContent))).toEqual([
      ["Email (SMTP)", "1.0.0", "Kb first-party", "Enabled"],
      ["Test probe", "1.0.0", "Kb tests", "Enabled"],
      ["Broken", "1.0.0", "Kb first-party", "Disabled by core"],
    ]);
    expect(screen.getByText("Safe mode")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Upload" })).toBeTruthy();
  });

  it("shows the core's reason under a plugin it disabled", async () => {
    const calls = server({ "GET /plugins": list([broken]), "GET /plugins/kb.broken/settings": () => ({ body: { values: {}, secretsSet: [] } }), "POST /plugins/kb.broken/release-lock": () => ({ status: 204 }) });
    show();
    await userEvent.click(await screen.findByText("Broken"));
    expect(await screen.findByText("Disabled by core: page.save hook: The remote system said no.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Enable" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Release lock" }));
    expect(calls.some((c) => c.key === "POST /plugins/kb.broken/release-lock")).toBe(true);
  });

  it("never renders a secret's value", async () => {
    const calls = server({
      "GET /plugins": list([probe]),
      "GET /plugins/kb.test.probe/settings": () => ({ body: { values: { Greeting: "Hello" }, secretsSet: ["Token"] } }),
      "PUT /plugins/kb.test.probe/settings": () => ({ body: { restart: false } }),
    });
    show();
    await userEvent.click(await screen.findByText("Test probe"));
    expect(await screen.findByLabelText("Greeting")).toHaveProperty("value", "Hello");
    expect(screen.queryByLabelText("Token")).toBeNull(); // set: dots and Change, no input
    expect(screen.getByText("••••••••")).toBeTruthy();

    await userEvent.type(screen.getByLabelText("Greeting"), "!");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    const sent = calls.find((c) => c.key === "PUT /plugins/kb.test.probe/settings")?.body as { values: Record<string, string | null> };
    expect(sent.values.Greeting).toBe("Hello!");
    expect(sent.values.Token).toBeNull(); // kept
  });

  it("draws each setting type: text, secret, number, toggle, select, multiline", async () => {
    const calls = server({
      "GET /plugins": list([probe]),
      "GET /plugins/kb.test.probe/settings": () => ({ body: { values: { Mode: "slow" }, secretsSet: [] } }),
      "PUT /plugins/kb.test.probe/settings": () => ({ status: 400, body: { errors: { Count: ["A number."] } } }),
    });
    show();
    await userEvent.click(await screen.findByText("Test probe"));
    expect((await screen.findByLabelText("Greeting") as HTMLInputElement).type).toBe("text");
    expect((screen.getByLabelText("Token") as HTMLInputElement).type).toBe("password");
    expect((screen.getByLabelText("Count") as HTMLInputElement).type).toBe("number");
    expect(screen.getByRole("switch", { name: "On" }).getAttribute("aria-checked")).toBe("false");
    expect((screen.getByLabelText("Mode") as HTMLSelectElement).tagName).toBe("SELECT");
    expect((screen.getByLabelText("Notes") as HTMLTextAreaElement).tagName).toBe("TEXTAREA");

    await userEvent.click(screen.getByRole("switch", { name: "On" }));
    await userEvent.selectOptions(screen.getByLabelText("Mode"), "fast");
    await userEvent.type(screen.getByLabelText("Token"), "s3cret");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect((calls.find((c) => c.key === "PUT /plugins/kb.test.probe/settings")?.body as { values: unknown }).values).toEqual(
      { Greeting: "", Token: "s3cret", Count: "", On: "true", Mode: "fast", Notes: "" });
    expect(await screen.findByText("A number.")).toBeTruthy(); // the server's words, beside the field
  });

  it("remove dialog leaves Delete data unchecked and sends deleteData=false", async () => {
    const native = vi.fn(() => true);
    vi.stubGlobal("confirm", native);
    const calls = server({
      "GET /plugins": list([probe]), "GET /plugins/kb.test.probe/settings": () => ({ body: { values: {}, secretsSet: [] } }),
      "DELETE /plugins/kb.test.probe?deleteData=false": () => ({ status: 409, body: { detail: "Required by Test dependent." } }),
    });
    show();
    await userEvent.click(await screen.findByText("Test probe"));
    await userEvent.click(await screen.findByRole("button", { name: "Remove" }));
    const dialog = screen.getByRole("dialog");
    expect((within(dialog).getByLabelText("Delete data") as HTMLInputElement).checked).toBe(false);
    expect(calls.some((c) => c.key.startsWith("DELETE"))).toBe(false);
    await userEvent.click(within(dialog).getByRole("button", { name: "Remove" }));
    expect(calls.some((c) => c.key === "DELETE /plugins/kb.test.probe?deleteData=false")).toBe(true);
    expect(await screen.findByText("Required by Test dependent.")).toBeTruthy(); // a refusal, as the server sent it
    expect(native).not.toHaveBeenCalled();
  });

  it("a removed plugin shows \"Removed, data kept\" and offers only Delete data, which asks first", async () => {
    const gone = plugin({ id: "kb.test.probe", name: "kb.test.probe", origin: "Uploaded", source: "upload", publisher: "", state: "Disabled", loaded: false, extensionPoints: [], removed: true });
    const calls = server({ "GET /plugins": list([gone]), "DELETE /plugins/kb.test.probe/data": () => ({ status: 204 }) });
    show();
    const row = (await screen.findAllByRole("row"))[1];
    expect(within(row).getAllByRole("cell").map((c) => c.textContent)).toEqual(["kb.test.probe", "1.0.0", "", "Removed, data kept"]);
    await userEvent.click(within(row).getByText("kb.test.probe"));
    expect(screen.getByText("Removed, data kept")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Enable" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Delete data" }));
    const dialog = screen.getByRole("dialog");
    expect(calls.some((c) => c.key.startsWith("DELETE"))).toBe(false);
    await userEvent.click(within(dialog).getByRole("button", { name: "Delete data" }));
    expect(calls.some((c) => c.key === "DELETE /plugins/kb.test.probe/data")).toBe(true);
    expect(await screen.findByText("Restarting")).toBeTruthy();
  });

  it("shows a plugin's health checks and jobs in the right panel", async () => {
    server({
      "GET /plugins": list([probe]), "GET /plugins/kb.test.probe/settings": () => ({ body: { values: {}, secretsSet: [] } }),
      "GET /plugins/health": () => ({ body: { plugins: [{ id: "kb.test.probe",
        checks: [{ name: "remote", status: "Unhealthy", text: "The remote system said no." }],
        jobs: [{ name: "sync", everyMinutes: 15, lastRunAt: "2026-11-12T10:00:00Z", nextRunAt: "2026-11-12T10:15:00Z", lastError: "Timed out." }, { name: "purge", everyMinutes: 1440, lastRunAt: null, nextRunAt: null, lastError: null }] }] } }),
    });
    show();
    await userEvent.click(await screen.findByText("Test probe"));
    const side = screen.getByLabelText("Details");
    expect(await within(side).findByText("Health")).toBeTruthy();
    expect(within(side).getByText("remote: Unhealthy")).toBeTruthy();
    expect(within(side).getByText("The remote system said no.")).toBeTruthy();
    expect(within(side).getByText("Jobs")).toBeTruthy();
    expect(within(side).getByText("sync, every 15 min")).toBeTruthy();
    expect(within(side).getByText("Timed out.")).toBeTruthy();
    expect(within(side).getByText("purge, every 1440 min")).toBeTruthy();
    expect(within(side).getByText("Not run yet")).toBeTruthy();
  });

  it("shows no Health or Jobs heading for a plugin that has neither", async () => {
    server({ "GET /plugins": list([plugin({})]), "GET /plugins/health": () => ({ body: { plugins: [] } }) });
    show();
    await userEvent.click(await screen.findByText("Email (SMTP)"));
    const side = screen.getByLabelText("Details");
    expect(within(side).getByText("Replaces")).toBeTruthy();
    expect(within(side).queryByText("Health")).toBeNull();
    expect(within(side).queryByText("Jobs")).toBeNull();
  });

  it("shows Restarting until the server answers, then reloads", async () => {
    vi.useFakeTimers();
    let up = false;
    let state: PluginInfo["state"] = "Enabled";
    const calls = server({
      "GET /plugins": () => ({ body: { plugins: [plugin({ state })], safeMode: false } }),
      "GET /plugins/kb.email.smtp/settings": () => ({ body: { values: {}, secretsSet: [] } }),
      "PUT /plugins/kb.email.smtp/state": () => { state = "Disabled"; return { status: 204 }; },
      "GET /health/ready": () => ({ status: up ? 200 : 503 }),
    });
    const tick = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));
    show();
    await tick(0);
    fireEvent.click(screen.getByText("Email (SMTP)")); // a plain click: the typing helper waits on the clock this test holds
    await tick(0);
    fireEvent.click(screen.getByRole("button", { name: "Disable" }));
    await tick(0);
    expect(calls.find((c) => c.key === "PUT /plugins/kb.email.smtp/state")?.body).toEqual({ enabled: false });
    expect(screen.getByText("Restarting")).toBeTruthy();

    await tick(2000);
    expect(screen.getByText("Restarting")).toBeTruthy(); // 503: still down
    up = true;
    await tick(2000);
    expect(screen.queryByText("Restarting")).toBeNull();
    expect(screen.getByRole("button", { name: "Enable" })).toBeTruthy(); // reloaded: it is disabled now
    const asked = calls.filter((c) => c.key === "GET /health/ready").length;
    await tick(6000);
    expect(calls.filter((c) => c.key === "GET /health/ready").length).toBe(asked); // it stops asking
  });

  it("a 413 on upload shows \"Larger than the web server allows.\"", async () => {
    server({ "GET /plugins": list([plugin({})]), "POST /plugins": () => ({ status: 413 }) });
    show();
    await screen.findByText("Email (SMTP)");
    await userEvent.upload(screen.getByLabelText("Plugin package"), new File([new Uint8Array(10)], "big.kbplugin"));
    expect(await screen.findByText("Larger than the web server allows.")).toBeTruthy();
  });

  it("uploads a package and restarts", async () => {
    const calls = server({ "GET /plugins": list([plugin({})]), "POST /plugins": () => ({ body: { id: "kb.test.probe", version: "1.0.0", restart: true } }), "GET /health/ready": () => ({ status: 503 }) });
    show();
    await screen.findByText("Email (SMTP)");
    await userEvent.upload(screen.getByLabelText("Plugin package"), new File([new Uint8Array([1, 2, 3])], "probe.kbplugin"));
    expect(await screen.findByText("Restarting")).toBeTruthy();
    expect(calls.find((c) => c.key === "POST /plugins")?.body).toBeInstanceOf(File);
  });
});
