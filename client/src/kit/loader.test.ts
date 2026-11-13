import { afterEach, describe, expect, it, vi } from "vitest";
import type { Me } from "../api";
import { loadPlugins, pluginsReady } from "./loader";
import { navigation, session, type KbHost } from "./host";
import { current } from "./current";
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

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); slots.clear(); session.me = null; });

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

  it("a plugin whose start never ends does not hold up the next one, and a start that fails later is reported", async () => {
    const calls = server([{ id: "kb.slow", version: "1.0.0", script: "/slow.js" }, { id: "kb.late", version: "1.0.0", script: "/late.js" }, { id: "kb.good", version: "1.0.0", script: "/good.js" }]);
    vi.spyOn(console, "error").mockImplementation(() => {});
    const loaded: string[] = [];

    await loadPlugins(me, async (url) => {
      if (url === "/slow.js") return { default: () => new Promise<void>(() => {}) };
      if (url === "/late.js") return { default: async () => { await Promise.resolve(); throw new Error("Failed after it started."); } };
      return { default: (kb: KbHost) => { loaded.push(kb.plugin.id); } };
    });

    expect(loaded).toEqual(["kb.good"]);
    await vi.waitFor(() => expect(calls.filter((c) => c.url === "/api/v1/ui/errors").map((c) => c.body)).toEqual([{ plugin: "kb.late", where: "load", message: "Failed after it started." }]));
  });

  it("kb.me() is the person signed in now, not the one the scripts were loaded for", async () => {
    server([{ id: "kb.a", version: "1.0.0", script: "/s.js" }]);
    let host!: KbHost;
    await loadPlugins(me, async () => ({ default: (kb: KbHost) => { host = kb; } }));
    expect(host.me().userName).toBe("gail");

    // Gail signs out and Olga signs in, in the same tab: the scripts are not loaded again.
    session.me = { ...me, id: "u2", userName: "olga", permissions: ["kb.a:run"] };

    expect(host.me().userName).toBe("olga");
    expect(host.me().permissions).toEqual(["kb.a:run"]);
  });

  it("kb.navigate asks the app's shell, which may refuse to leave an edit", async () => {
    server([{ id: "kb.a", version: "1.0.0", script: "/s.js" }]);
    let host!: KbHost;
    await loadPlugins(me, async () => ({ default: (kb: KbHost) => { host = kb; } }));
    const before = navigation.go;
    const asked: string[] = [];
    navigation.go = (path) => { asked.push(path); };
    try {
      host.navigate("/x/kb.a/reports");
      expect(asked).toEqual(["/x/kb.a/reports"]);
      expect(location.pathname).not.toBe("/x/kb.a/reports"); // the shell decides, not the plugin
    } finally { navigation.go = before; }
  });

  it("a listener of kb.current that throws is reported, and the others still hear", async () => {
    const calls = server([{ id: "kb.a", version: "1.0.0", script: "/a.js" }, { id: "kb.b", version: "1.0.0", script: "/b.js" }]);
    vi.spyOn(console, "error").mockImplementation(() => {});
    const hosts: KbHost[] = [];
    await loadPlugins(me, async () => ({ default: (kb: KbHost) => { hosts.push(kb); } }));
    let heard = 0;
    hosts[0].current.subscribe(() => { throw new Error("No page."); });
    hosts[1].current.subscribe(() => { heard++; });

    expect(() => current.set({ page: null, space: { id: "s1" } as never })).not.toThrow();

    expect(heard).toBe(1);
    await vi.waitFor(() => expect(calls.filter((c) => c.url === "/api/v1/ui/errors").map((c) => c.body)).toEqual([{ plugin: "kb.a", where: "current", message: "No page." }]));
    current.set({ page: null, space: null });
  });

  it("tells when the scripts have been loaded, also when the list cannot be read", async () => {
    pluginsReady.set(false);
    server([{ id: "kb.a", version: "1.0.0", script: "/s.js" }]);
    const loading = loadPlugins(me, async () => ({ default: () => {} }));
    expect(pluginsReady.done()).toBe(false);
    await loading;
    expect(pluginsReady.done()).toBe(true);

    pluginsReady.set(false);
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 500 })));
    await loadPlugins(me, vi.fn());
    expect(pluginsReady.done()).toBe(true);
  });

  it("does nothing when the list cannot be read", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 500 })));
    const importer = vi.fn();
    await loadPlugins(me, importer);
    expect(importer).not.toHaveBeenCalled();
  });
});
