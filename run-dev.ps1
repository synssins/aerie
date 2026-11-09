# Builds the UI and starts the site at https://localhost:7292 (Development: SQLite, no SQL install needed).
# First run writes the break-glass password to src\Kb.Web\App_Data\breakglass-initial-password.txt.
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
Push-Location (Join-Path $PSScriptRoot 'client')
try {
    if (-not (Test-Path node_modules)) { npm ci --no-fund }
    npx vite build
} finally { Pop-Location }
& $dotnet run --project (Join-Path $PSScriptRoot 'src\Kb.Web') --launch-profile https
