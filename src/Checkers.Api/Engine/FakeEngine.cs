using Checkers.Core;
using Checkers.EngineProtocol;

namespace Checkers.Api.Engine;

/// <summary>
/// Stand-in engine for Engine:Type = "fake": plays the first legal move, instantly.
/// Lets the API, the test board and integration tests run without KingsRow and the databases.
/// </summary>
public sealed class FakeEngine : IEngineAdapter
{
    private Position? _position;

    public string Name => "fake";
    public int DbPieces => 0;

    public Task SetPositionAsync(string pdn, CancellationToken ct = default)
    {
        _position = Pdn.Parse(pdn);
        return Task.CompletedTask;
    }

    public async Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct = default)
    {
        var position = _position ?? throw new InvalidOperationException("SetPositionAsync must be called first");
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var move = position.LegalMoves().FirstOrDefault()
                   ?? throw new EngineException("no legal moves in this position");
        return new SearchResult
        {
            BestMove = move.ToLongNotation(),
            Pv = new List<string> { move.ToLongNotation() },
            Score = 0,
            ScoreBound = "exact",
            Depth = 1,
            SelDepth = 1,
            Nodes = position.LegalMoves().Count,
            Result = "unknown",
            Pieces = position.PieceCount,
            FenAfter = position.Apply(move).ToFen(),
        };
    }
}

public sealed class FakeEnginePool : IEnginePool
{
    public string EngineName => "fake";
    public int Configured => 1;
    public int ReadyCount => 1;
    public int DbPieces => 0;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<EngineLease> AcquireAsync(CancellationToken ct) =>
        Task.FromResult(new EngineLease(new FakeEngine(), () => { }));
}
