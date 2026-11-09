import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Bell, ChevronRight, FileText, Folder, History, Link as LinkIcon, Download, MoreHorizontal, Pencil, Plus, Search,
  SlidersHorizontal, Lock, Moon, Sun, Maximize2, Minimize2, ShieldAlert, LogOut,
} from "lucide-react";
import { api, reloadIfPending, type Brand, type Me, type Notice, type Space } from "./api";

type Width = "boxed" | "full";
type Theme = "light" | "dark";

// Per-viewer conveniences only; server-side user settings arrive in plan 1c.
function usePref<T extends string>(key: string, initial: T): [T, (v: T) => void] {
  const [v, setV] = useState<T>(() => {
    try { return (localStorage.getItem(key) as T) ?? initial; } catch { return initial; }
  });
  const set = (nv: T) => { setV(nv); try { localStorage.setItem(key, nv); } catch { /* storage blocked */ } };
  return [v, set];
}

export function AppShell({ me, brand, onSignedOut }: { me: Me; brand: Brand; onSignedOut: () => void }) {
  const [width, setWidth] = usePref<Width>("kb.width", "boxed");
  const [theme, setTheme] = usePref<Theme>("kb.theme", "light");
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [current, setCurrent] = useState<Space | null>(null);
  const [unread, setUnread] = useState(0);
  const [showBell, setShowBell] = useState(false);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({});

  useEffect(() => { document.documentElement.dataset.theme = theme; }, [theme]);

  const loadSpaces = useCallback(() => api.get<Space[]>("/spaces").then((s) => {
    setSpaces(s);
    setCurrent((c) => c ?? s[0] ?? null);
  }), []);

  useEffect(() => { void loadSpaces(); }, [loadSpaces]);

  useEffect(() => {
    const poll = () => api.get<{ unread: number }>("/notifications/count").then((r) => setUnread(r.unread)).catch(() => undefined);
    void poll();
    const t = window.setInterval(poll, 60_000);
    return () => window.clearInterval(t);
  }, []);

  async function toggleBell() {
    if (!showBell) setNotices(await api.get<Notice[]>("/notifications"));
    setShowBell(!showBell);
  }

  async function markAllRead() {
    await api.post("/notifications/read-all");
    setUnread(0);
    setNotices((n) => n.map((x) => ({ ...x, readAt: x.readAt ?? new Date().toISOString() })));
  }

  function navigate(s: Space) {
    reloadIfPending();
    setCurrent(s);
  }

  async function newSpace() {
    const name = window.prompt("Name of the new space");
    if (!name) return;
    const key = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 32) || "space";
    try {
      const s = await api.post<Space>("/spaces", { key, name });
      await loadSpaces();
      setCurrent(s);
    } catch {
      window.alert("Only administrators can create spaces, and the key must be unique.");
    }
  }

  async function signOut() {
    await api.post("/auth/signout");
    onSignedOut();
  }

  const initials = useMemo(() => me.displayName.split(/\s+/).map((p) => p[0]).join("").slice(0, 2).toUpperCase(), [me]);

  return (
    <div className={`stage ${width}`}>
      <div className="kb">
        <header className="grid bar">
          <div className="c1">
            <div className="logo">COMPANY LOGO</div>
            <span className="brand">{brand.productName}</span>
          </div>
          <div className="c2">
            <label className="search">
              <Search className="ic" aria-hidden />
              <input placeholder="Search articles, apps, tags" aria-label="Search" />
              <kbd>Ctrl K</kbd>
            </label>
          </div>
          <div className="c3">
            {me.isAdmin && <button className="ib" onClick={newSpace}><Plus className="ic" aria-hidden />New</button>}
            <div className="bell-wrap">
              <button className="ib" onClick={toggleBell} aria-label={`Notifications, ${unread} unread`} aria-expanded={showBell}>
                <Bell className="ic" aria-hidden />
                {unread > 0 && <span className="dot">{unread > 99 ? "99+" : unread}</span>}
              </button>
              {showBell && (
                <div className="bell-panel" role="dialog" aria-label="Notifications">
                  <div className="bell-head"><b>Notifications</b>{notices.some((n) => !n.readAt) && <button className="link" onClick={markAllRead}>Mark all read</button>}</div>
                  {notices.length === 0 && <p className="muted">Nothing new. Access requests and security alerts appear here.</p>}
                  {notices.map((n) => (
                    <div key={n.id} className={`notice ${n.readAt ? "" : "unread"}`}>
                      <div className="notice-title">{n.kind.startsWith("security") && <ShieldAlert className="ic warn" aria-hidden />}{n.title}</div>
                      {n.body && <div className="muted small">{n.body}</div>}
                    </div>
                  ))}
                </div>
              )}
            </div>
            <button className="ib" onClick={() => setTheme(theme === "light" ? "dark" : "light")} aria-label="Toggle dark mode">
              {theme === "light" ? <Moon className="ic" aria-hidden /> : <Sun className="ic" aria-hidden />}
            </button>
            <button className="ib" onClick={() => setWidth(width === "boxed" ? "full" : "boxed")} aria-label={width === "boxed" ? "Full width" : "Boxed width"}>
              {width === "boxed" ? <Maximize2 className="ic" aria-hidden /> : <Minimize2 className="ic" aria-hidden />}
            </button>
            <button className="ib" aria-label="Settings"><SlidersHorizontal className="ic" aria-hidden /></button>
            <button className="av" onClick={signOut} title={`Signed in as ${me.displayName}. Click to sign out.`} aria-label="Sign out">{initials}</button>
          </div>
        </header>

        {me.isBreakGlass && (
          <div className="breakglass" role="status"><ShieldAlert className="ic" aria-hidden />Signed in with the break-glass account. Every action is audited and all administrators were alerted.</div>
        )}

        <div className="grid main">
          <nav className="tree" aria-label="Spaces and pages">
            <div className="tree-head">Spaces</div>
            {spaces.length === 0 && <p className="muted small pad">No spaces yet.{me.isAdmin ? " Use New to create one." : ""}</p>}
            {spaces.map((s) => (
              <div key={s.id}>
                <button
                  className={`node ${current?.id === s.id ? "cur" : ""} ${open[s.id] ? "open" : ""}`}
                  onClick={() => { navigate(s); setOpen({ ...open, [s.id]: !open[s.id] }); }}
                  aria-expanded={!!open[s.id]}
                >
                  <ChevronRight className="ic tw" aria-hidden />
                  <Folder className="ic ft" aria-hidden />
                  <span className="grow">{s.name}</span>
                  {s.isSensitive && <Lock className="ic muted" aria-label="Sensitive space" />}
                </button>
                {open[s.id] && (
                  <div className="kids">
                    <div className="node empty"><span className="ic tw" /><FileText className="ic pg" aria-hidden /><span className="muted">Pages arrive in plan 1b</span></div>
                  </div>
                )}
              </div>
            ))}
          </nav>

          <main className="doc">
            <div className="col">
              {current ? (
                <>
                  <div className="crumb"><span>Spaces</span><ChevronRight className="ic sm" aria-hidden /><b>{current.name}</b></div>
                  <div className="titlebar">
                    <h1>{current.name}</h1>
                    <div className="ptools">
                      <button className="tb" disabled title="Editing arrives in plan 1b"><Pencil className="ic" aria-hidden />Edit</button>
                      <button className="tb icon" disabled aria-label="History"><History className="ic" aria-hidden /></button>
                      <button className="tb icon" aria-label="Copy link" onClick={() => navigator.clipboard?.writeText(`${location.origin}/spaces/${current.key}`)}><LinkIcon className="ic" aria-hidden /></button>
                      <button className="tb icon" disabled aria-label="Export"><Download className="ic" aria-hidden /></button>
                      <button className="tb icon" aria-label="More"><MoreHorizontal className="ic" aria-hidden /></button>
                    </div>
                  </div>
                  <div className="meta">Your role: {current.myRole ?? "none"}{current.isSensitive ? " · Sensitive space" : ""}{current.isPublic ? " · Public help space" : ""}</div>
                  <p>{current.description ?? "This space has no description yet."}</p>
                  <h2 className="s" id="next">What comes next</h2>
                  <p>The folder tree, the editor, version history and exports are built in the next plan. This shell shows the layout, sign-in, spaces, roles and notifications working against the real API.</p>
                </>
              ) : (
                <>
                  <h1>Welcome, {me.displayName}</h1>
                  <p>You have no spaces yet. {me.isAdmin ? "Create one with New in the top bar." : "Ask a space Owner for access."}</p>
                </>
              )}
            </div>
            {current && (
              <aside className="toc" aria-label="On this page">
                <b>On this page</b>
                <a href="#next">What comes next</a>
              </aside>
            )}
          </main>

          <aside className="side" aria-label="Details">
            <h3>Space</h3>
            <div>{current?.name ?? "None selected"}</div>
            <div className="muted">{current?.key}</div>
            <h3>Access</h3>
            <div>{current?.myRole ?? "No role"}</div>
            {current?.isSensitive && <div className="muted small">Administrators need Owner approval to open this space.</div>}
            <h3>Signed in</h3>
            <div>{me.displayName}</div>
            <button className="link small" onClick={signOut}><LogOut className="ic sm" aria-hidden />Sign out</button>
          </aside>
        </div>
      </div>
    </div>
  );
}
