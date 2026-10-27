<#
.SYNOPSIS
    One-shot setup for the checkers API: endgame databases + KingsRow engine + appsettings.json.

.DESCRIPTION
    Runs scripts\install-databases.ps1 and scripts\install-engine.ps1, then writes
    the resulting paths into appsettings.json and the CHECKERS_DB user environment variable.
    Each step can be skipped; every step is safe to re-run.
    Afterwards, deploy\iis-setup.ps1 publishes the API to IIS.

.EXAMPLE
    .\setup.ps1 -DbPath "E:\tb\chinook"
.EXAMPLE
    .\setup.ps1 -Quick -Silent           # 2-6 piece DB only, silent engine install
.EXAMPLE
    .\setup.ps1 -SkipDatabases           # engine only
#>
[CmdletBinding()]
param(
    [string]$DbPath = "D:\tb\chinook",
    [string]$EnginePath = "C:\engines\checkerboard",
    [string]$AppSettings = "",
    [switch]$Quick,
    [switch]$Silent,
    [switch]$SkipDatabases,
    [switch]$SkipEngine
)

$ErrorActionPreference = "Stop"
$scripts = Join-Path $PSScriptRoot "scripts"
$results = [ordered]@{}

if (-not $SkipDatabases) {
    & (Join-Path $scripts "install-databases.ps1") -DbPath $DbPath -Quick:$Quick
    $results["Databases"] = if ($LASTEXITCODE -eq 0) { "ok  ($DbPath)" } else { "INCOMPLETE ($DbPath)" }
    [Environment]::SetEnvironmentVariable("CHECKERS_DB", $DbPath, "User")
}

if (-not $SkipEngine) {
    & (Join-Path $scripts "install-engine.ps1") -EnginePath $EnginePath -Silent:$Silent
    $dll = Get-ChildItem $EnginePath -Recurse -Filter "kingsrow*.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    $results["Engine"] = if ($LASTEXITCODE -eq 0 -and $dll) { "ok  ($($dll.FullName))" } else { "FAILED" }
}

# ---------------------------------------------------------------- appsettings.json
if (-not $AppSettings) {
    $AppSettings = Join-Path $PSScriptRoot "src\Checkers.Api\appsettings.json"
}
if ($AppSettings -and (Test-Path $AppSettings)) {
    $json = Get-Content $AppSettings -Raw | ConvertFrom-Json
    if (-not $json.Engine) { $json | Add-Member Engine ([pscustomobject]@{}) }
    if (-not $SkipDatabases) { $json.Engine | Add-Member Databases $DbPath -Force }
    if ($dll) { $json.Engine | Add-Member Path $dll.FullName -Force }
    $json | ConvertTo-Json -Depth 20 | Set-Content $AppSettings -Encoding UTF8
    $results["appsettings"] = "updated ($AppSettings)"
} else {
    $results["appsettings"] = "not found - skipped"
}

Write-Host "`n=== Setup summary ===" -ForegroundColor Cyan
$results.GetEnumerator() | ForEach-Object { "  {0,-12} {1}" -f $_.Key, $_.Value } | Write-Host
