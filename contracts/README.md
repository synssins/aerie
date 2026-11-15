# Contracts

The API contracts aerie is built to. They are written in the `aerie-spec` repository and arrive here only as a tagged release, unedited. See [CLEAN_ROOM.md](../CLEAN_ROOM.md).

## Layout (once the first release is pinned)

| Path | What |
|---|---|
| `RELEASE` | The `aerie-spec` release tag pinned here, for example `contracts-v1` |
| `SHA256SUMS` | The checksum of every file in the release |
| `aerie/openapi.yaml` | Aerie's own API (`/api/v1`) |
| `datacenter/openapi.yaml` | The Data Center compatible API (`/rest/api`, `/rest/oauth2`) |
| `cloud/v1/openapi.yaml`, `cloud/v2/openapi.yaml` | The Cloud compatible APIs |
| `behaviour/` | The written behaviour specs that go with the contracts |

## Updating

1. Merge the change in `aerie-spec` and tag a release.
2. Replace this folder's contents with the release, unchanged, in one pull request titled with the release tag.
3. Change the code and tests to match in the same pull request or the ones after it.

Never edit a contract file here. A wrong contract is fixed in `aerie-spec`.
