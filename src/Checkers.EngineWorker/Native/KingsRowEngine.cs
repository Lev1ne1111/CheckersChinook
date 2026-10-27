using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Checkers.EngineWorker.Native;

/// <summary>Result of one getmove() call.</summary>
internal sealed record EngineMoveResult(
    int ResultCode,                 // Cb.Draw / Win / Loss / Unknown
    int[] BoardAfter,               // 32 squares after the engine's move, index = square-1
    string FinalStatus,             // last content of the status string
    IReadOnlyList<(long ms, string text)> StatusHistory,
    long ElapsedMs,
    bool StoppedByPlaynow);

/// <summary>
/// Loads a CheckerBoard engine DLL (KingsRow64.dll) and calls it.
/// Not thread-safe: the DLL has global state, so one instance per process, one search at a time.
/// </summary>
internal sealed class KingsRowEngine : IDisposable
{
    private readonly IntPtr _lib;
    private readonly IntPtr _egdb;
    private readonly GetMoveFn _getMove;
    private readonly EngineCommandFn _engineCommand;

    // Unmanaged buffers, allocated once and reused by every call
    private readonly IntPtr _board   = Marshal.AllocHGlobal(64 * sizeof(int));
    private readonly IntPtr _status  = Marshal.AllocHGlobal(Cb.StatusSize);
    private readonly IntPtr _playnow = Marshal.AllocHGlobal(sizeof(int));
    private readonly IntPtr _move    = Marshal.AllocHGlobal(Cb.CbMoveSize);
    private readonly IntPtr _cmd     = Marshal.AllocHGlobal(Cb.CommandSize);
    private readonly IntPtr _reply   = Marshal.AllocHGlobal(Cb.ReplySize);

    public string DllPath { get; }

    public KingsRowEngine(string dllPath)
    {
        DllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(DllPath)) throw new FileNotFoundException("Engine DLL not found", DllPath);

        string dllDir = Path.GetDirectoryName(DllPath)!;

        // KingsRow64.dll imports egdb64.dll (endgame DB driver). CheckerBoard keeps it in its own
        // folder, one level above "engines". Let Windows look in both places, and preload it.
        SetDllDirectory(dllDir);
        _egdb = TryPreload("egdb64.dll", dllDir, Path.GetDirectoryName(dllDir));

        _lib = NativeLibrary.Load(DllPath);
        _getMove = Marshal.GetDelegateForFunctionPointer<GetMoveFn>(NativeLibrary.GetExport(_lib, "getmove"));
        _engineCommand = Marshal.GetDelegateForFunctionPointer<EngineCommandFn>(NativeLibrary.GetExport(_lib, "enginecommand"));
    }

    public bool EgdbPreloaded => _egdb != IntPtr.Zero;

    /// <summary>Sends a text command ("name", "set dbpath X", ...). Returns (handled, reply).</summary>
    public (bool handled, string reply) Command(string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(command);
        if (bytes.Length >= Cb.CommandSize) throw new ArgumentException("command too long", nameof(command));

        ZeroMemory(_cmd, Cb.CommandSize);
        ZeroMemory(_reply, Cb.ReplySize);
        Marshal.Copy(bytes, 0, _cmd, bytes.Length);

        int handled = _engineCommand(_cmd, _reply);
        string reply = Marshal.PtrToStringAnsi(_reply) ?? "";
        return (handled != 0, reply);
    }

    /// <summary>
    /// Asks the engine for a move.
    /// squares: 32 entries (index = square-1) with Cb piece codes.
    /// maxTimeSec: the engine's own time target (KingsRow uses 0.5x..3x of it unless exactTime).
    /// hardStop: after this time we set playnow=1 and the engine must return at once.
    /// onStatus: called whenever the status string changes (from a polling thread).
    /// tightPolling: check the status continuously instead of every 20 ms. KingsRow finishes
    ///   shallow iterations in well under a millisecond, so a depth limit needs this; it keeps
    ///   one more core busy while the search runs.
    /// </summary>
    public EngineMoveResult GetMove(int[] squares, int color, double maxTimeSec, bool exactTime,
                                    TimeSpan hardStop, Action<long, string>? onStatus = null,
                                    bool tightPolling = false)
    {
        WriteBoard(squares);
        ZeroMemory(_status, Cb.StatusSize);
        ZeroMemory(_move, Cb.CbMoveSize);
        Marshal.WriteInt32(_playnow, 0);

        var history = new List<(long, string)>();
        var sw = Stopwatch.StartNew();
        bool stopped = false;
        int info = exactTime ? Cb.ExactTime : 0;

        // getmove() blocks, so it runs on its own thread; this thread watches the status
        // string and pulls playnow when the hard limit is reached.
        int result = 0;
        // Deep recursive search: give the thread a big stack, like a native app's main thread.
        var search = new Thread(() => result = _getMove(_board, color, maxTimeSec, _status, _playnow, info, 0, _move),
                                maxStackSize: 64 * 1024 * 1024) { IsBackground = true, Name = "engine-search" };
        search.Start();

        string last = "";
        while (!search.Join(tightPolling ? 0 : 20))
        {
            if (tightPolling) Thread.SpinWait(50);
            string now = Marshal.PtrToStringAnsi(_status) ?? "";
            if (now != last)
            {
                last = now;
                history.Add((sw.ElapsedMilliseconds, now));
                onStatus?.Invoke(sw.ElapsedMilliseconds, now);
            }
            if (!stopped && sw.Elapsed >= hardStop)
            {
                Marshal.WriteInt32(_playnow, 1);
                stopped = true;
            }
        }
        sw.Stop();

        string final = Marshal.PtrToStringAnsi(_status) ?? "";
        if (final != last) history.Add((sw.ElapsedMilliseconds, final));

        return new EngineMoveResult(result, ReadBoard(), final, history, sw.ElapsedMilliseconds, stopped);
    }

    /// <summary>
    /// Asks a running search to stop now (same as CheckerBoard's "Play!" button).
    /// Safe to call from any thread; the engine returns its best move so far.
    /// </summary>
    public void RequestStop() => Marshal.WriteInt32(_playnow, 1);

    private void WriteBoard(int[] squares)
    {
        ZeroMemory(_board, 64 * sizeof(int));
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = Squares.ToCoords(sq);
            Marshal.WriteInt32(_board, Squares.ByteOffset(x, y), squares[sq - 1]);
        }
    }

    private int[] ReadBoard()
    {
        var squares = new int[32];
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = Squares.ToCoords(sq);
            squares[sq - 1] = Marshal.ReadInt32(_board, Squares.ByteOffset(x, y));
        }
        return squares;
    }

    private static IntPtr TryPreload(string name, params string?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (d is null) continue;
            string p = Path.Combine(d, name);
            if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
        }
        return IntPtr.Zero;
    }

    private static readonly byte[] Zeros = new byte[Cb.StatusSize];

    private static void ZeroMemory(IntPtr p, int size) => Marshal.Copy(Zeros, 0, p, size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public void Dispose()
    {
        // The DLL is deliberately not unloaded: engines often keep threads and DB handles open,
        // and the worker process exits right after anyway.
        foreach (var p in new[] { _board, _status, _playnow, _move, _cmd, _reply })
            Marshal.FreeHGlobal(p);
    }
}
