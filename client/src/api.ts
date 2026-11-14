// Thin API client. Same-origin cookie auth; the server never redirects API calls, it returns 401/403.
// X-Kb-Version: when the server is newer than this bundle (A/B patch flip), reload at the next navigation, never mid-edit.

const bundleVersion: string | null = null;
let serverVersion: string | null = null;
export let reloadPending = false;

export class ApiError extends Error {
  /** The answer's JSON, when it had one: `{ detail }` or `{ error }` for a refusal, `{ errors }` for fields. */
  constructor(public status: number, message: string, public body: { error?: string; detail?: string; errors?: Record<string, string[]> } | null = null) {
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
  if (!res.ok) throw new ApiError(res.status, res.statusText, await res.json().catch(() => null));
  if (res.status === 204 || res.headers.get("content-length") === "0") return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(p: string) => call<T>("GET", p),
  post: <T>(p: string, b?: unknown) => call<T>("POST", p, b ?? {}),
  put: <T>(p: string, b?: unknown) => call<T>("PUT", p, b ?? {}),
  patch: <T>(p: string, b?: unknown) => call<T>("PATCH", p, b ?? {}),
  del: <T>(p: string) => call<T>("DELETE", p),
};

export function reloadIfPending(): void {
  if (reloadPending) window.location.reload();
}

void bundleVersion;

export type Me = { id: string; userName: string; displayName: string; isAdmin: boolean; isBreakGlass: boolean; canAnnounce: boolean; permissions?: string[] | null };
export type Brand = { productName: string; logoLightUrl: string | null; logoDarkUrl: string | null; primaryColor: string; signInNote?: string | null };
export type Space = {
  id: string; key: string; name: string; description: string | null; isSensitive: boolean; visibility: "Private" | "SignedIn" | "Anyone"; myRole: string | null;
  commentsEnabled: boolean; agingDays: number; agingAudience: "Everyone" | "Supervisors"; recycleDays: number | null; createdAt: string;
};
export type NotifyPref = { inApp: boolean; email: boolean };
export type UserSettings = { theme: "light" | "dark" | "system"; width: "boxed" | "full"; startPage: string; codeLanguage: string; notify: Record<string, NotifyPref> };
export type Notice = { id: string; kind: string; title: string; body: string | null; link: string | null; createdAt: string; readAt: string | null };
export type TreeNode = { id: string; parentId: string | null; kind: "Folder" | "Page"; title: string; sort: number };
export type PageVersion = { version: string; at: string; author: string; note: string | null };
export type Page = {
  id: string; spaceId: string; parentId: string | null; kind: "Folder" | "Page"; title: string; contentJson: string; version: string;
  updatedAt: string; updatedBy: string; path: { id: string; title: string }[]; history: PageVersion[]; canEdit: boolean; tags: string[];
  canDelete: boolean; draft: { mine: boolean; by: string; updatedAt: string } | null; attachments: Attachment[]; maxAttachmentBytes: number;
  details?: PageDetails | null;
};
/** Who owns a process or escalation path: a person (user id) or a group (its key). */
export type OwnerRef = { type: "User" | "Group"; id: string; name: string };
/** servers: some of the application's servers; none means all of them. */
export type AppliesTo = { applicationId: string; name: string; servers: string[] };
export type PageDetails = { appliesTo: AppliesTo[]; owner: OwnerRef | null; reviewMonths: number | null };
export type InvAppRow = { id: string; name: string; vendor: string | null; owner: OwnerRef | null; installs: number };
export type InvHistory = { at: string; by: string; kind: "Added" | "Changed" | "Removed"; from: string | null; to: string | null };
export type InvInstall = { id: string; server: string; version: string; changedAt: string; changedBy: string; history: InvHistory[] };
export type InvApp = { id: string; name: string; vendor: string | null; owner: OwnerRef | null; notes: string | null; installs: InvInstall[];
  pages: { id: string; title: string; spaceName: string; servers: string[] }[]; canEdit: boolean };
export type SyncChange = { application: string; server: string; kind: "Added" | "Changed" | "Unchanged" | "Removed"; from: string | null; to: string | null };
export type SyncResult = { added: number; changed: number; unchanged: number; removed: number; newApplications: number; changes: SyncChange[] };
export type InvRule = { kind: "Everyone" | "Space" | "Role" | "User" | "Group" | "Token"; id: string | null; name?: string | null };
export type Attachment = { id: string; name: string; size: number; url: string; at: string };
export type Draft = { title: string; contentJson: string; tags: string[]; baseVersion: string; updatedAt: string };
export type BinItem = { id: string; spaceId: string; spaceName: string; kind: "Folder" | "Page"; title: string; location: string; items: number; deletedAt: string; deletedBy: string; hidden: boolean };
export type SearchHit = {
  id: string; spaceId: string; spaceName: string; kind: "Folder" | "Page"; title: string; path: string[]; url: string; version: string;
  updatedAt: string; tags: string[]; snippet: string; score: number; matched: string[];
};
export type SearchResponse = { query: string; total: number; limit: number; offset: number; hits: SearchHit[] };
export type DiffLine = { op: "Same" | "Add" | "Del"; text: string };
export type Announcement = { id: string; title: string; body: string; by: string; createdAt: string; expiresAt: string | null };
export type Home = { announcements: Announcement[]; recent: { id: string; title: string; spaceId: string; spaceName: string; updatedAt: string; updatedBy: string }[] };

export type TokenScope = { spaceId: string | null; access: "Read" | "Write" };
export type ApiToken = {
  id: string; name: string; kind: "Personal" | "Service"; prefix: string; scopes: TokenScope[]; ownerId: string; owner: string;
  createdAt: string; expiresAt: string; revokedAt: string | null; lastUsedAt: string | null; state: "Active" | "Expired" | "Revoked";
};
export type PluginSetting = { key: string; label: string; secret: boolean; required: boolean; type: string; options: string[] | null; restart: boolean };
export type AdminUser = { id: string; userName: string; displayName: string; source: string; isAdmin: boolean; isBreakGlass: boolean; createdAt: string; deactivatedAt: string | null; canAnnounce: boolean; permissions: string[]; groups?: string[]; codesExempt?: boolean };
export type Group = { id: string; key: string; name: string; isLocal: boolean; members: number };
export type GroupMember = { userId: string; userName: string; displayName: string; source: string };
export type Principal = { type: "User" | "Group"; id: string; name: string; detail: string };
export type SpaceRoleName = "Reader" | "Contributor" | "Supervisor" | "Owner";
export type RoleAssignment = { id: string; principalType: "User" | "Group"; principalId: string; role: SpaceRoleName; name: string | null; roleId: string; roleName: string | null };
/** The ten space permissions, by name. */
export const PERMISSIONS = [
  ["Read", "Read"], ["CreateEdit", "Create and edit"], ["MoveDeleteOwn", "Move and delete own"], ["MoveDeleteOthers", "Move and delete others'"],
  ["SeeOthersDrafts", "See others' drafts"], ["ReviewTeamHistory", "Review team history"], ["ManageBinAgingComments", "Manage bin, aging and comments"],
  ["RemoveAttribution", "Remove attribution"], ["ManageRolesSettingsSensitive", "Manage members and settings"], ["ApproveAccessRequests", "Approve access requests"],
] as const;
export type Permission = (typeof PERMISSIONS)[number][0];
/** A role of the global list. builtIn: the level of a built-in role; null for one an Admin added. inUse: how many assignments name it. */
export type RoleDef = { id: string; name: string; permissions: Permission[]; builtIn: SpaceRoleName | null; inUse: number };
/** A role that manages members and settings counts as Owner: only an Admin gives, changes or removes it. */
export const ownerGrade = (r: RoleDef | undefined) => !!r?.permissions.includes("ManageRolesSettingsSensitive");
export type PluginPermissions = { permissions: { key: string; label: string; scope: string; defaultRole: string; role: string | null }[] };
export type PluginHealth = {
  plugins: {
    id: string;
    checks: { name: string; status: "Healthy" | "Degraded" | "Unhealthy"; text: string | null }[];
    jobs: { name: string; everyMinutes: number; lastRunAt: string | null; nextRunAt: string | null; lastError: string | null }[];
  }[];
};
export type PluginInfo = {
  id: string; name: string; version: string; publisher: string; description: string | null; origin: "BuiltIn" | "Uploaded"; state: "Enabled" | "Disabled" | "DisabledByCore";
  reason: string | null; locked: boolean; loaded: boolean; source: string; requires: string[]; requiredBy: string[]; extensionPoints: string[]; outboundHosts: string[];
  permissions: { key: string; label: string; scope: string; defaultRole: string }[]; enforcing: boolean; settings: PluginSetting[]; replaces: string[]; removed: boolean;
};
export type LogField = { key: string; label: string; type: "text" | "secret" | "select"; options: string[] | null };
export type LogKind = { kind: string; label: string; fields: LogField[] };
export type LogTarget = {
  id: string; name: string; kind: string; settings: Record<string, string | null>; secretsSet: string[]; enabled: boolean; lastSequence: number;
  lastShippedAt: string | null; lastError: string | null; lastErrorAt: string | null; createdAt: string;
};
export type LogsInfo = { kinds: LogKind[]; targets: LogTarget[]; head: number };
export type StorageField = { key: string; label: string; type: "text" | "secret" };
export type StorageKind = { kind: string; label: string; fields: StorageField[] };
export type StoragePlace = { id: string; name: string; kind: string; settings: Record<string, string | null>; secretsSet: string[]; role: "Active" | "Target" | "Old"; createdAt: string };
export type StorageMove = {
  id: string; fromId: string; toId: string; state: "Copying" | "Copied" | "Switched" | "Cancelled"; total: number; copied: number; checked: number; failed: number;
  lastError: string | null; preview: boolean; startedAt: string; finishedAt: string | null;
};
export type StorageInfo = { kinds: StorageKind[]; places: StoragePlace[]; move: StorageMove | null };

export const EDIT_ROLES =["Contributor", "Supervisor", "Owner"];
export const BIN_ROLES = ["Supervisor", "Owner"];
export const fmtDateTime = (iso: string) => new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
export const fmtSize = (n: number) => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`;

/** Sends a file as the raw request body, the way the upload endpoints take it. */
export async function upload<T>(path: string, file: File): Promise<T> {
  const r = await fetch(`/api/v1${path}`, {
    method: "POST", body: file, credentials: "same-origin",
    headers: { "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(file.name) },
  });
  if (!r.ok) throw new ApiError(r.status, r.statusText, await r.json().catch(() => null));
  return await r.json() as T;
}

/** Tree order: folders first, then pages, each alphabetical (natural number order). */
export const byTreeOrder = (a: TreeNode, b: TreeNode) =>
  a.kind === b.kind ? a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: "base" }) : a.kind === "Folder" ? -1 : 1;
