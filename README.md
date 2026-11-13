# Aerie

Aerie is an internal knowledge and documentation system for runbooks, how-to articles, application records, and network documentation. Each team works in its own permissioned space.

Written for security review; separates Phase 1 (in progress) from planned phases.

| | |
|---|---|
| Status | Design approved 2026-11-08. Phase 1 foundation in progress. Not in production. |
| Owner | Project owner |
| Stack | .NET 10 LTS (ASP.NET Core), React with TypeScript, SQL Server |
| Hosting | Azure App Service on private endpoints, or IIS on an internal server |
| Exposure | Internal network and VPN only, across all hostnames |
| Design document | [docs/superpowers/specs/2026-11-08-aerie-design.md](docs/superpowers/specs/2026-11-08-aerie-design.md) |

## Why the company needs it

The company has no central documentation system. Fixes sit in ticket comments and personal files, knowledge walks out when engineers leave, and application contract and support records are scattered across inboxes.

Products like Confluence cover this scope, but their sprawl makes them slow for non-IT staff to adopt. Aerie covers the features the company needs without training overhead.

## What it does

### Built in Phase 1 (in progress)

| Feature | Scope |
|---|---|
| Spaces | Per-team spaces (Service Desk, Infrastructure Operations, etc.) bound to Active Directory / Entra ID groups. |
| Roles | Per-space roles: Reader, Contributor, Supervisor, Owner. Users without a role in a space cannot see that the space exists. |
| Sign-in | Microsoft Entra ID SSO, local accounts with passkeys, and one break-glass account for identity outages. |
| Documents | Folder tree, breadcrumbs, and a rich-text editor (code blocks, tables, images, attachments) with numbered version history and side-by-side diffs. |
| Recycle bin | Restores deleted documents to their original path. |
| Exports | Pages export to PDF or Word with secure links stripped. Tables export to CSV or Excel. Code blocks download with the matching file extension. |
| API | REST API with scoped, expiring tokens. |
| Plugins | Core plugin host included in v1. Email delivery ships as the first plugin. |

### Planned for Phase 2

- **Application registry:** Tracks versions per environment, support contacts, and links to contract records in a password manager (e.g., Bitwarden). Retiring an application archives linked articles.
- **Content aging:** Flags pages unreviewed past a configured threshold and surfaces them in aging reports.
- **Engagement and reporting:** Article ratings, comments, email notifications, and activity reports.
- **Public help space:** Readable by any signed-in employee for self-service articles linked from support tickets.
- **Identity and TLS:** LDAP and SAML sign-in, plus automated certificate renewal.

### Planned for Phase 3

- **Network and asset directory:** Admin-defined schemas for servers, services, circuits, network drops, switch port maps, locations, and contacts (including Area Tech Leads per division group).
- **Location map:** Pin map of sites and assigned Area Tech Leads.
- **Meraki plugin:** Imports selected networks, VLANs, and DNS settings. Imported fields are read-only in Aerie.
- **SolarWinds Service Desk plugin:** "This fixed my problem" action posts the resolution and article link to a ticket as a private comment.
- **Contribution scoring:** Scores changes for supervisors by substantive diff size, ignoring whitespace and minor edits.
- **AI-assisted search:** Optional, disabled by default.

## Security

### Exposure

- Internal hostnames only. On Azure, the app, database, storage, and Key Vault run behind private endpoints with public network access disabled.
- No anonymous pages. Only load-balancer health check endpoints respond without authentication.

### Identity

- Entra ID sign-in is restricted to assigned groups, with guest accounts blocked by default. Only the Phase 2 public help space is readable by all signed-in employees.
- Scheduled group sync revokes access on group removal without waiting for sign-out.
- Local accounts are invite-only (no self-registration) and require passkeys or NIST SP 800-63B passwords with failed-attempt lockout.
- Local sign-in can be disabled after SSO is configured. The toggle remains locked until an administrator completes an SSO sign-in.
- Every use of the break-glass account is logged and alerts all administrators by email.

### Authorization

- All permission checks run server-side through a single authorization service covering search results, exports, API responses, and links.
- Sensitive spaces (Legal, HR) deny default administrator access. Administrators must request Owner approval or trigger a 4-hour emergency override that immediately emails all space Owners.
- Users can request space access in-app. Owners approve or deny via in-app notifications.

### Data protection

- Aerie stores no passwords or contract secrets, only permission-filtered links to the password manager. Secure links are stripped from exports and blocked in the public help space.
- Editor content is stored as structured data and sanitized against an allow-list before rendering, backed by a strict Content Security Policy that blocks inline scripts.
- Attachments are served with `Content-Disposition: attachment` and never rendered inline.
- Emptying a space recycle bin soft-deletes items for the retention period. Early purge requires two-administrator approval, and legal holds block purging.

### Audit

- Append-only audit log covering sign-ins, permission changes, sensitive-space access, settings changes, deletions, restores, exports, token use, break-glass events, and plugin changes.
- Entries are SHA-256 hash-chained, with scheduled integrity verification.
- Supports log streaming to a SIEM (e.g., Microsoft Sentinel).

### API tokens

- Scoped to read or write (per-space or global), bound to the owner's permissions, with mandatory expiration.
- Stored hashed and displayed once at creation.
- Fixed token prefix for secret scanners.
- Revocation takes effect immediately across all nodes.

### Plugins

- A plugin runs inside the app, with the app's rights. Install only a plugin from a publisher you trust as much as the app itself.
- A plugin is either shipped in the app's folder (built-in) or installed by an Admin, who uploads its package. The package's SHA-256 is written to the audit log with the install. There is no allow-list of hashes: a package an Admin uploads is loaded.
- The HTTP client the app gives a plugin refuses a request to a host the plugin's manifest does not declare. A redirect from a declared host is followed.
- A value saved for a secret setting is stored in the database encrypted with ASP.NET Core Data Protection. The API never returns it and it is not written to logs.
- Plugin install, update, enable, disable, removal and data deletion are audited.
- A plugin that keeps failing is switched off by the app. Safe Mode starts the app without uploaded plugins.

### Supply chain

- Dependencies are minimized and restricted to actively maintained packages under permissive licenses (MIT, Apache 2.0, BSD).
- Builds fail on any high or critical vulnerability in direct or transitive dependencies (enforced in CI, where transitive advisory `GHSA-846f-f764-76mx` was blocked and pinned before merge).
- Locked package versions, CycloneDX SBOM generation per build, weekly Dependabot updates, and CodeQL scanning on every change.

### Availability and patching

Runs on two or more nodes behind a load balancer. Updates use rolling node drains with shared session state and backward-compatible database migrations.

## How it is built

- Built in-house on open-source components with no licensing cost. Company-specific configuration is kept out of the source tree so the project can be open-sourced later.
- Code is written primarily by AI coding agents (Claude and Cursor) against the approved design and implementation plan (`AGENTS.md`). Every task starts with a failing test, and merges require passing tests and CI security gates.
- The product name lives in a single brand setting, while the codebase uses a neutral `Kb` prefix.

## Repository layout

| Path | Contents |
|---|---|
| `src/Kb.Plugins.Abstractions` | Plugin contract interfaces |
| `src/Kb.Plugins.Data` | Plugin contract for a plugin's own tables |
| `src/Kb.Core` | Domain logic: permissions, audit, access requests, notifications, plugin host |
| `src/Kb.Data` | Data access |
| `src/Kb.Web` | Web application and REST API |
| `plugins/` | First-party plugins |
| `tests/` | Unit and integration tests |
| `docs/` | Design specs, implementation plans, plugin and deployment guides |

## Plugin guides

- [Developer guide](docs/plugins/developer-guide.md): how to write a plugin, with the sample plugin's code.
- [Manifest reference](docs/plugins/manifest-reference.md): every property of `kb-plugin.json`.
- [Admin guide](docs/plugins/admin-guide.md): install, update, remove, settings, locks, Safe Mode.
- [Action reference](docs/plugins/action-reference.md): every action a plugin can hook.

## API

- [Agent API](docs/agent-api.md): how an agent or another system signs in with a token, finds, reads and changes pages.
- [API reference](docs/api-reference.md): every route, the action it sends, and whether an API token may call it.

## Open items

- DNS provider for the company's public zone (required for Phase 2 ACME certificate automation).
- Open-source license selection prior to any public release.
