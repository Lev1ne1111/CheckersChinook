using System.Diagnostics;
using Checkers.Api.Engine;
using Checkers.Api.Options;
using Checkers.Core;
using Checkers.EngineProtocol;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Moves;

/// <summary>A request the API rejects; the controller turns it into a problem response.</summary>
public sealed class MoveRequestException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>A checked /v1/move/suggest request with the limits that apply to it.</summary>
public sealed record SuggestPlan(
    Position Position,
    LevelProfile Level,
    SearchLimits Limits,
    bool Tablebase,
    string PositionKey,
    string CacheKey);

/// <summary>
/// The /v1/move/* logic:
///   1. parse and normalize the PDN, check squares and piece counts (422 if wrong);
///   2. positions covered by the endgame DB are answered from it (a short engine call that
///      resolves from the DB, tablebaseHit = true);
///   3. anything else is searched with the level's depth/time limits;
///   4. the engine's move is checked against the rules (500 if no legal move comes back);
///   5. results are cached by canonical position + limits (LRU, TTL).
/// </summary>
public sealed class MoveService
{
    public const string GameId = "checkers-8x8";

    private readonly IEnginePool _pool;
    private readonly LimitsOptions _limits;
    private readonly string _engineType;
    private readonly ILogger<MoveService> _log;
    private readonly LruCache<string, SuggestResponse> _cache;

    public MoveService(IEnginePool pool, IOptions<LimitsOptions> limits, IOptions<CacheOptions> cache,
                       IOptions<EngineOptions> engine, ILogger<MoveService> log)
    {
        _pool = pool;
        _limits = limits.Value;
        _engineType = string.IsNullOrWhiteSpace(engine.Value.Type) ? "chinook" : engine.Value.Type;
        _log = log;
        _cache = new LruCache<string, SuggestResponse>(cache.Value.Capacity, TimeSpan.FromMinutes(cache.Value.TtlMinutes));
    }

    public int CacheCount => _cache.Count;

    /// <summary>Step 1: everything that can be rejected without the engine. Throws MoveRequestException.</summary>
    public SuggestPlan Prepare(SuggestRequest? request)
    {
        if (request is null) throw Unprocessable("bad_request", "request body is missing");
        if (!string.IsNullOrWhiteSpace(request.GameId) && !string.Equals(request.GameId.Trim(), GameId, StringComparison.OrdinalIgnoreCase))
            throw Unprocessable("unsupported_game", $"gameId must be '{GameId}'");
        if (request.State is null) throw Unprocessable("bad_request", "state is missing");
        if (!string.IsNullOrWhiteSpace(request.State.Notation) && !string.Equals(request.State.Notation.Trim(), "PDN", StringComparison.OrdinalIgnoreCase))
            throw Unprocessable("unsupported_notation", "state.notation must be 'PDN'");
        if (!LevelProfile.TryParse(request.Level, out var level))
            throw Unprocessable("bad_level", "level must be 'weak', 'medium' or 'strong'");

        var position = ParsePosition(request.State.Position);
        if (position.LegalMoves().Count == 0)
            throw Unprocessable("game_over", $"{position.SideToMove} has no legal moves: the game is over");

        var l = request.Limits;
        if (l?.MaxDepth is < 1 || l?.SoftTimeMs is < 1 || l?.HardTimeMs is < 1)
            throw Unprocessable("bad_limits", "limits must be positive numbers");

        int hard = Math.Min(l?.HardTimeMs ?? _limits.DefaultHardTimeMs, _limits.MaxHardTimeMs);
        string fen = position.ToFen();
        string positionKey = "pdn:" + fen;

        if (_pool.DbPieces > 0 && position.PieceCount <= _pool.DbPieces)
        {
            int tbTime = Math.Min(_limits.TablebaseTimeMs, hard);
            return new SuggestPlan(position, level, new SearchLimits(tbTime, hard, Exact: true),
                                   Tablebase: true, positionKey, $"{fen}|tb");
        }

        // The level is the ceiling; the request's limits may only make it shallower or faster.
        // No level given: medium's depth with Limits:DefaultSoftTimeMs as the move time.
        if (string.IsNullOrWhiteSpace(request.Level)) level = level with { MoveTimeMs = _limits.DefaultSoftTimeMs };

        int? depth = (l?.MaxDepth, level.MaxDepth) switch
        {
            (int r, int lv) => Math.Min(r, lv),
            (int r, null) => r,
            (null, var lv) => lv,
        };
        int soft = Math.Min(l?.SoftTimeMs ?? level.MoveTimeMs, level.MoveTimeMs);
        soft = Math.Max(1, Math.Min(soft, hard * 4 / 5));   // leave room to answer before the hard limit
        return new SuggestPlan(position, level, new SearchLimits(soft, hard, Exact: true, MaxDepth: depth),
                               Tablebase: false, positionKey, $"{fen}|{depth}|{soft}");
    }

    /// <summary>Steps 2–5. Throws OperationCanceledException when <paramref name="ct"/> fires (hard limit).</summary>
    public async Task<SuggestResponse> SuggestAsync(SuggestPlan plan, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (_cache.TryGet(plan.CacheKey, out var hit))
            return Copy(hit, plan, cached: true, sw.ElapsedMilliseconds);

        SearchResult result;
        using (var lease = await _pool.AcquireAsync(ct))
        {
            await lease.Engine.SetPositionAsync(plan.Position.ToFen(), ct);
            result = await lease.Engine.SearchAsync(plan.Limits, ct);
        }

        var best = LegalMove(plan.Position, result)
            ?? throw new EngineException($"engine returned no legal move (best '{result.BestMove}', pv {string.Join(" ", result.Pv)})");

        string? wdl = result.TablebaseHit && result.Result is "win" or "draw" or "loss" ? result.Result : null;
        var response = new SuggestResponse
        {
            Engine = _engineType,
            BestMove = best.ToLongNotation(),
            Pv = result.Pv.Count > 0 && result.Pv[0] == best.ToLongNotation() ? result.Pv : new List<string> { best.ToLongNotation() },
            ScoreOrWDL = wdl switch { "win" => 1, "loss" => -1, "draw" => 0, _ => result.Score ?? 0 },
            Depth = result.Depth,
            Nodes = result.Nodes,
            PositionKey = plan.PositionKey,
            Info = new SuggestInfo
            {
                TablebaseHit = result.TablebaseHit,
                Wdl = wdl,
                Level = plan.Level.Name,
                EngineTimeMs = result.ElapsedMs,
                ScoreBound = wdl is null ? result.ScoreBound : "exact",
                PositionAfter = plan.Position.Apply(best).ToFen(),
                EngineVersion = _pool.EngineName,
            },
        };
        _cache.Set(plan.CacheKey, response);
        return Copy(response, plan, cached: false, sw.ElapsedMilliseconds);
    }

    public ValidateResponse Validate(ValidateRequest? request)
    {
        if (request is null) throw Unprocessable("bad_request", "request body is missing");
        var position = ParsePosition(request.Position);
        var match = MoveNotation.Match(position, request.Move);
        if (!match.IsLegal)
            return new ValidateResponse { Legal = false, Reason = match.Reason };
        return new ValidateResponse
        {
            Legal = true,
            Move = match.Move!.ToLongNotation(),
            PositionAfter = position.Apply(match.Move!).ToFen(),
        };
    }

    /// <summary>Parses a PDN FEN; also accepts a positionKey ("pdn:B:W...:B...").</summary>
    public static Position ParsePosition(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.StartsWith("pdn:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        try
        {
            return Pdn.Parse(s);
        }
        catch (PdnException ex)
        {
            throw Unprocessable("invalid_pdn", ex.Message);
        }
    }

    /// Step 4: the best move must be legal; otherwise fall back to the first PV move that is.
    private Move? LegalMove(Position position, SearchResult result)
    {
        foreach (var text in result.Pv.Prepend(result.BestMove))
        {
            var match = MoveNotation.Match(position, text);
            if (match.IsLegal)
            {
                if (text != result.BestMove)
                    _log.LogWarning("engine best move {Best} is not legal in {Fen}; using {Move} from the PV",
                                    result.BestMove, position.ToFen(), text);
                return match.Move;
            }
        }
        return null;
    }

    private static SuggestResponse Copy(SuggestResponse r, SuggestPlan plan, bool cached, long timeMs) => new()
    {
        Engine = r.Engine,
        BestMove = r.BestMove,
        Pv = r.Pv,
        ScoreOrWDL = r.ScoreOrWDL,
        Depth = r.Depth,
        Nodes = r.Nodes,
        PositionKey = r.PositionKey,
        Info = new SuggestInfo
        {
            TablebaseHit = r.Info.TablebaseHit,
            TimeMs = timeMs,
            Wdl = r.Info.Wdl,
            Level = plan.Level.Name,
            Cached = cached,
            EngineTimeMs = cached ? 0 : r.Info.EngineTimeMs,
            ScoreBound = r.Info.ScoreBound,
            PositionAfter = r.Info.PositionAfter,
            EngineVersion = r.Info.EngineVersion,
        },
    };

    private static MoveRequestException Unprocessable(string code, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, code, message);
}
