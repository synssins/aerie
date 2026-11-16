import { useEffect, useState } from "react";
import { AppWindow, ArrowLeft, Bell, ChevronRight, House, KeyRound, ShieldCheck, SlidersHorizontal } from "lucide-react";
import { Link, to } from "./Link";
import { api } from "./api";
import { CodeSetup } from "./Codes";
import type { NotifyPref, Space, UserSettings } from "./api";
import { TokensPanel } from "./Tokens";
import { ConnectedAppsPanel } from "./ConnectedApps";

export type SettingsSection = "preferences" | "notifications" | "tokens" | "applications" | "signin";

type Event = { kind: string; label: string; locked?: boolean };

export function Seg<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: [T, string][]; onChange: (v: T) => void }) {
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

/** One-time codes for the person's own account. Required by the setting: on, and cannot be turned off. */
export function MyCodesPanel() {
  const [state, setState] = useState<{ on: boolean; required: boolean; allowed: boolean } | null>(null);
  const [setup, setSetup] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const load = () => api.get<{ on: boolean; required: boolean; allowed: boolean }>("/me/codes").then(setState);
  useEffect(() => { void load(); }, []);
  if (!state) return null;

  async function change(on: boolean) {
    setError(null);
    if (on) { setSetup(true); return; }
    try { await api.del("/me/codes"); await load(); } catch { setError("Could not turn codes off."); }
  }

  return (
    <>
      <div className="form">
        <span className="lbl">Use codes</span>
        <Check on={state.on || setup} label="Use codes" disabled={!state.allowed || state.required && state.on || setup} onChange={(v) => void change(v)} />
      </div>
      {error && <p className="form-error" role="alert">{error}</p>}
      {setup && <CodeSetup className="signin-card" onCancel={() => setSetup(false)} onDone={() => { setSetup(false); void load(); }} />}
    </>
  );
}

/** User app settings in the three-panel shell. Changes save as they are made. */
export function SettingsView({ section, settings, isAdmin, ownsSpaces, spaces, onSection, onChange, onExit, onHome }: {
  section: SettingsSection;
  settings: UserSettings;
  isAdmin: boolean;
  ownsSpaces: boolean;
  spaces: Space[];
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
    { kind: "comment.page", label: "Comments on pages I own" },
    { kind: "comment.reply", label: "Replies to my comments" },
    ...(ownsSpaces || isAdmin ? [{ kind: "security.alert", label: "Security alerts", locked: true }] : []),
  ];
  const pref = (e: Event): NotifyPref => e.locked ? { inApp: true, email: true } : settings.notify[e.kind] ?? { inApp: true, email: e.kind === "access.request" };
  const setPref = (kind: string, p: NotifyPref) => set("notify", { ...settings.notify, [kind]: p });

  return (
    <>
      <nav className="tree" aria-label="Settings">
        <button className="node" onClick={onExit}><ArrowLeft className="ic" aria-hidden /><span className="grow">Back to spaces</span></button>
        <div className="tree-head admin-head">Settings</div>
        <Link href={to.settings("preferences")} className={`node ${section === "preferences" ? "cur" : ""}`} onNav={() => onSection("preferences")} aria-current={section === "preferences" ? "page" : undefined}>
          <SlidersHorizontal className="ic" aria-hidden /><span className="grow">Preferences</span>
        </Link>
        <Link href={to.settings("notifications")} className={`node ${section === "notifications" ? "cur" : ""}`} onNav={() => onSection("notifications")} aria-current={section === "notifications" ? "page" : undefined}>
          <Bell className="ic" aria-hidden /><span className="grow">Notifications</span>
        </Link>
        <Link href={to.settings("tokens")} className={`node ${section === "tokens" ? "cur" : ""}`} onNav={() => onSection("tokens")} aria-current={section === "tokens" ? "page" : undefined}>
          <KeyRound className="ic" aria-hidden /><span className="grow">API tokens</span>
        </Link>
        <Link href={to.settings("applications")} className={`node ${section === "applications" ? "cur" : ""}`} onNav={() => onSection("applications")} aria-current={section === "applications" ? "page" : undefined}>
          <AppWindow className="ic" aria-hidden /><span className="grow">Connected applications</span>
        </Link>
        <Link href={to.settings("signin")} className={`node ${section === "signin" ? "cur" : ""}`} onNav={() => onSection("signin")} aria-current={section === "signin" ? "page" : undefined}>
          <ShieldCheck className="ic" aria-hidden /><span className="grow">Sign-in</span>
        </Link>
      </nav>

      <main className="doc wide">
        <div className="col">
          <nav className="crumb" aria-label="Breadcrumb"><Link href={to.home()} className="crumb-home" onNav={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></Link><ChevronRight className="ic sm" aria-hidden /><span>Settings</span></nav>
          <div className="titlebar"><h1>{section === "preferences" ? "Preferences" : section === "tokens" ? "API tokens" : section === "applications" ? "Connected applications" : section === "signin" ? "Sign-in" : "Notifications"}</h1></div>

          {section === "tokens" ? <TokensPanel admin={false} spaces={spaces} /> : section === "applications" ? <ConnectedAppsPanel /> : section === "signin" ? <MyCodesPanel /> : section === "preferences" ? (
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
