namespace Checkers.Core;

/// <summary>
/// Immutable position: what stands on squares 1–32 and whose turn it is.
/// Create one with <see cref="Pdn.Parse"/> or <see cref="Position.Start"/>.
/// </summary>
public sealed class Position : IEquatable<Position>
{
    // index = square number; index 0 is unused so that squares read naturally
    private readonly Piece[] _squares;

    public Side SideToMove { get; }

    public Position(IReadOnlyList<Piece> squares, Side sideToMove)
    {
        if (squares.Count != Geometry.SquareCount + 1)
            throw new ArgumentException($"expected {Geometry.SquareCount + 1} entries (index 0 unused)", nameof(squares));
        _squares = squares.ToArray();
        _squares[0] = Piece.None;
        SideToMove = sideToMove;
    }

    private Position(Piece[] squares, Side sideToMove, bool _)
    {
        _squares = squares;
        SideToMove = sideToMove;
    }

    public static Position Start { get; } = Pdn.Parse("B:W21-32:B1-12");

    public Piece this[int square] =>
        square is >= 1 and <= Geometry.SquareCount
            ? _squares[square]
            : throw new ArgumentOutOfRangeException(nameof(square));

    public int PieceCount => _squares.Count(p => p != Piece.None);
    public int CountOf(Side side) => _squares.Count(p => p.BelongsTo(side));

    /// <summary>Squares 1–32 with the pieces on them (index 0 unused).</summary>
    public IReadOnlyList<Piece> Squares => Array.AsReadOnly(_squares);

    public IReadOnlyList<Move> LegalMoves() => MoveGenerator.Generate(this);

    /// <summary>Plays a move (assumed legal for this position) and returns the new position.</summary>
    public Position Apply(Move move)
    {
        var next = (Piece[])_squares.Clone();
        next[move.From] = Piece.None;
        foreach (var c in move.Captured) next[c] = Piece.None;
        next[move.To] = move.PieceAfter;
        return new Position(next, SideToMove.Opponent(), true);
    }

    /// <summary>Canonical PDN FEN: side to move, White list, Black list, ascending, kings with K.</summary>
    public string ToFen() => Pdn.ToFen(this);

    public override string ToString() => ToFen();

    public bool Equals(Position? other) =>
        other is not null && SideToMove == other.SideToMove && _squares.AsSpan().SequenceEqual(other._squares);

    public override bool Equals(object? obj) => Equals(obj as Position);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(SideToMove);
        foreach (var p in _squares) h.Add(p);
        return h.ToHashCode();
    }
}
