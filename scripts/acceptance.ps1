<#
.SYNOPSIS
    Checks the acceptance criteria against a running instance of the API (IIS or dotnet run).

.DESCRIPTION
    1. /healthz returns ok with every worker running.
    2. Endgame-database positions return in under 50 ms with tablebaseHit = true.
    3. Midgame positions at level "strong" return in under 600 ms with a legal move.
    4. Invalid PDN returns 422.
    5. A request that cannot finish within hardTimeMs returns 504 (more requests than workers,
       all at once: the later ones wait for a worker and run out of time).

    Positions for 2 and 3 are generated fresh on every run (random legal moves), so the
    15-minute result cache does not make the timings look better than they are.
    Times are measured by this script (round trip), not taken from the server's own timeMs.

.EXAMPLE
    .\scripts\acceptance.ps1 -BaseUrl http://localhost:8080
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [int]$Positions = 10
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(30)
$rng = New-Object System.Random
$results = New-Object System.Collections.Generic.List[object]

function Call([string]$method, [string]$path, $body) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($method)), "$BaseUrl$path"
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent (($body | ConvertTo-Json -Depth 6 -Compress), [Text.Encoding]::UTF8, "application/json") }
    $res = $http.SendAsync($req).GetAwaiter().GetResult()
    $text = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $ms = $sw.ElapsedMilliseconds
    $data = $null; if ($text) { try { $data = $text | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = [int]$res.StatusCode; Data = $data; Ms = $ms }
}

function Check([string]$name, [bool]$pass, [string]$detail) {
    $results.Add([pscustomobject]@{ Check = $name; Result = $(if ($pass) { "PASS" } else { "FAIL" }); Detail = $detail })
    Write-Host ("  [{0}] {1}: {2}" -f $(if ($pass) { "PASS" } else { "FAIL" }), $name, $detail) -ForegroundColor $(if ($pass) { "Green" } else { "Red" })
}

function Suggest([string]$fen, [string]$level, $limits) {
    $body = @{ gameId = "checkers-8x8"; state = @{ notation = "PDN"; position = $fen }; level = $level }
    if ($limits) { $body.limits = $limits }
    Call POST "/v1/move/suggest" $body
}

# Random legal game from the start position, $plies moves long (or until the game ends)
function RandomPosition([int]$plies) {
    $fen = "B:W21-32:B1-12"
    for ($i = 0; $i -lt $plies; $i++) {
        $legal = (Call GET "/v1/move/legal?position=$([Uri]::EscapeDataString($fen))").Data
        if ($legal.moves.Count -eq 0) { break }
        $move = $legal.moves[$rng.Next($legal.moves.Count)]
        $fen = (Call POST "/v1/move/validate" @{ position = $fen; move = $move }).Data.positionAfter
    }
    $fen
}

# Random endgame: a few kings and men, checked by the server
function RandomEndgame([int]$pieces) {
    while ($true) {
        $squares = 1..32 | Sort-Object { $rng.Next() } | Select-Object -First $pieces
        $white = @(); $black = @()
        for ($i = 0; $i -lt $squares.Count; $i++) {
            $sq = $squares[$i]
            $isWhite = ($i % 2 -eq 0)
            $king = ($rng.Next(2) -eq 0) -or ($isWhite -and $sq -le 4) -or (-not $isWhite -and $sq -ge 29)
            $tok = $(if ($king) { "K$sq" } else { "$sq" })
            if ($isWhite) { $white += $tok } else { $black += $tok }
        }
        $fen = "{0}:W{1}:B{2}" -f $(if ($rng.Next(2) -eq 0) { "W" } else { "B" }), ($white -join ","), ($black -join ",")
        $legal = Call GET "/v1/move/legal?position=$([Uri]::EscapeDataString($fen))"
        if ($legal.Status -eq 200 -and $legal.Data.moves.Count -gt 0) { return $legal.Data.position }
    }
}

Write-Host "`n=== Acceptance: $BaseUrl ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. health
$h = Call GET "/healthz"
Check "health ok" ($h.Status -eq 200 -and $h.Data.ok -and $h.Data.workers -eq $h.Data.configured) `
      ("HTTP {0}, workers {1}/{2}, {3}, DB {4} pieces" -f $h.Status, $h.Data.workers, $h.Data.configured, $h.Data.engine, $h.Data.tablebasePieces)
$dbPieces = [int]$h.Data.tablebasePieces
$workers = [int]$h.Data.configured
$null = Suggest "B:W21-32:B1-12" "weak"   # warm the HTTP connection

# ---------------------------------------------------------------- 2. tablebase
if ($dbPieces -gt 0) {
    $times = @(); $bad = @(); $counts = @{}
    for ($i = 0; $i -lt $Positions; $i++) {
        $fen = RandomEndgame (3 + $i % ($dbPieces - 2))   # cycles through 3..dbPieces pieces
        $r = Suggest $fen "strong"
        $times += $r.Ms
        $n = ($fen -split '[:,]' | Where-Object { $_ -match '\d' }).Count
        $counts[$n] = 1 + [int]$counts[$n]
        if ($r.Status -ne 200 -or -not $r.Data.info.tablebaseHit -or $r.Ms -ge 50) {
            $bad += "$fen -> HTTP $($r.Status), tablebaseHit=$($r.Data.info.tablebaseHit), $($r.Ms) ms"
        }
    }
    $sorted = $times | Sort-Object
    $mix = ($counts.Keys | Sort-Object | ForEach-Object { "$($_)p x$($counts[$_])" }) -join ", "
    Check "tablebase < 50 ms" ($bad.Count -eq 0) ("{0} positions ({1}), max {2} ms, median {3} ms{4}" -f $times.Count, $mix, $sorted[-1], $sorted[[int]($sorted.Count / 2)], $(if ($bad) { "; " + ($bad -join "; ") } else { "" }))
} else {
    Check "tablebase < 50 ms" $false "the service reports no endgame database (tablebasePieces = 0)"
}

# ---------------------------------------------------------------- 3. strong midgame
$times = @(); $bad = @()
for ($i = 0; $i -lt $Positions; $i++) {
    $fen = RandomPosition (6 + $rng.Next(14))
    $r = Suggest $fen "strong"
    $times += $r.Ms
    $legal = $false
    if ($r.Status -eq 200) { $legal = (Call POST "/v1/move/validate" @{ position = $fen; move = $r.Data.bestMove }).Data.legal }
    if ($r.Status -ne 200 -or -not $legal -or $r.Ms -ge 600) {
        $bad += "$fen -> HTTP $($r.Status), move $($r.Data.bestMove), legal=$legal, $($r.Ms) ms"
    }
}
$sorted = $times | Sort-Object
Check "strong midgame < 600 ms, legal" ($bad.Count -eq 0) ("{0} positions, max {1} ms, median {2} ms{3}" -f $times.Count, $sorted[-1], $sorted[[int]($sorted.Count / 2)], $(if ($bad) { "; " + ($bad -join "; ") } else { "" }))

# ---------------------------------------------------------------- 4. invalid PDN
$codes = foreach ($fen in "B:W33:B1", "B:W1,1:B5", "X:W1:B5", "nonsense") { (Suggest $fen "weak").Status }
Check "invalid PDN -> 422" (@($codes | Where-Object { $_ -ne 422 }).Count -eq 0) ("HTTP " + ($codes -join ", "))

# ---------------------------------------------------------------- 5. timeout
# 3 x workers strong requests at once, hardTimeMs 400 (search time ~250-320 ms each): the later
# ones wait for a worker and cannot finish in time. The start position has no forced moves, so
# every search uses its full time; a different softTimeMs per request keeps the cache out of it.
$tasks = @()
$softs = 250..319 | Sort-Object { $rng.Next() } | Select-Object -First (3 * $workers)
foreach ($soft in $softs) {
    $json = @{ state = @{ position = "B:W21-32:B1-12" }; level = "strong"; limits = @{ softTimeMs = $soft; hardTimeMs = 400 } } | ConvertTo-Json -Depth 5 -Compress
    $tasks += $http.PostAsync("$BaseUrl/v1/move/suggest", (New-Object System.Net.Http.StringContent ($json, [Text.Encoding]::UTF8, "application/json")))
}
[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$tasks)
$codes = $tasks | ForEach-Object { [int]$_.Result.StatusCode }
Check "timeout -> 504" ($codes -contains 504) ("{0} parallel requests, {1} workers: HTTP {2}" -f $tasks.Count, $workers, ($codes -join ", "))

Write-Host ""
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object Result -eq "FAIL").Count
if ($failed -gt 0) { Write-Host "$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All checks passed" -ForegroundColor Green
exit 0
