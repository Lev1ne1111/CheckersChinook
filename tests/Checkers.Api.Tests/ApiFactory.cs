using Checkers.Api.Engine;
using Checkers.EngineProtocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Checkers.Api.Tests;

/// <summary>
/// The API in memory with the fake engine (Engine:Type = fake), so tests need no KingsRow.
/// Environment "Testing" keeps appsettings.Development.json (real engine paths) out.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    protected virtual IEnginePool? Pool => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Engine:Type", "fake");
        builder.UseSetting("RequestLog:Directory", "");
        if (Pool is { } pool)
            builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(pool)));
    }
}

/// <summary>The API with a given engine pool instead of the fake one.</summary>
public sealed class ApiFactoryWith(IEnginePool pool) : ApiFactory
{
    protected override IEnginePool? Pool => pool;
}

/// <summary>Pool around any engine adapter, with settable readiness and DB size.</summary>
public sealed class StubPool(IEngineAdapter engine, int ready = 1, int dbPieces = 0) : IEnginePool
{
    public string EngineName => "stub";
    public int Configured => 1;
    public int ReadyCount => ready;
    public int DbPieces => dbPieces;
    public int Searches;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<EngineLease> AcquireAsync(CancellationToken ct)
    {
        if (ready == 0) throw new EngineUnavailableException("no engine worker is running");
        Interlocked.Increment(ref Searches);
        return Task.FromResult(new EngineLease(engine, () => { }));
    }
}

/// <summary>An engine that never finishes on its own: only the cancellation token ends it.</summary>
public sealed class HangingEngine : IEngineAdapter
{
    public string Name => "hanging";
    public int DbPieces => 0;
    public Task SetPositionAsync(string pdn, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>An engine that answers with a fixed move, whatever the position.</summary>
public sealed class ScriptedEngine(string bestMove, bool tablebase = false, string result = "unknown") : IEngineAdapter
{
    public string Name => "scripted";
    public int DbPieces => 0;
    public SearchLimits? LastLimits { get; private set; }
    public Task SetPositionAsync(string pdn, CancellationToken ct = default) => Task.CompletedTask;

    public Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct = default)
    {
        LastLimits = limits;
        return Task.FromResult(new SearchResult
        {
            BestMove = bestMove, Pv = new List<string> { bestMove }, Score = 42, Depth = 9, Nodes = 1000,
            TablebaseHit = tablebase, Result = result,
        });
    }
}
