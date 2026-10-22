using System.Globalization;
using Rewind.Broker;
using Rewind.History;
using Rewind.Replay;

namespace Rewind.Tests;

/// <summary>spec §8: tm-trades.csv — TM's legs measured from the broker's fills with TM's formulas. TM is played by hand here.</summary>
public sealed class TmResultsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rewind-tm-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);
    private const long Tenth = 1_000_000;

    private static readonly Dictionary<string, SymbolSpec> Specs = new()
    {
        ["EURUSD"] = new("EURUSD", "EUR", "USD", 5, 4, 10_000_000, 100_000, 100_000, 10_000_000_000, 30),
    };

    /// <summary>Bid candle; ask = bid + 0.00002.</summary>
    private static Bar B(int i, double o, double h, double l, double c) =>
        new(T0.AddMinutes(5 * i), o, h, l, c, o + 0.00002, h + 0.00002, l + 0.00002, c + 0.00002);

    private (SimBroker Broker, ReplayClock Clock) Make(params Bar[] candles)
    {
        var flat = Enumerable.Range(-20, 20).Select(i => B(i, 1.1, 1.1, 1.1, 1.1));
        var history = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), flat.Concat(candles).ToArray()) };
        var broker = new SimBroker(history, Specs, new RewindConfig());
        var clock = new ReplayClock(history, path: null, broker, lookback: TimeSpan.Zero);
        clock.GoTo(T0);
        broker.Open(_dir, clock.State.CursorUtc);
        return (broker, clock);
    }

    private static string L(long main, string role) => $"TM-abcdef-P{main}-{role}";

    /// <summary>What TM does at take-over (tm.md §3): mirror at market, two hedges at H, stop-losses removed.</summary>
    private static (Position Mirror, PendingOrder HMain, PendingOrder HMir) TakeOver(SimBroker b, Position main, double h)
    {
        var mirror = b.MarketOrder("EURUSD", main.Dir, main.Volume, h, null, L(main.Id, "MIR"));
        var hMain = b.StopOrder("EURUSD", -main.Dir, main.Volume, h, L(main.Id, "HMAIN"));
        var hMir = b.StopOrder("EURUSD", -main.Dir, main.Volume, h, L(main.Id, "HMIR"));
        b.AmendPosition(main.Id, null, null);
        b.AmendPosition(mirror.Id, null, null);
        return (mirror, hMain, hMir);
    }

    private Dictionary<string, string>[] Rows()
    {
        var lines = File.ReadAllLines(Path.Combine(_dir, "tm-trades.csv"));
        var header = lines[0].Split(',');
        Assert.Equal(TmResults.Header, lines[0]);
        return lines.Skip(1).Select(l => header.Zip(l.Split(',')).ToDictionary(x => x.First, x => x.Second)).ToArray();
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void Hedge_cycle_owner_close_and_TM_close_give_one_row_per_leg()
    {
        var (b, clock) = Make(
            B(0, 1.1000, 1.1012, 1.0998, 1.1008),   // rising: low, then high — both hedges fill at 1.10100
            B(1, 1.1008, 1.1009, 1.1003, 1.1004),   // falling: low 1.1003 — the hedges' ½R stops at 1.10050 are hit
            B(2, 1.1004, 1.1004, 1.1004, 1.1004));
        var main = b.MarketOrder("EURUSD", -1, Tenth, 1.101, null); // the owner sells at 1.10000, stop-loss 1.10100 → 1R = 10 pips
        var (mirror, _, _) = TakeOver(b, main, 1.101);

        clock.Step();
        var hedges = b.Positions().Where(p => p.Label.Contains("-H")).ToList();
        Assert.Equal(2, hedges.Count);
        foreach (var h in hedges)
            b.AmendPosition(h.Id, h.Entry - 0.0005, null);          // TM: hedge stop ½R below the fill
        clock.Step();
        Assert.Equal(2, b.Positions().Count);                        // hedges stopped out
        var reMain = b.StopOrder("EURUSD", 1, Tenth, 1.101, L(main.Id, "HMAIN")); // TM re-hedges at H
        var reMir = b.StopOrder("EURUSD", 1, Tenth, 1.101, L(main.Id, "HMIR"));

        b.ClosePosition(main.Id);                                    // the owner closes the main at the ask 1.10042
        Assert.False(File.Exists(Path.Combine(_dir, "tm-trades.csv")) && Rows().Length > 0); // its hedge order is still there
        b.CancelOrder(reMain.Id);                                    // TM removes it
        b.ClosePosition(mirror.Id, by: CloseReason.Tm);              // TM's 3R rule (played by hand)
        b.CancelOrder(reMir.Id);

        var rows = Rows();
        Assert.Equal(2, rows.Length);
        var m = rows[0];
        Assert.Equal(($"P{main.Id}", "MAIN", "sell", "0.10"), (m["trade"], m["leg"], m["side"], m["lots"]));
        Assert.Equal(("1.10000", "1.10100", "10.0", "1.10042"), (m["entry"], m["hedge_level"], m["r_pips"], m["exit"]));
        Assert.Equal(("1", "-0.50", "no", "owner"), (m["hedge_cycles"], m["hedges_r"], m["break_even"], m["closed_by"]));
        Assert.Equal(-0.92, D(m["result_r"]), 2);                    // −4.2 pips on the leg, −5 pips on the hedge
        Assert.Equal(1.02, D(m["max_loss_r"]), 2);                   // hedged at 1.10100: frozen at 1R plus the spread
        Assert.Equal(-9.2 - D(m["commission_usd"]), D(m["pnl_usd"]), 2);
        Assert.Equal(4 * 0.33, D(m["commission_usd"]), 2);           // main open + close, hedge open + close
        var r = rows[1];
        Assert.Equal(("MIR", "TM", "1"), (r["leg"], r["closed_by"], r["hedge_cycles"]));
    }

    [Fact]
    public void Break_even_then_stop_at_entry()
    {
        var (b, clock) = Make(
            B(0, 1.1000, 1.1000, 1.0985, 1.0989),   // falling: low 1.0985 — the short is 1.5R in profit
            B(1, 1.0989, 1.1005, 1.0989, 1.1003));  // rising: back up through the entry
        var main = b.MarketOrder("EURUSD", -1, Tenth, 1.101, null);
        var (mirror, hMain, hMir) = TakeOver(b, main, 1.101);
        clock.Step();
        b.AmendPosition(main.Id, 1.1, null);                         // TM at +1R: stop-loss at the entry, then the hedge goes
        b.CancelOrder(hMain.Id);
        clock.Step();                                                // the stop at the entry is hit

        var row = Rows().Single();
        Assert.Equal(("MAIN", "yes", "stop-loss", "0", "0.00"), (row["leg"], row["break_even"], row["closed_by"], row["hedge_cycles"], row["result_r"]));
        Assert.Equal(0.02, D(row["max_loss_r"]), 2);                 // only the spread, right after the entry
        b.ClosePosition(mirror.Id);
        b.CancelOrder(hMir.Id);
        Assert.Equal(2, Rows().Length);
    }

    [Fact]
    public void Moving_the_hedges_before_a_fill_changes_1R_partial_closes_average_and_a_restart_keeps_the_leg()
    {
        var (b, clock) = Make(
            B(0, 1.1000, 1.1000, 1.0990, 1.0990),
            B(1, 1.0990, 1.0990, 1.0980, 1.0980));
        var main = b.MarketOrder("EURUSD", -1, 2 * Tenth, 1.101, null);
        var (mirror, hMain, hMir) = TakeOver(b, main, 1.101);
        b.AmendOrder(hMain.Id, 1.1015);                              // the owner drags a hedge; TM moves both
        b.AmendOrder(hMir.Id, 1.1015);

        b.Open(_dir, clock.State.CursorUtc);                         // Rewind restarts: the open legs come back from tm-open.json
        clock.Step();
        b.ClosePosition(main.Id, Tenth);                             // half at the ask 1.09902
        clock.Step();
        b.ClosePosition(main.Id);                                    // the rest at 1.09802
        b.CancelOrder(hMain.Id);

        var row = Rows().Single();
        Assert.Equal(("1.10150", "15.0", "0.20"), (row["hedge_level"], row["r_pips"], row["lots"]));
        Assert.Equal("1.09852", row["exit"]);                        // volume-weighted
        Assert.Equal(0.0148 / 0.015, D(row["result_r"]), 2);
        b.ClosePosition(mirror.Id);
        b.CancelOrder(hMir.Id);
    }

    [Fact]
    public void Positions_TM_never_touched_get_no_row()
    {
        var (b, _) = Make(B(0, 1.1, 1.1, 1.1, 1.1));
        var p = b.MarketOrder("EURUSD", 1, Tenth, null, null);
        b.ClosePosition(p.Id);
        Assert.Single(File.ReadAllLines(Path.Combine(_dir, "tm-trades.csv"))); // header only
    }

    [Fact]
    public void A_pending_order_and_its_mirror_order_make_one_trade_with_two_legs()
    {
        // The owner's sell-stop at 1.0990 with SL 1.1000; TM's mirror order beside it, labelled with the owner's orderId.
        var (b, clock) = Make(B(0, 1.1, 1.1002, 1.0985, 1.0988), B(1, 1.0988, 1.0989, 1.0975, 1.0978));
        var order = b.PlaceOrder(OrderType.Stop, "EURUSD", -1, Tenth, 1.0990, stopLoss: 1.1000);
        b.PlaceOrder(OrderType.Stop, "EURUSD", -1, Tenth, 1.0990, stopLoss: 1.1000, label: $"TM-abcdef-O{order.Id}-MIR");
        clock.Step();                                                    // both fill at 1.0990
        var main = b.Positions().Single(p => p.Label.Length == 0);
        var mirror = b.Positions().Single(p => p.Label.EndsWith("-MIR"));
        b.StopOrder("EURUSD", 1, Tenth, 1.1000, L(main.Id, "HMAIN"));     // TM hedges both at H
        b.StopOrder("EURUSD", 1, Tenth, 1.1000, L(main.Id, "HMIR"));
        clock.Step();
        b.CloseAll();

        var rows = Rows();
        Assert.Equal(["MAIN", "MIR"], rows.Select(r => r["leg"]));
        Assert.All(rows, r => Assert.Equal(($"P{main.Id}", "1.09900", "10.0"), (r["trade"], r["entry"], r["r_pips"])));
        Assert.Empty(b.Positions());
        Assert.NotEqual(main.Id, mirror.Id);
    }
}
