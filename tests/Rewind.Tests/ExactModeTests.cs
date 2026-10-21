using System.Diagnostics;
using Rewind.Broker;
using Rewind.History;
using Rewind.Replay;

namespace Rewind.Tests;

/// <summary>spec §6.2: exact mode waits for TM after the price steps of symbols in a trade, only while TM is connected.</summary>
public sealed class ExactModeTests
{
    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StepWait = TimeSpan.FromMilliseconds(100);

    private static Bar Flat(int i, double p) => new(T0.AddMinutes(5 * i), p, p, p, p, p + 0.00002, p + 0.00002, p + 0.00002, p + 0.00002);

    private static readonly Dictionary<string, SymbolSpec> Specs = new()
    {
        ["EURUSD"] = new("EURUSD", "EUR", "USD", 5, 4, 10_000_000, 100_000, 100_000, 10_000_000_000, 30),
        ["EURGBP"] = new("EURGBP", "EUR", "GBP", 5, 4, 10_000_000, 100_000, 100_000, 500_000_000, 30),
    };

    private sealed class Rig
    {
        public required SimBroker Broker { get; init; }
        public required ReplayClock Clock { get; init; }
        public bool Tm { get; set; } = true;
        public Func<DateTime> LastRequest { get; set; } = () => DateTime.MinValue;
        public List<PriceStep> Paced { get; } = [];
    }

    private static Rig Make()
    {
        var history = new Dictionary<string, SymbolHistory>
        {
            ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), Enumerable.Range(-20, 40).Select(i => Flat(i, 1.1)).ToArray()),
            ["EURGBP"] = new(new SymbolInfo("EURGBP", 5), Enumerable.Range(-20, 40).Select(i => Flat(i, 0.85)).ToArray()),
        };
        var broker = new SimBroker(history, Specs, new RewindConfig());
        var clock = new ReplayClock(history, path: null, broker, lookback: TimeSpan.Zero);
        clock.GoTo(T0);
        broker.Open(null, clock.State.CursorUtc);
        var rig = new Rig { Broker = broker, Clock = clock };
        clock.Pacer = new Pacer(() => rig.Tm, broker.InTrade, () => rig.LastRequest(), StepWait, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(400));
        clock.Paced += s => { lock (rig.Paced) rig.Paced.Add(s); };
        return rig;
    }

    private static TimeSpan Time(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        return sw.Elapsed;
    }

    [Fact]
    public void Waits_after_each_price_step_of_a_symbol_in_a_trade_only()
    {
        var r = Make();
        r.Broker.MarketOrder("EURUSD", 1, 1_000_000, null, null);
        var took = Time(() => r.Clock.Step());
        Assert.InRange(took, 4 * StepWait, 4 * StepWait + TimeSpan.FromMilliseconds(300));
        Assert.Equal([0, 1, 2, 3], r.Paced.Select(p => p.Index));       // the 4 steps of EURUSD's candle
        Assert.All(r.Paced, p => Assert.Equal("EURUSD", p.Symbol));     // EURGBP's steps went straight through
    }

    [Fact]
    public void A_pending_order_counts_as_in_a_trade()
    {
        var r = Make();
        r.Broker.StopOrder("EURUSD", 1, 1_000_000, 1.2);
        r.Clock.Step();
        Assert.Equal(4, r.Paced.Count);
    }

    [Fact]
    public void No_wait_when_flat_without_TM_or_in_fast_mode()
    {
        var r = Make();
        Assert.True(Time(() => r.Clock.Step()) < StepWait);           // flat

        r.Broker.MarketOrder("EURUSD", 1, 1_000_000, null, null);
        r.Tm = false;
        Assert.True(Time(() => r.Clock.Step()) < StepWait);           // no TM connected

        r.Tm = true;
        r.Clock.SetExact(false);
        Assert.True(Time(() => r.Clock.Step()) < StepWait);           // fast mode
        Assert.Empty(r.Paced);

        r.Clock.SetExact(true);
        Assert.True(Time(() => r.Clock.Skip(SkipTarget.London)) < StepWait); // skips are never paced
    }

    [Fact]
    public void Waits_until_TM_is_quiet_but_never_longer_than_the_maximum()
    {
        var r = Make();
        r.Broker.MarketOrder("EURUSD", 1, 1_000_000, null, null);
        var busyUntil = DateTime.UtcNow.AddMilliseconds(250);
        r.LastRequest = () => DateTime.UtcNow < busyUntil ? DateTime.UtcNow : busyUntil; // TM sends orders for 250 ms
        var took = Time(() => r.Clock.Step());
        Assert.True(took >= TimeSpan.FromMilliseconds(250 + 30 + 3 * 100), $"{took}"); // first step waits for the quiet

        r.LastRequest = () => DateTime.UtcNow; // TM never stops talking
        took = Time(() => r.Clock.Step());
        Assert.InRange(took, 4 * TimeSpan.FromMilliseconds(400), 4 * TimeSpan.FromMilliseconds(400) + TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task State_and_orders_do_not_wait_for_a_paced_step_and_pause_ends_the_candle()
    {
        var r = Make();
        r.Broker.MarketOrder("EURUSD", 1, 1_000_000, null, null);
        r.Clock.Play(1);
        var before = r.Clock.State.CursorUtc;
        var step = Task.Run(r.Clock.Step);
        await Task.Delay(150); // inside the candle

        Assert.True(Time(() => _ = r.Clock.State) < TimeSpan.FromMilliseconds(20));
        Assert.True(Time(() => r.Broker.StopOrder("EURUSD", -1, 1_000_000, 1.09)) < TimeSpan.FromMilliseconds(50)); // TM's order lands between steps
        r.Clock.Pause();

        await step.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(before.AddMinutes(5), r.Clock.State.CursorUtc); // the candle was finished
        Assert.False(r.Clock.State.Playing);
    }
}
