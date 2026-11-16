import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import { ApplicationsPanel, CompatSettingsPanel, type OAuthClient } from "./Applications";

const client = (c: Partial<OAuthClient>): OAuthClient => ({
  id: "c1", clientId: "AbCdEfGhIjKlMnOpQrStUvWxYz012345", name: "Desk app", type: "confidential", redirectUris: ["https://app.example/cb"], scopes: ["READ", "WRITE"],
  requirePkce: true, enabled: true, activeGrants: 2, createdBy: "boss", createdAt: "2026-11-15T10:00:00Z", secretRotatedAt: null, ...c,
});

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

const show = () => render(<DialogProvider><ApplicationsPanel /></DialogProvider>);

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("Applications", () => {
  it("lists clients without any secret", async () => {
    server({ "GET /admin/oauth/clients": () => ({ body: [client({}), client({ id: "c2", name: "CLI", type: "public", enabled: false })] }) });
    show();
    const rows = (await screen.findAllByRole("row")).slice(1);
    expect(within(rows[0]).getAllByRole("cell").slice(0, 5).map((c) => c.textContent)).toEqual(["Desk app", "AbCdEfGhIjKlMnOpQrStUvWxYz012345", "Confidential", "READ, WRITE", "2"]);
    expect(within(rows[0]).getByRole("button", { name: "Rotate secret" })).toBeTruthy();
    expect(within(rows[1]).queryByRole("button", { name: "Rotate secret" })).toBeNull(); // a public client has no secret
    expect(within(rows[1]).getByText("Disabled")).toBeTruthy();
    expect(screen.queryByLabelText("Client secret")).toBeNull();
  });

  it("creates a client and shows its secret once", async () => {
    const calls = server({
      "GET /admin/oauth/clients": () => ({ body: [] }),
      "POST /admin/oauth/clients": () => ({ status: 201, body: { clientSecret: "s3cr3t", client: client({}) } }),
    });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Add application" }));
    await userEvent.type(screen.getByLabelText("Name"), "Desk app");
    await userEvent.type(screen.getByLabelText("Redirect URIs"), "https://app.example/cb");
    await userEvent.click(screen.getByRole("checkbox", { name: "WRITE" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "POST /admin/oauth/clients")?.body).toEqual({ name: "Desk app", type: "confidential", redirectUris: ["https://app.example/cb"], scopes: ["READ", "WRITE"], requirePkce: true });
    expect((await screen.findByLabelText("Client secret", { selector: "input" }) as HTMLInputElement).value).toBe("s3cr3t");
    expect(screen.getByText(/You will not see this secret again/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByLabelText("Client secret", { selector: "input" })).toBeNull();
  });

  it("shows a field the server refused", async () => {
    server({
      "GET /admin/oauth/clients": () => ({ body: [] }),
      "POST /admin/oauth/clients": () => ({ status: 400, body: { errors: { redirectUris: ["http://example.com/cb: http is allowed only on a loopback host"] } } }),
    });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Add application" }));
    await userEvent.type(screen.getByLabelText("Name"), "Bad");
    await userEvent.type(screen.getByLabelText("Redirect URIs"), "http://example.com/cb");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect((await screen.findByRole("alert")).textContent).toContain("loopback");
  });

  it("rotates a secret after confirming, and deletes only after confirming", async () => {
    const calls = server({
      "GET /admin/oauth/clients": () => ({ body: [client({})] }),
      "POST /admin/oauth/clients/c1/secret": () => ({ body: { clientSecret: "n3w", client: client({}) } }),
      "DELETE /admin/oauth/clients/c1": () => ({ status: 204 }),
    });
    show();
    await userEvent.click(await screen.findByRole("button", { name: "Rotate secret" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Rotate" }));
    expect((await screen.findByLabelText("Client secret", { selector: "input" }) as HTMLInputElement).value).toBe("n3w");
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(calls.some((c) => c.key === "DELETE /admin/oauth/clients/c1")).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Delete" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Delete" }));
    expect(calls.some((c) => c.key === "DELETE /admin/oauth/clients/c1")).toBe(true);
  });

  it("saves the compatible sign-in settings", async () => {
    const settings = { basicPasswordEnabled: true, allowHttpRedirects: false, codeSeconds: 300, accessTokenSeconds: 3600, refreshTokenDays: 90, grantMaxDays: null };
    const calls = server({ "GET /admin/auth/compat": () => ({ body: settings }), "PUT /admin/auth/compat": (b) => ({ body: b }) });
    render(<CompatSettingsPanel />);
    await userEvent.click(await screen.findByRole("checkbox", { name: /Allow a password in HTTP Basic/ }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(calls.find((c) => c.key === "PUT /admin/auth/compat")?.body).toEqual({ ...settings, basicPasswordEnabled: false });
    expect((await screen.findByRole("status")).textContent).toBe("Saved.");
  });
});
