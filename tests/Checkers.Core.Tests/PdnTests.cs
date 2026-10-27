using Checkers.Core;

namespace Checkers.Core.Tests;

public class PdnTests
{
    [Fact]
    public void ParsesSpecPosition()
    {
        var p = Pdn.Parse("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16");

        Assert.Equal(Side.Black, p.SideToMove);
        Assert.Equal(16, p.PieceCount);
        Assert.Equal(Piece.WhiteMan, p[18]);
        Assert.Equal(Piece.BlackMan, p[1]);
        Assert.Equal(Piece.None, p[2]);
    }

    [Theory]
    [InlineData("B:W21-32:B1-12", "B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12")]
    [InlineData("  b:b12,1,K5:w32,k20.  ", "B:WK20,32:B1,K5,12")]                 // case, order, spaces, dot
    [InlineData("[FEN \"W:W18,K22:B5\"]", "W:W18,K22:B5")]                        // PDN tag
    [InlineData("\"W:W18:B5\"", "W:W18:B5")]                                     // quotes
    [InlineData("W:W:B5", "W:W:B5")]                                             // empty list
    public void Canonicalizes(string input, string canonical)
    {
        Assert.Equal(canonical, Pdn.Parse(input).ToFen());
    }

    [Fact]
    public void RoundTrip()
    {
        const string fen = "W:W22,27,K14:B5,6,K30";
        Assert.Equal(Pdn.Parse(fen), Pdn.Parse(Pdn.Parse(fen).ToFen()));
    }

    [Theory]
    [InlineData("", PdnError.Empty)]
    [InlineData("   ", PdnError.Empty)]
    [InlineData("B:W18,19", PdnError.Syntax)]                       // one list missing
    [InlineData("B:W18:B1:B2", PdnError.Syntax)]                    // too many parts
    [InlineData("X:W18:B1", PdnError.BadSideToMove)]
    [InlineData("B:W18:W19", PdnError.BadPieceList)]                // two white lists
    [InlineData("B:Q18:B1", PdnError.BadPieceList)]
    [InlineData("B:W33:B1", PdnError.SquareOutOfRange)]
    [InlineData("B:W0:B1", PdnError.SquareOutOfRange)]
    [InlineData("B:W18,18:B1", PdnError.DuplicateSquare)]
    [InlineData("B:W18:B18", PdnError.DuplicateSquare)]
    [InlineData("B:W18,abc:B1", PdnError.Syntax)]
    [InlineData("B:W18,,19:B1", PdnError.Syntax)]
    [InlineData("B:W12-5:B1", PdnError.Syntax)]                     // backwards range
    [InlineData("B:W1-13:B20", PdnError.ManOnCrowningRow)]          // white man on 1..4
    [InlineData("B:W5-17:B20", PdnError.TooManyPieces)]             // 13 white pieces
    [InlineData("B:W18:B29", PdnError.ManOnCrowningRow)]            // black man on 29..32
    public void RejectsInvalid(string input, PdnError expected)
    {
        var ex = Assert.Throws<PdnException>(() => Pdn.Parse(input));
        Assert.Equal(expected, ex.Error);
    }

    [Fact]
    public void KingsMayStandOnCrowningRow()
    {
        var p = Pdn.Parse("W:WK1:BK32");
        Assert.Equal(Piece.WhiteKing, p[1]);
        Assert.Equal(Piece.BlackKing, p[32]);
    }

    [Fact]
    public void TryParse_ReportsError()
    {
        Assert.False(Pdn.TryParse("B:W33:B1", out var position, out var error));
        Assert.Null(position);
        Assert.Equal(PdnError.SquareOutOfRange, error!.Error);
    }
}
