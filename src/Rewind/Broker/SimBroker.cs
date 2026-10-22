using System.Text.Json;
using Rewind.History;
using Rewind.Replay;

namespace Rewind.Broker;

/// <summary>The current price of a symbol; <see cref="Live"/> is false while its market is closed (no candle for a while).</summary>
public readonly record struct Quote(double Bid, double Ask, DateTime TimeUtc, bool Live);

/// <summary>
/// The simulated broker (spec §7): one USD hedging account. Prices come from the history as the replay cursor moves; each
/// 5-minute candle is walked through 4 price steps (spec §6.3): open → low → high → close for a rising candle, open → high →
/// low → close for a falling one, bid and ask each along their own candle. At every step it fills stop orders and hits
/// stop-losses and take-profits: at their price when the price moved there within the candle, at the step's price when it
/// jumped past them (a gap at the candle's open). Market orders fill at the current ask (buy) or bid (sell).
/// Thread-safe: one lock for everything; while the replay walks, the lock is let go between price steps, so requests (TM's
/// orders in exact mode, spec §6.2) land between two steps as they would between two ticks.
/// </summary>
public sealed class SimBroker : IMarket
{
    private static readonly TimeSpan ClosedAfter = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IReadOnlyDictionary<string, SymbolHistory> _history;
    private readonly IReadOnlyDictionary<string, SymbolSpec> _specs;
    private readonly RewindConfig _config;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Quote> _quotes = [];
    private readonly List<BrokerEvent> _recent = [];
    private AccountState _account = new();
    private Journal? _journal;
    private TmResults? _tm;
    private string? _path;
    private DateTime _now;

    public SimBroker(IReadOnlyDictionary<string, SymbolHistory> history, IReadOnlyDictionary<string, SymbolSpec> specs, RewindConfig config)
    {
        _history = history;
        _specs = specs;
        _config = config;
        _account = new AccountState { Balance = config.StartBalance, StartBalance = config.StartBalance };
    }

    /// <summary>Every event as it happens (step 4: the Open API server turns them into execution events).</summary>
    public event Action<BrokerEvent>? Event;

    /// <summary>After a change made by a request (an order, a close) — not after price steps, which the replay clock reports.</summary>
    public event Action? Changed;

    /// <summary>Every new price of a symbol (each price step, and all symbols after a jump or a session change). Inside the lock: never call back.</summary>
    public event Action<string, Quote>? QuoteChanged;

    public IReadOnlyDictionary<string, SymbolSpec> Specs => _specs;

    // ---- sessions ---------------------------------------------------------------------------------------------

    /// <summary>Loads the account of a session folder (or starts one with <paramref name="startBalance"/>) and prices at the cursor.</summary>
    public void Open(string? dir, DateTime cursorUtc, double? startBalance = null)
    {
        lock (_lock)
        {
            _journal?.Dispose();
            _tm?.Dispose();
            _path = dir is null ? null : Path.Combine(dir, "account.json");
            _account = _path is not null && File.Exists(_path)
                ? JsonSerializer.Deserialize<AccountState>(File.ReadAllText(_path)) ?? new AccountState()
                : new AccountState { Balance = startBalance ?? _config.StartBalance, StartBalance = startBalance ?? _config.StartBalance };
            _journal = dir is null ? null : new Journal(dir);
            _tm = dir is null ? null : new TmResults(dir, _specs, FindOpen);
            _recent.Clear();
            SetQuotes(cursorUtc);
            Save();
        }
    }

    public bool IsFlat
    {
        get { lock (_lock) return _account.Positions.Count == 0 && _account.Orders.Count == 0; }
    }

    /// <summary>A jump of the cursor (go to): only allowed when flat, so no position skips prices (spec §8).</summary>
    public void JumpTo(DateTime cursorUtc)
    {
        lock (_lock)
        {
            if (_account.Positions.Count > 0 || _account.Orders.Count > 0)
                throw new ArgumentException("Close all positions and orders first: go to and new sessions only when the account is flat (spec §8)");
            SetQuotes(cursorUtc);
        }
    }

    /// <summary>
    /// Walks every candle of every symbol that finishes after <paramref name="fromUtc"/> and by <paramref name="toUtc"/>, in
    /// time order, 4 price steps each (spec §6.3). <paramref name="afterStep"/> is called after each step outside the lock,
    /// so it may wait (exact mode, spec §6.2) while requests come in.
    /// </summary>
    public void AdvanceTo(DateTime fromUtc, DateTime toUtc, Action<PriceStep>? afterStep = null)
    {
        List<(string Symbol, Bar Bar)> bars;
        lock (_lock)
            bars = _history.Values
                .SelectMany(h => h.Bars.Skip(h.RevealedCount(fromUtc)).Take(h.RevealedCount(toUtc) - h.RevealedCount(fromUtc)).Select(b => (h.Symbol.Name, Bar: b)))
                .OrderBy(x => x.Bar.TimeUtc)
                .ToList();
        foreach (var (symbol, bar) in bars)
        {
            var path = PricePath(bar);
            for (var i = 0; i < path.Length; i++)
            {
                lock (_lock)
                {
                    var t = bar.TimeUtc + TimeSpan.FromTicks(Bar.Length.Ticks * i / (path.Length - 1));
                    _now = t;
                    _quotes[symbol] = new Quote(path[i].Bid, path[i].Ask, t, true);
                    QuoteChanged?.Invoke(symbol, _quotes[symbol]);
                    var changed = Trigger(symbol, path[i].Bid, path[i].Ask, gap: i == 0);
                    _tm?.Quote(symbol, path[i].Bid, path[i].Ask); // after the fills: a stop that closed a leg here caps its loss
                    if (changed)
                        Save();
                }
                afterStep?.Invoke(new PriceStep(symbol, bar, i, path[i].Bid, path[i].Ask));
            }
        }
        lock (_lock)
        {
            _now = toUtc;
            foreach (var s in _quotes.Keys.ToList())
                _quotes[s] = _quotes[s] with { Live = toUtc - _quotes[s].TimeUtc <= ClosedAfter };
        }
    }

    /// <summary>True while the symbol has an open position or a pending order (exact mode waits for TM only then).</summary>
    public bool InTrade(string symbol)
    {
        lock (_lock)
            return _account.Positions.Any(p => p.Symbol == symbol) || _account.Orders.Any(o => o.Symbol == symbol);
    }

    /// <summary>The 4 price steps of a candle (spec §6.3), bid and ask each along their own candle.</summary>
    public static (double Bid, double Ask)[] PricePath(Bar b) => b.BidClose >= b.BidOpen
        ? [(b.BidOpen, b.AskOpen), (b.BidLow, b.AskLow), (b.BidHigh, b.AskHigh), (b.BidClose, b.AskClose)]
        : [(b.BidOpen, b.AskOpen), (b.BidHigh, b.AskHigh), (b.BidLow, b.AskLow), (b.BidClose, b.AskClose)];

    // ---- requests (the order panel; in step 4 also TM) --------------------------------------------------------

    /// <param name="trailing">The stop-loss trails the price (spec §7); needs a stop-loss.</param>
    public Position MarketOrder(string symbol, int dir, long volume, double? stopLoss, double? takeProfit, string label = "", string comment = "", bool trailing = false)
    {
        lock (_lock)
        {
            var spec = Spec(symbol);
            CheckVolume(spec, volume);
            var q = LiveQuote(symbol);
            var price = dir > 0 ? q.Ask : q.Bid;
            CheckStops(spec, dir, q, stopLoss, takeProfit);
            CheckTrailing(stopLoss, trailing);
            var p = OpenPosition(symbol, dir, volume, price, label, comment, stopLoss, takeProfit, order: null, trailing);
            Done();
            return p;
        }
    }

    /// <summary>A pending stop order (TM's hedges): buy-stop above the ask, sell-stop below the bid.</summary>
    public PendingOrder StopOrder(string symbol, int dir, long volume, double stopPrice, string label = "", string comment = "") =>
        PlaceOrder(OrderType.Stop, symbol, dir, volume, stopPrice, label: label, comment: comment);

    /// <summary>
    /// A pending order (spec §7): stop (buy above the ask, sell below the bid) or limit (buy below the ask, sell above
    /// the bid), with an optional stop-loss, take-profit and trailing switch for the position it opens.
    /// </summary>
    public PendingOrder PlaceOrder(OrderType type, string symbol, int dir, long volume, double price, double? stopLoss = null, double? takeProfit = null,
        bool trailing = false, string label = "", string comment = "")
    {
        lock (_lock)
        {
            var spec = Spec(symbol);
            CheckVolume(spec, volume);
            var o = new PendingOrder { Id = _account.NextId, Symbol = symbol, Type = type, Dir = dir, Volume = volume, CreatedUtc = _now, Label = label, Comment = comment };
            SetOrder(o, spec, LiveQuote(symbol), price, stopLoss, takeProfit, trailing, "be");
            _account.NextId++;
            _account.Orders.Add(o);
            Record(new BrokerEvent(_now, EventKind.OrderPlaced, symbol, null, o.Id, dir, volume, o.Price, $"{o.Name} at {Fmt(spec, o.Price)}{Protection(spec, o)}", label) { Order = o.Copy() });
            Done();
            return o;
        }
    }

    /// <summary>Moves a pending order to <paramref name="price"/>; <paramref name="protection"/> replaces its stop-loss,
    /// take-profit and trailing switch (null keeps them).</summary>
    public void AmendOrder(long orderId, double price, (double? StopLoss, double? TakeProfit, bool Trailing)? protection = null)
    {
        lock (_lock)
        {
            var o = _account.Orders.FirstOrDefault(x => x.Id == orderId) ?? throw new BrokerException("OA_ORDER_NOT_FOUND", $"Order {orderId} not found");
            var spec = Spec(o.Symbol);
            var (sl, tp, trailing) = protection ?? (o.StopLoss, o.TakeProfit, o.Trailing);
            SetOrder(o, spec, LiveQuote(o.Symbol), price, sl, tp, trailing, "stay");
            Record(new BrokerEvent(_now, EventKind.OrderAmended, o.Symbol, null, o.Id, o.Dir, o.Volume, o.Price, $"{o.Name} moved to {Fmt(spec, o.Price)}{Protection(spec, o)}", o.Label) { Order = o.Copy() });
            Done();
        }
    }

    public void CancelOrder(long orderId)
    {
        lock (_lock)
        {
            var o = _account.Orders.FirstOrDefault(x => x.Id == orderId) ?? throw new BrokerException("OA_ORDER_NOT_FOUND", $"Order {orderId} not found");
            _account.Orders.Remove(o);
            Record(new BrokerEvent(_now, EventKind.OrderCancelled, o.Symbol, null, o.Id, o.Dir, o.Volume, o.Price, $"{o.Name} cancelled", o.Label) { Order = o.Copy() });
            Done();
        }
    }

    /// <summary>Sets a position's stop-loss and take-profit (null removes it) and whether the stop-loss trails (spec §7).</summary>
    public void AmendPosition(long positionId, double? stopLoss, double? takeProfit, bool trailing = false)
    {
        lock (_lock)
        {
            var p = FindPosition(positionId);
            var spec = Spec(p.Symbol);
            stopLoss = stopLoss is { } sl ? spec.Round(sl) : null;
            takeProfit = takeProfit is { } tp ? spec.Round(tp) : null;
            var q = LiveQuote(p.Symbol, allowClosed: true);
            CheckStops(spec, p.Dir, q, stopLoss, takeProfit);
            CheckTrailing(stopLoss, trailing);
            p.StopLoss = stopLoss;
            p.TakeProfit = takeProfit;
            SetTrailing(p, trailing, p.Dir > 0 ? q.Bid : q.Ask);
            Record(new BrokerEvent(_now, EventKind.ProtectionChanged, p.Symbol, p.Id, null, p.Dir, p.Volume, p.Entry,
                $"SL {(stopLoss is null ? "none" : Fmt(spec, stopLoss.Value))}{(p.Trailing ? " trailing" : "")}, TP {(takeProfit is null ? "none" : Fmt(spec, takeProfit.Value))}", p.Label) { Position = p.Copy() });
            Done();
        }
    }

    /// <summary>Closes <paramref name="volume"/> of a position (all of it when null) at the current price; <paramref name="by"/>
    /// Manual for the owner (browser), Tm for a close over the Open API.</summary>
    public void ClosePosition(long positionId, long? volume = null, CloseReason by = CloseReason.Manual)
    {
        lock (_lock)
        {
            var p = FindPosition(positionId);
            var spec = Spec(p.Symbol);
            var v = volume ?? p.Volume;
            if (v != p.Volume)
                CheckVolume(spec, v);
            if (v <= 0 || v > p.Volume)
                throw new BrokerException("TRADING_BAD_VOLUME", $"Can close at most {spec.Lots(p.Volume):0.##} lots");
            var q = LiveQuote(p.Symbol);
            Close(p, v, p.Dir > 0 ? q.Bid : q.Ask, by);
            Done();
        }
    }

    public void CloseAll()
    {
        lock (_lock)
        {
            foreach (var o in _account.Orders.ToList())
            {
                _account.Orders.Remove(o);
                Record(new BrokerEvent(_now, EventKind.OrderCancelled, o.Symbol, null, o.Id, o.Dir, o.Volume, o.Price, $"{o.Name} cancelled (close all)", o.Label) { Order = o.Copy() });
            }
            foreach (var p in _account.Positions.ToList())
            {
                var q = LiveQuote(p.Symbol, allowClosed: true);
                Close(p, p.Volume, p.Dir > 0 ? q.Bid : q.Ask, CloseReason.Manual);
            }
            Done();
        }
    }

    // ---- what the page shows ----------------------------------------------------------------------------------

    public object Snapshot()
    {
        lock (_lock)
        {
            var positions = _account.Positions.Select(p =>
            {
                var spec = _specs[p.Symbol];
                var q = _quotes.GetValueOrDefault(p.Symbol);
                var exit = p.Dir > 0 ? q.Bid : q.Ask;
                return new
                {
                    p.Id, p.Symbol, Side = Side(p.Dir), Lots = spec.Lots(p.Volume), p.Entry, p.StopLoss, p.TakeProfit, p.Trailing, p.Label, p.OpenUtc,
                    Price = exit,
                    Pips = Math.Round(p.Dir * (exit - p.Entry) / spec.PipSize, 1),
                    PnlUsd = Math.Round(Pnl(p, p.Volume, exit), 2),
                };
            }).ToList();
            var open = positions.Sum(p => p.PnlUsd);
            return new
            {
                Balance = Math.Round(_account.Balance, 2),
                Equity = Math.Round(_account.Balance + open, 2),
                OpenPnl = Math.Round(open, 2),
                CommissionPaid = Math.Round(_account.CommissionPaid, 2),
                _account.StartBalance,
                Positions = positions,
                Orders = _account.Orders.Select(o => new
                {
                    o.Id, o.Symbol, Side = Side(o.Dir), Type = o.Type.ToString().ToLowerInvariant(), o.Name, Lots = _specs[o.Symbol].Lots(o.Volume), o.Price,
                    o.StopLoss, o.TakeProfit, o.Trailing, o.Label, o.CreatedUtc,
                }),
                Quotes = _quotes.ToDictionary(kv => kv.Key, kv => new { kv.Value.Bid, kv.Value.Ask, kv.Value.Live }),
                Events = _recent.AsEnumerable().Reverse().Take(60).Select(e => new
                {
                    e.TimeUtc, Kind = e.Kind.ToString(), e.Symbol, e.PositionId, e.OrderId, Side = Side(e.Dir), Lots = _specs[e.Symbol].Lots(e.Volume), e.Price, e.Detail, e.Label,
                }),
            };
        }
    }

    public double Balance()
    {
        lock (_lock) return _account.Balance;
    }

    /// <summary>Copies of the open positions and pending orders (for tests and, in step 4, the Open API server).</summary>
    public IReadOnlyList<Position> Positions()
    {
        lock (_lock) return _account.Positions.ToList();
    }

    public IReadOnlyList<PendingOrder> Orders()
    {
        lock (_lock) return _account.Orders.ToList();
    }

    public string PositionSymbol(long positionId)
    {
        lock (_lock) return FindPosition(positionId).Symbol;
    }

    public Quote QuoteOf(string symbol)
    {
        lock (_lock) return _quotes.GetValueOrDefault(symbol);
    }

    // ---- the engine -------------------------------------------------------------------------------------------

    private void SetQuotes(DateTime cursorUtc)
    {
        _now = cursorUtc;
        _quotes.Clear();
        foreach (var h in _history.Values)
        {
            var n = h.RevealedCount(cursorUtc);
            if (n == 0)
                continue;
            var b = h.Bars[n - 1];
            _quotes[h.Symbol.Name] = new Quote(b.BidClose, b.AskClose, b.EndUtc, cursorUtc - b.EndUtc <= ClosedAfter);
            QuoteChanged?.Invoke(h.Symbol.Name, _quotes[h.Symbol.Name]);
        }
    }

    /// <summary>
    /// Fills and hits at one price step, on the side of the price cTrader uses: buy orders on the ask, sell orders on
    /// the bid; a long's SL/TP on the bid, a short's on the ask. A stop fills when the price reaches it, a limit when the
    /// price comes back to it. Within a candle the level's own price is used; at the open (gap) the open's price — worse
    /// than the level for a stop and a stop-loss (slippage), better for a limit and a take-profit. Trailing stops then
    /// follow the step's price (spec §7).
    /// </summary>
    private bool Trigger(string symbol, double bid, double ask, bool gap)
    {
        var changed = false;
        foreach (var o in _account.Orders.Where(o => o.Symbol == symbol).ToList())
        {
            var market = o.Dir > 0 ? ask : bid;
            var hit = o.Type == OrderType.Stop ? o.Dir * (market - o.Price) >= 0 : o.Dir * (market - o.Price) <= 0;
            if (!hit)
                continue;
            var price = gap ? market : o.Price;
            _account.Orders.Remove(o);
            Record(new BrokerEvent(_now, EventKind.OrderFilled, symbol, null, o.Id, o.Dir, o.Volume, price,
                $"{o.Name} {Fmt(_specs[symbol], o.Price)} filled at {Fmt(_specs[symbol], price)}", o.Label) { Order = o.Copy() });
            OpenPosition(symbol, o.Dir, o.Volume, price, o.Label, o.Comment, o.StopLoss, o.TakeProfit, o, o.Trailing);
            changed = true;
        }

        foreach (var p in _account.Positions.Where(p => p.Symbol == symbol).ToList())
        {
            var exitSide = p.Dir > 0 ? bid : ask;
            if (p.StopLoss is { } sl && (p.Dir > 0 ? exitSide <= sl : exitSide >= sl))
            {
                Close(p, p.Volume, gap ? exitSide : sl, CloseReason.StopLoss);
                changed = true;
            }
            else if (p.TakeProfit is { } tp && (p.Dir > 0 ? exitSide >= tp : exitSide <= tp))
            {
                Close(p, p.Volume, gap ? exitSide : tp, CloseReason.TakeProfit);
                changed = true;
            }
            else if (p is { Trailing: true, StopLoss: { } current })
            {
                // Moved without an event: TM sees the new stop-loss at its next reconcile (spec §10).
                var next = _specs[symbol].Round(exitSide - p.Dir * p.TrailDistance);
                if (p.Dir * (next - current) > 0)
                {
                    p.StopLoss = next;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private Position OpenPosition(string symbol, int dir, long volume, double price, string label, string comment, double? sl, double? tp, PendingOrder? order,
        bool trailing = false)
    {
        var spec = _specs[symbol];
        var commission = Commission(symbol, volume, price);
        var p = new Position
        {
            Id = _account.NextId++, Symbol = symbol, Dir = dir, Volume = volume, Entry = price, OpenUtc = _now, OpenRealUtc = DateTime.UtcNow,
            StopLoss = sl, TakeProfit = tp, InitialStopLoss = sl, Label = label, Comment = comment, OpenCommission = commission,
        };
        var q = _quotes[symbol];
        SetTrailing(p, trailing, price - dir * (q.Ask - q.Bid)); // from the price it would close at, at the fill
        _account.Positions.Add(p);
        _account.Balance -= commission;
        _account.CommissionPaid += commission;
        Record(new BrokerEvent(_now, EventKind.PositionOpened, symbol, p.Id, order?.Id, dir, volume, price,
            $"{Side(dir)} {spec.Lots(volume):0.##} at {Fmt(spec, price)}" + (sl is null ? "" : $", SL {Fmt(spec, sl.Value)}{(p.Trailing ? " trailing" : "")}") + (tp is null ? "" : $", TP {Fmt(spec, tp.Value)}"), label)
            { Position = p.Copy(), Order = order?.Copy(), Commission = commission, Balance = _account.Balance });
        return p;
    }

    private void Close(Position p, long volume, double price, CloseReason reason)
    {
        var spec = _specs[p.Symbol];
        var share = (double)volume / p.Volume;
        var openCommission = p.OpenCommission * share;
        var closeCommission = Commission(p.Symbol, volume, price);
        var pnl = Pnl(p, volume, price);
        _account.Balance += pnl - closeCommission;
        _account.CommissionPaid += closeCommission;
        p.OpenCommission -= openCommission;
        p.Volume -= volume;
        var all = p.Volume == 0;
        if (all)
            _account.Positions.Remove(p);

        var what = reason switch { CloseReason.StopLoss => "stop-loss hit", CloseReason.TakeProfit => "take-profit hit", CloseReason.Tm => "closed by TM", _ => "closed" };
        Record(new BrokerEvent(_now, all ? EventKind.PositionClosed : EventKind.PositionReduced, p.Symbol, p.Id, null, p.Dir, volume, price,
            $"{what}: {spec.Lots(volume):0.##} at {Fmt(spec, price)}, {pnl - openCommission - closeCommission:+0.00;-0.00} USD", p.Label)
            { Position = p.Copy(), Reason = reason, Commission = closeCommission, GrossProfit = pnl, Balance = _account.Balance });
        _journal?.Trade(p, spec, volume, price, _now, reason, pnl, openCommission + closeCommission);
    }

    /// <summary>Profit in USD of closing <paramref name="volume"/> at <paramref name="exit"/> (before commission).</summary>
    private double Pnl(Position p, long volume, double exit) =>
        p.Dir * (exit - p.Entry) * volume / 100.0 * QuoteToUsd(_specs[p.Symbol].Quote);

    /// <summary>Commission of one side in USD: per million USD of traded value (spec §7).</summary>
    private double Commission(string symbol, long volume, double price)
    {
        var spec = _specs[symbol];
        return spec.CommissionPerMillionUsd == 0 ? 0
            : Math.Round(volume / 100.0 * price * QuoteToUsd(spec.Quote) * spec.CommissionPerMillionUsd / 1_000_000, 2);
    }

    /// <summary>USD value of 1 unit of the quote currency: GBPUSD = EURUSD / EURGBP, USDJPY = GBPJPY / GBPUSD (spec §7).</summary>
    private double QuoteToUsd(string currency) => currency switch
    {
        "USD" => 1,
        "GBP" => GbpUsd(),
        "JPY" => _quotes.TryGetValue("GBPJPY", out var gj) && gj.Bid > 0 ? GbpUsd() / Mid(gj) : 1 / Fallback("USDJPY"),
        _ => throw new BrokerException("UNSUPPORTED", $"No conversion from {currency} to USD"),
    };

    private double GbpUsd() =>
        _quotes.TryGetValue("EURUSD", out var eu) && _quotes.TryGetValue("EURGBP", out var eg) && eg.Bid > 0 ? Mid(eu) / Mid(eg) : Fallback("GBPUSD");

    private double Fallback(string pair) => _config.FallbackRates?.GetValueOrDefault(pair) is { } r and > 0 ? r
        : throw new BrokerException("UNSUPPORTED", $"No history and no fallback rate for {pair}");

    private static double Mid(Quote q) => (q.Bid + q.Ask) / 2;

    // ---- checks and helpers -----------------------------------------------------------------------------------

    private SymbolSpec Spec(string symbol) =>
        _specs.TryGetValue(symbol, out var s) && _history.ContainsKey(symbol) ? s : throw new BrokerException("SYMBOL_NOT_FOUND", $"No history for {symbol}");

    private Quote LiveQuote(string symbol, bool allowClosed = false)
    {
        if (!_quotes.TryGetValue(symbol, out var q))
            throw new BrokerException("MARKET_CLOSED", $"{symbol}: no price yet");
        if (!q.Live && !allowClosed)
            throw new BrokerException("MARKET_CLOSED", $"{symbol}: market closed (last price {q.TimeUtc:ddd HH:mm} UTC)");
        return q;
    }

    private static void CheckVolume(SymbolSpec s, long volume)
    {
        if (volume < s.MinVolume || volume > s.MaxVolume || volume % s.StepVolume != 0)
            throw new BrokerException("TRADING_BAD_VOLUME",
                $"{s.Name}: volume must be {s.Lots(s.MinVolume):0.##}–{s.Lots(s.MaxVolume):0.##} lots in steps of {s.Lots(s.StepVolume):0.##}");
    }

    /// <summary>cTrader refuses a stop-loss or take-profit on the wrong side of the price it closes at.</summary>
    private static void CheckStops(SymbolSpec s, int dir, Quote q, double? sl, double? tp)
    {
        var exit = dir > 0 ? q.Bid : q.Ask;
        if (sl is { } l && (dir > 0 ? l >= exit : l <= exit))
            throw new BrokerException("TRADING_BAD_STOPS", $"Stop-loss {Fmt(s, l)} must be {(dir > 0 ? "below the bid" : "above the ask")} ({Fmt(s, exit)})");
        if (tp is { } t && (dir > 0 ? t <= exit : t >= exit))
            throw new BrokerException("TRADING_BAD_STOPS", $"Take-profit {Fmt(s, t)} must be {(dir > 0 ? "above the bid" : "below the ask")} ({Fmt(s, exit)})");
    }

    /// <summary>
    /// Checks and sets a pending order's price and protection. cTrader's codes: a stop on the wrong side of the price
    /// TRADING_BAD_STOPS, a limit TRADING_BAD_PRICES; the stop-loss / take-profit must lie on the right side of the order price.
    /// </summary>
    private static void SetOrder(PendingOrder o, SymbolSpec s, Quote q, double price, double? sl, double? tp, bool trailing, string verb)
    {
        price = s.Round(price);
        var market = o.Dir > 0 ? q.Ask : q.Bid;
        var where = (o.Type, o.Dir) switch
        {
            (OrderType.Stop, > 0) => "above the ask",
            (OrderType.Stop, _) => "below the bid",
            (OrderType.Limit, > 0) => "below the ask",
            _ => "above the bid",
        };
        var ahead = o.Type == OrderType.Stop ? o.Dir * (price - market) > 0 : o.Dir * (price - market) < 0;
        if (!ahead)
            throw new BrokerException(o.Type == OrderType.Stop ? "TRADING_BAD_STOPS" : "TRADING_BAD_PRICES", $"A {o.Name} must {verb} {where} ({Fmt(s, market)})");
        sl = sl is { } l ? s.Round(l) : null;
        tp = tp is { } t ? s.Round(t) : null;
        if (sl is { } l2 && o.Dir * (price - l2) <= 0)
            throw new BrokerException("TRADING_BAD_STOPS", $"Stop-loss {Fmt(s, l2)} must be {(o.Dir > 0 ? "below" : "above")} the order price {Fmt(s, price)}");
        if (tp is { } t2 && o.Dir * (t2 - price) <= 0)
            throw new BrokerException("TRADING_BAD_STOPS", $"Take-profit {Fmt(s, t2)} must be {(o.Dir > 0 ? "above" : "below")} the order price {Fmt(s, price)}");
        CheckTrailing(sl, trailing);
        (o.Price, o.StopLoss, o.TakeProfit, o.Trailing) = (price, sl, tp, trailing);
    }

    private static void CheckTrailing(double? stopLoss, bool trailing)
    {
        if (trailing && stopLoss is null)
            throw new BrokerException("TRADING_BAD_STOPS", "A trailing stop needs a stop-loss");
    }

    /// <summary>Switches a position's trailing stop on or off (spec §7); its distance is fixed now, from <paramref name="exit"/>,
    /// the price the position would close at.</summary>
    private static void SetTrailing(Position p, bool on, double exit)
    {
        p.Trailing = on && p.StopLoss is not null;
        p.TrailDistance = p.Trailing ? Math.Abs(exit - p.StopLoss!.Value) : 0;
    }

    private static string Protection(SymbolSpec s, PendingOrder o) =>
        (o.StopLoss is { } sl ? $", SL {Fmt(s, sl)}{(o.Trailing ? " trailing" : "")}" : "") + (o.TakeProfit is { } tp ? $", TP {Fmt(s, tp)}" : "");

    private Position FindPosition(long id) =>
        _account.Positions.FirstOrDefault(p => p.Id == id) ?? throw new BrokerException("POSITION_NOT_FOUND", $"Position {id} not found");

    private void Record(BrokerEvent e)
    {
        _recent.Add(e);
        if (_recent.Count > 500)
            _recent.RemoveRange(0, 100);
        _journal?.Event(e, _specs[e.Symbol]);
        _tm?.On(e);
        Event?.Invoke(e);
    }

    private void Done()
    {
        Save();
        // Outside the lock: the page update reads the account again.
        if (Changed is { } changed)
            ThreadPool.QueueUserWorkItem(_ => changed());
    }

    private void Save()
    {
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_account, Json));
        File.Move(_path + ".tmp", _path, overwrite: true);
        _tm?.Save();
    }

    private Position? FindOpen(long id) => _account.Positions.FirstOrDefault(p => p.Id == id);

    public static string Side(int dir) => dir > 0 ? "buy" : "sell";
    private static string Fmt(SymbolSpec s, double price) => price.ToString("F" + s.Digits, System.Globalization.CultureInfo.InvariantCulture);
}
