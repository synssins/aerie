import { afterEach, describe, expect, it, vi } from "vitest";
import { createShellHistory, type ShellHistory } from "./shellHistory";

const here = () => location.pathname + location.search;
let shell: ShellHistory | null = null;

function start(o: { dirty?: () => boolean; ask?: () => Promise<boolean> } = {}) {
  const applied: string[] = [];
  shell = createShellHistory({ dirty: o.dirty ?? (() => false), ask: o.ask ?? (async () => true), apply: () => { applied.push(here()); } });
  return applied;
}

afterEach(() => { shell?.dispose(); shell = null; history.replaceState(null, "", "/"); });

describe("the shell's history", () => {
  it("Back shows the address before, and Forward the one after", async () => {
    const applied = start();
    shell!.go("/p/one");
    shell!.go("/p/two");

    history.back();
    await vi.waitFor(() => expect(applied).toEqual(["/p/one"]));
    history.forward();
    await vi.waitFor(() => expect(applied).toEqual(["/p/one", "/p/two"]));
  });

  it("Back in the middle of an edit asks first; on Cancel the address of the edit is put back and nothing is shown anew", async () => {
    const ask = vi.fn(async () => false);
    const applied = start({ dirty: () => true, ask });
    shell!.go("/p/one");
    shell!.go("/p/editing");

    history.back();

    await vi.waitFor(() => expect(ask).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(here()).toBe("/p/editing"));
    expect(applied).toEqual([]);
    // The move back to the edit was the shell's own: it asked once, not twice.
    await new Promise((r) => setTimeout(r, 20));
    expect(ask).toHaveBeenCalledOnce();
  });

  it("Back in the middle of an edit, answered Discard, goes where Back leads", async () => {
    const applied = start({ dirty: () => true, ask: async () => true });
    shell!.go("/p/one");
    shell!.go("/p/editing");

    history.back();

    await vi.waitFor(() => expect(applied).toEqual(["/p/one"]));
    expect(here()).toBe("/p/one");
  });

  it("Forward in the middle of an edit is put back the same way", async () => {
    let dirty = false;
    const ask = vi.fn(async () => false);
    const applied = start({ dirty: () => dirty, ask });
    shell!.go("/p/editing");
    shell!.go("/p/later");
    history.back();
    await vi.waitFor(() => expect(applied).toEqual(["/p/editing"]));

    dirty = true;
    history.forward();

    await vi.waitFor(() => expect(ask).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(here()).toBe("/p/editing"));
    expect(applied).toEqual(["/p/editing"]);
  });

  it("an address shown while the question is open (a save that creates the page) keeps the numbering right", async () => {
    let answer: ((leave: boolean) => void) | null = null;
    const applied = start({ dirty: () => true, ask: () => new Promise<boolean>((r) => { answer = r; }) });
    shell!.go("/p/one");
    shell!.go("/p/new");

    history.back();
    await vi.waitFor(() => expect(answer).not.toBeNull());
    shell!.go("/p/created"); // from the entry the browser is on now
    answer!(false);
    answer = null;

    // Cancel: the browser did not move after that address was shown, so there is nothing to undo and it stays.
    await new Promise((r) => setTimeout(r, 20));
    expect(here()).toBe("/p/created");
    expect(applied).toEqual([]);
    // And the numbering is still the row's: Back from here is one step, seen as one step.
    history.back();
    await vi.waitFor(() => expect(answer).not.toBeNull());
    answer!(true);
    await vi.waitFor(() => expect(applied).toEqual(["/p/one"]));
  });

  it("go to the address already shown adds no entry", () => {
    start();
    shell!.go("/p/one");
    const length = history.length;
    shell!.go("/p/one");
    expect(history.length).toBe(length);
  });
});
