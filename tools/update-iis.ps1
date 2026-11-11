# Pushes a new build (artifacts\publish) to the local IIS site without admin rights.
# Needs one prior run of deploy-iis.ps1 (elevated), which grants the installing user write access to the site folder.
# IIS serves app_offline.htm while files are copied; removing it restarts the app (migrations run on start).
param([string]$SitePath = 'C:\inetpub\aerie', [int]$Port = 443)
$ErrorActionPreference = 'Stop'
$publish = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\publish'
if (-not (Test-Path (Join-Path $publish 'Kb.Web.dll'))) { throw "No build in $publish. Run tools\publish.ps1 first." }
$offline = Join-Path $SitePath 'app_offline.htm'
Set-Content -Path $offline -Value '<!doctype html><title>Updating</title><p>Updating. Back in a moment.</p>' -Encoding utf8
try {
    Start-Sleep -Seconds 2   # let the app shut down and release its files
    robocopy $publish $SitePath /MIR /XD App_Data logs /XF app_offline.htm /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
} finally {
    Remove-Item $offline -ErrorAction SilentlyContinue
}
$code = ''
for ($i = 0; $i -lt 30 -and $code -ne '200'; $i++) { Start-Sleep 2; $code = & curl.exe -sk -o NUL -w '%{http_code}' "https://localhost:$Port/health/ready" }
Write-Host "health/ready: $code"
