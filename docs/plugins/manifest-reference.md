# Manifest reference

`kb-plugin.json` sits at the top of the package. Property names are read without regard to case; the keys of settings are case-sensitive. This file lists every property the app reads.

Related: [developer guide](developer-guide.md), [admin guide](admin-guide.md).

## Top level

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | text | required | Lowercase letters and digits joined by single dots or hyphens, 3 to 64 characters. It names the plugin everywhere: its routes, its table prefix, its settings. |
| `version` | text | required | Three numbers: `1.0.0`. |
| `publisher` | text | required | Who made it. Shown on the Plugins screen. |
| `name` | text | the id | The name people see. |
| `description` | text | none | One line about what it does. |
| `assembly` | text | `<id>.dll` | The file name of the main assembly, at the top of the package. |
| `minKbVersion` | text | `0.1.0` | The lowest app version it runs on. |
| `abstractionsVersion` | text | `0.1.0` | The version of `Kb.Plugins.Abstractions` it was built against. |
| `uiApiVersion` | text | none | The version of the script API its script was written for: `1.0.0`. Needed when the plugin has a script. |
| `enforcing` | true or false | false | True when the plugin enforces a rule. If the core disables it, the actions it guarded stay locked until an Admin releases the lock. |
| `requires` | list | empty | The plugins it needs. See below. |
| `exports` | list of text | empty | The assemblies it shares with the plugins that require it, by assembly name without `.dll` (`My.Lib`). |
| `extensionPoints` | list of text | empty | The extension points it supplies, for example `email-sender` or `audit-sink`. Shown on the Plugins screen. |
| `outboundHosts` | list of text | empty | The only hosts its `HttpClient` may call. |
| `settings` | list | empty | The settings an Admin sets in the app. See below. |
| `permissions` | list | empty | The permissions it declares. See below. |
| `schedules` | list | empty | The jobs it runs. See below. |
| `ui` | object | none | Its script for the browser. See below. |

## requires

| Property | Type | Meaning |
|---|---|---|
| `id` | text | The id of the plugin it needs. |
| `minVersion` | text | The lowest version of that plugin. |

## settings

| Property | Type | Default | Meaning |
|---|---|---|---|
| `key` | text | required | The name the plugin reads the setting by. Case-sensitive. |
| `label` | text | required | The label on the Plugins screen. |
| `type` | text | `text` | `text`, `secret`, `number`, `toggle`, `select` or `multiline`. A toggle is stored as `true` or `false`. |
| `options` | list of text | none | The choices of a `select`. |
| `secret` | true or false | false | Stored protected, never sent back to the browser, never logged. |
| `required` | true or false | false | Saving the settings answers "Required." while it is empty. |
| `restart` | true or false | false | A change restarts the app. Use it for a setting the plugin reads only while it is configured. |

## permissions

The plugin asks whether its caller holds one with `IAccess.CanAsync(actor, "<plugin id>:<key>", spaceId)`.

| Property | Type | Default | Meaning |
|---|---|---|---|
| `key` | text | required | The name inside the plugin. The full name is `<plugin id>:<key>`. |
| `label` | text | required | Shown on the Plugins screen; for a global permission, the heading of its column in the Users table. |
| `scope` | text | required | `space`: held from a minimum role up, in each space. `global`: given to people one by one by an Admin; Admins hold it. |
| `defaultRole` | text | `Owner` | For a space permission, the minimum role until an Admin changes it: `Reader`, `Contributor`, `Supervisor` or `Owner`. |

## schedules

| Property | Type | Meaning |
|---|---|---|
| `name` | text | The name of a job the plugin registers with `app.Job<T>(name)`. 1 to 100 characters. |
| `everyMinutes` | number | 1 or more. Counted from the end of the last run. A run is given up after this long, five minutes at least. |

## ui

| Property | Type | Meaning |
|---|---|---|
| `script` | text | The plugin's script for the browser: an ES module inside the package's `ui/` folder, for example `ui/index.js`. |
| `replaces` | list of text | Not used yet. |

## What refuses an install

The upload answers with one of these, and nothing is stored.

| Message | Cause |
|---|---|
| Not a plugin package. | Not a zip; no `kb-plugin.json` at the top; a manifest that is not valid JSON, or has `null` where a list, a name or an item must be; a file path that leaves the package. |
| Package too large. | More than 2000 files, or more than 200 MB unpacked. |
| Larger than the web server allows. | The web server's own request size limit, which comes first. |
| Plugin id is not valid. | See `id`. |
| Version is not valid. | See `version`. |
| Missing <file>. | The main assembly is not at the top of the package. |
| Built-in plugins cannot be replaced. | The id is that of a plugin shipped with the app. |
| Version <version> is already installed. | Raise the version. |
| A newer version is installed. | An update must be higher. To go back, remove the plugin and upload the older package. |
| Built for a newer plugin API (<version>). | `abstractionsVersion` is newer than the app's. |
| Needs version <version> or later. | `minKbVersion` is newer than the app. |
| Requires <plugin> <version> or later. | A required plugin is not installed, or is too old. |
| Conflicts with <id>. | Two ids give the same table prefix (`a.b` and `a-b`). |

## What disables a plugin at start

The app starts without the plugin and shows the reason on the Plugins screen.

| Reason | Cause |
|---|---|
| The message of the exception | `ConfigureServices` or `Configure` threw. |
| Hosted services are not supported. | The plugin registered a hosted service. |
| No job named <name>. | A schedule has no job. |
| No schedule named <name>. | A job has no schedule. |
| Schedule <name>: everyMinutes must be 1 or more. | |
| A schedule name is 1 to 100 characters. | |
| <action> is not open to plugins. | It registered a hook, a filter, an after handler or a handler on a core-only action. |
| <action> already has a handler. | It registered a handler for an action of the core or of another plugin. |
| <type> is a node of the editor. | It registered a renderer for a node type the editor has by itself. |
| <type> already has a renderer. | It registered a renderer for a node type another plugin renders, or twice. |
| An editor node type is 1 to 100 characters. | |
| Its tables changed. A plugin that changes its tables needs migrations. | A plugin without migrations has some of its tables and misses others. |
| Requires <plugin>. | A required plugin is disabled or failed. |
| Circular requirement. | |
| The package is missing. | The stored package is gone. Install the plugin again. |
