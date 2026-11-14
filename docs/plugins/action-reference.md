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

## ai.models

Write. `ListAiModels`, answers `string[]`. Core only.

| Field | Type | |
|---|---|---|
| Request | `AiModelsRequest` | required |

## ai.settings.read

Read. `ReadAiSettings`, answers `AiSettingsDto`. Core only.

## ai.settings.save

Write. `SaveAiSettings`, answers `AiSettingsDto`. Core only.

| Field | Type | |
|---|---|---|
| Request | `AiSettingsRequest` | required |

## ai.test

Write. `TestAi`, answers `AiTestDto`. Core only.

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
| Offset | `int?` |  |
| Action | `string?` |  |
| ActorId | `Guid?` |  |
| From | `DateOnly?` |  |
| To | `DateOnly?` |  |
| TokenId | `Guid?` |  |

## audit.verify

Read. `VerifyAudit`, answers `AuditVerifyResult`.

## auth.codes.enroll

Write. `EnrollCodes`, answers `CodesEnrolled`. Core only.

| Field | Type | |
|---|---|---|
| Code | `string` |  |

## auth.codes.key

Write. `NewCodeKey`, answers `CodeKey`. Core only.

## auth.local.disable

Write. `DisableLocalSignIn`, answers `Unit`.

## auth.password.set

Write. `SetPassword`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Token | `string` | required |
| Password | `string` | required |

## auth.signin

Write. `SignIn`, answers `MeResponse`.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| Password | `string` | never logged |

## auth.signin.code

Write. `SignInCode`, answers `MeResponse`. Core only.

| Field | Type | |
|---|---|---|
| Code | `string` |  |

## auth.signout

Write. `SignOut`, answers `Unit`.

## bin.browse

Read. `BrowseBin`, answers `IReadOnlyList<BinNodeDto>`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.browse.all

Read. `BrowseAllBins`, answers `IReadOnlyList<BinNodeDto>`.

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

## bin.page.read

Read. `ReadBinPage`, answers `BinPageDto`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

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

## group.create

Write. `CreateGroup`, answers `GroupDto`. Core only.

| Field | Type | |
|---|---|---|
| Name | `string` | required |

## group.delete

Write. `DeleteGroup`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |

## group.list

Read. `ListGroups`, answers `IReadOnlyList<GroupDto>`. Core only.

## group.member.add

Write. `AddGroupMember`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |
| UserId | `Guid` | required |

## group.member.list

Read. `ListGroupMembers`, answers `IReadOnlyList<GroupMemberDto>`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |

## group.member.remove

Write. `RemoveGroupMember`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |
| UserId | `Guid` | required |

## group.rename

Write. `RenameGroup`, answers `GroupDto`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |
| Name | `string` | required |

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

## import.accept

Write. `AcceptImport`, answers `IReadOnlyList<Guid>`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |
| Request | `AcceptRequest` | required |

## import.batch.create

Write. `CreateImportBatch`, answers `ImportBatchDto`. Core only.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid?` |  |
| ParentId | `Guid?` |  |

## import.batch.start

Write. `StartImportBatch`, answers `ImportBatchDto`. Core only.

| Field | Type | |
|---|---|---|
| BatchId | `Guid` | required |

## import.discard

Write. `DiscardImport`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |

## import.file.add

Write. `AddImportFile`, answers `ImportFileDto`. Core only.

| Field | Type | |
|---|---|---|
| BatchId | `Guid` | required |
| File | `UploadContent?` | never logged |

## import.file.read

Read. `ReadImportFile`, answers `FileOut`. Core only.

| Field | Type | |
|---|---|---|
| FileId | `Guid` | required |

## import.list

Read. `ListImports`, answers `ImportListDto`. Core only.

## import.merge

Write. `MergeImports`, answers `ImportItemRowDto`. Core only.

| Field | Type | |
|---|---|---|
| ItemIds | `IReadOnlyList<Guid>` | required |

## import.read

Read. `ReadImportItem`, answers `ImportItemDto`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |

## import.reject

Write. `RejectImport`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |

## import.retry

Write. `RetryImport`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |

## inventory.access.read

Read. `ReadInventoryAccess`, answers `InvAccessDto`. Core only.

## inventory.access.save

Write. `SaveInventoryAccess`, answers `InvAccessDto`. Core only.

| Field | Type | |
|---|---|---|
| Access | `InvAccessDto` | required |

## inventory.app.create

Write. `CreateInvApp`, answers `InvAppDto`. Core only.

| Field | Type | |
|---|---|---|
| App | `InvAppRequest` | required |

## inventory.app.delete

Write. `DeleteInvApp`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |

## inventory.app.list

Read. `ListInvApps`, answers `IReadOnlyList<InvAppRowDto>`. Core only.

| Field | Type | |
|---|---|---|
| Query | `string?` |  |
| Server | `string?` |  |

## inventory.app.read

Read. `ReadInvApp`, answers `InvAppDto`. Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |

## inventory.app.update

Write. `UpdateInvApp`, answers `InvAppDto`. Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |
| App | `InvAppRequest` | required |

## inventory.import

Write. `ImportInventory`, answers `SyncResultDto`. Core only.

| Field | Type | |
|---|---|---|
| Csv | `string` | required |
| RemoveMissing | `bool` |  |
| Apply | `bool` |  |

## inventory.install.remove

Write. `RemoveInvInstall`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| InstallId | `Guid` | required |

## inventory.install.set

Write. `SetInvInstall`, answers `InvAppDto`. Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |
| Server | `string` | required |
| Version | `string` | required |

## inventory.installs.sync

Write. `SyncInstalls`, answers `SyncResultDto`.

| Field | Type | |
|---|---|---|
| Source | `string` | required |
| Rows | `IReadOnlyList<SyncRowDto>` | required |
| RemoveMissing | `bool` |  |
| Apply | `bool` |  |

## inventory.me

Read. `ReadInventoryMe`, answers `InvMeDto`. Core only.

## inventory.server.list

Read. `ListInvServers`, answers `IReadOnlyList<string>`. Core only.

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

## me.codes.off

Write. `TurnOffMyCodes`, answers `Unit`. Core only.

## me.codes.read

Read. `ReadMyCodes`, answers `MyCodes`. Core only.

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

## page.details.save

Write. `SavePageDetails`, answers `PageDetailsDto`. Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Details | `PageDetailsRequest` | required |

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

## page.format

Write. `FormatPage`, answers `FormatDto`. Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| ContentJson | `string` | required |

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

## principal.list

Read. `ListPrincipals`, answers `IReadOnlyList<PrincipalDto>`. Core only.

| Field | Type | |
|---|---|---|
| Query | `string?` |  |

## role.create

Write. `CreateRole`, answers `RoleDto`. Core only.

| Field | Type | |
|---|---|---|
| Name | `string` | required |
| Permissions | `IReadOnlyList<string>` | required |

## role.delete

Write. `DeleteRole`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| RoleId | `Guid` | required |

## role.list

Read. `ListRoles`, answers `IReadOnlyList<RoleDto>`. Core only.

## role.update

Write. `UpdateRole`, answers `RoleDto`. Core only.

| Field | Type | |
|---|---|---|
| RoleId | `Guid` | required |
| Name | `string` | required |
| Permissions | `IReadOnlyList<string>` | required |

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

## settings.codes.read

Read. `ReadCodeSettings`, answers `CodeSettings`. Core only.

## settings.codes.set

Write. `SetCodeSettings`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| RequiredFor | `string` | required |

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
| Visibility | `SpaceVisibility` |  |

## space.delete

Write. `DeleteSpace`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Confirm | `string?` |  |

## space.list

Read. `ListSpaces`, answers `IReadOnlyList<SpaceDto>`.

## space.list.all

Read. `ListAllSpaces`, answers `IReadOnlyList<SpaceDto>`.

## space.list.deleted

Read. `ListDeletedSpaces`, answers `IReadOnlyList<DeletedSpaceDto>`.

## space.read

Read. `ReadSpace`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## space.restore

Write. `RestoreSpace`, answers `SpaceDto`.

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
| RoleId | `Guid?` |  |
| Role | `SpaceRole?` |  |

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
| Visibility | `SpaceVisibility?` |  |
| CommentsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` |  |
| RecycleDays | `int?` |  |
| ReviewMonths | `int?` |  |

## space.update.admin

Write. `UpdateSpaceAdmin`, answers `SpaceDto`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Name | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool?` |  |
| Visibility | `SpaceVisibility?` |  |
| CommentsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` |  |
| RecycleDays | `int?` |  |
| ReviewMonths | `int?` |  |

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

## ui.error.report

Write. `ReportUiError`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Plugin | `string` | required |
| Where | `string?` |  |
| Message | `string?` |  |

## ui.plugins

Read. `ListUiPlugins`, answers `UiPluginsDto`. Core only.

## user.announcer.set

Write. `SetAnnouncer`, answers `Unit`.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Enabled | `bool` | required |

## user.codes.exempt

Write. `SetUserCodesExempt`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Exempt | `bool` | required |

## user.codes.reset

Write. `ResetUserCodes`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |

## user.delete

Write. `DeleteUser`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |

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

## user.password.link

Write. `NewPasswordLink`, answers `InvitedUser`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |

## user.permissions.set

Write. `SetUserPermission`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| UserId | `Guid` | required |
| Permission | `string` | required |
| Granted | `bool` | required |
