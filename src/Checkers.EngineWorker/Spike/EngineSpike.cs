using System.Diagnostics;
using Checkers.EngineWorker.Native;

namespace Checkers.EngineWorker.Spike;

/// <summary>
/// One-off exploration of KingsRow64.dll. Answers the questions the real worker depends on:
///   1. Does the DLL load from our own process (with egdb64.dll)?
///   2. Does it accept the endgame DB path, and does it report using the Chinook DB?
///   3. What does the status string look like (depth, score, nodes, PV)?
///   4. How fast are tablebase positions?
///   5. Does playnow stop a long search quickly?
///
/// Usage:
///   Checkers.EngineWorker.exe spike --dll "E:\Downloads\CheckerBoard\engines\Kingsrow64.dll"
///                                    --db  "E:\tb\chinook" [--fen "B:W..:B.."] [--time 0.5]
/// </summary>
internal static class EngineSpike
{
    private static readonly (string name, string fen)[] DefaultPositions =
    {
        ("start position",              "B:W21-32:B1-12"),
        ("midgame from the spec",       "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"),
        ("6 pieces (in 2-6 piece DB)",  "W:W22,27,K14:B5,6,K30"),
        ("4 pieces (in 2-6 piece DB)",  "W:WK27,K14:B5,6"),
    };

    public static int Run(string[] args)
    {
        string? dll = Arg(args, "--dll");
        string? db = Arg(args, "--db");
        string? fen = Arg(args, "--fen");
        double time = double.Parse(Arg(args, "--time") ?? "0.5", System.Globalization.CultureInfo.InvariantCulture);
        string? workDir = Arg(args, "--workdir");

        if (dll is null)
        {
            Console.WriteLine("usage: spike --dll <path to Kingsrow64.dll> [--db <db folder>] [--fen <fen>] [--time <sec>] [--workdir <dir>]");
            return 2;
        }

        // CheckerBoard runs engines with ITS folder as the current directory (parent of "engines").
        // Copy that by default, so relative paths inside the engine behave the same way.
        workDir ??= Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dll))!)!;
        Environment.CurrentDirectory = workDir;

        Section("1. Load");
        Console.WriteLine($"process      : {(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}");
        Console.WriteLine($"current dir  : {Environment.CurrentDirectory}");
        var loadSw = Stopwatch.StartNew();
        using var engine = new KingsRowEngine(dll);
        Console.WriteLine($"loaded       : {engine.DllPath} in {loadSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"egdb64.dll   : {(engine.EgdbPreloaded ? "preloaded" : "not found next to the engine (Windows will search for it)")}");

        Section("2. Engine info");
        foreach (var cmd in new[] { "name", "get protocolversion", "get gametype", "get hashsize", "get dbpath",
                                     "get dbmbytes", "get book", "get bookfile", "get enable_wld", "get max_dbpieces",
                                     "get searchthreads" })
            Show(engine, cmd);

        Section("3. Configure");
        if (db is not null)
        {
            Show(engine, $"set dbpath {Path.GetFullPath(db)}");
            Show(engine, "set enable_wld 1");
        }
        Show(engine, "set hashsize 128");
        Show(engine, "set dbmbytes 512");
        Show(engine, "set book 0");          // no opening book: we want to see a real search
        Show(engine, "get dbpath");
        Show(engine, "get enable_wld");

        var positions = fen is not null ? new[] { ("from --fen", fen) } : DefaultPositions;
        int n = 4;
        foreach (var (name, f) in positions)
        {
            Section($"{n++}. Search: {name}");
            Search(engine, f, time);
        }

        Section($"{n++}. About (should mention the endgame database after the first search)");
        Show(engine, "about");

        Section($"{n++}. Stop test: maxtime 30 s, playnow after 300 ms");
        var (sq, color) = FenLite.Parse(DefaultPositions[1].fen);
        var r = engine.GetMove(sq, color, 30.0, exactTime: false, hardStop: TimeSpan.FromMilliseconds(300));
        Console.WriteLine($"returned after {r.ElapsedMs} ms (stoppedByPlaynow={r.StoppedByPlaynow}), move {FenLite.DiffToMove(sq, r.BoardAfter, color)}");
        Console.WriteLine($"last status  : {r.FinalStatus}");

        Section("Done");
        Console.WriteLine("Copy everything above and send it back.");
        return 0;
    }

    private static void Search(KingsRowEngine engine, string fen, double maxTime)
    {
        var (squares, color) = FenLite.Parse(fen);
        Console.WriteLine($"fen          : {FenLite.ToFen(squares, color)}  ({FenLite.CountPieces(squares)} pieces)");
        Console.WriteLine($"maxtime      : {maxTime} s, hard stop at {maxTime * 4} s");
        Console.WriteLine("status updates (ms : text):");

        var r = engine.GetMove(squares, color, maxTime, exactTime: false,
                               hardStop: TimeSpan.FromSeconds(maxTime * 4),
                               onStatus: (ms, text) => Console.WriteLine($"  {ms,6} : {text}"));

        Console.WriteLine($"move         : {FenLite.DiffToMove(squares, r.BoardAfter, color)}");
        Console.WriteLine($"result code  : {r.ResultCode} ({Cb.ResultName(r.ResultCode)})");
        Console.WriteLine($"time         : {r.ElapsedMs} ms{(r.StoppedByPlaynow ? " (stopped by playnow)" : "")}");
        Console.WriteLine($"final status : {r.FinalStatus}");
        Console.WriteLine($"fen after    : {FenLite.ToFen(r.BoardAfter, color == Cb.Black ? Cb.White : Cb.Black)}");
    }

    private static void Show(KingsRowEngine engine, string cmd)
    {
        try
        {
            var (handled, reply) = engine.Command(cmd);
            Console.WriteLine($"{cmd,-28} -> [{(handled ? "ok" : "not handled")}] {reply.Replace("\n", " | ")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{cmd,-28} -> EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
