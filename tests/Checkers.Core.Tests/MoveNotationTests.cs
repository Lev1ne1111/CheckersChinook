using Checkers.Core;

namespace Checkers.Core.Tests;

public class MoveNotationTests
{
    private const string Spec = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";

    [Theory]
    [InlineData("B:W21-32:B1-12", "11-15")]
    [InlineData("B:W21-32:B1-12", "11:15")]
    [InlineData(Spec, "14x23")]
    [InlineData(Spec, "16X23")]
    [InlineData("B:W10,17,22:B6,7,8,9,12,K25,K28", "7x21")]          // short form of a double jump
    [InlineData("B:W10,17,22:B6,7,8,9,12,K25,K28", "7x14x21")]       // full route
    public void AcceptsLegalMoves(string fen, string move)
    {
        Assert.True(MoveNotation.Match(Pdn.Parse(fen), move).IsLegal);
    }

    [Theory]
    [InlineData(Spec, "1-5", MoveMatchStatus.Illegal)]               // own piece in the way
    [InlineData(Spec, "10-15", MoveMatchStatus.Illegal)]             // capture is mandatory
    [InlineData(Spec, "14-23", MoveMatchStatus.Illegal)]             // capture written with '-'
    [InlineData("B:W21-32:B1-12", "11x15", MoveMatchStatus.Illegal)] // simple move written with 'x'
    [InlineData("B:W10,17,22:B6,7,8,9,12,K25,K28", "7x14x22", MoveMatchStatus.Illegal)]  // wrong route
    [InlineData(Spec, "", MoveMatchStatus.Invalid)]
    [InlineData(Spec, "14", MoveMatchStatus.Invalid)]
    [InlineData(Spec, "14x", MoveMatchStatus.Invalid)]
    [InlineData(Spec, "14x33", MoveMatchStatus.Invalid)]
    [InlineData(Spec, "a-b", MoveMatchStatus.Invalid)]
    [InlineData(Spec, "22-18x11-7", MoveMatchStatus.Invalid)]       // mixed separators
    public void RejectsBadMoves(string fen, string move, MoveMatchStatus expected)
    {
        Assert.Equal(expected, MoveNotation.Match(Pdn.Parse(fen), move).Status);
    }

    [Fact]
    public void MandatoryCapture_HasReason()
    {
        var match = MoveNotation.Match(Pdn.Parse(Spec), "10-15");
        Assert.Equal("a capture is mandatory", match.Reason);
    }

    [Fact]
    public void TwoRoutesToSamePosition_AreNotAmbiguous()
    {
        // the king can take the same five pieces clockwise or anticlockwise
        var match = MoveNotation.Match(Pdn.Parse("W:WK9:B4,5,10,14,15,16,K23,24,K32"), "9x18");
        Assert.True(match.IsLegal);
        Assert.Equal(2, match.Candidates.Count);
    }

    [Fact]
    public void FindsEngineMoveFromResultingBoard()
    {
        var before = Pdn.Parse(Spec);
        var after = Pdn.Parse("W:W19,22,25,27,28,30,32:B1,5,6,7,10,12,16,23");
        Assert.Equal("14x23", MoveNotation.FromResultingPosition(before, after)!.ToLongNotation());
    }
}
