# Builds the UI and publishes the site (Release) to artifacts\publish for tools\deploy-iis.ps1. No admin needed.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
Push-Location (Join-Path $root 'client')
try {
    if (-not (Test-Path node_modules)) { npm ci --no-fund }
    npx vite build
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
} finally { Pop-Location }
$out = Join-Path $root 'artifacts\publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish (Join-Path $root 'src\Kb.Web') -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
# Development settings and secrets never ship.
Remove-Item (Join-Path $out 'appsettings.Development.json') -ErrorAction SilentlyContinue
