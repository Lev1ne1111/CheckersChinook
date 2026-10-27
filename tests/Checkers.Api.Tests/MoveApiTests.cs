using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Checkers.Api.Engine;

namespace Checkers.Api.Tests;

public class MoveApiTests : IClassFixture<ApiFactory>
{
    private const string Spec = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";
    private readonly HttpClient _client;

    public MoveApiTests(ApiFactory factory) => _client = factory.CreateClient();

    private static object Suggest(string position, string? level = "medium", object? limits = null) => new
    {
        gameId = "checkers-8x8",
        state = new { notation = "PDN", position },
        level,
        limits,
    };

    private async Task<(HttpStatusCode status, JsonElement body)> PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, json);
    }

    [Fact]
    public async Task Health_IsOk()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.GetProperty("workers").GetInt32());
    }

    [Fact]
    public async Task Suggest_ReturnsLegalMoveWithSpecFields()
    {
        var (status, body) = await PostAsync(_client, "/v1/move/suggest", Suggest(Spec, "weak", new { maxDepth = 12, softTimeMs = 250, hardTimeMs = 1200 }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("14x23", body.GetProperty("bestMove").GetString());   // fake engine: first legal move
        Assert.Equal("pdn:" + Spec, body.GetProperty("positionKey").GetString());
        foreach (var field in new[] { "engine", "pv", "scoreOrWDL", "depth", "nodes", "info" })
            Assert.True(body.TryGetProperty(field, out _), field);
        Assert.False(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.True(body.GetProperty("info").TryGetProperty("timeMs", out _));
    }

    [Fact]
    public async Task Suggest_NormalizesPosition()
    {
        var (status, body) = await PostAsync(_client, "/v1/move/suggest", Suggest(" b:B1-12:W21-32 ", null));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pdn:B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12",
                     body.GetProperty("positionKey").GetString());
    }

    [Fact]
    public async Task Suggest_SecondCallIsCached()
    {
        const string fen = "W:W21,25,29,K30:B4,8,K12";
        var (_, first) = await PostAsync(_client, "/v1/move/suggest", Suggest(fen, "strong"));
        var (_, second) = await PostAsync(_client, "/v1/move/suggest", Suggest(fen, "strong"));
        Assert.False(first.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.True(second.GetProperty("info").GetProperty("cached").GetBoolean());
        Assert.Equal(first.GetProperty("bestMove").GetString(), second.GetProperty("bestMove").GetString());
    }

    [Theory]
    [InlineData("B:W33:B1", "invalid_pdn")]                      // square out of range
    [InlineData("B:W1,1:B5", "invalid_pdn")]                     // duplicate square
    [InlineData("B:W1-13:B20", "invalid_pdn")]                   // too many pieces (and men on the crowning row)
    [InlineData("B:W21-32", "invalid_pdn")]                      // syntax
    [InlineData("hello", "invalid_pdn")]
    [InlineData("", "invalid_pdn")]
    [InlineData("W:W:B1,2", "game_over")]                        // side to move has no pieces
    public async Task Suggest_InvalidPosition_Is422(string position, string code)
    {
        var (status, body) = await PostAsync(_client, "/v1/move/suggest", Suggest(position));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(code, body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Suggest_UnknownGameOrLevel_Is422()
    {
        var (s1, _) = await PostAsync(_client, "/v1/move/suggest", new { gameId = "chess", state = new { position = Spec } });
        var (s2, _) = await PostAsync(_client, "/v1/move/suggest", Suggest(Spec, "godlike"));
        var (s3, _) = await PostAsync(_client, "/v1/move/suggest", new { state = new { notation = "FEN", position = Spec } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, s1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, s2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, s3);
    }

    [Theory]
    [InlineData("14x23", true)]
    [InlineData("16x23", true)]
    [InlineData("16-20", false)]    // capture is mandatory
    [InlineData("12x19", false)]    // no white piece to jump
    [InlineData("10-15", false)]
    [InlineData("nonsense", false)]
    public async Task Validate(string move, bool legal)
    {
        var (status, body) = await PostAsync(_client, "/v1/move/validate", new { position = Spec, move });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(legal, body.GetProperty("legal").GetBoolean());
    }

    [Fact]
    public async Task Validate_InvalidPosition_Is422()
    {
        var (status, _) = await PostAsync(_client, "/v1/move/validate", new { position = "B:W99:B1", move = "1-5" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
    }

    [Fact]
    public async Task Legal_ListsMoves()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/v1/move/legal?position=" + Uri.EscapeDataString(Spec));
        Assert.Equal(new[] { "14x23", "16x23" }, json.GetProperty("moves").EnumerateArray().Select(m => m.GetString()).ToArray());
    }

    [Fact]
    public async Task TestBoard_IsServed()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("Checkers test board", html);
    }
}

public class EngineFailureTests
{
    private const string Midgame = "B:W21-32:B1-12";

    private static object Suggest(object limits) => new { state = new { position = Midgame }, level = "strong", limits };

    [Fact]
    public async Task HardTimeLimit_Is504()
    {
        await using var factory = new ApiFactoryWith(new StubPool(new HangingEngine()));
        var client = factory.CreateClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/v1/move/suggest", Suggest(new { hardTimeMs = 150 }));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(sw.ElapsedMilliseconds, 100, 3000);
    }

    [Fact]
    public async Task NoWorker_Is503_AndHealthNotOk()
    {
        await using var factory = new ApiFactoryWith(new StubPool(new HangingEngine(), ready: 0));
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/move/suggest", Suggest(new { }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task IllegalEngineMove_Is500()
    {
        await using var factory = new ApiFactoryWith(new StubPool(new ScriptedEngine("22-17")));   // a White move, Black is to move
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/move/suggest", Suggest(new { }));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task TablebasePosition_ReportsWdl()
    {
        var engine = new ScriptedEngine("27-23", tablebase: true, result: "win");
        await using var factory = new ApiFactoryWith(new StubPool(engine, dbPieces: 6));
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/move/suggest",
            new { state = new { position = "W:WK14,K27:B5,6" }, level = "weak" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("info").GetProperty("tablebaseHit").GetBoolean());
        Assert.Equal(1, body.GetProperty("scoreOrWDL").GetInt32());
        Assert.Equal("win", body.GetProperty("info").GetProperty("wdl").GetString());
        Assert.Null(engine.LastLimits!.MaxDepth);           // the DB answers; no depth limit
        Assert.Equal(10, engine.LastLimits.MaxTimeMs);       // Limits:TablebaseTimeMs
    }
}
