using System.Globalization;
using System.Text.RegularExpressions;

namespace Checkers.EngineProtocol;

/// <summary>
/// One parsed KingsRow status line. Two formats were seen in the engine spike:
///
///   normal search:
///     "value=159,  depth 22/20.4/37,  0.4s,  7726 kN/s,  pv 14x23 27x18 16x23 25-21"
///     value=/&gt;/&lt; is exact / lower bound / upper bound, always from BLACK's point of view;
///     depth = nominal / average / maximum; pv in short notation.
///
///   quick move list (seen in a 6-piece endgame):
///     "depth 6; 22-17* (0.050), 27-24 (0.067), 14-17 (0.133)"
///     the starred move is the one chosen; no score.
///
/// Anything else ("Thinking...", "Wait for db init; ...") is not a search line.
/// </summary>
public sealed record StatusLine(
    int? ValueForBlack,
    string? Bound,          // "exact" / "lower" / "upper"
    int Depth,
    double AvgDepth,
    int MaxDepth,
    double Seconds,
    long KiloNodesPerSecond,
    IReadOnlyList<string> Pv)
{
    private static readonly Regex Normal = new(
        @"^\s*value\s*(?<b>[=<>])\s*(?<v>-?\d+)\s*,\s*depth\s+(?<d>\d+)/(?<avg>[\d.]+)/(?<max>\d+)\s*,\s*(?<t>[\d.]+)\s*s\s*,\s*(?<kn>\d+)\s*kN/s\s*,\s*pv\s*(?<pv>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MoveList = new(
        @"^\s*depth\s+(?<d>\d+)\s*;\s*(?<list>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MoveToken = new(@"^\d{1,2}([-x]\d{1,2})+$", RegexOptions.Compiled);

    public static StatusLine? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = Normal.Match(text);
        if (m.Success)
        {
            var pv = m.Groups["pv"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => MoveToken.IsMatch(t))
                .ToList();
            return new StatusLine(
                int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture),
                m.Groups["b"].Value switch { "=" => "exact", ">" => "lower", _ => "upper" },
                int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["avg"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups["kn"].Value, CultureInfo.InvariantCulture),
                pv);
        }

        m = MoveList.Match(text);
        if (m.Success)
        {
            // "22-17* (0.050), 27-24 (0.067)" -> the starred move
            string? chosen = m.Groups["list"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.Split(' ')[0])
                .FirstOrDefault(t => t.EndsWith('*'))?.TrimEnd('*');
            int depth = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            return new StatusLine(null, null, depth, depth, depth, 0, 0,
                chosen is not null && MoveToken.IsMatch(chosen) ? new[] { chosen } : Array.Empty<string>());
        }

        return null;
    }
}
