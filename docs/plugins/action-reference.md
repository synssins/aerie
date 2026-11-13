# Action reference

Written by the test `ActionReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.

Every operation of the core is one action, sent through one bus. A plugin can put a before hook, a result filter (reads only) or an after handler
on an action of the contract assembly (`Kb.Plugins.Abstractions`). Core-only actions go through the same bus but are not part of the contract:
a plugin cannot hook how plugins, API tokens, storage and log shipping are administered.

## page.create

Write. `CreatePage`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| ParentId | `Guid?` |  |
| Kind | `string` | required |
| Title | `string` | required |
| Tags | `IReadOnlyList<string>?` |  |
| ContentJson | `string?` |  |

## page.read

Read. `ReadPage`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.save

Write. `SavePage`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Title | `string` | required |
| ContentJson | `string` | required |
| BaseVersion | `string` | required |
| Major | `bool` |  |
| Note | `string?` |  |
| Tags | `IReadOnlyList<string>?` |  |

## tree.read

Read. `ReadTree`, answers `IReadOnlyList<TreeNodeDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
