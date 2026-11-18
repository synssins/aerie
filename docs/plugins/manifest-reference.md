# Manifest reference

`kb-plugin.json` sits at the top of the package. Property names are read without regard to case; the keys of settings are case-sensitive. This file lists every property the app reads.

Related: [building and packaging](packaging.md) (the package around the manifest, and a complete manifest), [developer guide](developer-guide.md), [admin guide](admin-guide.md).

A list may be left out (it is then empty) but may not be `null`, and no item of a list may be `null`: either refuses the install with "Not a plugin package.". Properties the app does not know are ignored.

## Top level

| Property | Type | Default | Meaning |
|---|---|---|---|
| `id` | text | required | Lowercase letters and digits joined by single dots or hyphens, 3 to 64 characters. It names the plugin everywhere: its routes, its table prefix, its settings. |
| `version` | text | required | Three numbers: `1.0.0`. No prefix or suffix. |
| `publisher` | text | required | Who made it and stands behind it: your organisation or your name as people know it (`Example Ltd`, not an email address). Shown on the Plugins screen, beside the id. |
| `name` | text | the id | The name people see. |
| `description` | text | none | One line about what it does. |
| `assembly` | text | `<id>.dll` | The file name of the main assembly, at the top of the package. A file name only: no `/`, `\` or `:`. |
| `minKbVersion` | text | `0.1.0` | The lowest app version it runs on. The app is at 0.9.0. |
| `abstractionsVersion` | text | `0.1.0` | The version of `Kb.Plugins.Abstractions` it was built against. The app's is 0.9.0; a plugin fits when its major number is the app's and it is not higher. |
| `uiApiVersion` | text | none | The version of the script API its script was written for: `1.0.0`, or `1.1.0` for a script that adds panels to the plugin's page or uploads with `kb.api.upload`. The app's is 1.1.0. Needed when the plugin has a script: without it the install is refused. |
| `enforcing` | true or false | false | True when the plugin enforces a rule. If the core disables it, the actions it guarded stay locked until an Admin releases the lock. |
| `requires` | list | empty | The plugins it needs. See below. |
| `exports` | list of text | empty | The assemblies it shares with the plugins that require it, by assembly name without `.dll` (`My.Lib`). |
| `extensionPoints` | list of text | empty | The extension points it supplies, for example `email-sender`, `audit-sink`, `malware-scanner`, `outbound-request-decorator`. Shown on the Plugins screen; the app does not act on it. The services the plugin registers are what count. |
| `outboundHosts` | list of text | empty | The only hosts the plugin's own `HttpClient` may call (host names, no scheme or path). It does not apply to the core's calls a plugin decorates. A [sign-in method](developer-guide.md#sign-in-methods)'s provider is reached through it too: list the hosts of the issuer, the token endpoint and the keys. |
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
| `key` | text | required | The name the plugin reads the setting by. Case-sensitive. A key starting with `signIn.` (any case) is the app's: it holds a sign-in method's client id and secret. |
| `label` | text | required | The label on the Plugins screen. |
| `type` | text | `text` | `text`, `secret`, `number`, `toggle`, `select` or `multiline`. A toggle is stored as `true` or `false`. Every value reaches the plugin as text. `secret` is the same as `"secret": true`. A `multiline` value arrives with LF (`\n`) line ends, whatever the browser or an API caller sent. |
| `options` | list of text | none | The choices of a `select`. |
| `optionLabels` | object | none | For a `select`: what the list shows for a stored value, `{ "sq": "Square" }`. An option without one shows its value. A label for a value that is not in `options` is refused at install. |
| `help` | text | none | A sentence shown under the field. |
| `default` | text | none | What `GetSetting` returns while nothing is saved, shown in the empty field (a toggle shows it as its state, a select names it). It must fit the setting's type and bounds; a secret has none. A `required` setting with a default counts as set. |
| `unit` | text | none | Shown after the field: `MB`, `days`. Named in the refusal of a number out of bounds ("At most 500 MB."). |
| `min` | number | none | For a `number`: the lowest value an Admin may save. |
| `max` | number | none | For a `number`: the highest value an Admin may save. Not lower than `min`. |
| `secret` | true or false | false | Stored protected, never sent back to the browser, never logged. Read with `GetSecretAsync`, after start; `GetSetting` answers null for it. |
| `required` | true or false | false | Saving the settings answers "Required." while it is empty. A secret that is already stored counts as set: saving the other settings keeps it and passes. Clearing it answers "Required.". |
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
| A plugin with a script needs uiApiVersion. | `ui.script` is set and `uiApiVersion` is not. |
| Setting KeepDays: min is more than max. | A number's `min` is above its `max`. |
| Setting KeepDays: min and max are for a number. | `min` or `max` on a setting whose type is not `number`. |
| Setting KeepDays: the default is refused: At most 3650 days. | The `default` does not fit the setting's type, options or bounds. |
| Setting Token: a secret has no default. | A secret has a `default`. |
| Setting Shape: optionLabels names "x", which is not one of the options. | A label for a value that is not in `options`. |
| Setting signIn.x: keys starting with "signIn." are the app's. | A key the app keeps for sign-in methods (from 0.8.0). |
| Permission <key>: scope is space or global. | The names are lower case. |
| Permission <key>: defaultRole is Reader, Contributor, Supervisor or Owner. | A space permission with another default role. The names are case-sensitive. |
| Permission <key> is listed twice. | |
| A permission key is 1 to 100 characters, with no colon. | |
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
