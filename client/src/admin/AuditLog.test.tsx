import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AuditLog, auditQuery, type AuditRow } from "./AuditLog";

const row = (n: number, r: Partial<AuditRow> = {}): AuditRow => ({ sequence: n, at: "2026-11-14T10:00:00Z", action: "page.delete", actor: "Bea", targetType: "page", targetId: "p1", via: "Web", token: null, ...r });

function serve(entries: AuditRow[]) {
  const urls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    urls.push(url.replace("/api/v1", ""));
    if (url.includes("/admin/users")) return new Response(JSON.stringify([{ id: "u1", userName: "bea", displayName: "Bea" }]), { status: 200 });
    if (url.includes("/admin/tokens")) return new Response(JSON.stringify([{ id: "t1", name: "Build agent", owner: "Bea" }]), { status: 200 });
    const q = new URL(url, "https://x").searchParams;
    const take = Number(q.get("take")), offset = Number(q.get("offset"));
    return new Response(JSON.stringify(entries.slice(offset, offset + take)), { status: 200 });
  }));
  return urls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("AuditLog", () => {
  it("builds the query from only the filters that are set", () => {
    expect(auditQuery({ action: " page. ", actor: "u1", token: "", from: "2026-11-01", to: "" }, 26, 25))
      .toBe("/admin/audit?take=26&offset=25&action=page.&actor=u1&from=2026-11-01");
  });

  it("shows Via and the token's name, and pages by the chosen size", async () => {
    const urls = serve(Array.from({ length: 30 }, (_, i) => row(100 - i, i === 0 ? { via: "API", token: "Build agent" } : {})));
    render(<AuditLog />);
    const first = await screen.findByRole("cell", { name: "100" });
    expect(first.closest("tr")!.textContent).toContain("APIBuild agent");
    expect(screen.getAllByRole("row")).toHaveLength(26); // header and 25
    await userEvent.selectOptions(screen.getByLabelText("Per page"), "10");
    await waitFor(() => expect(screen.getAllByRole("row")).toHaveLength(11));
    await userEvent.click(screen.getByRole("button", { name: "Next" }));
    expect(await screen.findByRole("cell", { name: "90" })).toBeTruthy();
    expect(urls).toContain("/admin/audit?take=11&offset=10");
    expect((screen.getByRole("button", { name: "Previous" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("filters by person and by API token", async () => {
    const urls = serve([row(1)]);
    render(<AuditLog />);
    await screen.findByRole("option", { name: "Bea" });
    await userEvent.selectOptions(screen.getByLabelText("Person"), "Bea");
    await userEvent.selectOptions(screen.getByLabelText("API token"), "Build agent · Bea");
    await waitFor(() => expect(urls).toContain("/admin/audit?take=26&offset=0&actor=u1&token=t1"));
  });
});
