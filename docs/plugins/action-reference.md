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

## announcement.post

Write. `PostAnnouncement`, answers `AnnouncementPostedDto`.

| Field | Type | |
|---|---|---|
| Title | `string` | required |
| Body | `string?` |  |
| ExpiresAt | `DateTimeOffset?` |  |

## announcement.remove

Write. `RemoveAnnouncement`, answers `Unit`.

| Field | Type | |
|---|---|---|
| AnnouncementId | `Guid` | required |

## attachment.add

Write. `AddAttachment`, answers `AttachmentDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| File | `UploadContent?` | never logged |

## attachment.read

Read. `ReadAttachment`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| AttachmentId | `Guid` | required |

## attachment.remove

Write. `RemoveAttachment`, answers `Unit`.

| Field | Type | |
|---|---|---|
| AttachmentId | `Guid` | required |

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

## bin.empty

Write. `EmptyBin`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.list

Read. `ListBin`, answers `IReadOnlyList<BinItemDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.list.all

Read. `ListAllBins`, answers `IReadOnlyList<BinItemDto>`.

## brand.logo.read

Read. `ReadBrandLogo`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| Variant | `string` | required |

## brand.logo.remove

Write. `RemoveBrandLogo`, answers `Unit`.

| Field | Type | |
|---|---|---|
| Variant | `string` | required |

## brand.logo.set

Write. `SetBrandLogo`, answers `Unit`.

| Field | Type | |
|---|---|---|
| Variant | `string` | required |
| File | `UploadContent?` | never logged |

## brand.read

Read. `ReadBrand`, answers `BrandDto`.

## brand.update

Write. `UpdateBrand`, answers `Unit`.

| Field | Type | |
|---|---|---|
| ProductName | `string` | required |

## draft.discard

Write. `DiscardDraft`, answers `Unit`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## draft.read

Read. `ReadDraft`, answers `DraftDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## draft.save

Write. `SaveDraft`, answers `DraftSavedDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Title | `string?` |  |
| ContentJson | `string` | required |
| BaseVersion | `string` | required |
| Tags | `IReadOnlyList<string>?` |  |

## home.read

Read. `ReadHome`, answers `HomeDto`.

## image.add

Write. `AddImage`, answers `ImageDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| File | `UploadContent?` | never logged |

## image.import

Write. `ImportImage`, answers `ImageDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Url | `string` | required |

## image.read

Read. `ReadImage`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| ImageId | `Guid` | required |

## log.read

Read. `ReadLogs`, answers `LogsDto`. Core only.

## log.target.add

Write. `AddLogTarget`, answers `LogTargetDto`. Core only.

| Field | Type | |
|---|---|---|
| Name | `string?` |  |
| Kind | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |
| Enabled | `bool` |  |

## log.target.remove

Write. `RemoveLogTarget`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## log.target.save

Write. `SaveLogTarget`, answers `LogTargetDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Name | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |
| Enabled | `bool` |  |

## log.target.test

Write. `TestLogTarget`, answers `StorageTestDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

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

## page.delete

Write. `DeletePage`, answers `Unit`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.diff

Read. `DiffPage`, answers `DiffDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| From | `string` | required |
| To | `string` | required |

## page.export

Read. `ExportPage`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Format | `string?` |  |

## page.export.code

Read. `ExportPageCode`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Number | `int` | required |

## page.export.table

Read. `ExportPageTable`, answers `FileOut`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Number | `int` | required |
| Format | `string?` |  |

## page.markdown

Read. `ReadPageMarkdown`, answers `PageMarkdownDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.move

Write. `MovePage`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| SpaceId | `Guid?` |  |
| ParentId | `Guid?` |  |

## page.read

Read. `ReadPage`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.restore

Write. `RestorePage`, answers `RestoredPageDto`.

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

## page.version.list

Read. `ListVersions`, answers `IReadOnlyList<VersionDto>`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.version.read

Read. `ReadVersion`, answers `VersionContentDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Version | `string` | required |

## page.version.restore

Write. `RestoreVersion`, answers `PageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Version | `string` | required |

## plugin.data.delete

Write. `DeletePluginData`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.health

Read. `ReadPluginHealth`, answers `PluginHealthDto`. Core only.

## plugin.install

Write. `InstallPlugin`, answers `InstalledDto`. Core only.

| Field | Type | |
|---|---|---|
| Package | `UploadContent` | required, never logged |

## plugin.list

Read. `ListPlugins`, answers `PluginListDto`. Core only.

## plugin.lock.release

Write. `ReleasePluginLock`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.permissions.read

Read. `ReadPluginPermissions`, answers `PluginPermissionsDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.permissions.save

Write. `SavePluginPermissions`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Roles | `IReadOnlyDictionary<string, string>` | required |

## plugin.remove

Write. `RemovePlugin`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| DeleteData | `bool` |  |

## plugin.settings.read

Read. `ReadPluginSettings`, answers `PluginSettingsDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.settings.save

Write. `SavePluginSettings`, answers `PluginSettingsSaved`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Values | `IReadOnlyDictionary<string, string>` | required, never logged |

## plugin.state.set

Write. `SetPluginState`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Enabled | `bool` | required |

## search.query

Read. `SearchPages`, answers `SearchResponse`.

| Field | Type | |
|---|---|---|
| Text | `string?` |  |
| SpaceId | `Guid?` |  |
| Tag | `string?` |  |
| Limit | `int?` |  |
| Offset | `int?` |  |
| Include | `string?` |  |
| MaxChars | `int?` |  |

## search.reindex

Write. `ReindexSearch`, answers `ReindexResult`.

## search.synonyms.read

Read. `ReadSynonyms`, answers `SynonymsDto`.

## search.synonyms.save

Write. `SaveSynonyms`, answers `SynonymsDto`.

| Field | Type | |
|---|---|---|
| Text | `string` | required |

## settings.public-space.set

Write. `SetPublicSpace`, answers `Unit`.

| Field | Type | |
|---|---|---|
| Enabled | `bool` | required |

## settings.uploads.read

Read. `ReadUploadSettings`, answers `UploadSettingsDto`.

## settings.uploads.save

Write. `SaveUploadSettings`, answers `UploadSettingsDto`.

| Field | Type | |
|---|---|---|
| AttachmentMb | `int` | required |

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

## storage.move.cancel

Write. `CancelStorageMove`, answers `Unit`. Core only.

## storage.move.copy-again

Write. `CopyStorageMoveAgain`, answers `Unit`. Core only.

## storage.move.preview

Write. `PreviewStorageMove`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| On | `bool` | required |

## storage.move.start

Write. `StartStorageMove`, answers `StorageMoveDto`. Core only.

| Field | Type | |
|---|---|---|
| ToId | `Guid` | required |

## storage.move.switch

Write. `SwitchStorageMove`, answers `Unit`. Core only.

## storage.place.add

Write. `AddStoragePlace`, answers `StoragePlaceDto`. Core only.

| Field | Type | |
|---|---|---|
| Name | `string?` |  |
| Kind | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |

## storage.place.remove

Write. `RemoveStoragePlace`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## storage.place.save

Write. `SaveStoragePlace`, answers `StoragePlaceDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Name | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |

## storage.place.test

Write. `TestStoragePlace`, answers `StorageTestDto`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## storage.read

Read. `ReadStorage`, answers `StorageDto`. Core only.

## token.create

Write. `CreateToken`, answers `ApiTokenCreatedDto`. Core only.

| Field | Type | |
|---|---|---|
| Name | `string?` |  |
| Kind | `TokenKind` | required |
| Scopes | `IReadOnlyList<TokenScope>?` |  |
| Days | `int?` |  |
| AccountId | `Guid?` |  |

## token.list

Read. `ListMyTokens`, answers `IReadOnlyList<ApiTokenDto>`. Core only.

## token.list.all

Read. `ListAllTokens`, answers `IReadOnlyList<ApiTokenDto>`. Core only.

## token.revoke

Write. `RevokeToken`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| TokenId | `Guid` | required |

## token.settings.read

Read. `ReadTokenSettings`, answers `TokenSettingsDto`. Core only.

## token.settings.save

Write. `SaveTokenSettings`, answers `TokenSettingsDto`. Core only.

| Field | Type | |
|---|---|---|
| MaxDays | `int` | required |

## tree.read

Read. `ReadTree`, answers `IReadOnlyList<TreeNodeDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## user.announcer.set

Write. `SetAnnouncer`, answers `Unit`.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Enabled | `bool` | required |

## user.invite

Write. `InviteUser`, answers `InvitedUser`. Core only.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| DisplayName | `string` | required |
| Email | `string?` |  |
| IsAdmin | `bool` |  |

## user.list

Read. `ListUsers`, answers `IReadOnlyList<AdminUserDto>`.

## user.permissions.set

Write. `SetUserPermission`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Permission | `string` | required |
| Granted | `bool` | required |
