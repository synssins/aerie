# Plugin admin guide

Plugins are managed on Administration > Plugins, by an Admin. This guide covers what is built today.

Related: [developer guide](developer-guide.md), [manifest reference](manifest-reference.md).

## Before you install one

A plugin runs inside the app with the app's rights. Install a plugin only from a publisher you trust as much as the app itself.
The app loads any package an Admin uploads. It records each install in the audit log with the package's SHA-256.

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
- Box on: the plugin's tables are dropped. This cannot be undone.

For a plugin listed as "Removed, data kept", select it and select Delete data.

## Settings and secrets

Select a plugin to see its settings. A secret that is set shows as dots. Its value is never loaded into the browser; type a new value to replace it.
A change applies at once, unless the setting needs a restart.
The audit log records which keys changed, never a value.

## Disabled by core

The app turns a plugin off by itself when the plugin cannot start, or when it fails five times in ten minutes. The screen shows the reason.
Plugins that require it are turned off with it.

To put it back: correct the cause (a setting, the remote system, a new version of the plugin), then select Enable.

## Locked

A plugin marked as enforcing guards a rule. When the app has to disable such a plugin, the actions it guarded are locked:
everyone but an Admin gets "<plugin name> is disabled." for them. Sign-in is never locked, and an Admin can still do everything.

Select Release lock when you accept that the rule is not enforced for now. Enabling the plugin again also ends the lock.

## Safe Mode

When a plugin stops the app from starting, set `Kb:Plugins:SafeMode` to `true` in the app's configuration and start the app.
In Safe Mode no uploaded plugin is loaded, no state is changed and locks are ignored. Remove or disable the plugin, then switch Safe Mode off.

## The upload size limit

The web server limits the size of a request before the app sees it (IIS: 30,000,000 bytes unless `web.config` says otherwise).
A larger package answers "Larger than the web server allows."

## More than one server

Servers that share a database share the plugins. A change made on one server restarts that server at once.
The others see the change within a short time and restart themselves.

## Jobs

A plugin's jobs run on one server at a time, on the schedule its manifest gives. A job that fails counts as a failure of its plugin.

## The audit log

| Entry | When |
|---|---|
| `plugin.install`, `plugin.update` | A package was uploaded. Carries the version, the source and the SHA-256. |
| `plugin.enable`, `plugin.disable` | An Admin changed the state. |
| `plugin.disable.core` | The app disabled it. Carries the reason. |
| `plugin.remove`, `plugin.data.delete` | |
| `plugin.lock.release` | |
| `plugin.settings.change` | Carries the keys that changed. |
