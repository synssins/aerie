# Agent API

The calls an LLM agent, a script or another system uses with an API token: find, read and change documentation, and feed the application inventory. Every call is authenticated and trimmed to what the caller may read. All paths are under `/api/v1`.
[The API reference](api-reference.md) lists every route, the scope a token needs for it, its body and its answer, and the error body.

## Sign in with a token

Send `Authorization: Bearer <token>` with every call. A token looks like `kbt_...` and is shown once, when it is made.

- **Personal token:** made in Settings > API tokens. It acts as the person who made it.
- **Service token:** made by an Admin in Administration > Security > API tokens, for another system. It acts as a service account that cannot sign in; pages it changes carry the account's name.
- **Scopes:** each scope is Read or Write, for one space or for all spaces. Read gives the Reader role and Write gives the Contributor role, never more, and never more than the owner of a personal token has. A service token's all-spaces scope does not reach a sensitive space; name the space to include it.
- **Never an Admin.** A token made by an Admin has no Admin rights. Administration answers `403` to every token.
- **Expiry:** every token has one. The Admin sets the longest life (default 365 days).
- **Limits:** 600 calls a minute per token, then `429`. A revoked, expired or mistyped token gets `401`.
- **Open routes:** a token reaches only the calls on this page (the API reference marks them Read or Write). Every other route answers `403` "API tokens can't call this route.": administration, token management, sign-in, notifications, settings, plugin routes, AI import, formatting and the AI settings.
- **The audit log:** every call made with a token writes a `token.use` entry with its method and route, and what the call changed carries the token's name. Administration > Audit log shows such entries with "API" in the Via column (a person in the browser: "Web").

## Find

`GET /search?q=&space=&tag=&limit=&offset=&include=markdown&maxChars=`

| Parameter | Meaning |
|---|---|
| `q` | Words to find. Typos, word prefixes and synonyms match. |
| `space` | Limit to one space id. |
| `tag` | Limit to a tag. With no `q`, lists the tag's pages, newest first. |
| `limit`, `offset` | Paging. `limit` is 1 to 50, default 20. |
| `include=markdown` | Adds each hit's text as Markdown in `text`. |
| `maxChars` | Cap on each `text`, 200 to 20000, default 4000. |

Each hit carries:

- `id`, `url` and `version`: enough to cite the page and to detect that it changed.
- `spaceId`, `spaceName`, `path`: where it lives.
- `title`, `tags`, `snippet`, `updatedAt`.
- `score` and `matched`: the index words that matched, so the agent can tell an exact hit from a typo or synonym match.

## Read

- `GET /pages/{id}/markdown`: the page as Markdown, with its title, version and last update.
- `GET /pages/{id}`: the page as editor JSON (ProseMirror), with tags, breadcrumbs, attachments, recent history and its details: the applications it applies to, its owner, its review cycle.
- `GET /pages/{id}/versions`, `GET /pages/{id}/versions/{version}` and `GET /pages/{id}/diff?from=1.2&to=1.4`: history and a line diff. History is for members of the space: someone who reads it only because it is open to everyone signed in (or to anyone) gets `403` here, and the page's `history` and `updatedBy` are empty with `canSeeHistory` false.
- `GET /pages/{id}/export` (Word or PDF), `.../export/code/{n}`, `.../export/table/{n}`: a page, a code block or a table as a file.
- `GET /spaces`, `GET /spaces/{id}` and `GET /spaces/{id}/tree`: what exists. `GET /home`: recent pages.
- `GET /images/{id}`, `GET /attachments/{id}`: files of pages.
- `GET /me`: who the token acts as.

## Write

These need a Write scope.

- `POST /spaces/{id}/pages`: a new page or folder.
- `PUT /pages/{id}` with `baseVersion`: publish a new version. The save is refused with 409 if the page changed since `baseVersion`.
- `POST /pages/{id}/move`, `DELETE /pages/{id}` (to the recycle bin), `POST /pages/{id}/versions/{version}/restore`.
- `POST /spaces/{id}/images` (raw body): upload an image. The response gives the `url` to use in an image node. `POST /spaces/{id}/images/import`: copy an image from an address.
- `POST /pages/{id}/attachments` (raw body), `DELETE /attachments/{id}`.

Images are nodes in the editor JSON with `src`, `alt`, `width` and `height`. To resize one, change `width` and save the page.
Drafts (`PUT /pages/{id}/draft`) are for people in the browser; a token cannot save one.

## Feed the inventory

`POST /inventory/sync` keeps the application inventory up to date from a CMDB, a scanner or a script. It needs a Write scope, and the token's owner, or the token itself, must be allowed to edit the inventory on Administration > Security > Inventory access (a token is never an Admin, so an Admin's token needs a rule too).

```json
{
  "source": "cmdb",
  "rows": [
    { "application": "SQL Server", "vendor": "Microsoft", "server": "SQL01", "version": "2022 CU14" }
  ],
  "removeMissing": false,
  "apply": true
}
```

- `source` names where the data comes from; the history of each install shows it.
- `removeMissing: true` removes the installs of this source that are not in `rows`.
- `apply: false` saves nothing and answers what would change.

The answer counts what was added, changed, unchanged and removed, and lists each change. `tools/Seed-Inventory.ps1` is a working example.

## The AI

The AI features (import, formatting, in-depth search) are for people in the browser. A token cannot reach them, and an agent cannot use the app's model. Each feature is open to Admins, to everyone, or off, on Administration > AI.

## Not built yet

- Events (page saved, image added) that an agent can subscribe to. These belong to the plugin model.
- Image pixel dimensions in the API. Today only the display width set in the page is stored.
