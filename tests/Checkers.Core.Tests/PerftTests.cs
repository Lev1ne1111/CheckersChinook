using Checkers.Core;

namespace Checkers.Core.Tests;

/// <summary>
/// Perft = number of leaf nodes of the full move tree to a given depth.
/// Expected values come from the Perft tool shipped with CheckerBoard (Martin Fierz's
/// move generator), so a match means our rules (mandatory capture, multi-jumps,
/// crowning, kings) agree with an independent, long-used implementation.
/// </summary>
public class PerftTests
{
    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 49)]
    [InlineData(3, 302)]
    [InlineData(4, 1469)]
    [InlineData(5, 7361)]
    [InlineData(6, 36768)]
    [InlineData(7, 179740)]
    [InlineData(8, 845931)]
    public void StartPosition(int depth, long expected) =>
        Assert.Equal(expected, MoveGenerator.Perft(Position.Start, depth));

    [Theory]
    // midgame position from the spec: black must capture
    [InlineData("B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16", new long[] { 2, 2, 8, 21, 100, 320, 1558 })]
    // 6 pieces with kings on both sides
    [InlineData("W:W22,27,K14:B5,6,K30", new long[] { 8, 33, 160, 555, 2602, 9527, 41387 })]
    // two kings against two men
    [InlineData("W:WK14,K27:B5,6", new long[] { 8, 21, 92, 198, 857, 2221, 10336 })]
    // a man is crowned in the middle of a capture, which ends the move
    [InlineData("B:W20,21,26,28,29,30,32:B1,2,4,8,11,12,22", new long[] { 1, 7, 40, 192, 1104, 5368 })]
    // king captures four pieces in one move
    [InlineData("W:WK3,20,22,23,24,26,29,30,32:B4,6,8,11,13,15,16", new long[] { 1, 4, 26, 84, 425, 1420 })]
    // several captures to choose from, including a double jump and a king capture
    [InlineData("B:W10,17,22:B6,7,8,9,12,K25,K28", new long[] { 3, 7, 47, 110, 845, 1315 })]
    // a king can take the same five pieces along two different routes
    [InlineData("W:WK9:B4,5,10,14,15,16,K23,24,K32", new long[] { 2, 12, 36, 126, 398, 2242 })]
    // only kings
    [InlineData("B:WK2,K18:BK17,K24,K27,K31", new long[] { 10, 45, 302, 1629, 12655, 72426 })]
    public void TrickyPositions(string fen, long[] expected)
    {
        var position = Pdn.Parse(fen);
        for (int depth = 1; depth <= expected.Length; depth++)
            Assert.True(expected[depth - 1] == MoveGenerator.Perft(position, depth),
                $"perft({depth}) of {fen}: expected {expected[depth - 1]}, got {MoveGenerator.Perft(position, depth)}");
    }
}
