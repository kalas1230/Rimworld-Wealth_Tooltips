<#
.SYNOPSIS
    Builds a clean, uploadable copy of the mod under Release\Wealth Tooltips\.

.DESCRIPTION
    The repo root doubles as a working mod folder: RimWorld ignores docs\, temp\,
    Source\ and friends, so the mod loads in-place for development. The Steam
    uploader is not so forgiving -- it ships whatever folder you point it at.
    This script produces a folder containing only what a player should receive.

    The copy list is an ALLOWLIST. New folders added to the repo later stay out
    of the release by default; to ship one, add it to $ShipDirs below.

    The staging folder is wiped and rebuilt on every run. Never hand-edit it --
    edits there are destroyed on the next run and were never in git.

.PARAMETER Build
    Run 'dotnet build -c Release' first, so the shipped DLL is built from the
    current source rather than whatever happened to be in Assemblies\.

.PARAMETER Zip
    Also produce Release\WealthTooltips-<version>.zip for a GitHub Release.

.PARAMETER Force
    Continue past the preflight checks that would otherwise abort. Use only when
    you know why a check is failing -- e.g. deliberately staging a build without
    a preview image to eyeball the layout.

.PARAMETER Check
    Validate the EXISTING staging folder against the current repo without
    rebuilding or restaging anything, then exit. Answers the one question this
    script could not previously answer: "is what is sitting in Release\ actually
    what the repo says today?"

    Why this exists (HANDOVER pre-publish item 17). The staleness check below
    compares the repo DLL against Source\, and it only runs WHEN YOU RESTAGE. It
    makes staging correct at the moment it is created and says nothing afterwards.
    Staging is a build output with an indefinite shelf life sitting in the exact
    folder the Steam uploader is pointed at, and Release\ is gitignored, so nothing
    in git tracks it either. A real instance: the staged DLL was 142,848 bytes from
    09:29 while the repo's was 150,016 from 10:01, and separately an About.xml edit
    left the staged copy behind. Both were invisible.

    Note this checks EVERY shipped input, not just the DLL -- About\, Languages\
    and LICENSE go stale exactly the same way, and the original check only ever
    looked at Source\.

.EXAMPLE
    .\tools\build-release.ps1 -Build -Zip

.EXAMPLE
    .\tools\build-release.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$Zip,
    [switch]$Force,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ReleaseDir = Join-Path $RepoRoot 'Release'
$ModName    = 'Wealth Tooltips'
$StageDir   = Join-Path $ReleaseDir $ModName
$AssemblyName = 'WealthReadout'

# --- Allowlist -----------------------------------------------------------
# Directories copied wholesale, if present. Anything not listed never ships.
# Defs and Patches are deliberately ABSENT and must stay absent. HANDOVER rule 1
# ("never touch save data") bans defs outright, so a Defs\ folder appearing here
# would be a rule violation shipping itself. Textures/Sounds are unused: this mod
# adds no assets, only tooltip text.
$ShipDirs  = @('About', 'Languages')
# Individual files copied from the repo root, if present.
# README.md is deliberately NOT here: it is the developer/agent-facing document
# (build loop, mirrored implementations, invariants) and means nothing to a
# player. The player-facing text is About.xml's <description> plus the Workshop
# description. If those two ever need to diverge from each other, add a separate
# player-facing file -- do not re-add README.md.
$ShipFiles = @('LICENSE')
# File extensions stripped from the copy wherever they appear.
$StripExt  = @('.pdb', '.mdb', '.log', '.user', '.orig', '.rej')

# Provenance for the staged folder. Deliberately written to Release\ and NOT into
# Release\Wealth Tooltips\ -- anything inside the staging folder is uploaded to the
# Workshop, and a build stamp is not player content. Keeping it outside is what
# makes it impossible to ship by accident; do not "tidy" it into the mod folder.
$StampPath = Join-Path $ReleaseDir 'staging.stamp.json'

# The paste-ready Workshop description, DERIVED from docs\workshop-description.txt
# (the source of truth per the publish sequence) rather than maintained by hand.
#
# WHY THE SCRIPT OWNS THIS FILE NOW. It used to be a hand-made duplicate that
# nothing regenerated, which made it the third copy of the listing text and the
# one most likely to be pasted at upload time -- so it went stale silently and
# invisibly. Measured on 2026-08-13 before this was added: 22 mojibake sequences
# (every em dash double-encoded to a-hat-euro, from a UTF-8 file written back as
# Windows-1252) plus a BOM, against exactly 1 surviving clean em dash. Pasting it
# would have published a listing full of garbled punctuation.
#
# Deliberately NOT inside Release\Wealth Tooltips\ -- everything in the staging
# folder is uploaded, and this is a clipboard aid, not player content.
$DescSource = Join-Path $RepoRoot 'docs\workshop-description.txt'
$PastePath  = Join-Path $ReleaseDir 'upload\description-paste.txt'

$problems = New-Object System.Collections.Generic.List[string]
function Fail($msg)  { $script:problems.Add($msg) }
function Warn($msg)  { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Ok($msg)    { Write-Host "  ok    $msg" -ForegroundColor DarkGray }

# The single definition of what ships, as relativePath -> repo source path. Both
# the staging pass and -Check read it, so the two cannot disagree about the file
# set -- a check that derived its own list would eventually drift from the copier
# and start passing things the copier never produced.
function Get-ExpectedShipMap {
    $map = [ordered]@{}
    foreach ($d in $ShipDirs) {
        $src = Join-Path $RepoRoot $d
        if (-not (Test-Path $src)) { continue }
        foreach ($f in (Get-ChildItem -Recurse -File -Force $src)) {
            if ($StripExt -contains $f.Extension.ToLower()) { continue }
            $map[$f.FullName.Substring($RepoRoot.Length + 1)] = $f.FullName
        }
    }
    # Assemblies is not in $ShipDirs: only the mod's own DLL ships, never the
    # Harmony or RimWorld references sitting beside it.
    $dllSrc = Join-Path $RepoRoot "Assemblies\$AssemblyName.dll"
    if (Test-Path $dllSrc) { $map["Assemblies\$AssemblyName.dll"] = $dllSrc }
    foreach ($f in $ShipFiles) {
        $src = Join-Path $RepoRoot $f
        if (Test-Path $src) { $map[$f] = $src }
    }
    return $map
}

function Get-Sha256($path) { return (Get-FileHash -Path $path -Algorithm SHA256).Hash }

# The paste-ready listing text: everything in docs\workshop-description.txt BELOW
# the '====' divider. Above the divider is guidance for whoever maintains the
# file and must never reach the Workshop.
#
# Read and written explicitly as UTF-8 WITHOUT a BOM. Get-Content/Set-Content
# default to the system ANSI codepage here, which is what double-encoded every
# em dash in the hand-made version, and a BOM pastes into Steam's description box
# as an invisible leading character.
function Get-PasteText {
    if (-not (Test-Path $DescSource)) { return $null }
    $text = [System.IO.File]::ReadAllText($DescSource, [System.Text.Encoding]::UTF8)
    $lines = $text -split "`r?`n"
    $divider = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^={10,}\s*$') { $divider = $i; break }
    }
    # No divider means the file's shape changed. Fail loudly rather than quietly
    # pasting the maintainer notes into a public listing.
    if ($divider -lt 0) { return $null }
    $body = $lines[($divider + 1)..($lines.Count - 1)]
    while ($body.Count -gt 0 -and $body[0].Trim() -eq '') { $body = $body[1..($body.Count - 1)] }
    return ($body -join "`r`n").TrimEnd() + "`r`n"
}

function Write-PasteFile($text) {
    $dir = Split-Path -Parent $PastePath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [System.IO.File]::WriteAllText($PastePath, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-NewestSourceFile {
    return Get-ChildItem -Path (Join-Path $RepoRoot 'Source') -Recurse -Filter *.cs -File |
           Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1
}

# --- Check-only mode -----------------------------------------------------
# Runs before everything else and exits: -Check must never rebuild, restage or
# mutate anything. Its whole value is reporting on the artifact as it stands.
if ($Check) {
    Write-Host "Checking staged '$ModName' against $RepoRoot" -ForegroundColor Cyan

    if (-not (Test-Path $StageDir)) {
        Write-Host "`nNothing staged at $StageDir." -ForegroundColor Red
        Write-Host "Run .\tools\build-release.ps1 -Build -Zip first.`n" -ForegroundColor Red
        exit 1
    }

    if (Test-Path $StampPath) {
        try {
            $stamp = Get-Content $StampPath -Raw | ConvertFrom-Json
            Write-Host "`nStaged $($stamp.stagedUtc) from commit $($stamp.commit)$(if ($stamp.dirty) { ' (tree was dirty)' })" -ForegroundColor DarkGray
            Write-Host "Mod version $($stamp.modVersion)" -ForegroundColor DarkGray
        } catch {
            Warn "staging.stamp.json is unreadable: $($_.Exception.Message)"
        }
    } else {
        Warn "no staging.stamp.json -- this folder predates stamping, or was not produced by this script"
    }

    $expected = Get-ExpectedShipMap
    $stagedFiles = @{}
    foreach ($f in (Get-ChildItem -Recurse -File -Force $StageDir)) {
        $stagedFiles[$f.FullName.Substring($StageDir.Length + 1)] = $f.FullName
    }

    $stale   = New-Object System.Collections.Generic.List[string]
    $absent  = New-Object System.Collections.Generic.List[string]
    $extra   = New-Object System.Collections.Generic.List[string]

    foreach ($rel in $expected.Keys) {
        if (-not $stagedFiles.ContainsKey($rel)) { $absent.Add($rel); continue }
        if ((Get-Sha256 $expected[$rel]) -ne (Get-Sha256 $stagedFiles[$rel])) { $stale.Add($rel) }
    }
    foreach ($rel in $stagedFiles.Keys) {
        if (-not $expected.Contains($rel)) { $extra.Add($rel) }
    }

    Write-Host "`n$($stagedFiles.Count) staged file(s), $($expected.Count) expected" -ForegroundColor Cyan

    if ($stale.Count -gt 0) {
        Write-Host "`nSTALE -- staged copy differs from the repo. This is the item 17 failure:" -ForegroundColor Red
        foreach ($r in $stale) { Write-Host "  $r" -ForegroundColor Red }
    }
    if ($absent.Count -gt 0) {
        Write-Host "`nMISSING -- in the repo, absent from staging:" -ForegroundColor Red
        foreach ($r in $absent) { Write-Host "  $r" -ForegroundColor Red }
    }
    if ($extra.Count -gt 0) {
        Write-Host "`nUNEXPECTED -- staged but not produced by the allowlist. Do not upload:" -ForegroundColor Red
        foreach ($r in $extra) { Write-Host "  $r" -ForegroundColor Red }
    }

    # The paste-ready description is not a staged file, so the hash sweep above
    # cannot see it -- and it is the copy most likely to be pasted at upload time.
    # Checked here against what the source of truth would produce.
    $pasteStale = $false
    $expectedPaste = Get-PasteText
    if ($null -eq $expectedPaste) {
        Warn "cannot derive the paste description from docs\workshop-description.txt (missing, or no '====' divider)"
    } elseif (-not (Test-Path $PastePath)) {
        Warn "no Release\upload\description-paste.txt -- it will be generated on the next -Build"
    } elseif ([System.IO.File]::ReadAllText($PastePath, [System.Text.Encoding]::UTF8) -ne $expectedPaste) {
        $pasteStale = $true
        Write-Host "`nSTALE DESCRIPTION -- Release\upload\description-paste.txt does not match" -ForegroundColor Red
        Write-Host "  docs\workshop-description.txt. Do not paste it. Re-run -Build to regenerate." -ForegroundColor Red
    }

    # Independent of the hash comparison: even a perfectly-synced staging is wrong
    # if the repo's own DLL was built before the last source edit.
    $dllPath = Join-Path $RepoRoot "Assemblies\$AssemblyName.dll"
    $newestSrc = Get-NewestSourceFile
    $dllStale = $false
    if ((Test-Path $dllPath) -and $newestSrc -and $newestSrc.LastWriteTime -gt (Get-Item $dllPath).LastWriteTime) {
        $dllStale = $true
        Write-Host "`nSTALE BUILD -- the repo's own DLL is older than $($newestSrc.Name)." -ForegroundColor Red
        Write-Host "  Staging may match the repo and still ship code that matches no source." -ForegroundColor Red
    }

    if ($stale.Count + $absent.Count + $extra.Count -gt 0 -or $dllStale -or $pasteStale) {
        Write-Host "`nDo NOT upload this folder. Re-run: .\tools\build-release.ps1 -Build -Zip`n" -ForegroundColor Red
        exit 1
    }

    Write-Host "`nOK -- every staged file matches the repo, and the DLL is newer than the newest source.`n" -ForegroundColor Green
    exit 0
}

Write-Host "Staging '$ModName' from $RepoRoot" -ForegroundColor Cyan

# --- Optional build ------------------------------------------------------
if ($Build) {
    $csproj = Join-Path $RepoRoot "Source\$AssemblyName.csproj"
    if (-not (Test-Path $csproj)) { throw "csproj not found at $csproj" }
    Write-Host "`nBuilding (Release)..." -ForegroundColor Cyan
    & dotnet build $csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE. Not staging a release from a broken build." }
}

# --- Preflight -----------------------------------------------------------
Write-Host "`nPreflight:" -ForegroundColor Cyan

$dll = Join-Path $RepoRoot "Assemblies\$AssemblyName.dll"
if (-not (Test-Path $dll)) {
    Fail "Assemblies\$AssemblyName.dll is missing. Build first (-Build), or the release ships no code at all."
} else {
    Ok "assembly present"

    # Stale-build catch. Assemblies\ is gitignored and survives branch switches,
    # so a DLL older than the newest source file is the classic way to upload
    # code that matches nothing you tested.
    $newestSrc = Get-NewestSourceFile
    if ($newestSrc -and $newestSrc.LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
        Fail ("Stale build: $($newestSrc.Name) (modified $($newestSrc.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))) " +
              "is newer than the DLL ($((Get-Item $dll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))). Re-run with -Build.")
    } else {
        Ok "assembly is newer than the newest source file"
    }
}

$aboutXml = Join-Path $RepoRoot 'About\About.xml'
if (-not (Test-Path $aboutXml)) {
    Fail "About\About.xml is missing. RimWorld will not list the mod without it."
} else {
    Ok "About.xml present"
}

# About.xml must PARSE, not merely exist. Found the hard way on 2026-09-02: an XML comment
# containing a double hyphen (illegal in XML) made About.xml unparseable. RimWorld skips a mod
# whose About.xml will not parse, so this ships a mod that silently does not load. The only
# symptom here was the zip quietly falling back to -dev because modVersion could not be read.
if (Test-Path $aboutXml) {
    try {
        $ax = [xml](Get-Content $aboutXml -Raw)
        if (-not $ax.ModMetaData.packageId) { Fail 'About.xml parses but has no <packageId>.' }
        elseif (-not $ax.ModMetaData.name)  { Fail 'About.xml parses but has no <name>.' }
        else { Ok "About.xml parses (packageId $($ax.ModMetaData.packageId), version $($ax.ModMetaData.modVersion))" }
    } catch {
        Fail "About.xml is not well-formed XML: $($_.Exception.Message) -- RimWorld will not load this mod."
    }
}

# Steam Workshop shows Preview.png as the item thumbnail; a Workshop item
# without one reads as broken. ModIcon.png is the in-game mod list row icon.
if (-not (Test-Path (Join-Path $RepoRoot 'About\Preview.png'))) {
    Fail "About\Preview.png is missing. This is the Workshop thumbnail -- do not publish without it."
} else {
    Ok "Preview.png present"
}
if (-not (Test-Path (Join-Path $RepoRoot 'About\ModIcon.png'))) {
    Warn "About\ModIcon.png is missing (mod list row icon). Cosmetic, not a blocker."
}

# Advisory only. Per project convention an unpushed or dirty tree is not itself
# a defect on a solo, unreleased repo -- but at upload time you want to know
# that the bits you are shipping correspond to no commit anyone can check out.
try {
    $dirty = & git -C $RepoRoot status --porcelain 2>$null
    if ($LASTEXITCODE -eq 0) {
        if ($dirty) { Warn "working tree has uncommitted changes -- this upload will match no commit" }
        $ahead = & git -C $RepoRoot rev-list --count '@{upstream}..HEAD' 2>$null
        if ($LASTEXITCODE -eq 0 -and [int]$ahead -gt 0) { Warn "$ahead commit(s) not pushed to origin" }
    }
} catch { }

if ($problems.Count -gt 0) {
    Write-Host "`n$($problems.Count) blocker(s):" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  - $p" -ForegroundColor Red }
    if (-not $Force) {
        Write-Host "`nNothing was staged. Fix the above, or re-run with -Force if you know why.`n" -ForegroundColor Red
        exit 1
    }
    Write-Host "`n-Force given: staging anyway. Do not publish this.`n" -ForegroundColor Yellow
}

# --- Stage ---------------------------------------------------------------
Write-Host "`nStaging:" -ForegroundColor Cyan

if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

foreach ($d in $ShipDirs) {
    $src = Join-Path $RepoRoot $d
    if (Test-Path $src) {
        Copy-Item -Recurse -Force $src -Destination (Join-Path $StageDir $d)
        Ok "$d\"
    }
}

# Assemblies is handled separately: only the mod's own DLL ships. Harmony and
# the RimWorld references are resolved by the game at load time; bundling them
# is a known way to break other mods.
New-Item -ItemType Directory -Force -Path (Join-Path $StageDir 'Assemblies') | Out-Null
if (Test-Path $dll) {
    Copy-Item -Force $dll -Destination (Join-Path $StageDir "Assemblies\$AssemblyName.dll")
    Ok "Assemblies\$AssemblyName.dll"
}

foreach ($f in $ShipFiles) {
    $src = Join-Path $RepoRoot $f
    if (Test-Path $src) {
        Copy-Item -Force $src -Destination (Join-Path $StageDir $f)
        Ok $f
    } else {
        Warn "$f not found at repo root; not shipped"
    }
}

# --- Scrub ---------------------------------------------------------------
$stripped = Get-ChildItem -Recurse -File -Force $StageDir |
            Where-Object { $StripExt -contains $_.Extension.ToLower() }
foreach ($s in $stripped) {
    Remove-Item -Force $s.FullName
    Ok "stripped $($s.Name)"
}

# --- Verify --------------------------------------------------------------
# Belt and braces: assert nothing dev-only survived the copy. If this trips,
# the allowlist above leaked and the release must not go out.
$forbidden = Get-ChildItem -Recurse -Force $StageDir |
             Where-Object { $_.FullName -match '\\(docs|temp|obj|bin|zzz-Do-Not-Commit|Source|tools|\.git|\.idea|\.superpowers)\\' -or
                            $_.Name -match '^(HANDOVER|README)\.md$' -or
                            $_.Name -like '*.bak' }
if ($forbidden) {
    Write-Host "`nFATAL: dev-only content reached the staging folder:" -ForegroundColor Red
    $forbidden | ForEach-Object { Write-Host "  - $($_.FullName.Substring($StageDir.Length + 1))" -ForegroundColor Red }
    Write-Host "Staging folder left in place for inspection. Do not upload it.`n" -ForegroundColor Red
    exit 1
}

$files = @(Get-ChildItem -Recurse -File -Force $StageDir)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

# --- Stamp ---------------------------------------------------------------
# Provenance for what was just staged, so a later -Check can say which commit
# this folder came from. The hash comparison in -Check does not depend on this
# file -- it re-derives everything from the repo -- so a missing or corrupt
# stamp degrades to a warning rather than blocking an upload.
$stampCommit = 'unknown'
$stampDirty  = $true
try {
    $c = & git -C $RepoRoot rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $c) { $stampCommit = $c.Trim() }
    $d = & git -C $RepoRoot status --porcelain 2>$null
    if ($LASTEXITCODE -eq 0) { $stampDirty = [bool]$d }
} catch { }

$stampVersion = 'dev'
if (Test-Path $aboutXml) {
    try {
        $sv = ([xml](Get-Content $aboutXml)).ModMetaData.modVersion
        if ($sv) { $stampVersion = $sv }
    } catch { }
}

[ordered]@{
    stagedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    commit     = $stampCommit
    dirty      = $stampDirty
    modVersion = $stampVersion
    fileCount  = $files.Count
    dllSha256  = $(if (Test-Path $dll) { Get-Sha256 $dll } else { $null })
} | ConvertTo-Json | Set-Content -Path $StampPath -Encoding utf8

if ($stampDirty) {
    Warn "stamped as dirty -- this staging matches no commit anyone can check out"
}

# --- Zip -----------------------------------------------------------------
$zipPath = $null
if ($Zip) {
    $version = 'dev'
    if (Test-Path $aboutXml) {
        try {
            $v = ([xml](Get-Content $aboutXml)).ModMetaData.modVersion
            if ($v) { $version = $v }
        } catch { }
    }
    $zipPath = Join-Path $ReleaseDir "WealthTooltips-$version.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $StageDir -DestinationPath $zipPath
    Write-Host "`n  zip   $zipPath" -ForegroundColor DarkGray
}

# --- Paste-ready Workshop description ------------------------------------
$pasteText = Get-PasteText
if ($null -eq $pasteText) {
    Warn "could not derive Release\upload\description-paste.txt from docs\workshop-description.txt"
} else {
    Write-PasteFile $pasteText
    Write-Host "`n  desc  $PastePath" -ForegroundColor DarkGray
}

# --- Report --------------------------------------------------------------
Write-Host "`nStaged $($files.Count) file(s), $([math]::Round($bytes / 1KB, 1)) KB" -ForegroundColor Green
Write-Host "  $StageDir" -ForegroundColor Green
Write-Host ""
Write-Host "Point the Steam uploader at that folder -- not at the repo root." -ForegroundColor Cyan
if ($zipPath) { Write-Host "Attach $(Split-Path -Leaf $zipPath) to the GitHub Release." -ForegroundColor Cyan }
# The debug harness was deleted on 2026-09-02, so there is no longer an in-game action
# to re-run here. What replaces it is smaller and manual, and it is the one check no
# build-time gate can make: whether the game still loads and patches the mod at all.
Write-Host "Before publishing, load this DLL in game once. Confirm '[Wealth Readout] Patches" -ForegroundColor Cyan
Write-Host "applied.' in Player.log, then hover one readout row." -ForegroundColor Cyan
Write-Host ""
