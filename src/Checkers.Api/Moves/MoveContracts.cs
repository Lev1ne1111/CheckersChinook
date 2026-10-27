namespace Checkers.Api.Moves;

// Request and response bodies of /v1/move/*. JSON names are camelCase (ASP.NET Core default).

public sealed class SuggestRequest
{
    /// Only "checkers-8x8" (English checkers) is supported. Optional.
    public string? GameId { get; set; }
    public GameState? State { get; set; }
    /// "weak", "medium" (default) or "strong".
    public string? Level { get; set; }
    public RequestLimits? Limits { get; set; }
}

public sealed class GameState
{
    /// Only "PDN" is supported. Optional.
    public string? Notation { get; set; }
    /// PDN FEN, e.g. "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16".
    public string? Position { get; set; }
}

/// Every field optional; they can only make the level's own limits tighter.
public sealed class RequestLimits
{
    public int? MaxDepth { get; set; }
    public int? SoftTimeMs { get; set; }
    public int? HardTimeMs { get; set; }
}

public sealed class SuggestResponse
{
    public string Engine { get; set; } = "chinook";
    /// Full notation with every landing square: "22-18", "22x15", "22x15x8".
    public string BestMove { get; set; } = "";
    public List<string> Pv { get; set; } = new();
    /// Outside the endgame DB: engine score for the side to move (100 ≈ one man).
    /// Inside it: 1 = win, 0 = draw, -1 = loss for the side to move.
    public int ScoreOrWDL { get; set; }
    public int Depth { get; set; }
    public long Nodes { get; set; }
    public string PositionKey { get; set; } = "";
    public SuggestInfo Info { get; set; } = new();
}

public sealed class SuggestInfo
{
    public bool TablebaseHit { get; set; }
    /// Whole request, measured by the API.
    public long TimeMs { get; set; }
    /// "win" / "draw" / "loss" when known for certain (endgame DB), otherwise null.
    public string? Wdl { get; set; }
    public string Level { get; set; } = "";
    public bool Cached { get; set; }
    public long EngineTimeMs { get; set; }
    /// "exact", "lower" or "upper" (the score was only a bound when the search stopped).
    public string? ScoreBound { get; set; }
    public string PositionAfter { get; set; } = "";
    public string? EngineVersion { get; set; }
}

public sealed class ValidateRequest
{
    public string? Position { get; set; }
    public string? Move { get; set; }
}

public sealed class ValidateResponse
{
    public bool Legal { get; set; }
    /// Why the move is not legal.
    public string? Reason { get; set; }
    /// The move in full notation, when legal.
    public string? Move { get; set; }
    /// Position after the move (PDN FEN), when legal.
    public string? PositionAfter { get; set; }
}
