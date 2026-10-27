<#
.SYNOPSIS
    Installs CheckerBoard + the KingsRow engine (64-bit) and copies the engine DLL
    into a fixed folder for the CheckersBot service.

.DESCRIPTION
    1. Downloads the installers from https://edgilbert.org (cached in %TEMP%).
    2. Runs them: silently with -Silent (Inno Setup flags), otherwise with the normal wizard.
    3. Finds kingsrow*.dll, checks it is a 64-bit DLL, and copies the CheckerBoard folder
       (engines\Kingsrow64.dll, egdb64.dll, weights, book) to -EnginePath, keeping its layout.
    4. Prints the exported functions so we can confirm the engine API (getmove etc.).
    The API's Engine:Path is then <EnginePath>\engines\Kingsrow64.dll.

    KingsRow and CheckerBoard (c) Ed Gilbert / Martin Fierz. Not redistributed by this
    repository: always downloaded from the author's site.

.EXAMPLE
    .\install-engine.ps1
.EXAMPLE
    .\install-engine.ps1 -EnginePath "E:\engines\checkerboard" -Silent
#>
[CmdletBinding()]
param(
    [string]$EnginePath = "C:\engines\checkerboard",
    [switch]$Silent,
    [switch]$SkipCheckerBoard,   # skip only if CheckerBoard (it brings egdb64.dll) is already installed
    [switch]$Force,              # reinstall even if a DLL is already found
    [string[]]$SearchPaths = @() # extra folders to look in, e.g. where you pointed the installer
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Installers = [ordered]@{
    CheckerBoard = "https://edgilbert.org/ItalianCheckers/downloads/CheckerBoardSetup64.1.78.exe"
    KingsRow     = "https://edgilbert.org/EnglishCheckers/downloads/KingsrowSetup64.1.20.exe"
}
if ($SkipCheckerBoard) { $Installers.Remove("CheckerBoard") }

function Write-Step($t) { Write-Host "`n=== $t ===" -ForegroundColor Cyan }
function Write-Ok($t)   { Write-Host "  [ok]   $t" -ForegroundColor Green }
function Write-Bad($t)  { Write-Host "  [fail] $t" -ForegroundColor Red }

function Get-SearchRoots {
    # Explicit paths first, then the usual install places, then every drive (shallow).
    $deep = @($SearchPaths) + @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs",
              "$env:USERPROFILE\Downloads", "$env:USERPROFILE\Documents")
    $deep | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { [pscustomobject]@{ Path = $_; Depth = 6 } }
    Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null } |
        ForEach-Object { [pscustomobject]@{ Path = $_.Root; Depth = 3 } }
}

function Find-File([string]$Filter) {
    foreach ($r in Get-SearchRoots) {
        $hit = Get-ChildItem $r.Path -Recurse -Depth $r.Depth -Filter $Filter -File -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notlike "$EnginePath*" } |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

function Find-KingsRowDll { Find-File "kingsrow*.dll" }

# Names of DLLs a PE file imports
function Get-DllImports([string]$Path) {
    $b  = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $numSections = [BitConverter]::ToUInt16($b, $pe + 6)
    $optSize     = [BitConverter]::ToUInt16($b, $pe + 20)
    $opt   = $pe + 24
    $ddOff = if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $impRva = [BitConverter]::ToUInt32($b, $ddOff + 8)
    $secTab = $opt + $optSize
    $off = {
        param([uint32]$rva)
        for ($i = 0; $i -lt $numSections; $i++) {
            $s = $secTab + 40 * $i
            $va = [BitConverter]::ToUInt32($b, $s + 12); $sz = [BitConverter]::ToUInt32($b, $s + 16)
            if ($rva -ge $va -and $rva -lt $va + $sz) { return [int]($rva - $va + [BitConverter]::ToUInt32($b, $s + 20)) }
        }
        -1
    }
    $d = & $off $impRva
    while ($d -ge 0) {
        $nameRva = [BitConverter]::ToUInt32($b, $d + 12)
        if ($nameRva -eq 0) { break }
        $p = & $off $nameRva; $end = $p; while ($b[$end] -ne 0) { $end++ }
        [Text.Encoding]::ASCII.GetString($b, $p, $end - $p)
        $d += 20
    }
}

# Reads the PE header: returns "x64", "x86" or $null
function Get-PeMachine([string]$Path) {
    $b = [IO.File]::ReadAllBytes($Path)
    if ($b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return $null }          # "MZ"
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ([BitConverter]::ToUInt32($b, $pe) -ne 0x00004550) { return $null }  # "PE\0\0"
    switch ([BitConverter]::ToUInt16($b, $pe + 4)) {
        0x8664 { "x64" } 0x014C { "x86" } default { "other" }
    }
}

# Lists exported function names from a PE32+ DLL (no dumpbin needed)
function Get-DllExports([string]$Path) {
    $b  = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $numSections = [BitConverter]::ToUInt16($b, $pe + 6)
    $optSize     = [BitConverter]::ToUInt16($b, $pe + 20)
    $opt   = $pe + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $ddOff = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $expRva = [BitConverter]::ToUInt32($b, $ddOff)
    if ($expRva -eq 0) { return @() }
    $secTab = $opt + $optSize
    $sections = for ($i = 0; $i -lt $numSections; $i++) {
        $s = $secTab + 40 * $i
        [pscustomobject]@{
            Va  = [BitConverter]::ToUInt32($b, $s + 12)
            Sz  = [BitConverter]::ToUInt32($b, $s + 8)
            Raw = [BitConverter]::ToUInt32($b, $s + 20)
        }
    }
    function RvaToOff([uint32]$rva) {
        foreach ($s in $sections) { if ($rva -ge $s.Va -and $rva -lt $s.Va + [Math]::Max($s.Sz, 1)) { return [int]($rva - $s.Va + $s.Raw) } }
        return -1
    }
    $e = RvaToOff $expRva
    $count = [BitConverter]::ToUInt32($b, $e + 24)
    $names = RvaToOff ([BitConverter]::ToUInt32($b, $e + 32))
    for ($i = 0; $i -lt $count; $i++) {
        $p = RvaToOff ([BitConverter]::ToUInt32($b, $names + 4 * $i))
        $end = $p; while ($b[$end] -ne 0) { $end++ }
        [Text.Encoding]::ASCII.GetString($b, $p, $end - $p)
    }
}

# ---------------------------------------------------------------- install
$dll = if ($Force) { $null } else { Find-KingsRowDll }
if ($dll) {
    Write-Ok "KingsRow already installed: $($dll.FullName)"
} else {
    foreach ($name in $Installers.Keys) {
        Write-Step "Installing $name"
        $url = $Installers[$name]
        $exe = Join-Path $env:TEMP ([IO.Path]::GetFileName($url))
        if (-not (Test-Path $exe) -or (Get-Item $exe).Length -lt 100KB) {
            Write-Host "  downloading $url"
            Invoke-WebRequest -Uri $url -OutFile $exe -UseBasicParsing -TimeoutSec 600
        }
        if ((Get-PeMachine $exe) -eq $null) { throw "$exe is not a Windows executable (blocked download?)" }

        if ($Silent) {
            Write-Host "  running silently ..."
            $p = Start-Process $exe -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-" -Wait -PassThru
            if ($p.ExitCode -ne 0) {
                Write-Warning "  silent install exit code $($p.ExitCode); running the wizard instead"
                Start-Process $exe -Wait
            }
        } else {
            Write-Host "  finish the setup wizard; the script waits for it ..." -ForegroundColor Yellow
            Start-Process $exe -Wait
        }
        Write-Ok "$name installer finished"
    }
    $dll = Find-KingsRowDll
}

if (-not $dll) {
    Write-Bad "kingsrow*.dll not found. Where did the installer put it? Copy that folder to $EnginePath."
    exit 1
}

# ---------------------------------------------------------------- copy + verify
Write-Step "Copying engine -> $EnginePath"
New-Item -ItemType Directory -Force -Path $EnginePath | Out-Null
# Keep CheckerBoard's layout (<root>\engines\Kingsrow64.dll, <root>\egdb64.dll): the engine reads
# its weights and book from "engines\" relative to the CheckerBoard folder, which the worker uses
# as the current directory. So copy the whole CheckerBoard folder, not just the DLL's folder.
$source = if ((Split-Path $dll.DirectoryName -Leaf) -eq "engines") { Split-Path $dll.DirectoryName -Parent } else { $dll.DirectoryName }
Get-ChildItem $source -Force | Where-Object { $_.Name -notin "source", "games", "unins000.exe", "unins000.dat" } |
    Copy-Item -Destination $EnginePath -Recurse -Force
$target = Get-ChildItem $EnginePath -Recurse -Filter $dll.Name | Select-Object -First 1 -ExpandProperty FullName
Write-Ok "from $source"

$arch = Get-PeMachine $target
if ($arch -eq "x64") { Write-Ok "$($dll.Name) is 64-bit" }
else { Write-Bad "$($dll.Name) is '$arch', expected x64 (EngineHost will be built as x64)" }

# KingsRow imports egdb64.dll (endgame DB driver), which CheckerBoard installs in its own folder,
# not next to the engine. Every non-system import must sit next to the engine DLL.
Write-Step "Dependencies"
$sys = Join-Path $env:WINDIR "System32"
$targetDir = Split-Path $target -Parent
foreach ($imp in Get-DllImports $target) {
    if (Test-Path (Join-Path $targetDir $imp))  { Write-Ok "$imp (engine folder)"; continue }
    if (Test-Path (Join-Path $EnginePath $imp)) { Write-Ok "$imp (CheckerBoard folder)"; continue }
    if (Test-Path (Join-Path $sys $imp))        { Write-Ok "$imp (system)"; continue }
    $found = Find-File $imp
    if ($found) {
        Copy-Item $found.FullName $EnginePath -Force
        Write-Ok "$imp copied from $($found.DirectoryName)"
    } else {
        Write-Bad "$imp not found - install CheckerBoard, then run this script again"
    }
}

Write-Step "Engine files"
Get-ChildItem $targetDir -File | ForEach-Object { "  {0,-40} {1,12:N0} bytes" -f $_.Name, $_.Length } | Write-Host

Write-Step "Exported functions of $($dll.Name)"
try {
    $exports = @(Get-DllExports $target)
    $exports | ForEach-Object { Write-Host "  $_" }
    foreach ($fn in "getmove", "enginecommand") {
        if ($exports -contains $fn) { Write-Ok "$fn present" } else { Write-Bad "$fn not exported" }
    }
} catch {
    Write-Warning "  could not read export table: $($_.Exception.Message)"
}

Write-Step "Done"
Write-Host "  Engine.Path = $target"
exit 0
