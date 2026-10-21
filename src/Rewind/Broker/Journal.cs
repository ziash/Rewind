using System.Globalization;
using System.Text;

namespace Rewind.Broker;

/// <summary>
/// The session's CSV files (spec §8), appended as things happen: events.csv (every broker event) and trades.csv (one row
/// per closed position or closed part). Same CSV rules as the Trader project: UTF-8, UTC times, '.' decimals.
/// </summary>
public sealed class Journal(string dir) : IDisposable
{
    public const string EventsHeader = "time_utc,event,symbol,position_id,order_id,side,lots,price,label,detail";
    public const string TradesHeader =
        "open_time_utc,close_time_utc,symbol,side,lots,entry,exit,initial_sl,pips,r_multiple,commission_usd,pnl_usd,close_reason,position_id,label";

    private readonly StreamWriter _events = Open(Path.Combine(dir, "events.csv"), EventsHeader);
    private readonly StreamWriter _trades = Open(Path.Combine(dir, "trades.csv"), TradesHeader);

    private static StreamWriter Open(string path, string header)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        if (isNew)
            w.WriteLine(header);
        return w;
    }

    public void Event(BrokerEvent e, SymbolSpec s) =>
        _events.WriteLine(string.Join(',', Time(e.TimeUtc), e.Kind, e.Symbol, e.PositionId, e.OrderId, SimBroker.Side(e.Dir),
            Num(s.Lots(e.Volume), 2), Num(e.Price, s.Digits), Text(e.Label), Text(e.Detail)));

    /// <param name="pnl">Profit in USD before commission.</param>
    public void Trade(Position p, SymbolSpec s, long volume, double exit, DateTime closeUtc, CloseReason reason, double pnl, double commission)
    {
        var move = p.Dir * (exit - p.Entry);
        double? r = p.InitialStopLoss is { } sl && Math.Abs(p.Entry - sl) > 0 ? move / Math.Abs(p.Entry - sl) : null;
        _trades.WriteLine(string.Join(',', Time(p.OpenUtc), Time(closeUtc), p.Symbol, SimBroker.Side(p.Dir), Num(s.Lots(volume), 2),
            Num(p.Entry, s.Digits), Num(exit, s.Digits), p.InitialStopLoss is { } i ? Num(i, s.Digits) : "", Num(move / s.PipSize, 1),
            r is { } rr ? Num(rr, 2) : "", Num(commission, 2), Num(pnl - commission, 2), reason, p.Id, Text(p.Label)));
    }

    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Num(double v, int digits) => v.ToString("F" + digits, CultureInfo.InvariantCulture);
    private static string Text(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    public void Dispose()
    {
        _events.Dispose();
        _trades.Dispose();
    }
}
