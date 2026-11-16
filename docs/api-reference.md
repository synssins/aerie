# API reference

Written by the test `ApiReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.

Every route sends one action; [the action reference](plugins/action-reference.md) lists each action's fields and result.
The answer names the action in the `X-Kb-Action` header. A route needs sign-in unless it says otherwise.
API token: the scope a token needs to call the route. "No" means the route answers 403 to every token. See [the agent API](agent-api.md).
Body: the JSON the route reads; Answer: the JSON it answers (`Unit`: no body, status 204). Each type links to its fields and an example in [the action reference](plugins/action-reference.md#types).

A refusal answers a problem body (RFC 9457), with the status of the answer:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Not a plugin package.",
  "errors": { "title": ["Required."] },
  "traceId": "00-..."
}
```

`detail` is the message for a person. `errors` comes with a 400 about a field (the field's JSON name, its messages). 401: not signed in or a bad token. 403: not allowed. 404: not found, or not visible to the caller. 409: a conflict (a version changed, a name taken). 422: refused by a rule (a plugin's hook, the AI tripwire). 423: locked by a disabled enforcing plugin. 429: too many calls.

| Method | Route | Action | Body | Answer | Changes data | Sign-in | API token |
|---|---|---|---|---|---|---|---|
| POST | `/api/v1/access-requests/{id:guid}/decision` | `access.decide` | `DecideBody` ([DecideBody](plugins/action-reference.md#decidebody)) | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/ai/` | `ai.settings.read` |  | `AiSettingsDto` ([AiSettingsDto](plugins/action-reference.md#aisettingsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/ai/` | `ai.settings.save` | `AiSettingsRequest` ([AiSettingsRequest](plugins/action-reference.md#aisettingsrequest)) | `AiSettingsDto` ([AiSettingsDto](plugins/action-reference.md#aisettingsdto)) | Yes | Needed | No |
| POST | `/api/v1/admin/ai/models` | `ai.models` | `AiModelsRequest` ([AiModelsRequest](plugins/action-reference.md#aimodelsrequest)) | `string[]` | Yes | Needed | No |
| POST | `/api/v1/admin/ai/test` | `ai.test` |  | `AiTestDto` ([AiTestDto](plugins/action-reference.md#aitestdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/audit` | `audit.list` |  | `IReadOnlyList<AuditEntryDto>` ([AuditEntryDto](plugins/action-reference.md#auditentrydto)) | No | Needed | No |
| GET | `/api/v1/admin/audit/verify` | `audit.verify` |  | `AuditVerifyResult` ([AuditVerifyResult](plugins/action-reference.md#auditverifyresult)) | No | Needed | No |
| GET | `/api/v1/admin/auth/compat` | `auth.compat.read` |  | `CompatAuthSettingsDto` ([CompatAuthSettingsDto](plugins/action-reference.md#compatauthsettingsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/auth/compat` | `auth.compat.save` | `CompatAuthSettingsDto` ([CompatAuthSettingsDto](plugins/action-reference.md#compatauthsettingsdto)) | `CompatAuthSettingsDto` ([CompatAuthSettingsDto](plugins/action-reference.md#compatauthsettingsdto)) | Yes | Needed | No |
| POST | `/api/v1/admin/auth/local/disable` | `auth.local.disable` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/bin` | `bin.list.all` |  | `IReadOnlyList<BinItemDto>` ([BinItemDto](plugins/action-reference.md#binitemdto)) | No | Needed | No |
| GET | `/api/v1/admin/bin/tree` | `bin.browse.all` |  | `IReadOnlyList<BinNodeDto>` ([BinNodeDto](plugins/action-reference.md#binnodedto)) | No | Needed | No |
| PUT | `/api/v1/admin/brand` | `brand.update` | `RenameProductRequest` ([RenameProductRequest](plugins/action-reference.md#renameproductrequest)) | `Unit` | Yes | Needed | No |
| DELETE | `/api/v1/admin/brand/logo/{variant}` | `brand.logo.remove` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/brand/logo/{variant}` | `brand.logo.set` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/brand/theme` | `brand.theme.save` | `ThemeDto` ([ThemeDto](plugins/action-reference.md#themedto)) | `ThemeDto` ([ThemeDto](plugins/action-reference.md#themedto)) | Yes | Needed | No |
| GET | `/api/v1/admin/groups` | `group.list` |  | `IReadOnlyList<GroupDto>` ([GroupDto](plugins/action-reference.md#groupdto)) | No | Needed | No |
| POST | `/api/v1/admin/groups` | `group.create` | `GroupNameRequest` ([GroupNameRequest](plugins/action-reference.md#groupnamerequest)) | `GroupDto` ([GroupDto](plugins/action-reference.md#groupdto)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/groups/{id:guid}` | `group.delete` |  | `Unit` | Yes | Needed | No |
| PATCH | `/api/v1/admin/groups/{id:guid}` | `group.rename` | `GroupNameRequest` ([GroupNameRequest](plugins/action-reference.md#groupnamerequest)) | `GroupDto` ([GroupDto](plugins/action-reference.md#groupdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/groups/{id:guid}/members` | `group.member.list` |  | `IReadOnlyList<GroupMemberDto>` ([GroupMemberDto](plugins/action-reference.md#groupmemberdto)) | No | Needed | No |
| DELETE | `/api/v1/admin/groups/{id:guid}/members/{userId:guid}` | `group.member.remove` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/groups/{id:guid}/members/{userId:guid}` | `group.member.add` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/inventory/access` | `inventory.access.read` |  | `InvAccessDto` ([InvAccessDto](plugins/action-reference.md#invaccessdto)) | No | Needed | No |
| PUT | `/api/v1/admin/inventory/access` | `inventory.access.save` | `InvAccessDto` ([InvAccessDto](plugins/action-reference.md#invaccessdto)) | `InvAccessDto` ([InvAccessDto](plugins/action-reference.md#invaccessdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/logs/` | `log.read` |  | `LogsDto` ([LogsDto](plugins/action-reference.md#logsdto)) | No | Needed | No |
| POST | `/api/v1/admin/logs/targets` | `log.target.add` | `LogTargetRequest` ([LogTargetRequest](plugins/action-reference.md#logtargetrequest)) | `LogTargetDto` ([LogTargetDto](plugins/action-reference.md#logtargetdto)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/logs/targets/{id:guid}` | `log.target.remove` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/logs/targets/{id:guid}` | `log.target.save` | `LogTargetRequest` ([LogTargetRequest](plugins/action-reference.md#logtargetrequest)) | `LogTargetDto` ([LogTargetDto](plugins/action-reference.md#logtargetdto)) | Yes | Needed | No |
| POST | `/api/v1/admin/logs/targets/{id:guid}/test` | `log.target.test` |  | `StorageTestDto` ([StorageTestDto](plugins/action-reference.md#storagetestdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/oauth/clients` | `oauth.client.list` |  | `IReadOnlyList<OAuthClientDto>` ([OAuthClientDto](plugins/action-reference.md#oauthclientdto)) | No | Needed | No |
| POST | `/api/v1/admin/oauth/clients` | `oauth.client.create` | `OAuthClientRequest` ([OAuthClientRequest](plugins/action-reference.md#oauthclientrequest)) | `OAuthClientCreatedDto` ([OAuthClientCreatedDto](plugins/action-reference.md#oauthclientcreateddto)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/oauth/clients/{id:guid}` | `oauth.client.delete` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/oauth/clients/{id:guid}` | `oauth.client.read` |  | `OAuthClientDto` ([OAuthClientDto](plugins/action-reference.md#oauthclientdto)) | No | Needed | No |
| PUT | `/api/v1/admin/oauth/clients/{id:guid}` | `oauth.client.update` | `OAuthClientRequest` ([OAuthClientRequest](plugins/action-reference.md#oauthclientrequest)) | `OAuthClientDto` ([OAuthClientDto](plugins/action-reference.md#oauthclientdto)) | Yes | Needed | No |
| POST | `/api/v1/admin/oauth/clients/{id:guid}/secret` | `oauth.client.secret.rotate` |  | `OAuthClientCreatedDto` ([OAuthClientCreatedDto](plugins/action-reference.md#oauthclientcreateddto)) | Yes | Needed | No |
| GET | `/api/v1/admin/plugins` | `plugin.list` |  | `PluginListDto` ([PluginListDto](plugins/action-reference.md#pluginlistdto)) | No | Needed | No |
| POST | `/api/v1/admin/plugins` | `plugin.install` |  | `InstalledDto` ([InstalledDto](plugins/action-reference.md#installeddto)) | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/health` | `plugin.health` |  | `PluginHealthDto` ([PluginHealthDto](plugins/action-reference.md#pluginhealthdto)) | No | Needed | No |
| DELETE | `/api/v1/admin/plugins/{id}` | `plugin.remove` |  | `Unit` | Yes | Needed | No |
| DELETE | `/api/v1/admin/plugins/{id}/data` | `plugin.data.delete` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/{id}/permissions` | `plugin.permissions.read` |  | `PluginPermissionsDto` ([PluginPermissionsDto](plugins/action-reference.md#pluginpermissionsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/permissions` | `plugin.permissions.save` | `PluginPermissionsRequest` ([PluginPermissionsRequest](plugins/action-reference.md#pluginpermissionsrequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/plugins/{id}/release-lock` | `plugin.lock.release` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/{id}/settings` | `plugin.settings.read` |  | `PluginSettingsDto` ([PluginSettingsDto](plugins/action-reference.md#pluginsettingsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/settings` | `plugin.settings.save` | `PluginSettingsRequest` ([PluginSettingsRequest](plugins/action-reference.md#pluginsettingsrequest)) | `PluginSettingsSaved` ([PluginSettingsSaved](plugins/action-reference.md#pluginsettingssaved)) | Yes | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/state` | `plugin.state.set` | `PluginStateRequest` ([PluginStateRequest](plugins/action-reference.md#pluginstaterequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/roles` | `role.create` | `RoleRequest` ([RoleRequest](plugins/action-reference.md#rolerequest)) | `RoleDto` ([RoleDto](plugins/action-reference.md#roledto)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/roles/{id:guid}` | `role.delete` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/roles/{id:guid}` | `role.update` | `RoleRequest` ([RoleRequest](plugins/action-reference.md#rolerequest)) | `RoleDto` ([RoleDto](plugins/action-reference.md#roledto)) | Yes | Needed | No |
| POST | `/api/v1/admin/search/reindex` | `search.reindex` |  | `ReindexResult` ([ReindexResult](plugins/action-reference.md#reindexresult)) | Yes | Needed | No |
| GET | `/api/v1/admin/search/synonyms` | `search.synonyms.read` |  | `SynonymsDto` ([SynonymsDto](plugins/action-reference.md#synonymsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/search/synonyms` | `search.synonyms.save` | `SynonymsDto` ([SynonymsDto](plugins/action-reference.md#synonymsdto)) | `SynonymsDto` ([SynonymsDto](plugins/action-reference.md#synonymsdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/settings/codes` | `settings.codes.read` |  | `CodeSettings` ([CodeSettings](plugins/action-reference.md#codesettings)) | No | Needed | No |
| PUT | `/api/v1/admin/settings/codes` | `settings.codes.set` | `CodeSettings` ([CodeSettings](plugins/action-reference.md#codesettings)) | `Unit` | Yes | Needed | No |
| GET | `/api/v1/admin/spaces` | `space.list.all` |  | `IReadOnlyList<SpaceDto>` ([SpaceDto](plugins/action-reference.md#spacedto)) | No | Needed | No |
| GET | `/api/v1/admin/spaces/deleted` | `space.list.deleted` |  | `IReadOnlyList<DeletedSpaceDto>` ([DeletedSpaceDto](plugins/action-reference.md#deletedspacedto)) | No | Needed | No |
| PATCH | `/api/v1/admin/spaces/{id:guid}` | `space.update.admin` | `UpdateSpaceRequest` ([UpdateSpaceRequest](plugins/action-reference.md#updatespacerequest)) | `SpaceDto` ([SpaceDto](plugins/action-reference.md#spacedto)) | Yes | Needed | No |
| POST | `/api/v1/admin/spaces/{id:guid}/delete` | `space.delete` | `DeleteSpaceRequest` ([DeleteSpaceRequest](plugins/action-reference.md#deletespacerequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/spaces/{id:guid}/restore` | `space.restore` |  | `SpaceDto` ([SpaceDto](plugins/action-reference.md#spacedto)) | Yes | Needed | No |
| GET | `/api/v1/admin/storage/` | `storage.read` |  | `StorageDto` ([StorageDto](plugins/action-reference.md#storagedto)) | No | Needed | No |
| POST | `/api/v1/admin/storage/move` | `storage.move.start` | `StorageMoveRequest` ([StorageMoveRequest](plugins/action-reference.md#storagemoverequest)) | `StorageMoveDto` ([StorageMoveDto](plugins/action-reference.md#storagemovedto)) | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/cancel` | `storage.move.cancel` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/copy-again` | `storage.move.copy-again` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/storage/move/preview` | `storage.move.preview` | `StoragePreviewRequest` ([StoragePreviewRequest](plugins/action-reference.md#storagepreviewrequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/switch` | `storage.move.switch` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/places` | `storage.place.add` | `StoragePlaceRequest` ([StoragePlaceRequest](plugins/action-reference.md#storageplacerequest)) | `StoragePlaceDto` ([StoragePlaceDto](plugins/action-reference.md#storageplacedto)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/storage/places/{id:guid}` | `storage.place.remove` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/storage/places/{id:guid}` | `storage.place.save` | `StoragePlaceRequest` ([StoragePlaceRequest](plugins/action-reference.md#storageplacerequest)) | `StoragePlaceDto` ([StoragePlaceDto](plugins/action-reference.md#storageplacedto)) | Yes | Needed | No |
| POST | `/api/v1/admin/storage/places/{id:guid}/test` | `storage.place.test` |  | `StorageTestDto` ([StorageTestDto](plugins/action-reference.md#storagetestdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/tokens` | `token.list.all` |  | `IReadOnlyList<ApiTokenDto>` ([ApiTokenDto](plugins/action-reference.md#apitokendto)) | No | Needed | No |
| GET | `/api/v1/admin/tokens/settings` | `token.settings.read` |  | `TokenSettingsDto` ([TokenSettingsDto](plugins/action-reference.md#tokensettingsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/tokens/settings` | `token.settings.save` | `TokenSettingsDto` ([TokenSettingsDto](plugins/action-reference.md#tokensettingsdto)) | `TokenSettingsDto` ([TokenSettingsDto](plugins/action-reference.md#tokensettingsdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/uploads` | `settings.uploads.read` |  | `UploadSettingsDto` ([UploadSettingsDto](plugins/action-reference.md#uploadsettingsdto)) | No | Needed | No |
| PUT | `/api/v1/admin/uploads` | `settings.uploads.save` | `UploadSettingsRequest` ([UploadSettingsRequest](plugins/action-reference.md#uploadsettingsrequest)) | `UploadSettingsDto` ([UploadSettingsDto](plugins/action-reference.md#uploadsettingsdto)) | Yes | Needed | No |
| GET | `/api/v1/admin/users` | `user.list` |  | `IReadOnlyList<AdminUserDto>` ([AdminUserDto](plugins/action-reference.md#adminuserdto)) | No | Needed | No |
| POST | `/api/v1/admin/users/invite` | `user.invite` | `InviteRequest` ([InviteRequest](plugins/action-reference.md#inviterequest)) | `InvitedUser` ([InvitedUser](plugins/action-reference.md#inviteduser)) | Yes | Needed | No |
| DELETE | `/api/v1/admin/users/{id:guid}` | `user.delete` |  | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/users/{id:guid}/announcer` | `user.announcer.set` | `AnnouncerRequest` ([AnnouncerRequest](plugins/action-reference.md#announcerrequest)) | `Unit` | Yes | Needed | No |
| PUT | `/api/v1/admin/users/{id:guid}/codes/exempt` | `user.codes.exempt` | `CodesExemptRequest` ([CodesExemptRequest](plugins/action-reference.md#codesexemptrequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/users/{id:guid}/codes/reset` | `user.codes.reset` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/admin/users/{id:guid}/password-link` | `user.password.link` |  | `InvitedUser` ([InvitedUser](plugins/action-reference.md#inviteduser)) | Yes | Needed | No |
| PUT | `/api/v1/admin/users/{id:guid}/permissions` | `user.permissions.set` | `UserPermissionRequest` ([UserPermissionRequest](plugins/action-reference.md#userpermissionrequest)) | `Unit` | Yes | Needed | No |
| POST | `/api/v1/announcements` | `announcement.post` | `PostAnnouncementRequest` ([PostAnnouncementRequest](plugins/action-reference.md#postannouncementrequest)) | `AnnouncementPostedDto` ([AnnouncementPostedDto](plugins/action-reference.md#announcementposteddto)) | Yes | Needed | No |
| DELETE | `/api/v1/announcements/{id:guid}` | `announcement.remove` |  | `Unit` | Yes | Needed | No |
| DELETE | `/api/v1/attachments/{id:guid}` | `attachment.remove` |  | `Unit` | Yes | Needed | Write |
| GET | `/api/v1/attachments/{id:guid}` | `attachment.read` |  | `FileOut` | No | Not needed | Read |
| POST | `/api/v1/auth/codes` | `auth.codes.enroll` | `CodeRequest` ([CodeRequest](plugins/action-reference.md#coderequest)) | `CodesEnrolled` ([CodesEnrolled](plugins/action-reference.md#codesenrolled)) | Yes | Not needed | No |
| POST | `/api/v1/auth/codes/key` | `auth.codes.key` |  | `CodeKey` ([CodeKey](plugins/action-reference.md#codekey)) | Yes | Not needed | No |
| POST | `/api/v1/auth/local/code` | `auth.signin.code` | `CodeRequest` ([CodeRequest](plugins/action-reference.md#coderequest)) | `MeResponse` ([MeResponse](plugins/action-reference.md#meresponse)) | Yes | Not needed | No |
| POST | `/api/v1/auth/local/password` | `auth.password.set` | `SetPasswordRequest` ([SetPasswordRequest](plugins/action-reference.md#setpasswordrequest)) | `Unit` | Yes | Not needed | No |
| POST | `/api/v1/auth/local/signin` | `auth.signin` | `SignInRequest` ([SignInRequest](plugins/action-reference.md#signinrequest)) | `MeResponse` ([MeResponse](plugins/action-reference.md#meresponse)) | Yes | Not needed | No |
| POST | `/api/v1/auth/signout` | `auth.signout` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/bin/pages/{id:guid}` | `bin.page.read` |  | `BinPageDto` ([BinPageDto](plugins/action-reference.md#binpagedto)) | No | Needed | No |
| GET | `/api/v1/brand` | `brand.read` |  | `BrandDto` ([BrandDto](plugins/action-reference.md#branddto)) | No | Not needed | Read |
| GET | `/api/v1/brand/logo/{variant}` | `brand.logo.read` |  | `FileOut` | No | Not needed | Read |
| GET | `/api/v1/home` | `home.read` |  | `HomeDto` ([HomeDto](plugins/action-reference.md#homedto)) | No | Needed | Read |
| GET | `/api/v1/images/{id:guid}` | `image.read` |  | `FileOut` | No | Not needed | Read |
| GET | `/api/v1/imports/` | `import.list` |  | `ImportListDto` ([ImportListDto](plugins/action-reference.md#importlistdto)) | No | Needed | No |
| POST | `/api/v1/imports/` | `import.batch.create` | `ImportBatchRequest` ([ImportBatchRequest](plugins/action-reference.md#importbatchrequest)) | `ImportBatchDto` ([ImportBatchDto](plugins/action-reference.md#importbatchdto)) | Yes | Needed | No |
| GET | `/api/v1/imports/files/{id:guid}` | `import.file.read` |  | `FileOut` | No | Needed | No |
| DELETE | `/api/v1/imports/items/{id:guid}` | `import.discard` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/imports/items/{id:guid}` | `import.read` |  | `ImportItemDto` ([ImportItemDto](plugins/action-reference.md#importitemdto)) | No | Needed | No |
| POST | `/api/v1/imports/items/{id:guid}/accept` | `import.accept` | `AcceptRequest` ([AcceptRequest](plugins/action-reference.md#acceptrequest)) | `IReadOnlyList<Guid>` | Yes | Needed | No |
| POST | `/api/v1/imports/items/{id:guid}/reject` | `import.reject` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/imports/items/{id:guid}/retry` | `import.retry` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/imports/merge` | `import.merge` | `ImportMergeRequest` ([ImportMergeRequest](plugins/action-reference.md#importmergerequest)) | `ImportItemRowDto` ([ImportItemRowDto](plugins/action-reference.md#importitemrowdto)) | Yes | Needed | No |
| POST | `/api/v1/imports/{id:guid}/files` | `import.file.add` |  | `ImportFileDto` ([ImportFileDto](plugins/action-reference.md#importfiledto)) | Yes | Needed | No |
| POST | `/api/v1/imports/{id:guid}/start` | `import.batch.start` |  | `ImportBatchDto` ([ImportBatchDto](plugins/action-reference.md#importbatchdto)) | Yes | Needed | No |
| GET | `/api/v1/inventory/apps` | `inventory.app.list` |  | `IReadOnlyList<InvAppRowDto>` ([InvAppRowDto](plugins/action-reference.md#invapprowdto)) | No | Needed | No |
| POST | `/api/v1/inventory/apps` | `inventory.app.create` | `InvAppRequest` ([InvAppRequest](plugins/action-reference.md#invapprequest)) | `InvAppDto` ([InvAppDto](plugins/action-reference.md#invappdto)) | Yes | Needed | No |
| DELETE | `/api/v1/inventory/apps/{id:guid}` | `inventory.app.delete` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/inventory/apps/{id:guid}` | `inventory.app.read` |  | `InvAppDto` ([InvAppDto](plugins/action-reference.md#invappdto)) | No | Needed | No |
| PUT | `/api/v1/inventory/apps/{id:guid}` | `inventory.app.update` | `InvAppRequest` ([InvAppRequest](plugins/action-reference.md#invapprequest)) | `InvAppDto` ([InvAppDto](plugins/action-reference.md#invappdto)) | Yes | Needed | No |
| PUT | `/api/v1/inventory/apps/{id:guid}/installs` | `inventory.install.set` | `InvInstallRequest` ([InvInstallRequest](plugins/action-reference.md#invinstallrequest)) | `InvAppDto` ([InvAppDto](plugins/action-reference.md#invappdto)) | Yes | Needed | No |
| POST | `/api/v1/inventory/import` | `inventory.import` | `InvImportRequest` ([InvImportRequest](plugins/action-reference.md#invimportrequest)) | `SyncResultDto` ([SyncResultDto](plugins/action-reference.md#syncresultdto)) | Yes | Needed | No |
| DELETE | `/api/v1/inventory/installs/{id:guid}` | `inventory.install.remove` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/inventory/me` | `inventory.me` |  | `InvMeDto` ([InvMeDto](plugins/action-reference.md#invmedto)) | No | Needed | No |
| GET | `/api/v1/inventory/servers` | `inventory.server.list` |  | `IReadOnlyList<string>` | No | Needed | No |
| POST | `/api/v1/inventory/sync` | `inventory.installs.sync` | `InvSyncRequest` ([InvSyncRequest](plugins/action-reference.md#invsyncrequest)) | `SyncResultDto` ([SyncResultDto](plugins/action-reference.md#syncresultdto)) | Yes | Needed | Write |
| GET | `/api/v1/me` | `me.read` |  | `MeResponse` ([MeResponse](plugins/action-reference.md#meresponse)) | No | Needed | Read |
| DELETE | `/api/v1/me/codes` | `me.codes.off` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/me/codes` | `me.codes.read` |  | `MyCodes` ([MyCodes](plugins/action-reference.md#mycodes)) | No | Needed | No |
| GET | `/api/v1/me/settings` | `me.settings.read` |  | `UserSettings` ([UserSettings](plugins/action-reference.md#usersettings)) | No | Needed | No |
| PUT | `/api/v1/me/settings` | `me.settings.save` | `UserSettings` ([UserSettings](plugins/action-reference.md#usersettings)) | `UserSettings` ([UserSettings](plugins/action-reference.md#usersettings)) | Yes | Needed | No |
| GET | `/api/v1/notifications` | `notification.list` |  | `IReadOnlyList<NotificationDto>` ([NotificationDto](plugins/action-reference.md#notificationdto)) | No | Needed | No |
| GET | `/api/v1/notifications/count` | `notification.count` |  | `NotificationCountDto` ([NotificationCountDto](plugins/action-reference.md#notificationcountdto)) | No | Needed | No |
| POST | `/api/v1/notifications/read-all` | `notification.mark.all` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/notifications/{id:guid}/read` | `notification.mark` |  | `Unit` | Yes | Needed | No |
| DELETE | `/api/v1/pages/{id:guid}` | `page.delete` |  | `Unit` | Yes | Needed | Write |
| GET | `/api/v1/pages/{id:guid}` | `page.read` |  | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | No | Not needed | Read |
| PUT | `/api/v1/pages/{id:guid}` | `page.save` | `SavePageRequest` ([SavePageRequest](plugins/action-reference.md#savepagerequest)) | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | Yes | Needed | Write |
| POST | `/api/v1/pages/{id:guid}/ai-review` | `page.ai.review` |  | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | Yes | Needed | No |
| POST | `/api/v1/pages/{id:guid}/attachments` | `attachment.add` |  | `AttachmentDto` ([AttachmentDto](plugins/action-reference.md#attachmentdto)) | Yes | Needed | Write |
| PUT | `/api/v1/pages/{id:guid}/details` | `page.details.save` | `PageDetailsRequest` ([PageDetailsRequest](plugins/action-reference.md#pagedetailsrequest)) | `PageDetailsDto` ([PageDetailsDto](plugins/action-reference.md#pagedetailsdto)) | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/diff` | `page.diff` |  | `DiffDto` ([DiffDto](plugins/action-reference.md#diffdto)) | No | Needed | Read |
| DELETE | `/api/v1/pages/{id:guid}/draft` | `draft.discard` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/draft` | `draft.read` |  | `DraftDto` ([DraftDto](plugins/action-reference.md#draftdto)) | No | Needed | No |
| PUT | `/api/v1/pages/{id:guid}/draft` | `draft.save` | `SaveDraftRequest` ([SaveDraftRequest](plugins/action-reference.md#savedraftrequest)) | `DraftSavedDto` ([DraftSavedDto](plugins/action-reference.md#draftsaveddto)) | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/export` | `page.export` |  | `FileOut` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/export/code/{n:int}` | `page.export.code` |  | `FileOut` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/export/table/{n:int}` | `page.export.table` |  | `FileOut` | No | Needed | Read |
| POST | `/api/v1/pages/{id:guid}/format` | `page.format` | `FormatRequest` ([FormatRequest](plugins/action-reference.md#formatrequest)) | `FormatDto` ([FormatDto](plugins/action-reference.md#formatdto)) | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/markdown` | `page.markdown` |  | `PageMarkdownDto` ([PageMarkdownDto](plugins/action-reference.md#pagemarkdowndto)) | No | Needed | Read |
| POST | `/api/v1/pages/{id:guid}/move` | `page.move` | `MoveRequest` ([MoveRequest](plugins/action-reference.md#moverequest)) | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | Yes | Needed | Write |
| POST | `/api/v1/pages/{id:guid}/restore` | `page.restore` |  | `RestoredPageDto` ([RestoredPageDto](plugins/action-reference.md#restoredpagedto)) | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/versions` | `page.version.list` |  | `IReadOnlyList<VersionDto>` ([VersionDto](plugins/action-reference.md#versiondto)) | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/versions/{version}` | `page.version.read` |  | `VersionContentDto` ([VersionContentDto](plugins/action-reference.md#versioncontentdto)) | No | Needed | Read |
| POST | `/api/v1/pages/{id:guid}/versions/{version}/restore` | `page.version.restore` |  | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | Yes | Needed | Write |
| GET | `/api/v1/principals` | `principal.list` |  | `IReadOnlyList<PrincipalDto>` ([PrincipalDto](plugins/action-reference.md#principaldto)) | No | Needed | No |
| GET | `/api/v1/roles` | `role.list` |  | `IReadOnlyList<RoleDto>` ([RoleDto](plugins/action-reference.md#roledto)) | No | Needed | No |
| GET | `/api/v1/search` | `search.query` |  | `SearchResponse` ([SearchResponse](plugins/action-reference.md#searchresponse)) | No | Not needed | Read |
| POST | `/api/v1/setup` | `setup.run` | `SetupRequest` ([SetupRequest](plugins/action-reference.md#setuprequest)) | `MeResponse` ([MeResponse](plugins/action-reference.md#meresponse)) | Yes | Not needed | No |
| GET | `/api/v1/setup/status` | `setup.status` |  | `SetupStatus` ([SetupStatus](plugins/action-reference.md#setupstatus)) | No | Not needed | No |
| GET | `/api/v1/spaces` | `space.list` |  | `IReadOnlyList<SpaceDto>` ([SpaceDto](plugins/action-reference.md#spacedto)) | No | Not needed | Read |
| POST | `/api/v1/spaces` | `space.create` | `CreateSpaceRequest` ([CreateSpaceRequest](plugins/action-reference.md#createspacerequest)) | `SpaceDto` ([SpaceDto](plugins/action-reference.md#spacedto)) | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}` | `space.read` |  | `SpaceDto` ([SpaceDto](plugins/action-reference.md#spacedto)) | No | Not needed | Read |
| PATCH | `/api/v1/spaces/{id:guid}` | `space.update` | `UpdateSpaceRequest` ([UpdateSpaceRequest](plugins/action-reference.md#updatespacerequest)) | `SpaceDto` ([SpaceDto](plugins/action-reference.md#spacedto)) | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/access-requests` | `access.request` | `AccessRequestBody` ([AccessRequestBody](plugins/action-reference.md#accessrequestbody)) | `AccessRequestedDto` ([AccessRequestedDto](plugins/action-reference.md#accessrequesteddto)) | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/assist` | `page.assist` | `AssistRequest` ([AssistRequest](plugins/action-reference.md#assistrequest)) | `AssistDto` ([AssistDto](plugins/action-reference.md#assistdto)) | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}/bin` | `bin.list` |  | `IReadOnlyList<BinItemDto>` ([BinItemDto](plugins/action-reference.md#binitemdto)) | No | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/bin/empty` | `bin.empty` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}/bin/tree` | `bin.browse` |  | `IReadOnlyList<BinNodeDto>` ([BinNodeDto](plugins/action-reference.md#binnodedto)) | No | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/emergency-access` | `space.sensitive.emergency` | `EmergencyBody` ([EmergencyBody](plugins/action-reference.md#emergencybody)) | `EmergencyAccessDto` ([EmergencyAccessDto](plugins/action-reference.md#emergencyaccessdto)) | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/images` | `image.add` |  | `ImageDto` ([ImageDto](plugins/action-reference.md#imagedto)) | Yes | Needed | Write |
| POST | `/api/v1/spaces/{id:guid}/images/import` | `image.import` | `ImportImageRequest` ([ImportImageRequest](plugins/action-reference.md#importimagerequest)) | `ImageDto` ([ImageDto](plugins/action-reference.md#imagedto)) | Yes | Needed | Write |
| POST | `/api/v1/spaces/{id:guid}/pages` | `page.create` | `CreatePageRequest` ([CreatePageRequest](plugins/action-reference.md#createpagerequest)) | `PageDto` ([PageDto](plugins/action-reference.md#pagedto)) | Yes | Needed | Write |
| GET | `/api/v1/spaces/{id:guid}/roles` | `space.role.list` |  | `IReadOnlyList<RoleAssignmentDto>` ([RoleAssignmentDto](plugins/action-reference.md#roleassignmentdto)) | No | Needed | No |
| PUT | `/api/v1/spaces/{id:guid}/roles` | `space.role.grant` | `GrantRoleRequest` ([GrantRoleRequest](plugins/action-reference.md#grantrolerequest)) | `Unit` | Yes | Needed | No |
| DELETE | `/api/v1/spaces/{id:guid}/roles/{assignmentId:guid}` | `space.role.revoke` |  | `Unit` | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}/tree` | `tree.read` |  | `IReadOnlyList<TreeNodeDto>` ([TreeNodeDto](plugins/action-reference.md#treenodedto)) | No | Not needed | Read |
| GET | `/api/v1/tokens` | `token.list` |  | `IReadOnlyList<ApiTokenDto>` ([ApiTokenDto](plugins/action-reference.md#apitokendto)) | No | Needed | No |
| POST | `/api/v1/tokens` | `token.create` | `CreateTokenRequest` ([CreateTokenRequest](plugins/action-reference.md#createtokenrequest)) | `ApiTokenCreatedDto` ([ApiTokenCreatedDto](plugins/action-reference.md#apitokencreateddto)) | Yes | Needed | No |
| DELETE | `/api/v1/tokens/{id:guid}` | `token.revoke` |  | `Unit` | Yes | Needed | No |
| POST | `/api/v1/ui/errors` | `ui.error.report` | `UiErrorRequest` ([UiErrorRequest](plugins/action-reference.md#uierrorrequest)) | `Unit` | Yes | Needed | No |
| GET | `/api/v1/ui/plugins` | `ui.plugins` |  | `UiPluginsDto` ([UiPluginsDto](plugins/action-reference.md#uipluginsdto)) | No | Needed | No |
