using Checkers.Api.Options;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Engine;

/// <summary>
/// Fixed set of long-lived engine worker processes (Engine:Workers, default 2).
///
/// Routing: requests go round-robin; each worker has its own async lock (SemaphoreSlim(1)),
/// so one worker serves one request at a time. If the next worker in turn is busy but another
/// is free, the free one is used; if all are busy the request waits for the one in turn.
///
/// Failures: a worker that dies or has to be killed is replaced in the background; until it is
/// back, its lock stays taken so no request is routed to it.
/// </summary>
public sealed class EnginePool : IEnginePool, IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<EnginePool> _log;
    private readonly Slot[] _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private int _roundRobin = -1;

    private sealed class Slot(int index)
    {
        public int Index { get; } = index;
        public SemaphoreSlim Gate { get; } = new(0, 1);   // taken until the worker is ready
        public WorkerProcess? Worker;
        public volatile bool Ready;
        public int Restarting;                             // 1 while a (re)start is running
    }

    public EnginePool(IOptions<EngineOptions> options, ILoggerFactory loggers)
    {
        _options = options.Value;
        _loggers = loggers;
        _log = loggers.CreateLogger<EnginePool>();
        _slots = Enumerable.Range(0, Math.Max(1, _options.Workers)).Select(i => new Slot(i)).ToArray();
    }

    public string EngineName { get; private set; } = "chinook";
    public int Configured => _slots.Length;
    public int ReadyCount => _slots.Count(s => s.Ready && s.Worker is { Faulted: false });
    public int DbPieces { get; private set; }

    /// <summary>Starts all workers in parallel and waits for the first attempt of each.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await Task.WhenAll(_slots.Select(s => StartSlotAsync(s, retryForever: false)));
        _log.LogInformation("engine pool: {Ready}/{Total} workers ready", ReadyCount, Configured);

        // Workers that failed the first time keep retrying in the background.
        foreach (var s in _slots.Where(s => !s.Ready))
            _ = StartSlotAsync(s, retryForever: true);
    }

    public async Task<EngineLease> AcquireAsync(CancellationToken ct)
    {
        if (!_slots.Any(s => s.Ready))
            throw new EngineUnavailableException("no engine worker is running");

        int start = (int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)_slots.Length);

        // 1) the worker in turn, or the next free one
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[(start + i) % _slots.Length];
            if (slot.Ready && slot.Gate.Wait(0))
            {
                var lease = TryLease(slot);
                if (lease is not null) return lease;
            }
        }

        // 2) all busy: wait for the worker in turn (or the first ready one if it is restarting)
        var target = _slots[start].Ready ? _slots[start] : _slots.FirstOrDefault(s => s.Ready)
                     ?? throw new EngineUnavailableException("no engine worker is running");
        await target.Gate.WaitAsync(ct);
        return TryLease(target) ?? throw new EngineUnavailableException("engine worker failed, try again");
    }

    private EngineLease? TryLease(Slot slot)
    {
        var worker = slot.Worker;
        if (worker is null || worker.Faulted)
        {
            Replace(slot);          // keeps the gate taken until the new worker is ready
            return null;
        }
        return new EngineLease(worker, () =>
        {
            if (worker.Faulted) Replace(slot);
            else slot.Gate.Release();
        });
    }

    /// Called with the slot's gate held (or by Died while idle). Starts a new worker in the background.
    private void Replace(Slot slot)
    {
        slot.Ready = false;
        _ = StartSlotAsync(slot, retryForever: true);
    }

    private async Task StartSlotAsync(Slot slot, bool retryForever)
    {
        if (Interlocked.Exchange(ref slot.Restarting, 1) == 1) return;
        try
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!_shutdown.IsCancellationRequested)
            {
                var old = slot.Worker;
                if (old is not null)
                {
                    old.Died -= OnWorkerDied;
                    await old.DisposeAsync();
                }

                var worker = new WorkerProcess(slot.Index, _options, _loggers.CreateLogger($"Checkers.Api.Engine.Worker{slot.Index}"));
                try
                {
                    await worker.StartAsync(_shutdown.Token);
                    worker.Died += OnWorkerDied;
                    slot.Worker = worker;
                    EngineName = worker.Init?.EngineName ?? EngineName;
                    DbPieces = worker.DbPieces;
                    slot.Ready = true;
                    slot.Gate.Release();
                    return;
                }
                catch (Exception ex) when (!_shutdown.IsCancellationRequested)
                {
                    _log.LogError(ex, "worker {Index} failed to start", slot.Index);
                    slot.Worker = worker;
                    worker.Kill();
                    if (!retryForever) return;
                    await Task.Delay(delay, _shutdown.Token);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            Volatile.Write(ref slot.Restarting, 0);
        }
    }

    /// A worker died while nobody was using it: take its gate (if free) and replace it.
    private void OnWorkerDied(WorkerProcess worker)
    {
        var slot = _slots.FirstOrDefault(s => ReferenceEquals(s.Worker, worker));
        if (slot is null) return;
        slot.Ready = false;
        if (slot.Gate.Wait(0)) Replace(slot);
        // otherwise a request holds it; the lease release will notice Faulted and replace it
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // called by EngineStartup.StopAsync and again by the DI container: only the first call counts
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
        foreach (var s in _slots)
            if (s.Worker is not null)
                await s.Worker.DisposeAsync();
    }
}
