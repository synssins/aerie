import { ArrowLeft, Bell, ChevronRight, House, SlidersHorizontal } from "lucide-react";
import type { NotifyPref, UserSettings } from "./api";

export type SettingsSection = "preferences" | "notifications";

type Event = { kind: string; label: string; locked?: boolean };

function Seg<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: [T, string][]; onChange: (v: T) => void }) {
  return (
    <div className="seg" role="radiogroup" aria-label={label}>
      {options.map(([v, text]) => (
        <button key={v} type="button" role="radio" aria-checked={value === v} className={value === v ? "on" : ""} onClick={() => onChange(v)}>{text}</button>
      ))}
    </div>
  );
}

function Check({ on, label, disabled, onChange }: { on: boolean; label: string; disabled?: boolean; onChange: (v: boolean) => void }) {
  return <input type="checkbox" className="chk" checked={on} aria-label={label} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />;
}

/** User app settings in the three-panel shell. Changes save as they are made. */
export function SettingsView({ section, settings, isAdmin, ownsSpaces, onSection, onChange, onExit, onHome }: {
  section: SettingsSection;
  settings: UserSettings;
  isAdmin: boolean;
  ownsSpaces: boolean;
  onSection: (s: SettingsSection) => void;
  onChange: (s: UserSettings) => void;
  onExit: () => void;
  onHome: () => void;
}) {
  const set = <K extends keyof UserSettings>(k: K, v: UserSettings[K]) => onChange({ ...settings, [k]: v });

  // Only events this user can receive. Security alerts can't be turned off.
  const events: Event[] = [
    ...(ownsSpaces || isAdmin ? [{ kind: "access.request", label: "Access requests to my spaces" }] : []),
    { kind: "access.decision", label: "Decisions on my access requests" },
    ...(ownsSpaces || isAdmin ? [{ kind: "security.alert", label: "Security alerts", locked: true }] : []),
  ];
  const pref = (e: Event): NotifyPref => e.locked ? { inApp: true, email: true } : settings.notify[e.kind] ?? { inApp: true, email: e.kind === "access.request" };
  const setPref = (kind: string, p: NotifyPref) => set("notify", { ...settings.notify, [kind]: p });

  return (
    <>
      <nav className="tree" aria-label="Settings">
        <button className="node" onClick={onExit}><ArrowLeft className="ic" aria-hidden /><span className="grow">Back to spaces</span></button>
        <div className="tree-head admin-head">Settings</div>
        <button className={`node ${section === "preferences" ? "cur" : ""}`} onClick={() => onSection("preferences")} aria-current={section === "preferences" ? "page" : undefined}>
          <SlidersHorizontal className="ic" aria-hidden /><span className="grow">Preferences</span>
        </button>
        <button className={`node ${section === "notifications" ? "cur" : ""}`} onClick={() => onSection("notifications")} aria-current={section === "notifications" ? "page" : undefined}>
          <Bell className="ic" aria-hidden /><span className="grow">Notifications</span>
        </button>
      </nav>

      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb"><button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden /><span>Settings</span></nav>
          <div className="titlebar"><h1>{section === "preferences" ? "Preferences" : "Notifications"}</h1></div>

          {section === "preferences" ? (
            <div className="form">
              <span className="lbl">Theme</span>
              <Seg label="Theme" value={settings.theme} onChange={(v) => set("theme", v)} options={[["light", "Light"], ["dark", "Dark"], ["system", "Match system"]]} />
              <span className="lbl">Layout</span>
              <Seg label="Layout" value={settings.width} onChange={(v) => set("width", v)} options={[["boxed", "Boxed"], ["full", "Full width"]]} />
            </div>
          ) : (
            <table className="grid-table compact">
              <thead><tr><th className="c">In-app</th><th className="c">Email</th><th>Event</th></tr></thead>
              <tbody>
                {events.map((e) => {
                  const p = pref(e);
                  return (
                    <tr key={e.kind}>
                      <td className="c"><Check on={p.inApp} label={`${e.label}: in-app`} disabled={e.locked} onChange={(v) => setPref(e.kind, { ...p, inApp: v })} /></td>
                      <td className="c"><Check on={p.email} label={`${e.label}: email`} disabled={e.locked} onChange={(v) => setPref(e.kind, { ...p, email: v })} /></td>
                      <td>{e.label}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      </main>

      <aside className="side" aria-label="Details" />
    </>
  );
}
