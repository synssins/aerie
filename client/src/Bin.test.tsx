import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { BinPageView, BinTree } from "./Bin";
import { DialogProvider } from "./Dialogs";
import { editorExtensions } from "./editor/extensions";
import { BinTable } from "./Views";

type Call = { key: string };
function server(routes: Record<string, () => { status?: number; body?: unknown }>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    calls.push({ key });
    const a = routes[key]?.() ?? { status: 404 };
    return new Response(JSON.stringify(a.body ?? null), { status: a.status ?? 200 });
  }));
  return calls;
}

const node = (id: string, parentId: string | null, spaceId: string, kind: "Folder" | "Page", title: string) => ({ id, parentId, spaceId, kind, title, sort: 0 });
const DOC = JSON.stringify({ type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "restart the spooler" }] }] });
const binPage = (p: object = {}) => ({
  id: "p1", spaceId: "s1", kind: "Page", title: "Spooler", contentJson: DOC, path: ["Service Desk", "Printers"],
  updatedAt: "2026-11-12T10:00:00Z", updatedBy: "Sue", deletedAt: "2026-11-13T10:00:00Z", deletedBy: "Pat", restoreId: "f1", children: [], ...p,
});

afterEach(() => { cleanup(); editorExtensions.clear(); vi.unstubAllGlobals(); });

describe("BinTree", () => {
  it("shows a space's bin as folders that open and pages that are picked", async () => {
    server({ "GET /spaces/s1/bin/tree": () => ({ body: [node("f1", null, "s1", "Folder", "Printers"), node("p1", "f1", "s1", "Page", "Spooler")] }) });
    const onSelect = vi.fn();
    render(<BinTree spaceId="s1" selectedId={null} onSelect={onSelect} />);

    const folder = await screen.findByRole("button", { name: "Printers" });
    expect(screen.queryByRole("button", { name: "Spooler" })).toBeNull(); // closed, as a folder of the knowledge base is
    await userEvent.click(folder);
    await userEvent.click(screen.getByRole("button", { name: "Spooler" }));

    expect(onSelect).toHaveBeenLastCalledWith("p1");
  });

  it("says Empty when nothing is in the bin", async () => {
    server({ "GET /spaces/s1/bin/tree": () => ({ body: [] }) });
    render(<BinTree spaceId="s1" selectedId={null} onSelect={() => {}} />);
    expect(await screen.findByText("Empty")).toBeTruthy();
  });

  it("the admin bin: each space with what is in its bin, and a deleted space with everything under it", async () => {
    server({
      "GET /admin/bin/tree": () => ({ body: [node("p9", null, "live", "Page", "Old note"), node("f1", null, "gone", "Folder", "Printers"), node("p1", "f1", "gone", "Page", "Spooler")] }),
      "GET /admin/spaces/deleted": () => ({ body: [{ id: "gone", key: "desk", name: "Service Desk", isSensitive: false }, { id: "vault", key: "vault", name: "Vault", isSensitive: true }] }),
    });
    const onSelect = vi.fn();
    render(<BinTree spaceId={null} spaces={[{ id: "live", name: "Docs" }, { id: "quiet", name: "Nothing deleted here" }]} selectedId={null} onSelect={onSelect} />);

    const gone = await screen.findByRole("button", { name: /^Service Desk/ });
    expect(within(gone).getByLabelText("Deleted space")).toBeTruthy();
    await userEvent.click(gone);
    await userEvent.click(screen.getByRole("button", { name: "Printers" }));
    await userEvent.click(screen.getByRole("button", { name: "Spooler" }));
    expect(onSelect).toHaveBeenLastCalledWith("p1");

    await userEvent.click(screen.getByRole("button", { name: "Docs" }));
    expect(screen.getByRole("button", { name: "Old note" })).toBeTruthy();
    // A deleted sensitive space is named, and has nothing to open. A space with an empty bin is not listed.
    const vault = screen.getByRole("button", { name: /^Vault/ }) as HTMLButtonElement;
    expect(within(vault).getByLabelText("Sensitive")).toBeTruthy();
    expect(vault.disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Nothing deleted here" })).toBeNull();
  });

  it("loads again when told to", async () => {
    const calls = server({ "GET /spaces/s1/bin/tree": () => ({ body: [] }) });
    const { rerender } = render(<BinTree spaceId="s1" selectedId={null} reloadKey={0} onSelect={() => {}} />);
    await waitFor(() => expect(calls).toHaveLength(1));
    rerender(<BinTree spaceId="s1" selectedId={null} reloadKey={1} onSelect={() => {}} />);
    await waitFor(() => expect(calls).toHaveLength(2));
  });
});

describe("BinTable", () => {
  it("opens a bin item from its name", async () => {
    server({ "GET /spaces/s1/bin": () => ({ body: [{ id: "f1", spaceId: "s1", spaceName: "Service Desk", kind: "Folder", title: "Printers", location: "Service Desk", items: 2, deletedAt: "2026-11-13T10:00:00Z", deletedBy: "Pat", hidden: false }] }) });
    const onOpen = vi.fn();
    render(<DialogProvider><BinTable spaceId="s1" canEmpty onRestored={() => {}} onOpen={onOpen} /></DialogProvider>);

    await userEvent.click(await screen.findByRole("button", { name: "Printers" }));

    expect(onOpen).toHaveBeenCalledWith("f1");
  });
});

describe("BinPageView", () => {
  it("shows a deleted page to read, where it was, and who deleted it", async () => {
    server({ "GET /bin/pages/p1": () => ({ body: binPage() }) });
    const { container } = render(<BinPageView id="p1" onHome={() => {}} onBin={() => {}} onOpen={() => {}} onRestored={() => {}} />);

    expect(await screen.findByRole("heading", { name: "Spooler" })).toBeTruthy();
    await waitFor(() => expect(container.querySelector(".prose")?.textContent).toBe("restart the spooler"));
    expect(container.querySelector(".prose")?.getAttribute("contenteditable")).toBe("false");
    const crumb = screen.getByRole("navigation", { name: "Breadcrumb" }).textContent;
    expect(crumb).toBe("Recycle binService DeskPrintersSpooler");
    const side = screen.getByRole("complementary", { name: "Details" }).textContent!;
    expect(side).toContain("Pat");
    expect(side).toContain("Sue");
  });

  it("Restore brings back the bin item the page belongs to", async () => {
    const calls = server({ "GET /bin/pages/p1": () => ({ body: binPage() }), "POST /pages/f1/restore": () => ({ body: { id: "f1", spaceId: "s1" } }) });
    const onRestored = vi.fn();
    render(<BinPageView id="p1" onHome={() => {}} onBin={() => {}} onOpen={() => {}} onRestored={onRestored} />);

    await userEvent.click(await screen.findByRole("button", { name: "Restore" }));

    await waitFor(() => expect(onRestored).toHaveBeenCalledWith("s1"));
    expect(calls.map((c) => c.key)).toEqual(["GET /bin/pages/p1", "POST /pages/f1/restore"]);
  });

  it("a page of a deleted space has no Restore of its own", async () => {
    server({ "GET /bin/pages/p1": () => ({ body: binPage({ restoreId: null }) }) });
    render(<BinPageView id="p1" onHome={() => {}} onBin={() => {}} onOpen={() => {}} onRestored={() => {}} />);
    await screen.findByRole("heading", { name: "Spooler" });
    expect(screen.queryByRole("button", { name: "Restore" })).toBeNull();
  });

  it("a folder lists what is in it, and a listed item opens", async () => {
    server({ "GET /bin/pages/f1": () => ({ body: binPage({ id: "f1", kind: "Folder", title: "Printers", contentJson: "", path: ["Service Desk"], children: [node("p1", "f1", "s1", "Page", "Spooler")] }) }) });
    const onOpen = vi.fn();
    const { container } = render(<BinPageView id="f1" onHome={() => {}} onBin={() => {}} onOpen={onOpen} onRestored={() => {}} />);

    await userEvent.click(await screen.findByRole("button", { name: "Spooler" }));

    expect(onOpen).toHaveBeenCalledWith("p1");
    expect(container.querySelector(".prose")).toBeNull(); // a folder has no text of its own
  });

  it("goes back to the bin when the item is not there", async () => {
    server({});
    const onBin = vi.fn();
    render(<BinPageView id="p1" onHome={() => {}} onBin={onBin} onOpen={() => {}} onRestored={() => {}} />);
    await waitFor(() => expect(onBin).toHaveBeenCalled());
  });
});
