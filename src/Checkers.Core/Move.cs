namespace Checkers.Core;

/// <summary>
/// A legal move. For a simple move Path = [to]; for a capture Path lists every landing
/// square in order and Captured lists the jumped squares in the same order.
/// </summary>
public sealed class Move : IEquatable<Move>
{
    public int From { get; }
    public IReadOnlyList<int> Path { get; }
    public IReadOnlyList<int> Captured { get; }

    /// <summary>The piece before and after the move (differs when a man is crowned).</summary>
    public Piece Piece { get; }
    public Piece PieceAfter { get; }

    public int To => Path[^1];
    public bool IsCapture => Captured.Count > 0;
    public bool Promotes => Piece != PieceAfter;

    public Move(int from, IReadOnlyList<int> path, IReadOnlyList<int> captured, Piece piece, Piece pieceAfter)
    {
        if (path.Count == 0) throw new ArgumentException("a move needs at least one landing square", nameof(path));
        if (captured.Count != 0 && captured.Count != path.Count)
            throw new ArgumentException("a capture needs one jumped square per landing square", nameof(captured));
        From = from;
        Path = path;
        Captured = captured;
        Piece = piece;
        PieceAfter = pieceAfter;
    }

    /// <summary>Standard short PDN notation: "22-18", "22x15" (only start and end of a multi-jump).</summary>
    public string ToShortNotation() => $"{From}{(IsCapture ? 'x' : '-')}{To}";

    /// <summary>Full notation with every landing square: "22-18", "22x15x8".</summary>
    public string ToLongNotation() =>
        IsCapture ? $"{From}x{string.Join("x", Path)}" : $"{From}-{To}";

    public override string ToString() => ToLongNotation();

    public bool Equals(Move? other) =>
        other is not null &&
        From == other.From &&
        Path.SequenceEqual(other.Path) &&
        Captured.SequenceEqual(other.Captured);

    public override bool Equals(object? obj) => Equals(obj as Move);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(From);
        foreach (var s in Path) h.Add(s);
        foreach (var s in Captured) h.Add(s);
        return h.ToHashCode();
    }
}
