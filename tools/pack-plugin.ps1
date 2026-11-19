# Builds a plugin project and packs it for upload on Administration > Plugins.
# License: Apache-2.0 (this file; see LICENSING.md).
#   tools\pack-plugin.ps1 plugins\Kb.Plugin.Sample
# Writes artifacts\plugins\<id>-<version>.kbplugin: a zip of the published output with kb-plugin.json at the top.
# The manifest is taken from the project folder, because a plugin may embed it and not copy it to its output.
param([Parameter(Mandatory = $true)][string]$Project)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
$manifestPath = Join-Path $dir 'kb-plugin.json'
if (-not (Test-Path $manifestPath)) { throw "No kb-plugin.json in $dir." }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.id -or -not $manifest.version) { throw 'kb-plugin.json needs an id and a version.' }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$out = Join-Path $root "artifacts\plugins\build\$($manifest.id)"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish $dir -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item $manifestPath (Join-Path $out 'kb-plugin.json') -Force

$package = Join-Path $root "artifacts\plugins\$($manifest.id)-$($manifest.version).kbplugin"
if (Test-Path $package) { Remove-Item $package -Force }
# Entry names use "/", as the zip format says; Windows PowerShell's CreateFromDirectory writes "\".
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($package, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $out -Recurse -File) {
        $name = $file.FullName.Substring($out.Length).TrimStart([char]92).Replace([string][char]92, '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name)
    }
} finally { $zip.Dispose() }
Write-Host $package
