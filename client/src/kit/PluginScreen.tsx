import { ChevronRight, House } from "lucide-react";
import { usePluginsReady } from "./loader";
import { useScreen } from "./screens";
import { PluginBoundary, SlotSections } from "./SlotViews";

/** A plugin's own screen, inside the app's three panels: the left one stays the app's, the right one is the slot the screen names. */
export function PluginScreen({ at, onHome }: { at: { id: string; rest: string }; onHome: () => void }) {
  const found = useScreen(at.id, at.rest);
  const ready = usePluginsReady();
  if (!found) {
    return (
      <>
        <main className="doc"><div className="col">{ready && <p className="muted">Not found.</p>}</div></main>
        <aside className="side" aria-label="Details" />
      </>
    );
  }
  const { def, rest } = found;
  return (
    <>
      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb">
            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button>
            <ChevronRight className="ic sm" aria-hidden /><b>{def.title}</b>
          </nav>
          <div className="titlebar"><h1>{def.title}</h1></div>
          <PluginBoundary key={`${at.id}/${at.rest}`} source={at.id} where={`screen ${def.path}`}><Body render={() => def.render(rest)} /></PluginBoundary>
        </div>
      </main>
      <aside className="side" aria-label="Details">{def.panel && <SlotSections slot={def.panel} ctx={{ rest }} />}</aside>
    </>
  );
}

const Body = ({ render }: { render: () => React.ReactNode }) => <>{render()}</>;
