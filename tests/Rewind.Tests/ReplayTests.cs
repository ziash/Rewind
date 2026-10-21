using Rewind.History;
using Rewind.Replay;

namespace Rewind.Tests;

/// <summary>spec §5, §6: timeframes, no peeking ahead, the cursor, skips.</summary>
public sealed class ReplayTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>EURUSD-like 5-minute candles from <paramref name="from"/> to <paramref name="to"/>, skipping the forex weekend
    /// (Friday 21:00 → Sunday 21:00 UTC). Each candle opens 0.0001 above the previous one.</summary>
    private static SymbolHistory Forex(DateTime from, DateTime to)
    {
        var bars = new List<Bar>();
        var price = 1.1;
        for (var t = from; t < to; t += Bar.Length)
        {
            var weekend = (t.DayOfWeek == DayOfWeek.Friday && t.Hour >= 21) || t.DayOfWeek == DayOfWeek.Saturday
                          || (t.DayOfWeek == DayOfWeek.Sunday && t.Hour < 21);
            if (weekend)
                continue;
            bars.Add(new Bar(t, price, price + 0.0003, price - 0.0002, price + 0.0001, price + 0.00002, price + 0.00032, price - 0.00018, price + 0.00012));
            price += 0.0001;
        }
        return new SymbolHistory(new SymbolInfo("EURUSD", 5), bars.ToArray());
    }

    [Theory]
    [InlineData(2026, 7, 14, 10, 0, 2026, 7, 13, 21)]   // summer: the trading day starts 21:00 UTC (17:00 New York)
    [InlineData(2026, 7, 13, 21, 0, 2026, 7, 13, 21)]
    [InlineData(2026, 7, 13, 20, 55, 2026, 7, 12, 21)]
    [InlineData(2026, 12, 15, 10, 0, 2026, 12, 14, 22)] // winter: 22:00 UTC
    public void D1_starts_at_17_00_New_York(int y, int mo, int d, int h, int mi, int sy, int smo, int sd, int sh) =>
        Assert.Equal(Utc(sy, smo, sd, sh, 0), Timeframes.CandleStart(Utc(y, mo, d, h, mi), Timeframe.D1));

    [Fact]
    public void H4_follows_cTrader_17_21_01_New_York()
    {
        // cTrader (IC Markets) H4 candles of Monday 13-Jul-2026 open at 21:00, 01:00, 05:00 … UTC
        Assert.Equal(Utc(2026, 7, 13, 1, 0), Timeframes.CandleStart(Utc(2026, 7, 13, 4, 55), Timeframe.H4));
        Assert.Equal(Utc(2026, 7, 13, 5, 0), Timeframes.CandleStart(Utc(2026, 7, 13, 5, 0), Timeframe.H4));
        Assert.Equal(Utc(2026, 7, 12, 21, 0), Timeframes.CandleStart(Utc(2026, 7, 13, 0, 30), Timeframe.H4));
        Assert.Equal(Utc(2026, 7, 13, 9, 0), Timeframes.CandleStart(Utc(2026, 7, 13, 9, 45), Timeframe.H1));
    }

    [Fact]
    public void Nothing_after_the_cursor_and_the_last_candle_is_drawn_as_far_as_it_has_formed()
    {
        var h = Forex(Utc(2026, 7, 13, 0, 0), Utc(2026, 7, 15, 0, 0));
        var cursor = Utc(2026, 7, 14, 10, 10); // candles 10:00 and 10:05 have finished

        var m5 = Timeframes.Candles(h, Timeframe.M5, cursor, DateTime.MaxValue, 3);
        Assert.Equal([Utc(2026, 7, 14, 9, 55), Utc(2026, 7, 14, 10, 0), Utc(2026, 7, 14, 10, 5)], m5.Select(c => c.TimeUtc));

        var h1 = Timeframes.Candles(h, Timeframe.H1, cursor, DateTime.MaxValue, 1).Single();
        var at10 = h.Bars.Single(b => b.TimeUtc == Utc(2026, 7, 14, 10, 0));
        var at1005 = h.Bars.Single(b => b.TimeUtc == Utc(2026, 7, 14, 10, 5));
        Assert.Equal(new ChartCandle(Utc(2026, 7, 14, 10, 0), at10.BidOpen, at1005.BidHigh, at10.BidLow, at1005.BidClose), h1);

        var d1 = Timeframes.Candles(h, Timeframe.D1, cursor, DateTime.MaxValue, 5);
        Assert.Equal(Utc(2026, 7, 13, 21, 0), d1[^1].TimeUtc);
        Assert.Equal(at1005.BidClose, d1[^1].Close);
    }

    [Fact]
    public void Older_pages_end_before_the_first_loaded_candle()
    {
        var h = Forex(Utc(2026, 7, 13, 0, 0), Utc(2026, 7, 15, 0, 0));
        var older = Timeframes.Candles(h, Timeframe.H1, Utc(2026, 7, 14, 10, 10), Utc(2026, 7, 14, 6, 0), 2);
        Assert.Equal([Utc(2026, 7, 14, 4, 0), Utc(2026, 7, 14, 5, 0)], older.Select(c => c.TimeUtc));
    }

    [Fact]
    public void Step_reveals_one_candle_and_jumps_the_weekend_in_one_step()
    {
        var h = Forex(Utc(2026, 7, 6, 0, 0), Utc(2026, 7, 21, 0, 0));
        var clock = new ReplayClock(new Dictionary<string, SymbolHistory> { ["EURUSD"] = h }, path: null);
        clock.GoTo(Utc(2026, 7, 17, 20, 50));
        Assert.True(clock.Step());
        Assert.Equal(Utc(2026, 7, 17, 20, 55), clock.State.CursorUtc);
        clock.Step(); // Friday's last candle 20:55
        Assert.Equal(Utc(2026, 7, 17, 21, 0), clock.State.CursorUtc);
        clock.Step(); // next candle: Sunday 21:00
        Assert.Equal(Utc(2026, 7, 19, 21, 5), clock.State.CursorUtc);
    }

    [Fact]
    public void GoTo_is_kept_inside_the_history_and_on_5_minute_marks()
    {
        var h = Forex(Utc(2026, 7, 6, 0, 0), Utc(2026, 7, 21, 0, 0));
        var clock = new ReplayClock(new Dictionary<string, SymbolHistory> { ["EURUSD"] = h }, path: null);
        clock.GoTo(Utc(2020, 1, 1, 0, 0));
        Assert.Equal(Utc(2026, 7, 13, 0, 0), clock.State.CursorUtc); // a week after the first candle
        clock.GoTo(Utc(2026, 7, 14, 10, 7));
        Assert.Equal(Utc(2026, 7, 14, 10, 5), clock.State.CursorUtc);
        clock.GoTo(Utc(2030, 1, 1, 0, 0));
        Assert.Equal(h.EndUtc, clock.State.CursorUtc);
    }

    [Theory]
    [InlineData(SkipTarget.Day, 2026, 7, 14, 10, 0, 2026, 7, 14, 21, 0)]      // 17:00 New York (summer)
    [InlineData(SkipTarget.London, 2026, 7, 14, 10, 0, 2026, 7, 15, 7, 0)]    // 08:00 London (BST) next day
    [InlineData(SkipTarget.NewYork, 2026, 7, 14, 10, 0, 2026, 7, 14, 12, 0)]  // 08:00 New York (EDT) same day
    [InlineData(SkipTarget.London, 2026, 7, 17, 9, 0, 2026, 7, 20, 7, 0)]     // Friday → Monday
    [InlineData(SkipTarget.NewYork, 2026, 12, 14, 10, 0, 2026, 12, 14, 13, 0)] // winter (EST)
    public void Skip_targets(SkipTarget target, int y, int mo, int d, int h, int mi, int ty, int tmo, int td, int th, int tmi) =>
        Assert.Equal(Utc(ty, tmo, td, th, tmi), ReplayClock.Next(target, Utc(y, mo, d, h, mi)));
}
