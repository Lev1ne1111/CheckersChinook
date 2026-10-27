using System.Diagnostics;
using System.Text.RegularExpressions;
using Checkers.Core;
using Checkers.EngineProtocol;
using Checkers.EngineWorker.Native;

namespace Checkers.EngineWorker;

/// <summary>Error that is reported back to the API as {"ok":false,"error":...}.</summary>
internal sealed class WorkerException(string message) : Exception(message);

/// <summary>
/// The engine inside one worker process: configuration, warm-up and searches.
/// Translates between Checkers.Core positions/moves and the engine's raw board array.
/// </summary>
internal sealed class EngineSession : IDisposable
{
    private KingsRowEngine? _engine;
    private int _dbPieces;

    public InitResult Init(WorkerRequest request)
    {
        if (_engine is not null) throw new WorkerException("already initialized");
        if (string.IsNullOrWhiteSpace(request.Dll)) throw new WorkerException("init: 'dll' is required");

        string dll = Path.GetFullPath(request.Dll);
        if (!File.Exists(dll)) throw new WorkerException($"engine DLL not found: {dll}");

        // CheckerBoard runs engines with its own folder (the parent of "engines") as the
        // current directory. Do the same unless told otherwise.
        string workDir = request.WorkDir ?? Path.GetDirectoryName(Path.GetDirectoryName(dll)!)!;
        Environment.CurrentDirectory = workDir;

        // The engine opens its log in Documents while loading; make sure that works for this account.
        Console.Error.WriteLine($"[worker {Environment.ProcessId}] {EngineHome.Ensure(request.HomeDir)}");

        var sw = Stopwatch.StartNew();
        _engine = new KingsRowEngine(dll);

        if (!string.IsNullOrWhiteSpace(request.DbPath))
        {
            string db = Path.GetFullPath(request.DbPath);
            if (!Directory.Exists(db)) throw new WorkerException($"endgame DB folder not found: {db}");
            Configure($"set dbpath {db}");
            Configure("set enable_wld 1");
        }
        else
        {
            Configure("set enable_wld 0");
        }
        Configure($"set hashsize {request.HashMb ?? 128}");
        Configure($"set dbmbytes {request.DbMb ?? 512}");   // engine default is 16 GB per process
        Configure($"set book {request.Book ?? 0}");          // 0 = no book: deterministic moves

        // The first search initializes the endgame DB (~1 s). Do it now, not on a user's request.
        var start = Position.Start;
        _engine.GetMove(ToEngineBoard(start), (int)start.SideToMove, 0.05, exactTime: true,
                        hardStop: TimeSpan.FromSeconds(10));

        string about = _engine.Command("about").reply;
        var m = Regex.Match(about, @"(\d+)-piece");
        _dbPieces = m.Success && !about.Contains("Not using an endgame", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(m.Groups[1].Value)
            : 0;

        // The first lookup in each part of the endgame DB is slow (driver paths, first file reads,
        // measured ~55 ms under IIS). Touch every piece count the DB covers now, not on a user's request.
        foreach (string fen in WarmupEndgames.Where(f => Pdn.Parse(f).PieceCount <= _dbPieces))
        {
            var endgame = Pdn.Parse(fen);
            _engine.GetMove(ToEngineBoard(endgame), (int)endgame.SideToMove, 0.02, exactTime: true,
                            hardStop: TimeSpan.FromSeconds(5));
        }

        return new InitResult
        {
            EngineName = _engine.Command("name").reply,
            About = about.Replace("\n", " | ").Trim(),
            DbPieces = _dbPieces,
            WarmupMs = sw.ElapsedMilliseconds,
        };
    }

    // One legal position per piece count, 2 to 8
    private static readonly string[] WarmupEndgames =
    {
        "W:WK1:BK32",
        "W:WK5,K7:BK3",
        "W:WK14,K27:B5,6",
        "B:W17,19,27:BK9,26",
        "W:W22,27,K14:B5,6,K30",
        "W:W22,27,31,K14:B5,6,K30",
        "W:W22,27,31,K14:B1,5,6,K30",
    };

    public SearchResult Search(WorkerRequest request)
    {
        var engine = _engine ?? throw new WorkerException("not initialized: send 'init' first");

        Position before;
        try { before = Pdn.Parse(request.Fen); }
        catch (PdnException ex) { throw new WorkerException($"bad fen: {ex.Message}"); }

        if (before.LegalMoves().Count == 0) throw new WorkerException("no legal moves in this position");

        double maxSeconds = Math.Max(1, request.MaxTimeMs ?? 500) / 1000.0;
        var hardStop = TimeSpan.FromMilliseconds(request.HardTimeMs ?? (int)(maxSeconds * 4000));

        // getmove() has no depth limit: watch the reported depth and pull playnow. Measured on
        // KingsRow 1.20: after playnow it usually finishes one more iteration than the status showed,
        // so stop one below the limit. The final depth is within one of maxDepth (it does not report
        // every iteration, and positions already in the hashtable go deeper at once).
        int? maxDepth = request.MaxDepth is > 0 ? request.MaxDepth : null;
        StatusLine? last = null;
        var raw = engine.GetMove(ToEngineBoard(before), (int)before.SideToMove, maxSeconds,
                                 exactTime: request.Exact ?? false, hardStop: hardStop,
                                 onStatus: (_, text) =>
                                 {
                                     last = StatusLine.Parse(text) ?? last;
                                     if (maxDepth is int d && last is { ValueForBlack: not null } && last.Depth >= d - 1)
                                         engine.RequestStop();
                                 },
                                 tightPolling: maxDepth is not null);
        last = StatusLine.Parse(raw.FinalStatus) ?? last;

        // The engine answers by changing the board; find which legal move does that.
        var after = FromEngineBoard(raw.BoardAfter, before.SideToMove.Opponent());
        var best = MoveNotation.FromResultingPosition(before, after)
            ?? throw new WorkerException($"engine returned a position no legal move leads to: {after.ToFen()} (status: {raw.FinalStatus})");

        bool white = before.SideToMove == Side.White;
        int? score = last?.ValueForBlack is int v ? (white ? -v : v) : null;
        string? bound = last?.Bound;
        if (white && bound is "lower" or "upper") bound = bound == "lower" ? "upper" : "lower";

        // Inside the endgame DB KingsRow reports a win/loss as such. For a drawn DB position it
        // returns UNKNOWN with a plain move list instead of a search line ("depth 6; 22-17* ..."),
        // so in the DB anything that is not a win or loss is a draw.
        bool tablebase = _dbPieces > 0 && before.PieceCount <= _dbPieces;
        string resultName = raw.ResultCode switch { Cb.Win => "win", Cb.Loss => "loss", Cb.Draw => "draw", _ => "unknown" };
        if (tablebase && resultName == "unknown") resultName = "draw";

        return new SearchResult
        {
            BestMove = best.ToLongNotation(),
            Pv = ExpandPv(before, best, last?.Pv ?? Array.Empty<string>()),
            Score = score,
            ScoreBound = bound,
            Depth = last?.Depth ?? 0,
            SelDepth = last?.MaxDepth ?? 0,
            Nodes = last is null ? 0 : (long)(last.KiloNodesPerSecond * raw.ElapsedMs),   // kN/s × ms = nodes
            Result = resultName,
            TablebaseHit = tablebase,
            Pieces = before.PieceCount,
            ElapsedMs = raw.ElapsedMs,
            Stopped = raw.StoppedByPlaynow,
            FenAfter = after.ToFen(),
            RawStatus = raw.FinalStatus,
        };
    }

    public void Stop() => _engine?.RequestStop();

    /// <summary>
    /// Turns the engine's short-notation PV ("14x23 27x18") into full moves, checking each one
    /// against the rules. Stops at the first move that does not fit. Always starts with the best move.
    /// </summary>
    private static List<string> ExpandPv(Position position, Move best, IReadOnlyList<string> pv)
    {
        var result = new List<string> { best.ToLongNotation() };
        var current = position.Apply(best);

        // The PV normally starts with the move that was played; skip it if so.
        int i = pv.Count > 0 && MoveNotation.Match(position, pv[0]) is { IsLegal: true, Move: { } first } && first.Equals(best) ? 1 : 0;
        if (i == 0 && pv.Count > 0) return result;   // PV belongs to a different move (e.g. stopped mid-iteration)

        for (; i < pv.Count; i++)
        {
            var match = MoveNotation.Match(current, pv[i]);
            if (!match.IsLegal) break;
            result.Add(match.Move!.ToLongNotation());
            current = current.Apply(match.Move!);
        }
        return result;
    }

    private void Configure(string command)
    {
        var (handled, reply) = _engine!.Command(command);
        Console.Error.WriteLine($"[worker {Environment.ProcessId}] {command} -> {(handled ? "ok" : "NOT HANDLED")} {reply}");
    }

    // Core and the engine use the same piece codes (1/2/4/8), so this is a straight copy.
    private static int[] ToEngineBoard(Position p) =>
        Enumerable.Range(1, Geometry.SquareCount).Select(sq => (int)p[sq]).ToArray();

    private static Position FromEngineBoard(int[] squares, Side sideToMove)
    {
        var pieces = new Piece[Geometry.SquareCount + 1];
        for (int sq = 1; sq <= Geometry.SquareCount; sq++) pieces[sq] = (Piece)squares[sq - 1];
        return new Position(pieces, sideToMove);
    }

    public void Dispose() => _engine?.Dispose();
}
