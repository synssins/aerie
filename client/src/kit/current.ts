import type { Page, Space } from "../api";

/** What the person is looking at, for a plugin's script: the open space and page. */
let page: Page | null = null;
let space: Space | null = null;
const listeners = new Set<() => void>();

export const current = {
  page: () => page,
  space: () => space,
  subscribe(fn: () => void): () => void { listeners.add(fn); return () => listeners.delete(fn); },
  /** Called by the app's shell when the open space or page changes. */
  set(next: { page: Page | null; space: Space | null }): void {
    if (next.page === page && next.space === space) return;
    ({ page, space } = next);
    // Called from the shell's own effect: a listener that throws must not take the shell with it, nor keep the others from hearing.
    listeners.forEach((l) => { try { l(); } catch (e) { console.error("A listener of the current view failed:", e); } });
  },
};
