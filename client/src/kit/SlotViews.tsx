import { Component, type ReactNode } from "react";
import { reportUiError } from "./errors";
import { useSlot, type ActionItem, type SectionItem } from "./slots";

/** What a plugin draws sits inside one of these: when it throws, that part is left out and reported, and the screen stays. */
export class PluginBoundary extends Component<{ source: string; where: string; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error: unknown) { reportUiError(this.props.source, this.props.where, error); }
  render() { return this.state.failed ? null : this.props.children; }
}

function ToolButton({ item, ctx, source, slot }: { item: ActionItem; ctx: unknown; source: string; slot: string }) {
  const label = typeof item.label === "function" ? item.label(ctx) : item.label;
  const click = () => { try { item.onClick?.(ctx); } catch (e) { reportUiError(source, slot, e); } };
  return <button type="button" className={`tb ${item.primary ? "primary" : ""} ${item.danger ? "danger" : ""}`} onClick={click}>{label}</button>;
}

/** The action items of a slot, as buttons of a tool row. */
export function SlotTools({ slot, ctx }: { slot: string; ctx: unknown }) {
  return <>{useSlot<ActionItem>(slot, ctx).map((e) => (
    <PluginBoundary key={`${e.source}:${e.item.id}`} source={e.source} where={slot}><ToolButton item={e.item} ctx={ctx} source={e.source} slot={slot} /></PluginBoundary>
  ))}</>;
}

function Section({ item, ctx }: { item: SectionItem; ctx: unknown }) {
  const body = item.render(ctx);
  return <>{item.title && <h3>{item.title}</h3>}{body}</>;
}

/** The section items of a slot, each under its title: the parts of a panel. */
export function SlotSections({ slot, ctx }: { slot: string; ctx: unknown }) {
  return <>{useSlot<SectionItem>(slot, ctx).map((e) => (
    <PluginBoundary key={`${e.source}:${e.item.id}`} source={e.source} where={slot}><Section item={e.item} ctx={ctx} /></PluginBoundary>
  ))}</>;
}
