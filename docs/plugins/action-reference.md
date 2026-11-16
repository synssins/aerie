# Action reference

Written by the test `ActionReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.

Every operation of the core is one action, sent through one bus. A plugin can put a before hook, a result filter (reads only) or an after handler
on an action of the contract assembly (`Kb.Plugins.Abstractions`). Core-only actions go through the same bus but are not part of the contract:
a plugin cannot hook how plugins, API tokens, storage and log shipping are administered.

A type in a table links to its fields and an example of its JSON under [Types](#types). JSON names are camelCase; an enum is its name as text.
A plugin sends an action with `IActionBus.SendAsync(new TheAction { ... }, ct)` and gets an `Outcome<T>`: `Value` or `Problem`.

## access.decide

Write. `DecideAccess`, answers `Unit`.

| Field | Type | |
|---|---|---|
| RequestId | `Guid` | required |
| Approve | `bool` | required |

## access.request

Write. `RequestAccess`, answers `AccessRequestedDto` ([AccessRequestedDto](#accessrequesteddto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Role | `SpaceRole` ([SpaceRole](#spacerole)) | required |
| Reason | `string?` |  |

## ai.models

Write. `ListAiModels`, answers `string[]`. Core only.

| Field | Type | |
|---|---|---|
| Request | `AiModelsRequest` ([AiModelsRequest](#aimodelsrequest)) | required |

## ai.settings.read

Read. `ReadAiSettings`, answers `AiSettingsDto` ([AiSettingsDto](#aisettingsdto)). Core only.

## ai.settings.save

Write. `SaveAiSettings`, answers `AiSettingsDto` ([AiSettingsDto](#aisettingsdto)). Core only.

| Field | Type | |
|---|---|---|
| Request | `AiSettingsRequest` ([AiSettingsRequest](#aisettingsrequest)) | required |

## ai.test

Write. `TestAi`, answers `AiTestDto` ([AiTestDto](#aitestdto)). Core only.

## announcement.post

Write. `PostAnnouncement`, answers `AnnouncementPostedDto` ([AnnouncementPostedDto](#announcementposteddto)).

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

Write. `AddAttachment`, answers `AttachmentDto` ([AttachmentDto](#attachmentdto)).

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

Read. `ListAudit`, answers `IReadOnlyList<AuditEntryDto>` ([AuditEntryDto](#auditentrydto)).

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

Read. `VerifyAudit`, answers `AuditVerifyResult` ([AuditVerifyResult](#auditverifyresult)).

## auth.codes.enroll

Write. `EnrollCodes`, answers `CodesEnrolled` ([CodesEnrolled](#codesenrolled)). Core only.

| Field | Type | |
|---|---|---|
| Code | `string` |  |

## auth.codes.key

Write. `NewCodeKey`, answers `CodeKey` ([CodeKey](#codekey)). Core only.

## auth.compat.read

Read. `ReadCompatAuthSettings`, answers `CompatAuthSettingsDto` ([CompatAuthSettingsDto](#compatauthsettingsdto)). Core only.

## auth.compat.save

Write. `SaveCompatAuthSettings`, answers `CompatAuthSettingsDto` ([CompatAuthSettingsDto](#compatauthsettingsdto)). Core only.

| Field | Type | |
|---|---|---|
| Settings | `CompatAuthSettingsDto` ([CompatAuthSettingsDto](#compatauthsettingsdto)) | required |

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

Write. `SignIn`, answers `MeResponse` ([MeResponse](#meresponse)).

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| Password | `string` | never logged |

## auth.signin.code

Write. `SignInCode`, answers `MeResponse` ([MeResponse](#meresponse)). Core only.

| Field | Type | |
|---|---|---|
| Code | `string` |  |

## auth.signout

Write. `SignOut`, answers `Unit`.

## bin.browse

Read. `BrowseBin`, answers `IReadOnlyList<BinNodeDto>` ([BinNodeDto](#binnodedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.browse.all

Read. `BrowseAllBins`, answers `IReadOnlyList<BinNodeDto>` ([BinNodeDto](#binnodedto)).

## bin.empty

Write. `EmptyBin`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.list

Read. `ListBin`, answers `IReadOnlyList<BinItemDto>` ([BinItemDto](#binitemdto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## bin.list.all

Read. `ListAllBins`, answers `IReadOnlyList<BinItemDto>` ([BinItemDto](#binitemdto)).

## bin.page.read

Read. `ReadBinPage`, answers `BinPageDto` ([BinPageDto](#binpagedto)).

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

Read. `ReadBrand`, answers `BrandDto` ([BrandDto](#branddto)).

## brand.theme.save

Write. `SaveTheme`, answers `ThemeDto` ([ThemeDto](#themedto)). Core only.

| Field | Type | |
|---|---|---|
| Theme | `ThemeDto` ([ThemeDto](#themedto)) | required |

## brand.update

Write. `UpdateBrand`, answers `Unit`.

| Field | Type | |
|---|---|---|
| ProductName | `string` | required |

## dc.attachment.create

Write. `DcCreateAttachments`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Files | `IReadOnlyList<DcFilePart>` ([DcFilePart](#dcfilepart)) | never logged |
| Comments | `IReadOnlyList<string>` |  |
| MinorEdit | `bool` |  |
| AllowDuplicated | `bool` |  |
| Expand | `string` | required |

## dc.attachment.data

Write. `DcAttachmentData`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |
| File | `DcFilePart?` ([DcFilePart](#dcfilepart)) | never logged |
| Comment | `string?` |  |
| MinorEdit | `bool` |  |

## dc.attachment.download

Read. `DcDownloadAttachment`, answers `FileOut`. Core only.

| Field | Type | |
|---|---|---|
| AttachmentId | `string` | required |
| Version | `int?` |  |

## dc.attachment.list

Read. `DcListAttachments`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| FileName | `string?` |  |
| MediaType | `string?` |  |
| Start | `int` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.attachment.move

Write. `DcMoveAttachment`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |
| NewContentId | `string?` |  |
| NewName | `string?` |  |

## dc.attachment.remove

Write. `DcRemoveAttachment`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |

## dc.attachment.text

Read. `DcAttachmentText`, answers `string`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |
| Limit | `long?` |  |

## dc.attachment.update

Write. `DcUpdateAttachment`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |
| Body | `JsonObject` | required |

## dc.attachment.version.remove

Write. `DcRemoveAttachmentVersion`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| AttachmentId | `string` | required |
| Number | `int` | required |

## dc.body.convert

Read. `DcConvertBody`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| To | `string` | required |
| Value | `string?` |  |
| Representation | `string?` |  |
| ContentId | `string?` |  |
| Expand | `string` | required |

## dc.content.children

Read. `DcContentChildren`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Descendants | `bool` | required |
| Type | `string?` |  |
| Start | `int` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.content.count

Read. `DcCountDescendants`, answers `long`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## dc.content.create

Write. `DcCreateContent`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Body | `JsonObject` | required |
| Status | `string?` |  |
| Expand | `string` | required |

## dc.content.delete

Write. `DcDeleteContent`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Status | `string` | required |

## dc.content.history

Read. `DcContentHistory`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Expand | `string` | required |

## dc.content.list

Read. `DcListContent`, answers `DcJsonPage` ([DcJsonPage](#dcjsonpage)). Core only.

| Field | Type | |
|---|---|---|
| Type | `string` | required |
| SpaceKey | `string?` |  |
| Title | `string?` |  |
| Statuses | `IReadOnlyList<string>` | required |
| Ids | `IReadOnlyList<string>?` |  |
| Start | `int` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.content.macro

Read. `DcReadMacro`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Version | `int` | required |
| MacroId | `string?` |  |
| Hash | `string?` |  |

## dc.content.read

Read. `DcReadContent`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Statuses | `IReadOnlyList<string>` | required |
| Version | `int?` |  |
| Expand | `string` | required |

## dc.content.scan

Read. `DcScanContent`, answers `DcCursorPage` ([DcCursorPage](#dccursorpage)). Core only.

| Field | Type | |
|---|---|---|
| Cursor | `string?` |  |
| Type | `string` | required |
| SpaceKey | `string?` |  |
| Statuses | `IReadOnlyList<string>` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.content.update

Write. `DcUpdateContent`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Body | `JsonObject` | required |
| Status | `string` | required |
| Expand | `string` | required |

## dc.content.version.remove

Write. `DcRemoveVersion`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Number | `int` | required |

## dc.label.add

Write. `DcAddLabels`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Body | `JsonNode` | required |

## dc.label.list

Read. `DcListLabels`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Prefixes | `IReadOnlyList<string>` |  |
| Start | `int` | required |
| Limit | `int` | required |

## dc.label.remove

Write. `DcRemoveLabel`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |
| Label | `string` | required |

## dc.longtask.list

Read. `DcListLongTasks`, answers `DcLongTaskPage` ([DcLongTaskPage](#dclongtaskpage)). Core only.

| Field | Type | |
|---|---|---|
| Start | `int` | required |
| Limit | `int` | required |

## dc.longtask.read

Read. `DcReadLongTask`, answers `DcLongTaskView` ([DcLongTaskView](#dclongtaskview)). Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## dc.search

Read. `DcSearch`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Cql | `string` | required |
| CqlContext | `string?` |  |
| Results | `bool` | required |
| IncludeArchivedSpaces | `bool` |  |
| Excerpt | `string` |  |
| Start | `int` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.space.archive

Write. `DcArchiveSpace`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |
| Archive | `bool` | required |

## dc.space.content

Read. `DcSpaceContent`, answers `JsonObject`. Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |
| Type | `string?` |  |
| RootOnly | `bool` | required |
| Start | `int` | required |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.space.create

Write. `DcCreateSpace`, answers `DcSpaceView` ([DcSpaceView](#dcspaceview)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string?` |  |
| Name | `string?` |  |
| Description | `string?` |  |
| Private | `bool` |  |

## dc.space.delete

Write. `DcDeleteSpace`, answers `DcLongTaskView` ([DcLongTaskView](#dclongtaskview)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |

## dc.space.list

Read. `DcListSpaces`, answers `DcSpacePage` ([DcSpacePage](#dcspacepage)). Core only.

| Field | Type | |
|---|---|---|
| Keys | `IReadOnlyList<string>?` |  |
| Ids | `IReadOnlyList<long>?` |  |
| Type | `string?` |  |
| Status | `string?` |  |
| Labels | `IReadOnlyList<string>?` |  |
| ContentLabels | `IReadOnlyList<string>?` |  |
| NoneCanMatch | `bool` |  |
| Start | `int` | required |
| Limit | `int` | required |

## dc.space.personal.create

Write. `DcCreatePersonalSpace`, answers `DcSpaceView` ([DcSpaceView](#dcspaceview)). Core only.

| Field | Type | |
|---|---|---|
| Description | `string?` |  |
| Private | `bool` |  |

## dc.space.read

Read. `DcReadSpace`, answers `DcSpaceView` ([DcSpaceView](#dcspaceview)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |

## dc.space.trash.empty

Write. `DcEmptyTrash`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |

## dc.space.trash.list

Read. `DcListTrash`, answers `DcCursorPage` ([DcCursorPage](#dccursorpage)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |
| Cursor | `string?` |  |
| Limit | `int` | required |
| Expand | `string` | required |

## dc.space.update

Write. `DcUpdateSpace`, answers `DcSpaceView` ([DcSpaceView](#dcspaceview)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string` | required |
| BodyKey | `string?` |  |
| Name | `string?` |  |
| Description | `string?` |  |

## dc.user.groups

Read. `DcUserGroups`, answers `DcGroupPage` ([DcGroupPage](#dcgrouppage)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string?` |  |
| UserName | `string?` |  |
| Start | `int` | required |
| Limit | `int` | required |

## dc.user.list

Read. `DcListUsers`, answers `DcUserPage` ([DcUserPage](#dcuserpage)). Core only.

| Field | Type | |
|---|---|---|
| Start | `int` | required |
| Limit | `int` | required |

## dc.user.read

Read. `DcReadUser`, answers `DcUser` ([DcUser](#dcuser)). Core only.

| Field | Type | |
|---|---|---|
| Key | `string?` |  |
| UserName | `string?` |  |

## draft.discard

Write. `DiscardDraft`, answers `Unit`.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## draft.read

Read. `ReadDraft`, answers `DraftDto` ([DraftDto](#draftdto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## draft.save

Write. `SaveDraft`, answers `DraftSavedDto` ([DraftSavedDto](#draftsaveddto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Title | `string?` |  |
| ContentJson | `string` | required |
| BaseVersion | `string` | required |
| Tags | `IReadOnlyList<string>?` |  |

## feedback.settings.read

Read. `ReadFeedbackSettings`, answers `FeedbackSettingsDto` ([FeedbackSettingsDto](#feedbacksettingsdto)). Core only.

## feedback.settings.save

Write. `SaveFeedbackSettings`, answers `FeedbackSettingsDto` ([FeedbackSettingsDto](#feedbacksettingsdto)). Core only.

| Field | Type | |
|---|---|---|
| Settings | `FeedbackSettingsDto` ([FeedbackSettingsDto](#feedbacksettingsdto)) | required |

## group.create

Write. `CreateGroup`, answers `GroupDto` ([GroupDto](#groupdto)). Core only.

| Field | Type | |
|---|---|---|
| Name | `string` | required |

## group.delete

Write. `DeleteGroup`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |

## group.list

Read. `ListGroups`, answers `IReadOnlyList<GroupDto>` ([GroupDto](#groupdto)). Core only.

## group.member.add

Write. `AddGroupMember`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |
| UserId | `Guid` | required |

## group.member.list

Read. `ListGroupMembers`, answers `IReadOnlyList<GroupMemberDto>` ([GroupMemberDto](#groupmemberdto)). Core only.

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

Write. `RenameGroup`, answers `GroupDto` ([GroupDto](#groupdto)). Core only.

| Field | Type | |
|---|---|---|
| GroupId | `Guid` | required |
| Name | `string` | required |

## home.read

Read. `ReadHome`, answers `HomeDto` ([HomeDto](#homedto)).

## image.add

Write. `AddImage`, answers `ImageDto` ([ImageDto](#imagedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| File | `UploadContent?` | never logged |

## image.import

Write. `ImportImage`, answers `ImageDto` ([ImageDto](#imagedto)).

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
| Request | `AcceptRequest` ([AcceptRequest](#acceptrequest)) | required |

## import.batch.create

Write. `CreateImportBatch`, answers `ImportBatchDto` ([ImportBatchDto](#importbatchdto)). Core only.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid?` |  |
| ParentId | `Guid?` |  |

## import.batch.start

Write. `StartImportBatch`, answers `ImportBatchDto` ([ImportBatchDto](#importbatchdto)). Core only.

| Field | Type | |
|---|---|---|
| BatchId | `Guid` | required |

## import.discard

Write. `DiscardImport`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ItemId | `Guid` | required |

## import.file.add

Write. `AddImportFile`, answers `ImportFileDto` ([ImportFileDto](#importfiledto)). Core only.

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

Read. `ListImports`, answers `ImportListDto` ([ImportListDto](#importlistdto)). Core only.

## import.merge

Write. `MergeImports`, answers `ImportItemRowDto` ([ImportItemRowDto](#importitemrowdto)). Core only.

| Field | Type | |
|---|---|---|
| ItemIds | `IReadOnlyList<Guid>` | required |

## import.read

Read. `ReadImportItem`, answers `ImportItemDto` ([ImportItemDto](#importitemdto)). Core only.

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

Read. `ReadInventoryAccess`, answers `InvAccessDto` ([InvAccessDto](#invaccessdto)). Core only.

## inventory.access.save

Write. `SaveInventoryAccess`, answers `InvAccessDto` ([InvAccessDto](#invaccessdto)). Core only.

| Field | Type | |
|---|---|---|
| Access | `InvAccessDto` ([InvAccessDto](#invaccessdto)) | required |

## inventory.app.create

Write. `CreateInvApp`, answers `InvAppDto` ([InvAppDto](#invappdto)). Core only.

| Field | Type | |
|---|---|---|
| App | `InvAppRequest` ([InvAppRequest](#invapprequest)) | required |

## inventory.app.delete

Write. `DeleteInvApp`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |

## inventory.app.list

Read. `ListInvApps`, answers `IReadOnlyList<InvAppRowDto>` ([InvAppRowDto](#invapprowdto)). Core only.

| Field | Type | |
|---|---|---|
| Query | `string?` |  |
| Server | `string?` |  |

## inventory.app.read

Read. `ReadInvApp`, answers `InvAppDto` ([InvAppDto](#invappdto)). Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |

## inventory.app.update

Write. `UpdateInvApp`, answers `InvAppDto` ([InvAppDto](#invappdto)). Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |
| App | `InvAppRequest` ([InvAppRequest](#invapprequest)) | required |

## inventory.import

Write. `ImportInventory`, answers `SyncResultDto` ([SyncResultDto](#syncresultdto)). Core only.

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

Write. `SetInvInstall`, answers `InvAppDto` ([InvAppDto](#invappdto)). Core only.

| Field | Type | |
|---|---|---|
| ApplicationId | `Guid` | required |
| Server | `string` | required |
| Version | `string` | required |

## inventory.installs.sync

Write. `SyncInstalls`, answers `SyncResultDto` ([SyncResultDto](#syncresultdto)).

| Field | Type | |
|---|---|---|
| Source | `string` | required |
| Rows | `IReadOnlyList<SyncRowDto>` ([SyncRowDto](#syncrowdto)) | required |
| RemoveMissing | `bool` |  |
| Apply | `bool` |  |

## inventory.me

Read. `ReadInventoryMe`, answers `InvMeDto` ([InvMeDto](#invmedto)). Core only.

## inventory.server.list

Read. `ListInvServers`, answers `IReadOnlyList<string>`. Core only.

## log.read

Read. `ReadLogs`, answers `LogsDto` ([LogsDto](#logsdto)). Core only.

## log.target.add

Write. `AddLogTarget`, answers `LogTargetDto` ([LogTargetDto](#logtargetdto)). Core only.

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

Write. `SaveLogTarget`, answers `LogTargetDto` ([LogTargetDto](#logtargetdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Name | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |
| Enabled | `bool` |  |

## log.target.test

Write. `TestLogTarget`, answers `StorageTestDto` ([StorageTestDto](#storagetestdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## me.codes.off

Write. `TurnOffMyCodes`, answers `Unit`. Core only.

## me.codes.read

Read. `ReadMyCodes`, answers `MyCodes` ([MyCodes](#mycodes)). Core only.

## me.read

Read. `ReadMe`, answers `MeResponse` ([MeResponse](#meresponse)).

## me.settings.read

Read. `ReadMySettings`, answers `UserSettings` ([UserSettings](#usersettings)).

## me.settings.save

Write. `SaveMySettings`, answers `UserSettings` ([UserSettings](#usersettings)).

| Field | Type | |
|---|---|---|
| Theme | `string` | required |
| Width | `string` | required |
| StartPage | `string` | required |
| CodeLanguage | `string` | required |
| Notify | `Dictionary<string, NotifyPref>?` ([NotifyPref](#notifypref)) |  |

## notification.count

Read. `CountNotifications`, answers `NotificationCountDto` ([NotificationCountDto](#notificationcountdto)).

## notification.list

Read. `ListNotifications`, answers `IReadOnlyList<NotificationDto>` ([NotificationDto](#notificationdto)).

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

## oauth.authorize

Write. `DcOAuthAuthorize`, answers `OAuthAuthorizeResult` ([OAuthAuthorizeResult](#oauthauthorizeresult)). Core only.

| Field | Type | |
|---|---|---|
| Query | `IReadOnlyDictionary<string, string[]>` | required |
| SiteIsHttps | `bool` |  |

## oauth.client.create

Write. `CreateOAuthClient`, answers `OAuthClientCreatedDto` ([OAuthClientCreatedDto](#oauthclientcreateddto)). Core only.

| Field | Type | |
|---|---|---|
| Request | `OAuthClientRequest` ([OAuthClientRequest](#oauthclientrequest)) | required |

## oauth.client.delete

Write. `DeleteOAuthClient`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## oauth.client.list

Read. `ListOAuthClients`, answers `IReadOnlyList<OAuthClientDto>` ([OAuthClientDto](#oauthclientdto)). Core only.

## oauth.client.read

Read. `ReadOAuthClient`, answers `OAuthClientDto` ([OAuthClientDto](#oauthclientdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## oauth.client.secret.rotate

Write. `RotateOAuthClientSecret`, answers `OAuthClientCreatedDto` ([OAuthClientCreatedDto](#oauthclientcreateddto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## oauth.client.update

Write. `UpdateOAuthClient`, answers `OAuthClientDto` ([OAuthClientDto](#oauthclientdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Request | `OAuthClientRequest` ([OAuthClientRequest](#oauthclientrequest)) | required |

## oauth.grant.list

Read. `ListMyOAuthGrants`, answers `IReadOnlyList<OAuthGrantDto>` ([OAuthGrantDto](#oauthgrantdto)). Core only.

## oauth.grant.revoke

Write. `RevokeMyOAuthGrant`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## oauth.request.decide

Write. `DecideOAuthAuthorizeRequest`, answers `OAuthDecisionDto` ([OAuthDecisionDto](#oauthdecisiondto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Allow | `bool` | required |

## oauth.request.read

Read. `ReadOAuthAuthorizeRequest`, answers `OAuthAuthorizeRequestDto` ([OAuthAuthorizeRequestDto](#oauthauthorizerequestdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## oauth.revoke

Write. `DcOAuthRevoke`, answers `OAuthEndpointResult` ([OAuthEndpointResult](#oauthendpointresult)). Core only.

| Field | Type | |
|---|---|---|
| Parameters | `IReadOnlyDictionary<string, string[]>` | required |
| Basic | `ValueTuple<string, string>?` |  |
| BadBasic | `bool` |  |
| Address | `string` |  |
| SiteIsHttps | `bool` |  |

## oauth.token

Write. `DcOAuthToken`, answers `OAuthEndpointResult` ([OAuthEndpointResult](#oauthendpointresult)). Core only.

| Field | Type | |
|---|---|---|
| Parameters | `IReadOnlyDictionary<string, string[]>` | required |
| Basic | `ValueTuple<string, string>?` |  |
| BadBasic | `bool` |  |
| Address | `string` |  |
| SiteIsHttps | `bool` |  |

## page.ai.review

Write. `ReviewPageAi`, answers `PageDto` ([PageDto](#pagedto)). Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.assist

Write. `AssistPage`, answers `AssistDto` ([AssistDto](#assistdto)). Core only.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| PageId | `Guid?` |  |
| Title | `string?` |  |
| ContentJson | `string` | required |
| Text | `string?` |  |
| Files | `IReadOnlyList<AssistFile>?` ([AssistFile](#assistfile)) | never logged |

## page.create

Write. `CreatePage`, answers `PageDto` ([PageDto](#pagedto)).

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

Write. `SavePageDetails`, answers `PageDetailsDto` ([PageDetailsDto](#pagedetailsdto)). Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Details | `PageDetailsRequest` ([PageDetailsRequest](#pagedetailsrequest)) | required |

## page.diff

Read. `DiffPage`, answers `DiffDto` ([DiffDto](#diffdto)).

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

## page.feedback.read

Read. `ReadPageFeedback`, answers `PageFeedbackDto` ([PageFeedbackDto](#pagefeedbackdto)). Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.feedback.save

Write. `SavePageFeedback`, answers `PageFeedbackDto` ([PageFeedbackDto](#pagefeedbackdto)). Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| CommentsEnabled | `bool?` |  |
| RatingsEnabled | `bool?` |  |

## page.format

Write. `FormatPage`, answers `FormatDto` ([FormatDto](#formatdto)). Core only.

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| ContentJson | `string` | required |
| Roles | `IReadOnlyList<FormatRoleDto>?` ([FormatRoleDto](#formatroledto)) |  |
| Keep | `IReadOnlyList<int>?` |  |

## page.markdown

Read. `ReadPageMarkdown`, answers `PageMarkdownDto` ([PageMarkdownDto](#pagemarkdowndto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.move

Write. `MovePage`, answers `PageDto` ([PageDto](#pagedto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| SpaceId | `Guid?` |  |
| ParentId | `Guid?` |  |

## page.read

Read. `ReadPage`, answers `PageDto` ([PageDto](#pagedto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.restore

Write. `RestorePage`, answers `RestoredPageDto` ([RestoredPageDto](#restoredpagedto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.save

Write. `SavePage`, answers `PageDto` ([PageDto](#pagedto)).

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

Read. `ListVersions`, answers `IReadOnlyList<VersionDto>` ([VersionDto](#versiondto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |

## page.version.read

Read. `ReadVersion`, answers `VersionContentDto` ([VersionContentDto](#versioncontentdto)).

| Field | Type | |
|---|---|---|
| PageId | `Guid` | required |
| Version | `string` | required |

## page.version.restore

Write. `RestoreVersion`, answers `PageDto` ([PageDto](#pagedto)).

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

Read. `ReadPluginHealth`, answers `PluginHealthDto` ([PluginHealthDto](#pluginhealthdto)). Core only.

## plugin.install

Write. `InstallPlugin`, answers `InstalledDto` ([InstalledDto](#installeddto)). Core only.

| Field | Type | |
|---|---|---|
| Package | `UploadContent` | required, never logged |

## plugin.list

Read. `ListPlugins`, answers `PluginListDto` ([PluginListDto](#pluginlistdto)). Core only.

## plugin.lock.release

Write. `ReleasePluginLock`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.permissions.read

Read. `ReadPluginPermissions`, answers `PluginPermissionsDto` ([PluginPermissionsDto](#pluginpermissionsdto)). Core only.

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

Read. `ReadPluginSettings`, answers `PluginSettingsDto` ([PluginSettingsDto](#pluginsettingsdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `string` | required |

## plugin.settings.save

Write. `SavePluginSettings`, answers `PluginSettingsSaved` ([PluginSettingsSaved](#pluginsettingssaved)). Core only.

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

Read. `ListPrincipals`, answers `IReadOnlyList<PrincipalDto>` ([PrincipalDto](#principaldto)). Core only.

| Field | Type | |
|---|---|---|
| Query | `string?` |  |

## role.create

Write. `CreateRole`, answers `RoleDto` ([RoleDto](#roledto)). Core only.

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

Read. `ListRoles`, answers `IReadOnlyList<RoleDto>` ([RoleDto](#roledto)). Core only.

## role.update

Write. `UpdateRole`, answers `RoleDto` ([RoleDto](#roledto)). Core only.

| Field | Type | |
|---|---|---|
| RoleId | `Guid` | required |
| Name | `string` | required |
| Permissions | `IReadOnlyList<string>` | required |

## search.query

Read. `SearchPages`, answers `SearchResponse` ([SearchResponse](#searchresponse)).

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

Write. `ReindexSearch`, answers `ReindexResult` ([ReindexResult](#reindexresult)).

## search.synonyms.read

Read. `ReadSynonyms`, answers `SynonymsDto` ([SynonymsDto](#synonymsdto)).

## search.synonyms.save

Write. `SaveSynonyms`, answers `SynonymsDto` ([SynonymsDto](#synonymsdto)).

| Field | Type | |
|---|---|---|
| Text | `string` | required |

## settings.codes.read

Read. `ReadCodeSettings`, answers `CodeSettings` ([CodeSettings](#codesettings)). Core only.

## settings.codes.set

Write. `SetCodeSettings`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| RequiredFor | `string` | required |

## settings.uploads.read

Read. `ReadUploadSettings`, answers `UploadSettingsDto` ([UploadSettingsDto](#uploadsettingsdto)).

## settings.uploads.save

Write. `SaveUploadSettings`, answers `UploadSettingsDto` ([UploadSettingsDto](#uploadsettingsdto)).

| Field | Type | |
|---|---|---|
| AttachmentMb | `int` | required |

## setup.run

Write. `RunSetup`, answers `MeResponse` ([MeResponse](#meresponse)).

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| DisplayName | `string` | required |
| Password | `string` | never logged |
| Code | `string?` | never logged |

## setup.status

Read. `ReadSetupStatus`, answers `SetupStatus` ([SetupStatus](#setupstatus)).

## space.create

Write. `CreateSpace`, answers `SpaceDto` ([SpaceDto](#spacedto)).

| Field | Type | |
|---|---|---|
| Name | `string` | required |
| Key | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool` |  |
| Visibility | `SpaceVisibility` ([SpaceVisibility](#spacevisibility)) |  |

## space.delete

Write. `DeleteSpace`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Confirm | `string?` |  |

## space.list

Read. `ListSpaces`, answers `IReadOnlyList<SpaceDto>` ([SpaceDto](#spacedto)).

## space.list.all

Read. `ListAllSpaces`, answers `IReadOnlyList<SpaceDto>` ([SpaceDto](#spacedto)).

## space.list.deleted

Read. `ListDeletedSpaces`, answers `IReadOnlyList<DeletedSpaceDto>` ([DeletedSpaceDto](#deletedspacedto)).

## space.read

Read. `ReadSpace`, answers `SpaceDto` ([SpaceDto](#spacedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## space.restore

Write. `RestoreSpace`, answers `SpaceDto` ([SpaceDto](#spacedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |

## space.role.grant

Write. `GrantSpaceRole`, answers `Unit`.

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| PrincipalType | `PrincipalType` ([PrincipalType](#principaltype)) | required |
| PrincipalId | `string` | required |
| RoleId | `Guid?` |  |
| Role | `SpaceRole?` ([SpaceRole](#spacerole)) |  |

## space.role.list

Read. `ListSpaceRoles`, answers `IReadOnlyList<RoleAssignmentDto>` ([RoleAssignmentDto](#roleassignmentdto)).

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

Write. `OpenEmergencyAccess`, answers `EmergencyAccessDto` ([EmergencyAccessDto](#emergencyaccessdto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Reason | `string` | required |

## space.update

Write. `UpdateSpace`, answers `SpaceDto` ([SpaceDto](#spacedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Name | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool?` |  |
| Visibility | `SpaceVisibility?` ([SpaceVisibility](#spacevisibility)) |  |
| CommentsEnabled | `bool?` |  |
| RatingsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` ([AgingAudience](#agingaudience)) |  |
| RecycleDays | `int?` |  |
| ReviewMonths | `int?` |  |

## space.update.admin

Write. `UpdateSpaceAdmin`, answers `SpaceDto` ([SpaceDto](#spacedto)).

| Field | Type | |
|---|---|---|
| SpaceId | `Guid` | required |
| Name | `string?` |  |
| Description | `string?` |  |
| IsSensitive | `bool?` |  |
| Visibility | `SpaceVisibility?` ([SpaceVisibility](#spacevisibility)) |  |
| CommentsEnabled | `bool?` |  |
| RatingsEnabled | `bool?` |  |
| AgingDays | `int?` |  |
| AgingAudience | `AgingAudience?` ([AgingAudience](#agingaudience)) |  |
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

Write. `StartStorageMove`, answers `StorageMoveDto` ([StorageMoveDto](#storagemovedto)). Core only.

| Field | Type | |
|---|---|---|
| ToId | `Guid` | required |

## storage.move.switch

Write. `SwitchStorageMove`, answers `Unit`. Core only.

## storage.place.add

Write. `AddStoragePlace`, answers `StoragePlaceDto` ([StoragePlaceDto](#storageplacedto)). Core only.

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

Write. `SaveStoragePlace`, answers `StoragePlaceDto` ([StoragePlaceDto](#storageplacedto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |
| Name | `string?` |  |
| Settings | `IReadOnlyDictionary<string, string>?` |  |
| Secrets | `IReadOnlyDictionary<string, string>?` | never logged |

## storage.place.test

Write. `TestStoragePlace`, answers `StorageTestDto` ([StorageTestDto](#storagetestdto)). Core only.

| Field | Type | |
|---|---|---|
| Id | `Guid` | required |

## storage.read

Read. `ReadStorage`, answers `StorageDto` ([StorageDto](#storagedto)). Core only.

## token.create

Write. `CreateToken`, answers `ApiTokenCreatedDto` ([ApiTokenCreatedDto](#apitokencreateddto)). Core only.

| Field | Type | |
|---|---|---|
| Name | `string?` |  |
| Kind | `TokenKind` ([TokenKind](#tokenkind)) | required |
| Scopes | `IReadOnlyList<TokenScope>?` ([TokenScope](#tokenscope)) |  |
| Days | `int?` |  |
| AccountId | `Guid?` |  |

## token.list

Read. `ListMyTokens`, answers `IReadOnlyList<ApiTokenDto>` ([ApiTokenDto](#apitokendto)). Core only.

## token.list.all

Read. `ListAllTokens`, answers `IReadOnlyList<ApiTokenDto>` ([ApiTokenDto](#apitokendto)). Core only.

## token.revoke

Write. `RevokeToken`, answers `Unit`. Core only.

| Field | Type | |
|---|---|---|
| TokenId | `Guid` | required |

## token.settings.read

Read. `ReadTokenSettings`, answers `TokenSettingsDto` ([TokenSettingsDto](#tokensettingsdto)). Core only.

## token.settings.save

Write. `SaveTokenSettings`, answers `TokenSettingsDto` ([TokenSettingsDto](#tokensettingsdto)). Core only.

| Field | Type | |
|---|---|---|
| MaxDays | `int` | required |

## tree.read

Read. `ReadTree`, answers `IReadOnlyList<TreeNodeDto>` ([TreeNodeDto](#treenodedto)).

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

Read. `ListUiPlugins`, answers `UiPluginsDto` ([UiPluginsDto](#uipluginsdto)). Core only.

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

Write. `InviteUser`, answers `InvitedUser` ([InvitedUser](#inviteduser)). Core only.

| Field | Type | |
|---|---|---|
| UserName | `string` | required |
| DisplayName | `string` | required |
| Email | `string?` |  |
| IsAdmin | `bool` |  |

## user.list

Read. `ListUsers`, answers `IReadOnlyList<AdminUserDto>` ([AdminUserDto](#adminuserdto)).

## user.password.link

Write. `NewPasswordLink`, answers `InvitedUser` ([InvitedUser](#inviteduser)). Core only.

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

## Types

The records and enums the actions and routes use. `?` means it may be null or left out.

### AcceptPage

| Field | Type |
|---|---|
| title | `string` |
| contentJson | `string` |
| tags | `IReadOnlyList<string>?` |

```json
{
  "title": "text",
  "contentJson": "text",
  "tags": [
    "text"
  ]
}
```

### AcceptRequest

| Field | Type |
|---|---|
| pages | `IReadOnlyList<AcceptPage>?` ([AcceptPage](#acceptpage)) |
| spaceId | `Guid?` |
| parentId | `Guid?` |
| updatePageId | `Guid?` |
| details | `PageDetailsRequest?` ([PageDetailsRequest](#pagedetailsrequest)) |
| attachOriginals | `bool` |
| newFolder | `string?` |

```json
{
  "pages": [
    {
      "title": "text",
      "contentJson": "text",
      "tags": [
        "text"
      ]
    }
  ],
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "updatePageId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "details": {
    "appliesTo": [
      {
        "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        "servers": null
      }
    ],
    "ownerType": "text",
    "ownerId": "text",
    "reviewMonths": 0
  },
  "attachOriginals": false,
  "newFolder": "text"
}
```

### AccessRequestBody

| Field | Type |
|---|---|
| role | `SpaceRole` ([SpaceRole](#spacerole)) |
| reason | `string?` |

```json
{
  "role": "Reader",
  "reason": "text"
}
```

### AccessRequestedDto

| Field | Type |
|---|---|
| id | `Guid` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### AdminUserDto

| Field | Type |
|---|---|
| id | `Guid` |
| userName | `string` |
| displayName | `string` |
| source | `string` |
| isAdmin | `bool` |
| isBreakGlass | `bool` |
| createdAt | `DateTimeOffset` |
| deactivatedAt | `DateTimeOffset?` |
| canAnnounce | `bool` |
| permissions | `IReadOnlyList<string>?` |
| groups | `IReadOnlyList<string>?` |
| codesExempt | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "userName": "text",
  "displayName": "text",
  "source": "text",
  "isAdmin": false,
  "isBreakGlass": false,
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "deactivatedAt": "2026-11-14T10:00:00\u002B00:00",
  "canAnnounce": false,
  "permissions": [
    "text"
  ],
  "groups": [
    "text"
  ],
  "codesExempt": false
}
```

### AgingAudience

Enum: `Everyone`, `Supervisors`.

### AiAccess

Enum: `Off`, `Admins`, `Everyone`.

### AiModelsRequest

| Field | Type |
|---|---|
| baseUrl | `string?` |
| apiKey | `string?` |

```json
{
  "baseUrl": "text",
  "apiKey": "text"
}
```

### AiSettings

| Field | Type |
|---|---|
| enabled | `bool` |
| baseUrl | `string` |
| model | `string` |
| timeoutSeconds | `int` |
| maxTokens | `int` |
| temperature | `Double` |
| reasoning | `bool` |
| ruleset | `string` |
| maxInputChars | `int` |
| userPerMinute | `int` |
| tokenPerMinute | `int` |
| importConcurrency | `int` |
| features | `Dictionary<string, AiAccess>` ([AiAccess](#aiaccess)) |

```json
{
  "enabled": false,
  "baseUrl": "text",
  "model": "text",
  "timeoutSeconds": 0,
  "maxTokens": 0,
  "temperature": 0.5,
  "reasoning": false,
  "ruleset": "text",
  "maxInputChars": 0,
  "userPerMinute": 0,
  "tokenPerMinute": 0,
  "importConcurrency": 0,
  "features": {
    "key": "Off"
  }
}
```

### AiSettingsDto

| Field | Type |
|---|---|
| settings | `AiSettings` ([AiSettings](#aisettings)) |
| apiKeySet | `bool` |
| rulesetIsDefault | `bool` |
| defaultRuleset | `string` |

```json
{
  "settings": {
    "enabled": false,
    "baseUrl": "text",
    "model": "text",
    "timeoutSeconds": 0,
    "maxTokens": 0,
    "temperature": 0.5,
    "reasoning": false,
    "ruleset": "text",
    "maxInputChars": 0,
    "userPerMinute": 0,
    "tokenPerMinute": 0,
    "importConcurrency": 0,
    "features": {
      "key": "Off"
    }
  },
  "apiKeySet": false,
  "rulesetIsDefault": false,
  "defaultRuleset": "text"
}
```

### AiSettingsRequest

| Field | Type |
|---|---|
| settings | `AiSettings?` ([AiSettings](#aisettings)) |
| apiKey | `string?` |

```json
{
  "settings": {
    "enabled": false,
    "baseUrl": "text",
    "model": "text",
    "timeoutSeconds": 0,
    "maxTokens": 0,
    "temperature": 0.5,
    "reasoning": false,
    "ruleset": "text",
    "maxInputChars": 0,
    "userPerMinute": 0,
    "tokenPerMinute": 0,
    "importConcurrency": 0,
    "features": {
      "key": "Off"
    }
  },
  "apiKey": "text"
}
```

### AiTestDto

| Field | Type |
|---|---|
| ok | `bool` |
| model | `string?` |
| milliseconds | `long` |
| reasoningEmpty | `bool` |
| error | `string?` |

```json
{
  "ok": false,
  "model": "text",
  "milliseconds": 0,
  "reasoningEmpty": false,
  "error": "text"
}
```

### AnnouncementDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |
| body | `string` |
| by | `string` |
| createdAt | `DateTimeOffset` |
| expiresAt | `DateTimeOffset?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "body": "text",
  "by": "text",
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "expiresAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### AnnouncementPostedDto

| Field | Type |
|---|---|
| id | `Guid` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### AnnouncerRequest

| Field | Type |
|---|---|
| enabled | `bool` |

```json
{
  "enabled": false
}
```

### ApiTokenCreatedDto

| Field | Type |
|---|---|
| token | `string` |
| info | `ApiTokenDto` ([ApiTokenDto](#apitokendto)) |

```json
{
  "token": "text",
  "info": {
    "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "name": "text",
    "kind": "Personal",
    "prefix": "text",
    "scopes": [
      {
        "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        "access": "Read"
      }
    ],
    "ownerId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "owner": "text",
    "createdAt": "2026-11-14T10:00:00\u002B00:00",
    "expiresAt": "2026-11-14T10:00:00\u002B00:00",
    "revokedAt": "2026-11-14T10:00:00\u002B00:00",
    "lastUsedAt": "2026-11-14T10:00:00\u002B00:00",
    "state": "text"
  }
}
```

### ApiTokenDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| kind | `TokenKind` ([TokenKind](#tokenkind)) |
| prefix | `string` |
| scopes | `IReadOnlyList<TokenScope>` ([TokenScope](#tokenscope)) |
| ownerId | `Guid` |
| owner | `string` |
| createdAt | `DateTimeOffset` |
| expiresAt | `DateTimeOffset` |
| revokedAt | `DateTimeOffset?` |
| lastUsedAt | `DateTimeOffset?` |
| state | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "kind": "Personal",
  "prefix": "text",
  "scopes": [
    {
      "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "access": "Read"
    }
  ],
  "ownerId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "owner": "text",
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "expiresAt": "2026-11-14T10:00:00\u002B00:00",
  "revokedAt": "2026-11-14T10:00:00\u002B00:00",
  "lastUsedAt": "2026-11-14T10:00:00\u002B00:00",
  "state": "text"
}
```

### AppliesToDto

| Field | Type |
|---|---|
| applicationId | `Guid` |
| name | `string` |
| servers | `IReadOnlyList<string>` |

```json
{
  "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "servers": [
    "text"
  ]
}
```

### AppliesToRequest

| Field | Type |
|---|---|
| applicationId | `Guid` |
| servers | `IReadOnlyList<string>?` |

```json
{
  "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "servers": [
    "text"
  ]
}
```

### AssistChangeDto

| Field | Type |
|---|---|
| from | `string` |
| to | `string` |

```json
{
  "from": "text",
  "to": "text"
}
```

### AssistDto

| Field | Type |
|---|---|
| title | `string` |
| contentJson | `string` |
| diff | `IReadOnlyList<DiffLine>` ([DiffLine](#diffline)) |
| removed | `IReadOnlyList<FormatRemovedDto>` ([FormatRemovedDto](#formatremoveddto)) |
| changed | `IReadOnlyList<AssistChangeDto>` ([AssistChangeDto](#assistchangedto)) |
| invented | `IReadOnlyList<string>` |
| warnings | `IReadOnlyList<string>` |
| tags | `IReadOnlyList<string>` |
| appliesTo | `IReadOnlyList<AppliesToDto>` ([AppliesToDto](#appliestodto)) |

```json
{
  "title": "text",
  "contentJson": "text",
  "diff": [
    {
      "op": "Same",
      "text": "text"
    }
  ],
  "removed": [
    {
      "units": [
        0
      ],
      "text": "text",
      "sameAs": "text"
    }
  ],
  "changed": [
    {
      "from": "text",
      "to": "text"
    }
  ],
  "invented": [
    "text"
  ],
  "warnings": [
    "text"
  ],
  "tags": [
    "text"
  ],
  "appliesTo": [
    {
      "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "servers": [
        "text"
      ]
    }
  ]
}
```

### AssistFile

| Field | Type |
|---|---|
| name | `string` |
| data | `string` |

```json
{
  "name": "text",
  "data": "text"
}
```

### AssistRequest

| Field | Type |
|---|---|
| pageId | `Guid?` |
| title | `string?` |
| contentJson | `string?` |
| text | `string?` |
| files | `IReadOnlyList<AssistFile>?` ([AssistFile](#assistfile)) |

```json
{
  "pageId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "contentJson": "text",
  "text": "text",
  "files": [
    {
      "name": "text",
      "data": "text"
    }
  ]
}
```

### AttachmentDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| size | `long` |
| url | `string` |
| at | `DateTimeOffset` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "size": 0,
  "url": "text",
  "at": "2026-11-14T10:00:00\u002B00:00"
}
```

### AuditEntryDto

| Field | Type |
|---|---|
| sequence | `long` |
| at | `DateTimeOffset` |
| action | `string` |
| actor | `string?` |
| targetType | `string?` |
| targetId | `string?` |
| via | `string` |
| token | `string?` |

```json
{
  "sequence": 0,
  "at": "2026-11-14T10:00:00\u002B00:00",
  "action": "text",
  "actor": "text",
  "targetType": "text",
  "targetId": "text",
  "via": "text",
  "token": "text"
}
```

### AuditVerifyResult

| Field | Type |
|---|---|
| valid | `bool` |
| firstBrokenSequence | `long?` |

```json
{
  "valid": false,
  "firstBrokenSequence": 0
}
```

### BinItemDto

| Field | Type |
|---|---|
| id | `Guid` |
| spaceId | `Guid` |
| spaceName | `string` |
| kind | `string` |
| title | `string` |
| location | `string` |
| items | `int` |
| deletedAt | `DateTimeOffset` |
| deletedBy | `string` |
| hidden | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceName": "text",
  "kind": "text",
  "title": "text",
  "location": "text",
  "items": 0,
  "deletedAt": "2026-11-14T10:00:00\u002B00:00",
  "deletedBy": "text",
  "hidden": false
}
```

### BinNodeDto

| Field | Type |
|---|---|
| id | `Guid` |
| parentId | `Guid?` |
| spaceId | `Guid` |
| kind | `string` |
| title | `string` |
| sort | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "text",
  "title": "text",
  "sort": 0
}
```

### BinPageDto

| Field | Type |
|---|---|
| id | `Guid` |
| spaceId | `Guid` |
| kind | `string` |
| title | `string` |
| contentJson | `string` |
| path | `IReadOnlyList<string>` |
| updatedAt | `DateTimeOffset` |
| updatedBy | `string` |
| deletedAt | `DateTimeOffset` |
| deletedBy | `string` |
| restoreId | `Guid?` |
| children | `IReadOnlyList<BinNodeDto>` ([BinNodeDto](#binnodedto)) |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "text",
  "title": "text",
  "contentJson": "text",
  "path": [
    "text"
  ],
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "updatedBy": "text",
  "deletedAt": "2026-11-14T10:00:00\u002B00:00",
  "deletedBy": "text",
  "restoreId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "children": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "kind": "text",
      "title": "text",
      "sort": 0
    }
  ]
}
```

### BrandDto

| Field | Type |
|---|---|
| productName | `string` |
| logoLightUrl | `string?` |
| logoDarkUrl | `string?` |
| primaryColor | `string` |
| signInNote | `string?` |
| theme | `ThemeDto?` ([ThemeDto](#themedto)) |

```json
{
  "productName": "text",
  "logoLightUrl": "text",
  "logoDarkUrl": "text",
  "primaryColor": "text",
  "signInNote": "text",
  "theme": {
    "day": {
      "key": "text"
    },
    "night": {
      "key": "text"
    }
  }
}
```

### CodeKey

| Field | Type |
|---|---|
| key | `string` |
| uri | `string` |

```json
{
  "key": "text",
  "uri": "text"
}
```

### CodeRequest

| Field | Type |
|---|---|
| code | `string?` |

```json
{
  "code": "text"
}
```

### CodeSettings

| Field | Type |
|---|---|
| requiredFor | `string` |

```json
{
  "requiredFor": "text"
}
```

### CodesEnrolled

| Field | Type |
|---|---|
| recoveryCodes | `IReadOnlyList<string>` |
| me | `MeResponse` ([MeResponse](#meresponse)) |

```json
{
  "recoveryCodes": [
    "text"
  ],
  "me": {
    "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "userName": "text",
    "displayName": "text",
    "isAdmin": false,
    "isBreakGlass": false,
    "canAnnounce": false,
    "permissions": [
      "text"
    ]
  }
}
```

### CodesExemptRequest

| Field | Type |
|---|---|
| exempt | `bool` |

```json
{
  "exempt": false
}
```

### CompatAuthSettingsDto

| Field | Type |
|---|---|
| basicPasswordEnabled | `bool` |
| allowHttpRedirects | `bool` |
| codeSeconds | `int` |
| accessTokenSeconds | `int` |
| refreshTokenDays | `int` |
| grantMaxDays | `int?` |

```json
{
  "basicPasswordEnabled": false,
  "allowHttpRedirects": false,
  "codeSeconds": 0,
  "accessTokenSeconds": 0,
  "refreshTokenDays": 0,
  "grantMaxDays": 0
}
```

### CreatePageRequest

| Field | Type |
|---|---|
| parentId | `Guid?` |
| kind | `PageKind` ([PageKind](#pagekind)) |
| title | `string` |
| tags | `string[]?` |
| contentJson | `string?` |

```json
{
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "Folder",
  "title": "text",
  "tags": [
    "text"
  ],
  "contentJson": "text"
}
```

### CreateSpaceRequest

| Field | Type |
|---|---|
| name | `string` |
| key | `string?` |
| description | `string?` |
| isSensitive | `bool` |
| visibility | `SpaceVisibility` ([SpaceVisibility](#spacevisibility)) |

```json
{
  "name": "text",
  "key": "text",
  "description": "text",
  "isSensitive": false,
  "visibility": "Private"
}
```

### CreateTokenRequest

| Field | Type |
|---|---|
| name | `string?` |
| kind | `TokenKind` ([TokenKind](#tokenkind)) |
| scopes | `IReadOnlyList<TokenScope>?` ([TokenScope](#tokenscope)) |
| days | `int?` |
| accountId | `Guid?` |

```json
{
  "name": "text",
  "kind": "Personal",
  "scopes": [
    {
      "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "access": "Read"
    }
  ],
  "days": 0,
  "accountId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### CrumbDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text"
}
```

### DcConvertBody

| Field | Type |
|---|---|
| to | `string` |
| value | `string?` |
| representation | `string?` |
| contentId | `string?` |
| expand | `string` |

```json
{
  "to": "text",
  "value": "text",
  "representation": "text",
  "contentId": "text",
  "expand": "text"
}
```

### DcCursorPage

| Field | Type |
|---|---|
| items | `IReadOnlyList<JsonObject>` |
| cursor | `string?` |
| next | `string?` |
| prev | `string?` |

```json
{
  "items": [
    {
      "key": {}
    }
  ],
  "cursor": "text",
  "next": "text",
  "prev": "text"
}
```

### DcFilePart

| Field | Type |
|---|---|
| fileName | `string?` |
| contentType | `string?` |
| length | `long?` |

```json
{
  "fileName": "text",
  "contentType": "text",
  "length": 0
}
```

### DcGroupPage

| Field | Type |
|---|---|
| names | `IReadOnlyList<string>` |
| more | `bool` |

```json
{
  "names": [
    "text"
  ],
  "more": false
}
```

### DcJsonPage

| Field | Type |
|---|---|
| items | `IReadOnlyList<JsonObject>` |
| more | `bool` |

```json
{
  "items": [
    {
      "key": {}
    }
  ],
  "more": false
}
```

### DcLongTaskPage

| Field | Type |
|---|---|
| tasks | `IReadOnlyList<DcLongTaskView>` ([DcLongTaskView](#dclongtaskview)) |
| more | `bool` |

```json
{
  "tasks": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "subject": "text",
      "startedAt": "2026-11-14T10:00:00\u002B00:00",
      "finishedAt": "2026-11-14T10:00:00\u002B00:00",
      "successful": false
    }
  ],
  "more": false
}
```

### DcLongTaskView

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| subject | `string` |
| startedAt | `DateTimeOffset` |
| finishedAt | `DateTimeOffset?` |
| successful | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "subject": "text",
  "startedAt": "2026-11-14T10:00:00\u002B00:00",
  "finishedAt": "2026-11-14T10:00:00\u002B00:00",
  "successful": false
}
```

### DcSpacePage

| Field | Type |
|---|---|
| spaces | `IReadOnlyList<DcSpaceView>` ([DcSpaceView](#dcspaceview)) |
| more | `bool` |

```json
{
  "spaces": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "dcId": 0,
      "key": "text",
      "name": "text",
      "description": "text",
      "personal": false,
      "archived": false,
      "creator": {
        "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        "userName": "text",
        "displayName": "text",
        "deactivated": false,
        "deleted": false
      },
      "createdAt": "2026-11-14T10:00:00\u002B00:00"
    }
  ],
  "more": false
}
```

### DcSpaceView

| Field | Type |
|---|---|
| id | `Guid` |
| dcId | `long` |
| key | `string` |
| name | `string` |
| description | `string?` |
| personal | `bool` |
| archived | `bool` |
| creator | `DcUser?` ([DcUser](#dcuser)) |
| createdAt | `DateTimeOffset` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "dcId": 0,
  "key": "text",
  "name": "text",
  "description": "text",
  "personal": false,
  "archived": false,
  "creator": {
    "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "userName": "text",
    "displayName": "text",
    "deactivated": false,
    "deleted": false
  },
  "createdAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### DcUser

| Field | Type |
|---|---|
| id | `Guid` |
| userName | `string` |
| displayName | `string` |
| deactivated | `bool` |
| deleted | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "userName": "text",
  "displayName": "text",
  "deactivated": false,
  "deleted": false
}
```

### DcUserPage

| Field | Type |
|---|---|
| users | `IReadOnlyList<DcUser>` ([DcUser](#dcuser)) |
| more | `bool` |

```json
{
  "users": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "userName": "text",
      "displayName": "text",
      "deactivated": false,
      "deleted": false
    }
  ],
  "more": false
}
```

### DecideBody

| Field | Type |
|---|---|
| approve | `bool` |

```json
{
  "approve": false
}
```

### DecideOAuthAuthorizeRequest

| Field | Type |
|---|---|
| id | `Guid` |
| allow | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "allow": false
}
```

### DeleteSpaceRequest

| Field | Type |
|---|---|
| confirm | `string?` |

```json
{
  "confirm": "text"
}
```

### DeletedSpaceDto

| Field | Type |
|---|---|
| id | `Guid` |
| key | `string` |
| name | `string` |
| isSensitive | `bool` |
| deletedAt | `DateTimeOffset` |
| deletedBy | `string` |
| pages | `int?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "key": "text",
  "name": "text",
  "isSensitive": false,
  "deletedAt": "2026-11-14T10:00:00\u002B00:00",
  "deletedBy": "text",
  "pages": 0
}
```

### DiffDto

| Field | Type |
|---|---|
| from | `string` |
| to | `string` |
| lines | `IReadOnlyList<DiffLine>` ([DiffLine](#diffline)) |

```json
{
  "from": "text",
  "to": "text",
  "lines": [
    {
      "op": "Same",
      "text": "text"
    }
  ]
}
```

### DiffLine

| Field | Type |
|---|---|
| op | `DiffOp` ([DiffOp](#diffop)) |
| text | `string` |

```json
{
  "op": "Same",
  "text": "text"
}
```

### DiffOp

Enum: `Same`, `Add`, `Del`.

### DraftDto

| Field | Type |
|---|---|
| title | `string` |
| contentJson | `string` |
| tags | `IReadOnlyList<string>` |
| baseVersion | `string` |
| updatedAt | `DateTimeOffset` |

```json
{
  "title": "text",
  "contentJson": "text",
  "tags": [
    "text"
  ],
  "baseVersion": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### DraftInfoDto

| Field | Type |
|---|---|
| mine | `bool` |
| by | `string` |
| updatedAt | `DateTimeOffset` |

```json
{
  "mine": false,
  "by": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### DraftSavedDto

| Field | Type |
|---|---|
| updatedAt | `DateTimeOffset` |

```json
{
  "updatedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### EmergencyAccessDto

| Field | Type |
|---|---|
| expiresAt | `DateTimeOffset?` |

```json
{
  "expiresAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### EmergencyBody

| Field | Type |
|---|---|
| reason | `string` |

```json
{
  "reason": "text"
}
```

### FeedbackLevelsDto

| Field | Type |
|---|---|
| site | `bool` |
| space | `bool` |
| page | `bool` |
| effective | `bool` |

```json
{
  "site": false,
  "space": false,
  "page": false,
  "effective": false
}
```

### FeedbackSettingsDto

| Field | Type |
|---|---|
| commentsEnabled | `bool` |
| ratingsEnabled | `bool` |

```json
{
  "commentsEnabled": false,
  "ratingsEnabled": false
}
```

### FileStoreField

| Field | Type |
|---|---|
| key | `string` |
| label | `string` |
| type | `string` |

```json
{
  "key": "text",
  "label": "text",
  "type": "text"
}
```

### FormatDto

| Field | Type |
|---|---|
| contentJson | `string` |
| diff | `IReadOnlyList<DiffLine>` ([DiffLine](#diffline)) |
| removed | `IReadOnlyList<FormatRemovedDto>?` ([FormatRemovedDto](#formatremoveddto)) |
| roles | `IReadOnlyList<FormatRoleDto>?` ([FormatRoleDto](#formatroledto)) |

```json
{
  "contentJson": "text",
  "diff": [
    {
      "op": "Same",
      "text": "text"
    }
  ],
  "removed": [
    {
      "units": [
        0
      ],
      "text": "text",
      "sameAs": "text"
    }
  ],
  "roles": [
    {
      "n": 0,
      "role": "text"
    }
  ]
}
```

### FormatRemovedDto

| Field | Type |
|---|---|
| units | `IReadOnlyList<int>` |
| text | `string` |
| sameAs | `string` |

```json
{
  "units": [
    0
  ],
  "text": "text",
  "sameAs": "text"
}
```

### FormatRequest

| Field | Type |
|---|---|
| contentJson | `string?` |
| roles | `IReadOnlyList<FormatRoleDto>?` ([FormatRoleDto](#formatroledto)) |
| keep | `IReadOnlyList<int>?` |

```json
{
  "contentJson": "text",
  "roles": [
    {
      "n": 0,
      "role": "text"
    }
  ],
  "keep": [
    0
  ]
}
```

### FormatRoleDto

| Field | Type |
|---|---|
| n | `int` |
| role | `string` |

```json
{
  "n": 0,
  "role": "text"
}
```

### GrantRoleRequest

| Field | Type |
|---|---|
| principalType | `PrincipalType` ([PrincipalType](#principaltype)) |
| principalId | `string` |
| role | `SpaceRole?` ([SpaceRole](#spacerole)) |
| roleId | `Guid?` |

```json
{
  "principalType": "User",
  "principalId": "text",
  "role": "Reader",
  "roleId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### GroupDto

| Field | Type |
|---|---|
| id | `Guid` |
| key | `string` |
| name | `string` |
| isLocal | `bool` |
| members | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "key": "text",
  "name": "text",
  "isLocal": false,
  "members": 0
}
```

### GroupMemberDto

| Field | Type |
|---|---|
| userId | `Guid` |
| userName | `string` |
| displayName | `string` |
| source | `string` |

```json
{
  "userId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "userName": "text",
  "displayName": "text",
  "source": "text"
}
```

### GroupNameRequest

| Field | Type |
|---|---|
| name | `string` |

```json
{
  "name": "text"
}
```

### HomeDto

| Field | Type |
|---|---|
| announcements | `IReadOnlyList<AnnouncementDto>` ([AnnouncementDto](#announcementdto)) |
| recent | `IReadOnlyList<RecentPageDto>` ([RecentPageDto](#recentpagedto)) |

```json
{
  "announcements": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "title": "text",
      "body": "text",
      "by": "text",
      "createdAt": "2026-11-14T10:00:00\u002B00:00",
      "expiresAt": "2026-11-14T10:00:00\u002B00:00"
    }
  ],
  "recent": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "title": "text",
      "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "spaceName": "text",
      "updatedAt": "2026-11-14T10:00:00\u002B00:00",
      "updatedBy": "text"
    }
  ]
}
```

### ImageDto

| Field | Type |
|---|---|
| id | `Guid` |
| url | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "url": "text"
}
```

### ImportBatchDto

| Field | Type |
|---|---|
| id | `Guid` |
| createdAt | `DateTimeOffset` |
| files | `int` |
| started | `bool` |
| grouped | `bool` |
| error | `string?` |
| queued | `int` |
| working | `int` |
| ready | `int` |
| failed | `int` |
| blocked | `int` |
| done | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "files": 0,
  "started": false,
  "grouped": false,
  "error": "text",
  "queued": 0,
  "working": 0,
  "ready": 0,
  "failed": 0,
  "blocked": 0,
  "done": 0
}
```

### ImportBatchRequest

| Field | Type |
|---|---|
| spaceId | `Guid?` |
| parentId | `Guid?` |

```json
{
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### ImportFileDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| path | `string` |
| size | `long` |
| kind | `string` |
| note | `string?` |
| embedded | `bool` |
| sha256 | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "path": "text",
  "size": 0,
  "kind": "text",
  "note": "text",
  "embedded": false,
  "sha256": "text"
}
```

### ImportImageRequest

| Field | Type |
|---|---|
| url | `string` |

```json
{
  "url": "text"
}
```

### ImportItemDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |
| status | `string` |
| error | `string?` |
| proposal | `JsonNode?` |
| files | `IReadOnlyList<ImportFileDto>` ([ImportFileDto](#importfiledto)) |
| sources | `IReadOnlyDictionary<Guid, string>` |
| pageId | `Guid?` |
| spaces | `IReadOnlyList<ImportSpaceDto>?` ([ImportSpaceDto](#importspacedto)) |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "status": "text",
  "error": "text",
  "proposal": {},
  "files": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "path": "text",
      "size": 0,
      "kind": "text",
      "note": "text",
      "embedded": false,
      "sha256": "text"
    }
  ],
  "sources": {
    "key": "text"
  },
  "pageId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaces": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "isSensitive": false,
      "reviewMonths": 0
    }
  ]
}
```

### ImportItemRowDto

| Field | Type |
|---|---|
| id | `Guid` |
| batchId | `Guid` |
| title | `string` |
| status | `string` |
| error | `string?` |
| files | `int` |
| createdAt | `DateTimeOffset` |
| pageId | `Guid?` |
| source | `string?` |
| suggestedSpace | `string?` |
| updatedAt | `DateTimeOffset?` |
| draftMs | `long?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "batchId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "status": "text",
  "error": "text",
  "files": 0,
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "pageId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "source": "text",
  "suggestedSpace": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "draftMs": 0
}
```

### ImportListDto

| Field | Type |
|---|---|
| batches | `IReadOnlyList<ImportBatchDto>` ([ImportBatchDto](#importbatchdto)) |
| items | `IReadOnlyList<ImportItemRowDto>` ([ImportItemRowDto](#importitemrowdto)) |
| canImport | `bool` |
| canFormat | `bool` |
| spaces | `IReadOnlyList<ImportSpaceDto>?` ([ImportSpaceDto](#importspacedto)) |
| canAssist | `bool` |

```json
{
  "batches": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "createdAt": "2026-11-14T10:00:00\u002B00:00",
      "files": 0,
      "started": false,
      "grouped": false,
      "error": "text",
      "queued": 0,
      "working": 0,
      "ready": 0,
      "failed": 0,
      "blocked": 0,
      "done": 0
    }
  ],
  "items": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "batchId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "title": "text",
      "status": "text",
      "error": "text",
      "files": 0,
      "createdAt": "2026-11-14T10:00:00\u002B00:00",
      "pageId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "source": "text",
      "suggestedSpace": "text",
      "updatedAt": "2026-11-14T10:00:00\u002B00:00",
      "draftMs": 0
    }
  ],
  "canImport": false,
  "canFormat": false,
  "spaces": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "isSensitive": false,
      "reviewMonths": 0
    }
  ],
  "canAssist": false
}
```

### ImportMergeRequest

| Field | Type |
|---|---|
| itemIds | `IReadOnlyList<Guid>?` |

```json
{
  "itemIds": [
    "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
  ]
}
```

### ImportSpaceDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| isSensitive | `bool` |
| reviewMonths | `int?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "isSensitive": false,
  "reviewMonths": 0
}
```

### InstalledDto

| Field | Type |
|---|---|
| id | `string` |
| version | `string` |
| restart | `bool` |

```json
{
  "id": "text",
  "version": "text",
  "restart": false
}
```

### InvAccessDto

| Field | Type |
|---|---|
| view | `IReadOnlyList<InvRuleDto>` ([InvRuleDto](#invruledto)) |
| edit | `IReadOnlyList<InvRuleDto>` ([InvRuleDto](#invruledto)) |

```json
{
  "view": [
    {
      "kind": "text",
      "id": "text",
      "name": "text"
    }
  ],
  "edit": [
    {
      "kind": "text",
      "id": "text",
      "name": "text"
    }
  ]
}
```

### InvAppDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| vendor | `string?` |
| owner | `OwnerRefDto?` ([OwnerRefDto](#ownerrefdto)) |
| notes | `string?` |
| installs | `IReadOnlyList<InvInstallDto>` ([InvInstallDto](#invinstalldto)) |
| pages | `IReadOnlyList<InvPageDto>` ([InvPageDto](#invpagedto)) |
| canEdit | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "vendor": "text",
  "owner": {
    "type": "text",
    "id": "text",
    "name": "text"
  },
  "notes": "text",
  "installs": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "server": "text",
      "version": "text",
      "changedAt": "2026-11-14T10:00:00\u002B00:00",
      "changedBy": "text",
      "history": [
        null
      ]
    }
  ],
  "pages": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "title": "text",
      "spaceName": "text",
      "servers": [
        "text"
      ]
    }
  ],
  "canEdit": false
}
```

### InvAppRequest

| Field | Type |
|---|---|
| name | `string?` |
| vendor | `string?` |
| notes | `string?` |
| ownerType | `string?` |
| ownerId | `string?` |

```json
{
  "name": "text",
  "vendor": "text",
  "notes": "text",
  "ownerType": "text",
  "ownerId": "text"
}
```

### InvAppRowDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| vendor | `string?` |
| owner | `OwnerRefDto?` ([OwnerRefDto](#ownerrefdto)) |
| installs | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "vendor": "text",
  "owner": {
    "type": "text",
    "id": "text",
    "name": "text"
  },
  "installs": 0
}
```

### InvHistoryDto

| Field | Type |
|---|---|
| at | `DateTimeOffset` |
| by | `string` |
| kind | `string` |
| from | `string?` |
| to | `string?` |

```json
{
  "at": "2026-11-14T10:00:00\u002B00:00",
  "by": "text",
  "kind": "text",
  "from": "text",
  "to": "text"
}
```

### InvImportRequest

| Field | Type |
|---|---|
| csv | `string?` |
| removeMissing | `bool` |
| apply | `bool` |

```json
{
  "csv": "text",
  "removeMissing": false,
  "apply": false
}
```

### InvInstallDto

| Field | Type |
|---|---|
| id | `Guid` |
| server | `string` |
| version | `string` |
| changedAt | `DateTimeOffset` |
| changedBy | `string` |
| history | `IReadOnlyList<InvHistoryDto>` ([InvHistoryDto](#invhistorydto)) |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "server": "text",
  "version": "text",
  "changedAt": "2026-11-14T10:00:00\u002B00:00",
  "changedBy": "text",
  "history": [
    {
      "at": "2026-11-14T10:00:00\u002B00:00",
      "by": "text",
      "kind": "text",
      "from": "text",
      "to": "text"
    }
  ]
}
```

### InvInstallRequest

| Field | Type |
|---|---|
| server | `string?` |
| version | `string?` |

```json
{
  "server": "text",
  "version": "text"
}
```

### InvMeDto

| Field | Type |
|---|---|
| canView | `bool` |
| canEdit | `bool` |

```json
{
  "canView": false,
  "canEdit": false
}
```

### InvPageDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |
| spaceName | `string` |
| servers | `IReadOnlyList<string>` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "spaceName": "text",
  "servers": [
    "text"
  ]
}
```

### InvRuleDto

| Field | Type |
|---|---|
| kind | `string` |
| id | `string?` |
| name | `string?` |

```json
{
  "kind": "text",
  "id": "text",
  "name": "text"
}
```

### InvSyncRequest

| Field | Type |
|---|---|
| source | `string?` |
| rows | `IReadOnlyList<SyncRowDto>?` ([SyncRowDto](#syncrowdto)) |
| removeMissing | `bool` |
| apply | `bool?` |

```json
{
  "source": "text",
  "rows": [
    {
      "application": "text",
      "vendor": "text",
      "server": "text",
      "version": "text"
    }
  ],
  "removeMissing": false,
  "apply": false
}
```

### InviteRequest

| Field | Type |
|---|---|
| userName | `string` |
| displayName | `string` |
| email | `string?` |
| isAdmin | `bool` |

```json
{
  "userName": "text",
  "displayName": "text",
  "email": "text",
  "isAdmin": false
}
```

### InvitedUser

| Field | Type |
|---|---|
| id | `Guid` |
| setPasswordToken | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "setPasswordToken": "text"
}
```

### LogKindDto

| Field | Type |
|---|---|
| kind | `string` |
| label | `string` |
| fields | `IReadOnlyList<LogShipField>` ([LogShipField](#logshipfield)) |

```json
{
  "kind": "text",
  "label": "text",
  "fields": [
    {
      "key": "text",
      "label": "text",
      "type": "text",
      "options": [
        "text"
      ]
    }
  ]
}
```

### LogShipField

| Field | Type |
|---|---|
| key | `string` |
| label | `string` |
| type | `string` |
| options | `IReadOnlyList<string>?` |

```json
{
  "key": "text",
  "label": "text",
  "type": "text",
  "options": [
    "text"
  ]
}
```

### LogTargetDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| kind | `string` |
| settings | `IReadOnlyDictionary<string, string>` |
| secretsSet | `IReadOnlyList<string>` |
| enabled | `bool` |
| lastSequence | `long` |
| lastShippedAt | `DateTimeOffset?` |
| lastError | `string?` |
| lastErrorAt | `DateTimeOffset?` |
| createdAt | `DateTimeOffset` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "kind": "text",
  "settings": {
    "key": "text"
  },
  "secretsSet": [
    "text"
  ],
  "enabled": false,
  "lastSequence": 0,
  "lastShippedAt": "2026-11-14T10:00:00\u002B00:00",
  "lastError": "text",
  "lastErrorAt": "2026-11-14T10:00:00\u002B00:00",
  "createdAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### LogTargetRequest

| Field | Type |
|---|---|
| name | `string?` |
| kind | `string?` |
| settings | `Dictionary<string, string>?` |
| secrets | `Dictionary<string, string>?` |
| enabled | `bool` |

```json
{
  "name": "text",
  "kind": "text",
  "settings": {
    "key": "text"
  },
  "secrets": {
    "key": "text"
  },
  "enabled": false
}
```

### LogsDto

| Field | Type |
|---|---|
| kinds | `IReadOnlyList<LogKindDto>` ([LogKindDto](#logkinddto)) |
| targets | `IReadOnlyList<LogTargetDto>` ([LogTargetDto](#logtargetdto)) |
| head | `long` |

```json
{
  "kinds": [
    {
      "kind": "text",
      "label": "text",
      "fields": [
        null
      ]
    }
  ],
  "targets": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "kind": "text",
      "settings": {
        "key": "text"
      },
      "secretsSet": [
        "text"
      ],
      "enabled": false,
      "lastSequence": 0,
      "lastShippedAt": "2026-11-14T10:00:00\u002B00:00",
      "lastError": "text",
      "lastErrorAt": "2026-11-14T10:00:00\u002B00:00",
      "createdAt": "2026-11-14T10:00:00\u002B00:00"
    }
  ],
  "head": 0
}
```

### MeResponse

| Field | Type |
|---|---|
| id | `Guid` |
| userName | `string` |
| displayName | `string` |
| isAdmin | `bool` |
| isBreakGlass | `bool` |
| canAnnounce | `bool` |
| permissions | `IReadOnlyList<string>?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "userName": "text",
  "displayName": "text",
  "isAdmin": false,
  "isBreakGlass": false,
  "canAnnounce": false,
  "permissions": [
    "text"
  ]
}
```

### MoveRequest

| Field | Type |
|---|---|
| spaceId | `Guid?` |
| parentId | `Guid?` |

```json
{
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### MyCodes

| Field | Type |
|---|---|
| on | `bool` |
| required | `bool` |
| allowed | `bool` |

```json
{
  "on": false,
  "required": false,
  "allowed": false
}
```

### NotificationCountDto

| Field | Type |
|---|---|
| unread | `int` |

```json
{
  "unread": 0
}
```

### NotificationDto

| Field | Type |
|---|---|
| id | `Guid` |
| userId | `Guid` |
| kind | `string` |
| title | `string` |
| body | `string?` |
| link | `string?` |
| createdAt | `DateTimeOffset` |
| readAt | `DateTimeOffset?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "userId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "text",
  "title": "text",
  "body": "text",
  "link": "text",
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "readAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### NotifyPref

| Field | Type |
|---|---|
| inApp | `bool` |
| email | `bool` |

```json
{
  "inApp": false,
  "email": false
}
```

### OAuthAuthorizeRequestDto

| Field | Type |
|---|---|
| id | `Guid` |
| clientName | `string` |
| scopes | `IReadOnlyList<string>` |
| alreadyGranted | `IReadOnlyList<string>` |
| expiresAt | `DateTimeOffset` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "clientName": "text",
  "scopes": [
    "text"
  ],
  "alreadyGranted": [
    "text"
  ],
  "expiresAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### OAuthAuthorizeResult

| Field | Type |
|---|---|
| location | `string?` |
| errorPage | `string?` |

```json
{
  "location": "text",
  "errorPage": "text"
}
```

### OAuthClientCreatedDto

| Field | Type |
|---|---|
| clientSecret | `string?` |
| client | `OAuthClientDto` ([OAuthClientDto](#oauthclientdto)) |

```json
{
  "clientSecret": "text",
  "client": {
    "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "clientId": "text",
    "name": "text",
    "type": "text",
    "redirectUris": [
      "text"
    ],
    "scopes": [
      "text"
    ],
    "requirePkce": false,
    "enabled": false,
    "activeGrants": 0,
    "createdBy": "text",
    "createdAt": "2026-11-14T10:00:00\u002B00:00",
    "secretRotatedAt": "2026-11-14T10:00:00\u002B00:00"
  }
}
```

### OAuthClientDto

| Field | Type |
|---|---|
| id | `Guid` |
| clientId | `string` |
| name | `string` |
| type | `string` |
| redirectUris | `IReadOnlyList<string>` |
| scopes | `IReadOnlyList<string>` |
| requirePkce | `bool` |
| enabled | `bool` |
| activeGrants | `int` |
| createdBy | `string` |
| createdAt | `DateTimeOffset` |
| secretRotatedAt | `DateTimeOffset?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "clientId": "text",
  "name": "text",
  "type": "text",
  "redirectUris": [
    "text"
  ],
  "scopes": [
    "text"
  ],
  "requirePkce": false,
  "enabled": false,
  "activeGrants": 0,
  "createdBy": "text",
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "secretRotatedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### OAuthClientRequest

| Field | Type |
|---|---|
| name | `string?` |
| type | `string?` |
| redirectUris | `IReadOnlyList<string>?` |
| scopes | `IReadOnlyList<string>?` |
| requirePkce | `bool?` |
| enabled | `bool?` |

```json
{
  "name": "text",
  "type": "text",
  "redirectUris": [
    "text"
  ],
  "scopes": [
    "text"
  ],
  "requirePkce": false,
  "enabled": false
}
```

### OAuthDecisionDto

| Field | Type |
|---|---|
| redirectTo | `string` |

```json
{
  "redirectTo": "text"
}
```

### OAuthDecisionRequest

| Field | Type |
|---|---|
| allow | `bool` |

```json
{
  "allow": false
}
```

### OAuthEndpointResult

| Field | Type |
|---|---|
| status | `int` |
| body | `JsonObject?` |
| basicChallenge | `bool` |
| retryAfter | `int?` |

```json
{
  "status": 0,
  "body": {
    "key": {}
  },
  "basicChallenge": false,
  "retryAfter": 0
}
```

### OAuthGrantDto

| Field | Type |
|---|---|
| id | `Guid` |
| clientName | `string` |
| scopes | `IReadOnlyList<string>` |
| grantedAt | `DateTimeOffset` |
| lastUsedAt | `DateTimeOffset?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "clientName": "text",
  "scopes": [
    "text"
  ],
  "grantedAt": "2026-11-14T10:00:00\u002B00:00",
  "lastUsedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### OwnerRefDto

| Field | Type |
|---|---|
| type | `string` |
| id | `string` |
| name | `string` |

```json
{
  "type": "text",
  "id": "text",
  "name": "text"
}
```

### PageDetailsDto

| Field | Type |
|---|---|
| appliesTo | `IReadOnlyList<AppliesToDto>` ([AppliesToDto](#appliestodto)) |
| owner | `OwnerRefDto?` ([OwnerRefDto](#ownerrefdto)) |
| reviewMonths | `int?` |

```json
{
  "appliesTo": [
    {
      "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "servers": [
        "text"
      ]
    }
  ],
  "owner": {
    "type": "text",
    "id": "text",
    "name": "text"
  },
  "reviewMonths": 0
}
```

### PageDetailsRequest

| Field | Type |
|---|---|
| appliesTo | `IReadOnlyList<AppliesToRequest>?` ([AppliesToRequest](#appliestorequest)) |
| ownerType | `string?` |
| ownerId | `string?` |
| reviewMonths | `int?` |

```json
{
  "appliesTo": [
    {
      "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "servers": [
        "text"
      ]
    }
  ],
  "ownerType": "text",
  "ownerId": "text",
  "reviewMonths": 0
}
```

### PageDto

| Field | Type |
|---|---|
| id | `Guid` |
| spaceId | `Guid` |
| parentId | `Guid?` |
| kind | `string` |
| title | `string` |
| contentJson | `string` |
| version | `string` |
| updatedAt | `DateTimeOffset` |
| updatedBy | `string` |
| path | `IReadOnlyList<CrumbDto>` ([CrumbDto](#crumbdto)) |
| history | `IReadOnlyList<VersionDto>` ([VersionDto](#versiondto)) |
| canEdit | `bool` |
| tags | `IReadOnlyList<string>` |
| canDelete | `bool` |
| draft | `DraftInfoDto?` ([DraftInfoDto](#draftinfodto)) |
| attachments | `IReadOnlyList<AttachmentDto>` ([AttachmentDto](#attachmentdto)) |
| maxAttachmentBytes | `long` |
| details | `PageDetailsDto?` ([PageDetailsDto](#pagedetailsdto)) |
| aiFlag | `string?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "text",
  "title": "text",
  "contentJson": "text",
  "version": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "updatedBy": "text",
  "path": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "title": "text"
    }
  ],
  "history": [
    {
      "version": "text",
      "at": "2026-11-14T10:00:00\u002B00:00",
      "author": "text",
      "note": "text"
    }
  ],
  "canEdit": false,
  "tags": [
    "text"
  ],
  "canDelete": false,
  "draft": {
    "mine": false,
    "by": "text",
    "updatedAt": "2026-11-14T10:00:00\u002B00:00"
  },
  "attachments": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "size": 0,
      "url": "text",
      "at": "2026-11-14T10:00:00\u002B00:00"
    }
  ],
  "maxAttachmentBytes": 0,
  "details": {
    "appliesTo": [
      {
        "applicationId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
        "name": "text",
        "servers": null
      }
    ],
    "owner": {
      "type": "text",
      "id": "text",
      "name": "text"
    },
    "reviewMonths": 0
  },
  "aiFlag": "text"
}
```

### PageFeedbackDto

| Field | Type |
|---|---|
| comments | `FeedbackLevelsDto` ([FeedbackLevelsDto](#feedbacklevelsdto)) |
| ratings | `FeedbackLevelsDto` ([FeedbackLevelsDto](#feedbacklevelsdto)) |

```json
{
  "comments": {
    "site": false,
    "space": false,
    "page": false,
    "effective": false
  },
  "ratings": {
    "site": false,
    "space": false,
    "page": false,
    "effective": false
  }
}
```

### PageFeedbackRequest

| Field | Type |
|---|---|
| commentsEnabled | `bool?` |
| ratingsEnabled | `bool?` |

```json
{
  "commentsEnabled": false,
  "ratingsEnabled": false
}
```

### PageKind

Enum: `Folder`, `Page`.

### PageMarkdownDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |
| version | `string` |
| updatedAt | `DateTimeOffset` |
| markdown | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "version": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "markdown": "text"
}
```

### PluginCheckDto

| Field | Type |
|---|---|
| name | `string` |
| status | `string` |
| text | `string?` |

```json
{
  "name": "text",
  "status": "text",
  "text": "text"
}
```

### PluginHealthDto

| Field | Type |
|---|---|
| plugins | `IReadOnlyList<PluginHealthEntry>` ([PluginHealthEntry](#pluginhealthentry)) |

```json
{
  "plugins": [
    {
      "id": "text",
      "checks": [
        null
      ],
      "jobs": [
        null
      ]
    }
  ]
}
```

### PluginHealthEntry

| Field | Type |
|---|---|
| id | `string` |
| checks | `IReadOnlyList<PluginCheckDto>` ([PluginCheckDto](#plugincheckdto)) |
| jobs | `IReadOnlyList<PluginJobDto>` ([PluginJobDto](#pluginjobdto)) |

```json
{
  "id": "text",
  "checks": [
    {
      "name": "text",
      "status": "text",
      "text": "text"
    }
  ],
  "jobs": [
    {
      "name": "text",
      "everyMinutes": 0,
      "lastRunAt": "2026-11-14T10:00:00\u002B00:00",
      "nextRunAt": "2026-11-14T10:00:00\u002B00:00",
      "lastError": "text"
    }
  ]
}
```

### PluginInfoDto

| Field | Type |
|---|---|
| id | `string` |
| name | `string` |
| version | `string` |
| publisher | `string` |
| description | `string?` |
| origin | `PluginOrigin` ([PluginOrigin](#pluginorigin)) |
| state | `PluginState` ([PluginState](#pluginstate)) |
| reason | `string?` |
| locked | `bool` |
| loaded | `bool` |
| source | `string` |
| requires | `IReadOnlyList<string>` |
| requiredBy | `IReadOnlyList<string>` |
| extensionPoints | `IReadOnlyList<string>` |
| outboundHosts | `IReadOnlyList<string>` |
| permissions | `IReadOnlyList<PluginPermission>` ([PluginPermission](#pluginpermission)) |
| enforcing | `bool` |
| settings | `IReadOnlyList<PluginSetting>` ([PluginSetting](#pluginsetting)) |
| replaces | `IReadOnlyList<string>` |
| removed | `bool` |

```json
{
  "id": "text",
  "name": "text",
  "version": "text",
  "publisher": "text",
  "description": "text",
  "origin": "BuiltIn",
  "state": "Enabled",
  "reason": "text",
  "locked": false,
  "loaded": false,
  "source": "text",
  "requires": [
    "text"
  ],
  "requiredBy": [
    "text"
  ],
  "extensionPoints": [
    "text"
  ],
  "outboundHosts": [
    "text"
  ],
  "permissions": [
    {
      "key": "text",
      "label": "text",
      "scope": "text",
      "defaultRole": "text"
    }
  ],
  "enforcing": false,
  "settings": [
    {
      "key": "text",
      "label": "text",
      "secret": false,
      "required": false,
      "type": "text",
      "options": [
        "text"
      ],
      "restart": false
    }
  ],
  "replaces": [
    "text"
  ],
  "removed": false
}
```

### PluginJobDto

| Field | Type |
|---|---|
| name | `string` |
| everyMinutes | `int` |
| lastRunAt | `DateTimeOffset?` |
| nextRunAt | `DateTimeOffset?` |
| lastError | `string?` |

```json
{
  "name": "text",
  "everyMinutes": 0,
  "lastRunAt": "2026-11-14T10:00:00\u002B00:00",
  "nextRunAt": "2026-11-14T10:00:00\u002B00:00",
  "lastError": "text"
}
```

### PluginListDto

| Field | Type |
|---|---|
| plugins | `IReadOnlyList<PluginInfoDto>` ([PluginInfoDto](#plugininfodto)) |
| safeMode | `bool` |

```json
{
  "plugins": [
    {
      "id": "text",
      "name": "text",
      "version": "text",
      "publisher": "text",
      "description": "text",
      "origin": "BuiltIn",
      "state": "Enabled",
      "reason": "text",
      "locked": false,
      "loaded": false,
      "source": "text",
      "requires": [
        "text"
      ],
      "requiredBy": [
        "text"
      ],
      "extensionPoints": [
        "text"
      ],
      "outboundHosts": [
        "text"
      ],
      "permissions": [
        null
      ],
      "enforcing": false,
      "settings": [
        null
      ],
      "replaces": [
        "text"
      ],
      "removed": false
    }
  ],
  "safeMode": false
}
```

### PluginOrigin

Enum: `BuiltIn`, `Uploaded`.

### PluginPermission

| Field | Type |
|---|---|
| key | `string` |
| label | `string` |
| scope | `string` |
| defaultRole | `string` |

```json
{
  "key": "text",
  "label": "text",
  "scope": "text",
  "defaultRole": "text"
}
```

### PluginPermissionDto

| Field | Type |
|---|---|
| key | `string` |
| label | `string` |
| scope | `string` |
| defaultRole | `string` |
| role | `string?` |

```json
{
  "key": "text",
  "label": "text",
  "scope": "text",
  "defaultRole": "text",
  "role": "text"
}
```

### PluginPermissionsDto

| Field | Type |
|---|---|
| permissions | `IReadOnlyList<PluginPermissionDto>` ([PluginPermissionDto](#pluginpermissiondto)) |

```json
{
  "permissions": [
    {
      "key": "text",
      "label": "text",
      "scope": "text",
      "defaultRole": "text",
      "role": "text"
    }
  ]
}
```

### PluginPermissionsRequest

| Field | Type |
|---|---|
| roles | `Dictionary<string, string>?` |

```json
{
  "roles": {
    "key": "text"
  }
}
```

### PluginSetting

| Field | Type |
|---|---|
| key | `string` |
| label | `string` |
| secret | `bool` |
| required | `bool` |
| type | `string` |
| options | `IReadOnlyList<string>?` |
| restart | `bool` |

```json
{
  "key": "text",
  "label": "text",
  "secret": false,
  "required": false,
  "type": "text",
  "options": [
    "text"
  ],
  "restart": false
}
```

### PluginSettingsDto

| Field | Type |
|---|---|
| values | `IReadOnlyDictionary<string, string>` |
| secretsSet | `IReadOnlyList<string>` |

```json
{
  "values": {
    "key": "text"
  },
  "secretsSet": [
    "text"
  ]
}
```

### PluginSettingsRequest

| Field | Type |
|---|---|
| values | `Dictionary<string, string>?` |

```json
{
  "values": {
    "key": "text"
  }
}
```

### PluginSettingsSaved

| Field | Type |
|---|---|
| restart | `bool` |

```json
{
  "restart": false
}
```

### PluginState

Enum: `Enabled`, `Disabled`, `DisabledByCore`.

### PluginStateRequest

| Field | Type |
|---|---|
| enabled | `bool` |

```json
{
  "enabled": false
}
```

### PostAnnouncementRequest

| Field | Type |
|---|---|
| title | `string` |
| body | `string?` |
| expiresAt | `DateTimeOffset?` |

```json
{
  "title": "text",
  "body": "text",
  "expiresAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### PrincipalDto

| Field | Type |
|---|---|
| type | `string` |
| id | `string` |
| name | `string` |
| detail | `string` |

```json
{
  "type": "text",
  "id": "text",
  "name": "text",
  "detail": "text"
}
```

### PrincipalType

Enum: `User`, `Group`.

### ReadOAuthAuthorizeRequest

| Field | Type |
|---|---|
| id | `Guid` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### RecentPageDto

| Field | Type |
|---|---|
| id | `Guid` |
| title | `string` |
| spaceId | `Guid` |
| spaceName | `string` |
| updatedAt | `DateTimeOffset` |
| updatedBy | `string` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "title": "text",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceName": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "updatedBy": "text"
}
```

### ReindexResult

| Field | Type |
|---|---|
| pages | `int` |

```json
{
  "pages": 0
}
```

### RenameProductRequest

| Field | Type |
|---|---|
| productName | `string` |

```json
{
  "productName": "text"
}
```

### RestoredPageDto

| Field | Type |
|---|---|
| id | `Guid` |
| spaceId | `Guid` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### RoleAssignmentDto

| Field | Type |
|---|---|
| id | `Guid` |
| principalType | `PrincipalType` ([PrincipalType](#principaltype)) |
| principalId | `string` |
| role | `SpaceRole` ([SpaceRole](#spacerole)) |
| name | `string?` |
| roleId | `Guid` |
| roleName | `string?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "principalType": "User",
  "principalId": "text",
  "role": "Reader",
  "name": "text",
  "roleId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "roleName": "text"
}
```

### RoleDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| permissions | `IReadOnlyList<string>` |
| builtIn | `string?` |
| inUse | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "permissions": [
    "text"
  ],
  "builtIn": "text",
  "inUse": 0
}
```

### RoleRequest

| Field | Type |
|---|---|
| name | `string?` |
| permissions | `IReadOnlyList<string>?` |

```json
{
  "name": "text",
  "permissions": [
    "text"
  ]
}
```

### SaveDraftRequest

| Field | Type |
|---|---|
| title | `string?` |
| contentJson | `string` |
| baseVersion | `string` |
| tags | `string[]?` |

```json
{
  "title": "text",
  "contentJson": "text",
  "baseVersion": "text",
  "tags": [
    "text"
  ]
}
```

### SavePageRequest

| Field | Type |
|---|---|
| title | `string` |
| contentJson | `string` |
| baseVersion | `string` |
| major | `bool` |
| note | `string?` |
| tags | `string[]?` |

```json
{
  "title": "text",
  "contentJson": "text",
  "baseVersion": "text",
  "major": false,
  "note": "text",
  "tags": [
    "text"
  ]
}
```

### SearchHitDto

| Field | Type |
|---|---|
| id | `Guid` |
| spaceId | `Guid` |
| spaceName | `string` |
| kind | `string` |
| title | `string` |
| path | `IReadOnlyList<string>` |
| url | `string` |
| version | `string` |
| updatedAt | `DateTimeOffset` |
| tags | `IReadOnlyList<string>` |
| snippet | `string` |
| score | `Double` |
| matched | `IReadOnlyList<string>` |
| text | `string?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "spaceName": "text",
  "kind": "text",
  "title": "text",
  "path": [
    "text"
  ],
  "url": "text",
  "version": "text",
  "updatedAt": "2026-11-14T10:00:00\u002B00:00",
  "tags": [
    "text"
  ],
  "snippet": "text",
  "score": 0.5,
  "matched": [
    "text"
  ],
  "text": "text"
}
```

### SearchResponse

| Field | Type |
|---|---|
| query | `string` |
| total | `int` |
| limit | `int` |
| offset | `int` |
| hits | `IReadOnlyList<SearchHitDto>` ([SearchHitDto](#searchhitdto)) |

```json
{
  "query": "text",
  "total": 0,
  "limit": 0,
  "offset": 0,
  "hits": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "spaceName": "text",
      "kind": "text",
      "title": "text",
      "path": [
        "text"
      ],
      "url": "text",
      "version": "text",
      "updatedAt": "2026-11-14T10:00:00\u002B00:00",
      "tags": [
        "text"
      ],
      "snippet": "text",
      "score": 0.5,
      "matched": [
        "text"
      ],
      "text": "text"
    }
  ]
}
```

### SetPasswordRequest

| Field | Type |
|---|---|
| userId | `Guid` |
| token | `string?` |
| password | `string?` |

```json
{
  "userId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "token": "text",
  "password": "text"
}
```

### SetupRequest

| Field | Type |
|---|---|
| userName | `string` |
| displayName | `string` |
| password | `string` |
| code | `string?` |

```json
{
  "userName": "text",
  "displayName": "text",
  "password": "text",
  "code": "text"
}
```

### SetupStatus

| Field | Type |
|---|---|
| required | `bool` |
| codeRequired | `bool` |

```json
{
  "required": false,
  "codeRequired": false
}
```

### SignInRequest

| Field | Type |
|---|---|
| userName | `string` |
| password | `string` |

```json
{
  "userName": "text",
  "password": "text"
}
```

### SpaceDto

| Field | Type |
|---|---|
| id | `Guid` |
| key | `string` |
| name | `string` |
| description | `string?` |
| isSensitive | `bool` |
| visibility | `string` |
| myRole | `string?` |
| commentsEnabled | `bool` |
| agingDays | `int` |
| agingAudience | `string` |
| recycleDays | `int?` |
| createdAt | `DateTimeOffset` |
| reviewMonths | `int?` |
| ratingsEnabled | `bool` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "key": "text",
  "name": "text",
  "description": "text",
  "isSensitive": false,
  "visibility": "text",
  "myRole": "text",
  "commentsEnabled": false,
  "agingDays": 0,
  "agingAudience": "text",
  "recycleDays": 0,
  "createdAt": "2026-11-14T10:00:00\u002B00:00",
  "reviewMonths": 0,
  "ratingsEnabled": false
}
```

### SpaceRole

Enum: `Reader`, `Contributor`, `Supervisor`, `Owner`.

### SpaceVisibility

Enum: `Private`, `SignedIn`, `Anyone`.

### StorageDto

| Field | Type |
|---|---|
| kinds | `IReadOnlyList<StorageKindDto>` ([StorageKindDto](#storagekinddto)) |
| places | `IReadOnlyList<StoragePlaceDto>` ([StoragePlaceDto](#storageplacedto)) |
| move | `StorageMoveDto?` ([StorageMoveDto](#storagemovedto)) |

```json
{
  "kinds": [
    {
      "kind": "text",
      "label": "text",
      "fields": [
        null
      ]
    }
  ],
  "places": [
    {
      "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
      "name": "text",
      "kind": "text",
      "settings": {
        "key": "text"
      },
      "secretsSet": [
        "text"
      ],
      "role": "Active",
      "createdAt": "2026-11-14T10:00:00\u002B00:00"
    }
  ],
  "move": {
    "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "fromId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "toId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
    "state": "Copying",
    "total": 0,
    "copied": 0,
    "checked": 0,
    "failed": 0,
    "lastError": "text",
    "preview": false,
    "startedAt": "2026-11-14T10:00:00\u002B00:00",
    "finishedAt": "2026-11-14T10:00:00\u002B00:00"
  }
}
```

### StorageKindDto

| Field | Type |
|---|---|
| kind | `string` |
| label | `string` |
| fields | `IReadOnlyList<FileStoreField>` ([FileStoreField](#filestorefield)) |

```json
{
  "kind": "text",
  "label": "text",
  "fields": [
    {
      "key": "text",
      "label": "text",
      "type": "text"
    }
  ]
}
```

### StorageMoveDto

| Field | Type |
|---|---|
| id | `Guid` |
| fromId | `Guid` |
| toId | `Guid` |
| state | `StorageMoveState` ([StorageMoveState](#storagemovestate)) |
| total | `int` |
| copied | `int` |
| checked | `int` |
| failed | `int` |
| lastError | `string?` |
| preview | `bool` |
| startedAt | `DateTimeOffset` |
| finishedAt | `DateTimeOffset?` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "fromId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "toId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "state": "Copying",
  "total": 0,
  "copied": 0,
  "checked": 0,
  "failed": 0,
  "lastError": "text",
  "preview": false,
  "startedAt": "2026-11-14T10:00:00\u002B00:00",
  "finishedAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### StorageMoveRequest

| Field | Type |
|---|---|
| toId | `Guid` |

```json
{
  "toId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301"
}
```

### StorageMoveState

Enum: `Copying`, `Copied`, `Switched`, `Cancelled`.

### StoragePlaceDto

| Field | Type |
|---|---|
| id | `Guid` |
| name | `string` |
| kind | `string` |
| settings | `IReadOnlyDictionary<string, string>` |
| secretsSet | `IReadOnlyList<string>` |
| role | `StorageRole` ([StorageRole](#storagerole)) |
| createdAt | `DateTimeOffset` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "name": "text",
  "kind": "text",
  "settings": {
    "key": "text"
  },
  "secretsSet": [
    "text"
  ],
  "role": "Active",
  "createdAt": "2026-11-14T10:00:00\u002B00:00"
}
```

### StoragePlaceRequest

| Field | Type |
|---|---|
| name | `string?` |
| kind | `string?` |
| settings | `Dictionary<string, string>?` |
| secrets | `Dictionary<string, string>?` |

```json
{
  "name": "text",
  "kind": "text",
  "settings": {
    "key": "text"
  },
  "secrets": {
    "key": "text"
  }
}
```

### StoragePreviewRequest

| Field | Type |
|---|---|
| on | `bool` |

```json
{
  "on": false
}
```

### StorageRole

Enum: `Active`, `Target`, `Old`.

### StorageTestDto

| Field | Type |
|---|---|
| ok | `bool` |
| error | `string?` |

```json
{
  "ok": false,
  "error": "text"
}
```

### SyncChangeDto

| Field | Type |
|---|---|
| application | `string` |
| server | `string` |
| kind | `string` |
| from | `string?` |
| to | `string?` |

```json
{
  "application": "text",
  "server": "text",
  "kind": "text",
  "from": "text",
  "to": "text"
}
```

### SyncResultDto

| Field | Type |
|---|---|
| added | `int` |
| changed | `int` |
| unchanged | `int` |
| removed | `int` |
| newApplications | `int` |
| changes | `IReadOnlyList<SyncChangeDto>` ([SyncChangeDto](#syncchangedto)) |

```json
{
  "added": 0,
  "changed": 0,
  "unchanged": 0,
  "removed": 0,
  "newApplications": 0,
  "changes": [
    {
      "application": "text",
      "server": "text",
      "kind": "text",
      "from": "text",
      "to": "text"
    }
  ]
}
```

### SyncRowDto

| Field | Type |
|---|---|
| application | `string` |
| vendor | `string?` |
| server | `string` |
| version | `string` |

```json
{
  "application": "text",
  "vendor": "text",
  "server": "text",
  "version": "text"
}
```

### SynonymsDto

| Field | Type |
|---|---|
| text | `string` |

```json
{
  "text": "text"
}
```

### ThemeDto

| Field | Type |
|---|---|
| day | `IReadOnlyDictionary<string, string>` |
| night | `IReadOnlyDictionary<string, string>` |

```json
{
  "day": {
    "key": "text"
  },
  "night": {
    "key": "text"
  }
}
```

### TokenAccess

Enum: `Read`, `Write`.

### TokenKind

Enum: `Personal`, `Service`.

### TokenScope

| Field | Type |
|---|---|
| spaceId | `Guid?` |
| access | `TokenAccess` ([TokenAccess](#tokenaccess)) |

```json
{
  "spaceId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "access": "Read"
}
```

### TokenSettingsDto

| Field | Type |
|---|---|
| maxDays | `int` |

```json
{
  "maxDays": 0
}
```

### TreeNodeDto

| Field | Type |
|---|---|
| id | `Guid` |
| parentId | `Guid?` |
| kind | `string` |
| title | `string` |
| sort | `int` |

```json
{
  "id": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "parentId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
  "kind": "text",
  "title": "text",
  "sort": 0
}
```

### UiErrorRequest

| Field | Type |
|---|---|
| plugin | `string?` |
| where | `string?` |
| message | `string?` |

```json
{
  "plugin": "text",
  "where": "text",
  "message": "text"
}
```

### UiPluginDto

| Field | Type |
|---|---|
| id | `string` |
| version | `string` |
| script | `string` |

```json
{
  "id": "text",
  "version": "text",
  "script": "text"
}
```

### UiPluginsDto

| Field | Type |
|---|---|
| uiApiVersion | `string` |
| plugins | `IReadOnlyList<UiPluginDto>` ([UiPluginDto](#uiplugindto)) |

```json
{
  "uiApiVersion": "text",
  "plugins": [
    {
      "id": "text",
      "version": "text",
      "script": "text"
    }
  ]
}
```

### UpdateSpaceRequest

| Field | Type |
|---|---|
| name | `string?` |
| description | `string?` |
| isSensitive | `bool?` |
| visibility | `SpaceVisibility?` ([SpaceVisibility](#spacevisibility)) |
| commentsEnabled | `bool?` |
| agingDays | `int?` |
| agingAudience | `AgingAudience?` ([AgingAudience](#agingaudience)) |
| recycleDays | `int?` |
| reviewMonths | `int?` |
| ratingsEnabled | `bool?` |

```json
{
  "name": "text",
  "description": "text",
  "isSensitive": false,
  "visibility": "Private",
  "commentsEnabled": false,
  "agingDays": 0,
  "agingAudience": "Everyone",
  "recycleDays": 0,
  "reviewMonths": 0,
  "ratingsEnabled": false
}
```

### UploadSettingsDto

| Field | Type |
|---|---|
| attachmentMb | `int` |
| serverLimitBytes | `long?` |

```json
{
  "attachmentMb": 0,
  "serverLimitBytes": 0
}
```

### UploadSettingsRequest

| Field | Type |
|---|---|
| attachmentMb | `int` |

```json
{
  "attachmentMb": 0
}
```

### UserPermissionRequest

| Field | Type |
|---|---|
| permission | `string` |
| granted | `bool` |

```json
{
  "permission": "text",
  "granted": false
}
```

### UserSettings

| Field | Type |
|---|---|
| theme | `string` |
| width | `string` |
| startPage | `string` |
| codeLanguage | `string` |
| notify | `Dictionary<string, NotifyPref>` ([NotifyPref](#notifypref)) |

```json
{
  "theme": "text",
  "width": "text",
  "startPage": "text",
  "codeLanguage": "text",
  "notify": {
    "key": {
      "inApp": false,
      "email": false
    }
  }
}
```

### VersionContentDto

| Field | Type |
|---|---|
| version | `string` |
| title | `string` |
| contentJson | `string` |
| at | `DateTimeOffset` |
| author | `string` |

```json
{
  "version": "text",
  "title": "text",
  "contentJson": "text",
  "at": "2026-11-14T10:00:00\u002B00:00",
  "author": "text"
}
```

### VersionDto

| Field | Type |
|---|---|
| version | `string` |
| at | `DateTimeOffset` |
| author | `string` |
| note | `string?` |

```json
{
  "version": "text",
  "at": "2026-11-14T10:00:00\u002B00:00",
  "author": "text",
  "note": "text"
}
```
