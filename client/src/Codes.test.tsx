import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Brand } from "./api";
import { SignIn } from "./SignIn";
import { CodeSettingsPanel } from "./admin/CodeSettings";
import { MyCodesPanel } from "./SettingsView";
import { grouped } from "./Codes";

const brand: Brand = { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" };
const me = { id: "u1", userName: "ada", displayName: "Ada", isAdmin: true, isBreakGlass: false };

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

async function password() {
  await userEvent.type(screen.getByLabelText("User name"), "ada");
  await userEvent.type(screen.getByLabelText("Password"), "a-long-enough-password");
  await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("one-time codes", () => {
  it("groups the key in fours", () => expect(grouped("ABCDEFGHIJ")).toBe("ABCD EFGH IJ"));

  it("asks for a code after the password, and signs in with it", async () => {
    const calls = server({
      "POST /auth/local/signin": () => ({ status: 409, body: { step: "code" } }),
      "POST /auth/local/code": (b) => (b as { code: string }).code === "123456" ? { body: me } : { status: 401 },
    });
    const signedIn = vi.fn();
    render(<SignIn brand={brand} onSignedIn={signedIn} />);
    await password();
    expect(signedIn).not.toHaveBeenCalled();

    await userEvent.type(await screen.findByLabelText("Code or recovery code"), "000000");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Wrong code.");

    await userEvent.clear(screen.getByLabelText("Code or recovery code"));
    await userEvent.type(screen.getByLabelText("Code or recovery code"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(signedIn).toHaveBeenCalledWith(me));
    // The sign-in screen also reads its sign-in methods (GET); the password and the codes are these posts.
    expect(calls.map((c) => c.key).filter((k) => k.startsWith("POST"))).toEqual(["POST /auth/local/signin", "POST /auth/local/code", "POST /auth/local/code"]);
  });

  it("sets up codes after the password: QR and key, one code, then the recovery codes once", async () => {
    const recoveryCodes = Array.from({ length: 10 }, (_, i) => `AAAA${i}-BBBB${i}`);
    server({
      "POST /auth/local/signin": () => ({ status: 409, body: { step: "enroll" } }),
      "POST /auth/codes/key": () => ({ body: { key: "JBSWY3DPEHPK3PXP", uri: "otpauth://totp/KB:ada?secret=JBSWY3DPEHPK3PXP&issuer=KB&digits=6" } }),
      "POST /auth/codes": () => ({ body: { recoveryCodes, me } }),
    });
    const signedIn = vi.fn();
    render(<SignIn brand={brand} onSignedIn={signedIn} />);
    await password();

    expect(await screen.findByText("JBSW Y3DP EHPK 3PXP")).toBeTruthy();
    expect((await screen.findByAltText("QR code")).getAttribute("src")).toMatch(/^data:image\/png/);
    await userEvent.type(screen.getByLabelText("Code"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Confirm" }));

    const list = await screen.findByRole("list", { name: "Recovery codes" });
    expect(list.querySelectorAll("li")).toHaveLength(10);
    expect(signedIn).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Continue" }));
    expect(signedIn).toHaveBeenCalledWith(me);
  });

  it("own settings: required codes cannot be turned off", async () => {
    server({ "GET /me/codes": () => ({ body: { on: true, required: true, allowed: true } }) });
    render(<MyCodesPanel />);
    const box = await screen.findByRole("checkbox", { name: "Use codes" });
    expect((box as HTMLInputElement).checked).toBe(true);
    expect((box as HTMLInputElement).disabled).toBe(true);
  });

  it("own settings: an account that cannot use codes cannot turn them on", async () => {
    server({ "GET /me/codes": () => ({ body: { on: false, required: false, allowed: false } }) });
    render(<MyCodesPanel />);
    expect(((await screen.findByRole("checkbox", { name: "Use codes" })) as HTMLInputElement).disabled).toBe(true);
  });

  it("own settings: turning codes off", async () => {
    let on = true;
    const calls = server({ "GET /me/codes": () => ({ body: { on, required: false, allowed: true } }), "DELETE /me/codes": () => { on = false; return { status: 204 }; } });
    render(<MyCodesPanel />);
    await userEvent.click(await screen.findByRole("checkbox", { name: "Use codes" }));
    await waitFor(() => expect((screen.getByRole("checkbox", { name: "Use codes" }) as HTMLInputElement).checked).toBe(false));
    expect(calls.map((c) => c.key)).toContain("DELETE /me/codes");
  });

  it("Administration: Require codes for Admins or Everyone", async () => {
    const calls = server({ "GET /admin/settings/codes": () => ({ body: { requiredFor: "admins" } }), "PUT /admin/settings/codes": () => ({ status: 204 }) });
    render(<CodeSettingsPanel />);
    const everyone = await screen.findByRole("radio", { name: "Everyone" });
    expect(screen.getByRole("radio", { name: "Admins" }).getAttribute("aria-checked")).toBe("true");
    await userEvent.click(everyone);
    await waitFor(() => expect(calls).toContainEqual({ key: "PUT /admin/settings/codes", body: { requiredFor: "everyone" } }));
    expect(everyone.getAttribute("aria-checked")).toBe("true");
  });
});
