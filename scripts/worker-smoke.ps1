<#
.SYNOPSIS
    Smoke test of Checkers.EngineWorker: builds it, pipes a few JSON-line requests into it
    and prints the responses.

.EXAMPLE
    .\scripts\worker-smoke.ps1 -Dll "E:\Downloads\CheckerBoard\engines\Kingsrow64.dll" -DbPath "E:\tb\chinook"
#>
param(
    [Parameter(Mandatory)] [string]$Dll,
    [string]$DbPath = "",      # not "-Db": that clashes with the built-in -Debug alias
    [int]$MaxTimeMs = 500
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\Checkers.EngineWorker"

dotnet build $proj -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = Join-Path $proj "bin\Debug\net10.0\Checkers.EngineWorker.exe"

function J($obj) { $obj | ConvertTo-Json -Compress }

$requests = @(
    (J @{ id = 1; cmd = "ping" })
    (J @{ id = 2; cmd = "init"; dll = $Dll; dbPath = $DbPath; hashMb = 128; dbMb = 512 })
    (J @{ id = 3; cmd = "search"; fen = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"; maxTimeMs = $MaxTimeMs; hardTimeMs = 1200 })
    (J @{ id = 4; cmd = "search"; fen = "B:W21-32:B1-12"; maxTimeMs = 250; hardTimeMs = 1200; exact = $true })
    (J @{ id = 5; cmd = "search"; fen = "W:WK14,K27:B5,6"; maxTimeMs = 30; hardTimeMs = 200; exact = $true })
    (J @{ id = 6; cmd = "search"; fen = "W:W22,27,K14:B5,6,K30"; maxTimeMs = 30; hardTimeMs = 200; exact = $true })
    (J @{ id = 7; cmd = "search"; fen = "B:W33:B1" })                      # bad fen -> error
    (J @{ id = 8; cmd = "search"; fen = "W:W:B1,2" })                      # no moves -> error
    (J @{ id = 9; cmd = "bogus" })                                         # unknown command -> error
)

# Start the worker as a plain process. (Piping into "& $exe" does not work in Windows
# PowerShell 5.1: with ErrorActionPreference=Stop every stderr line becomes an exception.)
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$psi.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)

$proc = [Diagnostics.Process]::Start($psi)
$stderrTask = $proc.StandardError.ReadToEndAsync()   # collected separately, shown at the end
$stdin = New-Object IO.StreamWriter($proc.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false))
$stdin.AutoFlush = $true

Write-Host "`n--- requests ---" -ForegroundColor Cyan
$requests | ForEach-Object { Write-Host "  $_"; $stdin.WriteLine($_) }
$stdin.Close()   # end of input: the worker finishes the queue and exits

Write-Host "`n--- responses (stdout), ms since start ---" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($null -ne ($line = $proc.StandardOutput.ReadLine())) {
    "{0,6} ms  {1}" -f $sw.ElapsedMilliseconds, $line
}
if (-not $proc.WaitForExit(15000)) { $proc.Kill(); Write-Warning "worker did not exit, killed" }

Write-Host "`n--- diagnostics (stderr) ---" -ForegroundColor Cyan
$stderrTask.Result
Write-Host "exit code: $($proc.ExitCode)"
