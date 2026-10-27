using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Checkers.Api.Options;
using Checkers.Core;
using Checkers.EngineProtocol;

namespace Checkers.Api.Engine;

/// <summary>
/// One Checkers.EngineWorker process, seen from the API. Implements the spec's adapter:
/// SetPositionAsync stores the position, SearchAsync sends it to the worker with the limits.
///
/// Requests and responses are JSON lines matched by id. If a search is cancelled
/// (hard time limit, client gone) the worker is told to stop; if it does not answer within
/// StopGraceMs it is killed and marked <see cref="Faulted"/>, and the pool replaces it.
/// </summary>
public sealed class WorkerProcess : IEngineAdapter, IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly ILogger _log;
    private readonly int _index;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WorkerResponse>> _pending = new();
    private readonly object _writeLock = new();
    private Process? _process;
    private long _nextId;
    private string? _fen;
    private volatile bool _faulted;

    public WorkerProcess(int index, EngineOptions options, ILogger log)
    {
        _index = index;
        _options = options;
        _log = log;
    }

    public string Name { get; private set; } = "chinook";
    public int DbPieces { get; private set; }
    public InitResult? Init { get; private set; }
    public int? ProcessId => _process?.Id;

    /// True once the worker died or had to be killed; it must not be used again.
    public bool Faulted
    {
        get
        {
            if (_faulted) return true;
            try { return _process is null || _process.HasExited; }
            catch (InvalidOperationException) { return true; }   // process object already disposed
        }
    }

    /// Raised once when the worker dies on its own (crash, killed from outside).
    public event Action<WorkerProcess>? Died;

    public async Task StartAsync(CancellationToken ct)
    {
        string exe = System.IO.Path.IsPathRooted(_options.WorkerPath)
            ? _options.WorkerPath
            : System.IO.Path.Combine(AppContext.BaseDirectory, _options.WorkerPath);
        if (!File.Exists(exe)) throw new EngineException($"engine worker not found: {exe}");

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
        };

        _process = Process.Start(psi) ?? throw new EngineException("could not start the engine worker");
        JobObject.Assign(_process);
        _ = Task.Run(ReadStdoutAsync);
        _ = Task.Run(ReadStderrAsync);
        _log.LogInformation("worker {Index} started, pid {Pid}", _index, _process.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.StartupTimeoutMs);

        WorkerResponse response;
        try
        {
            response = await SendAsync(new WorkerRequest
            {
                Cmd = Commands.Init,
                Dll = _options.Path,
                DbPath = string.IsNullOrWhiteSpace(_options.Databases) ? null : _options.Databases,
                WorkDir = string.IsNullOrWhiteSpace(_options.WorkDir) ? null : _options.WorkDir,
                HomeDir = string.IsNullOrWhiteSpace(_options.HomeDir) ? null
                          : System.IO.Path.GetFullPath(_options.HomeDir, AppContext.BaseDirectory),
                HashMb = _options.HashMb,
                DbMb = _options.DbMb,
                Book = 0,
            }).WaitAsync(timeout.Token);

            if (!response.Ok || response.Init is null)
                throw new EngineException($"engine init failed: {response.Error}");
        }
        catch (Exception ex) when (ex is EngineException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // stderr is only logged at Debug level; when start-up fails, its last lines say why
            await Task.Delay(100, CancellationToken.None);   // let the reader pick up the final lines
            throw new EngineException($"{ex.Message}. Worker output: {string.Join(" | ", StderrTail())}", ex);
        }

        Init = response.Init;
        DbPieces = response.Init.DbPieces;
        _log.LogInformation("worker {Index} ready: {Engine}, tablebase {Pieces} pieces, warm-up {Ms} ms",
            _index, Init.EngineName, Init.DbPieces, Init.WarmupMs);
    }

    public Task SetPositionAsync(string pdn, CancellationToken ct = default)
    {
        _fen = Pdn.Parse(pdn).ToFen();   // throws PdnException for a bad position
        return Task.CompletedTask;
    }

    public async Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct = default)
    {
        if (_fen is null) throw new InvalidOperationException("SetPositionAsync must be called first");
        if (Faulted) throw new EngineException("engine worker is not running");

        Task<WorkerResponse> pending = SendAsync(new WorkerRequest
        {
            Cmd = Commands.Search,
            Fen = _fen,
            MaxTimeMs = limits.MaxTimeMs,
            HardTimeMs = limits.HardTimeMs,
            Exact = limits.Exact,
            MaxDepth = limits.MaxDepth,
        });

        WorkerResponse response;
        try
        {
            response = await pending.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Out of time: make the engine stop, so the worker is free for the next request.
            TrySend(new WorkerRequest { Cmd = Commands.Stop });
            var finished = await Task.WhenAny(pending, Task.Delay(_options.StopGraceMs));
            if (finished != pending)
            {
                _log.LogWarning("worker {Index} did not stop within {Ms} ms, killing it", _index, _options.StopGraceMs);
                Kill();
            }
            throw;
        }

        if (!response.Ok || response.Search is null)
            throw new EngineException(response.Error ?? "engine returned no result");
        return response.Search;
    }

    public async Task<bool> PingAsync(TimeSpan timeout)
    {
        try
        {
            var r = await SendAsync(new WorkerRequest { Cmd = Commands.Ping }).WaitAsync(timeout);
            return r.Ok;
        }
        catch { return false; }
    }

    private Task<WorkerResponse> SendAsync(WorkerRequest request)
    {
        long id = Interlocked.Increment(ref _nextId);
        request.Id = id;
        var tcs = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (!TrySend(request))
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetException(new EngineException("engine worker is not running"));
        }
        return tcs.Task;
    }

    private bool TrySend(WorkerRequest request)
    {
        var p = _process;
        if (p is null || p.HasExited) return false;
        try
        {
            string line = ProtocolJson.Serialize(request);
            lock (_writeLock)
            {
                p.StandardInput.WriteLine(line);
                p.StandardInput.Flush();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task ReadStdoutAsync()
    {
        var p = _process!;
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
            {
                WorkerResponse? response;
                try { response = ProtocolJson.Deserialize<WorkerResponse>(line); }
                catch (JsonException) { _log.LogWarning("worker {Index}: unreadable line: {Line}", _index, line); continue; }

                if (response?.Id is long id && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(response);
                else
                    _log.LogWarning("worker {Index}: unexpected message: {Line}", _index, line);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "worker {Index}: stdout reader stopped", _index);
        }

        // stdout closed = the process is gone
        bool wasExpected = _faulted;
        _faulted = true;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new EngineException("engine worker exited"));

        if (!wasExpected)
        {
            _log.LogError("worker {Index} (pid {Pid}) exited unexpectedly", _index, p.Id);
            Died?.Invoke(this);
        }
    }

    private async Task ReadStderrAsync()
    {
        var p = _process!;
        try
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) is not null)
            {
                _log.LogDebug("worker {Index}: {Line}", _index, line);
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 12) _stderrTail.Dequeue();
                }
            }
        }
        catch { /* process gone */ }
    }

    private readonly Queue<string> _stderrTail = new();

    private string[] StderrTail()
    {
        lock (_stderrTail) return _stderrTail.ToArray();
    }

    public void Kill()
    {
        _faulted = true;
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p is null) return;
        _faulted = true;
        TrySend(new WorkerRequest { Cmd = Commands.Quit });
        try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { Kill(); }
        p.Dispose();
    }
}
