import { useSyncExternalStore, type ReactNode } from "react";
import { reportUiError } from "./errors";

/**
 * Named places in a screen that the core and plugins fill the same way: a tool row, a panel, a menu.
 * A screen draws what is registered in its slots; a plugin's script adds to them through its host.
 */
export type Source = "core" | string; // "core", or a plugin id

type Base<C> = { id: string; order?: number; when?: (c: C) => boolean };
/** A button or a menu entry. */
export type ActionItem<C = any> = Base<C> & { label: string | ((c: C) => string); onClick?: (c: C) => void; danger?: boolean; primary?: boolean };
/** A titled part of a panel. */
export type SectionItem<C = any> = Base<C> & { title?: string; render: (c: C) => ReactNode };
/** Anything a slot can hold. */
export type SlotItem = ActionItem | SectionItem;
export type SlotEntry<I = Base<any>> = { item: I; source: Source };

type Stored = { item: Base<any>; source: Source; seq: number; hidden: boolean };
const registry = new Map<string, Stored[]>();
const listeners = new Set<() => void>();
let version = 0;
let seq = 0;
const changed = () => { version++; listeners.forEach((l) => l()); };

function visible(slot: string, ctx: unknown): Stored[] {
  return (registry.get(slot) ?? [])
    .filter((e) => {
      if (e.hidden) return false;
      // A plugin's condition that throws leaves its item out; it does not break the screen.
      try { return e.item.when ? e.item.when(ctx) : true; } catch (err) { reportUiError(e.source, slot, err); return false; }
    })
    // Items render in their order; one without an order comes after those that have one. Ties keep the order they were added in.
    .sort((a, b) => (a.item.order ?? Infinity) - (b.item.order ?? Infinity) || a.seq - b.seq);
}

export const slots = {
  /** Adds an item. Adding again under the same id, from the same source, replaces it. The core registers at 100, 200, 300, so a plugin can land between. */
  add(slot: string, item: SlotItem, source: Source = "core"): void {
    const list = registry.get(slot) ?? [];
    const at = list.findIndex((e) => e.source === source && e.item.id === item.id);
    if (at >= 0) list[at] = { ...list[at], item }; else list.push({ item, source, seq: seq++, hidden: false });
    registry.set(slot, list);
    changed();
  },
  /** Takes an item out of a slot, a core one too. */
  hide(slot: string, id: string): void {
    for (const e of registry.get(slot) ?? []) if (e.item.id === id) e.hidden = true;
    changed();
  },
  move(slot: string, id: string, order: number): void {
    for (const e of registry.get(slot) ?? []) if (e.item.id === id) e.item = { ...e.item, order };
    changed();
  },
  /** The items to draw for this context, in order. */
  list<I = Base<any>>(slot: string, ctx: unknown): I[] { return visible(slot, ctx).map((e) => e.item as I); },
  /** The same, each with who added it, so a plugin's item can be drawn inside its own error boundary. */
  entries<I = Base<any>>(slot: string, ctx: unknown): SlotEntry<I>[] { return visible(slot, ctx).map((e) => ({ item: e.item as I, source: e.source })); },
  names(): string[] { return [...registry.keys()].sort(); },
  /** For tests. */
  clear(): void { registry.clear(); changed(); },
};

/** The entries of a slot; the component re-renders when the registry changes. */
export function useSlot<I = Base<any>>(slot: string, ctx: unknown): SlotEntry<I>[] {
  useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => version);
  return slots.entries<I>(slot, ctx);
}
