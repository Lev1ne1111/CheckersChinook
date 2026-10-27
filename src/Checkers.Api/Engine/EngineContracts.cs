using Checkers.EngineProtocol;

namespace Checkers.Api.Engine;

/// <summary>
/// Limits for one search. MaxTimeMs is the engine's target ("soft"), HardTimeMs the cut-off,
/// MaxDepth stops the search once that nominal depth is reached (null = time only).
/// </summary>
public sealed record SearchLimits(int MaxTimeMs, int HardTimeMs, bool Exact = true, int? MaxDepth = null);

/// <summary>
/// One engine instance, as named in the spec: setPosition(pdn) then search(limits).
/// Not thread-safe; get one from <see cref="IEnginePool"/> and use it from one request at a time.
/// </summary>
public interface IEngineAdapter
{
    string Name { get; }

    /// Largest piece count covered by the endgame database (0 = none).
    int DbPieces { get; }

    /// Sets the position to search. Throws Checkers.Core.PdnException for an invalid position.
    Task SetPositionAsync(string pdn, CancellationToken ct = default);

    /// Searches the current position. Throws OperationCanceledException when <paramref name="ct"/> fires
    /// (the engine is told to stop), <see cref="EngineException"/> when the engine fails.
    Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct = default);
}

/// <summary>Pool of engine instances with round-robin routing and one request per engine at a time.</summary>
public interface IEnginePool
{
    string EngineName { get; }
    int Configured { get; }
    int ReadyCount { get; }
    int DbPieces { get; }

    Task StartAsync(CancellationToken ct);

    /// Waits for an engine. Throws <see cref="EngineUnavailableException"/> if none is running.
    Task<EngineLease> AcquireAsync(CancellationToken ct);
}

/// <summary>Exclusive use of one engine; dispose to give it back.</summary>
public sealed class EngineLease(IEngineAdapter engine, Action release) : IDisposable
{
    private Action? _release = release;

    public IEngineAdapter Engine { get; } = engine;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>The engine returned an error or its worker died.</summary>
public class EngineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>No engine worker is running (all failed to start or are restarting).</summary>
public sealed class EngineUnavailableException(string message) : EngineException(message);
