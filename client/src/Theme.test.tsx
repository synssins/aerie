import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "./Dialogs";
import { ThemeColors } from "./admin/ThemeColors";
import type { Brand } from "./api";
import {
  applyTheme, clean, contrast, EMPTY_THEME, lowContrast, mixWhite, normalizeHex, PRESETS, resolved, THEME_TOKENS, themeCss, valueOf,
} from "./themeTokens";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

/** The variables of one block of theme.css. */
function block(css: string, selector: string): Record<string, string> {
  const start = css.indexOf(selector + " {");
  const body = css.slice(start + selector.length + 2, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/(--[a-z0-9-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
}

describe("theme tokens", () => {
  const css = readFileSync(resolve(__dirname, "theme.css"), "utf8");
  const day = block(css, ":root");
  const night = { ...day, ...block(css, ':root[data-theme="dark"]') };

  it("are theme.css's own variables and defaults, so the default theme looks as it did", () => {
    for (const t of THEME_TOKENS) {
      if (t.from) {
        const expected = t.mix ? `color-mix(in srgb, var(${t.from}), #fff ${t.mix}%)` : `var(${t.from})`;
        expect(day[t.name], t.name).toBe(expected);
        expect(night[t.name], t.name).toBe(t.night || expected);
      } else {
        expect(day[t.name], t.name).toBe(t.day);
        expect(night[t.name], t.name).toBe(t.night);
      }
    }
  });

  it("no color is left outside a variable, beyond shadows, the dialog backdrop and the white behind a QR code", () => {
    const rules = css.slice(css.indexOf("* { box-sizing"));
    const stray = rules.split("\n").filter((l) => /#[0-9a-fA-F]{3,8}\b/.test(l) && !/color-mix\(in srgb, var\(--[a-z-]+\), #fff/.test(l) && !l.includes(".code-qr") && !l.includes(".logo-swatch.light"));
    expect(stray).toEqual([]);
  });

  it("read hex in both forms, follow another token, and mix as CSS does", () => {
    expect(normalizeHex("#ABC")).toBe("#aabbcc");
    expect(normalizeHex("blue")).toBeNull();
    expect(mixWhite("#14304f", 10)).toBe("#2c4561");
    expect(valueOf(EMPTY_THEME, "day", "--btn-primary")).toBe("#14304f");
    expect(valueOf({ day: { "--nav": "#000000" }, night: {} }, "day", "--btn-primary")).toBe("#000000"); // follows the header
    expect(valueOf({ day: { "--nav": "#000000", "--btn-primary": "#123456" }, night: {} }, "day", "--btn-primary")).toBe("#123456");
    expect(valueOf({ day: { "--nav": "#000000" }, night: {} }, "night", "--btn-primary")).toBe("#7fb0f5"); // Night keeps its own
    expect(contrast("#000000", "#ffffff")).toBeCloseTo(21, 0);
  });

  it("only known colors reach the page, Day on the light page and Night on the dark one", () => {
    expect(clean({ day: { "--nav": "#ABC", "--width": "#fff", "--ink": "red" }, night: {} })).toEqual({ day: { "--nav": "#aabbcc" }, night: {} });
    expect(themeCss({ day: { "--nav": "#000000" }, night: { "--nav": "#111111" } }))
      .toBe(':root:not([data-theme="dark"]) { --nav: #000000; }\n:root[data-theme="dark"] { --nav: #111111; }\n');
    // Through the CSS object model, never a style element: the content security policy refuses inline style elements.
    const css = () => [...document.adoptedStyleSheets].flatMap((s) => [...s.cssRules].map((r) => r.cssText)).join(" ");
    applyTheme({ day: { "--paper": "#fefefe" }, night: {} });
    expect(document.querySelector("style")).toBeNull();
    expect(css()).toContain("--paper: #fefefe;");
    applyTheme({ day: {}, night: { "--paper": "#101010" } });
    expect(document.adoptedStyleSheets).toHaveLength(1);
    expect(css()).not.toContain("#fefefe");
    expect(css()).toContain("--paper: #101010;");
    applyTheme(null);
    expect(css()).toBe("");
  });

  it("the default Night primary button stands out: 3:1 on the page, the tree and the header, its text 4.5:1", () => {
    const v = resolved(EMPTY_THEME, "night");
    for (const bg of ["--paper", "--rail", "--stage", "--nav"]) expect(contrast(v["--btn-primary"], v[bg]), bg).toBeGreaterThanOrEqual(3);
    expect(contrast(v["--btn-primary-ink"], v["--btn-primary"])).toBeGreaterThanOrEqual(4.5);
  });

  it("each preset holds every text pair at 4.5:1, Day and Night", () => {
    for (const p of PRESETS.filter((x) => x.name !== "Aerie (default)"))
      for (const m of ["day", "night"] as const) expect(lowContrast(resolved(p.theme, m)), `${p.name} ${m}`).toEqual([]);
  });
});

describe("Theme colors", () => {
  const brand: Brand = { productName: "Acme KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1f5fbf", theme: { day: { "--nav": "#222222" }, night: {} } };
  afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

  function server() {
    const calls: { key: string; body: unknown }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ key: `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`, body: init?.body ? JSON.parse(init.body as string) : undefined });
      return new Response("{}", { status: 200 });
    }));
    return calls;
  }

  it("every color, Day and Night, with a picker and a hex field; saves only what differs from the default", async () => {
    const calls = server();
    const onChanged = vi.fn(async () => {});
    render(<DialogProvider><ThemeColors brand={brand} onChanged={onChanged} /></DialogProvider>);
    for (const g of ["Header", "Surfaces", "Text", "Accent", "Notices", "Content"]) expect(screen.getByRole("columnheader", { name: g })).toBeTruthy();
    expect((screen.getByLabelText("Header, Day hex") as HTMLInputElement).value).toBe("#222222");
    expect((screen.getByLabelText("Header, Night hex") as HTMLInputElement).value).toBe("#0d1b2c");
    expect(screen.getByRole("button", { name: "Reset Header, Day" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Reset Header, Night" })).toBeNull();

    // A bad value is marked and not kept; a good one, in either form, is.
    const hex = screen.getByLabelText("Page, Night hex");
    await userEvent.clear(hex);
    await userEvent.type(hex, "#12");
    expect(hex.getAttribute("aria-invalid")).toBe("true");
    await userEvent.type(hex, "3");
    expect(hex.getAttribute("aria-invalid")).toBe("false");
    // The preview shows Night in the new page color.
    expect((screen.getByRole("figure", { name: "Preview, Night" }) as HTMLElement).style.getPropertyValue("--paper")).toBe("#112233");
    await userEvent.click(screen.getByRole("button", { name: "Reset Header, Day" }));
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.key === "PUT /admin/brand/theme")?.body).toEqual({ day: {}, night: { "--paper": "#112233" } }));
    expect(onChanged).toHaveBeenCalled();
  });

  it("a preset fills the fields to adjust and save; contrast is a warning; Reset all asks first", async () => {
    const calls = server();
    render(<DialogProvider><ThemeColors brand={{ ...brand, theme: null }} onChanged={async () => {}} /></DialogProvider>);
    await userEvent.selectOptions(screen.getByLabelText("Preset"), "Forest");
    await userEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect((screen.getByLabelText("Header, Day hex") as HTMLInputElement).value).toBe("#1f3d2b");
    expect(calls.some((c) => c.key === "PUT /admin/brand/theme")).toBe(false); // nothing saved yet

    const ink = screen.getByLabelText("Text, Day hex");
    await userEvent.clear(ink);
    await userEvent.type(ink, "#eeeeee");
    expect(screen.getByText(/Low contrast/).parentElement!.textContent).toContain("Day: Text on Page");
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false); // a warning never blocks

    await userEvent.click(screen.getByRole("button", { name: "Reset all to default" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reset all" }));
    await waitFor(() => expect(calls.find((c) => c.key === "PUT /admin/brand/theme")?.body).toEqual({ day: {}, night: {} }));
  });
});
