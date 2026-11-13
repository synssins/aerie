import type { AnyExtension } from "@tiptap/react";
import { api, type Me, type Page, type Space } from "../api";
import { editorExtensions } from "../editor/extensions";
import { current } from "./current";
import { reportUiError } from "./errors";
import { screens, type ScreenDef } from "./screens";
import { slots } from "./slots";

/** The version of what a plugin's script may use. The manifest's uiApiVersion must not be newer. */
export const UI_API_VERSION = "1.0.0";

type Api = { get<T>(path: string): Promise<T>; post<T>(path: string, body?: unknown): Promise<T>; put<T>(path: string, body?: unknown): Promise<T>; patch<T>(path: string, body?: unknown): Promise<T>; del<T>(path: string): Promise<T> };
type ConfirmOptions = { title: string; body?: string; ok?: string; cancel?: string; danger?: boolean };
type AskOptions = { title: string; label: string; initial?: string; ok?: string };

/** The app's in-page dialogs, set by the dialog provider when it mounts. A plugin never uses a browser popup. */
export const dialogBridge: { confirm: (o: ConfirmOptions) => Promise<boolean>; ask: (o: AskOptions) => Promise<string | null> } = {
  confirm: async () => false,
  ask: async () => null,
};

/** Who is signed in now. The app sets it at every sign-in and sign-out; scripts are loaded once per page load and outlive both. */
export const session: { me: Me | null } = { me: null };

/**
 * How a plugin's script moves inside the app. The shell replaces this with its own, which asks before leaving an edit, as a click
 * on a link does. Until the shell is there, the address changes and the shell reads it when it starts.
 */
export const navigation: { go: (path: string) => void } = {
  go(path) {
    history.pushState(null, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
  },
};

/** The object a plugin's script receives: everything it may use of the app. */
export type KbHost = {
  uiApiVersion: string;
  plugin: { id: string; version: string };
  /** Add to the named places of the app's screens. What is added is known as this plugin's. */
  slots: { add(slot: string, item: Parameters<typeof slots.add>[1]): void; hide: typeof slots.hide; move: typeof slots.move; names: typeof slots.names };
  /** The plugin's own routes: /api/v1/plugins/<id>/... */
  api: Api;
  /** The app's API: /api/v1/... The server answers what the signed-in person may see. */
  core: Api;
  me(): Me;
  current: { space(): Space | null; page(): Page | null; subscribe(fn: () => void): () => void };
  /** Goes to an address inside the app, as a click on a link would: someone in the middle of an edit is asked first. */
  navigate(path: string): void;
  /** Screens of the plugin's own, at /x/<plugin id>/<path>. */
  screens: { add(def: ScreenDef): void };
  dialogs: { confirm(o: ConfirmOptions): Promise<boolean>; ask(o: AskOptions): Promise<string | null> };
  /** Adds TipTap extensions (nodes, marks, paste and drop handlers) to every editor made from now on. Buttons go in the slot "editor.toolbar". */
  editor: { extend(...extensions: AnyExtension[]): void };
};

const rooted = (root: string): Api => ({
  get: (p) => api.get(root + p), post: (p, b) => api.post(root + p, b), put: (p, b) => api.put(root + p, b), patch: (p, b) => api.patch(root + p, b), del: (p) => api.del(root + p),
});

export function makeHost(plugin: { id: string; version: string }, me: Me): KbHost {
  return {
    uiApiVersion: UI_API_VERSION,
    plugin,
    slots: { add: (slot, item) => slots.add(slot, item, plugin.id), hide: slots.hide, move: slots.move, names: slots.names },
    api: rooted(`/plugins/${plugin.id}`),
    core: rooted(""),
    me: () => session.me ?? me,
    current: {
      space: current.space,
      page: current.page,
      subscribe: (fn) => current.subscribe(() => { try { fn(); } catch (e) { reportUiError(plugin.id, "current", e); } }),
    },
    navigate: (path) => navigation.go(path),
    screens: { add: (def) => screens.add(plugin.id, def) },
    dialogs: { confirm: (o) => dialogBridge.confirm(o), ask: (o) => dialogBridge.ask(o) },
    editor: { extend: (...extensions) => editorExtensions.add(plugin.id, ...extensions) },
  };
}
