import { useSyncExternalStore, type ReactNode } from "react";

/** A screen of a plugin's own, at /x/<plugin id>/<path>. `rest` is what follows the path. `panel` names the slot drawn in the right panel. */
export type ScreenDef = { path: string; title: string; render: (rest: string) => ReactNode; panel?: string };

const registry = new Map<string, ScreenDef[]>();
const listeners = new Set<() => void>();
let version = 0;
const changed = () => { version++; listeners.forEach((l) => l()); };
const clean = (p: string) => p.replace(/^\/+|\/+$/g, "");

export const screens = {
  add(plugin: string, def: ScreenDef): void {
    const path = clean(def.path);
    registry.set(plugin, [...(registry.get(plugin) ?? []).filter((d) => d.path !== path), { ...def, path }]);
    changed();
  },
  /** The plugin's screen for this address: the one with the longest path that the address starts with. */
  find(plugin: string, rest: string): { def: ScreenDef; rest: string } | null {
    const at = clean(rest);
    const def = (registry.get(plugin) ?? [])
      .filter((d) => d.path === "" || at === d.path || at.startsWith(d.path + "/"))
      .sort((a, b) => b.path.length - a.path.length)[0];
    return def ? { def, rest: clean(at.slice(def.path.length)) } : null;
  },
  /** For tests. */
  clear(): void { registry.clear(); changed(); },
};

export function useScreen(plugin: string, rest: string): { def: ScreenDef; rest: string } | null {
  useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => version);
  return screens.find(plugin, rest);
}
