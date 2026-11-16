# Roadmap

What is being built and what is planned. What already works is in the [README](README.md) and on [aeriekb.com](https://aeriekb.com). Nothing here has a date.

## In progress

- The first release, with an install guide.

## Planned

- **Page comments**, moderated by the space's Supervisors and Owners.
- **Page ratings**, including a "This solved my problem" button.
- **AI search**: answers drawn from the pages a person is allowed to read.
- **Sign-in through Entra ID, AD/LDAP and passkeys**, with AD and Entra groups.
- **Azure Blob** for file storage.
- **Cloud compatible APIs.** The Data Center compatible API is built; the Cloud v1 and v2 compatible APIs come next, so integrations written for the cloud product also work unchanged.
- **Vendor token-management API**, so tools that create their own personal access tokens can do so.
- **Service accounts for OAuth 2.0** (client credentials).

## How it gets built

Each item starts as a written behaviour spec and an API contract in [aerie-spec](https://github.com/synssins/aerie-spec), is built here against the pinned contract, and is checked by [aerie-compat-lab](https://github.com/synssins/aerie-compat-lab). See [CLEAN_ROOM.md](CLEAN_ROOM.md).
