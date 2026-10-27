namespace Checkers.Core;

/// <summary>Side in English checkers. Black moves first and starts on squares 1–12.</summary>
public enum Side : byte
{
    White = 1,
    Black = 2,
}

/// <summary>
/// Piece on a square. The numeric values are the same as the CheckerBoard engine
/// interface (CB_WHITE=1, CB_BLACK=2, CB_MAN=4, CB_KING=8), so a board can be handed
/// to the engine without translation.
/// </summary>
[Flags]
public enum Piece : byte
{
    None = 0,
    White = 1,
    Black = 2,
    Man = 4,
    King = 8,

    WhiteMan = White | Man,
    BlackMan = Black | Man,
    WhiteKing = White | King,
    BlackKing = Black | King,
}

public static class PieceExtensions
{
    public static Side Opponent(this Side side) => side == Side.White ? Side.Black : Side.White;

    public static bool IsEmpty(this Piece p) => p == Piece.None;
    public static bool IsKing(this Piece p) => (p & Piece.King) != 0;
    public static bool IsMan(this Piece p) => (p & Piece.Man) != 0;

    public static bool BelongsTo(this Piece p, Side side) => ((byte)p & (byte)side) != 0;

    public static Side? Owner(this Piece p) =>
        p.BelongsTo(Side.White) ? Side.White : p.BelongsTo(Side.Black) ? Side.Black : null;

    public static Piece ManOf(Side side) => (Piece)((byte)side | (byte)Piece.Man);
    public static Piece KingOf(Side side) => (Piece)((byte)side | (byte)Piece.King);

    public static char ToLetter(this Side side) => side == Side.White ? 'W' : 'B';
}
