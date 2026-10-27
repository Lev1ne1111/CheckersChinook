using Checkers.EngineProtocol;

namespace Checkers.Core.Tests;

/// <summary>Status lines copied from the KingsRow 1.20 engine spike.</summary>
public class StatusLineTests
{
    [Fact]
    public void ParsesNormalSearchLine()
    {
        var s = StatusLine.Parse("value=159,  depth 22/20.4/37,  0.4s,  7726 kN/s,  pv 14x23 27x18 16x23 25-21 12-16 28-24 16-19 24x15 10x19 21-17 19-24")!;

        Assert.Equal(159, s.ValueForBlack);
        Assert.Equal("exact", s.Bound);
        Assert.Equal(22, s.Depth);
        Assert.Equal(20.4, s.AvgDepth, 3);
        Assert.Equal(37, s.MaxDepth);
        Assert.Equal(0.4, s.Seconds, 3);
        Assert.Equal(7726, s.KiloNodesPerSecond);
        Assert.Equal(11, s.Pv.Count);
        Assert.Equal("14x23", s.Pv[0]);
        Assert.Equal("19-24", s.Pv[^1]);
    }

    [Theory]
    [InlineData("value>6,  depth 11/11.2/22,  0.0s,  6852 kN/s,  pv 11-15 22-17", 6, "lower")]
    [InlineData("value<126,  depth 17/17.0/30,  0.0s,  7559 kN/s,  pv 14x23", 126, "upper")]
    [InlineData("value=-3991,  depth 95/16.3/29,  0.1s,  7175 kN/s,  pv 27-23 6-10 14x7", -3991, "exact")]
    public void ParsesBoundsAndNegativeValues(string line, int value, string bound)
    {
        var s = StatusLine.Parse(line)!;
        Assert.Equal(value, s.ValueForBlack);
        Assert.Equal(bound, s.Bound);
    }

    [Fact]
    public void ParsesQuickMoveList()
    {
        var s = StatusLine.Parse("depth 6; 22-17* (0.050), 27-24 (0.067), 14-17 (0.133), 22-18 (0.206), 14-18 (1.000),")!;

        Assert.Null(s.ValueForBlack);
        Assert.Equal(6, s.Depth);
        Assert.Equal(new[] { "22-17" }, s.Pv);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Thinking...")]
    [InlineData("Wait for db init; Chinook db, 6 pieces, 512 mb cache, 128 mb hashtable ...")]
    public void IgnoresOtherText(string? line)
    {
        Assert.Null(StatusLine.Parse(line));
    }

    [Fact]
    public void ProtocolRoundTrip()
    {
        var json = ProtocolJson.Serialize(new WorkerRequest { Id = 7, Cmd = "search", Fen = "B:W21-32:B1-12", MaxTimeMs = 250 });
        Assert.Equal("""{"id":7,"cmd":"search","fen":"B:W21-32:B1-12","maxTimeMs":250}""", json);

        var back = ProtocolJson.Deserialize<WorkerRequest>(json)!;
        Assert.Equal(7, back.Id);
        Assert.Equal(250, back.MaxTimeMs);
    }
}
