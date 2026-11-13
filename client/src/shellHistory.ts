/**
 * The shell's side of the browser's history. Each address the shell adds carries its place in the row of entries, so when the
 * browser moves (Back, Forward) the shell knows how far and in which direction. That is what lets it ask before an edit is left
 * and, on Cancel, put the address of the edit back: the browser has already moved by the time the page hears of it.
 */
export type ShellHistory = {
  /** Shows a new address, as a click on a link does. No entry is added for the address already shown. */
  go(path: string): void;
  dispose(): void;
};

type State = { kb?: number } | null;
const placeOf = (): number | undefined => (history.state as State)?.kb;
const here = () => location.pathname + location.search;

export function createShellHistory(o: { dirty: () => boolean; ask: () => Promise<boolean>; apply: () => void | Promise<void> }): ShellHistory {
  let place = placeOf() ?? 0;
  let path = here();
  let own = false;     // the next move is the shell's own (putting the edit's address back)
  let asking = false;  // the question is open; further moves are settled when it is answered
  if (placeOf() === undefined) history.replaceState({ ...(history.state as object | null), kb: place }, "");

  const onPop = () => void (async () => {
    if (own) { own = false; return; }
    if (asking) return;
    if (o.dirty()) {
      asking = true;
      let leave: boolean;
      try { leave = await o.ask(); } finally { asking = false; }
      if (!leave) {
        // Stay: undo the browser's move, however far it went while the question was open.
        const moved = (placeOf() ?? place) - place;
        if (moved !== 0) { own = true; history.go(-moved); }
        else if (here() !== path) history.pushState({ kb: place }, "", path); // an entry the shell did not number
        return;
      }
    }
    place = placeOf() ?? place;
    path = here();
    await o.apply();
  })();
  window.addEventListener("popstate", onPop);

  return {
    go(next) {
      // While the question is open the browser stands on another entry than the shell counts from: number from where it stands.
      if (asking) place = placeOf() ?? place;
      if (here() !== next) history.pushState({ kb: ++place }, "", next);
      path = next;
    },
    dispose() { window.removeEventListener("popstate", onPop); },
  };
}
