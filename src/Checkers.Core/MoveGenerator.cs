namespace Checkers.Core;

/// <summary>
/// Legal move generation for English (American) checkers:
/// - captures are mandatory; if any capture exists only captures are legal;
/// - a capture continues while further jumps are available (any capturing sequence may be chosen);
/// - men move and capture forward only, kings in all four directions, one square at a time;
/// - a man reaching the far row is crowned and the move ends there, even mid-capture.
///
/// Verified against the reference perft of CheckerBoard's move generator
/// (see Checkers.Core.Tests/PerftTests).
/// </summary>
public static class MoveGenerator
{
    public static IReadOnlyList<Move> Generate(Position position)
    {
        var board = position.Squares.ToArray();   // working copy, index = square
        Side side = position.SideToMove;

        var captures = new List<Move>();
        for (int sq = 1; sq <= Geometry.SquareCount; sq++)
        {
            Piece piece = board[sq];
            if (!piece.BelongsTo(side)) continue;

            board[sq] = Piece.None;                // the moving piece leaves its square
            FindJumps(board, sq, sq, piece, side, new List<int>(), new List<int>(), captures);
            board[sq] = piece;
        }
        if (captures.Count > 0) return Deduplicate(captures);

        var moves = new List<Move>();
        for (int sq = 1; sq <= Geometry.SquareCount; sq++)
        {
            Piece piece = board[sq];
            if (!piece.BelongsTo(side)) continue;

            foreach (int d in Geometry.DirectionsFor(piece))
            {
                int to = Geometry.Neighbor(sq, d);
                if (to != 0 && board[to] == Piece.None)
                    moves.Add(new Move(sq, new[] { to }, Array.Empty<int>(), piece, Crown(piece, to, side)));
            }
        }
        return moves;
    }

    private static void FindJumps(Piece[] board, int from, int current, Piece piece, Side side,
                                  List<int> path, List<int> captured, List<Move> result)
    {
        bool extended = false;
        foreach (int d in Geometry.DirectionsFor(piece))
        {
            int over = Geometry.Neighbor(current, d);
            int land = Geometry.Landing(current, d);
            if (land == 0 || board[land] != Piece.None) continue;
            if (!board[over].BelongsTo(side.Opponent())) continue;

            extended = true;
            Piece jumped = board[over];
            board[over] = Piece.None;             // a jumped piece cannot be jumped again
            path.Add(land);
            captured.Add(over);

            Piece after = Crown(piece, land, side);
            if (after != piece)
                result.Add(new Move(from, path.ToArray(), captured.ToArray(), piece, after));   // crowning ends the move
            else
                FindJumps(board, from, land, piece, side, path, captured, result);

            path.RemoveAt(path.Count - 1);
            captured.RemoveAt(captured.Count - 1);
            board[over] = jumped;
        }

        if (!extended && captured.Count > 0)
            result.Add(new Move(from, path.ToArray(), captured.ToArray(), piece, piece));
    }

    private static Piece Crown(Piece piece, int square, Side side) =>
        piece.IsMan() && Geometry.IsPromotionSquare(square, side) ? PieceExtensions.KingOf(side) : piece;

    private static IReadOnlyList<Move> Deduplicate(List<Move> moves)
    {
        var seen = new HashSet<Move>();
        var unique = new List<Move>(moves.Count);
        foreach (var m in moves)
            if (seen.Add(m)) unique.Add(m);
        return unique;
    }

    /// <summary>Counts leaf nodes of the move tree to the given depth (for testing the generator).</summary>
    public static long Perft(Position position, int depth)
    {
        if (depth == 0) return 1;
        var moves = Generate(position);
        if (depth == 1) return moves.Count;

        long total = 0;
        foreach (var m in moves) total += Perft(position.Apply(m), depth - 1);
        return total;
    }
}
