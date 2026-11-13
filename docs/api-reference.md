# API reference

Written by the test `ApiReference_IsUpToDate` (run it with `KB_WRITE_REFS=1`). Do not edit by hand.

Every route sends one action; [the action reference](plugins/action-reference.md) lists each action's fields and result.
The answer names the action in the `X-Kb-Action` header. A route needs sign-in unless it says otherwise.
API token: the scope a token needs to call the route. "No" means the route answers 403 to every token. See [the agent API](agent-api.md).

| Method | Route | Action | Changes data | Sign-in | API token |
|---|---|---|---|---|---|
| POST | `/api/v1/access-requests/{id:guid}/decision` | `access.decide` | Yes | Needed | No |
| GET | `/api/v1/admin/audit` | `audit.list` | No | Needed | No |
| GET | `/api/v1/admin/audit/verify` | `audit.verify` | No | Needed | No |
| POST | `/api/v1/admin/auth/local/disable` | `auth.local.disable` | Yes | Needed | No |
| GET | `/api/v1/admin/bin` | `bin.list.all` | No | Needed | No |
| PUT | `/api/v1/admin/brand` | `brand.update` | Yes | Needed | No |
| DELETE | `/api/v1/admin/brand/logo/{variant}` | `brand.logo.remove` | Yes | Needed | No |
| PUT | `/api/v1/admin/brand/logo/{variant}` | `brand.logo.set` | Yes | Needed | No |
| GET | `/api/v1/admin/logs/` | `log.read` | No | Needed | No |
| POST | `/api/v1/admin/logs/targets` | `log.target.add` | Yes | Needed | No |
| DELETE | `/api/v1/admin/logs/targets/{id:guid}` | `log.target.remove` | Yes | Needed | No |
| PUT | `/api/v1/admin/logs/targets/{id:guid}` | `log.target.save` | Yes | Needed | No |
| POST | `/api/v1/admin/logs/targets/{id:guid}/test` | `log.target.test` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins` | `plugin.list` | No | Needed | No |
| POST | `/api/v1/admin/plugins` | `plugin.install` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/health` | `plugin.health` | No | Needed | No |
| DELETE | `/api/v1/admin/plugins/{id}` | `plugin.remove` | Yes | Needed | No |
| DELETE | `/api/v1/admin/plugins/{id}/data` | `plugin.data.delete` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/{id}/permissions` | `plugin.permissions.read` | No | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/permissions` | `plugin.permissions.save` | Yes | Needed | No |
| POST | `/api/v1/admin/plugins/{id}/release-lock` | `plugin.lock.release` | Yes | Needed | No |
| GET | `/api/v1/admin/plugins/{id}/settings` | `plugin.settings.read` | No | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/settings` | `plugin.settings.save` | Yes | Needed | No |
| PUT | `/api/v1/admin/plugins/{id}/state` | `plugin.state.set` | Yes | Needed | No |
| POST | `/api/v1/admin/search/reindex` | `search.reindex` | Yes | Needed | No |
| GET | `/api/v1/admin/search/synonyms` | `search.synonyms.read` | No | Needed | No |
| PUT | `/api/v1/admin/search/synonyms` | `search.synonyms.save` | Yes | Needed | No |
| PUT | `/api/v1/admin/settings/public-space` | `settings.public-space.set` | Yes | Needed | No |
| GET | `/api/v1/admin/spaces` | `space.list.all` | No | Needed | No |
| PATCH | `/api/v1/admin/spaces/{id:guid}` | `space.update.admin` | Yes | Needed | No |
| GET | `/api/v1/admin/storage/` | `storage.read` | No | Needed | No |
| POST | `/api/v1/admin/storage/move` | `storage.move.start` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/cancel` | `storage.move.cancel` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/copy-again` | `storage.move.copy-again` | Yes | Needed | No |
| PUT | `/api/v1/admin/storage/move/preview` | `storage.move.preview` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/move/switch` | `storage.move.switch` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/places` | `storage.place.add` | Yes | Needed | No |
| DELETE | `/api/v1/admin/storage/places/{id:guid}` | `storage.place.remove` | Yes | Needed | No |
| PUT | `/api/v1/admin/storage/places/{id:guid}` | `storage.place.save` | Yes | Needed | No |
| POST | `/api/v1/admin/storage/places/{id:guid}/test` | `storage.place.test` | Yes | Needed | No |
| GET | `/api/v1/admin/tokens` | `token.list.all` | No | Needed | No |
| GET | `/api/v1/admin/tokens/settings` | `token.settings.read` | No | Needed | No |
| PUT | `/api/v1/admin/tokens/settings` | `token.settings.save` | Yes | Needed | No |
| GET | `/api/v1/admin/uploads` | `settings.uploads.read` | No | Needed | No |
| PUT | `/api/v1/admin/uploads` | `settings.uploads.save` | Yes | Needed | No |
| GET | `/api/v1/admin/users` | `user.list` | No | Needed | No |
| POST | `/api/v1/admin/users/invite` | `user.invite` | Yes | Needed | No |
| PUT | `/api/v1/admin/users/{id:guid}/announcer` | `user.announcer.set` | Yes | Needed | No |
| PUT | `/api/v1/admin/users/{id:guid}/permissions` | `user.permissions.set` | Yes | Needed | No |
| POST | `/api/v1/announcements` | `announcement.post` | Yes | Needed | No |
| DELETE | `/api/v1/announcements/{id:guid}` | `announcement.remove` | Yes | Needed | No |
| DELETE | `/api/v1/attachments/{id:guid}` | `attachment.remove` | Yes | Needed | Write |
| GET | `/api/v1/attachments/{id:guid}` | `attachment.read` | No | Needed | Read |
| POST | `/api/v1/auth/local/signin` | `auth.signin` | Yes | Not needed | No |
| POST | `/api/v1/auth/signout` | `auth.signout` | Yes | Needed | No |
| GET | `/api/v1/brand` | `brand.read` | No | Not needed | Read |
| GET | `/api/v1/brand/logo/{variant}` | `brand.logo.read` | No | Not needed | Read |
| GET | `/api/v1/home` | `home.read` | No | Needed | Read |
| GET | `/api/v1/images/{id:guid}` | `image.read` | No | Needed | Read |
| GET | `/api/v1/me` | `me.read` | No | Needed | Read |
| GET | `/api/v1/me/settings` | `me.settings.read` | No | Needed | No |
| PUT | `/api/v1/me/settings` | `me.settings.save` | Yes | Needed | No |
| GET | `/api/v1/notifications` | `notification.list` | No | Needed | No |
| GET | `/api/v1/notifications/count` | `notification.count` | No | Needed | No |
| POST | `/api/v1/notifications/read-all` | `notification.mark.all` | Yes | Needed | No |
| POST | `/api/v1/notifications/{id:guid}/read` | `notification.mark` | Yes | Needed | No |
| DELETE | `/api/v1/pages/{id:guid}` | `page.delete` | Yes | Needed | Write |
| GET | `/api/v1/pages/{id:guid}` | `page.read` | No | Needed | Read |
| PUT | `/api/v1/pages/{id:guid}` | `page.save` | Yes | Needed | Write |
| POST | `/api/v1/pages/{id:guid}/attachments` | `attachment.add` | Yes | Needed | Write |
| GET | `/api/v1/pages/{id:guid}/diff` | `page.diff` | No | Needed | Read |
| DELETE | `/api/v1/pages/{id:guid}/draft` | `draft.discard` | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/draft` | `draft.read` | No | Needed | No |
| PUT | `/api/v1/pages/{id:guid}/draft` | `draft.save` | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/export` | `page.export` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/export/code/{n:int}` | `page.export.code` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/export/table/{n:int}` | `page.export.table` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/markdown` | `page.markdown` | No | Needed | Read |
| POST | `/api/v1/pages/{id:guid}/move` | `page.move` | Yes | Needed | Write |
| POST | `/api/v1/pages/{id:guid}/restore` | `page.restore` | Yes | Needed | No |
| GET | `/api/v1/pages/{id:guid}/versions` | `page.version.list` | No | Needed | Read |
| GET | `/api/v1/pages/{id:guid}/versions/{version}` | `page.version.read` | No | Needed | Read |
| POST | `/api/v1/pages/{id:guid}/versions/{version}/restore` | `page.version.restore` | Yes | Needed | Write |
| GET | `/api/v1/search` | `search.query` | No | Needed | Read |
| POST | `/api/v1/setup` | `setup.run` | Yes | Not needed | No |
| GET | `/api/v1/setup/status` | `setup.status` | No | Not needed | No |
| GET | `/api/v1/spaces` | `space.list` | No | Needed | Read |
| POST | `/api/v1/spaces` | `space.create` | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}` | `space.read` | No | Needed | Read |
| PATCH | `/api/v1/spaces/{id:guid}` | `space.update` | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/access-requests` | `access.request` | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}/bin` | `bin.list` | No | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/bin/empty` | `bin.empty` | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/emergency-access` | `space.sensitive.emergency` | Yes | Needed | No |
| POST | `/api/v1/spaces/{id:guid}/images` | `image.add` | Yes | Needed | Write |
| POST | `/api/v1/spaces/{id:guid}/images/import` | `image.import` | Yes | Needed | Write |
| POST | `/api/v1/spaces/{id:guid}/pages` | `page.create` | Yes | Needed | Write |
| GET | `/api/v1/spaces/{id:guid}/roles` | `space.role.list` | No | Needed | No |
| PUT | `/api/v1/spaces/{id:guid}/roles` | `space.role.grant` | Yes | Needed | No |
| DELETE | `/api/v1/spaces/{id:guid}/roles/{assignmentId:guid}` | `space.role.revoke` | Yes | Needed | No |
| GET | `/api/v1/spaces/{id:guid}/tree` | `tree.read` | No | Needed | Read |
| GET | `/api/v1/tokens` | `token.list` | No | Needed | No |
| POST | `/api/v1/tokens` | `token.create` | Yes | Needed | No |
| DELETE | `/api/v1/tokens/{id:guid}` | `token.revoke` | Yes | Needed | No |
| POST | `/api/v1/ui/errors` | `ui.error.report` | Yes | Needed | No |
| GET | `/api/v1/ui/plugins` | `ui.plugins` | No | Needed | No |
