<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/aerie-logo-on-dark.svg">
  <img src="branding/aerie-logo-on-light.svg" alt="Aerie" width="240">
</picture>

A self-hosted knowledge base for IT teams. You write runbooks and how-tos in it, and each team gets its own space that other teams can't see unless they're given a role there.

It's still in development, so expect rough edges and breaking changes.

## Features

Pages live in spaces, in a tree of folders. The editor handles code blocks, tables, images and attachments. Every save is a version you can diff against or roll back to, and drafts are kept while you type. Search copes with typos. Pages export to PDF or Word, and tables to CSV or Excel.

Access is per space. Someone is a Reader, Contributor, Supervisor or Owner in a space, either directly or through a group. An Admin creates spaces and Owners. Owners handle the rest of their space's members. A space marked sensitive is closed to Admins as well, unless they use emergency access, and its Owners hear about it when they do.

Deleted pages and spaces go to a recycle bin. Sign-ins, permission changes, deletions and other security events go into an audit log whose entries are hash-chained, and the log can be sent to a syslog or SIEM. Entries of a sensitive space leave the database with who did what, when and from where, and without the details that can hold the space's own words (a page title, a file name, an excerpt): an Admin chooses where the log goes, and has no access to those spaces. API tokens are scoped and always expire. A session ends after 8 hours without use and, however much it is used, 12 hours after it was opened. People change their own password under Settings > Sign-in.

Set `AllowedHosts` in the server's configuration to the site's own host names once they are known; it ships as `*`, which accepts any. Most extra features are meant to be plugins. A plugin brings its own code, tables and screens, and an Admin installs one by uploading it.

Not built yet: sign-in through Entra ID, AD/LDAP or passkeys, AD and Entra groups, comments and ratings, and Azure Blob storage. What is planned is in [ROADMAP.md](ROADMAP.md).

## Works with existing integrations

Aerie answers a Data Center compatible REST API next to its own, so scripts and tools written for that API keep working when you point them at an Aerie site. Nothing has to be installed or turned on.

- **The API** is under `/rest/api`: pages and their versions, attachments, labels, content properties, spaces, users, CQL search and long-running tasks. Bodies are read and written in the storage format. A caller sees only what its account may see in Aerie, and each space keeps its own access rules.
- **HTTP Basic** takes a username with a password or with an API token. Accounts that sign in with codes, which includes every Admin, must use a token. Failed attempts count against the same lockout limits as the sign-in page.
- **Personal access tokens** are Aerie's own API tokens, sent as `Authorization: Bearer`. Their space and read-only limits still apply. An Admin's token does what only an Admin may (create or delete a space, read another user's groups) only when it has a scope for all spaces; a token limited to named spaces never does.
- **OAuth 2.0** is built in, and Aerie is the authorization server at `/rest/oauth2/latest`. It uses the authorization code flow with PKCE. Refresh tokens rotate, and presenting a used one again revokes every token issued under that grant. An Admin registers each application under Administration › Applications, where the client secret is shown once. A user approves each application on a consent screen and can cut it off again under Connected applications in their profile. Clients can find the endpoints at `/.well-known/oauth-authorization-server`.

OAuth access tokens work on the compatible API only, not on Aerie's own `/api/v1`. OAuth needs HTTPS. An application does what only an Admin may only when the Admin who approved it gave it the `ADMIN` scope; with `READ` or `WRITE` it has that Admin's rights on content and no more.

A page's history is for members of its space, on this API as in the app. Someone who reads a page only because the space is open to signed-in people or to anyone gets the page without `history` (it is left out and not offered under `_expandable`), and its `version` carries the number and date with the unknown user as `by` and an empty `message`.

The exact behaviour is in [contracts/](contracts/README.md): `datacenter/openapi.yaml` lists every operation, and `behaviour/identity.md` covers sign-in and tokens. Every published image is run against clients generated in three languages by [aerie-compat-lab](https://github.com/synssins/aerie-compat-lab). The Cloud compatible APIs are not built yet.

## Running it in Docker

The image runs on any Linux Docker host and keeps its data in a SQLite file on a volume. Each push to `main` publishes it as `ghcr.io/synssins/aerie:latest`, so a host only needs [`docker-compose.yml`](docker-compose.yml):

```bash
docker compose up -d
```

To build it yourself instead, run `docker build -t aerie .` and change the image name in the compose file.

It listens on port 8080 over plain HTTP. Put it behind a reverse proxy that serves HTTPS, because the sign-in cookie is only sent over HTTPS. The compose file publishes the port on the Docker host itself (`127.0.0.1`), so the proxy is the only way in.

Tell Aerie which proxy to believe. Set `Kb__Proxy__Trusted__0` (and `__1`, `__2` for more) to the address the proxy connects from, as the container sees it: one address or a network such as `172.18.0.0/16`. Only a proxy named there is believed when it sends `X-Forwarded-For` (who the client is) and `X-Forwarded-Proto` (that the client used HTTPS). Without the setting the site still works, but rate limits and the audit log see the proxy's address for every caller, and sign-in methods and OAuth on the compatible API refuse because they do not see HTTPS. The log names the address the first time it ignores a forwarded request. An install that is not behind a proxy, such as IIS below, needs nothing.

Open the site and the setup page asks for the first admin account, or set one up front in `docker-compose.yml`, which also shows how to point it at SQL Server instead.

The volume holds everything: the database, uploaded files and logs. Back up `/app/App_Data` and you have it all.

### Protecting the keys in the database

The keys that sign the session cookie and encrypt stored secrets (plugin secrets, log target secrets, storage passwords, a sign-in method's client secret) are kept in the database, so that several servers share them. By default they are stored as they are, which makes a copy of the database enough to read those secrets and to forge a session. Set `Kb--KeyProtectionSecret` to a long random value and they are stored encrypted with it: the keys already there are encrypted at the next start, and new ones from then on. Set it where the database is not: an environment variable of the container, a Fly secret, an environment variable of the IIS app pool. This applies to every kind of install.

Keep the value as safe as a backup. Without it the app refuses to start on that database and says so, and if it is lost for good the stored secrets have to be typed in again and everyone is signed out. Backups of the database made before the secret was set still hold the keys unencrypted.

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

## How changes are made

Aerie is being redeveloped under a clean-room method: contracts first, then code, then tests, with a human review on every merge. [CLEAN_ROOM.md](CLEAN_ROOM.md) says what may and may not come into this repository, and [PROVENANCE.md](PROVENANCE.md) records where every fixture, asset and outside reference came from. [contracts/](contracts/README.md) is where the API contracts are pinned.

## License

Aerie is licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE) for attributions; the bundled fonts keep their own SIL Open Font License.
