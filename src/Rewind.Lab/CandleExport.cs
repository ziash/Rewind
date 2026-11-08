using System.Globalization;
using System.Text;
using Rewind.History;

namespace Rewind.Lab;

/// <summary>
/// Writes a symbol's 1H, 4H and daily candles (bid prices, built from the 5-minute history exactly as the chart builds
/// them, spec §5) in the candle-file format of Trader's Scout (<c>--candles</c> / <c>--replay</c>): time_utc,open,high,low,close.
/// The last candle of each timeframe is left out: the history may end before it closes.
/// </summary>
public static class CandleExport
{
    public static readonly Timeframe[] Timeframes = [Timeframe.H1, Timeframe.H4, Timeframe.D1];

    public static List<ChartCandle> Closed(SymbolHistory h, Timeframe tf)
    {
        var all = History.Timeframes.Candles(h, tf, h.EndUtc, DateTime.MaxValue, h.Bars.Length);
        if (all.Count > 0)
            all.RemoveAt(all.Count - 1);
        return all;
    }

    /// <summary>File name Trader reads: &lt;SYMBOL&gt;-&lt;TF&gt;.csv, e.g. EURUSD-H1.csv.</summary>
    public static string FileName(string symbol, Timeframe tf) => $"{symbol}-{tf}.csv";

    public static void Write(string path, IEnumerable<ChartCandle> candles, int digits)
    {
        string P(double v) => v.ToString("F" + digits, CultureInfo.InvariantCulture);
        var tmp = path + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine("time_utc,open,high,low,close");
            foreach (var c in candles)
                w.WriteLine($"{c.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)},{P(c.Open)},{P(c.High)},{P(c.Low)},{P(c.Close)}");
        }
        File.Move(tmp, path, overwrite: true);
    }
}
