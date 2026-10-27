namespace Checkers.Api.Options;

/// <summary>The "Cache" section of appsettings.json: LRU cache of /v1/move/suggest results.</summary>
public sealed class CacheOptions
{
    public const string Section = "Cache";

    /// Maximum number of cached positions; the least recently used one is dropped first. 0 = no cache.
    public int Capacity { get; set; } = 20_000;
    public int TtlMinutes { get; set; } = 15;
}

/// <summary>The "Limits" section of appsettings.json.</summary>
public sealed class LimitsOptions
{
    public const string Section = "Limits";

    /// Move time for a request without a level (it gets medium's depth limit with this time).
    public int DefaultSoftTimeMs { get; set; } = 300;

    /// Used when a request gives no hardTimeMs. When it passes, the request ends with 504.
    public int DefaultHardTimeMs { get; set; } = 1200;

    /// Upper bound for hardTimeMs from a request.
    public int MaxHardTimeMs { get; set; } = 10_000;

    /// Engine time for a position inside the endgame database (the answer comes from the DB).
    public int TablebaseTimeMs { get; set; } = 10;
}

/// <summary>The "RequestLog" section: one JSON line per API request.</summary>
public sealed class RequestLogOptions
{
    public const string Section = "RequestLog";

    /// Folder for requests-YYYYMMDD.jsonl; a relative path is resolved against the app folder.
    /// Empty = only the normal ILogger output.
    public string Directory { get; set; } = "logs";
}
