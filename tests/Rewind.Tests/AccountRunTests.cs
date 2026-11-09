using Rewind.Broker;
using Rewind.History;

namespace Rewind.Tests;

/// <summary>spec §7.2: the whole-account run's broker rules. A lot is 100,000 units, 1:100 leverage: one lot at 1.10 holds $1,100.</summary>
public sealed class AccountRunTests
{
    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc); // a Tuesday
    private const long Lot = 10_000_000;

    private static SymbolSpec Spec(string name, double swapLong = 0, double swapShort = 0, bool percent = false) =>
        new(name, name[..3], "USD", 5, 4, 10_000_000, 100_000, 100_000, 10_000_000_000, 0, 100, swapLong, swapShort, percent, 3);

    private static Bar Flat(DateTime t, double p) => new(t, p, p, p, p, p, p, p, p);

    private static Bar Move(DateTime t, double o, double h, double l, double c) => new(t, o, h, l, c, o, h, l, c);

    private static SimBroker Make(string mode, double balance, IEnumerable<Bar> eur, IEnumerable<Bar>? gbp = null, SymbolSpec? eurSpec = null, double stopOut = 50)
    {
        var history = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), eur.ToArray()) };
        var specs = new Dictionary<string, SymbolSpec> { ["EURUSD"] = eurSpec ?? Spec("EURUSD") };
        if (gbp is not null)
        {
            history["GBPUSD"] = new(new SymbolInfo("GBPUSD", 5), gbp.ToArray());
            specs["GBPUSD"] = Spec("GBPUSD");
        }
        var broker = new SimBroker(history, specs, new RewindConfig(StartBalance: balance)) { Rules = new AccountRules(mode, stopOut) };
        broker.Open(null, T0);
        return broker;
    }

    private static IEnumerable<Bar> Bars(int n, double price, int from = 0) => Enumerable.Range(from, n).Select(i => Flat(T0.AddMinutes(5 * i), price));

    private static void Walk(SimBroker b, int candles, Action<DateTime, int>? after = null) => b.Walk(T0, T0.AddMinutes(5 * candles), after ?? ((_, _) => { }));

    /// <summary>History before T0 so the broker has prices at the start.</summary>
    private static IEnumerable<Bar> Before(double p) => Enumerable.Range(-3, 3).Select(i => Flat(T0.AddMinutes(5 * i), p));

    [Theory]
    [InlineData("Sum", 2200)]
    [InlineData("Max", 1100)]
    [InlineData("Net", 0)]
    public void A_symbols_two_sides_share_margin_by_the_brokers_rule(string mode, double margin)
    {
        var b = Make(mode, 10_000, Before(1.1).Concat(Bars(2, 1.1)));
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        b.MarketOrder("EURUSD", -1, Lot, null, null);
        Assert.Equal(margin, b.Money().Margin, 2);
    }

    [Fact]
    public void An_order_without_the_margin_is_refused()
    {
        var b = Make("Sum", 1000, Before(1.1).Concat(Bars(2, 1.1)));
        Assert.Equal("NOT_ENOUGH_MONEY", Assert.Throws<BrokerException>(() => b.MarketOrder("EURUSD", 1, Lot, null, null)).Code);
    }

    [Fact]
    public void Under_MAX_the_smaller_side_needs_no_margin()
    {
        var b = Make("Max", 1200, Before(1.1).Concat(Bars(2, 1.1)));
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        b.MarketOrder("EURUSD", -1, Lot, null, null);
        Assert.Equal(2, b.Money().Positions);
    }

    [Fact]
    public void A_hedge_order_without_the_margin_is_refused_at_its_price()
    {
        var bars = Before(1.1).Append(Move(T0, 1.1, 1.1, 1.09, 1.095));
        var b = Make("Sum", 1200, bars, stopOut: 0);
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        var kinds = new List<EventKind>();
        b.Event += e => kinds.Add(e.Kind);
        b.PlaceOrder(OrderType.Stop, "EURUSD", -1, Lot, 1.095);
        Walk(b, 1);
        Assert.Contains(EventKind.OrderRejected, kinds);
        Assert.Empty(b.Orders());
        Assert.Single(b.Positions());
    }

    [Fact]
    public void Below_the_stop_out_level_the_worst_position_closes_first()
    {
        var eur = Before(1.1).Append(Move(T0, 1.1, 1.1, 1.0895, 1.0895));
        var gbp = Before(1.3).Append(Flat(T0, 1.3));
        var b = Make("Sum", 1300, eur, gbp); // margin $1,100 + $130 = $1,230; equity falls to $250, level 20 %
        var loser = b.MarketOrder("EURUSD", 1, Lot, null, null);
        b.MarketOrder("GBPUSD", 1, Lot / 10, null, null);
        Walk(b, 1);
        Assert.DoesNotContain(b.Positions(), p => p.Id == loser.Id);
        Assert.Single(b.Positions());
    }

    [Fact]
    public void Above_the_stop_out_level_nothing_closes()
    {
        var b = Make("Sum", 5000, Before(1.1).Append(Move(T0, 1.1, 1.1, 1.0895, 1.0895)));
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        Walk(b, 1);
        Assert.Single(b.Positions());
    }

    [Fact]
    public void Swap_is_charged_at_17_New_York_and_three_times_on_the_triple_day()
    {
        // Tuesday 20:55 → 21:10 UTC crosses 17:00 New York (summer): one day. Swap −2 pips on a lot = −$20.
        var start = new DateTime(2026, 7, 14, 20, 45, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 8).Select(i => Flat(start.AddMinutes(5 * i), 1.1));
        var history = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), bars.ToArray()) };
        var specs = new Dictionary<string, SymbolSpec> { ["EURUSD"] = Spec("EURUSD", swapLong: -2) };
        var b = new SimBroker(history, specs, new RewindConfig(StartBalance: 5000)) { Rules = new AccountRules() };
        b.Open(null, start.AddMinutes(5));
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        b.Walk(start.AddMinutes(5), start.AddMinutes(40), (_, _) => { });
        Assert.Equal(4980, b.Money().Balance, 2);

        var wednesday = new DateTime(2026, 7, 15, 20, 45, 0, DateTimeKind.Utc);
        var bars2 = Enumerable.Range(0, 8).Select(i => Flat(wednesday.AddMinutes(5 * i), 1.1));
        var h2 = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), bars2.ToArray()) };
        var b2 = new SimBroker(h2, specs, new RewindConfig(StartBalance: 5000)) { Rules = new AccountRules() };
        b2.Open(null, wednesday.AddMinutes(5));
        b2.MarketOrder("EURUSD", 1, Lot, null, null);
        b2.Walk(wednesday.AddMinutes(5), wednesday.AddMinutes(40), (_, _) => { });
        Assert.Equal(4940, b2.Money().Balance, 2);
    }

    [Fact]
    public void A_pending_order_expires_on_the_server()
    {
        var b = Make("Sum", 5000, Before(1.1).Concat(Bars(4, 1.1)));
        b.PlaceOrder(OrderType.Stop, "EURUSD", 1, Lot, 1.2, expiresUtc: T0.AddMinutes(10));
        var kinds = new List<EventKind>();
        b.Event += e => kinds.Add(e.Kind);
        Walk(b, 4);
        Assert.Equal([EventKind.OrderExpired], kinds);
        Assert.Empty(b.Orders());
    }

    [Fact]
    public void All_symbols_move_together_in_each_step()
    {
        var b = Make("Sum", 5000, Before(1.1).Append(Move(T0, 1.1, 1.2, 1.05, 1.15)), Before(1.3).Append(Move(T0, 1.3, 1.4, 1.25, 1.35)));
        var seen = new List<(double Eur, double Gbp)>();
        var times = new List<(DateTime, int)>();
        Walk(b, 1, (t, i) =>
        {
            seen.Add((b.QuoteOf("EURUSD").Bid, b.QuoteOf("GBPUSD").Bid));
            times.Add((t, i));
        });
        Assert.Equal([(1.1, 1.3), (1.05, 1.25), (1.2, 1.4), (1.15, 1.35)], seen);
        Assert.Equal([(T0, 0), (T0.AddSeconds(100), 1), (T0.AddSeconds(200), 2), (T0.AddSeconds(300), 3)], times);
    }

    [Fact]
    public void Without_rules_the_browser_replay_has_no_margin_or_refusals()
    {
        var history = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), Before(1.1).Concat(Bars(2, 1.1)).ToArray()) };
        var b = new SimBroker(history, new Dictionary<string, SymbolSpec> { ["EURUSD"] = Spec("EURUSD") }, new RewindConfig(StartBalance: 10));
        b.Open(null, T0);
        b.MarketOrder("EURUSD", 1, Lot, null, null);
        Assert.Equal(0, b.Money().Margin);
    }
}
