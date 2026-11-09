// Thin API client. Same-origin cookie auth; the server never redirects API calls, it returns 401/403.
// X-Kb-Version: when the server is newer than this bundle (A/B patch flip), reload at the next navigation, never mid-edit.

const bundleVersion: string | null = null;
let serverVersion: string | null = null;
export let reloadPending = false;

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api/v1${path}`, {
    method,
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const v = res.headers.get("X-Kb-Version");
  if (v) {
    if (serverVersion && serverVersion !== v) reloadPending = true;
    serverVersion ??= v;
  }
  if (!res.ok) throw new ApiError(res.status, res.statusText);
  if (res.status === 204 || res.headers.get("content-length") === "0") return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(p: string) => call<T>("GET", p),
  post: <T>(p: string, b?: unknown) => call<T>("POST", p, b ?? {}),
  put: <T>(p: string, b?: unknown) => call<T>("PUT", p, b ?? {}),
  patch: <T>(p: string, b?: unknown) => call<T>("PATCH", p, b ?? {}),
};

export function reloadIfPending(): void {
  if (reloadPending) window.location.reload();
}

void bundleVersion;

export type Me = { id: string; userName: string; displayName: string; isAdmin: boolean; isBreakGlass: boolean };
export type Brand = { productName: string; logoLightPath: string; logoDarkPath: string; primaryColor: string };
export type Space = { id: string; key: string; name: string; description: string | null; isSensitive: boolean; isPublic: boolean; myRole: string | null };
export type Notice = { id: string; kind: string; title: string; body: string | null; link: string | null; createdAt: string; readAt: string | null };
