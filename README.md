<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/aerie-logo-on-dark.svg">
  <img src="branding/aerie-logo-on-light.svg" alt="Aerie" width="240">
</picture>

A self-hosted knowledge base for IT teams. You write runbooks and how-tos in it, and each team gets its own space that other teams can't see unless they're given a role there.

It's still in development, so expect rough edges and breaking changes.

## Features

Pages live in spaces, in a tree of folders. The editor handles code blocks, tables, images and attachments. Every save is a version you can diff against or roll back to, and drafts are kept while you type. Search copes with typos. Pages export to PDF or Word, and tables to CSV or Excel.

Access is per space. Someone is a Reader, Contributor, Supervisor or Owner in a space, either directly or through a group. An Admin creates spaces and Owners. Owners handle the rest of their space's members. A space marked sensitive is closed to Admins as well, unless they use emergency access, and its Owners hear about it when they do.

Deleted pages and spaces go to a recycle bin. Sign-ins, permission changes, deletions and other security events go into an audit log whose entries are hash-chained, and the log can be sent to a syslog or SIEM. API tokens are scoped and always expire.

Most extra features are meant to be plugins. A plugin brings its own code, tables and screens, and an Admin installs one by uploading it.

Not built yet: sign-in through Entra ID, AD/LDAP or passkeys, AD and Entra groups, comments and ratings, and Azure Blob storage.

## Running it in Docker

The image runs on any Linux Docker host and keeps its data in a SQLite file on a volume. Each push to `main` publishes it as `ghcr.io/synssins/aerie:latest`, so a host only needs [`docker-compose.yml`](docker-compose.yml):

```bash
docker compose up -d
```

To build it yourself instead, run `docker build -t aerie .` and change the image name in the compose file.

It listens on port 8080 over plain HTTP. Put it behind a reverse proxy that serves HTTPS, because the sign-in cookie is only sent over HTTPS. Open the site and the setup page asks for the first admin account, or set one up front in `docker-compose.yml`, which also shows how to point it at SQL Server instead.

The volume holds everything: the database, uploaded files and logs. Back up `/app/App_Data` and you have it all.

## Running it on IIS

You need Windows with IIS, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Node.js. For the database, use SQL Server (Express works) or a single SQLite file. The SQLite file needs nothing else installed.

From the repository root, in an elevated PowerShell:

```powershell
.\tools\install-prereqs.ps1          # IIS, SQL Server Express, the ASP.NET Core Hosting Bundle
.\tools\publish.ps1                  # builds the client and server into artifacts\publish
.\tools\deploy-iis.ps1               # site, app pool, database, self-signed certificate
```

For SQLite, add `-Sqlite` to the first and last commands. The data then lives in `C:\inetpub\aerie\App_Data\kb.db`, and updates leave it alone.

Open `https://localhost` and the setup page asks for the first admin account. `deploy-iis.ps1 -TrustCert` stops the browser warning on that machine.

To update later, without elevation:

```powershell
.\tools\publish.ps1
.\tools\update-iis.ps1
```

## Tests

```powershell
dotnet test --solution Kb.sln
cd client; npm test
```

The server tests run on SQLite and need no database server.

## Where things are

- `src/Kb.Web`: the web app and REST API
- `src/Kb.Core`: permissions, audit, notifications, the plugin host
- `src/Kb.Data`: data access, with SQL Server migrations
- `src/Kb.Data.Sqlite`: the SQLite migrations
- `src/Kb.Plugins.Abstractions`: what a plugin builds against
- `client/`: the React web client
- `plugins/`: plugins that ship with it
- `tools/`: install and deploy scripts
- `docs/`: [agent API](docs/agent-api.md), [API reference](docs/api-reference.md), and the plugin guides ([developer](docs/plugins/developer-guide.md), [manifest](docs/plugins/manifest-reference.md), [admin](docs/plugins/admin-guide.md), [actions](docs/plugins/action-reference.md))

Code uses the prefix `Kb` rather than the product name, so a rename doesn't touch it.

## License

None yet. All rights reserved until one is added.
