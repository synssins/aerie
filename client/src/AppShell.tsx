import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import {
  Bell, ChevronDown, ChevronRight, Ellipsis, House, KeyRound, FilePlus, FileText, Folder, FolderCog, FolderInput, FolderPlus, Link as LinkIcon, Plus, Search, Settings,
  ShieldCheck, SlidersHorizontal, Lock, Moon, Sun, Maximize2, Minimize2, ShieldAlert, LogOut, Trash2, UserRound,
} from "lucide-react";
import { AccountDialog } from "./AccountDialogs";
import { BrandLogo } from "./Branding";
import { api, BIN_ROLES, byTreeOrder, EDIT_ROLES, reloadIfPending, type Brand, type Me, type Notice, type Page, type Space, type TreeNode, type UserSettings } from "./api";
import { MenuButton, MoveDialog, useDismiss } from "./ui";
import { BinPageView, BinTree } from "./Bin";
import { BinView, HistoryView, HomeView, SearchView, type RecentView } from "./Views";
import { ADMIN_SECTIONS, AdminView, type AdminSection } from "./AdminView";
import { SettingsView, type SettingsSection } from "./SettingsView";
import { useDialogs } from "./Dialogs";
import { NewItemDialog, type NewItem } from "./NewItemDialog";
import { PageTree } from "./PageTree";
import { PageView } from "./PageView";
import { current as currentView } from "./kit/current";
import { navigation } from "./kit/host";
import { createShellHistory, type ShellHistory } from "./shellHistory";
import { PluginScreen } from "./kit/PluginScreen";
import { SlotMenuItems } from "./kit/SlotViews";

const DEFAULT_SETTINGS: UserSettings = { theme: "system", width: "boxed", startPage: "last", codeLanguage: "plaintext", notify: {} };
type AdminAt = { section: AdminSection; spaceId: string | null };
type PluginAt = { id: string; rest: string };

/** URLs carry IDs only, never names, so renames never break links. */
function route(): { space?: string; page?: string; admin?: AdminAt; settings?: SettingsSection; search?: string; history?: boolean; bin?: boolean; plugin?: PluginAt } {
  if (location.pathname === "/search") return { search: new URLSearchParams(location.search).get("q") ?? "" };
  // A plugin's own screen: /x/<plugin id>/<whatever the plugin defines>.
  const x = /^\/x\/([a-z0-9]+(?:[.-][a-z0-9]+)*)(?:\/(.*))?$/.exec(location.pathname);
  if (x) return { plugin: { id: x[1], rest: x[2] ?? "" } };
  const h = /^\/p\/([0-9a-f-]{36})\/history$/i.exec(location.pathname);
  if (h) return { page: h[1], history: true };
  const b = /^\/s\/([0-9a-f-]{36})\/bin$/i.exec(location.pathname);
  if (b) return { space: b[1], bin: true };
  const a = /^\/admin(?:\/([a-z]+))?(?:\/([0-9a-f-]{36}))?$/i.exec(location.pathname);
  if (a) return { admin: { section: ADMIN_SECTIONS.includes(a[1] as AdminSection) ? a[1] as AdminSection : "spaces", spaceId: a[2] ?? null } };
  const st = /^\/settings(?:\/(preferences|notifications|tokens))?$/.exec(location.pathname);
  if (st) return { settings: (st[1] as SettingsSection | undefined) ?? "preferences" };
  const m = /^\/(s|p)\/([0-9a-f-]{36})$/i.exec(location.pathname);
  return m ? (m[1] === "s" ? { space: m[2] } : { page: m[2] }) : {};
}

function store(key: string, value: string) { try { localStorage.setItem(key, value); } catch { /* storage blocked */ } }
function stored(key: string) { try { return localStorage.getItem(key); } catch { return null; } }
function storedViews(): RecentView[] { try { return JSON.parse(stored("kb.recent") ?? "[]") as RecentView[]; } catch { return []; } }

// What an entry of the gear menu is handed: nothing of the screen. An entry leads somewhere (kb.navigate).
const GEAR_CTX = {};

export function AppShell({ me, brand, onBrandChanged, onSignedOut }: { me: Me; brand: Brand; onBrandChanged: () => Promise<void>; onSignedOut: () => void }) {
  const [settings, setSettings] = useState<UserSettings | null>(null);
  const prefs = settings ?? DEFAULT_SETTINGS;
  const [systemDark, setSystemDark] = useState(() => window.matchMedia("(prefers-color-scheme: dark)").matches);
  const theme = prefs.theme === "system" ? (systemDark ? "dark" : "light") : prefs.theme;
  const width = prefs.width;
  const [spaces, setSpaces] = useState<Space[]>([]);
  const [current, setCurrent] = useState<Space | null>(null);
  const [unread, setUnread] = useState(0);
  const [showBell, setShowBell] = useState(false);
  const [notices, setNotices] = useState<Notice[]>([]);
  const [open, setOpen] = useState<Record<string, boolean>>({});
  const [trees, setTrees] = useState<Record<string, TreeNode[]>>({});
  const [page, setPage] = useState<Page | null>(null);
  const [admin, setAdmin] = useState<AdminAt | null>(null);
  const [settingsAt, setSettingsAt] = useState<SettingsSection | null>(null);
  const [home, setHome] = useState(false);
  const [searchQ, setSearchQ] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const [historyOf, setHistoryOf] = useState(false);
  const [binOf, setBinOf] = useState(false);
  // The bin item open in the middle, and a count that makes the bin's tree load again after a restore.
  const [binItem, setBinItem] = useState<string | null>(null);
  const [binKey, setBinKey] = useState(0);
  const [pluginAt, setPluginAt] = useState<PluginAt | null>(null);
  // What the person is looking at, for the scripts of plugins.
  useEffect(() => { currentView.set({ page: page?.id ? page : null, space: current }); }, [page, current]);
  const [moving, setMoving] = useState<Page | null>(null);
  const [viewed, setViewed] = useState<RecentView[]>(storedViews);
  const searchRef = useRef<HTMLInputElement>(null);
  const [showGear, setShowGear] = useState(false);
  const [showNew, setShowNew] = useState(false);
  const [newItem, setNewItem] = useState<NewItem | null>(null);
  const [showUser, setShowUser] = useState(false);
  const [justCreated, setJustCreated] = useState<string | null>(null);
  const [returnTo, setReturnTo] = useState<string | null>(null);
  const [panel, setPanel] = useState<"account" | null>(null);
  const dialogs = useDialogs();
  const dirty = useRef(false);
  const onDirty = useCallback((d: boolean) => { dirty.current = d; }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    store("kb.theme", theme); // the sign-in page follows the last theme used here
  }, [theme]);
  useEffect(() => {
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const h = () => setSystemDark(mq.matches);
    mq.addEventListener("change", h);
    return () => mq.removeEventListener("change", h);
  }, []);

  function saveSettings(next: UserSettings) {
    setSettings(next);
    void api.put<UserSettings>("/me/settings", next).catch(() => undefined);
  }

  const loadTree = useCallback((spaceId: string) =>
    api.get<TreeNode[]>(`/spaces/${spaceId}/tree`).then((t) => setTrees((all) => ({ ...all, [spaceId]: t }))), []);

  /** Leaves whatever view is showing; the caller then sets the one it wants. */
  const reset = useCallback(() => {
    setHome(false);
    setAdmin(null);
    setSettingsAt(null);
    setSearchQ(null);
    setHistoryOf(false);
    setBinOf(false);
    setBinItem(null);
    setPluginAt(null);
  }, []);

  const showSpace = useCallback((s: Space) => {
    reset();
    setCurrent(s);
    setPage(null);
    setOpen((o) => ({ ...o, [s.id]: true }));
    void loadTree(s.id);
  }, [loadTree, reset]);

  const showPage = useCallback(async (id: string, all: Space[]) => {
    const p = await api.get<Page>(`/pages/${id}`);
    reset();
    setCurrent(all.find((x) => x.id === p.spaceId) ?? null);
    setPage(p);
    if (p.kind === "Page") {
      setViewed((old) => {
        const next = [{ id: p.id, title: p.title }, ...old.filter((v) => v.id !== p.id)].slice(0, 8);
        store("kb.recent", JSON.stringify(next));
        return next;
      });
    }
    setOpen((o) => ({ ...o, [p.spaceId]: true, ...Object.fromEntries(p.path.map((c) => [c.id, true])) }));
    void loadTree(p.spaceId);
  }, [loadTree, reset]);

  const applyRoute = useCallback(async (all: Space[], start: string) => {
    const r = route();
    const owns = all.some((x) => x.myRole === "Owner");
    if (r.admin && (me.isAdmin || (owns && r.admin.section === "spaces"))) { reset(); setAdmin(r.admin); setPage(null); return; }
    if (r.settings) { reset(); setSettingsAt(r.settings); setPage(null); return; }
    if (r.plugin) { reset(); setPluginAt(r.plugin); setPage(null); return; }
    if (r.search !== undefined) { reset(); setSearchQ(r.search); setQ(r.search); setPage(null); setCurrent(null); return; }
    try {
      if (r.page) { await showPage(r.page, all); if (r.history) setHistoryOf(true); return; }
      if (!r.space) { reset(); setHome(true); setPage(null); setCurrent(null); return; }
      const s = all.find((x) => x.id === r.space) ?? all[0];
      if (s) { showSpace(s); if (r.bin && s.id === r.space) setBinOf(true); }
    } catch {
      if (all[0]) showSpace(all[0]);
    }
  }, [showPage, showSpace, reset, me.isAdmin]);

  const loadSpaces = useCallback(() => api.get<Space[]>("/spaces").then((s) => { setSpaces(s); return s; }), []);

  useEffect(() => {
    void (async () => {
      const st = await api.get<UserSettings>("/me/settings").catch(() => DEFAULT_SETTINGS);
      setSettings(st);
      await applyRoute(await loadSpaces(), st.startPage);
    })();
  }, [loadSpaces, applyRoute]);
  // The browser's Back and Forward. Someone in the middle of an edit is asked first, as for a click on a link; on Cancel they stay.
  const shellHistory = useRef<ShellHistory | null>(null);
  const onMove = useRef({ ask: async () => true, apply: async () => {} });
  useEffect(() => {
    const h = createShellHistory({ dirty: () => dirty.current, ask: () => onMove.current.ask(), apply: () => onMove.current.apply() });
    shellHistory.current = h;
    return () => { h.dispose(); shellHistory.current = null; };
  }, []);

  useEffect(() => {
    const poll = () => api.get<{ unread: number }>("/notifications/count").then((r) => setUnread(r.unread)).catch(() => undefined);
    void poll();
    const t = window.setInterval(poll, 60_000);
    return () => window.clearInterval(t);
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); searchRef.current?.focus(); searchRef.current?.select(); } };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const bellRef = useRef<HTMLDivElement>(null);
  const newRef = useRef<HTMLDivElement>(null);
  useDismiss(bellRef, showBell, useCallback(() => setShowBell(false), []));
  useDismiss(newRef, showNew, useCallback(() => setShowNew(false), []));
  const userRef = useRef<HTMLDivElement>(null);
  useDismiss(userRef, showUser, useCallback(() => setShowUser(false), []));
  const gearRef = useRef<HTMLDivElement>(null);
  useDismiss(gearRef, showGear, useCallback(() => setShowGear(false), []));

  async function toggleBell() {
    if (!showBell) setNotices(await api.get<Notice[]>("/notifications"));
    setShowBell(!showBell);
  }

  async function markAllRead() {
    await api.post("/notifications/read-all");
    setUnread(0);
    setNotices((n) => n.map((x) => ({ ...x, readAt: x.readAt ?? new Date().toISOString() })));
  }

  const discardOk = async () => {
    if (!dirty.current) return true;
    if (!await dialogs.confirm({ title: "Discard unsaved changes?", ok: "Discard", danger: true })) return false;
    dirty.current = false; // the editor is gone after this; it won't report clean itself
    return true;
  };
  onMove.current = { ask: discardOk, apply: () => applyRoute(spaces, "") };
  const go = (path: string) => {
    if (shellHistory.current) shellHistory.current.go(path);
    else if (location.pathname + location.search !== path) history.pushState(null, "", path);
    if (path.startsWith("/p/") || path.startsWith("/s/")) store("kb.last", path);
  };
  // A plugin's kb.navigate comes through here: someone in the middle of an edit is asked first, as for a click on a link.
  useEffect(() => {
    const before = navigation.go;
    navigation.go = (path) => void (async () => {
      if (!await discardOk()) return;
      go(path);
      await applyRoute(spaces, "");
    })();
    return () => { navigation.go = before; };
  });

  async function openSpace(s: Space) {
    if (!await discardOk()) return;
    reloadIfPending();
    if (current?.id === s.id && !page && !admin && !settingsAt && !home && !binOf && !pluginAt && searchQ === null) setOpen((o) => ({ ...o, [s.id]: !o[s.id] }));
    else showSpace(s);
    go(`/s/${s.id}`);
  }

  async function openItem(id: string | null) {
    if (!current || !await discardOk()) return;
    reloadIfPending();
    if (id === null) { showSpace(current); go(`/s/${current.id}`); return; }
    void showPage(id, spaces);
    go(`/p/${id}`);
  }

  async function openAdmin(section: AdminSection = "spaces", spaceId: string | null = null) {
    setShowGear(false);
    if (!await discardOk()) return;
    reset();
    setAdmin({ section, spaceId });
    setPage(null);
    go(`/admin/${section}${spaceId ? `/${spaceId}` : ""}`);
  }

  async function openSettings(section: SettingsSection) {
    setShowGear(false);
    if (!await discardOk()) return;
    reset();
    setSettingsAt(section);
    setPage(null);
    go(`/settings/${section}`);
  }

  async function goHome() {
    setShowGear(false);
    if (!await discardOk()) return;
    reloadIfPending();
    reset();
    setHome(true);
    setPage(null);
    setCurrent(null);
    go("/");
  }
  const onHome = () => void goHome();

  async function openSearch(e: FormEvent) {
    e.preventDefault();
    const text = q.trim();
    if (!text || !await discardOk()) return;
    reset();
    setSearchQ(text);
    setPage(null);
    setCurrent(null);
    go(`/search?q=${encodeURIComponent(text)}`);
  }

  async function openPageById(id: string) {
    if (!await discardOk()) return;
    reloadIfPending();
    void showPage(id, spaces);
    go(`/p/${id}`);
  }

  async function openBin(s: Space) {
    if (!await discardOk()) return;
    showSpace(s);
    setBinOf(true);
    go(`/s/${s.id}/bin`);
  }

  function openHistory() {
    if (!page) return;
    setHistoryOf(true);
    go(`/p/${page.id}/history`);
  }

  async function deleteItem(p: Page) {
    if (!current || !await dialogs.confirm({ title: `Move “${p.title}” to the recycle bin?`, ok: "Delete", danger: true })) return;
    try { await api.del(`/pages/${p.id}`); }
    catch { await dialogs.confirm({ title: "Not deleted.", body: p.kind === "Folder" ? "The folder holds pages you can't delete." : undefined, ok: "OK" }); return; }
    setViewed((old) => { const next = old.filter((v) => v.id !== p.id); store("kb.recent", JSON.stringify(next)); return next; });
    if (p.parentId) { void showPage(p.parentId, spaces); go(`/p/${p.parentId}`); }
    else { showSpace(current); go(`/s/${current.id}`); }
  }

  function moved(from: Page, to: Page) {
    setMoving(null);
    if (to.spaceId !== from.spaceId) void loadTree(from.spaceId);
    void showPage(to.id, spaces);
    go(`/p/${to.id}`);
  }

  function backToSpaces() {
    if (current) { showSpace(current); go(`/s/${current.id}`); }
    else { setAdmin(null); setSettingsAt(null); go("/"); }
  }

  // New items go where the user is: inside the open folder, or beside the open page.
  const parentId = page?.kind === "Folder" ? page.id : page?.parentId ?? null;
  const parentPath = page ? (page.kind === "Folder" ? [...page.path.map((c) => c.title), page.title] : page.path.map((c) => c.title)) : [];

  async function startNew(kind: "Page" | "Folder") {
    setShowNew(false);
    if (!current || !await discardOk()) return;
    if (kind === "Folder") { setNewItem({ kind, spaceName: current.name, path: parentPath }); return; }
    // New page: straight into the editor. Nothing is stored until the first save.
    const crumbs = page ? (page.kind === "Folder" ? [...page.path, { id: page.id, title: page.title }] : page.path) : [];
    setReturnTo(page?.id ?? null);
    setPage({
      id: "", spaceId: current.id, parentId, kind: "Page", title: "", contentJson: '{"type":"doc","content":[]}', version: "",
      updatedAt: "", updatedBy: "", path: crumbs, history: [], canEdit: true, tags: [], canDelete: false, draft: null, attachments: [], maxAttachmentBytes: 0,
    });
  }

  async function create(title: string, tags: string[]) {
    if (!current || !newItem) return;
    const created = await api.post<Page>(`/spaces/${current.id}/pages`, { parentId, kind: newItem.kind, title, tags });
    setNewItem(null);
    setJustCreated(created.id);
    await loadTree(current.id);
    if (parentId) setOpen((o) => ({ ...o, [parentId]: true }));
    setPage(created);
    go(`/p/${created.id}`);
  }

  async function signOut() {
    if (!await discardOk()) return;
    await api.post("/auth/signout");
    history.replaceState(history.state, "", "/"); // the entry keeps its place in the row (shellHistory)
    onSignedOut();
  }

  const initials = useMemo(() => me.displayName.split(/\s+/).map((p) => p[0]).join("").slice(0, 2).toUpperCase(), [me]);
  const canEdit = !!current?.myRole && EDIT_ROLES.includes(current.myRole) && !binOf && !historyOf;
  const ownsSpaces = spaces.some((x) => x.myRole === "Owner");
  const canManageCurrent = !!current && (me.isAdmin || current.myRole === "Owner");
  const listed = current
    ? (trees[current.id] ?? []).filter((n) => n.parentId === (page?.kind === "Folder" ? page.id : null)).sort(byTreeOrder)
    : [];

  return (
    <div className={`stage ${width}`}>
      <div className="kb">
        <header className="grid bar">
          <div className="c1">
            <button className="home-link" onClick={onHome} title="Home">
              <BrandLogo brand={brand} background="dark" />
              <span className="brand">{brand.productName}</span>
            </button>
          </div>
          <div className="c2">
            <form className="search" role="search" onSubmit={(e) => void openSearch(e)}>
              <Search className="ic" aria-hidden />
              <input ref={searchRef} value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search articles, apps, tags" aria-label="Search" maxLength={200} />
              <kbd>Ctrl K</kbd>
            </form>
          </div>
          <div className="c3">
            {canEdit && !admin && !settingsAt && !home && !pluginAt && searchQ === null && (
              <div className="menu-wrap" ref={newRef}>
                <button className="ib" onClick={() => setShowNew(!showNew)} aria-haspopup="menu" aria-expanded={showNew}>
                  <Plus className="ic" aria-hidden />New<ChevronDown className="ic sm" aria-hidden />
                </button>
                {showNew && (
                  <div className="menu" role="menu">
                    <button role="menuitem" onClick={() => void startNew("Page")}><FilePlus className="ic" aria-hidden />Page</button>
                    <button role="menuitem" onClick={() => void startNew("Folder")}><FolderPlus className="ic" aria-hidden />Folder</button>
                  </div>
                )}
              </div>
            )}
            <div className="bell-wrap" ref={bellRef}>
              <button className="ib" onClick={toggleBell} aria-label={`Notifications, ${unread} unread`} aria-expanded={showBell}>
                <Bell className="ic" aria-hidden />
                {unread > 0 && <span className="dot">{unread > 99 ? "99+" : unread}</span>}
              </button>
              {showBell && (
                <div className="bell-panel" role="dialog" aria-label="Notifications">
                  <div className="bell-head"><b>Notifications</b>{notices.some((n) => !n.readAt) && <button className="link" onClick={markAllRead}>Mark all read</button>}</div>
                  {notices.length === 0 && <p className="muted">No notifications.</p>}
                  {notices.map((n) => (
                    <div key={n.id} className={`notice ${n.readAt ? "" : "unread"}`}>
                      <div className="notice-title">{n.kind.startsWith("security") && <ShieldAlert className="ic warn" aria-hidden />}{n.title}</div>
                      {n.body && <div className="muted small">{n.body}</div>}
                    </div>
                  ))}
                </div>
              )}
            </div>
            <button className="ib" onClick={() => saveSettings({ ...prefs, theme: theme === "light" ? "dark" : "light" })} aria-label="Toggle dark mode">
              {theme === "light" ? <Moon className="ic" aria-hidden /> : <Sun className="ic" aria-hidden />}
            </button>
            <button className="ib" onClick={() => saveSettings({ ...prefs, width: width === "boxed" ? "full" : "boxed" })} aria-label={width === "boxed" ? "Full width" : "Boxed width"}>
              {width === "boxed" ? <Maximize2 className="ic" aria-hidden /> : <Minimize2 className="ic" aria-hidden />}
            </button>
            <div className="menu-wrap" ref={gearRef}>
              <button className={`ib ${admin || settingsAt ? "on" : ""}`} onClick={() => setShowGear(!showGear)} aria-label="Settings" title="Settings" aria-haspopup="menu" aria-expanded={showGear}>
                <Settings className="ic" aria-hidden />
              </button>
              {showGear && (
                <div className="menu right" role="menu">
                  <div className="menu-grp">Settings</div>
                  <button role="menuitem" onClick={() => void openSettings("preferences")}><SlidersHorizontal className="ic" aria-hidden />Preferences</button>
                  <button role="menuitem" onClick={() => void openSettings("notifications")}><Bell className="ic" aria-hidden />Notifications</button>
                  <button role="menuitem" onClick={() => void openSettings("tokens")}><KeyRound className="ic" aria-hidden />API tokens</button>
                  {(canManageCurrent || me.isAdmin) && <div className="menu-sep" />}
                  {canManageCurrent && (
                    <button role="menuitem" onClick={() => void openAdmin("spaces", current!.id)}>
                      <FolderCog className="ic" aria-hidden /><span className="menu-two">Manage space<small>{current!.name}</small></span>
                    </button>
                  )}
                  {me.isAdmin && <button role="menuitem" onClick={() => void openAdmin("spaces")}><ShieldCheck className="ic" aria-hidden />Administration</button>}
                  <SlotMenuItems slot="header.gear" ctx={GEAR_CTX} onPick={() => setShowGear(false)} />
                </div>
              )}
            </div>
            <div className="menu-wrap" ref={userRef}>
              <button className="av" onClick={() => setShowUser(!showUser)} title={me.displayName} aria-label="Account menu" aria-haspopup="menu" aria-expanded={showUser}>{initials}</button>
              {showUser && (
                <div className="menu right" role="menu">
                  <div className="menu-head"><b>{me.displayName}</b><span className="muted small">{me.userName}</span></div>
                  <button role="menuitem" onClick={() => { setShowUser(false); setPanel("account"); }}><UserRound className="ic" aria-hidden />Account</button>
                  <div className="menu-sep" />
                  <button role="menuitem" onClick={() => { setShowUser(false); void signOut(); }}><LogOut className="ic" aria-hidden />Sign out</button>
                </div>
              )}
            </div>
          </div>
        </header>

        {me.isBreakGlass && (
          <div className="breakglass" role="status"><ShieldAlert className="ic" aria-hidden />Break-glass account</div>
        )}

        <div className="grid main">
          {admin ? (
            <AdminView
              isAdmin={me.isAdmin}
              section={admin.section}
              spaceId={admin.spaceId}
              mySpaces={spaces}
              onNavigate={(sec, id) => void openAdmin(sec, id ?? null)}
              onExit={backToSpaces}
              onSpacesChanged={loadSpaces}
              onHome={onHome}
              brand={brand}
              onBrandChanged={onBrandChanged}
            />
          ) : settingsAt ? (
            <SettingsView
              section={settingsAt}
              settings={prefs}
              isAdmin={me.isAdmin}
              ownsSpaces={ownsSpaces}
              spaces={spaces}
              onSection={(x) => void openSettings(x)}
              onChange={saveSettings}
              onExit={backToSpaces}
              onHome={onHome}
            />
          ) : (
            <>
              <nav className="tree" aria-label="Spaces and pages">
                <div className="tree-head">Spaces</div>
                {spaces.length === 0 && <p className="muted small pad">No spaces.</p>}
                {spaces.map((s) => (
                  <div key={s.id}>
                    <button
                      className={`node ${current?.id === s.id && !page && !binOf && !home && !pluginAt && searchQ === null ? "cur" : ""} ${open[s.id] ? "open" : ""}`}
                      onClick={() => void openSpace(s)}
                      aria-expanded={!!open[s.id]}
                    >
                      <ChevronRight className="ic tw" aria-hidden />
                      <Folder className="ic ft" aria-hidden />
                      <span className="grow">{s.name}</span>
                      {s.isSensitive && <Lock className="ic muted" aria-label="Sensitive space" />}
                    </button>
                    {open[s.id] && trees[s.id] && (
                      <div className="kids">
                        <PageTree
                          nodes={trees[s.id]} parentId={null} selectedId={page?.id ?? null} open={open}
                          onToggle={(id) => setOpen((o) => ({ ...o, [id]: !o[id] }))}
                          onSelect={async (n) => {
                            if (!await discardOk()) return;
                            void showPage(n.id, spaces);
                            go(`/p/${n.id}`);
                          }}
                        />
                        {s.myRole && BIN_ROLES.includes(s.myRole) && (
                          <>
                            <button className={`node ${binOf && !binItem && current?.id === s.id ? "cur" : ""} ${binOf && current?.id === s.id ? "open" : ""}`}
                              aria-expanded={binOf && current?.id === s.id} onClick={() => void openBin(s)}>
                              <ChevronRight className="ic tw" aria-hidden /><Trash2 className="ic pg" aria-hidden /><span className="grow">Recycle bin</span>
                            </button>
                            {binOf && current?.id === s.id && (
                              <div className="kids"><BinTree spaceId={s.id} selectedId={binItem} reloadKey={binKey} onSelect={setBinItem} /></div>
                            )}
                          </>
                        )}
                      </div>
                    )}
                  </div>
                ))}
              </nav>

              {pluginAt ? (
                <PluginScreen at={pluginAt} onHome={onHome} />
              ) : searchQ !== null ? (
                <SearchView query={searchQ} onHome={onHome} onOpen={(id) => void openPageById(id)} />
              ) : home || !current ? (
                <HomeView me={me} spaces={spaces} viewed={viewed} onOpenSpace={(s) => void openSpace(s)} onOpenPage={(id) => void openPageById(id)} />
              ) : binOf && binItem ? (
                <BinPageView id={binItem} onHome={onHome} onBin={() => setBinItem(null)} onOpen={setBinItem} onRestored={(id) => { setBinItem(null); setBinKey((k) => k + 1); void loadTree(id); }} />
              ) : binOf ? (
                <BinView space={current} onHome={onHome} onOpenSpace={() => void openSpace(current)} onOpen={setBinItem} onEmptied={() => setBinKey((k) => k + 1)} onRestored={(id) => { setBinKey((k) => k + 1); void loadTree(id); }} />
              ) : historyOf && page?.kind === "Page" ? (
                <HistoryView
                  page={page}
                  spaceName={current.name}
                  onHome={onHome}
                  onOpenSpace={() => void openItem(null)}
                  onBack={() => void openItem(page.id)}
                  onRestored={(p) => { setPage(p); setHistoryOf(false); go(`/p/${p.id}`); }}
                />
              ) : page?.kind === "Page" ? (
                <PageView
                  key={page.id}
                  page={page}
                  spaceName={current.name}
                  role={current.myRole}
                  codeLanguage={prefs.codeLanguage}
                  onHome={onHome}
                  onHistory={openHistory}
                  onMove={() => setMoving(page)}
                  onDelete={() => void deleteItem(page)}
                  onSaved={(p) => {
                    const wasNew = !page.id;
                    setPage(p);
                    void loadTree(p.spaceId);
                    if (wasNew) { if (p.parentId) setOpen((o) => ({ ...o, [p.parentId!]: true })); go(`/p/${p.id}`); }
                  }}
                  onCancelNew={() => { if (returnTo) void showPage(returnTo, spaces); else showSpace(current); }}
                  onOpen={(id) => void openItem(id)}
                  onDirty={onDirty}
                  autoEdit={!page.id || page.id === justCreated}
                />
              ) : (
                <>
                  <main className="doc wide">
                    <div className="col">
                          <nav className="crumb" aria-label="Breadcrumb">
                            <button className="crumb-home" onClick={onHome} aria-label="Home" title="Home"><House className="ic" aria-hidden /></button><ChevronRight className="ic sm" aria-hidden />
                            {page ? <button className="crumb-link" onClick={() => void openItem(null)}>{current.name}</button> : <b>{current.name}</b>}
                            {page?.path.map((c) => (
                              <span key={c.id} className="crumb-part"><ChevronRight className="ic sm" aria-hidden /><button className="crumb-link" onClick={() => void openItem(c.id)}>{c.title}</button></span>
                            ))}
                            {page && <><ChevronRight className="ic sm" aria-hidden /><b>{page.title}</b></>}
                          </nav>
                          <div className="titlebar">
                            <h1>{page?.title ?? current.name}</h1>
                            <div className="ptools">
                              <button className="tb icon" aria-label="Copy link" title="Copy link"
                                onClick={() => navigator.clipboard?.writeText(`${location.origin}${page ? `/p/${page.id}` : `/s/${current.id}`}`)}><LinkIcon className="ic" aria-hidden /></button>
                              {page?.canDelete && (
                                <MenuButton label="More" iconOnly icon={<Ellipsis className="ic" aria-hidden />} items={[
                                  { label: "Move", icon: <FolderInput className="ic" aria-hidden />, onClick: () => setMoving(page) },
                                  { label: "Delete", icon: <Trash2 className="ic" aria-hidden />, onClick: () => void deleteItem(page), danger: true },
                                ]} />
                              )}
                            </div>
                          </div>
                          {!page && current.description && <p>{current.description}</p>}
                          <div className="listing">
                            {listed.map((n) => (
                              <button key={n.id} className="node" onClick={() => void openItem(n.id)}>
                                {n.kind === "Folder" ? <Folder className="ic ft" aria-hidden /> : <FileText className="ic pg" aria-hidden />}
                                <span className="grow">{n.title}</span>
                              </button>
                            ))}
                            {listed.length === 0 && <p className="muted">Empty.</p>}
                          </div>
                    </div>
                  </main>
                  <aside className="side" aria-label="Details">
                    {(
                      <>
                        <h3>Space</h3>
                        <div>{current.name}</div>
                        {current.description && <div className="muted small">{current.description}</div>}
                        {page && <><h3>Folder</h3><div>{page.title}</div></>}
                        <h3>Contents</h3>
                        <div>{listed.filter((n) => n.kind === "Folder").length} folders · {listed.filter((n) => n.kind === "Page").length} pages</div>
                        <h3>Your access</h3>
                        <div>{current.myRole ?? "None"}</div>
                        {current.isSensitive && <div className="muted small">Sensitive space</div>}
                      </>
                    )}
                  </aside>
                </>
              )}
            </>
          )}

          {newItem && <NewItemDialog item={newItem} onCreate={create} onClose={() => setNewItem(null)} />}
          {moving && <MoveDialog item={moving} spaces={spaces} onMoved={(p) => moved(moving, p)} onClose={() => setMoving(null)} />}
          {panel === "account" && <AccountDialog me={me} onClose={() => setPanel(null)} />}
        </div>
      </div>
    </div>
  );
}
