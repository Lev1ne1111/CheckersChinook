using Checkers.Core;

namespace Checkers.Core.Tests;

public class MoveGeneratorTests
{
    private static string[] Moves(string fen) =>
        Pdn.Parse(fen).LegalMoves().Select(m => m.ToLongNotation()).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Fact]
    public void StartPosition_BlackHasSevenMoves()
    {
        Assert.Equal(new[] { "10-14", "10-15", "11-15", "11-16", "12-16", "9-13", "9-14" }, Moves("B:W21-32:B1-12"));
    }

    [Fact]
    public void CaptureIsMandatory()
    {
        // Black has simple moves too, but only the two captures are legal
        Assert.Equal(new[] { "14x23", "16x23" }, Moves("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"));
    }

    [Fact]
    public void MultiJumpContinuesToTheEnd()
    {
        var move = Pdn.Parse("W:WK3,20,22,23,24,26,29,30,32:B4,6,8,11,13,15,16").LegalMoves().Single();
        Assert.Equal(3, move.From);
        Assert.Equal(new[] { 12, 19, 10, 1 }, move.Path);
        Assert.Equal(new[] { 8, 16, 15, 6 }, move.Captured);
        Assert.Equal("3x12x19x10x1", move.ToLongNotation());
        Assert.Equal("3x1", move.ToShortNotation());
    }

    [Fact]
    public void CrowningEndsTheCapture()
    {
        var position = Pdn.Parse("B:W20,21,26,28,29,30,32:B1,2,4,8,11,12,22");
        var move = position.LegalMoves().Single();

        Assert.Equal("22x31", move.ToLongNotation());
        Assert.True(move.Promotes);
        Assert.Equal("W:W20,21,28,29,30,32:B1,2,4,8,11,12,K31", position.Apply(move).ToFen());
    }

    [Fact]
    public void ManMovesForwardOnly_KingMovesBothWays()
    {
        // white king on 14 has 4 moves, white men on 22 and 27 only move towards square 1
        Assert.Equal(new[] { "14-10", "14-17", "14-18", "14-9", "22-17", "22-18", "27-23", "27-24" },
                     Moves("W:W22,27,K14:B5,6,K30"));
    }

    [Fact]
    public void ApplyRemovesCapturedPiecesAndSwitchesSide()
    {
        var position = Pdn.Parse("B:W10,17,22:B6,7,8,9,12,K25,K28");
        var doubleJump = position.LegalMoves().Single(m => m.Captured.Count == 2);

        Assert.Equal("7x14x21", doubleJump.ToLongNotation());
        Assert.Equal("W:W22:B6,8,9,12,21,K25,K28", position.Apply(doubleJump).ToFen());
    }

    [Fact]
    public void NoPieces_NoMoves()
    {
        Assert.Empty(Moves("W:W:B1,2"));
    }

    [Fact]
    public void Geometry_RoundTripsAllSquares()
    {
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = Geometry.ToCoords(sq);
            Assert.Equal(sq, Geometry.ToSquare(x, y));
        }
    }
}
