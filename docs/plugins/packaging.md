# Building and packaging a plugin

How to build a plugin outside this repository, what goes in the package, and what the app checks when an Admin uploads it.
Every step here was run as written, against app version 0.3.0; 0.4.0 to 0.10.0 changed none of them.

Related: [developer guide](developer-guide.md), [manifest reference](manifest-reference.md), [example: an outbound-headers plugin](example-outbound-headers.md), [admin guide](admin-guide.md).

## What you need

- The .NET 10 SDK. A plugin targets `net10.0`, the app's own framework.
- `Kb.Plugins.Abstractions`: the contract, the one assembly of the app a plugin compiles against. There is no NuGet package, and the repository has no release tags yet. Take the DLL the app itself runs, or build it from the repository. One of:
  - **From the installed app.** Every installation has the exact contract it runs, `Kb.Plugins.Abstractions.dll`, in the app's folder: `/app/` in the container image (copy it out of a running container), the site's folder on IIS. `Kb.Plugins.Data.dll` is beside it. This is the surest way to match the app you install on.
  - **Project reference.** Clone the repository and reference `src/Kb.Plugins.Abstractions/Kb.Plugins.Abstractions.csproj`. This is how the plugins in `plugins/` are built.
  - **DLL reference.** Build the contract once and reference the DLL:

    ```
    git clone https://github.com/synssins/aerie.git
    dotnet build aerie/src/Kb.Plugins.Abstractions -c Release -o lib
    ```

    `lib/Kb.Plugins.Abstractions.dll` is the file to reference. Built from `main`, it is the newest contract, which may have members the app you run does not have yet.
- `Kb.Plugins.Data` as well, only when the plugin has tables of its own (`src/Kb.Plugins.Data`). It brings Entity Framework Core.
- Reference nothing else of the app. `Kb.Core`, `Kb.Data` and `Kb.Web` are not part of the contract and change without notice.

## The project file

A plugin that references the contract as a DLL:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- The main assembly's file name is the manifest's "assembly". -->
    <AssemblyName>Example.GatewayHeaders</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- The app's ASP.NET Core: the same one the host runs. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <!-- The contract. Private=false: the host's own copy is used, so it is not shipped. -->
    <Reference Include="Kb.Plugins.Abstractions">
      <HintPath>..\lib\Kb.Plugins.Abstractions.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <None Include="kb-plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

With a project reference instead, as the sample does:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\Kb.Plugins.Abstractions\Kb.Plugins.Abstractions.csproj" />
  <ProjectReference Include="..\..\src\Kb.Plugins.Data\Kb.Plugins.Data.csproj" />  <!-- only with tables -->
  <None Include="kb-plugin.json" CopyToOutputDirectory="PreserveNewest" />
  <None Include="ui\**" CopyToOutputDirectory="PreserveNewest" />                   <!-- only with a script -->
</ItemGroup>
```

### What not to ship

The app loads a plugin's assemblies in a load context of its own, and resolves every assembly name in this order:

1. the host's copy, when the host has an assembly of that name: .NET, ASP.NET Core, Entity Framework Core and every `Kb.*` assembly;
2. an assembly a required plugin shares (its `exports`);
3. the plugin's own folder.

So a copy of a host assembly in the package is never loaded. It does no harm, but it is dead weight, and a different version of it is ignored, not used.
Leave them out: `Kb.Plugins.Abstractions.dll`, `Kb.Plugins.Data.dll`, `Microsoft.EntityFrameworkCore*.dll`, `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `System.*`.
`<Private>false</Private>` on a reference keeps it out of the build output. The sample ships them; its package works all the same.

Ship your own assembly, its `.pdb` (a stack trace then names lines), its `.deps.json`, and every third-party library it uses that the host does not have.

## The package

A package is a zip archive (the ZIP format), and nothing else: a tar, tar.gz or 7z archive is refused ("Not a plugin package."), whatever its name. Name it `<id>-<version>.kbplugin`. The upload screen offers `.kbplugin` and `.zip` files, and the app reads the content, not the name: a `.zip` of the same content installs the same way.

```
example.gateway-headers-1.0.0.kbplugin
├── kb-plugin.json                    required, at the top
├── Example.GatewayHeaders.dll        required: the main assembly, at the top
├── Example.GatewayHeaders.deps.json  optional
├── Example.GatewayHeaders.pdb        optional
├── SomeLibrary.dll                   optional: libraries the host does not have, at the top
└── ui/                               optional: the browser script and its files
    ├── index.js
    └── notice.css
```

- `kb-plugin.json` is at the top of the zip, not inside a folder. The zip holds the files of the build output, not the folder that holds them.
- The main assembly is at the top, named by the manifest's `assembly` (default `<id>.dll`). Its file name has no `/`, `\` or `:`.
- Libraries are at the top too: an assembly is found as `<assembly name>.dll` in the plugin's folder.
- `ui/` holds the browser script and the files it loads. Only these types are served from it: `.js`, `.css`, `.json`, `.map`, `.svg`, `.png`, `.jpg`, `.webp`, `.woff2`. Anything else answers 404, `.mjs` and `.jpeg` included: bundle a library shipped as `.mjs` modules into `.js` files, or use its single-file browser build ([developer guide](developer-guide.md#screens)).
- Entry names use `/` between folders, as the zip format says. No entry name is absolute, holds `..`, or has a `:`.
- Folders may be stored as entries of their own (`ui/`) or not.
- Names are compared without regard to case.

The complete manifest of this package:

```json
{
  "id": "example.gateway-headers",
  "name": "Gateway headers for the AI server",
  "version": "1.0.0",
  "publisher": "Example Ltd",
  "description": "Adds two secret headers to the AI service's calls to one host.",
  "assembly": "Example.GatewayHeaders.dll",
  "minKbVersion": "0.10.0",
  "abstractionsVersion": "0.10.0",
  "extensionPoints": ["outbound-request-decorator"],
  "settings": [
    { "key": "Host", "label": "Host the headers are sent to", "type": "text", "required": true },
    { "key": "Header1Name", "label": "First header", "type": "text", "required": true },
    { "key": "Header1Value", "label": "First header's value", "type": "secret", "secret": true, "required": true },
    { "key": "Header2Name", "label": "Second header", "type": "text" },
    { "key": "Header2Value", "label": "Second header's value", "type": "secret", "secret": true }
  ]
}
```

The [manifest reference](manifest-reference.md) lists every property. Its code is in [the example](example-outbound-headers.md).

## Names

| What | Rule |
|---|---|
| Plugin id | 3 to 64 characters: lowercase letters and digits, in parts joined by a single `.` or `-` (`acme.ticket-sync`). It cannot start or end with `.` or `-`. It names the plugin's routes (`/api/v1/plugins/<id>/`), its tables, its settings and its permissions. It never changes. Start it with your organisation's name. |
| Version | Three numbers: `1.0.0`. No `v`, no suffix (`1.0.0-beta` is refused). |
| Table prefix | `plg_<id>__`, with the id's dots and hyphens as underscores: `acme.ticket-sync` gives `plg_acme_ticket_sync__`. Two ids that give the same prefix (`a.b`, `a-b`) cannot both be installed. |
| Action name | `<your prefix>.<what>` (`acme.ticket.open`). One name, one action, across the app and every plugin. |
| Permission | `<plugin id>:<key>`. The key is 1 to 100 characters, with no colon. |
| Setting key | Case-sensitive, as the plugin reads it. |
| Editor node type | 1 to 100 characters. Choose one no other plugin uses (`acmeTicket`). |
| Ids taken | The plugins built into the app: `kb.email.smtp`, `kb.email.graph`. |

## Build the package

### With the repository's script (Windows)

```powershell
tools\pack-plugin.ps1 path\to\YourPlugin
```

It publishes the project in Release, copies `kb-plugin.json` beside the output, and writes `artifacts\plugins\<id>-<version>.kbplugin` with `/` in its entry names.

### By hand, on Windows (PowerShell or cmd)

```powershell
dotnet publish YourPlugin -c Release -o out
cd out
tar.exe -a -c -f ..\your.id-1.0.0.zip *
cd ..
ren your.id-1.0.0.zip your.id-1.0.0.kbplugin
```

The rename is optional: the upload screen takes the `.zip` too.

`tar.exe` comes with Windows 10 and later; `-a` with a `.zip` name writes a zip with `/` in its names.
Do not use `Compress-Archive` in Windows PowerShell 5.1: it writes `\` in entry names, and on a Linux server the files of `ui/` are not found.

### By hand, on Linux or macOS

```bash
dotnet publish YourPlugin -c Release -o out
cd out && zip -r ../your.id-1.0.0.kbplugin . && cd ..
```

Check the result: the first names must be `kb-plugin.json` and your assembly, not `out/...`.

```bash
unzip -l your.id-1.0.0.kbplugin
```

## Install

1. Sign in as an Admin. Open Administration > Plugins and select Upload.
2. Choose the `.kbplugin` file. The app checks it (below); a package that is refused changes nothing, and the screen says why.
3. The app stores the package in its database and restarts itself. The screen shows "Restarting" and comes back.
4. The plugin is listed as Enabled. Select it to set its settings.

An update is a package with the same id and a higher version. Settings, data and the plugin's state are kept.

The same upload is `POST /api/v1/admin/plugins` with the package as the raw body, from an Admin's signed-in session. API tokens cannot administer plugins.

## What the app checks

In this order, when the package is uploaded. Nothing is written until every check has passed.

1. **The web server's size limit.** IIS refuses a request over 30,000,000 bytes unless `web.config` raises it: "Larger than the web server allows."
2. **The zip.** It must be a zip ("Not a plugin package."), have at most 2000 entries, and at most 200 MB unpacked ("Package too large."). The unpacked size is counted again as the files are written, so a zip that lies about its sizes is stopped.
3. **Every name.** No absolute path, no `..`, no `:` ("Not a plugin package.").
4. **The manifest.** `kb-plugin.json` at the top, valid JSON, no `null` where a list, a name or an item must be ("Not a plugin package."). Then the id, the version and the main assembly.
5. **The plugin's fit.** Built-in ids, the installed version, `abstractionsVersion`, `minKbVersion`, `uiApiVersion` with a script, permissions, requirements, table prefixes.

The [manifest reference](manifest-reference.md#what-refuses-an-install) lists every message.

- **Hash.** The app records the SHA-256 of the package in the audit log (`plugin.install`, `plugin.update`). Compare it with the hash of the file you built (`sha256sum`, `Get-FileHash`).
- **Signing.** Packages are not signed and the app checks no signature. An Admin decides whom to trust; the hash in the audit log says what was installed.
- **Then, at start.** The app loads the plugin after the restart. A plugin that installs can still be disabled at start ("Disabled by core"), for example when `Configure` throws. The [manifest reference](manifest-reference.md#what-disables-a-plugin-at-start) lists the reasons; the Plugins screen shows the one that applied.

## Versions

| Field | Meaning |
|---|---|
| `version` | Your plugin's version. An update must be higher than the installed one. To go back, remove the plugin (keep its data) and upload the older package. |
| `abstractionsVersion` | The contract version you built against. The app's is **0.10.0**. Before 1.0.0 a plugin fits when its major number is the app's and it is not higher: a plugin built for 0.1.0 to 0.10.0 runs on 0.10.0. |
| `minKbVersion` | The lowest app version you run on. The app is **0.10.0**. |
| `uiApiVersion` | The browser script API, **1.1.0** (1.0.0 scripts run as they did). Needed when the plugin has a script. |

What came with 0.10.0: diagrams the AI reads in an import's pictures, handed to draft contributors as data, and a place right after a picture ([developer guide](developer-guide.md#diagrams-in-the-pictures-of-an-import)). A plugin that uses them says `"abstractionsVersion": "0.10.0"`.
What came with 0.9.0: sign-in providers an Admin adds on the plugin's page, each its own button, read from settings at the time of use ([developer guide](developer-guide.md#sign-in-providers-an-admin-adds)). Their hosts come from what the Admin entered and the provider's discovery document, not from `outboundHosts`. A plugin that offers them says `"abstractionsVersion": "0.9.0"`.
What came with 0.8.0: sign-in methods, a button on the sign-in screen for an OpenID Connect provider, with the sign-in run by the app ([developer guide](developer-guide.md#sign-in-methods)). The provider's hosts go in `outboundHosts`. A plugin that uses them says `"abstractionsVersion": "0.8.0"`. Setting keys starting with `signIn.` are the app's from 0.8.0 on, and a manifest that declares one is refused.
What came with 0.7.0: a setting's help, default, unit, bounds and option labels, and the plugin's own page in Administration > Plugins, where its script adds panels and uploads files to its routes ([developer guide](developer-guide.md#panels-on-the-plugins-own-page)). A plugin that uses them says `"abstractionsVersion": "0.7.0"`, and `"uiApiVersion": "1.1.0"` for the script.
What came with 0.6.0: file kinds a plugin claims, so a file of a kind the app does not read reaches the plugin's import contributor ([developer guide](developer-guide.md#file-kinds-the-app-does-not-read)). A plugin that uses them says `"abstractionsVersion": "0.6.0"`.
What came with 0.5.0: draft contributors, which read an import's draft and say where their nodes go ([developer guide](developer-guide.md#adding-to-the-draft-from-its-text-draft-contributors)). A plugin that uses them says `"abstractionsVersion": "0.5.0"`.
What came with 0.4.0: import contributors, `DocPicture` in exports, `UploadContent.FromBytes` and `FromStream`, `ActorSources` and `Actor.ImportItemId` ([developer guide](developer-guide.md#versions)). A plugin that uses them says `"abstractionsVersion": "0.4.0"`.
What came with 0.3.0: `IOutboundRequestDecorator` (headers on the AI service's calls), the inventory sync action and its records, page details on `PageDto`. A plugin that uses them says `"abstractionsVersion": "0.3.0"`, so an older app refuses it instead of failing at start.
Editor blocks and the browser script came while the contract was still numbered 0.2.0: a plugin that uses them says `"abstractionsVersion": "0.3.0"` too.

The contract can still change before 1.0.0, and the app checks only the number: two builds of the app at 0.3.0 may differ in what the contract has. In practice:

1. Compile against the `Kb.Plugins.Abstractions.dll` taken from the installation you will install on ([above](#what-you-need)). A member your plugin uses that the app does not have is then a compile error, not a failure at start.
2. Set `abstractionsVersion` and `minKbVersion` to that app's version, **0.10.0** today.
3. After the app is updated, build again against the new DLL. A plugin that no longer fits is "Disabled by core" at start, with the reason on the Plugins screen.

## Check a package with the app's tests

The repository has a test that installs a package the way the Plugins screen does, restarts, checks the plugin loaded, and removes it, all in a throwaway database:

```bash
KB_PLUGIN_PACKAGE=path/to/your.id-1.0.0.kbplugin dotnet test tests/Kb.Tests.Integration --filter "FullyQualifiedName~PackageFileTests"
```

On Windows PowerShell: `$env:KB_PLUGIN_PACKAGE = "path\to\your.id-1.0.0.kbplugin"` first. Without the variable the test does nothing.
