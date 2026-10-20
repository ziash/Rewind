using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Rewind.Download;

/// <summary>One candle with both sides, prices as whole numbers (price × factor). Volume is the bid side's.</summary>
internal readonly record struct Candle(
    DateTime TimeUtc,
    long BidOpen, long BidHigh, long BidLow, long BidClose,
    long AskOpen, long AskHigh, long AskLow, long AskClose,
    double Volume);

/// <summary>Builds the history files of spec §4 from the bid and ask minute bars.</summary>
internal static class Candles
{
    public const string Header = "time_utc,bid_open,bid_high,bid_low,bid_close,ask_open,ask_high,ask_low,ask_close,volume";

    /// <summary>
    /// Pairs bid and ask minutes by time. A minute is kept when either side traded (volume above 0); minutes without
    /// any trading (forex nights at the weekend, quiet minutes) are dropped and stay gaps.
    /// </summary>
    public static List<Candle> Combine(IEnumerable<MinuteBar> bid, IEnumerable<MinuteBar> ask)
    {
        var asks = ask.ToDictionary(a => a.TimeUtc);
        var result = new List<Candle>();
        foreach (var b in bid.OrderBy(b => b.TimeUtc))
        {
            if (!asks.TryGetValue(b.TimeUtc, out var a) || (b.Volume <= 0 && a.Volume <= 0))
                continue;
            result.Add(new Candle(b.TimeUtc, b.Open, b.High, b.Low, b.Close, a.Open, a.High, a.Low, a.Close, b.Volume));
        }
        return result;
    }

    /// <summary>Groups minute candles into candles of <paramref name="minutes"/> minutes on the UTC clock (00:00, 00:05 …).</summary>
    public static List<Candle> Aggregate(IReadOnlyList<Candle> m1, int minutes)
    {
        var step = TimeSpan.FromMinutes(minutes).Ticks;
        return m1.GroupBy(c => c.TimeUtc.Ticks / step)
            .Select(g =>
            {
                var first = g.First();
                var last = g.Last();
                return new Candle(new DateTime(g.Key * step, DateTimeKind.Utc),
                    first.BidOpen, g.Max(c => c.BidHigh), g.Min(c => c.BidLow), last.BidClose,
                    first.AskOpen, g.Max(c => c.AskHigh), g.Min(c => c.AskLow), last.AskClose,
                    g.Sum(c => c.Volume));
            })
            .ToList();
    }

    /// <summary>Writes a CSV file (temp file, then rename); gzip-compressed when the name ends in .gz.</summary>
    public static void Write(string path, IEnumerable<Candle> candles, Instrument instrument)
    {
        var tmp = path + ".tmp";
        using (var file = File.Create(tmp))
        using (var stream = path.EndsWith(".gz") ? new GZipStream(file, CompressionLevel.SmallestSize) : (Stream)file)
        using (var w = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            w.NewLine = "\n";
            w.WriteLine(Header);
            foreach (var c in candles)
                w.WriteLine(Line(c, instrument));
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static string Line(Candle c, Instrument i)
    {
        string P(long v) => (v / (decimal)i.Factor).ToString("F" + i.Digits, CultureInfo.InvariantCulture);
        return string.Join(',',
            c.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            P(c.BidOpen), P(c.BidHigh), P(c.BidLow), P(c.BidClose),
            P(c.AskOpen), P(c.AskHigh), P(c.AskLow), P(c.AskClose),
            c.Volume.ToString("0.######", CultureInfo.InvariantCulture));
    }
}
