# Aerie

Aerie is an internal knowledge and documentation system. Teams keep runbooks, how-to articles, application records and network documentation in one place, each team in its own space with its own permissions. Aerie runs inside the corporate network only. Nothing in it is reachable from the internet.

This document is written for a security review board. It describes what Aerie is, what it will do, how it protects the information in it, and what it changes for the company. It separates what is built today from what is planned.

| | |
|---|---|
| Status | Design approved 2026-11-08. Phase 1 foundation in progress. Not in production. |
| Owner | Project owner |
| Stack | .NET 10 LTS (ASP.NET Core), React with TypeScript, SQL Server |
| Hosting | Azure App Service on private endpoints, or IIS on an internal server |
| Exposure | Internal network and VPN only, for every hostname |
| Design document | [docs/superpowers/specs/2026-11-08-aerie-design.md](docs/superpowers/specs/2026-11-08-aerie-design.md) |

## Why the company needs it

The company has no documentation system today. Fixes live in people's heads, in ticket comments and in personal files. When a service desk technician works a ticket, they have nowhere to search for a known fix. When an engineer leaves, what they knew leaves with them. Contract and support details for applications are spread across inboxes.

Confluence and similar products can do this job. They are also large and complex, and staff who do not work in IT find them hard to use. Aerie aims at the features this company needs, presented simply enough that anyone can read and write in it without training.

## What it does

### Built in phase 1 (in progress)

| Feature | What it does |
|---|---|
| Spaces | Each team gets a space: Service Desk, Infrastructure Operations, and so on. Access is granted to directory groups, so it follows HR and Active Directory changes automatically. |
| Roles | Inside a space, a person is a Reader, Contributor, Supervisor or Owner. A person with no role in a space cannot see that the space exists. |
| Sign-in | Microsoft Entra ID single sign-on, local accounts with passkeys, and one break-glass account for repairing sign-in problems. |
| Documents | A folder tree, breadcrumbs and a word-processor style editor with code blocks, tables, images and file attachments. Every saved change is a numbered version. History shows who changed what, with a side-by-side comparison of any two versions. |
| Recycle bin | Deleted documents can be restored to the exact place they came from. |
| Exports | A page exports to PDF or Word with secure links removed. A table exports to CSV or Excel. A script in a code block downloads as a file with the right extension, such as `.ps1` for PowerShell. |
| API | A REST API with scoped, expiring tokens. |
| Plugins | A plugin system is part of the core from the first release. Email delivery is the first plugin. |

### Planned for phase 2

- An application registry: each application, its versions per environment, support contacts, and links to contract details held in a password manager such as Bitwarden. Retiring an application marks every related article as archived.
- Aging. A page that has not been reviewed within a set number of days shows a notice asking whether it is still accurate. Aging reports list these pages.
- Ratings, comments, email notifications and activity reports.
- A public help space. Any employee can read it after signing in, which makes it the place for self-help articles that technicians link from tickets.
- LDAP and SAML sign-in, and automatic certificate renewal.

### Planned for phase 3

- A network and asset directory with record types that administrators define themselves: servers, services, circuits, network drops, switch port maps, locations and contacts, including the Area Tech Lead for each group of divisions.
- A map of locations, with pins showing each Area Tech Lead and their sites.
- A Meraki plugin that imports networks, VLANs and DNS settings that the network team selects. Imported fields are locked against hand edits so the next sync never overwrites someone's work.
- A SolarWinds Service Desk plugin. A technician clicks "This fixed my problem" on an article, enters the ticket number, and Aerie posts the fix and link to the ticket as a private comment.
- Contribution scoring for supervisors. Points come from how much a change says, so saving a document with one added space earns nothing.
- Optional AI-assisted search, off by default.

## Security

Security is the first priority in every design decision. Ease of use is second. Where the two conflict, security wins.

### Exposure

- Every hostname is internal. On Azure, the app, database, storage and key vault sit behind private endpoints with public network access turned off.
- The app has no anonymous pages. The only endpoints that answer without sign-in are the health checks the load balancer uses.

### Identity

- Entra ID sign-in is limited to members of the groups assigned to Aerie. Guest accounts are blocked by default. The public help space is the one area every employee can read after signing in.
- Group membership refreshes on a schedule, so a person removed from a group loses access without signing out.
- Local accounts exist only by administrator invitation. There is no self-registration. Local accounts use passkeys, or passwords that follow NIST SP 800-63B, with lockout after repeated failures.
- Local sign-in can be turned off once single sign-on is configured. The setting stays locked until an administrator has signed in through single sign-on successfully, so nobody can lock everyone out by mistake.
- The break-glass account survives a single sign-on outage. Every use of it is logged and emails every administrator.

### Authorization

- Every permission check runs on the server, through one service. Search results, exports, API responses and links are filtered to what the person can read.
- A space can be flagged as sensitive, for Legal or HR material. Administrators have no quiet access to a sensitive space. An administrator requests access and a space Owner approves it inside Aerie. In an emergency, an administrator can open a sensitive space at once, and Aerie emails every Owner immediately. Emergency access ends after four hours.
- Any person can request access to a space. Owners approve or deny in the app, and a bell icon shows waiting requests and alerts.

### Protecting what is in it

- Aerie never stores passwords or contract secrets. A secure link points to the item in the password manager, and only people who can read the page see it. Exports remove secure links, and the public help space refuses them.
- Editor content is stored as structured data and passed through an allow-list sanitizer before display. A strict Content Security Policy blocks inline scripts.
- Uploaded files are always served as downloads and never displayed inline, so an uploaded HTML or SVG file cannot run in a reader's browser.
- Deletion cannot destroy data quietly. A space Owner who empties their recycle bin only hides the items from that space. The administrator copy is kept for the full retention period. Purging early takes two different administrators, one to request and one to approve. A legal hold blocks purging entirely.

### Audit

- An append-only audit log records sign-ins, permission changes, sensitive-space access, settings changes, deletions, restores, exports, token use, break-glass use and plugin changes.
- Each entry carries a SHA-256 hash of the entry before it. Editing or deleting any entry breaks the chain, and a scheduled check reports where.
- The log can stream to a SIEM such as Microsoft Sentinel.

### API tokens

- Tokens are scoped to read or write, per space or globally, and must expire.
- Aerie stores only a hash of each token and shows the token once, at creation.
- Tokens carry a fixed prefix so secret scanners can find one that leaks into a file or chat.
- A revoked token stops working on every server at once.
- A token can never do more than the person who owns it.

### Plugins

- Plugins run inside Aerie, so only curated plugins are allowed.
- A plugin added after install loads only if its file hash is on an allow-list in configuration. Every refusal is logged.
- Each plugin declares the outside hosts it calls. Aerie blocks calls to any other host.
- Plugin secrets live in Key Vault, or in the protected secret store on IIS, never in plugin settings.
- Installing, enabling and disabling a plugin is audited.

### Supply chain

- Dependencies are limited to permissive open-source licenses (MIT, Apache 2.0, BSD) from actively maintained projects, and kept to as few as possible.
- Every build fails on a known high or critical vulnerability in any package, including packages pulled in by other packages. The check already stopped the first build: a cryptography library pulled in by the Azure sign-in package had a high-severity advisory (GHSA-846f-f764-76mx), and the build stayed red until it was pinned to the patched version.
- Package versions are locked. Each build produces a CycloneDX software bill of materials. Dependabot proposes updates weekly, and CodeQL scans the code on every change.

### Patching without downtime

Aerie runs on two or more servers behind a load balancer. To patch, one side is drained and updated while the other carries everyone. Then traffic moves to the patched side, and each person moves over on their next click without being signed out. Database changes are written so the old and new versions both run against the same database during the switch.

## Benefits

| Area | Change |
|---|---|
| Ticket resolution | Technicians search one place and link the fix to the ticket. In phase 3 they attach it with one click. |
| Staff turnover | Every page records who wrote it and every change since, so knowledge stays when people leave. |
| Accuracy | Aging notices and reports show what nobody has reviewed. Ratings show readers which articles others trusted. |
| Sensitive details | Contract and credential details stay in the password manager. Aerie holds only the link, and only for people allowed to see it. |
| Administration | Administrators cannot read Legal or HR spaces quietly, cannot purge data alone, and cannot edit the audit log without it showing. |
| Network records | Meraki data, circuits, port maps and site contacts sit in one searchable place, linked to the articles about them. |
| Cost | Aerie is built in-house on open-source components and has no license fee. The design keeps company-specific details out of the code, so it can be released as open source later. |

## How it is built

- Most of the code is written by AI coding agents (Claude and Cursor) working from the approved design and a written implementation plan. Every task starts with a failing test, and nothing merges without passing tests and the security checks above. The repository carries the rules agents must follow in `AGENTS.md`.
- The product name lives in one brand setting. The code uses a neutral `Kb` prefix, so a rename touches no code.

## Repository layout

| Path | Contents |
|---|---|
| `src/Kb.Plugins.Abstractions` | The contract every plugin builds against |
| `src/Kb.Core` | Rules: permissions, audit, access requests, notifications, plugin host |
| `src/Kb.Data` | Database access |
| `src/Kb.Web` | The web application and API |
| `plugins/` | First-party plugins |
| `tests/` | Unit and integration tests |
| `docs/` | Design, plans, plugin and deployment guides |

## Open items

- Which DNS provider hosts the company's public zone. Certificate automation in phase 2 needs it.
- The open-source license, decided before any public release.
