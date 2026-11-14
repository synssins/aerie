<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/aerie-logo-on-dark.svg">
  <img src="branding/aerie-logo-on-light.svg" alt="Aerie" width="240">
</picture>

Aerie is a self-hosted knowledge base for teams that keep runbooks, how-to articles and technical documentation. Each team works in its own space, and only the people given a role there can see it.

It runs on your own server (IIS) or on Azure App Service, with SQL Server. There is no cloud service behind it.

**Status:** in development. Not production-ready.

## What it does today

**Spaces and access**
- Spaces, each with its own folder and page tree.
- Four roles per space: Reader, Contributor, Supervisor, Owner. Without a role in a space, you can't see that the space exists.
- Local users and local groups. A group gets a role in a space, and every member holds that role.
- Only an Admin makes or removes an Owner. Owners give the other roles from the space's Members tab.
- Sensitive spaces, closed to Admins too. An Admin can open one through emergency access, and the space's Owners are told when that happens.
- Access requests: anyone can ask for a role in a space, and its Owners approve or deny.

**Pages**
- A rich-text editor with code blocks, tables, images and attachments.
- Version history with side-by-side diffs and restore. Drafts save as you type.
- Search with typo tolerance and synonyms.
- Exports to PDF and Word. Tables export to CSV or Excel, and code blocks download as files.
- A recycle bin for pages and whole spaces. Nothing is purged automatically.

**Administration and security**
- An audit log in which each entry is chained to the previous one by a SHA-256 hash, so changes and deletions show up. Entries can be shipped to a SIEM.
- API tokens, scoped to read or write, per space or for every space, always with an expiry. A token never reaches more than its owner can.
- Sign-in rate limiting and account lockout. Signing out ends every session of that user.
- File storage in a local folder or a network share, with a tool that moves files from one place to another.
- Branding: the product name and logos.

**Plugins**
- A plugin brings its own server code, database tables and screens. The core stays small.
- An Admin installs a plugin by uploading its package, and the package's SHA-256 is recorded in the audit log. Safe Mode starts the app without uploaded plugins.

## Planned

- Sign-in with Entra ID, AD/LDAP or passkeys, and AD/Entra security groups (nested groups included) that give roles in spaces. These will be plugins.
- Comments and ratings on pages.
- Azure Blob Storage for files.

## Requirements

- Windows Server or Windows 10/11 with IIS, or Azure App Service
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.403 or later) and the ASP.NET Core 10 Hosting Bundle
- Node.js, to build the web client
- SQL Server (Express is enough)

## Install on IIS

Run these from the repository root.

1. Install IIS, SQL Server Express and the Hosting Bundle. This step needs an elevated prompt.
   ```powershell
   .\tools\install-prereqs.ps1
   ```
2. Build the client and the server into `artifacts\publish`.
   ```powershell
   .\tools\publish.ps1
   ```
3. Create the site, the app pool, the database and a self-signed certificate. This step needs an elevated prompt, and is only needed the first time.
   ```powershell
   .\tools\deploy-iis.ps1
   ```
   Open `https://localhost` and the setup page creates the first admin. You can also pass `-AdminUser` and `-AdminPassword` to the script. `-TrustCert` trusts the certificate on this computer.
4. To update later, run `.\tools\publish.ps1` and then `.\tools\update-iis.ps1`. No elevated prompt is needed.

## Tests

```powershell
dotnet test --solution Kb.sln
```
```powershell
cd client; npm test
```

The server tests run against SQLite, so they need no SQL Server.

## Repository layout

| Path | Contents |
|---|---|
| `src/Kb.Web` | Web app and REST API |
| `src/Kb.Core` | Permissions, audit, notifications, the plugin host |
| `src/Kb.Data` | Data access and migrations |
| `src/Kb.Plugins.Abstractions` | The contract a plugin builds against |
| `client/` | React and TypeScript web client |
| `plugins/` | Built-in plugins |
| `tests/` | Server tests |
| `tools/` | Install, build and deploy scripts |
| `branding/` | Icon and logo |
| `docs/` | API and plugin guides |

The code uses the prefix `Kb`, so renaming the product changes no code.

## Documentation

- [Agent API](docs/agent-api.md): how a program signs in with a token, then finds, reads and changes pages.
- [API reference](docs/api-reference.md): every route, and whether an API token may call it.
- [Plugin developer guide](docs/plugins/developer-guide.md), [manifest reference](docs/plugins/manifest-reference.md), [admin guide](docs/plugins/admin-guide.md), [action reference](docs/plugins/action-reference.md).

## License

No license has been chosen yet. Until one is added, all rights are reserved.
