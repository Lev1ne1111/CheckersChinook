namespace Checkers.Api.Moves;

public enum Level
{
    Weak,
    Medium,
    Strong,
}

/// <summary>
/// Strength levels from the spec. The engine stops at whichever comes first: the depth or the
/// move time. Every level is deterministic (no opening book, no randomness).
///   weak:   depth 7,  at most 100 ms   (spec: depth 6–8)
///   medium: depth 11, at most 250 ms   (spec: depth 10–12)
///   strong: 500 ms, no depth cap       (spec: depth 14–18 or 500–600 ms)
/// The depth cap is approximate: KingsRow does not report every iteration, so the depth it ends
/// on is within one of the cap. The caps sit in the middle of the spec's ranges for that reason.
/// KingsRow is much faster than the spec assumed: depth 18 takes it about 40 ms, so a depth cap
/// would make "strong" barely stronger than "medium". Strong therefore searches for its full
/// move time and reaches depth 18–25 in a midgame. A maxDepth in the request still caps it.
/// Positions in the endgame database are answered from it at every level (see MoveService).
/// </summary>
public sealed record LevelProfile(Level Level, int? MaxDepth, int MoveTimeMs)
{
    public static readonly LevelProfile Weak = new(Level.Weak, 7, 100);
    public static readonly LevelProfile Medium = new(Level.Medium, 11, 250);
    public static readonly LevelProfile Strong = new(Level.Strong, null, 500);

    public string Name => Level.ToString().ToLowerInvariant();

    /// Null or empty = medium. Returns false for an unknown name.
    public static bool TryParse(string? name, out LevelProfile profile)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "": case "medium": profile = Medium; return true;
            case "weak": profile = Weak; return true;
            case "strong": profile = Strong; return true;
            default: profile = Medium; return false;
        }
    }
}
