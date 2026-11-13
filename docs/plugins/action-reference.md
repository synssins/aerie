# Action reference

Written by the test `ActionReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.

Every operation of the core is one action, sent through one bus. A plugin can put a before hook, a result filter (reads only) or an after handler
on an action of the contract assembly (`Kb.Plugins.Abstractions`). Core-only actions go through the same bus but are not part of the contract:
a plugin cannot hook how plugins, API tokens, storage and log shipping are administered.

## access.decide

Write. `DecideAccess`, answers `Unit`.

| Field | Type | |
|---|---|---|
| RequestId | `Guid` | required |
| Approve | `bool` | required |

## access.request

Write. `RequestAccess`, answers `AccessRequestedDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Role | `SpaceRole` | required |
| Reason | `string?` |  |

## audit.list

Read. `ListAudit`, answers `IReadOnlyList<AuditEntryDto>`.

| Field | Type | |
|---|---|---|
| Take | `int?` |  |

## audit.verify

Read. `VerifyAudit`, answers `AuditVerifyResult`.

## auth.local.disable

Write. `DisableLocalSignIn`, answers `Unit`.

## auth.signin

Write. `SignIn`, answers `MeResponse`.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| Password | `string` | never logged |

## auth.signout

Write. `SignOut`, answers `Unit`.

## me.read

Read. `ReadMe`, answers `MeResponse`.

## me.settings.read

Read. `ReadMySettings`, answers `UserSettings`.

## me.settings.save

Write. `SaveMySettings`, answers `UserSettings`.

| Field | Type | |
|---|---|---|
| Theme | `string` | required |
| Width | `string` | required |
| StartPage | `string` | required |
| CodeLanguage | `string` | required |
| Notify | `Dictionary<string, NotifyPref>?` |  |

## notification.count

Read. `CountNotifications`, answers `NotificationCountDto`.

## notification.list

Read. `ListNotifications`, answers `IReadOnlyList<NotificationDto>`.

| Field | Type | |
|---|---|---|
| UnreadOnly | `bool` |  |

## notification.mark

Write. `MarkNotification`, answers `Unit`.

| Field | Type | |
|---|---|---|
| NotificationId | `Guid` | required |

## notification.mark.all

Write. `MarkAllNotifications`, answers `Unit`.

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

## settings.public-space.set

Write. `SetPublicSpace`, answers `Unit`.

| Field | Type | |
|---|---|---|
| Enabled | `bool` | required |

## setup.run

Write. `RunSetup`, answers `MeResponse`.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| DisplayName | `string` | required |
| Password | `string` | never logged |
| Code | `string?` | never logged |

## setup.status

Read. `ReadSetupStatus`, answers `SetupStatus`.

## space.create

Write. `CreateSpace`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| Name | `string` | required |
| Key | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool` |  |
| IsPublic | `bool` |  |

## space.list

Read. `ListSpaces`, answers `IReadOnlyList<SpaceDto>`.

## space.list.all

Read. `ListAllSpaces`, answers `IReadOnlyList<SpaceDto>`.

## space.read

Read. `ReadSpace`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## space.role.grant

Write. `GrantSpaceRole`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| PrincipalType | `PrincipalType` | required |
| PrincipalId | `string` | required |
| Role | `SpaceRole` | required |

## space.role.list

Read. `ListSpaceRoles`, answers `IReadOnlyList<RoleAssignmentDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## space.role.revoke

Write. `RevokeSpaceRole`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| AssignmentId | `Guid` | required |

## space.sensitive.emergency

Write. `OpenEmergencyAccess`, answers `EmergencyAccessDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Reason | `string` | required |

## space.update

Write. `UpdateSpace`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Name | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool?` |  |
| IsPublic | `bool?` |  |
| CommentsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` |  |
| RecycleDays | `int?` |  |

## space.update.admin

Write. `UpdateSpaceAdmin`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Name | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool?` |  |
| IsPublic | `bool?` |  |
| CommentsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` |  |
| RecycleDays | `int?` |  |

## tree.read

Read. `ReadTree`, answers `IReadOnlyList<TreeNodeDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## user.invite

Write. `InviteUser`, answers `InvitedUser`.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| DisplayName | `string` | required |
| Email | `string?` |  |
| IsAdmin | `bool` |  |

## user.list

Read. `ListUsers`, answers `IReadOnlyList<AdminUserDto>`.
