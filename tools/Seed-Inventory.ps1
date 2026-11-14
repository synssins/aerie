# Fills the Aerie application inventory with test data through the API (POST /api/v1/inventory/sync).
# Needs a personal API token with the Write scope, owned by someone who can edit the inventory.
# Set the values below, then paste the whole script into PowerShell (or run the file).

$Token   = ""                              # paste your token between the quotes: kbt_...
$BaseUrl = "https://demo.aeriekb.com"      # or https://localhost for local IIS
$Cycles  = 4                               # patch cycles to send, 1 to 10
$Seed    = 42                              # same number = same data
$Preview = $false                          # $true: show what would change, save nothing

& {
$ErrorActionPreference = "Stop"
if (-not $Token) { Write-Host "Set `$Token at the top first." -ForegroundColor Red; return }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Application, vendor, versions oldest to newest.
$apps = @(
    @{ N = "SQL Server";                V = "Microsoft";        Ver = "2019 CU22","2019 CU25","2019 CU27","2022 CU12","2022 CU14","2022 CU16" }
    @{ N = "Exchange Server";           V = "Microsoft";        Ver = "2019 CU12","2019 CU13","2019 CU14","2019 CU15" }
    @{ N = "IIS";                       V = "Microsoft";        Ver = "10.0.17763","10.0.20348","10.0.26100" }
    @{ N = ".NET Runtime";              V = "Microsoft";        Ver = "6.0.36","8.0.8","8.0.11","9.0.1","10.0.0" }
    @{ N = "SharePoint Server";         V = "Microsoft";        Ver = "2019","Subscription Edition 24H1","Subscription Edition 24H2" }
    @{ N = "Configuration Manager";     V = "Microsoft";        Ver = "2303","2309","2403","2409" }
    @{ N = "Oracle Database";           V = "Oracle";           Ver = "19.18","19.21","19.24","23.4","23.5" }
    @{ N = "Java SE Runtime";           V = "Oracle";           Ver = "8u401","8u421","17.0.11","21.0.4" }
    @{ N = "vCenter Server";            V = "VMware by Broadcom"; Ver = "7.0 U3o","8.0 U2","8.0 U3","8.0 U3b" }
    @{ N = "VMware Tools";              V = "VMware by Broadcom"; Ver = "12.3.5","12.4.0","12.4.5","12.5.0" }
    @{ N = "Veeam Backup & Replication"; V = "Veeam";           Ver = "12.0","12.1","12.2","12.3" }
    @{ N = "Tomcat";                    V = "Apache";           Ver = "9.0.85","9.0.93","10.1.24","10.1.31" }
    @{ N = "HTTP Server";               V = "Apache";           Ver = "2.4.58","2.4.59","2.4.62" }
    @{ N = "nginx";                     V = "F5";               Ver = "1.24.0","1.26.1","1.26.2","1.27.3" }
    @{ N = "PostgreSQL";                V = "PostgreSQL Global Development Group"; Ver = "14.12","15.7","16.3","16.4","17.0" }
    @{ N = "MySQL";                     V = "Oracle";           Ver = "8.0.36","8.0.39","8.4.2","9.0.1" }
    @{ N = "Splunk Universal Forwarder"; V = "Splunk";          Ver = "9.1.4","9.2.1","9.2.2","9.3.0" }
    @{ N = "CrowdStrike Falcon Sensor"; V = "CrowdStrike";      Ver = "7.13","7.15","7.16","7.18" }
    @{ N = "Nessus Agent";              V = "Tenable";          Ver = "10.5.1","10.6.2","10.7.3" }
    @{ N = "7-Zip";                     V = "Igor Pavlov";      Ver = "23.01","24.07","24.08" }
    @{ N = "Notepad++";                 V = "Notepad++ Team";   Ver = "8.6.4","8.6.9","8.7","8.7.1" }
    @{ N = "Google Chrome";             V = "Google";           Ver = "126.0.6478","128.0.6613","130.0.6723","131.0.6778" }
    @{ N = "Adobe Acrobat Reader";      V = "Adobe";            Ver = "24.002.20759","24.002.20965","24.003.20112" }
    @{ N = "Zabbix Agent 2";            V = "Zabbix";           Ver = "6.0.29","6.4.15","7.0.3" }
    @{ N = "Elasticsearch";             V = "Elastic";          Ver = "8.12.2","8.14.1","8.15.0","8.15.3" }
    @{ N = "Redis";                     V = "Redis";            Ver = "7.0.15","7.2.4","7.2.5","7.4.0" }
    @{ N = "OpenSSL";                   V = "OpenSSL Project";  Ver = "1.1.1w","3.0.13","3.0.14","3.3.2" }
    @{ N = "Jenkins";                   V = "Jenkins Project";  Ver = "2.440.3","2.452.2","2.462.1","2.479.1" }
    @{ N = "Git for Windows";           V = "Git for Windows";  Ver = "2.44.0","2.45.2","2.46.0","2.47.0" }
    @{ N = "PowerShell";                V = "Microsoft";        Ver = "7.2.19","7.4.2","7.4.5","7.4.6" }
    # The made-up ones.
    @{ N = "LedgerPro Server";          V = "Abacus & Sons";    Ver = "6.9.4","7.2","7.2.1","8.0" }
    @{ N = "Flibbertigibbet Controller"; V = "Whimsy Systems";  Ver = "0.9-beta","1.0","1.0.1-dont-ask","1.1" }
    @{ N = "Packet Goblin";             V = "Under The Rack LLC"; Ver = "3.1","3.2-hungry","3.3","4.0-fed" }
    @{ N = "Spaghetti Switch Manager";  V = "Al Dente Networks"; Ver = "2.0","2.1","2.2-tangled","3.0" }
    @{ N = "Gremlin Appeaser";          V = "Rack 7 Holdings";  Ver = "1.0","1.1","1.2-granola" }
    @{ N = "PrintNightmare Pro";        V = "Toner Despair Inc."; Ver = "11.0","11.1","11.2-PRN02","12.0" }
    @{ N = "TicketBlaster 9000";        V = "Queue Overflow Co."; Ver = "9000.1","9000.2","9000.3","9001.0" }
    @{ N = "Coffee Telemetry Agent";    V = "Bean Counter Labs"; Ver = "4.2","4.3-decaf","5.0-espresso" }
    @{ N = "Legacy Thing Nobody Owns";  V = "Steve Johnson Consulting"; Ver = "1.0","1.0","1.0a" }
    @{ N = "FINAL_FINAL Backup Suite";  V = "Really This Time Software"; Ver = "1.0-FINAL","1.0-FINAL-FINAL","2.0-REALLY" }
)

$servers = @(
    "APP01","APP02","APP03","APP04","APP05","WEB01","WEB02","WEB03","SQL01","SQL02","SQL03",
    "FS01","FS02","DC01","DC02","BKP01","MON01","LOG01","CI01","PRN01","PRN02","EDGE01","RACK7-01","LEGACY-APP01"
)

$rng = New-Object System.Random $Seed

# Each application lands on 2 to 6 servers. Each install starts at an early version and has a chance to fall behind each cycle.
$installs = foreach ($a in $apps) {
    $count = $rng.Next(2, 7)
    $picked = $servers | Sort-Object { $rng.Next() } | Select-Object -First $count
    foreach ($s in $picked) {
        [pscustomobject]@{ App = $a; Server = $s; Index = $rng.Next(0, [Math]::Max(1, $a.Ver.Count - 2)) }
    }
}

$endpoint = $BaseUrl.TrimEnd("/") + "/api/v1/inventory/sync"
$headers = @{ Authorization = "Bearer $Token" }
Write-Host ("{0} applications, {1} installs on {2} servers, {3} cycle(s) -> {4}" -f $apps.Count, @($installs).Count, $servers.Count, $Cycles, $endpoint)

for ($c = 1; $c -le $Cycles; $c++) {
    if ($c -gt 1) {
        foreach ($i in $installs) {
            # About a third of installs miss each patch cycle.
            if ($rng.NextDouble() -gt 0.35 -and $i.Index -lt $i.App.Ver.Count - 1) { $i.Index++ }
        }
    }
    $rows = foreach ($i in $installs) {
        @{ application = $i.App.N; vendor = $i.App.V; server = $i.Server; version = $i.App.Ver[$i.Index] }
    }
    $body = @{ source = "Seed-Inventory.ps1 cycle $c"; rows = @($rows); removeMissing = $false; apply = (-not $Preview) } |
        ConvertTo-Json -Depth 5

    try {
        $r = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType "application/json; charset=utf-8" `
            -Body ([Text.Encoding]::UTF8.GetBytes($body))
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { "no answer" }
        Write-Host "Cycle $c failed: HTTP $status" -ForegroundColor Red
        if ($_.ErrorDetails.Message) { Write-Host $_.ErrorDetails.Message }
        return
    }
    Write-Host ("Cycle {0}: added {1}, changed {2}, unchanged {3}, new applications {4}{5}" -f `
        $c, $r.added, $r.changed, $r.unchanged, $r.newApplications, $(if ($Preview) { " (preview, nothing saved)" } else { "" }))
    if ($Preview) { break }   # a preview can't build on itself: later cycles would compare against unsaved data
}
}
