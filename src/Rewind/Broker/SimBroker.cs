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
/// Thread-safe: one lock for everything.
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

    public IReadOnlyDictionary<string, SymbolSpec> Specs => _specs;

    // ---- sessions ---------------------------------------------------------------------------------------------

    /// <summary>Loads the account of a session folder (or starts one with <paramref name="startBalance"/>) and prices at the cursor.</summary>
    public void Open(string? dir, DateTime cursorUtc, double? startBalance = null)
    {
        lock (_lock)
        {
            _journal?.Dispose();
            _path = dir is null ? null : Path.Combine(dir, "account.json");
            _account = _path is not null && File.Exists(_path)
                ? JsonSerializer.Deserialize<AccountState>(File.ReadAllText(_path)) ?? new AccountState()
                : new AccountState { Balance = startBalance ?? _config.StartBalance, StartBalance = startBalance ?? _config.StartBalance };
            _journal = dir is null ? null : new Journal(dir);
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

    /// <summary>Walks every candle of every symbol that finishes after <paramref name="fromUtc"/> and by <paramref name="toUtc"/>, in time order.</summary>
    public void AdvanceTo(DateTime fromUtc, DateTime toUtc)
    {
        lock (_lock)
        {
            var bars = _history.Values
                .SelectMany(h => h.Bars.Skip(h.RevealedCount(fromUtc)).Take(h.RevealedCount(toUtc) - h.RevealedCount(fromUtc)).Select(b => (h.Symbol.Name, Bar: b)))
                .OrderBy(x => x.Bar.TimeUtc);
            var changed = false;
            foreach (var (symbol, bar) in bars)
                changed |= Walk(symbol, bar);
            _now = toUtc;
            foreach (var s in _quotes.Keys.ToList())
                _quotes[s] = _quotes[s] with { Live = toUtc - _quotes[s].TimeUtc <= ClosedAfter };
            if (changed)
                Save();
        }
    }

    // ---- requests (the order panel; in step 4 also TM) --------------------------------------------------------

    public Position MarketOrder(string symbol, int dir, long volume, double? stopLoss, double? takeProfit, string label = "", string comment = "")
    {
        lock (_lock)
        {
            var spec = Spec(symbol);
            CheckVolume(spec, volume);
            var q = LiveQuote(symbol);
            var price = dir > 0 ? q.Ask : q.Bid;
            CheckStops(spec, dir, q, stopLoss, takeProfit);
            var p = OpenPosition(symbol, dir, volume, price, label, comment, stopLoss, takeProfit, orderId: null);
            Done();
            return p;
        }
    }

    public PendingOrder StopOrder(string symbol, int dir, long volume, double stopPrice, string label = "", string comment = "")
    {
        lock (_lock)
        {
            var spec = Spec(symbol);
            CheckVolume(spec, volume);
            var q = LiveQuote(symbol);
            stopPrice = spec.Round(stopPrice);
            if (dir > 0 ? stopPrice <= q.Ask : stopPrice >= q.Bid)
                throw new BrokerException("TRADING_BAD_STOPS", $"A {(dir > 0 ? "buy-stop must be above the ask" : "sell-stop must be below the bid")} ({Fmt(spec, dir > 0 ? q.Ask : q.Bid)})");
            var o = new PendingOrder { Id = _account.NextId++, Symbol = symbol, Dir = dir, Volume = volume, StopPrice = stopPrice, CreatedUtc = _now, Label = label, Comment = comment };
            _account.Orders.Add(o);
            Record(new BrokerEvent(_now, EventKind.OrderPlaced, symbol, null, o.Id, dir, volume, stopPrice, $"{Side(dir)}-stop at {Fmt(spec, stopPrice)}", label));
            Done();
            return o;
        }
    }

    public void AmendOrder(long orderId, double stopPrice)
    {
        lock (_lock)
        {
            var o = _account.Orders.FirstOrDefault(x => x.Id == orderId) ?? throw new BrokerException("OA_ORDER_NOT_FOUND", $"Order {orderId} not found");
            var spec = Spec(o.Symbol);
            var q = LiveQuote(o.Symbol);
            stopPrice = spec.Round(stopPrice);
            if (o.Dir > 0 ? stopPrice <= q.Ask : stopPrice >= q.Bid)
                throw new BrokerException("TRADING_BAD_STOPS", $"A {(o.Dir > 0 ? "buy-stop must stay above the ask" : "sell-stop must stay below the bid")} ({Fmt(spec, o.Dir > 0 ? q.Ask : q.Bid)})");
            o.StopPrice = stopPrice;
            Record(new BrokerEvent(_now, EventKind.OrderAmended, o.Symbol, null, o.Id, o.Dir, o.Volume, stopPrice, $"moved to {Fmt(spec, stopPrice)}", o.Label));
            Done();
        }
    }

    public void CancelOrder(long orderId)
    {
        lock (_lock)
        {
            var o = _account.Orders.FirstOrDefault(x => x.Id == orderId) ?? throw new BrokerException("OA_ORDER_NOT_FOUND", $"Order {orderId} not found");
            _account.Orders.Remove(o);
            Record(new BrokerEvent(_now, EventKind.OrderCancelled, o.Symbol, null, o.Id, o.Dir, o.Volume, o.StopPrice, "cancelled", o.Label));
            Done();
        }
    }

    /// <summary>Sets a position's stop-loss and take-profit; null removes it.</summary>
    public void AmendPosition(long positionId, double? stopLoss, double? takeProfit)
    {
        lock (_lock)
        {
            var p = FindPosition(positionId);
            var spec = Spec(p.Symbol);
            stopLoss = stopLoss is { } sl ? spec.Round(sl) : null;
            takeProfit = takeProfit is { } tp ? spec.Round(tp) : null;
            CheckStops(spec, p.Dir, LiveQuote(p.Symbol, allowClosed: true), stopLoss, takeProfit);
            p.StopLoss = stopLoss;
            p.TakeProfit = takeProfit;
            Record(new BrokerEvent(_now, EventKind.ProtectionChanged, p.Symbol, p.Id, null, p.Dir, p.Volume, p.Entry,
                $"SL {(stopLoss is null ? "none" : Fmt(spec, stopLoss.Value))}, TP {(takeProfit is null ? "none" : Fmt(spec, takeProfit.Value))}", p.Label));
            Done();
        }
    }

    /// <summary>Closes <paramref name="volume"/> of a position (all of it when null) at the current price.</summary>
    public void ClosePosition(long positionId, long? volume = null)
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
            Close(p, v, p.Dir > 0 ? q.Bid : q.Ask, CloseReason.Manual);
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
                Record(new BrokerEvent(_now, EventKind.OrderCancelled, o.Symbol, null, o.Id, o.Dir, o.Volume, o.StopPrice, "cancelled (close all)", o.Label));
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
                    p.Id, p.Symbol, Side = Side(p.Dir), Lots = spec.Lots(p.Volume), p.Entry, p.StopLoss, p.TakeProfit, p.Label, p.OpenUtc,
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
                Orders = _account.Orders.Select(o => new { o.Id, o.Symbol, Side = Side(o.Dir), Lots = _specs[o.Symbol].Lots(o.Volume), Price = o.StopPrice, o.Label, o.CreatedUtc }),
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
        }
    }

    /// <summary>The 4 price steps of a candle (spec §6.3). Returns true if anything was filled or hit.</summary>
    private bool Walk(string symbol, Bar b)
    {
        var rising = b.BidClose >= b.BidOpen;
        (double Bid, double Ask)[] path = rising
            ? [(b.BidOpen, b.AskOpen), (b.BidLow, b.AskLow), (b.BidHigh, b.AskHigh), (b.BidClose, b.AskClose)]
            : [(b.BidOpen, b.AskOpen), (b.BidHigh, b.AskHigh), (b.BidLow, b.AskLow), (b.BidClose, b.AskClose)];
        var changed = false;
        for (var i = 0; i < path.Length; i++)
        {
            var t = b.TimeUtc + TimeSpan.FromTicks(Bar.Length.Ticks * i / (path.Length - 1));
            _now = t;
            _quotes[symbol] = new Quote(path[i].Bid, path[i].Ask, t, true);
            changed |= Trigger(symbol, path[i].Bid, path[i].Ask, gap: i == 0);
        }
        return changed;
    }

    /// <summary>
    /// Fills and hits at one price step. Buy-stop and a short's SL / a long's TP watch the ask/bid as cTrader does:
    /// buy-stop when the ask reaches it; sell-stop when the bid does; a long's SL/TP on the bid, a short's on the ask.
    /// Within a candle the level's own price is used; at the open (gap) the open's price.
    /// </summary>
    private bool Trigger(string symbol, double bid, double ask, bool gap)
    {
        var changed = false;
        foreach (var o in _account.Orders.Where(o => o.Symbol == symbol).ToList())
        {
            var hit = o.Dir > 0 ? ask >= o.StopPrice : bid <= o.StopPrice;
            if (!hit)
                continue;
            var price = gap ? (o.Dir > 0 ? ask : bid) : o.StopPrice;
            _account.Orders.Remove(o);
            Record(new BrokerEvent(_now, EventKind.OrderFilled, symbol, null, o.Id, o.Dir, o.Volume, price,
                $"{Side(o.Dir)}-stop {Fmt(_specs[symbol], o.StopPrice)} filled at {Fmt(_specs[symbol], price)}", o.Label));
            OpenPosition(symbol, o.Dir, o.Volume, price, o.Label, o.Comment, null, null, o.Id);
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
        }
        return changed;
    }

    private Position OpenPosition(string symbol, int dir, long volume, double price, string label, string comment, double? sl, double? tp, long? orderId)
    {
        var spec = _specs[symbol];
        var commission = Commission(symbol, volume, price);
        var p = new Position
        {
            Id = _account.NextId++, Symbol = symbol, Dir = dir, Volume = volume, Entry = price, OpenUtc = _now,
            StopLoss = sl, TakeProfit = tp, InitialStopLoss = sl, Label = label, Comment = comment, OpenCommission = commission,
        };
        _account.Positions.Add(p);
        _account.Balance -= commission;
        _account.CommissionPaid += commission;
        Record(new BrokerEvent(_now, EventKind.PositionOpened, symbol, p.Id, orderId, dir, volume, price,
            $"{Side(dir)} {spec.Lots(volume):0.##} at {Fmt(spec, price)}" + (sl is null ? "" : $", SL {Fmt(spec, sl.Value)}") + (tp is null ? "" : $", TP {Fmt(spec, tp.Value)}"), label));
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

        var what = reason switch { CloseReason.StopLoss => "stop-loss hit", CloseReason.TakeProfit => "take-profit hit", _ => "closed" };
        Record(new BrokerEvent(_now, all ? EventKind.PositionClosed : EventKind.PositionReduced, p.Symbol, p.Id, null, p.Dir, volume, price,
            $"{what}: {spec.Lots(volume):0.##} at {Fmt(spec, price)}, {pnl - openCommission - closeCommission:+0.00;-0.00} USD", p.Label));
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

    private Position FindPosition(long id) =>
        _account.Positions.FirstOrDefault(p => p.Id == id) ?? throw new BrokerException("POSITION_NOT_FOUND", $"Position {id} not found");

    private void Record(BrokerEvent e)
    {
        _recent.Add(e);
        if (_recent.Count > 500)
            _recent.RemoveRange(0, 100);
        _journal?.Event(e, _specs[e.Symbol]);
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
    }

    public static string Side(int dir) => dir > 0 ? "buy" : "sell";
    private static string Fmt(SymbolSpec s, double price) => price.ToString("F" + s.Digits, System.Globalization.CultureInfo.InvariantCulture);
}
