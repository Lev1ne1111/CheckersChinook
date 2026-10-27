using System.Text;

namespace Checkers.Core;

/// <summary>Why a PDN position was rejected. The API maps every code to HTTP 422.</summary>
public enum PdnError
{
    Empty,
    Syntax,
    BadSideToMove,
    BadPieceList,
    SquareOutOfRange,
    DuplicateSquare,
    TooManyPieces,
    ManOnCrowningRow,
}

public sealed class PdnException : FormatException
{
    public PdnError Error { get; }

    public PdnException(PdnError error, string message) : base(message) => Error = error;
}

/// <summary>
/// Reads and writes positions in PDN FEN notation, e.g. "B:W18,19,K22:B1-12".
///
/// Accepted input (lenient):
///   - side to move B or W, then two piece lists starting with W and B, in either order;
///   - K before a square marks a king; ranges like 1-12 are expanded;
///   - surrounding whitespace, quotes, a [FEN "..."] tag and a trailing '.' are ignored;
///   - letters may be lower case; an empty list ("W" with no squares) is allowed.
/// Checked: squares 1–32, no square used twice, at most 12 pieces per side,
/// no man standing on the row where it would already have been crowned.
///
/// Output (<see cref="ToFen"/>) is canonical: "B:W18,19,K22:B1,5", White list first, ascending.
/// </summary>
public static class Pdn
{
    public const int MaxPiecesPerSide = 12;

    public static Position Parse(string? text)
    {
        string fen = Unwrap(text);
        if (fen.Length == 0) throw new PdnException(PdnError.Empty, "position is empty");

        string[] parts = fen.Split(':');
        if (parts.Length != 3)
            throw new PdnException(PdnError.Syntax, $"expected 'side:Wlist:Blist', got {parts.Length} part(s)");

        Side sideToMove = parts[0].Trim().ToUpperInvariant() switch
        {
            "W" => Side.White,
            "B" => Side.Black,
            var s => throw new PdnException(PdnError.BadSideToMove, $"side to move must be W or B, got '{s}'"),
        };

        var squares = new Piece[Geometry.SquareCount + 1];
        var seenLists = new HashSet<Side>();

        foreach (string rawList in parts[1..])
        {
            string list = rawList.Trim();
            if (list.Length == 0) throw new PdnException(PdnError.BadPieceList, "empty piece list");

            Side owner = char.ToUpperInvariant(list[0]) switch
            {
                'W' => Side.White,
                'B' => Side.Black,
                var c => throw new PdnException(PdnError.BadPieceList, $"piece list must start with W or B, got '{c}'"),
            };
            if (!seenLists.Add(owner))
                throw new PdnException(PdnError.BadPieceList, $"two piece lists for {owner}");

            foreach (string rawToken in list[1..].Split(',', StringSplitOptions.TrimEntries))
            {
                if (rawToken.Length == 0)
                {
                    if (list.Length > 1) throw new PdnException(PdnError.Syntax, $"empty entry in '{list}'");
                    continue;
                }
                ParseToken(rawToken, owner, squares);
            }
        }

        foreach (Side side in new[] { Side.White, Side.Black })
        {
            int count = squares.Count(p => p.BelongsTo(side));
            if (count > MaxPiecesPerSide)
                throw new PdnException(PdnError.TooManyPieces, $"{side} has {count} pieces, at most {MaxPiecesPerSide} allowed");
        }

        return new Position(squares, sideToMove);
    }

    public static bool TryParse(string? text, out Position? position, out PdnException? error)
    {
        try
        {
            position = Parse(text);
            error = null;
            return true;
        }
        catch (PdnException ex)
        {
            position = null;
            error = ex;
            return false;
        }
    }

    public static string ToFen(Position position)
    {
        var sb = new StringBuilder();
        sb.Append(position.SideToMove.ToLetter());
        foreach (Side side in new[] { Side.White, Side.Black })
        {
            sb.Append(':').Append(side.ToLetter());
            bool first = true;
            for (int sq = 1; sq <= Geometry.SquareCount; sq++)
            {
                Piece p = position[sq];
                if (!p.BelongsTo(side)) continue;
                if (!first) sb.Append(',');
                if (p.IsKing()) sb.Append('K');
                sb.Append(sq);
                first = false;
            }
        }
        return sb.ToString();
    }

    private static void ParseToken(string token, Side owner, Piece[] squares)
    {
        bool king = token[0] is 'K' or 'k';
        string body = king ? token[1..] : token;

        int dash = body.IndexOf('-');
        int first = ParseSquare(dash < 0 ? body : body[..dash], token);
        int last = dash < 0 ? first : ParseSquare(body[(dash + 1)..], token);
        if (last < first) throw new PdnException(PdnError.Syntax, $"bad range '{token}'");

        Piece piece = king ? PieceExtensions.KingOf(owner) : PieceExtensions.ManOf(owner);
        for (int sq = first; sq <= last; sq++)
        {
            if (squares[sq] != Piece.None)
                throw new PdnException(PdnError.DuplicateSquare, $"square {sq} is used twice");
            if (!king && Geometry.IsPromotionSquare(sq, owner))
                throw new PdnException(PdnError.ManOnCrowningRow, $"{owner} man on square {sq} should already be a king");
            squares[sq] = piece;
        }
    }

    private static int ParseSquare(string s, string token)
    {
        if (s.Length == 0 || !s.All(char.IsAsciiDigit) || s.Length > 2)
            throw new PdnException(PdnError.Syntax, $"bad square '{token}'");
        int sq = int.Parse(s);
        if (sq is < 1 or > Geometry.SquareCount)
            throw new PdnException(PdnError.SquareOutOfRange, $"square {sq} out of range 1-{Geometry.SquareCount}");
        return sq;
    }

    private static string Unwrap(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.StartsWith("[FEN", StringComparison.OrdinalIgnoreCase) && s.EndsWith(']'))
            s = s[4..^1].Trim();
        s = s.Trim('"').Trim();
        if (s.EndsWith('.')) s = s[..^1].TrimEnd();
        return s;
    }
}
