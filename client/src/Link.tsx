import type { AnchorHTMLAttributes, MouseEvent } from "react";

/** Addresses of the app's screens: URLs carry IDs only, never names, so renames never break links. */
export const to = {
  home: () => "/",
  space: (id: string) => `/s/${id}`,
  page: (id: string) => `/p/${id}`,
  bin: (spaceId: string) => `/s/${spaceId}/bin`,
  tag: (tag: string) => `/search?tag=${encodeURIComponent(tag)}`,
  search: (q: string) => `/search?q=${encodeURIComponent(q)}`,
  inventory: (appId?: string | null) => (appId ? `/inventory/${appId}` : "/inventory"),
  imports: (itemId?: string | null) => (itemId ? `/import/${itemId}` : "/import"),
  admin: (section: string, spaceId?: string | null) => `/admin/${section}${spaceId ? `/${spaceId}` : ""}`,
  settings: (section: string) => `/settings/${section}`,
};

/** True for the click the app handles itself: the main button, no modifier key. Any other opens the address as the browser does. */
export function isPlainClick(e: MouseEvent) {
  return e.button === 0 && !e.metaKey && !e.ctrlKey && !e.shiftKey && !e.altKey;
}

/**
 * A real link to one of the app's screens: middle-click, Ctrl/Cmd-click, Shift-click and the browser's "Open in new tab"
 * work on its address; a plain click moves within the app (onNav), as before.
 */
export function Link({ href, onNav, onClick, children, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement> & { href: string; onNav: () => void }) {
  return (
    <a href={href} {...rest} onClick={(e) => {
      onClick?.(e);
      if (e.defaultPrevented || !isPlainClick(e)) return;
      e.preventDefault();
      onNav();
    }}>{children}</a>
  );
}
