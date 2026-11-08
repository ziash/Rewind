using System.Globalization;
using System.Text;
using Rewind.Broker;
using Rewind.History;

namespace Rewind.Lab;

/// <summary>One row of the orders file Trader's Scout writes (<c>--replay … --orders</c>, Trader scout-lens.md §8 step 1).</summary>
public sealed record OrderRow(DateTime TimeUtc, string Key, string Symbol, string Action, int Dir, double Price, double? StopLoss,
    DateTime? ExpiresUtc, string Reason, string[] Fields)
{
    /// <summary>The other order of the same pause: the key ends in M (main) or R (reversal).</summary>
    public string Sibling => Key[..^1] + (Key[^1] == 'M' ? "R" : "M");
}

/// <summary>What became of one placed order, played out on the simulated broker.</summary>
public sealed class Outcome(OrderRow place)
{
    public OrderRow Place { get; } = place;
    /// <summary>filled · expired · cancelled · refused · skipped · no_stop_loss · pending_at_end · open_at_end.</summary>
    public string Result { get; set; } = "pending_at_end";
    public string Detail { get; set; } = "";
    public long OrderId { get; set; }
    public DateTime? FillUtc { get; set; }
    public double? Entry { get; set; }
    public double? InitialStopLoss { get; set; }
    public DateTime? ExitUtc { get; set; }
    public double? Exit { get; set; }
    public string ExitReason { get; set; } = "";
    public double Best { get; set; }   // furthest in favour, in price (from the entry, on the side it closes at)
    public double Worst { get; set; }  // furthest against

    public double? Risk => Entry is { } e && InitialStopLoss is { } sl ? Math.Abs(e - sl) : null;
    public double? R => Exit is { } x && Entry is { } e && Risk is { } r and > 0 ? Place.Dir * (x - e) / r : null;
    public double? BestR => Risk is { } r and > 0 && Entry is not null ? Best / r : null;
    public double? WorstR => Risk is { } r and > 0 && Entry is not null ? Worst / r : null;
}

/// <summary>
/// Plays Scout's orders on Rewind's simulated broker (spec §7) to give each one its result in R (spec §7.1, Trader
/// scout-lens.md §8 step 1). Rules of Scout's Trade mode (Trader scout.md §2.3): a Buy Stop / Sell Stop at the level,
/// stop-loss at the far side of the pause with trailing on, at the smallest volume; refused where the broker refuses
/// (e.g. the price is already past the level, the market is closed) and not retried; when one order of a pause fills the
/// other is cancelled at once (one cancels the other), and an order whose sibling already filled is not placed; an
/// unfilled order is cancelled when Scout cancels it (the setup ended) or when it expires. After the last order the
/// history is played on until every position has closed. TM is not simulated: R is that of Scout's own order.
/// </summary>
public sealed class Outcomes
{
    private readonly SimBroker _broker;
    private readonly SymbolSpec _spec;
    private readonly Dictionary<string, Outcome> _byKey = [];
    private readonly Dictionary<long, Outcome> _byOrder = [];
    private readonly Dictionary<long, Outcome> _byPosition = [];
    private readonly List<Outcome> _pending = [];
    private readonly List<Outcome> _open = [];
    private readonly List<Outcome> _filledNow = [];
    private readonly SymbolHistory _history;
    private DateTime _cursor;

    public List<Outcome> Results { get; } = [];

    public Outcomes(SymbolHistory history, SymbolSpec spec, RewindConfig config)
    {
        _history = history;
        _spec = spec;
        _broker = new SimBroker(new Dictionary<string, SymbolHistory> { [history.Symbol.Name] = history },
            new Dictionary<string, SymbolSpec> { [spec.Name] = spec }, config);
        _broker.Event += OnEvent;
    }

    /// <summary>Plays the rows of one symbol (oldest first) and returns one outcome per placed order, in order.</summary>
    public List<Outcome> Run(IReadOnlyList<OrderRow> rows)
    {
        if (rows.Count == 0)
            return Results;
        _cursor = rows[0].TimeUtc < _history.FirstUtc ? _history.FirstUtc : rows[0].TimeUtc;
        _broker.Open(null, _cursor);
        foreach (var row in rows)
        {
            if (row.TimeUtc > _history.EndUtc)
                break;
            AdvanceTo(row.TimeUtc);
            if (row.Action == "place")
                Place(row);
            else if (row.Action == "cancel" && _byKey.TryGetValue(row.Key, out var o) && _pending.Contains(o))
                Cancel(o, "cancelled", row.Reason);
        }
        // Play on until every order and position has ended, a week at a time (the history may end first).
        while ((_pending.Count > 0 || _open.Count > 0) && _cursor < _history.EndUtc)
            AdvanceTo(Min(_cursor.AddDays(7), _history.EndUtc));
        foreach (var o in _open)
        {
            o.Result = "open_at_end";
            var q = _broker.QuoteOf(_spec.Name);
            (o.Exit, o.ExitUtc, o.ExitReason) = (o.Place.Dir > 0 ? q.Bid : q.Ask, q.TimeUtc, "history ended");
        }
        return Results;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Moves the replay to <paramref name="to"/>, stopping at each expiry on the way (spec: an order expired over a
    /// weekend must not fill on Monday's gap).</summary>
    private void AdvanceTo(DateTime to)
    {
        while (_pending.Where(o => o.Place.ExpiresUtc is { } x && x <= to).MinBy(o => o.Place.ExpiresUtc) is { } next)
        {
            var at = next.Place.ExpiresUtc!.Value;
            if (at > _cursor)
            {
                _broker.AdvanceTo(_cursor, at, AfterStep);
                _cursor = at;
            }
            foreach (var o in _pending.Where(o => o.Place.ExpiresUtc <= at).ToList())
                Cancel(o, "expired", "");
        }
        if (to > _cursor)
        {
            _broker.AdvanceTo(_cursor, to, AfterStep);
            _cursor = to;
        }
    }

    private void Place(OrderRow row)
    {
        if (_byKey.ContainsKey(row.Key))
            return; // Scout places each role once, ever
        var o = new Outcome(row);
        _byKey[row.Key] = o;
        Results.Add(o);
        if (_byKey.TryGetValue(row.Sibling, out var sibling) && sibling.FillUtc is not null)
        {
            (o.Result, o.Detail) = ("skipped", "the other order of the pair filled");
            return;
        }
        if (row.StopLoss is null)
        {
            (o.Result, o.Detail) = ("no_stop_loss", "far side on the wrong side of the level");
            return;
        }
        try
        {
            var order = _broker.PlaceOrder(OrderType.Stop, row.Symbol, row.Dir, _spec.MinVolume, row.Price, row.StopLoss, null, trailing: true, label: row.Key);
            o.OrderId = order.Id;
            _byOrder[order.Id] = o;
            _pending.Add(o);
        }
        catch (BrokerException ex)
        {
            (o.Result, o.Detail) = ("refused", $"{ex.Code}: {ex.Message}");
        }
    }

    private void Cancel(Outcome o, string result, string detail)
    {
        _broker.CancelOrder(o.OrderId);
        _pending.Remove(o);
        (o.Result, o.Detail) = (result, detail);
    }

    // Inside the broker's lock: only bookkeeping here; the other order of the pair is cancelled after the step.
    private void OnEvent(BrokerEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.OrderFilled when e.OrderId is { } id && _byOrder.TryGetValue(id, out var o):
                _pending.Remove(o);
                (o.Result, o.FillUtc) = ("filled", e.TimeUtc);
                _filledNow.Add(o);
                break;
            case EventKind.PositionOpened when e.OrderId is { } id && _byOrder.TryGetValue(id, out var o) && e.Position is { } p:
                (o.Entry, o.InitialStopLoss) = (p.Entry, p.InitialStopLoss);
                _byPosition[p.Id] = o;
                _open.Add(o);
                break;
            case EventKind.PositionClosed when e.PositionId is { } pid && _byPosition.TryGetValue(pid, out var o):
                (o.Exit, o.ExitUtc) = (e.Price, e.TimeUtc);
                o.ExitReason = e.Reason == CloseReason.StopLoss && e.Position?.StopLoss is { } sl && o.InitialStopLoss is { } isl && sl != isl
                    ? "trailing stop" : e.Reason?.ToString() ?? "closed";
                Track(o, e.Price);
                _open.Remove(o);
                break;
        }
    }

    private void AfterStep(Replay.PriceStep step)
    {
        foreach (var o in _open)
            Track(o, o.Place.Dir > 0 ? step.Bid : step.Ask);
        foreach (var o in _filledNow)
            if (_byKey.TryGetValue(o.Place.Sibling, out var sibling) && _pending.Contains(sibling))
                Cancel(sibling, "cancelled", "the other order of the pair filled");
        _filledNow.Clear();
    }

    private static void Track(Outcome o, double exitSidePrice)
    {
        if (o.Entry is not { } e)
            return;
        var move = o.Place.Dir * (exitSidePrice - e);
        o.Best = Math.Max(o.Best, move);
        o.Worst = Math.Min(o.Worst, move);
    }

    // ---- files ------------------------------------------------------------------------------------------------

    public static (string Header, List<OrderRow> Rows) Read(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = lines[0];
        var cols = header.Split(',');
        int Col(string name) => Array.IndexOf(cols, name) is var i and >= 0 ? i : throw new FormatException($"{path}: no column {name}");
        int time = Col("time_utc"), key = Col("setup_id"), sym = Col("symbol"), act = Col("action"), side = Col("side"), price = Col("price"),
            sl = Col("stop_loss"), exp = Col("expires_utc"), reason = Col("reason");
        DateTime T(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        double? N(string s) => s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);
        var rows = new List<OrderRow>();
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var f = Split(line);
            rows.Add(new OrderRow(T(f[time]), f[key], f[sym], f[act], f[side] == "Buy" ? 1 : f[side] == "Sell" ? -1 : 0, N(f[price]) ?? 0, N(f[sl]),
                f[exp].Length == 0 ? null : T(f[exp]), f[reason], f));
        }
        return (header, rows);
    }

    /// <summary>A CSV line into fields (Trader CSV rules: a field is quoted only when it holds a comma or quote).</summary>
    public static string[] Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }

    public const string OutcomeColumns = "result,result_detail,fill_utc,entry,initial_stop_loss,exit_utc,exit,exit_reason,r,best_r,worst_r,hours_held";

    /// <summary>The place rows of the orders file with the outcome columns added (Trader CSV rules).</summary>
    public static void Write(string path, string ordersHeader, IEnumerable<Outcome> outcomes)
    {
        string Esc(string v) => v.AsSpan().IndexOfAny(",\"\r\n") < 0 ? v : "\"" + v.Replace("\"", "\"\"") + "\"";
        string Time(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
        string Num(double? v, int d) => v is { } x && double.IsFinite(x) ? (Math.Round(x, d) + 0.0).ToString("F" + d, CultureInfo.InvariantCulture) : "";
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine($"{ordersHeader},{OutcomeColumns}");
            foreach (var o in outcomes)
            {
                var held = o.FillUtc is { } f && o.ExitUtc is { } x ? (x - f).TotalHours : (double?)null;
                w.WriteLine(string.Join(',', o.Place.Fields.Select(Esc).Concat(
                [
                    o.Result, Esc(o.Detail), Time(o.FillUtc), Num(o.Entry, 6), Num(o.InitialStopLoss, 6), Time(o.ExitUtc), Num(o.Exit, 6), o.ExitReason,
                    Num(o.R, 3), Num(o.BestR, 3), Num(o.WorstR, 3), Num(held, 2),
                ])));
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
