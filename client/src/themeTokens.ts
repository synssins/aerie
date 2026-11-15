/**
 * Every color of the app, as a CSS variable with a Day and a Night value. The defaults here are theme.css's own (a test checks
 * they match), so a theme stores only what an Admin changed. The server keeps the same list of names (ThemeTokens.cs).
 * A token with `from` follows another one unless it is set itself (a primary button follows the header); `mix` lightens it
 * with white by that many percent (a header button's hover).
 */
export type ThemeGroup = "Header" | "Surfaces" | "Text" | "Accent" | "Notices" | "Content";
export type ThemeToken = { name: string; label: string; group: ThemeGroup; day: string; night: string; from?: string; mix?: number };
export const THEME_GROUPS: ThemeGroup[] = ["Header", "Surfaces", "Text", "Accent", "Notices", "Content"];

const t = (group: ThemeGroup, name: string, label: string, day: string, night = day): ThemeToken => ({ group, name, label, day, night });
const follows = (group: ThemeGroup, name: string, label: string, from: string, mix?: number): ThemeToken => ({ group, name, label, day: "", night: "", from, mix });

export const THEME_TOKENS: ThemeToken[] = [
  t("Header", "--nav", "Header", "#14304f", "#0d1b2c"),
  t("Header", "--nav-fg", "Header text", "#ffffff"),
  t("Header", "--nav-ink", "Header text, quiet", "#dbe4ef", "#c9d5e3"),
  follows("Header", "--nav-hover", "Header button, hover", "--nav", 10),
  follows("Header", "--nav-active", "Header button, on", "--nav", 14),
  t("Header", "--logo-bg", "Logo badge", "#f2a516"),
  t("Header", "--logo-ink", "Logo badge text", "#14304f"),
  t("Header", "--search-bg", "Search box", "#ffffff"),
  t("Header", "--search-ink", "Search text", "#1c2430"),
  t("Header", "--search-ink2", "Search hint and icon", "#5b6573"),
  t("Header", "--search-line", "Search key outline", "#d3d7dc"),
  t("Header", "--avatar-bg", "Account badge", "#7aa7e0"),
  t("Header", "--avatar-ink", "Account badge text", "#14304f"),
  t("Header", "--badge-bg", "Unread count", "#e5484d"),
  t("Header", "--badge-ink", "Unread count text", "#ffffff"),

  t("Surfaces", "--stage", "Behind the app", "#e6e8eb", "#0f141a"),
  t("Surfaces", "--paper", "Page", "#ffffff", "#1b222c"),
  t("Surfaces", "--rail", "Tree and side panel", "#f6f7f9", "#161c24"),
  t("Surfaces", "--line", "Lines and borders", "#e3e6ea", "#2a3340"),
  t("Surfaces", "--hover", "Row, hover", "#eceff3", "#222b37"),
  follows("Surfaces", "--toolbar-bg", "Editor toolbar", "--rail"),
  t("Surfaces", "--switch-knob", "Switch knob", "#ffffff"),
  t("Surfaces", "--scrollbar-thumb", "Scroll bar", "#c1c7cf", "#4a5565"),
  follows("Surfaces", "--scrollbar-track", "Scroll bar track", "--rail"),

  t("Text", "--ink", "Text", "#1c2430", "#e4e9f0"),
  t("Text", "--ink2", "Text, quiet", "#4a5565", "#a3afbf"),
  follows("Text", "--link", "Links", "--accent"),
  t("Text", "--folder", "Folder icons", "#c58a12"),

  t("Accent", "--accent", "Accent", "#1f5fbf", "#7fb0f5"),
  t("Accent", "--accent-ink", "Text on accent", "#ffffff"),
  t("Accent", "--cur-bg", "Selected item", "#e1ebf8", "#22344d"),
  follows("Accent", "--focus", "Focus ring", "--accent"),
  follows("Accent", "--selection", "Selected text", "--cur-bg"),
  follows("Accent", "--btn-primary", "Primary button", "--nav"),
  t("Accent", "--btn-primary-ink", "Primary button text", "#ffffff"),

  t("Notices", "--warn-bg", "Notice", "#fdf7e7", "#2d2412"),
  t("Notices", "--warn-line", "Notice outline", "#f0d58c", "#6b5420"),
  t("Notices", "--warn-ink", "Notice text", "#7a4e06", "#f2d38a"),
  t("Notices", "--danger", "Danger and errors", "#b42318", "#f97066"),
  t("Notices", "--danger-ink", "Text on danger buttons", "#ffffff"),
  t("Notices", "--success", "Added (history, diffs)", "#2da44e"),
  t("Notices", "--removed", "Removed (history, diffs)", "#cf222e"),

  follows("Content", "--tag-bg", "Tags and pills", "--cur-bg"),
  follows("Content", "--tag-ink", "Tag text", "--accent"),
  t("Content", "--code-bg", "Code block", "#0f1720"),
  t("Content", "--code-ink", "Code text", "#e2e8f0"),
  t("Content", "--code-comment", "Code: comments", "#7f8ea3"),
  t("Content", "--code-keyword", "Code: keywords", "#c792ea"),
  t("Content", "--code-string", "Code: strings", "#a5d6a7"),
  t("Content", "--code-number", "Code: numbers", "#f9a66c"),
  t("Content", "--code-variable", "Code: variables", "#82aaff"),
  t("Content", "--code-title", "Code: names", "#7dd3fc"),
  t("Content", "--code-deletion", "Code: deletions", "#f97066"),
  t("Content", "--code-meta", "Code: other", "#94a3b8"),
];

const BY_NAME = new Map(THEME_TOKENS.map((tk) => [tk.name, tk]));

/** Text on a background, held to WCAG AA (4.5:1). A pair below it is a warning, never a refusal. */
export const CONTRAST_PAIRS: [text: string, background: string][] = [
  ["--ink", "--paper"], ["--ink2", "--paper"], ["--ink", "--rail"], ["--ink2", "--rail"], ["--link", "--paper"],
  ["--nav-fg", "--nav"], ["--nav-ink", "--nav"], ["--logo-ink", "--logo-bg"], ["--search-ink", "--search-bg"], ["--search-ink2", "--search-bg"],
  ["--badge-ink", "--badge-bg"], ["--btn-primary-ink", "--btn-primary"], ["--warn-ink", "--warn-bg"],
  ["--danger", "--paper"], ["--tag-ink", "--tag-bg"], ["--code-ink", "--code-bg"], ["--accent", "--cur-bg"],
];

export type Mode = "day" | "night";
export type ThemeOverrides = { day: Record<string, string>; night: Record<string, string> };
export const EMPTY_THEME: ThemeOverrides = { day: {}, night: {} };

export const HEX = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i;

/** #abc becomes #aabbcc, lower case; anything else is null. */
export function normalizeHex(v: string): string | null {
  const s = v.trim();
  if (!HEX.test(s)) return null;
  const h = s.slice(1).toLowerCase();
  return "#" + (h.length === 3 ? [...h].map((c) => c + c).join("") : h);
}

const rgb = (hex: string) => { const h = normalizeHex(hex)!.slice(1); return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)); };
const toHex = (c: number[]) => "#" + c.map((x) => Math.round(Math.min(255, Math.max(0, x))).toString(16).padStart(2, "0")).join("");

/** A color lightened with white, as CSS color-mix(in srgb, color, #fff p%) gives it. */
export function mixWhite(hex: string, percent: number) {
  return toHex(rgb(hex).map((c) => c + (255 - c) * (percent / 100)));
}

/** One token's value in a mode: its override, else what it follows, else its default. */
export function valueOf(theme: ThemeOverrides, mode: Mode, name: string): string {
  const own = theme[mode][name];
  if (own) return normalizeHex(own) ?? own;
  const tk = BY_NAME.get(name)!;
  if (!tk.from) return tk[mode];
  const base = valueOf(theme, mode, tk.from);
  return tk.mix ? mixWhite(base, tk.mix) : base;
}

/** Every token's value in one mode. */
export function resolved(theme: ThemeOverrides, mode: Mode): Record<string, string> {
  return Object.fromEntries(THEME_TOKENS.map((tk) => [tk.name, valueOf(theme, mode, tk.name)]));
}

function luminance(hex: string): number {
  const [r, g, b] = rgb(hex).map((c) => c / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** The WCAG contrast ratio of two colors, 1 to 21. */
export function contrast(a: string, b: string): number {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}

/** The text pairs below 4.5:1 in one mode's values. */
export function lowContrast(values: Record<string, string>): { text: string; background: string; ratio: number }[] {
  return CONTRAST_PAIRS.map(([text, background]) => ({ text, background, ratio: contrast(values[text], values[background]) })).filter((p) => p.ratio < 4.5);
}

/** Only real overrides, as #rrggbb: known names, valid colors. */
export function clean(theme: Partial<ThemeOverrides> | null | undefined): ThemeOverrides {
  const one = (m: Record<string, string> | undefined) => Object.fromEntries(Object.entries(m ?? {})
    .filter(([k]) => BY_NAME.has(k)).map(([k, v]) => [k, typeof v === "string" ? normalizeHex(v) : null]).filter((e): e is [string, string] => !!e[1]));
  return { day: one(theme?.day), night: one(theme?.night) };
}

/** The stylesheet that puts a theme's overrides over theme.css: Day on the light page, Night on the dark one. */
export function themeCss(theme: ThemeOverrides): string {
  const c = clean(theme);
  const block = (sel: string, vals: Record<string, string>) => {
    const lines = Object.entries(vals).map(([k, v]) => `${k}: ${v};`);
    return lines.length ? `${sel} { ${lines.join(" ")} }\n` : "";
  };
  return block(':root:not([data-theme="dark"])', c.day) + block(':root[data-theme="dark"]', c.night);
}

/** Puts the theme on the page now: one style element after theme.css, replaced on each call. */
export function applyTheme(theme: ThemeOverrides | null | undefined) {
  if (typeof document === "undefined") return;
  let el = document.getElementById("kb-theme") as HTMLStyleElement | null;
  if (!el) { el = document.createElement("style"); el.id = "kb-theme"; document.head.append(el); }
  el.textContent = themeCss(theme ?? EMPTY_THEME);
}

export type ThemePreset = { name: string; theme: ThemeOverrides };

/** Starting points. Each Day and Night set holds every text pair at 4.5:1 or more (a test checks). */
export const PRESETS: ThemePreset[] = [
  { name: "Aerie (default)", theme: EMPTY_THEME },
  {
    name: "Slate",
    theme: {
      day: { "--badge-bg": "#c62f37", "--nav": "#2b3440", "--logo-bg": "#5eead4", "--logo-ink": "#13232b", "--accent": "#0f766e", "--cur-bg": "#d9f2ef", "--avatar-bg": "#99f6e4", "--avatar-ink": "#13232b" },
      night: { "--badge-bg": "#c62f37", "--nav": "#161b22", "--btn-primary": "#1f6f68", "--logo-bg": "#5eead4", "--logo-ink": "#13232b", "--accent": "#5eead4", "--accent-ink": "#13232b", "--cur-bg": "#163a38", "--avatar-bg": "#5eead4", "--avatar-ink": "#13232b" },
    },
  },
  {
    name: "Forest",
    theme: {
      day: { "--badge-bg": "#c62f37", "--nav": "#1f3d2b", "--logo-bg": "#e9c46a", "--logo-ink": "#1f3d2b", "--accent": "#2d6a4f", "--cur-bg": "#dcefe4", "--avatar-bg": "#95d5b2", "--avatar-ink": "#1f3d2b", "--folder": "#8a6d1c" },
      night: { "--badge-bg": "#c62f37", "--nav": "#122218", "--btn-primary": "#2d6a4f", "--logo-bg": "#e9c46a", "--logo-ink": "#1f3d2b", "--accent": "#8fd3ad", "--accent-ink": "#122218", "--cur-bg": "#1d3a2b", "--avatar-bg": "#95d5b2", "--avatar-ink": "#122218" },
    },
  },
  {
    name: "Plum",
    theme: {
      day: { "--badge-bg": "#c62f37", "--nav": "#3b1f4a", "--logo-bg": "#f4a6c6", "--logo-ink": "#3b1f4a", "--accent": "#7b3fa0", "--cur-bg": "#efe3f6", "--avatar-bg": "#d8b4fe", "--avatar-ink": "#3b1f4a" },
      night: { "--badge-bg": "#c62f37", "--nav": "#1e1226", "--btn-primary": "#6a3a88", "--logo-bg": "#f4a6c6", "--logo-ink": "#3b1f4a", "--accent": "#d2a8f0", "--accent-ink": "#1e1226", "--cur-bg": "#35234a", "--avatar-bg": "#d2a8f0", "--avatar-ink": "#1e1226" },
    },
  },
];
