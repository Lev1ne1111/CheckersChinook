using Checkers.Api.Engine;
using Checkers.Api.Moves;
using Checkers.Api.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Checkers.Api.Tests;

public class MoveServiceTests
{
    private const string Midgame = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";

    private static MoveService Service(int dbPieces = 0) => new(
        new StubPool(new FakeEngine(), dbPieces: dbPieces),
        Microsoft.Extensions.Options.Options.Create(new LimitsOptions()),
        Microsoft.Extensions.Options.Options.Create(new CacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new EngineOptions()),
        NullLogger<MoveService>.Instance);

    private static SuggestRequest Request(string? level, int? maxDepth = null, int? soft = null, int? hard = null, string position = Midgame) => new()
    {
        State = new GameState { Position = position },
        Level = level,
        Limits = new RequestLimits { MaxDepth = maxDepth, SoftTimeMs = soft, HardTimeMs = hard },
    };

    [Theory]
    [InlineData("weak", 7, 100)]
    [InlineData("medium", 11, 250)]
    [InlineData("strong", null, 500)]
    public void LevelDefaults(string level, int? depth, int time)
    {
        var plan = Service().Prepare(Request(level));
        Assert.Equal(depth, plan.Limits.MaxDepth);
        Assert.Equal(time, plan.Limits.MaxTimeMs);
        Assert.Equal(1200, plan.Limits.HardTimeMs);
    }

    [Fact]
    public void RequestLimits_OnlyTighten()
    {
        // the spec's example: weak with maxDepth 12 / 250 ms stays at weak's 7 / 100 ms
        var weak = Service().Prepare(Request("weak", maxDepth: 12, soft: 250));
        Assert.Equal(7, weak.Limits.MaxDepth);
        Assert.Equal(100, weak.Limits.MaxTimeMs);

        var strong = Service().Prepare(Request("strong", maxDepth: 16, soft: 300));
        Assert.Equal(16, strong.Limits.MaxDepth);
        Assert.Equal(300, strong.Limits.MaxTimeMs);
    }

    [Fact]
    public void SoftTime_StaysBelowHardTime()
    {
        var plan = Service().Prepare(Request("strong", hard: 200));
        Assert.Equal(160, plan.Limits.MaxTimeMs);
        Assert.Equal(200, plan.Limits.HardTimeMs);
    }

    [Fact]
    public void NoLevel_UsesDefaultSoftTime()
    {
        var plan = Service().Prepare(Request(null));
        Assert.Equal(300, plan.Limits.MaxTimeMs);    // Limits:DefaultSoftTimeMs
        Assert.Equal(11, plan.Limits.MaxDepth);
        Assert.Equal("medium", plan.Level.Name);
    }

    [Theory]
    [InlineData("W:WK14,K27:B5,6", 6, true)]
    [InlineData("W:WK14,K27:B5,6", 0, false)]          // no database loaded
    [InlineData(Midgame, 8, false)]                     // 16 pieces
    public void Tablebase_WhenPositionFitsTheDatabase(string fen, int dbPieces, bool expected)
    {
        var plan = Service(dbPieces).Prepare(Request("medium", position: fen));
        Assert.Equal(expected, plan.Tablebase);
    }

    [Fact]
    public void PositionKey_IsAccepted()
    {
        var plan = Service().Prepare(Request("weak", position: "pdn:" + Midgame));
        Assert.Equal("pdn:" + Midgame, plan.PositionKey);
    }
}

public class LruCacheTests
{
    [Fact]
    public void DropsLeastRecentlyUsed()
    {
        var cache = new LruCache<string, int>(2, TimeSpan.FromMinutes(1));
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _));   // a is now the most recent
        cache.Set("c", 3);                       // drops b

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void ExpiresAfterTtl()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var cache = new LruCache<string, int>(10, TimeSpan.FromMinutes(15), () => now);
        cache.Set("a", 1);
        now = now.AddMinutes(14);
        Assert.True(cache.TryGet("a", out _));
        now = now.AddMinutes(2);
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ZeroCapacity_CachesNothing()
    {
        var cache = new LruCache<string, int>(0, TimeSpan.FromMinutes(1));
        cache.Set("a", 1);
        Assert.False(cache.TryGet("a", out _));
    }
}
