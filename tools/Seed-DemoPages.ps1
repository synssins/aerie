# Writes the whimsical demo pages in tools/demo/whimsical-pages.json into each space's "00" folder, through the API.
# A missing page is created. A page that is there is replaced only while it is still exactly as the old demo generator left
# it: version 1.0, no other versions, no draft open, and the old generator's text in it. A page anyone has edited is skipped.
# Needs a personal API token with the Write scope, owned by someone who can edit pages in those spaces.
# Set the values below, then run the file from the repository (it reads demo/whimsical-pages.json next to it).

$Token   = ""                              # paste your token between the quotes: kbt_...
$BaseUrl = "https://localhost"             # the Aerie address
$Preview = $true                           # $true: show what would change, save nothing

& {
$ErrorActionPreference = "Stop"
if (-not $Token) { Write-Host "Set `$Token at the top first." -ForegroundColor Red; return }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$data = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot "demo\whimsical-pages.json") | ConvertFrom-Json
$api = $BaseUrl.TrimEnd("/") + "/api/v1"
$headers = @{ Authorization = "Bearer $Token" }
# Every page the old generator wrote ran this command; a person's own page would not.
$OldMarker = "whimsy-ctl"

function Call($method, $path, $body) {
    $req = @{ Method = $method; Uri = "$api$path"; Headers = $headers }
    if ($null -ne $body) { $req.ContentType = "application/json; charset=utf-8"; $req.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 30 -Compress)) }
    Invoke-RestMethod @req
}

# ---------- the editor document
function Text($s, $mark) { $n = [ordered]@{ type = "text"; text = $s }; if ($mark) { $n.marks = @(@{ type = $mark }) }; $n }
# `code` and **bold** inline.
function Rich($s) {
    @(foreach ($part in [regex]::Split($s, '(`[^`]+`|\*\*[^*]+\*\*)')) {
        if (-not $part) { continue }
        if ($part.StartsWith('`')) { Text $part.Substring(1, $part.Length - 2) "code" }
        elseif ($part.StartsWith('**')) { Text $part.Substring(2, $part.Length - 4) "bold" }
        else { Text $part }
    })
}
function Para($s) { [ordered]@{ type = "paragraph"; content = @(Rich $s) } }
function Item($s) { [ordered]@{ type = "listItem"; content = @(Para $s) } }
function Heading($s, $level) { [ordered]@{ type = "heading"; attrs = @{ level = $level }; content = @(Text $s) } }

function Block($b) {
    if ($b.h) { return Heading $b.h 2 }
    if ($b.h3) { return Heading $b.h3 3 }
    if ($b.p) { return Para $b.p }
    if ($b.note) { return [ordered]@{ type = "blockquote"; content = @(Para $b.note) } }
    if ($b.ul) { return [ordered]@{ type = "bulletList"; content = @($b.ul | ForEach-Object { Item $_ }) } }
    if ($b.ol) { return [ordered]@{ type = "orderedList"; attrs = @{ start = 1 }; content = @($b.ol | ForEach-Object { Item $_ }) } }
    if ($b.tasks) { return [ordered]@{ type = "taskList"; content = @($b.tasks | ForEach-Object { [ordered]@{ type = "taskItem"; attrs = @{ checked = [bool]$_[1] }; content = @(Para $_[0]) } }) } }
    if ($b.code) { return [ordered]@{ type = "codeBlock"; attrs = @{ language = $b.lang }; content = @(Text $b.code) } }
    if ($b.table) {
        $rows = @(for ($i = 0; $i -lt $b.table.Count; $i++) {
            $kind = if ($i -eq 0) { "tableHeader" } else { "tableCell" }
            [ordered]@{ type = "tableRow"; content = @($b.table[$i] | ForEach-Object { [ordered]@{ type = $kind; content = @(Para $_) } }) }
        })
        return [ordered]@{ type = "table"; content = $rows }
    }
    throw "Unknown block: $($b | ConvertTo-Json -Compress)"
}

function Document($page) {
    # Applies to, Owner and Last verified each on a line of their own, at the top.
    $blocks = @(
        Para "**Applies to:** $($page.appliesTo)"
        Para "**Owner:** $($page.owner)"
        Para "**Last verified:** $($page.lastVerified)"
    ) + @($page.blocks | ForEach-Object { Block $_ })
    [ordered]@{ type = "doc"; content = $blocks } | ConvertTo-Json -Depth 30 -Compress
}

# ---------- write
$spaces = @(Call Get "/spaces" $null)
$done = @{ created = 0; replaced = 0; skipped = 0 }
foreach ($group in $data.pages | Group-Object space) {
    $space = $spaces | Where-Object { $_.name -eq $group.Name } | Select-Object -First 1
    if (-not $space) { Write-Host "$($group.Name): no such space, skipped" -ForegroundColor Yellow; $done.skipped += $group.Count; continue }
    $tree = @(Call Get "/spaces/$($space.id)/tree" $null)
    $folderName = $group.Group[0].folder
    $folder = $tree | Where-Object { $_.kind -eq "Folder" -and $_.title -eq $folderName -and -not $_.parentId } | Select-Object -First 1
    if (-not $folder) {
        if ($Preview) { Write-Host "$($group.Name): would create folder $folderName" }
        else { $folder = Call Post "/spaces/$($space.id)/pages" @{ kind = "Folder"; title = $folderName; parentId = $null } }
    }
    foreach ($page in $group.Group) {
        $doc = Document $page
        $node = if ($folder) { $tree | Where-Object { $_.kind -eq "Page" -and $_.title -eq $page.title -and $_.parentId -eq $folder.id } | Select-Object -First 1 }
        if (-not $node) {
            Write-Host "  create  $($page.title)"
            if (-not $Preview) { $null = Call Post "/spaces/$($space.id)/pages" @{ kind = "Page"; title = $page.title; parentId = $folder.id; contentJson = $doc; tags = @($group.Name.Split(" ")[0].ToLower(), "whimsy") } }
            $done.created++
            continue
        }
        $current = Call Get "/pages/$($node.id)" $null
        $why = if ($current.version -ne "1.0" -or @($current.history).Count -gt 1) { "edited since it was generated (version $($current.version))" }
               elseif ($current.draft) { "a draft is open by $($current.draft.by)" }
               elseif (-not $current.contentJson.Contains($OldMarker)) { "not the old generator's text" }
        if ($why) { Write-Host "  skip    $($page.title): $why" -ForegroundColor Yellow; $done.skipped++; continue }
        Write-Host "  replace $($page.title)"
        if (-not $Preview) { $null = Call Put "/pages/$($node.id)" @{ title = $page.title; contentJson = $doc; baseVersion = $current.version; note = "Demo page rewritten by tools/Seed-DemoPages.ps1"; tags = @($current.tags) } }
        $done.replaced++
    }
}
Write-Host ("Created {0}, replaced {1}, skipped {2}{3}" -f $done.created, $done.replaced, $done.skipped, $(if ($Preview) { " (preview, nothing saved)" } else { "" }))
}
