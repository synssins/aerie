# Installs the local test prerequisites for Aerie. Run elevated (one UAC prompt).
# SQL Server 2025 Express, IIS (with features the site needs), ASP.NET Core 10 Hosting Bundle.
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'install-prereqs.log'
Start-Transcript -Path $log -Force | Out-Null

Write-Host '== IIS =='
Enable-WindowsOptionalFeature -Online -All -NoRestart -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ApplicationInit,IIS-WebSockets,IIS-RequestFiltering,IIS-WindowsAuthentication,IIS-HttpCompressionStatic,IIS-ManagementConsole | Out-Null

Write-Host '== SQL Server 2025 Express =='
winget install --id Microsoft.SQLServer.2025.Express --exact --silent --accept-package-agreements --accept-source-agreements

Write-Host '== ASP.NET Core 10 Hosting Bundle (after IIS so the module registers) =='
winget install --id Microsoft.DotNet.HostingBundle.10 --exact --silent --accept-package-agreements --accept-source-agreements

Write-Host '== Result =='
Get-Service 'MSSQL$SQLEXPRESS','W3SVC' -ErrorAction SilentlyContinue | Format-Table Name,Status
Write-Host ('ASP.NET Core Module: ' + (Test-Path 'C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'))
Stop-Transcript | Out-Null

