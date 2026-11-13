# Agent API

The calls an LLM agent uses to find, read and change documentation. Every call is authenticated and trimmed to what the caller may read. All paths are under `/api/v1`.

## Sign in with a token

Send `Authorization: Bearer <token>` with every call. A token looks like `kbt_...` and is shown once, when it is made.

- **Personal token:** made in Settings > API tokens. It acts as the person who made it.
- **Service token:** made by an Admin in Administration > API tokens, for another system. It acts as a service account that cannot sign in; pages it changes carry the account's name.
- **Scopes:** each scope is Read or Write, for one space or for all spaces. Read gives the Reader role and Write gives the Contributor role, never more, and never more than the owner of a personal token has. A service token's all-spaces scope does not reach a sensitive space; name the space to include it.
- **Expiry:** every token has one. The Admin sets the longest life (default 365 days).
- **Limits:** 600 calls a minute per token, then `429`. A revoked, expired or mistyped token gets `401`.
- **Closed routes:** a token reaches the calls on this page and nothing else. Administration, token management, sign-in, notifications and settings answer `403`.
- Every call made with a token is written to the audit log with its method and route.

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

The engine sits behind `ISearchProvider` (`src/Kb.Core/Search/Search.cs`). A plugin can replace it, for example with a vector index, and this contract stays the same.

## Read

- `GET /pages/{id}/markdown`: the page as Markdown, with its title, version and last update.
- `GET /pages/{id}`: the page as editor JSON (ProseMirror), with tags, breadcrumbs, attachments and recent history.
- `GET /pages/{id}/versions` and `GET /pages/{id}/diff?from=1.2&to=1.4`: history and a line diff.
- `GET /spaces` and `GET /spaces/{id}/tree`: what exists.

## Write

- `PUT /pages/{id}/draft`: propose changes without publishing. A person opens the page, sees the draft and decides.
- `PUT /pages/{id}` with `baseVersion`: publish. The save is refused with 409 if the page changed since `baseVersion`.
- `POST /spaces/{id}/images`: upload an image (raw body). The response gives the `url` to use in an image node.

Images are nodes in the editor JSON with `src`, `alt`, `width` and `height`. To resize one, change `width` and save the page.

## Not built yet

- API tokens and a service identity for agents.
- Events (page saved, image added) that an agent can subscribe to. These belong to the plugin model.
- Image pixel dimensions in the API. Today only the display width set in the page is stored.
