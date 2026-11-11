<#
.SYNOPSIS
  Deploys Aerie to local IIS for testing. Run elevated. Safe to re-run (updates in place).

.DESCRIPTION
  Expects a published build in artifacts\publish (tools\publish.ps1 makes it).
  - App pool "Aerie": no managed code, ApplicationPoolIdentity, ASPNETCORE_ENVIRONMENT=Production.
  - Site "Aerie" on https://*:443 (and http://*:80, which only redirects to https) with a self-signed
    certificate for localhost and this computer's name. Stops IIS's empty "Default Web Site" if it holds port 80.
  - SQL Express database "Kb"; the pool identity gets db_owner (migrations run at startup).
  - First admin: pass -AdminUser/-AdminPassword to create it at first start, or leave them off and the
    site shows a first-run setup page (open https://localhost on this server; no code needed locally).

.PARAMETER AdminUser
  First administrator's user name (used only when no admin exists yet).
.PARAMETER AdminPassword
  First administrator's password (15+ characters).
.PARAMETER BreakGlassPassword
  Optional. Creates the emergency break-glass account with this password (only if it doesn't exist yet).
.PARAMETER TrustCert
  Also add the self-signed certificate to Trusted Root (removes the browser warning on this PC only).
#>
param(
    [int]$Port = 443,
    [int]$HttpPort = 80,
    [string]$SiteName = 'Aerie',
    [string]$SitePath = 'C:\inetpub\aerie',
    [string]$SqlInstance = '.\SQLEXPRESS',
    [string]$Database = 'Kb',
    [string]$AdminUser = '',
    [string]$AdminPassword = '',
    [string]$BreakGlassPassword = '',
    [switch]$TrustCert
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
$appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
$log = Join-Path $PSScriptRoot 'deploy-iis.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script elevated.' }
    if (-not (Test-Path (Join-Path $publish 'Kb.Web.dll'))) { throw "No build in $publish. Run tools\publish.ps1 first." }
    $poolId = "IIS AppPool\$SiteName"

    Write-Host '== App pool =='
    $pools = & $appcmd list apppool /text:name
    if ($pools -notcontains $SiteName) { & $appcmd add apppool /name:$SiteName /managedRuntimeVersion:"" /managedPipelineMode:Integrated | Out-Null }
    & $appcmd set apppool $SiteName /processModel.identityType:ApplicationPoolIdentity /processModel.loadUserProfile:true /startMode:AlwaysRunning | Out-Null
    & $appcmd stop apppool $SiteName 2>$null | Out-Null

    Write-Host '== Settings =='
    function Set-PoolEnv([string]$name, [string]$value) {
        & $appcmd set config -section:system.applicationHost/applicationPools "/-[name='$SiteName'].environmentVariables.[name='$name']" /commit:apphost 2>$null | Out-Null
        if ($value) { & $appcmd set config -section:system.applicationHost/applicationPools "/+[name='$SiteName'].environmentVariables.[name='$name',value='$value']" /commit:apphost | Out-Null }
    }
    Set-PoolEnv 'ASPNETCORE_ENVIRONMENT' 'Production'
    # First-admin and break-glass values are read only when those accounts don't exist yet.
    Set-PoolEnv 'Kb__Setup__AdminUserName' $AdminUser
    Set-PoolEnv 'Kb--SetupAdminPassword' $AdminPassword
    Set-PoolEnv 'Kb--BreakGlassPassword' $BreakGlassPassword

    Write-Host '== Files =='
    New-Item -ItemType Directory -Force $SitePath, (Join-Path $SitePath 'App_Data') | Out-Null
    robocopy $publish $SitePath /MIR /XD App_Data /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    icacls $SitePath /grant "${poolId}:(OI)(CI)RX" | Out-Null
    # The installing user can push updates later without admin: tools/update-iis.ps1.
    icacls $SitePath /grant "$([Security.Principal.WindowsIdentity]::GetCurrent().Name):(OI)(CI)M" | Out-Null
    icacls (Join-Path $SitePath 'App_Data') /grant "${poolId}:(OI)(CI)M" | Out-Null

    Write-Host '== Certificate =='
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'Aerie local test' -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -DnsName 'localhost', $env:COMPUTERNAME, "$env:COMPUTERNAME.$env:USERDNSDOMAIN".TrimEnd('.') -CertStoreLocation Cert:\LocalMachine\My -FriendlyName 'Aerie local test' -NotAfter (Get-Date).AddYears(1) -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable
    }
    if ($TrustCert -and -not (Test-Path "Cert:\LocalMachine\Root\$($cert.Thumbprint)")) {
        $store = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine'); $store.Open('ReadWrite'); $store.Add($cert); $store.Close()
    }
    netsh http delete sslcert ipport=0.0.0.0:8443 2>$null | Out-Null   # earlier test port
    netsh http delete sslcert ipport=0.0.0.0:$Port 2>$null | Out-Null
    netsh http add sslcert ipport=0.0.0.0:$Port certhash=$($cert.Thumbprint) appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}' certstorename=MY | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'netsh sslcert binding failed' }

    Write-Host '== SQL Express =='
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=$SqlInstance;Database=master;Integrated Security=True;TrustServerCertificate=True"
    $cn.Open()
    $sql = @"
IF DB_ID(N'$Database') IS NULL CREATE DATABASE [$Database];
IF SUSER_ID(N'$poolId') IS NULL CREATE LOGIN [$poolId] FROM WINDOWS;
EXEC(N'USE [$Database]; IF USER_ID(N''$poolId'') IS NULL CREATE USER [$poolId] FOR LOGIN [$poolId]; ALTER ROLE db_owner ADD MEMBER [$poolId];');
"@
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; [void]$cmd.ExecuteNonQuery(); $cn.Close()

    Write-Host '== Site =='
    $sites = & $appcmd list site /text:name
    if ($sites -contains 'Default Web Site') {
        $dws = & $appcmd list site 'Default Web Site' /text:bindings
        if ($dws -match ":${HttpPort}:" -or $dws -match ":${Port}:") { & $appcmd stop site 'Default Web Site' | Out-Null; & $appcmd set site 'Default Web Site' /serverAutoStart:false | Out-Null; Write-Host 'Stopped Default Web Site (port conflict).' }
    }
    $bindings = "https/*:${Port}:,http/*:${HttpPort}:"
    # Re-create the site entry each run so bindings always match (config only; files and database are untouched).
    if ($sites -contains $SiteName) { & $appcmd delete site $SiteName | Out-Null }
    & $appcmd add site /name:$SiteName /physicalPath:$SitePath /bindings:$bindings | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'appcmd add site failed' }
    & $appcmd set site $SiteName /applicationDefaults.applicationPool:$SiteName /applicationDefaults.preloadEnabled:true | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'appcmd set site failed' }
    # Plain HTTP only redirects to HTTPS (UseHttpsRedirection + HSTS); the cookie is __Host- and Secure.

    Write-Host '== Start =='
    & $appcmd recycle apppool $SiteName 2>$null | Out-Null   # clears a cached failed start
    & $appcmd start apppool $SiteName 2>$null | Out-Null
    & $appcmd start site $SiteName 2>$null | Out-Null
    $code = ''
    for ($i = 0; $i -lt 30 -and $code -ne '200'; $i++) { Start-Sleep 2; $code = & curl.exe -sk -o NUL -w '%{http_code}' "https://localhost:$Port/health/ready" }
    Write-Host "health/ready: $code"
    Write-Host "Site: https://localhost$(if ($Port -ne 443) { ":$Port" })"
}
catch { Write-Host "FAILED: $_" -ForegroundColor Red }
finally { Stop-Transcript | Out-Null }

