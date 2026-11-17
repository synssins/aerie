# Plugin admin guide

Plugins are managed on Administration > Plugins, by an Admin. This guide covers what is built today.

Related: [developer guide](developer-guide.md), [building and packaging](packaging.md), [manifest reference](manifest-reference.md).

## Before you install one

A plugin runs inside the app with the app's rights. Install a plugin only from a publisher you trust as much as the app itself.
A plugin can also ship a script that runs in the browser of every person who signs in, with that person's session.
The app loads any package an Admin uploads. Packages are not signed. It records each install in the audit log with the package's SHA-256: compare it with the hash the publisher gives you (`Get-FileHash <file>` on Windows, `sha256sum <file>` elsewhere).

## Install and update

1. Select Upload and choose the `.kbplugin` file.
2. The screen shows "Restarting". The app restarts itself and the list comes back.
3. The plugin is listed as Enabled.

To update, upload a package with the same id and a higher version. The older package is deleted. Settings and data are kept.
To go back to an older version, remove the plugin (keep its data) and upload the older package.

A package that is refused changes nothing. The screen shows the reason; the [manifest reference](manifest-reference.md#what-refuses-an-install) lists them.

## Every change restarts the app

Install, update, enable, disable, remove, delete data, release a lock, and a setting marked as needing a restart: each restarts the app.
A restart takes a few seconds. People who are signed in stay signed in.

## Disable and enable

Disable keeps the package, the settings and the data. The plugin is not loaded.
A plugin that another enabled plugin requires cannot be disabled: "Required by <name>."
A plugin that supplies the storage kind of a storage place in use, or the kind of an enabled log target, cannot be disabled either.

## Remove, and delete data

Remove deletes the package and the settings. The dialog has a "Delete data" box, off by default.

- Box off: the plugin's tables are kept. The plugin stays in the list as "Removed, data kept". Installing it again finds its data.
- Box on: the plugin's tables are dropped, and with them what was set for its permissions: the minimum roles, and who was given its global permissions. This cannot be undone.

For a plugin listed as "Removed, data kept", select it and select Delete data.

## Content of a plugin that is not loaded

A plugin can add its own blocks to pages. When such a plugin is disabled or removed, its blocks stay in the pages.
A page shows a box with the block's type name in their place. The box can be moved or deleted, not edited. Saving the page keeps the block.
History and exports show `[type]` and the text inside the block. Search keeps the words it had when the page was last saved; select Rebuild index (Administration > Search) after a plugin that adds blocks was disabled or removed, and search finds the pages by the text inside the blocks.
Enable or install the plugin again and the blocks are back.
After installing or updating a plugin that adds blocks, select Rebuild index (Administration > Search) so search reads older pages the plugin's way.

## Settings and secrets

Select a plugin to see its settings. A secret that is set shows as dots. Its value is never loaded into the browser; type a new value to replace it.
A change applies at once, unless the setting needs a restart.
The audit log records which keys changed, never a value.

## Permissions

A plugin can declare permissions of its own.

- A space permission is held by everyone whose role in a space is at least a minimum role. Select the plugin and choose that role under Permissions. The plugin's own default is shown first.
- A global permission is given to people one by one. Administration > Users has one column for each; tick the box for a person. Admins hold every permission.

A permission counts only while its plugin is loaded. What was set or given is kept when the plugin is disabled or removed, and counts again when it is back. Removing the plugin with "Delete data" removes it for good.
A minimum role that equals the plugin's own default is not stored: when a later version of the plugin changes its default, the new default holds unless you chose another role.
While a plugin is not loaded, the Users screen has no column for its permissions. A permission can still be taken from a person through the API (`PUT /api/v1/admin/users/<id>/permissions` with `"granted": false`).

## Disabled by core

The app turns a plugin off by itself when the plugin cannot start, or when it fails five times in ten minutes. The screen shows the reason.
Plugins that require it are turned off with it.

To put it back: correct the cause (a setting, the remote system, a new version of the plugin), then select Enable. Uploading a new version does not enable it by itself.

## Locked

A plugin marked as enforcing guards a rule. When the app has to disable such a plugin, the actions it guarded are locked:
everyone but an Admin gets "<plugin name> is disabled." for them. Sign-in is never locked, and an Admin can still do everything.

Select Release lock when you accept that the rule is not enforced for now. Enabling the plugin again also ends the lock.

## Safe Mode

When a plugin stops the app from starting, or keeps people from signing in, set `Kb:Plugins:SafeMode` to `true` in the app's configuration and start the app.
In Safe Mode no uploaded plugin is loaded, no state is changed and locks are ignored. Remove or disable the plugin, then switch Safe Mode off.

## Plugins that reach into other features

- **Headers on the AI's calls.** A plugin for an access gateway in front of the model server adds headers to the AI service's calls (Administration > AI). Set its host and secrets on the Plugins screen, then select Test connection on Administration > AI. If the plugin fails, the AI's calls are refused, not sent without the headers: the test says "A plugin that adds to the AI's requests failed".
- **A virus scanner for imports.** Every file of an AI import is given to each scanner plugin first. A file it flags is quarantined (the draft says Blocked; the audit log has `import.quarantine`). A scanner that fails blocks that file. Attachments and images added to pages directly are not scanned.
- **File kinds for imports.** A plugin may read file kinds the app does not read. Select it to see them under "Reads for import". A file of such a kind in an import is drafted by the plugin, not by the AI; the audit log has `import.claim`. While the plugin is disabled, the file fails as before: "This kind of file is not read; it is kept as an attachment."
- **The inventory.** A plugin's job may feed the inventory; a plugin may always edit it. A script feeding it with an API token needs a rule on Administration > Security > Inventory access.

## The upload size limit

The web server limits the size of a request before the app sees it (IIS: 30,000,000 bytes unless `web.config` says otherwise).
A larger package answers "Larger than the web server allows."

## More than one server

Servers that share a database share the plugins. A change made on one server restarts that server at once.
The others see the change within a short time and restart themselves.

## Jobs

A plugin's jobs run on one server at a time, on the schedule its manifest gives. A job that fails, or runs past its interval (five minutes at least), counts as a failure of its plugin.
Select a plugin to see its health checks and its jobs (last run, next run, last error) in the right panel.

## The audit log

| Entry | When |
|---|---|
| `plugin.install`, `plugin.update` | A package was uploaded. Carries the version, the source and the SHA-256. |
| `plugin.enable`, `plugin.disable` | An Admin changed the state. |
| `plugin.disable.core` | The app disabled it. Carries the reason. |
| `plugin.remove` | A plugin was removed. |
| `plugin.data.delete` | Its tables were dropped: with the removal ("Delete data" ticked), or later from the list. |
| `plugin.lock.release` | |
| `plugin.settings.change` | Carries the keys that changed. |
| `plugin.permissions.change` | The minimum role of a space permission was changed. Carries the keys and roles. |
| `user.permissions.set` | A global permission was given to a person or taken back. |
