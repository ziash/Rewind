namespace Rewind.History;

public enum Timeframe { M5, M15, H1, H4, D1 }

/// <summary>A chart candle (bid prices), as the browser draws it.</summary>
public readonly record struct ChartCandle(DateTime TimeUtc, double Open, double High, double Low, double Close);

/// <summary>
/// Builds the chart's candles from the 5-minute history (spec §5). M5, M15 and H1 follow the UTC clock. H4 and D1
/// start at 17:00 New York, as cTrader draws them (IC Markets demo, checked 21-Oct-2026: D1 and H4 open at 21:00 UTC
/// in summer, 22:00 UTC in winter); H4 candles open at 17, 21, 01, 05, 09, 13 New York time.
/// </summary>
public static class Timeframes
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan TradingDayShift = TimeSpan.FromHours(7); // 17:00 New York → 00:00 of the trading day

    public static Timeframe Parse(string s) => Enum.Parse<Timeframe>(s, ignoreCase: true);

    /// <summary>Start of the candle of <paramref name="tf"/> that contains <paramref name="utc"/>.</summary>
    public static DateTime CandleStart(DateTime utc, Timeframe tf)
    {
        switch (tf)
        {
            case Timeframe.M5: return Floor(utc, 5);
            case Timeframe.M15: return Floor(utc, 15);
            case Timeframe.H1: return Floor(utc, 60);
        }
        var shifted = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NewYork) + TradingDayShift;
        var start = tf == Timeframe.D1 ? shifted.Date : shifted.Date.AddHours(shifted.Hour / 4 * 4);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start - TradingDayShift, DateTimeKind.Unspecified), NewYork);
    }

    private static DateTime Floor(DateTime utc, int minutes)
    {
        var step = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTime(utc.Ticks / step * step, DateTimeKind.Utc);
    }

    /// <summary>
    /// Up to <paramref name="count"/> candles that start before <paramref name="beforeUtc"/>, oldest first, built only from
    /// 5-minute candles finished by <paramref name="cursorUtc"/>: nothing after the replay cursor is ever returned, and the
    /// last candle is drawn as far as it has formed.
    /// </summary>
    public static List<ChartCandle> Candles(SymbolHistory h, Timeframe tf, DateTime cursorUtc, DateTime beforeUtc, int count)
    {
        var bars = h.Bars;
        var i = h.RevealedCount(cursorUtc) - 1;
        while (i >= 0 && CandleStart(bars[i].TimeUtc, tf) >= beforeUtc)
            i--;

        var result = new List<ChartCandle>(count);
        while (i >= 0 && result.Count < count)
        {
            var start = CandleStart(bars[i].TimeUtc, tf);
            var last = i;
            double high = double.MinValue, low = double.MaxValue;
            for (; i >= 0 && bars[i].TimeUtc >= start; i--)
            {
                high = Math.Max(high, bars[i].BidHigh);
                low = Math.Min(low, bars[i].BidLow);
            }
            result.Add(new ChartCandle(start, bars[i + 1].BidOpen, high, low, bars[last].BidClose));
        }
        result.Reverse();
        return result;
    }
}
