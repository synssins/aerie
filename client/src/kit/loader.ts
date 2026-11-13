import { useSyncExternalStore } from "react";
import { api, type Me } from "../api";
import { reportUiError } from "./errors";
import { makeHost, session, type KbHost } from "./host";

type UiPlugins = { uiApiVersion: string; plugins: { id: string; version: string; script: string }[] };
type PluginModule = { default?: (kb: KbHost) => void | Promise<void> };

let ready = false;
const listeners = new Set<() => void>();
/** Whether every plugin's script has been given its start. Until then an address of a plugin's screen may just not be known yet. */
export const pluginsReady = {
  done: (): boolean => ready,
  set(value: boolean): void { ready = value; listeners.forEach((l) => l()); },
  subscribe(fn: () => void): () => void { listeners.add(fn); return () => listeners.delete(fn); },
};
export const usePluginsReady = (): boolean => useSyncExternalStore(pluginsReady.subscribe, pluginsReady.done);

/**
 * Loads the script of every plugin that has one and hands each its host. A script that fails to load or throws is skipped and
 * reported; the app carries on, and the next plugin still loads.
 */
export async function loadPlugins(me: Me, importer: (url: string) => Promise<PluginModule> = (url) => import(/* @vite-ignore */ url)): Promise<void> {
  session.me ??= me;
  try {
    let list: UiPlugins;
    try { list = await api.get<UiPlugins>("/ui/plugins"); } catch { return; }
    for (const p of list.plugins) {
      try {
        const module = await importer(p.script);
        if (typeof module.default !== "function") throw new Error("The script has no default export.");
        // The start is not waited for: one that never ends must not hold up the plugins after it. If it fails later, that is reported then.
        const started = module.default(makeHost({ id: p.id, version: p.version }, me));
        if (started instanceof Promise) started.catch((e: unknown) => reportUiError(p.id, "load", e));
      } catch (e) {
        reportUiError(p.id, "load", e);
      }
    }
  } finally {
    pluginsReady.set(true);
  }
}

let started: Promise<void> | null = null;
/** Once per page load, however often the app renders. */
export function loadPluginsOnce(me: Me): Promise<void> { return started ??= loadPlugins(me); }
