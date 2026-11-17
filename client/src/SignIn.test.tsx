import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { Brand } from "./api";
import { SignIn } from "./SignIn";

const brand: Brand = { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" };

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function server(routes: Record<string, unknown>) {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const key = url.replace("/api/v1", "");
    return key in routes ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response(null, { status: 404 });
  }));
}

describe("SignIn", () => {
  it("shows the brand's sign-in note when there is one", () => {
    render(<SignIn brand={{ ...brand, signInNote: "Read-Only Preview: demo/demo" }} onSignedIn={() => {}} />);
    expect(screen.getByText("Read-Only Preview: demo/demo")).toBeTruthy();
  });

  it("shows no note when the brand has none", () => {
    const { container } = render(<SignIn brand={brand} onSignedIn={() => {}} />);
    expect(container.querySelector(".muted")).toBeNull();
  });

  it("shows a button for each sign-in method, which starts it and comes back here", async () => {
    server({ "/auth/methods": [{ id: "kb.x/corp", name: "Sign in with Corp ID" }], "/auth/methods/outcome": { message: null, step: null } });
    render(<SignIn brand={brand} onSignedIn={() => {}} />);
    const button = await screen.findByRole("link", { name: "Sign in with Corp ID" });
    expect(button.getAttribute("href")).toBe(`/api/v1/auth/methods/kb.x/corp/start?returnTo=${encodeURIComponent(location.pathname + location.search + location.hash)}`);
    expect(screen.getByLabelText("User name")).toBeTruthy(); // the password sign-in stays
  });

  it("shows no buttons when there are no methods", async () => {
    server({ "/auth/methods": [], "/auth/methods/outcome": { message: null, step: null } });
    render(<SignIn brand={brand} onSignedIn={() => {}} />);
    await Promise.resolve();
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("shows what a refused sign-in left", async () => {
    server({ "/auth/methods": [], "/auth/methods/outcome": { message: "Sign in with your account at corp.example.", step: null } });
    render(<SignIn brand={brand} onSignedIn={() => {}} />);
    expect((await screen.findByRole("alert")).textContent).toBe("Sign in with your account at corp.example.");
  });

  it("asks for the one-time code when a method's sign-in is waiting for it", async () => {
    server({ "/auth/methods": [], "/auth/methods/outcome": { message: null, step: "code" } });
    render(<SignIn brand={brand} onSignedIn={() => {}} />);
    expect(await screen.findByLabelText("Code or recovery code")).toBeTruthy();
  });
});
