using Checkers.Api.Moves;

namespace Checkers.Api.Engine;

/// <summary>
/// Starts and warms up the engine workers when the application starts (before it accepts
/// requests), and stops them on shutdown. With IIS "Start Mode = AlwaysRunning" + "Preload"
/// this happens right after an app pool start or recycle, not on the first user request.
/// </summary>
public sealed class EngineStartup(IEnginePool pool, MoveService moves, ILogger<EngineStartup> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await pool.StartAsync(ct);
        }
        catch (Exception ex)
        {
            // Never stop the whole app: /healthz reports ok=false and the pool keeps retrying.
            log.LogError(ex, "engine pool failed to start");
            return;
        }

        if (pool.ReadyCount > 0)
            await WarmUpAsync(ct);
    }

    /// One search and one endgame lookup through the move service, so the first real requests
    /// do not pay for JIT compilation and cold code paths (the 50 ms tablebase target is tight).
    private async Task WarmUpAsync(CancellationToken ct)
    {
        var positions = new List<string> { "B:W21-32:B1-12" };
        if (pool.DbPieces >= 4) positions.Add("W:WK14,K27:B5,6");
        foreach (var fen in positions)
        {
            try
            {
                var plan = moves.Prepare(new SuggestRequest { State = new GameState { Position = fen }, Level = "weak" });
                using var hard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hard.CancelAfter(plan.Limits.HardTimeMs);   // never hold up the app start
                var result = await moves.SuggestAsync(plan, hard.Token);
                log.LogInformation("warm-up {Fen}: {Move} in {Ms} ms", fen, result.BestMove, result.Info.TimeMs);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("warm-up search timed out for {Fen}", fen);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "warm-up search failed for {Fen}", fen);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (pool is IAsyncDisposable d) await d.DisposeAsync();
    }
}
