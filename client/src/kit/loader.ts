import { api, type Me } from "../api";
import { reportUiError } from "./errors";
import { makeHost, type KbHost } from "./host";

type UiPlugins = { uiApiVersion: string; plugins: { id: string; version: string; script: string }[] };
type PluginModule = { default?: (kb: KbHost) => void | Promise<void> };

/**
 * Loads the script of every plugin that has one and hands each its host. A script that fails to load or throws is skipped and
 * reported; the app carries on, and the next plugin still loads.
 */
export async function loadPlugins(me: Me, importer: (url: string) => Promise<PluginModule> = (url) => import(/* @vite-ignore */ url)): Promise<void> {
  let list: UiPlugins;
  try { list = await api.get<UiPlugins>("/ui/plugins"); } catch { return; }
  for (const p of list.plugins) {
    try {
      const module = await importer(p.script);
      if (typeof module.default !== "function") throw new Error("The script has no default export.");
      await module.default(makeHost({ id: p.id, version: p.version }, me));
    } catch (e) {
      reportUiError(p.id, "load", e);
    }
  }
}

let started: Promise<void> | null = null;
/** Once per page load, however often the app renders. */
export function loadPluginsOnce(me: Me): Promise<void> { return started ??= loadPlugins(me); }
