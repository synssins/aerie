import { afterEach, describe, expect, it, vi } from "vitest";
import type { Me } from "../api";
import { loadPlugins } from "./loader";
import type { KbHost } from "./host";
import { slots } from "./slots";

const me: Me = { id: "u1", userName: "gail", displayName: "Gail", isAdmin: false, isBreakGlass: false, canAnnounce: false, permissions: [] };

type Call = { method: string; url: string; body: unknown };
function server(plugins: { id: string; version: string; script: string }[]): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ method: init?.method ?? "GET", url, body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined });
    if (url === "/api/v1/ui/plugins") return new Response(JSON.stringify({ uiApiVersion: "1.0.0", plugins }), { status: 200 });
    return new Response(JSON.stringify({ ok: true }), { status: 200 });
  }));
  return calls;
}

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); slots.clear(); });

describe("loadPlugins", () => {
  it("calls each plugin with a host bound to its id", async () => {
    server([{ id: "kb.a", version: "1.0.0", script: "/plugins/kb.a/1.0.0/ui/index.js?v=1" }, { id: "kb.b", version: "2.1.0", script: "/plugins/kb.b/2.1.0/ui/index.js?v=2" }]);
    const hosts: Record<string, KbHost> = {};
    const imported: string[] = [];

    await loadPlugins(me, async (url) => { imported.push(url); return { default: (kb: KbHost) => { hosts[kb.plugin.id] = kb; kb.slots.add("page.tools", { id: "x", label: kb.plugin.id }); } }; });

    expect(imported).toEqual(["/plugins/kb.a/1.0.0/ui/index.js?v=1", "/plugins/kb.b/2.1.0/ui/index.js?v=2"]);
    expect(hosts["kb.b"].plugin).toEqual({ id: "kb.b", version: "2.1.0" });
    expect(hosts["kb.a"].uiApiVersion).toBe("1.0.0");
    expect(hosts["kb.a"].me()).toEqual(me);
    // What a plugin adds is known as that plugin's: the same id from two plugins is two items.
    expect(slots.entries("page.tools", {}).map((e) => [e.source, e.item.id])).toEqual([["kb.a", "x"], ["kb.b", "x"]]);
  });

  it("host.api is rooted at the plugin's routes and host.core at the API", async () => {
    const calls = server([{ id: "kb.a", version: "1.0.0", script: "/s.js" }]);
    let host!: KbHost;
    await loadPlugins(me, async () => ({ default: (kb: KbHost) => { host = kb; } }));

    await host.api.post("/pages/p1/ack");
    await host.core.get("/pages/p1");

    expect(calls.slice(1).map((c) => `${c.method} ${c.url}`)).toEqual(["POST /api/v1/plugins/kb.a/pages/p1/ack", "GET /api/v1/pages/p1"]);
  });

  it("skips a script that throws, reports it, and loads the next", async () => {
    const calls = server([{ id: "kb.bad", version: "1.0.0", script: "/bad.js" }, { id: "kb.none", version: "1.0.0", script: "/none.js" }, { id: "kb.good", version: "1.0.0", script: "/good.js" }]);
    vi.spyOn(console, "error").mockImplementation(() => {});
    const loaded: string[] = [];

    await loadPlugins(me, async (url) => {
      if (url === "/bad.js") return { default: () => { throw new Error("Broken on load."); } };
      if (url === "/none.js") return {};
      return { default: (kb: KbHost) => { loaded.push(kb.plugin.id); } };
    });

    expect(loaded).toEqual(["kb.good"]);
    const reports = calls.filter((c) => c.url === "/api/v1/ui/errors").map((c) => c.body);
    expect(reports).toEqual([{ plugin: "kb.bad", where: "load", message: "Broken on load." }, { plugin: "kb.none", where: "load", message: "The script has no default export." }]);
  });

  it("does nothing when the list cannot be read", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 500 })));
    const importer = vi.fn();
    await loadPlugins(me, importer);
    expect(importer).not.toHaveBeenCalled();
  });
});
