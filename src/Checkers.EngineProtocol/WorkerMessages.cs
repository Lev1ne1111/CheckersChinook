using System.Text.Json;
using System.Text.Json.Serialization;

namespace Checkers.EngineProtocol;

/// <summary>
/// Protocol between the API and an engine worker process: one JSON object per line.
///
/// API → worker (stdin):
///   {"id":1,"cmd":"init","dll":"C:\\engines\\kingsrow\\Kingsrow64.dll","dbPath":"D:\\tb\\chinook","hashMb":128,"dbMb":1024}
///   {"id":2,"cmd":"search","fen":"B:W18,...:B1,...","maxTimeMs":500,"hardTimeMs":1200,"exact":true,"maxDepth":12}
///   {"cmd":"stop"}                     stop the running search now (no reply of its own)
///   {"id":3,"cmd":"ping"}              answered at once, even during a search
///   {"cmd":"quit"}
///
/// Worker → API (stdout): one response per request id, e.g.
///   {"id":2,"ok":true,"search":{...}}  or  {"id":2,"ok":false,"error":"..."}
/// Diagnostics go to stderr, never to stdout.
///
/// Commands other than stop/ping are handled one at a time in arrival order.
/// </summary>
public static class Commands
{
    public const string Init = "init";
    public const string Search = "search";
    public const string Stop = "stop";
    public const string Ping = "ping";
    public const string Quit = "quit";
}

public sealed class WorkerRequest
{
    public long? Id { get; set; }
    public string Cmd { get; set; } = "";

    // init
    public string? Dll { get; set; }
    public string? DbPath { get; set; }
    public string? WorkDir { get; set; }
    /// Writable folder used as Documents for the engine's log when the account has none (IIS app pools).
    public string? HomeDir { get; set; }
    public int? HashMb { get; set; }
    public int? DbMb { get; set; }
    public int? Book { get; set; }

    // search
    public string? Fen { get; set; }
    public int? MaxTimeMs { get; set; }
    public int? HardTimeMs { get; set; }
    public bool? Exact { get; set; }
    /// Stop as soon as the engine reports this nominal depth (null = no depth limit).
    public int? MaxDepth { get; set; }
}

public sealed class WorkerResponse
{
    public long? Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? Busy { get; set; }                 // ping only
    public InitResult? Init { get; set; }
    public SearchResult? Search { get; set; }

    public static WorkerResponse Fail(long? id, string error) => new() { Id = id, Ok = false, Error = error };
}

public sealed class InitResult
{
    public string EngineName { get; set; } = "";
    public string About { get; set; } = "";
    /// Largest piece count covered by the endgame DB the engine reports (0 = none).
    public int DbPieces { get; set; }
    public long WarmupMs { get; set; }
}

public sealed class SearchResult
{
    /// Best move in full notation with every landing square ("22x15x8").
    public string BestMove { get; set; } = "";
    /// Principal variation in full notation; starts with BestMove.
    public List<string> Pv { get; set; } = new();
    /// Score from the side-to-move's point of view (positive = good for the side to move).
    public int? Score { get; set; }
    /// "exact", "lower" or "upper" (the engine had only a bound when it stopped).
    public string? ScoreBound { get; set; }
    public int Depth { get; set; }
    public int SelDepth { get; set; }
    /// Estimate: engine speed × measured time (KingsRow reports no node count).
    public long Nodes { get; set; }
    /// Engine's verdict: "win", "loss", "draw" (known for certain, e.g. from the endgame DB) or "unknown".
    public string Result { get; set; } = "unknown";
    /// The position is covered by the endgame database, so Result is exact.
    public bool TablebaseHit { get; set; }
    public int Pieces { get; set; }
    public long ElapsedMs { get; set; }
    public bool Stopped { get; set; }
    public string FenAfter { get; set; } = "";
    public string RawStatus { get; set; } = "";
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
