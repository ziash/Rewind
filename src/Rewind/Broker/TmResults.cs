using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rewind.Broker;

/// <summary>
/// TM's results per leg (spec §8, tm-trades.csv), measured from the broker's own fills with TM's formulas (Trader's
/// docs/bots/tm.md §1, §5): a trade is the owner's main plus everything labelled TM-xxxxxx-P&lt;mainId&gt;-&lt;role&gt;; its legs
/// are the main and the mirror (MIR), each with its hedge (HMAIN, HMIR). H = the first hedge order's price (moved with the
/// hedges only while no hedge is filled, as TM does); 1R = |H − leg entry|; a leg's loss = its own open P/L + its open
/// hedge + its earlier hedges, per unit of price. A row is written once the leg and its hedge are both closed.
/// Unfinished legs are kept in tm-open.json. Called by <see cref="SimBroker"/> inside its lock.
/// </summary>
public sealed partial class TmResults : IDisposable
{
    public const string Header =
        "trade,leg,symbol,side,lots,open_time_utc,close_time_utc,entry,hedge_level,r_pips,exit,hedge_cycles,hedges_r,break_even,max_loss_r,closed_by,result_r,commission_usd,pnl_usd";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IReadOnlyDictionary<string, SymbolSpec> _specs;
    private readonly Func<long, Position?> _position;
    private readonly string _openPath;
    private readonly StreamWriter _csv;
    private State _state;
    private bool _dirty;

    /// <param name="position">An open position by ID (the main is found this way when TM takes it over).</param>
    public TmResults(string dir, IReadOnlyDictionary<string, SymbolSpec> specs, Func<long, Position?> position)
    {
        _specs = specs;
        _position = position;
        _openPath = Path.Combine(dir, "tm-open.json");
        _state = File.Exists(_openPath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_openPath)) ?? new State() : new State();
        var csvPath = Path.Combine(dir, "tm-trades.csv");
        var isNew = !File.Exists(csvPath) || new FileInfo(csvPath).Length == 0;
        _csv = new StreamWriter(csvPath, append: true, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        if (isNew)
            _csv.WriteLine(Header);
    }

    /// <summary>A trade TM manages: its hedge level and its legs.</summary>
    public sealed class Trade
    {
        public long MainId { get; set; }
        public double? H { get; set; }
        public List<Leg> Legs { get; set; } = [];
    }

    public sealed class Leg
    {
        public string Role { get; set; } = "";              // MAIN or MIR
        public long PositionId { get; set; }
        public string Symbol { get; set; } = "";
        public int Dir { get; set; }
        public long OpenVolume { get; set; }
        public double Entry { get; set; }
        public DateTime OpenUtc { get; set; }
        public double R { get; set; }                       // |H − Entry|; 0 until the first hedge order
        public int Cycles { get; set; }
        public double HedgesRealized { get; set; }          // price per unit, + = gain
        public long? HedgeOrderId { get; set; }
        public long? HedgePositionId { get; set; }
        public long HedgeVolume { get; set; }
        public double HedgeFill { get; set; }
        public bool Raised { get; set; }                    // re-hedged 1R beyond the price after a +1 pip exit (tm.md §4)
        public bool BreakEven { get; set; }
        public double MaxLossR { get; set; }
        public bool Closed { get; set; }
        public DateTime CloseUtc { get; set; }
        public double ExitSum { get; set; }                 // price × volume of the closed parts
        public long ClosedVolume { get; set; }
        public CloseReason? ClosedBy { get; set; }
        public double Commission { get; set; }
        public double Gross { get; set; }
    }

    public sealed class State
    {
        public List<Trade> Trades { get; set; } = [];

        /// <summary>The owner's pending orders that filled: orderId → the main's positionId (a mirror order is labelled O&lt;orderId&gt;).</summary>
        public Dictionary<long, long> OrderMains { get; set; } = [];

        /// <summary>Mirrors of pending orders that filled before the owner's order: orderId → the mirror position.</summary>
        public Dictionary<long, Position> EarlyMirrors { get; set; } = [];
    }

    /// <summary>The legs still open (for the tests and the page).</summary>
    public IReadOnlyList<Trade> Open => _state.Trades;

    // P<main positionId>, or O<owner's orderId> for the mirror of a pending order (Trader tm.md §7).
    [GeneratedRegex(@"^TM-[0-9A-Za-z]{6}-(P|O)(\d+)-(MIR|HMAIN|HMIR)$")]
    private static partial Regex LabelRule();

    private static (long MainId, string Role, bool ByOrder)? Parse(string label) =>
        LabelRule().Match(label) is { Success: true } m
            ? (long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[3].Value, m.Groups[1].Value == "O")
            : null;

    /// <summary>Every broker event, in order.</summary>
    public void On(BrokerEvent e)
    {
        var tm = Parse(e.Label);
        if (tm is { ByOrder: true } byOrder && e is { Kind: EventKind.PositionOpened, Position: { } mirror })
        {
            // The mirror order of the owner's pending order filled: the trade's MIR leg (its closes are found by position below).
            if (_state.OrderMains.TryGetValue(byOrder.MainId, out var mainId))
                EnsureLeg(mainId, "MIR", mirror);
            else
                _state.EarlyMirrors[byOrder.MainId] = mirror.Copy();
            _dirty = true;
            Finish();
            return;
        }
        if (tm is null && e is { Kind: EventKind.PositionOpened, Order: { } fromOrder, Position: { } main })
        {
            _state.OrderMains[fromOrder.Id] = main.Id;
            if (_state.EarlyMirrors.Remove(fromOrder.Id, out var early))
                EnsureLeg(main.Id, "MIR", early);
            _dirty = true;
            return;
        }
        switch (e.Kind)
        {
            case EventKind.OrderPlaced or EventKind.OrderAmended when tm is { Role: "HMAIN" or "HMIR" } h:
                HedgeOrder(h.MainId, h.Role, e.OrderId!.Value, e.Price);
                break;
            case EventKind.OrderFilled or EventKind.OrderCancelled when tm is { Role: "HMAIN" or "HMIR" } h:
                if (FindLeg(h.MainId, LegOf(h.Role)) is { } l1 && l1.HedgeOrderId == e.OrderId)
                    l1.HedgeOrderId = null;
                break;
            case EventKind.PositionOpened when tm is { Role: "MIR" } m && e.Position is { } p:
                EnsureLeg(m.MainId, "MIR", p);
                break;
            case EventKind.PositionOpened when tm is { Role: "HMAIN" or "HMIR" } h && e.Position is { } p:
                if (EnsureLeg(h.MainId, LegOf(h.Role), null) is { } l2)
                {
                    l2.Cycles++;
                    l2.HedgePositionId = p.Id;
                    l2.HedgeVolume = p.Volume;
                    l2.HedgeFill = e.Price;
                    l2.Commission += e.Commission;
                }
                break;
            case EventKind.ProtectionChanged when e.Position is { } p && LegByPosition(p.Id) is { } l3:
                if (p.StopLoss is { } sl && Math.Abs(sl - l3.Entry) < _specs[l3.Symbol].PipSize * 0.05)
                    l3.BreakEven = true;
                break;
            case EventKind.PositionClosed or EventKind.PositionReduced when e.PositionId is { } id:
                Closed(e, id);
                break;
            default:
                return;
        }
        _dirty = true;
        Finish();
    }

    /// <summary>Every price step of a symbol: the largest loss of its open legs.</summary>
    public void Quote(string symbol, double bid, double ask)
    {
        foreach (var leg in _state.Trades.SelectMany(t => t.Legs).Where(l => l.Symbol == symbol && !l.Closed && l.R > 0))
        {
            var loss = LossR(leg, bid, ask);
            if (loss > leg.MaxLossR)
                leg.MaxLossR = loss;
        }
    }

    /// <summary>TM's loss in R of a leg (tm.md §5): its own open P/L + its open hedge + its earlier hedges.</summary>
    public static double LossR(Leg leg, double bid, double ask)
    {
        var profit = leg.Dir * ((leg.Dir > 0 ? bid : ask) - leg.Entry);
        var hd = -leg.Dir;
        var hedge = leg.HedgePositionId is null ? 0 : hd * ((hd > 0 ? bid : ask) - leg.HedgeFill);
        return -(profit + hedge + leg.HedgesRealized) / leg.R;
    }

    private static string LegOf(string hedgeRole) => hedgeRole == "HMAIN" ? "MAIN" : "MIR";

    private void HedgeOrder(long mainId, string hedgeRole, long orderId, double price)
    {
        var leg = EnsureLeg(mainId, LegOf(hedgeRole), null);
        var trade = FindTrade(mainId);
        if (leg is null || trade is null)
            return;
        leg.HedgeOrderId = orderId;
        if (trade.H is not { } h)
            SetH(trade, price);
        else if (Math.Abs(price - h) < _specs[leg.Symbol].PipSize * 0.05)
            leg.Raised = false;
        else if (!leg.Raised && !leg.BreakEven && trade.Legs.All(l => l.HedgePositionId is null))
            SetH(trade, price); // the owner moved the level (new stop-loss on the main, or a hedge dragged): TM moves both, 1R anew
    }

    private static void SetH(Trade trade, double h)
    {
        trade.H = h;
        foreach (var l in trade.Legs)
            l.R = Math.Abs(h - l.Entry);
    }

    private void Closed(BrokerEvent e, long id)
    {
        foreach (var leg in _state.Trades.SelectMany(t => t.Legs))
        {
            if (leg.PositionId == id)
            {
                leg.ExitSum += e.Price * e.Volume;
                leg.ClosedVolume += e.Volume;
                leg.Gross += e.GrossProfit;
                leg.Commission += e.Commission;
                if (e.Kind == EventKind.PositionClosed)
                {
                    leg.Closed = true;
                    leg.CloseUtc = e.TimeUtc;
                    leg.ClosedBy = e.Reason;
                    if (leg.R > 0)
                    {
                        var hd = -leg.Dir;
                        var openHedge = leg.HedgePositionId is null ? 0 : hd * (e.Price - leg.HedgeFill); // close enough: the hedge goes next
                        leg.MaxLossR = Math.Max(leg.MaxLossR, -(leg.Dir * (leg.ExitSum / leg.ClosedVolume - leg.Entry) + openHedge + leg.HedgesRealized) / leg.R);
                    }
                }
                return;
            }
            if (leg.HedgePositionId == id)
            {
                var gain = -leg.Dir * (e.Price - leg.HedgeFill);
                leg.HedgesRealized += gain * e.Volume / leg.HedgeVolume;
                leg.Raised = gain > 0; // closed at fill + 1 pip (armed): TM re-hedges 1R beyond the price first
                leg.Gross += e.GrossProfit;
                leg.Commission += e.Commission;
                if (e.Kind == EventKind.PositionClosed)
                    leg.HedgePositionId = null;
                return;
            }
        }
    }

    private Trade? FindTrade(long mainId) => _state.Trades.FirstOrDefault(t => t.MainId == mainId);

    private Leg? FindLeg(long mainId, string role) => FindTrade(mainId)?.Legs.FirstOrDefault(l => l.Role == role);

    private Leg? LegByPosition(long id) => _state.Trades.SelectMany(t => t.Legs).FirstOrDefault(l => l.PositionId == id);

    /// <summary>The leg, created when TM first acts on it: the main from the open position with the trade's ID, the mirror from its opening.</summary>
    private Leg? EnsureLeg(long mainId, string role, Position? opened)
    {
        if (FindLeg(mainId, role) is { } found)
            return found;
        var p = opened ?? (role == "MAIN" ? _position(mainId) : null);
        if (p is null)
            return null;
        var trade = FindTrade(mainId);
        if (trade is null)
        {
            trade = new Trade { MainId = mainId };
            _state.Trades.Add(trade);
        }
        if (role == "MIR")
            EnsureLeg(mainId, "MAIN", null); // the main first, so the rows come out in order
        var leg = new Leg
        {
            Role = role, PositionId = p.Id, Symbol = p.Symbol, Dir = p.Dir, OpenVolume = p.Volume, Entry = p.Entry, OpenUtc = p.OpenUtc,
            Commission = p.OpenCommission, R = trade.H is { } h ? Math.Abs(h - p.Entry) : 0,
        };
        trade.Legs.Add(leg);
        return leg;
    }

    /// <summary>Writes the legs that are done (leg and hedge closed, no hedge order left) and forgets finished trades.</summary>
    private void Finish()
    {
        foreach (var trade in _state.Trades.ToList())
        {
            foreach (var leg in trade.Legs.Where(l => l.Closed && l.HedgeOrderId is null && l.HedgePositionId is null && l.ClosedVolume > 0).ToList())
            {
                Write(trade, leg);
                trade.Legs.Remove(leg);
            }
            if (trade.Legs.Count == 0)
                _state.Trades.Remove(trade);
        }
        foreach (var (orderId, mainId) in _state.OrderMains.ToList())
            if (_position(mainId) is null && FindTrade(mainId) is null)
                _state.OrderMains.Remove(orderId);
    }

    private void Write(Trade trade, Leg leg)
    {
        var s = _specs[leg.Symbol];
        var exit = leg.ExitSum / leg.ClosedVolume;
        string R(double v) => leg.R > 0 ? Num(v / leg.R, 2) : "";
        _csv.WriteLine(string.Join(',',
            $"P{trade.MainId}", leg.Role, leg.Symbol, SimBroker.Side(leg.Dir), Num(s.Lots(leg.OpenVolume), 2), Time(leg.OpenUtc), Time(leg.CloseUtc),
            Num(leg.Entry, s.Digits), trade.H is { } h ? Num(h, s.Digits) : "", leg.R > 0 ? Num(leg.R / s.PipSize, 1) : "", Num(exit, s.Digits),
            leg.Cycles, R(leg.HedgesRealized), leg.BreakEven ? "yes" : "no", leg.R > 0 ? Num(leg.MaxLossR, 2) : "",
            leg.ClosedBy switch { CloseReason.Tm => "TM", CloseReason.StopLoss => "stop-loss", CloseReason.TakeProfit => "take-profit", _ => "owner" },
            R(leg.Dir * (exit - leg.Entry) + leg.HedgesRealized), Num(leg.Commission, 2), Num(leg.Gross - leg.Commission, 2)));
    }

    /// <summary>Called after every saved change of the account.</summary>
    public void Save()
    {
        if (!_dirty && !_state.Trades.Any())
            return;
        File.WriteAllText(_openPath + ".tmp", JsonSerializer.Serialize(_state, Json));
        File.Move(_openPath + ".tmp", _openPath, overwrite: true);
        _dirty = false;
    }

    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Num(double v, int digits) => v.ToString("F" + digits, CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Save();
        _csv.Dispose();
    }
}
