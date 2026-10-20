using Rewind.Download;

namespace Rewind.Download.Tests;

/// <summary>spec §4: file names, decoding, combining bid and ask, 5-minute candles, CSV, gaps.</summary>
public sealed class DownloadTests
{
    private static readonly Instrument EurUsd = Instrument.Find("EURUSD");
    private static readonly DateOnly Day = new(2026, 10, 14);

    private static List<MinuteBar> Sample(string side) =>
        Bi5.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", $"EURUSD-2026-10-14.{side}.bi5")), Day);

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Url_counts_months_from_00()
    {
        Assert.Equal("https://datafeed.dukascopy.com/datafeed/EURUSD/2026/09/14/BID_candles_min_1.bi5", Feed.Url(EurUsd, Day, Side.Bid));
        Assert.Equal("https://datafeed.dukascopy.com/datafeed/BTCUSD/2025/00/05/ASK_candles_min_1.bi5",
            Feed.Url(Instrument.Find("BTCUSD"), new DateOnly(2025, 1, 5), Side.Ask));
    }

    [Fact]
    public void Forex_skips_Saturdays_crypto_does_not()
    {
        var saturday = new DateOnly(2026, 10, 17);
        Assert.False(EurUsd.Trades(saturday));
        Assert.True(EurUsd.Trades(saturday.AddDays(1)));
        Assert.True(Instrument.Find("ETHUSD").Trades(saturday));
    }

    [Fact]
    public void Real_file_decodes_to_one_bar_per_minute()
    {
        var bid = Sample("BID");
        Assert.Equal(1440, bid.Count);
        Assert.Equal(new MinuteBar(Utc(2026, 10, 14, 0, 0), 112010, 112010, 111989, 111991, bid[0].Volume), bid[0]);
        Assert.Equal(129.17, bid[0].Volume, 2);
        Assert.Equal(Utc(2026, 10, 14, 23, 59), bid[^1].TimeUtc);
    }

    [Fact]
    public void Empty_file_is_no_data_and_a_broken_one_is_refused()
    {
        Assert.Empty(Bi5.Read([], Day));
        var broken = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", "EURUSD-2026-10-14.BID.bi5"))[..400];
        Assert.Throws<Bi5Exception>(() => Bi5.Read(broken, Day));
    }

    [Fact]
    public void Five_minute_candles_from_bid_and_ask()
    {
        var m1 = Candles.Combine(Sample("BID"), Sample("ASK"));
        var m5 = Candles.Aggregate(m1, 5);
        Assert.Equal(288, m5.Count);
        Assert.Equal(new Candle(Utc(2026, 10, 14, 0, 0), 112010, 112010, 111981, 111993, 112014, 112014, 111983, 111997, m5[0].Volume), m5[0]);
        Assert.Equal(351.17, m5[0].Volume, 2);
        Assert.StartsWith("2026-10-14 00:00:00,1.12010,1.12010,1.11981,1.11993,1.12014,1.12014,1.11983,1.11997,351.1", Candles.Line(m5[0], EurUsd));
    }

    [Fact]
    public void Minutes_without_trading_are_dropped()
    {
        MinuteBar Bar(int minute, double volume) => new(Utc(2026, 10, 17, 0, minute), 100, 100, 100, 100, volume);
        var m1 = Candles.Combine([Bar(0, 5), Bar(1, 0), Bar(2, 0)], [Bar(0, 5), Bar(1, 0), Bar(2, 3)]);
        Assert.Equal([0, 2], m1.Select(c => c.TimeUtc.Minute));
    }

    [Fact]
    public void Weekend_close_is_not_a_gap_for_forex_but_a_weekday_hole_is()
    {
        Candle At(DateTime t) => new(t, 1, 1, 1, 1, 1, 1, 1, 1, 1);
        Candle[] weekend = [At(Utc(2026, 10, 16, 20, 59)), At(Utc(2026, 10, 18, 21, 5))]; // Friday close → Sunday open
        Candle[] hole = [At(Utc(2026, 10, 19, 10, 0)), At(Utc(2026, 10, 19, 12, 0))];      // 2 h without candles on a Monday

        Assert.Empty(Summary.Gaps(weekend, forex: true));
        Assert.Single(Summary.Gaps(weekend, forex: false));   // crypto trades at the weekend: a real gap
        Assert.Equal([Utc(2026, 10, 19, 10, 1)], Summary.Gaps(hole, forex: true).Select(g => g.Start));
    }

    [Fact]
    public void Prices_are_written_with_the_symbol_s_decimals()
    {
        var c = new Candle(Utc(2025, 1, 2, 3, 4), 830739, 831340, 830739, 831096, 830800, 831400, 830800, 831150, 0.000142);
        Assert.Equal("2025-01-02 03:04:00,83073.9,83134.0,83073.9,83109.6,83080.0,83140.0,83080.0,83115.0,0.000142",
            Candles.Line(c, Instrument.Find("BTCUSD")));
        Assert.Equal(3, Instrument.Find("GBPJPY").Digits);
    }
}
