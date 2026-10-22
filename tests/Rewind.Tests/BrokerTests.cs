using Rewind.Broker;
using Rewind.History;
using Rewind.Replay;

namespace Rewind.Tests;

/// <summary>spec §6.3, §7, §8: fills, stops, gaps, commission, USD conversion, journal, flat-only jumps.</summary>
public sealed class BrokerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rewind-broker-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc); // a Tuesday
    private static DateTime At(int bar) => T0.AddMinutes(5 * bar);

    private static readonly Dictionary<string, SymbolSpec> Specs = new()
    {
        ["EURUSD"] = new("EURUSD", "EUR", "USD", 5, 4, 10_000_000, 100_000, 100_000, 10_000_000_000, 30),
        ["EURGBP"] = new("EURGBP", "EUR", "GBP", 5, 4, 10_000_000, 100_000, 100_000, 500_000_000, 30),
        ["GBPJPY"] = new("GBPJPY", "GBP", "JPY", 3, 2, 10_000_000, 100_000, 100_000, 500_000_000, 30),
    };

    /// <summary>Bid candle (open, high, low, close); ask = bid + 0.00002 (EURUSD-style 0.2 pip spread).</summary>
    private static Bar B(int i, double o, double h, double l, double c, double spread = 0.00002) =>
        new(At(i), o, h, l, c, o + spread, h + spread, l + spread, c + spread);

    /// <summary>EURUSD with the given candles after 20 flat candles at 1.10000; EURGBP flat 0.85, GBPJPY flat 200.</summary>
    private (SimBroker Broker, ReplayClock Clock) Make(params Bar[] candles)
    {
        var flat = Enumerable.Range(-20, 20).Select(i => B(i, 1.1, 1.1, 1.1, 1.1));
        Bar Flat(int i, double p) => new(At(i), p, p, p, p, p + 0.0002, p + 0.0002, p + 0.0002, p + 0.0002);
        var n = (int)((candles[^1].TimeUtc - T0).TotalMinutes / 5) + 1;
        var history = new Dictionary<string, SymbolHistory>
        {
            ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), flat.Concat(candles).ToArray()),
            ["EURGBP"] = new(new SymbolInfo("EURGBP", 5), Enumerable.Range(-20, 20 + n).Select(i => Flat(i, 0.85)).ToArray()),
            ["GBPJPY"] = new(new SymbolInfo("GBPJPY", 3), Enumerable.Range(-20, 20 + n).Select(i => Flat(i, 200.0)).ToArray()),
        };
        var broker = new SimBroker(history, Specs, new RewindConfig(StartBalance: 5000));
        var clock = new ReplayClock(history, path: null, broker, lookback: TimeSpan.Zero);
        clock.GoTo(At(0));
        broker.Open(_dir, clock.State.CursorUtc);
        return (broker, clock);
    }

    private const long Tenth = 1_000_000; // 0.10 lot

    [Fact]
    public void Market_buy_fills_at_the_ask_and_pays_commission()
    {
        var (b, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1));
        var p = b.MarketOrder("EURUSD", 1, Tenth, null, null);
        Assert.Equal(1.10002, p.Entry, 5);
        Assert.Equal(5000 - 0.33, b.Balance(), 2); // 10,000 EUR × 1.10002 = 11,000 USD × 30 / 1,000,000
    }

    [Fact]
    public void Stop_loss_inside_a_candle_closes_at_its_price()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1003, 1.0980, 1.0990)); // falling: open → high → low → close
        var p = b.MarketOrder("EURUSD", 1, Tenth, stopLoss: 1.0990, takeProfit: null);
        clock.Step();
        Assert.True(b.IsFlat);
        var trade = File.ReadAllLines(Path.Combine(_dir, "trades.csv"))[1].Split(',');
        Assert.Equal(["1.10002", "1.09900", "1.09900"], trade[5..8]);   // entry, exit at the SL, initial SL
        Assert.Equal("-1.00", trade[9]);                               // exactly −1R
        Assert.Equal("StopLoss", trade[12]);
        Assert.Equal(-(0.00102 * 10_000) - 0.33 - 0.33, double.Parse(trade[11], System.Globalization.CultureInfo.InvariantCulture), 2);
    }

    [Fact]
    public void A_gap_past_the_stop_loss_closes_at_the_open()
    {
        var (b, clock) = Make(B(0, 1.0970, 1.0975, 1.0960, 1.0972));
        b.MarketOrder("EURUSD", 1, Tenth, 1.0990, null);
        clock.Step();
        Assert.Equal("1.09700", File.ReadAllLines(Path.Combine(_dir, "trades.csv"))[1].Split(',')[6]); // worse than the SL
    }

    [Fact]
    public void Take_profit_and_the_order_inside_the_candle()
    {
        // Rising candle: open → low → high → close. Low 1.0995 is visited before high 1.1012: the SL at 1.0996 wins.
        var (b, clock) = Make(B(0, 1.1, 1.1012, 1.0995, 1.1010));
        b.MarketOrder("EURUSD", 1, Tenth, stopLoss: 1.0996, takeProfit: 1.1010);
        clock.Step();
        Assert.Equal("StopLoss", File.ReadAllLines(Path.Combine(_dir, "trades.csv"))[1].Split(',')[12]);

        var (b2, clock2) = Make(B(0, 1.1, 1.1012, 1.0998, 1.1010));
        b2.MarketOrder("EURUSD", -1, Tenth, stopLoss: null, takeProfit: 1.0999); // short: TP on the ask (1.0998 + 0.00002)
        clock2.Step();
        Assert.True(b2.IsFlat);
    }

    [Fact]
    public void Buy_stop_fills_at_its_price_or_at_the_open_after_a_gap()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1020, 1.0999, 1.1015), B(1, 1.1040, 1.1045, 1.1035, 1.1040));
        var inside = b.StopOrder("EURUSD", 1, Tenth, 1.1010);   // reached within candle 0
        var gapped = b.StopOrder("EURUSD", 1, Tenth, 1.1030);   // jumped over between candle 0 and 1
        clock.Step();
        Assert.Contains(b.Positions(), p => p.Entry == 1.1010);
        clock.Step();
        Assert.Contains(b.Positions(), p => Math.Abs(p.Entry - 1.10402) < 1e-9); // the ask at candle 1's open
        Assert.Empty(b.Orders());
        Assert.NotEqual(inside.Id, gapped.Id);
    }

    [Fact]
    public void Refusals_like_cTrader()
    {
        var (b, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1));
        Assert.Equal("TRADING_BAD_STOPS", Assert.Throws<BrokerException>(() => b.MarketOrder("EURUSD", 1, Tenth, stopLoss: 1.1005, null)).Code);
        Assert.Equal("TRADING_BAD_STOPS", Assert.Throws<BrokerException>(() => b.StopOrder("EURUSD", 1, Tenth, 1.0990)).Code);
        Assert.Equal("TRADING_BAD_VOLUME", Assert.Throws<BrokerException>(() => b.MarketOrder("EURUSD", 1, 150_000, null, null)).Code);
    }

    [Fact]
    public void Market_closed_when_there_is_no_candle()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1, 1.1, 1.1), B(10, 1.1, 1.1, 1.1, 1.1)); // no EURUSD candle for 45 minutes
        clock.GoTo(At(6));
        Assert.Equal("MARKET_CLOSED", Assert.Throws<BrokerException>(() => b.MarketOrder("EURUSD", 1, Tenth, null, null)).Code);
    }

    [Fact]
    public void Partial_close_and_JPY_profit_in_USD()
    {
        var (b, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1));
        var p = b.MarketOrder("GBPJPY", 1, 2 * Tenth, null, null); // buys at the ask 200.0002
        b.ClosePosition(p.Id, Tenth);                               // sells half at the bid 200.0
        var row = File.ReadAllLines(Path.Combine(_dir, "trades.csv"))[1].Split(',');
        Assert.Equal("0.10", row[4]);
        // −0.0002 JPY × 10,000 = −2 JPY; GBPUSD = EURUSD mid / EURGBP mid ≈ 1.10001 / 0.8501 → USDJPY ≈ 154.6
        var pnlBeforeCommission = -2.0 / (200.0001 / (1.10001 / 0.8501));
        var commission = double.Parse(row[10], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(pnlBeforeCommission - commission, double.Parse(row[11], System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Equal(Tenth, b.Positions().Single().Volume);
    }

    [Fact]
    public void Go_to_and_session_changes_only_when_flat_but_skips_carry_positions()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1, 1.1, 1.1), B(1, 1.1, 1.1, 1.1, 1.1));
        b.MarketOrder("EURUSD", 1, Tenth, null, null);
        Assert.Throws<ArgumentException>(() => clock.GoTo(At(-5)));
        clock.Step();
        Assert.Single(b.Positions());
        b.CloseAll();
        clock.GoTo(At(-5));
        Assert.Equal(At(-5), clock.State.CursorUtc);
    }

    [Fact]
    public void Account_survives_a_restart()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1, 1.1, 1.1));
        b.MarketOrder("EURUSD", -1, Tenth, 1.1050, null);
        var (b2, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1)); // opens the same folder
        Assert.Equal(1.1050, b2.Positions().Single().StopLoss);
        Assert.Equal(b.Balance(), b2.Balance(), 2);
    }

    // ---- pending orders and trailing stops (spec §7, v0.7) --------------------------------------------------------

    [Fact]
    public void Limit_orders_fill_at_their_price_or_better_after_a_gap()
    {
        // Candle 0 rises (open → low 1.0985 → high → close); candle 1 opens with a gap up to 1.1040.
        var (b, clock) = Make(B(0, 1.1, 1.1010, 1.0985, 1.1005), B(1, 1.1040, 1.1045, 1.1035, 1.1040));
        var buyLimit = b.PlaceOrder(OrderType.Limit, "EURUSD", 1, Tenth, 1.0990);   // pullback: the ask comes down to it
        var sellLimit = b.PlaceOrder(OrderType.Limit, "EURUSD", -1, Tenth, 1.1030); // rally: the bid jumps over it
        clock.Step();
        Assert.Equal(1.0990, b.Positions().Single(p => p.Dir > 0).Entry, 5);
        Assert.Equal(sellLimit.Id, b.Orders().Single().Id);
        clock.Step();
        Assert.Equal(1.1040, b.Positions().Single(p => p.Dir < 0).Entry, 5);       // the open: better than 1.1030
        Assert.Empty(b.Orders());
        Assert.NotEqual(buyLimit.Id, sellLimit.Id);
    }

    [Fact]
    public void Sell_stop_fills_when_the_bid_falls_to_it()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1002, 1.0980, 1.0985));
        b.PlaceOrder(OrderType.Stop, "EURUSD", -1, Tenth, 1.0990); // breakdown
        clock.Step();
        var p = b.Positions().Single();
        Assert.Equal((-1, 1.0990), (p.Dir, Math.Round(p.Entry, 5)));
    }

    [Fact]
    public void Each_pending_order_must_sit_on_its_own_side_of_the_price()
    {
        var (b, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1)); // bid 1.10000, ask 1.10002
        BrokerException Refused(OrderType t, int dir, double price) => Assert.Throws<BrokerException>(() => b.PlaceOrder(t, "EURUSD", dir, Tenth, price));
        Assert.Equal("TRADING_BAD_PRICES", Refused(OrderType.Limit, 1, 1.1010).Code);  // buy-limit above the ask
        Assert.Equal("TRADING_BAD_PRICES", Refused(OrderType.Limit, -1, 1.0990).Code); // sell-limit below the bid
        Assert.Equal("TRADING_BAD_STOPS", Refused(OrderType.Stop, 1, 1.0990).Code);    // buy-stop below the ask
        Assert.Equal("TRADING_BAD_STOPS", Refused(OrderType.Stop, -1, 1.1010).Code);   // sell-stop above the bid
        var o = b.PlaceOrder(OrderType.Limit, "EURUSD", 1, Tenth, 1.0990);
        Assert.Equal("TRADING_BAD_PRICES", Assert.Throws<BrokerException>(() => b.AmendOrder(o.Id, 1.1010)).Code);
        Assert.Equal(1.0990, b.Orders().Single().Price);
    }

    [Fact]
    public void A_pending_orders_stop_loss_and_take_profit_go_to_its_position()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1003, 1.0985, 1.0995)); // falling: the buy-limit fills on the way down
        Assert.Equal("TRADING_BAD_STOPS", Assert.Throws<BrokerException>(() =>
            b.PlaceOrder(OrderType.Limit, "EURUSD", 1, Tenth, 1.0990, stopLoss: 1.0995)).Code);  // SL above a buy's entry
        Assert.Equal("TRADING_BAD_STOPS", Assert.Throws<BrokerException>(() =>
            b.PlaceOrder(OrderType.Limit, "EURUSD", 1, Tenth, 1.0990, trailing: true)).Code);    // trailing without SL
        var o = b.PlaceOrder(OrderType.Limit, "EURUSD", 1, Tenth, 1.0990, stopLoss: 1.0970, takeProfit: 1.1030);
        b.AmendOrder(o.Id, 1.0988);                                                             // moved: protection kept
        Assert.Equal((1.0970, 1.1030), (b.Orders().Single().StopLoss, b.Orders().Single().TakeProfit));
        clock.Step();
        var p = b.Positions().Single();
        Assert.Equal((1.0988, 1.0970, 1.1030, 1.0970), (p.Entry, p.StopLoss, p.TakeProfit, p.InitialStopLoss));
    }

    [Fact]
    public void Trailing_stop_follows_the_price_in_the_trades_favour_only()
    {
        // Long at the ask 1.10002, SL 1.0990 trailing: 10 pips behind the bid (1.10000 at the opening).
        // Candle 0 rises to 1.1030 (SL → 1.1020) and closes at 1.1025; candle 1 (high 1.1026 would only give 1.1016)
        // dips to 1.1010: stopped at 1.1020.
        var (b, clock) = Make(B(0, 1.1, 1.1030, 1.0995, 1.1025), B(1, 1.1025, 1.1026, 1.1010, 1.1015));
        b.MarketOrder("EURUSD", 1, Tenth, 1.0990, null, trailing: true);
        clock.Step();
        var p = b.Positions().Single();
        Assert.True(p.Trailing);
        Assert.Equal(1.1020, p.StopLoss!.Value, 5);           // moved up at the high, not back down at the close
        clock.Step();
        Assert.True(b.IsFlat);
        var trade = File.ReadAllLines(Path.Combine(_dir, "trades.csv"))[1].Split(',');
        Assert.Equal(("1.10200", "StopLoss"), (trade[6], trade[12]));
    }

    [Fact]
    public void Trailing_switched_on_later_and_off_again_on_a_short()
    {
        var (b, clock) = Make(B(0, 1.1, 1.1003, 1.0970, 1.0975), B(1, 1.0975, 1.0976, 1.0960, 1.0965));
        var p = b.MarketOrder("EURUSD", -1, Tenth, 1.1010, null);
        b.AmendPosition(p.Id, 1.1010, null, trailing: true);  // 9.8 pips above the ask 1.10002
        clock.Step();                                         // low 1.0970 (ask 1.09702) → SL 1.09800
        Assert.Equal(1.0980, b.Positions().Single().StopLoss!.Value, 5);
        b.AmendPosition(p.Id, 1.0980, null, trailing: false);
        clock.Step();                                         // lower again, but no longer trailing
        Assert.Equal((false, 1.0980), (b.Positions().Single().Trailing, b.Positions().Single().StopLoss!.Value));
    }
}
