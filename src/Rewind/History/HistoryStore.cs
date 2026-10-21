using System.Globalization;

namespace Rewind.History;

/// <summary>One 5-minute candle of the history files (spec §4): bid and ask prices.</summary>
public readonly record struct Bar(DateTime TimeUtc, double BidOpen, double BidHigh, double BidLow, double BidClose,
    double AskOpen, double AskHigh, double AskLow, double AskClose)
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(5);
    public DateTime EndUtc => TimeUtc + Length;
}

/// <summary>All 5-minute candles of one symbol, oldest first.</summary>
public sealed class SymbolHistory(SymbolInfo symbol, Bar[] bars)
{
    public SymbolInfo Symbol { get; } = symbol;
    public Bar[] Bars { get; } = bars;
    public DateTime FirstUtc => Bars[0].TimeUtc;
    public DateTime EndUtc => Bars[^1].EndUtc;

    /// <summary>Number of candles that have finished by <paramref name="cursorUtc"/> (the ones the owner may see).</summary>
    public int RevealedCount(DateTime cursorUtc)
    {
        int lo = 0, hi = Bars.Length; // first index whose end is after the cursor
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Bars[mid].EndUtc <= cursorUtc)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

/// <summary>Loads ~/rewind/history/&lt;SYMBOL&gt;/*.m5.csv (written by rewind-download) into memory.</summary>
public static class HistoryStore
{
    public static Dictionary<string, SymbolHistory> Load(string dir, Action<string> log)
    {
        var result = new Dictionary<string, SymbolHistory>();
        foreach (var s in SymbolInfo.All)
        {
            var folder = Path.Combine(dir, s.Name);
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.m5.csv").Order().ToList() : [];
            var bars = files.SelectMany(ReadFile).ToArray();
            if (bars.Length == 0)
            {
                log($"{s.Name}: no history in {folder} (run rewind-download); not shown");
                continue;
            }
            result[s.Name] = new SymbolHistory(s, bars);
            log($"{s.Name}: {bars.Length:N0} 5-min candles, {bars[0].TimeUtc:yyyy-MM-dd} → {bars[^1].TimeUtc:yyyy-MM-dd HH:mm}");
        }
        return result;
    }

    internal static IEnumerable<Bar> ReadFile(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 9)
                continue;
            double P(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            yield return new Bar(
                DateTime.SpecifyKind(DateTime.ParseExact(f[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc),
                P(1), P(2), P(3), P(4), P(5), P(6), P(7), P(8));
        }
    }
}
