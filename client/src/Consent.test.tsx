import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Brand, Me } from "./api";
import { Consent, consentAt } from "./Consent";

const me = { id: "u1", userName: "ravi", displayName: "Ravi Person", isAdmin: false, isBreakGlass: false, canAnnounce: false } as Me;
const brand = { productName: "Aerie", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#000" } as Brand;
const ID = "0f8fad5b-d9cb-469f-a165-70867728950e";

type Call = { key: string; body: unknown };
function server(routes: Record<string, (body: unknown) => { status?: number; body?: unknown }>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    const a = routes[key]?.(body) ?? { status: 404 };
    return new Response(JSON.stringify(a.body ?? null), { status: a.status ?? 200 });
  }));
  return calls;
}

const assign = vi.fn();
afterEach(() => { cleanup(); vi.unstubAllGlobals(); assign.mockReset(); });
Object.defineProperty(window, "location", { value: { ...window.location, assign }, writable: true });

describe("Consent", () => {
  it("reads the id from the consent link", () => {
    expect(consentAt(`/oauth/consent/${ID}`)).toBe(ID);
    expect(consentAt("/oauth/consent/nope")).toBeNull();
    expect(consentAt("/spaces")).toBeNull();
  });

  it("names the application, the account and what it may do; Allow sends the browser back", async () => {
    const calls = server({
      [`GET /oauth/authorize-requests/${ID}`]: () => ({ body: { id: ID, clientName: "Desk app", scopes: ["READ", "WRITE"], alreadyGranted: [], expiresAt: "2026-11-15T10:10:00Z" } }),
      [`POST /oauth/authorize-requests/${ID}/decision`]: () => ({ body: { redirectTo: "https://app.example/cb?code=abc&state=x" } }),
    });
    render(<Consent id={ID} me={me} brand={brand} />);
    expect((await screen.findByRole("heading")).textContent).toBe("Desk app wants to use your Aerie account");
    expect(screen.getByText(/Ravi Person/)).toBeTruthy();
    expect(screen.getAllByRole("listitem").map((li) => li.textContent)).toEqual(["See what you can see, and your profile", "Create, change and delete what you can change"]);
    await userEvent.click(screen.getByRole("button", { name: "Allow" }));
    expect(calls.find((c) => c.key.startsWith("POST"))?.body).toEqual({ allow: true });
    expect(assign).toHaveBeenCalledWith("https://app.example/cb?code=abc&state=x");
  });

  it("Deny sends the refusal", async () => {
    const calls = server({
      [`GET /oauth/authorize-requests/${ID}`]: () => ({ body: { id: ID, clientName: "Desk app", scopes: ["READ"], alreadyGranted: [], expiresAt: "2026-11-15T10:10:00Z" } }),
      [`POST /oauth/authorize-requests/${ID}/decision`]: () => ({ body: { redirectTo: "https://app.example/cb?error=access_denied" } }),
    });
    render(<Consent id={ID} me={me} brand={brand} />);
    await userEvent.click(await screen.findByRole("button", { name: "Deny" }));
    expect(calls.find((c) => c.key.startsWith("POST"))?.body).toEqual({ allow: false });
    expect(assign).toHaveBeenCalledWith("https://app.example/cb?error=access_denied");
  });

  it("allows without asking when every scope was granted before", async () => {
    const calls = server({
      [`GET /oauth/authorize-requests/${ID}`]: () => ({ body: { id: ID, clientName: "Desk app", scopes: ["READ"], alreadyGranted: ["READ", "WRITE"], expiresAt: "2026-11-15T10:10:00Z" } }),
      [`POST /oauth/authorize-requests/${ID}/decision`]: () => ({ body: { redirectTo: "https://app.example/cb?code=abc" } }),
    });
    render(<Consent id={ID} me={me} brand={brand} />);
    await vi.waitFor(() => expect(assign).toHaveBeenCalledWith("https://app.example/cb?code=abc"));
    expect(calls.filter((c) => c.key.startsWith("POST"))).toHaveLength(1);
  });

  it("says so when the request is gone", async () => {
    server({});
    render(<Consent id={ID} me={me} brand={brand} />);
    expect((await screen.findByRole("alert")).textContent).toContain("expired");
    expect(screen.queryByRole("button", { name: "Allow" })).toBeNull();
  });
});
