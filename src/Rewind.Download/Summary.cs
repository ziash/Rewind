using System.Globalization;

namespace Rewind.Download;

/// <summary>The check after each run (spec §4): candles per symbol, gaps, spreads.</summary>
internal static class Summary
{
    private static readonly TimeSpan GapToReport = TimeSpan.FromHours(1);

    public static string Of(Instrument i, IReadOnlyList<Candle> m1, IReadOnlyList<DateOnly> missing)
    {
        if (m1.Count == 0)
            return $"{i.Name}: no data";

        var gaps = Gaps(m1, i.Forex).ToList();
        var spreads = m1.Select(c => c.AskClose - c.BidClose).Order().ToList();
        string P(long v) => (v / (decimal)i.Factor).ToString("F" + i.Digits, CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"{i.Name}: {m1[0].TimeUtc:yyyy-MM-dd} → {m1[^1].TimeUtc:yyyy-MM-dd}, {m1.Count:N0} 1-min candles, " +
            $"{m1.Select(c => c.TimeUtc.Date).Distinct().Count()} days with trading" +
            (missing.Count > 0 ? $", {missing.Count} day(s) not downloaded yet" : ""),
            $"    price {P(m1.Min(c => c.BidLow))} – {P(m1.Max(c => c.BidHigh))}; spread at close: smallest {P(spreads[0])}, " +
            $"median {P(spreads[spreads.Count / 2])}, 99 % below {P(spreads[(int)(spreads.Count * 0.99)])}, largest {P(spreads[^1])}",
            $"    {gaps.Count} gap(s) over {GapToReport.TotalHours:0} h{(i.Forex ? " outside weekends" : "")}" +
            (gaps.Count > 0 ? "; largest: " + string.Join(", ", gaps.OrderByDescending(g => g.Length).Take(3)
                .Select(g => $"{g.Start:yyyy-MM-dd HH:mm} ({g.Length.TotalHours:0.#} h)")) : ""),
        };
        return string.Join("\n", lines);
    }

    /// <summary>Gaps between consecutive candles longer than an hour; for forex, the weekend close is not a gap.</summary>
    internal static IEnumerable<(DateTime Start, TimeSpan Length)> Gaps(IReadOnlyList<Candle> m1, bool forex)
    {
        for (var n = 1; n < m1.Count; n++)
        {
            DateTime start = m1[n - 1].TimeUtc.AddMinutes(1), end = m1[n].TimeUtc;
            if (end - start <= GapToReport || (forex && IsWeekend(start, end)))
                continue;
            yield return (start, end - start);
        }
    }

    /// <summary>Forex closes Friday ~21:00–22:00 UTC and opens Sunday ~21:00–22:00 UTC (New York 17:00, summer / winter).</summary>
    private static bool IsWeekend(DateTime start, DateTime end) =>
        ((start.DayOfWeek == DayOfWeek.Friday && start.Hour >= 20) || start.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        && (end.DayOfWeek == DayOfWeek.Sunday || (end.DayOfWeek == DayOfWeek.Monday && end.Hour == 0))
        && end - start < TimeSpan.FromDays(3);
}
