using Rewind.Broker;
using Rewind.History;
using Rewind.Lab;

namespace Rewind.Tests;

/// <summary>spec §7.1: rewind-lab — Scout's orders played out in R, and the candle export for Trader's --replay.</summary>
public sealed class LabTests
{
    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc); // a Tuesday
    private static DateTime At(int bar) => T0.AddMinutes(5 * bar);
    private const double Spread = 0.02;

    private static readonly SymbolSpec Spec = new("TESTUSD", "TEST", "USD", 2, 2, 100, 1, 1, 100_000, 0);

    /// <summary>Bid candle; ask = bid + 0.02.</summary>
    private static Bar B(int i, double o, double h, double l, double c) => new(At(i), o, h, l, c, o + Spread, h + Spread, l + Spread, c + Spread);

    /// <summary>Flat at 100 for 4 candles, then <paramref name="bars"/> one after the other, then flat at the last close.</summary>
    private static SymbolHistory History(params (double O, double H, double L, double C)[] bars)
    {
        var list = Enumerable.Range(0, 4).Select(i => B(i, 100, 100, 100, 100)).ToList();
        list.AddRange(bars.Select((b, k) => B(4 + k, b.O, b.H, b.L, b.C)));
        var last = list[^1].BidClose;
        list.AddRange(Enumerable.Range(list.Count, 4).Select(i => B(i, last, last, last, last)));
        return new SymbolHistory(new SymbolInfo("TESTUSD", 2), [.. list]);
    }

    private static OrderRow Place(string key, int dir, double price, double? sl, DateTime? expires = null, int atBar = 1) =>
        new(At(atBar), key, "TESTUSD", "place", dir, price, sl, expires ?? At(atBar).AddHours(16), "", []);

    private static OrderRow CancelRow(string key, int atBar, string reason) => new(At(atBar), key, "TESTUSD", "cancel", 0, 0, null, null, reason, []);

    private static List<Outcome> Run(SymbolHistory h, params OrderRow[] rows) => new Outcomes(h, Spec, new RewindConfig()).Run(rows);

    private const string M = "TESTUSD|H1|202607140900|M";
    private const string R = "TESTUSD|H1|202607140900|R";

    [Fact]
    public void Buy_stop_filled_then_stopped_out_is_minus_one_R()
    {
        // fills at 101 when the ask reaches it (bid 100.98), then the bid falls through the stop-loss at 99
        var h = History((100, 100.98, 100, 100.98), (100.98, 100.98, 98.5, 98.6));
        var o = Assert.Single(Run(h, Place(M, 1, 101, 99)));
        Assert.Equal(("filled", 101.0, 99.0, 99.0), (o.Result, o.Entry!.Value, o.InitialStopLoss!.Value, o.Exit!.Value));
        Assert.Equal(-1.0, o.R!.Value, 6);
        Assert.Equal(-1.0, o.WorstR!.Value, 6);
        Assert.Equal("StopLoss", o.ExitReason);
    }

    [Fact]
    public void Trailing_stop_follows_the_rally_and_locks_in_profit()
    {
        // fill at 101 (risk 2; trailing distance from the bid at the fill: 100.98 − 99 = 1.98), rally to 106, fall back
        var h = History((100, 100.98, 100, 100.98), (100.98, 106, 100.98, 106), (106, 106, 100, 100.5));
        var o = Assert.Single(Run(h, Place(M, 1, 101, 99)));
        Assert.Equal(106 - 1.98, o.Exit!.Value, 6);
        Assert.Equal((106 - 1.98 - 101) / 2, o.R!.Value, 6);
        Assert.Equal(2.5, o.BestR!.Value, 6);
        Assert.Equal("trailing stop", o.ExitReason);
    }

    [Fact]
    public void One_fill_cancels_the_other_order_of_the_pause()
    {
        var h = History((100, 100.98, 100, 100.98), (100.98, 100.98, 98.5, 98.6));
        var res = Run(h, Place(M, 1, 101, 99), Place(R, -1, 99.5, 101));
        Assert.Equal("filled", res[0].Result);
        Assert.Equal(("cancelled", "the other order of the pair filled"), (res[1].Result, res[1].Detail));
    }

    [Fact]
    public void Expired_order_does_not_fill_on_a_later_gap()
    {
        var h = History((100, 100, 100, 100), (100, 100, 100, 100), (102, 102.5, 102, 102.4));
        var o = Assert.Single(Run(h, Place(M, 1, 101, 99, expires: At(5))));
        Assert.Equal("expired", o.Result);
        Assert.Null(o.R);
    }

    [Fact]
    public void Order_after_a_gap_fills_at_the_open_with_slippage()
    {
        var h = History((102, 102.5, 102, 102.4), (102.4, 102.4, 98, 98));
        var o = Assert.Single(Run(h, Place(M, 1, 101, 99)));
        Assert.Equal(102.02, o.Entry!.Value, 6); // the ask at the open: worse than the level
    }

    [Fact]
    public void Scout_cancels_and_broker_refusals_are_kept_with_their_reason()
    {
        var h = History((100, 100.5, 99.5, 100), (100, 101.5, 100, 101.4));
        var res = Run(h, Place(M, 1, 101, 99), Place(R, -1, 100.5, 102), CancelRow(M, 3, "pause too long or too deep"));
        Assert.Equal(("cancelled", "pause too long or too deep"), (res[0].Result, res[0].Detail));
        Assert.Equal("refused", res[1].Result);                    // a sell stop above the bid
        Assert.StartsWith("TRADING_BAD_STOPS", res[1].Detail);
    }

    [Fact]
    public void Order_without_a_stop_loss_is_not_played()
    {
        var o = Assert.Single(Run(History((100, 101.5, 100, 101.4)), Place(M, 1, 101, null)));
        Assert.Equal("no_stop_loss", o.Result);
    }

    [Fact]
    public void Orders_file_from_Trader_is_read_and_written_back_with_the_outcome()
    {
        var dir = Directory.CreateTempSubdirectory("rewind-lab-").FullName;
        try
        {
            var orders = Path.Combine(dir, "orders.csv");
            const string header = "time_utc,setup_id,symbol,timeframe,role,action,side,price,stop_loss,expires_utc,setup_utc,pause_candles,pause_height_atr,move_atr,pullback,atr,higher_tf,higher_trend,with_higher,reason";
            File.WriteAllLines(orders,
            [
                header,
                $"2026-07-14 10:05:00,{M},TESTUSD,H1,M,place,Buy,101.000000,99.000000,2026-07-15 02:05:00,2026-07-14 09:00:00,2,0.80,2.10,0.30,1.500000,H4,up,with,",
                $"2026-07-14 10:45:00,{M},TESTUSD,H1,M,cancel,,,,,2026-07-14 09:00:00,,,,,,,,,\"trend turned, again\"",
            ]);
            var (h, rows) = Outcomes.Read(orders);
            Assert.Equal(header, h);
            Assert.Equal((1, 101.0, 99.0, "place"), (rows[0].Dir, rows[0].Price, rows[0].StopLoss!.Value, rows[0].Action));
            Assert.Equal("trend turned, again", rows[1].Reason);

            var res = new Outcomes(History((100, 100.98, 100, 100.98), (100.98, 100.98, 98.5, 98.6)), Spec, new RewindConfig()).Run(rows);
            var output = Path.Combine(dir, "outcomes.csv");
            Outcomes.Write(output, h, res);
            var lines = File.ReadAllLines(output);
            Assert.Equal($"{header},{Outcomes.OutcomeColumns}", lines[0]);
            Assert.Contains(",filled,,2026-07-14 10:", lines[1]);
            Assert.Contains(",StopLoss,-1.000,", lines[1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Candle_export_builds_hours_from_five_minute_bars_and_leaves_out_the_last_one()
    {
        var bars = Enumerable.Range(0, 30).Select(i => B(i, 100 + i, 100.5 + i, 99.5 + i, 100.2 + i)).ToArray(); // 10:00 → 12:25
        var h1 = CandleExport.Closed(new SymbolHistory(new SymbolInfo("TESTUSD", 2), bars), Timeframe.H1);
        Assert.Equal(2, h1.Count); // 10:00 and 11:00; 12:00 may still be forming
        Assert.Equal(new ChartCandle(T0, 100, 111.5, 99.5, 111.2), h1[0]);
        Assert.Equal(T0.AddHours(1), h1[1].TimeUtc);
    }
}
