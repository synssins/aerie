import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import type { PluginInfo, PluginSetting } from "../api";
import { Plugins } from "./Plugins";
import { slots } from "../kit/slots";

const setting = (s: Partial<PluginSetting> & { key: string; label: string }): PluginSetting => ({ secret: false, required: false, type: "text", options: null, restart: false, ...s });
const plugin = (p: Partial<PluginInfo>): PluginInfo => ({
  id: "kb.email.smtp", name: "Email (SMTP)", version: "1.0.0", publisher: "Kb first-party", description: null, origin: "BuiltIn", state: "Enabled", reason: null,
  locked: false, loaded: true, source: "built-in", requires: [], requiredBy: [], extensionPoints: ["email-sender"], outboundHosts: [], permissions: [], enforcing: false,
  settings: [], replaces: [], removed: false, importFileKinds: [], ...p,
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
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); slots.clear(); });

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

  it("shows a role select per space permission and saves the choice", async () => {
    const withPermissions = plugin({ id: "kb.sample", name: "Sample", origin: "Uploaded", source: "upload", permissions: [
      { key: "see", label: "See who acknowledged a page", scope: "space", defaultRole: "Supervisor" },
      { key: "run", label: "Run reports", scope: "global", defaultRole: "Owner" },
    ] });
    const calls = server({
      "GET /plugins": list([withPermissions]),
      "GET /plugins/kb.sample/permissions": () => ({ body: { permissions: [
        { key: "see", label: "See who acknowledged a page", scope: "space", defaultRole: "Supervisor", role: "Supervisor" },
        { key: "run", label: "Run reports", scope: "global", defaultRole: "Owner", role: null },
      ] } }),
      "PUT /plugins/kb.sample/permissions": () => ({ status: 204 }),
    });
    show();
    await userEvent.click(await screen.findByText("Sample"));
    const select = await screen.findByLabelText("See who acknowledged a page") as HTMLSelectElement;
    expect(select.value).toBe("Supervisor");
    expect(Array.from(select.options).map((o) => o.value)).toEqual(["Reader", "Contributor", "Supervisor", "Owner"]);
    expect(screen.queryByLabelText("Run reports")).toBeNull(); // a global permission is given per person, on the Users screen
    await userEvent.selectOptions(select, "Reader");
    await userEvent.click(screen.getByRole("button", { name: "Save permissions" }));
    expect(calls.find((c) => c.key === "PUT /plugins/kb.sample/permissions")?.body).toEqual({ roles: { see: "Reader" } });
    expect(await screen.findByText("Saved.")).toBeTruthy();
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
  it("shows a sign-in method's redirect address on its plugin's page, with its settings in the form", async () => {
    const withMethod = plugin({
      ...probe, signInMethods: [{ id: "kb.test.probe/corp", name: "Sign in with Corp ID", redirectAddress: "https://kb.example/api/v1/auth/methods/kb.test.probe/corp/callback" }],
      settings: [setting({ key: "signIn.corp.clientId", label: "Client ID" }), setting({ key: "signIn.corp.clientSecret", label: "Client secret", secret: true })],
    });
    server({ "GET /plugins": list([withMethod]), "GET /plugins/kb.test.probe/settings": () => ({ body: { values: {}, secretsSet: [] } }) });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Settings of Test probe" }));
    const address = await screen.findByLabelText("Redirect address of Sign in with Corp ID");
    expect((address as HTMLInputElement).value).toBe("https://kb.example/api/v1/auth/methods/kb.test.probe/corp/callback");
    expect((address as HTMLInputElement).readOnly).toBe(true);
    expect(await screen.findByLabelText("Client ID")).toBeTruthy();
  });

  it("opens a plugin's page from the gear left of its name", async () => {
    server({ "GET /plugins": list([plugin({ description: "Sends email." }), probe]), "GET /plugins/kb.test.probe/settings": () => ({ body: { values: {}, secretsSet: ["Token"] } }) });
    show();
    const gear = await screen.findByRole("button", { name: "Settings of Test probe" });
    expect(gear.closest("td")?.textContent).toBe("Test probe"); // in the name's cell, before the name
    await userEvent.click(gear);
    expect(await screen.findByRole("heading", { name: "Test probe", level: 1 })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Disable" })).toBeTruthy();
    expect(await screen.findByLabelText("Greeting")).toBeTruthy();
  });

  it("shows a setting's help, unit, default and option labels, and keeps the default out of what is saved", async () => {
    const tidy = plugin({
      id: "kb.tidy", name: "Tidy", origin: "Uploaded", settings: [
        setting({ key: "MaxMb", label: "Largest file", type: "number", unit: "MB", default: "50", min: 1, max: 500, help: "Larger files are refused." }),
        setting({ key: "Shape", label: "Shape", type: "select", options: ["sq", "rd"], optionLabels: { sq: "Square", rd: "Round" }, default: "rd" }),
        setting({ key: "Loud", label: "Loud", type: "toggle", default: "true" }),
      ],
    });
    const calls = server({
      "GET /plugins": list([tidy]),
      "GET /plugins/kb.tidy/settings": () => ({ body: { values: {}, secretsSet: [] } }),
      "PUT /plugins/kb.tidy/settings": () => ({ status: 400, body: { errors: { MaxMb: ["At most 500 MB."] } } }),
    });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Settings of Tidy" }));
    const size = await screen.findByLabelText("Largest file");
    expect([size.getAttribute("placeholder"), size.getAttribute("min"), size.getAttribute("max"), size.getAttribute("aria-describedby")]).toEqual(["50", "1", "500", "ps-MaxMb-h"]);
    expect(screen.getByText("Larger files are refused.").id).toBe("ps-MaxMb-h");
    expect(size.parentElement?.textContent).toBe("MB");
    const shape = screen.getByLabelText("Shape") as HTMLSelectElement;
    expect([...shape.options].map((o) => [o.value, o.textContent])).toEqual([["", "Default (Round)"], ["sq", "Square"], ["rd", "Round"]]);
    expect(screen.getByRole("switch", { name: "Loud" }).getAttribute("aria-checked")).toBe("true");
    await userEvent.type(size, "900");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /plugins/kb.tidy/settings")?.body).toEqual({ values: { MaxMb: "900", Shape: "", Loud: "" } });
    expect(await screen.findByText("At most 500 MB.")).toBeTruthy();
  });

  it("draws the panels a plugin added to its own page, and a panel that throws only says so", async () => {
    server({ "GET /plugins": list([probe, plugin({})]), "GET /plugins/kb.test.probe/settings": () => ({ body: { values: {}, secretsSet: [] } }) });
    vi.spyOn(console, "error").mockImplementation(() => {});
    slots.add("plugin.settings", { id: "files", title: "Libraries", render: (c: { plugin: { id: string } }) => <p>Files of {c.plugin.id}</p> }, "kb.test.probe");
    slots.add("plugin.settings", { id: "bad", title: "Broken part", render: () => { throw new Error("boom"); } }, "kb.test.probe");
    slots.add("plugin.settings", { id: "other", title: "Not mine", render: () => <p>Other plugin</p> }, "kb.email.smtp");
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Settings of Test probe" }));
    expect(await screen.findByText("Files of kb.test.probe")).toBeTruthy();
    expect(screen.getByRole("region", { name: "Libraries" })).toBeTruthy();
    expect(within(screen.getByRole("region", { name: "Broken part" })).getByRole("alert").textContent).toBe("This part of the plugin failed. The rest of the page works.");
    expect(screen.queryByText("Other plugin")).toBeNull();
    expect(screen.getByRole("button", { name: "Disable" })).toBeTruthy();
  });
});

describe("Plugins: sign-in providers an Admin adds", () => {
  const P = "signIn.pa1b2c3d4e5";
  const oidc = plugin({
    id: "kb.oidc", name: "OpenID Connect", origin: "Uploaded", source: "upload", signInProviders: true,
    signInMethods: [{ id: "kb.oidc/pa1b2c3d4e5", name: "Lab", redirectAddress: "https://kb.test/api/v1/auth/methods/kb.oidc/pa1b2c3d4e5/callback", byAdmin: true, linked: 2 }],
    settings: [
      setting({ key: "Own", label: "Own setting" }),
      setting({ key: `${P}.issuer`, label: "Issuer" }),
      setting({ key: `${P}.discovery`, label: "Discovery address" }),
      setting({ key: `${P}.clientId`, label: "Client ID" }),
      setting({ key: `${P}.clientSecret`, label: "Client secret", secret: true }),
      setting({ key: `${P}.scopes`, label: "Scopes", default: "openid email profile" }),
      setting({ key: `${P}.newAccounts`, label: "New accounts", type: "toggle", default: "false" }),
    ],
  });
  const routes = (values: Record<string, string>) => ({
    "GET /plugins": list([oidc]),
    "GET /plugins/kb.oidc/settings": () => ({ body: { values, secretsSet: [] } }),
    "PUT /plugins/kb.oidc/settings": () => ({ body: { restart: false } }),
  });
  const openDialog = async () => {
    show();
    await userEvent.click(await screen.findByText("OpenID Connect"));
    await userEvent.click(within(await screen.findByRole("region", { name: "Sign-in providers" })).getByRole("button", { name: "Settings of Lab" }));
    const dialog = screen.getByRole("dialog", { name: "Lab" });
    await within(dialog).findByLabelText("Issuer (required)");
    return dialog;
  };

  it("lists each provider with its own gear; its settings are under the gear, not in the plugin's form", async () => {
    const calls = server(routes({ Own: "x", [`${P}.issuer`]: "https://id.example.com", [`${P}.clientId`]: "c1" }));
    const dialog = await openDialog();
    expect(screen.getByLabelText("Own setting")).toBeTruthy();
    expect(within(dialog).getByLabelText("Issuer (required)")).toHaveProperty("value", "https://id.example.com");
    expect(within(dialog).getByLabelText("Redirect address of Lab")).toHaveProperty("value", "https://kb.test/api/v1/auth/methods/kb.oidc/pa1b2c3d4e5/callback");
    expect(within(dialog).queryByText(/Not shown on the sign-in screen/)).toBeNull(); // issuer and client ID set

    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    const sent = calls.filter((c) => c.key === "PUT /plugins/kb.oidc/settings").at(-1)?.body as { values: Record<string, string | null> };
    expect(Object.keys(sent.values).sort()).toEqual([`${P}.clientId`, `${P}.clientSecret`, `${P}.discovery`, `${P}.issuer`, `${P}.newAccounts`, `${P}.scopes`]);
  });

  it("marks Issuer and Client ID required, shows them first with the secret, and keeps the rest closed under Advanced", async () => {
    server(routes({}));
    const dialog = await openDialog();
    expect(within(dialog).getByLabelText("Issuer (required)").getAttribute("aria-required")).toBe("true");
    expect(within(dialog).getByLabelText("Client ID (required)").getAttribute("aria-required")).toBe("true");
    const advanced = dialog.querySelector("details.advanced") as HTMLDetailsElement;
    expect(advanced.open).toBe(false);
    expect(advanced.contains(within(dialog).getByLabelText("Discovery address"))).toBe(true);
    expect(advanced.contains(within(dialog).getByLabelText("Scopes"))).toBe(true);
    expect(advanced.contains(within(dialog).getByText("New accounts"))).toBe(false);
    const labels = [...dialog.querySelectorAll(".lbl")].map((l) => l.textContent);
    expect(labels.slice(0, 4)).toEqual(["Issuer (required)", "Client ID (required)", "Client secret", "New accounts"]);
  });

  it("says why a provider has no button, in its row and in its dialog", async () => {
    server(routes({ [`${P}.clientId`]: "c1" }));
    show();
    await userEvent.click(await screen.findByText("OpenID Connect"));
    const section = await screen.findByRole("region", { name: "Sign-in providers" });
    expect(await within(section).findByText("Not shown on the sign-in screen: issuer missing.")).toBeTruthy();
    await userEvent.click(within(section).getByRole("button", { name: "Settings of Lab" }));
    const dialog = screen.getByRole("dialog", { name: "Lab" });
    expect(await within(dialog).findByText("Not shown on the sign-in screen: issuer missing.")).toBeTruthy();
  });

  it("refuses a discovery address with no issuer, saying to put it in Issuer, and opens Advanced to show it", async () => {
    const calls = server(routes({}));
    const dialog = await openDialog();
    const advanced = dialog.querySelector("details.advanced") as HTMLDetailsElement;
    await userEvent.type(within(dialog).getByLabelText("Discovery address"), "https://accounts.example.com");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    expect(within(dialog).getByRole("alert").textContent).toMatch(/Put the provider's address in Issuer/);
    expect(advanced.open).toBe(true);
    expect(calls.some((c) => c.key === "PUT /plugins/kb.oidc/settings")).toBe(false);
  });

  it("Close asks before it drops changed fields", async () => {
    server(routes({}));
    const dialog = await openDialog();
    await userEvent.type(within(dialog).getByLabelText("Issuer (required)"), "https://id.example.com");
    await userEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(screen.getByText("Discard unsaved changes?")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByRole("dialog", { name: "Lab" })).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    await userEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(screen.queryByRole("dialog", { name: "Lab" })).toBeNull();
  });

  it("Add new asks for a title; Remove says how many accounts keep their link", async () => {
    const calls = server({
      ...routes({}),
      "POST /plugins/kb.oidc/signin-providers": () => ({ body: { id: "pf0f0f0f0f0", title: "Second", redirectAddress: "x" } }),
      "DELETE /plugins/kb.oidc/signin-providers/pa1b2c3d4e5": () => ({ status: 204 }),
    });
    show();
    await userEvent.click(await screen.findByText("OpenID Connect"));
    await userEvent.click(await screen.findByRole("button", { name: "Add new" }));
    await userEvent.type(screen.getByLabelText("Title (the text of its button)"), "Second");
    await userEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(calls.find((c) => c.key === "POST /plugins/kb.oidc/signin-providers")?.body).toEqual({ title: "Second" });

    await userEvent.click(screen.getByRole("button", { name: "Settings of Lab" }));
    await userEvent.click(within(screen.getByRole("dialog", { name: "Lab" })).getByRole("button", { name: "Remove" }));
    expect(screen.getByText(/2 accounts are linked to this provider's issuer/)).toBeTruthy();
    expect(calls.some((c) => c.key.startsWith("DELETE"))).toBe(false);
    await userEvent.click(screen.getAllByRole("button", { name: "Remove" }).at(-1)!);
    expect(calls.some((c) => c.key === "DELETE /plugins/kb.oidc/signin-providers/pa1b2c3d4e5")).toBe(true);
  });
});
