# Provenance ledger

Where every fixture, asset, bundled file and outside reference in this repository came from. Add a row in the same pull request that adds the file. See [CLEAN_ROOM.md](CLEAN_ROOM.md).

## Attestations

| Date | Scope | Statement |
|---|---|---|
| 2026-11-15 | All code up to commit `eea7e023` | Written for aerie by its author with AI assistance, as original work. No vendor documentation, vendor API specs, vendor code or customer material was used. |
| 2026-11-15 | The commit that adds this file | Governance only: `LICENSE`, `NOTICE`, `CLEAN_ROOM.md`, this ledger, `contracts/README.md`, the pull request template, code owners, the CI workflow and the README's license section. It was written by a planning session that had read Atlassian's published API specifications, so it contains no implementation code and no API details. |

## Bundled third-party files

| Path | What | Origin | License |
|---|---|---|---|
| `src/Kb.Web/Fonts/Liberation*.ttf` | Liberation Sans and Liberation Mono 2.1.5, used by PDF export | https://github.com/liberationfonts, unchanged | SIL Open Font License 1.1 (`src/Kb.Web/Fonts/OFL.txt`) |
| `LICENSE` | Apache License 2.0 text | https://www.apache.org/licenses/LICENSE-2.0.txt, retrieved 2026-11-15, SHA-256 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` | Apache-2.0 |

## Original assets and fixtures

| Path | What | Origin |
|---|---|---|
| `branding/*.svg`, `branding/preview.html`, `client/public/*.svg` | Aerie logo, mark and icon | Drawn for aerie |
| `tests/Kb.Tests.Integration/Security/matrix/*.json` | Security test matrix: which caller may call which route | Written for aerie from its own routes |
| `tests/plugins/**` | Test plugins (dependent, library, probe) | Written for aerie's plugin tests |
| `plugins/**` | Sample and email plugins | Written for aerie |
| `docs/api-reference.md`, `docs/plugins/action-reference.md` | Generated references | Generated from aerie's own code by tests |
| `docs/openapi/aerie-as-is.yaml` | Aerie's own API (`/api/v1`) as OpenAPI 3.0.3, as it behaves today | Generated from aerie's own routes and action types by the test `OpenApiExport_IsUpToDate` (`KB_WRITE_REFS=1`) |
| `src/Kb.Web/DataCenter/DcUserJson.cs` (`AvatarSvg`) | Default profile picture (grey square, white head and shoulders) served at `/images/avatar/default.svg` | Drawn for aerie |

## Outside references

Public documents and standards consulted while building, with the version read. Vendor documentation is not consulted here; it belongs in `aerie-spec`.

| Reference | Version or date | Used for |
|---|---|---|
| `aerie-spec` contract release, https://github.com/synssins/aerie-spec/releases/tag/contracts-v1 | `contracts-v1`, 2026-11-15, checksums in `contracts/SHA256SUMS` | Pinned unchanged in `contracts/`: aerie's own API, the Data Center compatible T1 contract, the behaviour specs |

## Incidents

None.
