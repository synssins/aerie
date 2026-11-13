import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Brand } from "./api";
import { passwordLinkAt, SetPassword } from "./SetPassword";

const brand: Brand = { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" };

function server(answer: { status: number; body?: unknown }) {
  const calls: { url: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: JSON.parse(String(init?.body)) });
    return new Response(answer.status === 204 ? null : JSON.stringify(answer.body ?? null), { status: answer.status });
  }));
  return calls;
}

async function fill(first: string, second: string) {
  await userEvent.type(screen.getByLabelText("New password"), first);
  await userEvent.type(screen.getByLabelText("Repeat password"), second);
  await userEvent.click(screen.getByRole("button", { name: "Set password" }));
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("SetPassword", () => {
  it("reads the account from the address and the token from the part after #", () => {
    expect(passwordLinkAt("/set-password/0b5c0b1e-6f0e-4a55-9a43-7b1d7a2c9f10", "#tok%2B1%2F2%3D")).toEqual({ userId: "0b5c0b1e-6f0e-4a55-9a43-7b1d7a2c9f10", token: "tok+1/2=" });
    expect(passwordLinkAt("/p/0b5c0b1e-6f0e-4a55-9a43-7b1d7a2c9f10", "#x")).toBeNull();
    expect(passwordLinkAt("/set-password/not-an-id", "#x")).toBeNull();
  });

  it("sends the new password with the link's token and tells when it is done", async () => {
    const calls = server({ status: 204 });
    const done = vi.fn();
    render(<SetPassword brand={brand} userId="u9" token="tok+1" onDone={done} />);

    await fill("a-long-enough-password-1", "a-long-enough-password-1");

    await waitFor(() => expect(done).toHaveBeenCalled());
    expect(calls).toEqual([{ url: "/api/v1/auth/local/password", body: { userId: "u9", token: "tok+1", password: "a-long-enough-password-1" } }]);
  });

  it("does not send two passwords that differ", async () => {
    const calls = server({ status: 204 });
    render(<SetPassword brand={brand} userId="u9" token="t" onDone={() => {}} />);
    await fill("a-long-enough-password-1", "a-long-enough-password-2");
    expect(screen.getByRole("alert").textContent).toBe("The passwords differ.");
    expect(calls).toEqual([]);
  });

  it("shows the server's reason: a link that is not valid, a password that is too short", async () => {
    server({ status: 400, body: { detail: "The link is not valid." } });
    render(<SetPassword brand={brand} userId="u9" token="t" onDone={() => {}} />);
    await fill("a-long-enough-password-1", "a-long-enough-password-1");
    expect((await screen.findByRole("alert")).textContent).toBe("The link is not valid.");
    cleanup();

    server({ status: 400, body: { errors: { password: ["Passwords must be at least 15 characters."] } } });
    render(<SetPassword brand={brand} userId="u9" token="t" onDone={() => {}} />);
    await fill("short", "short");
    expect((await screen.findByRole("alert")).textContent).toBe("Passwords must be at least 15 characters.");
  });
});
