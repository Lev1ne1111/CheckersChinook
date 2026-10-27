namespace Checkers.Core;

public enum MoveMatchStatus
{
    /// Exactly one legal move fits (or several that all lead to the same position).
    Found,
    /// The text is not a move ("abc", "22", "22-").
    Invalid,
    /// Well-formed, but no legal move fits.
    Illegal,
}

public sealed record MoveMatch(MoveMatchStatus Status, Move? Move, IReadOnlyList<Move> Candidates, string? Reason)
{
    public bool IsLegal => Status == MoveMatchStatus.Found;
}

/// <summary>
/// Turns move text into a legal <see cref="Move"/> of a position.
///
/// Accepted forms: "22-18", "22x15" (start and end of a capture), "22x15x8" (every landing
/// square), also ':' as separator. A '-' with a capture or 'x' with a simple move is rejected,
/// so "22x18" is not accepted for the simple move 22-18.
///
/// A short capture "9x18" can fit several legal moves (different routes between the same squares).
/// If they all give the same position, the first is returned; otherwise the move is ambiguous and
/// treated as illegal unless the full route is given.
/// </summary>
public static class MoveNotation
{
    public static MoveMatch Match(Position position, string? text)
    {
        if (!TryParse(text, out var squares, out bool? capture))
            return new MoveMatch(MoveMatchStatus.Invalid, null, Array.Empty<Move>(), $"cannot read move '{text}'");

        var legal = position.LegalMoves();
        var candidates = legal.Where(m => Fits(m, squares, capture)).ToList();

        if (candidates.Count == 0)
        {
            string reason = legal.Count == 0
                ? "no legal moves in this position"
                : legal[0].IsCapture && capture != true
                    ? "a capture is mandatory"
                    : "not a legal move";
            return new MoveMatch(MoveMatchStatus.Illegal, null, candidates, reason);
        }

        var first = candidates[0];
        if (candidates.Count > 1)
        {
            var result = position.Apply(first);
            if (candidates.Skip(1).Any(m => !position.Apply(m).Equals(result)))
                return new MoveMatch(MoveMatchStatus.Illegal, null, candidates,
                    "ambiguous capture: give every landing square, e.g. " + candidates[0].ToLongNotation());
        }
        return new MoveMatch(MoveMatchStatus.Found, first, candidates, null);
    }

    /// <summary>
    /// Finds the legal move that turns <paramref name="before"/> into <paramref name="after"/>.
    /// Used to read the engine's reply, which comes back as a changed board.
    /// </summary>
    public static Move? FromResultingPosition(Position before, Position after) =>
        before.LegalMoves().FirstOrDefault(m => before.Apply(m).Equals(after));

    /// <summary>Splits "22x15x8" into squares and separator kind (true = 'x', false = '-', null = ':' only).</summary>
    public static bool TryParse(string? text, out List<int> squares, out bool? capture)
    {
        squares = new List<int>();
        capture = null;
        string s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        bool sawDash = false, sawX = false;
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i == start || i - start > 2) return false;
            int sq = int.Parse(s.AsSpan(start, i - start));
            if (sq is < 1 or > Geometry.SquareCount) return false;
            squares.Add(sq);

            if (i == s.Length) break;
            char sep = s[i++];
            switch (sep)
            {
                case '-': sawDash = true; break;
                case 'x': case 'X': sawX = true; break;
                case ':': break;
                default: return false;
            }
            if (i == s.Length) return false;     // trailing separator
        }

        if (squares.Count < 2 || (sawDash && sawX)) return false;
        if (sawDash && squares.Count > 2) return false;   // simple moves have exactly two squares
        capture = sawX ? true : sawDash ? false : null;
        return true;
    }

    private static bool Fits(Move move, List<int> squares, bool? capture)
    {
        if (capture.HasValue && capture.Value != move.IsCapture) return false;
        if (squares[0] != move.From || squares[^1] != move.To) return false;
        if (squares.Count == 2) return true;
        // full route given: it must match every landing square
        return squares.Count - 1 == move.Path.Count && squares.Skip(1).SequenceEqual(move.Path);
    }
}
