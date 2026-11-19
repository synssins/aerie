# Licensing

Aerie is source-available software. You may read the source, and you may use, change and share it for noncommercial purposes. It is not open source as the Open Source Initiative defines the term.

This file says in plain words which license covers what. It is a summary: the license texts themselves are what bind.

## The app: PolyForm Noncommercial 1.0.0

Everything in this repository after the tag `last-apache-2.0` is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE), except the folders and the file listed under "What stays under Apache-2.0" below.

Under that license Aerie is free for personal and noncommercial use, as the license defines them:

- Any noncommercial purpose.
- Personal use: study, research, hobby projects, private use.
- Use by a charitable organization, an educational institution, a public research organization, a public safety or health organization, an environmental protection organization or a government institution.

The copyright holder and licensor is Christopher Kliewer.

## Commercial use

Any other use is commercial use and needs a commercial license from the copyright holder. That includes running Aerie inside a for-profit company.

To ask for a commercial license, go to https://aeriekb.com or open an issue at https://github.com/synssins/aerie/issues.

## Versions up to the tag `last-apache-2.0`

Every version of Aerie up to and including the commit tagged `last-apache-2.0` was published under the Apache License, Version 2.0, and remains available under that license. The change of license applies to what was published after that tag.

## What stays under Apache-2.0

The plugin contract stays under the Apache License, Version 2.0, so that anyone can write a plugin for Aerie. Each of these folders carries its own `LICENSE` file:

| Path | What |
|---|---|
| `src/Kb.Plugins.Abstractions` | The contract a plugin compiles against |
| `src/Kb.Plugins.Data` | The data contract for plugins with their own tables |
| `plugins/Kb.Plugin.Sample` | The sample plugin |
| `docs/plugins` | The plugin guides |
| `contracts` | The API contracts, as released by the `aerie-spec` repository |

One single file is under Apache-2.0 as well: `tools/pack-plugin.ps1`, the script that packs a plugin.

The other plugins in `plugins/` are part of the app and are under the PolyForm Noncommercial License 1.0.0.

## Plugins

A plugin that works with Aerie only through the published plugin contract (the folders above and the script API the plugin guides describe) is not treated by the copyright holder as a work based on Aerie. Its author may put it under any license and may sell it.

A plugin's license does not give its user a license for Aerie. A company that uses a plugin still needs a commercial license for the app.

## Third-party components

Every bundled third-party component keeps its own license. [NOTICE](NOTICE) lists them and says where their license texts are. Packages that the build downloads are recorded in the lock files, each under its own license.

## Name and logo

The name "Aerie" and the Aerie logo are not licensed by either license. Neither license gives a right to use them, except to say truthfully where the software came from.
