using Checkers.EngineWorker.Native;

namespace Checkers.EngineWorker.Spike;

/// <summary>
/// Minimal PDN FEN reader/writer for the engine spike only ("B:W18,19,K22:B1-12").
/// The real parser with validation will live in Checkers.Core.
/// </summary>
internal static class FenLite
{
    public static (int[] squares, int color) Parse(string fen)
    {
        var squares = new int[32];
        string[] parts = fen.Trim().TrimEnd('.').Split(':');
        if (parts.Length != 3) throw new FormatException("expected 'B:W..:B..'");

        int color = parts[0].Trim().ToUpperInvariant() switch
        {
            "B" => Cb.Black,
            "W" => Cb.White,
            var s => throw new FormatException($"side to move '{s}'")
        };

        foreach (var side in parts[1..])
        {
            string s = side.Trim();
            int pieceColor = char.ToUpperInvariant(s[0]) switch
            {
                'W' => Cb.White,
                'B' => Cb.Black,
                var c => throw new FormatException($"piece list must start with W or B, got '{c}'")
            };
            foreach (var raw in s[1..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool king = raw.StartsWith('K') || raw.StartsWith('k');
                string token = king ? raw[1..] : raw;
                int from, to;
                if (token.Contains('-'))
                {
                    var r = token.Split('-');
                    from = int.Parse(r[0]); to = int.Parse(r[1]);
                }
                else from = to = int.Parse(token);

                for (int sq = from; sq <= to; sq++)
                {
                    if (sq is < 1 or > 32) throw new FormatException($"square {sq} out of range");
                    squares[sq - 1] = pieceColor | (king ? Cb.King : Cb.Man);
                }
            }
        }
        return (squares, color);
    }

    public static string ToFen(int[] squares, int color)
    {
        string List(int c) => string.Join(",", Enumerable.Range(1, 32)
            .Where(sq => (squares[sq - 1] & c) != 0)
            .Select(sq => (squares[sq - 1] & Cb.King) != 0 ? $"K{sq}" : sq.ToString()));
        return $"{(color == Cb.Black ? "B" : "W")}:W{List(Cb.White)}:B{List(Cb.Black)}";
    }

    public static int CountPieces(int[] squares) => squares.Count(p => p != Cb.Free);

    /// <summary>
    /// Works out the move by comparing the board before and after getmove().
    /// Gives "from-to" or "fromxto"; the intermediate squares of a multi-jump are not
    /// visible this way. Checkers.Core will later match it against the legal move list.
    /// </summary>
    public static string DiffToMove(int[] before, int[] after, int color)
    {
        int opp = color == Cb.Black ? Cb.White : Cb.Black;
        var from = new List<int>(); var to = new List<int>(); var captured = new List<int>();
        for (int sq = 1; sq <= 32; sq++)
        {
            int b = before[sq - 1], a = after[sq - 1];
            if (b == a) continue;
            if ((b & color) != 0 && (a & color) == 0) from.Add(sq);
            if ((a & color) != 0 && (b & color) == 0) to.Add(sq);
            if ((b & opp) != 0 && (a & opp) == 0) captured.Add(sq);
        }
        if (from.Count == 0 && to.Count == 0)
            return captured.Count > 0 ? $"(same square, captured {string.Join(",", captured)})" : "(no change)";
        if (from.Count != 1 || to.Count != 1)
            return $"(unexpected diff: from {string.Join(",", from)} to {string.Join(",", to)})";

        string sep = captured.Count > 0 ? "x" : "-";
        string move = $"{from[0]}{sep}{to[0]}";
        return captured.Count > 0 ? $"{move}  (captured {string.Join(",", captured)})" : move;
    }
}
