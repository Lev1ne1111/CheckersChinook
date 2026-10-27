<#
.SYNOPSIS
    Downloads and extracts the Chinook 2-8 piece endgame databases.

.DESCRIPTION
    Source: Wayback Machine snapshot of the Chinook site (the original site returns 404).
    Each archive is checked to be a real ZIP, not an HTML error page.
    Safe to re-run: archives already downloaded and extracted are skipped.

    Chinook endgame databases (c) Jonathan Schaeffer et al., University of Alberta.
    Free to use, not for sale; any use must credit the Chinook project as the source.

.EXAMPLE
    .\install-databases.ps1 -DbPath "E:\tb\chinook"
.EXAMPLE
    .\install-databases.ps1 -DbPath "E:\tb\chinook" -Quick     # only 2-6 pieces
#>
[CmdletBinding()]
param(
    [string]$DbPath = "D:\tb\chinook",
    [switch]$Quick,
    [switch]$KeepZips,
    [int]$Retries = 4
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # PS 5.1: progress bar slows downloads 10-50x
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$WaybackPrefix = "https://web.archive.org/web/20250331143637id_/http://www.cs.ualberta.ca/~chinook/DataBases"
$MegaFallback  = "https://mega.nz/#F!PZwFXL6I!8ALQgzMEx3UV7l_gT8sGXw"

$DbNames = @("DB6") +
    (0..4 | ForEach-Object { "DB7.$_" }) +
    @("DB8.00","DB8.10","DB8.20","DB8.30","DB8.40",
      "DB8.11","DB8.21","DB8.31","DB8.41",
      "DB8.22","DB8.32","DB8.42",
      "DB8.33","DB8.43",
      "DB8.44")
if ($Quick) { $DbNames = @("DB6") }

function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Write-Bad($t)  { Write-Host "  [fail] $t" -ForegroundColor Red }

function Test-ZipFile([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    if ((Get-Item $Path).Length -lt 1024) { return $false }
    $fs = [IO.File]::OpenRead($Path)
    try {
        $sig = New-Object byte[] 4
        [void]$fs.Read($sig, 0, 4)
        if ($sig[0] -ne 0x50 -or $sig[1] -ne 0x4B -or $sig[2] -ne 0x03 -or $sig[3] -ne 0x04) { return $false }
    } finally { $fs.Dispose() }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        $count = $zip.Entries.Count
        $zip.Dispose()
        return ($count -gt 0)
    } catch { return $false }
}

function Get-File([string]$Url, [string]$OutFile) {
    $tmp = "$OutFile.part"
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            Remove-Item $tmp -ErrorAction SilentlyContinue
            Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -TimeoutSec 900
            Move-Item $tmp $OutFile -Force
            return $true
        } catch {
            # web.archive.org answers 503/429 when it throttles after a lot of traffic: wait minutes, not seconds
            $code = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
            $wait = if ($code -in 429, 503) { @(60, 180, 360, 600)[[Math]::Min($i, 4) - 1] } else { [Math]::Min(60, 5 * $i * $i) }
            Write-Warning "  attempt $i/$Retries failed: $($_.Exception.Message)$(if ($i -lt $Retries) { " - waiting $wait s" })"
            if ($i -lt $Retries) { Start-Sleep -Seconds $wait }
        }
    }
    Remove-Item $tmp -ErrorAction SilentlyContinue
    return $false
}

Write-Step "Chinook databases -> $DbPath ($($DbNames.Count) archives)"
$zipDir = Join-Path $DbPath "_zips"
New-Item -ItemType Directory -Force -Path $DbPath, $zipDir | Out-Null

$failed = @()
foreach ($n in $DbNames) {
    $file   = Join-Path $zipDir "$n.zip"
    $marker = Join-Path $zipDir "$n.extracted"
    if (Test-Path $marker) { Write-Ok "$n already installed"; continue }
    if (Test-ZipFile $file) { Write-Ok "$n.zip already downloaded"; continue }

    Write-Host "  downloading $n.zip ..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ((Get-File "$WaybackPrefix/$n.zip" $file) -and (Test-ZipFile $file)) {
        Write-Ok ("{0}.zip  {1:N1} MB  {2:N0} s" -f $n, ((Get-Item $file).Length / 1MB), $sw.Elapsed.TotalSeconds)
    } else {
        Write-Bad "$n.zip is missing or not a valid ZIP"
        Remove-Item $file -ErrorAction SilentlyContinue
        $failed += $n
    }
}

Write-Step "Extracting"
foreach ($n in $DbNames) {
    if ($failed -contains $n) { continue }
    $file   = Join-Path $zipDir "$n.zip"
    $marker = Join-Path $zipDir "$n.extracted"
    if (Test-Path $marker) { continue }
    # Flatten: KingsRow expects every DB file directly in one folder
    $tmpDir = Join-Path $zipDir "_x_$n"
    Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $file -DestinationPath $tmpDir -Force
    Get-ChildItem $tmpDir -Recurse -File | ForEach-Object {
        $dest = Join-Path $DbPath $_.Name
        # Same file already in place (e.g. DB6 from a -Quick install): keep it. It may be open by
        # running engine workers, and overwriting it would fail.
        if ((Test-Path $dest) -and (Get-Item $dest).Length -eq $_.Length) { return }
        Move-Item $_.FullName $dest -Force
    }
    Remove-Item $tmpDir -Recurse -Force
    New-Item -ItemType File -Path $marker -Force | Out-Null
    Remove-Item $file -ErrorAction SilentlyContinue   # free disk as we go
    Write-Ok "$n extracted"
}

$dbFiles = Get-ChildItem $DbPath -File
$sizeGb  = ($dbFiles | Measure-Object Length -Sum).Sum / 1GB
Write-Step "Summary"
Write-Host ("  {0} files, {1:N2} GB in {2} (full set ~5.6 GB)" -f $dbFiles.Count, $sizeGb, $DbPath)
if ($failed.Count -gt 0) {
    Write-Bad "Not downloaded: $($failed -join ', ')"
    Write-Host "  Run again, or use the KingsRow 2-8 piece DB instead: $MegaFallback" -ForegroundColor Yellow
    exit 1
}
# Markers stay so re-runs skip finished parts; the folder is tiny.
if ($KeepZips) { Write-Host "  zips kept in $zipDir" }
exit 0
