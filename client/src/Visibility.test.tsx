import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { Space } from "./api";
import { App } from "./App";
import { SpaceEditor } from "./AdminView";
import { DialogProvider } from "./Dialogs";
import { PageView } from "./PageView";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
window.matchMedia ??= ((q: string) => ({ matches: false, media: q, addEventListener: () => {}, removeEventListener: () => {} })) as never;
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

const brand = { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" };
const space = (s: Partial<Space> = {}): Space => ({
  id: "s1", key: "help", name: "Help", description: null, isSensitive: false, visibility: "Private", myRole: "Reader",
  commentsEnabled: true, agingDays: 180, agingAudience: "Everyone", recycleDays: null, createdAt: "2026-11-01T10:00:00Z", ...s,
} as Space);

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

afterEach(() => { cleanup(); vi.unstubAllGlobals(); history.replaceState(null, "", "/"); });

describe("spaces open to Anyone", () => {
  it("someone not signed in reads them, with a Sign in button and nothing else of a signed-in person", async () => {
    const calls = server({
      "GET /brand": () => ({ body: brand }),
      "GET /setup/status": () => ({ body: { required: false, codeRequired: false } }),
      "GET /me": () => ({ status: 401 }),
      "GET /spaces": () => ({ body: [space({ visibility: "Anyone" })] }),
      "GET /spaces/s1/tree": () => ({ body: [] }),
    });
    render(<DialogProvider><App /></DialogProvider>);

    expect(await screen.findByRole("button", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Notifications/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Settings" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Account menu" })).toBeNull();
    expect(calls.map((c) => c.key)).not.toContain("GET /me/settings");
    expect(calls.map((c) => c.key)).not.toContain("GET /notifications/count");

    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByLabelText("User name")).toBeTruthy();
  });

  it("with no space open to Anyone, the sign-in page shows as before", async () => {
    server({
      "GET /brand": () => ({ body: brand }),
      "GET /setup/status": () => ({ body: { required: false, codeRequired: false } }),
      "GET /me": () => ({ status: 401 }),
      "GET /spaces": () => ({ body: [] }),
    });
    render(<DialogProvider><App /></DialogProvider>);
    expect(await screen.findByLabelText("User name")).toBeTruthy();
  });

  it("a page read without sign-in has no History and no Export", () => {
    server({});
    const page = { id: "p1", spaceId: "s1", parentId: null, kind: "Page", title: "Hello", contentJson: JSON.stringify({ type: "doc", content: [] }), version: "1.0",
      updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Pat", path: [], history: [], canEdit: false, tags: [], canDelete: false, draft: null, attachments: [], maxAttachmentBytes: 1000 };
    render(<DialogProvider><PageView page={page as never} spaceName="Help" role="Reader" guest onSaved={() => {}} onOpen={() => {}} onHome={() => {}} onDirty={() => {}} onHistory={() => {}} onMove={() => {}} onDelete={() => {}} /></DialogProvider>);
    expect(screen.queryByRole("button", { name: "History" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Export" })).toBeNull();
  });
});

describe("Visibility in space settings", () => {
  function show(isAdmin: boolean, s = space()) {
    const onSaved = vi.fn(async () => {});
    render(<DialogProvider><SpaceEditor space={s} isAdmin={isAdmin} area="Administration" onBack={() => {}} onSaved={onSaved} onHome={() => {}} onDeleted={() => {}} /></DialogProvider>);
    return onSaved;
  }

  it("an Admin chooses Anyone after an in-app question, and it is saved", async () => {
    const calls = server({ "GET /spaces/s1/tree": () => ({ body: [] }), "PATCH /admin/spaces/s1": () => ({ body: space({ visibility: "Anyone" }) }) });
    const onSaved = show(true);
    await userEvent.click(screen.getByRole("tab", { name: "Space settings" }));
    await userEvent.click(screen.getByRole("radio", { name: "Anyone" }));
    expect(screen.getByText("Open “Help” to anyone, without sign-in?")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Anyone" }));
    expect(screen.getByRole("radio", { name: "Anyone" }).getAttribute("aria-checked")).toBe("true");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(calls.find((c) => c.key === "PATCH /admin/spaces/s1")?.body).toMatchObject({ visibility: "Anyone" });
  });

  it("cancelling the question leaves it as it was; an Owner has no Visibility control", async () => {
    server({ "GET /spaces/s1/tree": () => ({ body: [] }) });
    show(true);
    await userEvent.click(screen.getByRole("tab", { name: "Space settings" }));
    await userEvent.click(screen.getByRole("radio", { name: "Anyone" }));
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
    expect(screen.getByRole("radio", { name: "Private" }).getAttribute("aria-checked")).toBe("true");
    cleanup();

    show(false);
    await userEvent.click(screen.getByRole("tab", { name: "Space settings" }));
    expect(screen.queryByRole("radiogroup", { name: "Visibility" })).toBeNull();
  });
});
