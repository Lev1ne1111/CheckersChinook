<#
.SYNOPSIS
    Publishes Checkers.Api and hosts it in IIS as a normal ASP.NET Core app (no Windows Service).

.DESCRIPTION
    Run from an elevated (Administrator) PowerShell. Safe to re-run: it updates what exists.

    1. (-InstallFeatures) turns on IIS, Application Initialization and the IIS console.
    2. Checks that the ASP.NET Core Hosting Bundle (ASP.NET Core Module V2) is installed.
    3. dotnet publish -> -SitePath (the app pool is stopped meanwhile, so files are not locked).
    4. Writes Engine:Path / Databases / Workers into the published appsettings.json.
    5. App pool: No Managed Code, AlwaysRunning, no idle timeout, no 29-hour recycle,
       so the engine workers start with the pool and stay warm.
    6. Site with preload enabled, on http://*:<Port>.
    7. Grants the app pool identity read access to the site, engine and databases,
       and write access to <SitePath>\logs.
    8. Waits for /healthz to report ok.

.EXAMPLE
    .\deploy\iis-setup.ps1 -InstallFeatures
.EXAMPLE
    .\deploy\iis-setup.ps1 -Port 80 -EnginePath "C:\engines\checkerboard\engines\Kingsrow64.dll" -DbPath "D:\tb\chinook"
.EXAMPLE
    .\deploy\iis-setup.ps1 -SkipPublish        # only (re)configure IIS
#>
[CmdletBinding()]
param(
    [string]$SiteName = "CheckersApi",
    [int]$Port = 8080,
    [string]$SitePath = "C:\inetpub\checkers",
    [string]$EnginePath = "C:\engines\checkerboard\engines\Kingsrow64.dll",
    [string]$DbPath = "D:\tb\chinook",
    [int]$Workers = 2,
    [switch]$InstallFeatures,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$appcmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
$pool = $SiteName
$identity = "IIS AppPool\$pool"

function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Write-Bad($t)  { Write-Host "  [fail] $t" -ForegroundColor Red }
function AppCmd { & $appcmd @args; if ($LASTEXITCODE -ne 0) { throw "appcmd $($args -join ' ') failed ($LASTEXITCODE)" } }
function Exists($kind, $name) { $null = & $appcmd list $kind $name 2>$null; $LASTEXITCODE -eq 0 }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated PowerShell (Run as Administrator)." }

# ---------------------------------------------------------------- 1. IIS features
if ($InstallFeatures) {
    Write-Step "IIS features"
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        Install-WindowsFeature Web-Server, Web-AppInit, Web-Mgmt-Console | Out-Null          # Windows Server
    } else {
        Enable-WindowsOptionalFeature -Online -All -NoRestart -FeatureName `
            IIS-WebServerRole, IIS-WebServer, IIS-ApplicationInit, IIS-ManagementConsole | Out-Null   # Windows 10/11
    }
    Write-Ok "IIS, Application Initialization, IIS Manager"
}
if (-not (Test-Path $appcmd)) { throw "IIS is not installed. Run again with -InstallFeatures." }

# ---------------------------------------------------------------- 2. Hosting Bundle
Write-Step "ASP.NET Core Hosting Bundle"
$ancm = Join-Path $env:ProgramFiles "IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
if (-not (Test-Path $ancm)) {
    Write-Bad "ASP.NET Core Module V2 not found."
    Write-Host "  Install the .NET 10 Hosting Bundle from https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
    Write-Host "  (column 'ASP.NET Core Runtime' -> 'Hosting Bundle'), then run 'iisreset' and this script again." -ForegroundColor Yellow
    exit 1
}
Write-Ok $ancm

# ---------------------------------------------------------------- 3. publish
if (Exists apppool $pool) { & $appcmd stop apppool $pool 2>$null | Out-Null; Start-Sleep 2 }
if (-not $SkipPublish) {
    Write-Step "Publishing to $SitePath"
    dotnet publish (Join-Path $root "src\Checkers.Api") -c Release -o $SitePath --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    if (-not (Test-Path (Join-Path $SitePath "worker\Checkers.EngineWorker.exe"))) { throw "worker\Checkers.EngineWorker.exe missing in the publish output" }
    Write-Ok "published (API + worker\)"
}

# ---------------------------------------------------------------- 4. appsettings.json
Write-Step "appsettings.json"
$settingsFile = Join-Path $SitePath "appsettings.json"
$json = Get-Content $settingsFile -Raw | ConvertFrom-Json
$json.Engine | Add-Member Path $EnginePath -Force
$json.Engine | Add-Member Databases $DbPath -Force
$json.Engine | Add-Member Workers $Workers -Force
$json | ConvertTo-Json -Depth 20 | Set-Content $settingsFile -Encoding UTF8
Write-Ok "Engine.Path = $EnginePath"
Write-Ok "Engine.Databases = $DbPath, Workers = $Workers"
if (-not (Test-Path $EnginePath)) { Write-Bad "engine DLL not found: $EnginePath (run setup.ps1 first)" }
if (-not (Test-Path $DbPath))     { Write-Bad "database folder not found: $DbPath (the API still runs, without tablebases)" }

# ---------------------------------------------------------------- 5. app pool
Write-Step "Application pool '$pool'"
if (-not (Exists apppool $pool)) { AppCmd add apppool /name:$pool | Out-Null }
AppCmd set apppool $pool /managedRuntimeVersion:"" /startMode:AlwaysRunning /enable32BitAppOnWin64:false `
    /processModel.idleTimeout:00:00:00 /recycling.periodicRestart.time:00:00:00 | Out-Null
Write-Ok "No Managed Code, AlwaysRunning, no idle timeout, no periodic recycle"

# ---------------------------------------------------------------- 6. site
Write-Step "Site '$SiteName' on port $Port"
if (-not (Exists site $SiteName)) {
    AppCmd add site /name:$SiteName /bindings:"http/*:${Port}:" /physicalPath:$SitePath | Out-Null
} else {
    AppCmd set site $SiteName /bindings:"http/*:${Port}:" | Out-Null
    AppCmd set vdir "$SiteName/" /physicalPath:$SitePath | Out-Null
}
AppCmd set app "$SiteName/" /applicationPool:$pool /preloadEnabled:true | Out-Null
Write-Ok "http://localhost:$Port/  ->  $SitePath (preload on)"

# ---------------------------------------------------------------- 7. permissions
Write-Step "Permissions for '$identity'"
$logs = Join-Path $SitePath "logs"
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$engineRoot = Split-Path (Split-Path $EnginePath -Parent) -Parent    # CheckerBoard folder (holds egdb64.dll and engines\)
foreach ($grant in @(
        @{ Path = $SitePath;   Rights = "(OI)(CI)RX" },
        @{ Path = $logs;       Rights = "(OI)(CI)M" },
        @{ Path = $engineRoot; Rights = "(OI)(CI)RX" },
        @{ Path = $DbPath;     Rights = "(OI)(CI)RX" })) {
    if (-not (Test-Path $grant.Path)) { continue }
    icacls $grant.Path /grant "${identity}:$($grant.Rights)" /T /Q | Out-Null
    Write-Ok "$($grant.Rights)  $($grant.Path)"
}

# ---------------------------------------------------------------- 8. start + health
Write-Step "Starting"
AppCmd start apppool $pool | Out-Null
& $appcmd start site $SiteName 2>$null | Out-Null
$url = "http://localhost:$Port/healthz"
$deadline = (Get-Date).AddSeconds(90)
$health = $null
while ((Get-Date) -lt $deadline) {
    try { $health = Invoke-RestMethod $url -TimeoutSec 5; if ($health.ok) { break } } catch { }
    Start-Sleep 2
}
if ($health.ok) {
    Write-Ok "$url -> ok, workers $($health.workers)/$($health.configured), $($health.engine), DB $($health.tablebasePieces) pieces"
    Write-Host "`n  Test board: http://localhost:$Port/" -ForegroundColor Cyan
} else {
    Write-Bad "$url did not report ok within 90 s (last answer: $($health | ConvertTo-Json -Compress))"
    Write-Host "  Look at: Event Viewer -> Windows Logs -> Application (source 'IIS AspNetCore Module V2')," -ForegroundColor Yellow
    Write-Host "  or set stdoutLogEnabled=""true"" in $SitePath\web.config and check $SitePath\logs." -ForegroundColor Yellow
    exit 1
}
