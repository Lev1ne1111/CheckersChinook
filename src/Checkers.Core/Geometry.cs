namespace Checkers.Core;

/// <summary>
/// Board geometry for English checkers with standard square numbers 1–32.
///
/// Coordinates follow the CheckerBoard engine interface (cb_interface.h):
/// square 1 is at (x=6, y=0), square 32 at (x=1, y=7). Row y=0 holds squares 1–4
/// (Black's back rank), row y=7 holds 29–32 (White's back rank).
/// Black men move towards higher numbers (y+1), White men towards lower numbers (y-1).
/// </summary>
public static class Geometry
{
    public const int SquareCount = 32;

    /// Directions: 0,1 = towards square 1 (White forward); 2,3 = towards square 32 (Black forward).
    private static readonly (int dx, int dy)[] Directions = { (-1, -1), (1, -1), (-1, 1), (1, 1) };

    // [square, direction] -> neighbouring square / square two steps away (0 = off the board)
    private static readonly int[,] Step = new int[SquareCount + 1, 4];
    private static readonly int[,] JumpLanding = new int[SquareCount + 1, 4];

    private static readonly int[] WhiteManDirections = { 0, 1 };
    private static readonly int[] BlackManDirections = { 2, 3 };
    private static readonly int[] KingDirections = { 0, 1, 2, 3 };

    static Geometry()
    {
        for (int sq = 1; sq <= SquareCount; sq++)
        {
            var (x, y) = ToCoords(sq);
            for (int d = 0; d < 4; d++)
            {
                var (dx, dy) = Directions[d];
                Step[sq, d] = ToSquare(x + dx, y + dy);
                JumpLanding[sq, d] = ToSquare(x + 2 * dx, y + 2 * dy);
            }
        }
    }

    /// <summary>Square number (1–32) to board coordinates (same as numbertocoors in cb_interface.h).</summary>
    public static (int x, int y) ToCoords(int square)
    {
        if (square is < 1 or > SquareCount) throw new ArgumentOutOfRangeException(nameof(square));
        int n = square - 1;
        int y = n / 4;
        int x = 2 * (3 - n % 4);
        if (y % 2 == 1) x++;
        return (x, y);
    }

    /// <summary>Board coordinates to square number, or 0 if off the board or a light square.</summary>
    public static int ToSquare(int x, int y)
    {
        if (x is < 0 or > 7 || y is < 0 or > 7) return 0;
        if ((x + y) % 2 != 0) return 0;          // playable squares have x+y even in this layout
        return 4 * (y + 1) - x / 2;
    }

    public static int Neighbor(int square, int direction) => Step[square, direction];
    public static int Landing(int square, int direction) => JumpLanding[square, direction];

    /// <summary>Directions a piece may move or capture in (men: forward only; kings: all four).</summary>
    public static int[] DirectionsFor(Piece piece)
    {
        if (piece.IsKing()) return KingDirections;
        return piece.BelongsTo(Side.White) ? WhiteManDirections : BlackManDirections;
    }

    /// <summary>True if a man of this side is crowned on this square.</summary>
    public static bool IsPromotionSquare(int square, Side side) =>
        side == Side.White ? square is >= 1 and <= 4 : square is >= 29 and <= 32;
}
