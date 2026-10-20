// rewind-download (spec §4): downloads Dukascopy's free 1-minute bid and ask candles for the six symbols into a raw
// cache, then writes one CSV pair per symbol and month: <yyyy-MM>.m1.csv.gz (1-minute) and <yyyy-MM>.m5.csv.
// Polite: one request at a time; files already in the cache are never fetched again, so a re-run resumes.
//
//   rewind-download                         24 months up to yesterday (UTC), all six symbols
//   rewind-download --from 2026-09-01 --to 2026-09-30 --symbols EURUSD,BTCUSD
//   rewind-download --build-only            rebuild the CSV files from the cache, no download
//
// Options: --dir <folder> (default ~/rewind/history), --interval-ms <ms> between requests (default 1000).

using System.Diagnostics;
using System.Globalization;
using Rewind.Download;

const string Usage = """
    rewind-download — Dukascopy history for Rewind (docs/spec.md §4)

      --from <yyyy-MM-dd>     first day (default: 1st of the month 24 months ago)
      --to <yyyy-MM-dd>       last day (default: yesterday, UTC; today is never complete)
      --symbols <A,B,…>       default: AUDJPY,EURGBP,EURUSD,GBPJPY,BTCUSD,ETHUSD
      --dir <folder>          default: ~/rewind/history
      --interval-ms <ms>      pause between requests (default 1000)
      --build-only            only rebuild the CSV files from the downloaded files
      --help
    """;

var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
var thisMonth = new DateOnly(yesterday.Year, yesterday.Month, 1);
var from = thisMonth.AddMonths(-24);
var to = yesterday;
var instruments = Instrument.All;
var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "rewind", "history");
var interval = TimeSpan.FromSeconds(1);
var buildOnly = false;

try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i])
        {
            case "--from": from = DateOnly.ParseExact(Value(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "--to": to = DateOnly.ParseExact(Value(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "--symbols": instruments = Value().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Instrument.Find).ToList(); break;
            case "--dir": dir = Value(); break;
            case "--interval-ms": interval = TimeSpan.FromMilliseconds(int.Parse(Value(), CultureInfo.InvariantCulture)); break;
            case "--build-only": buildOnly = true; break;
            case "--help" or "-h": Console.WriteLine(Usage); return 0;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
    if (to > yesterday)
        throw new ArgumentException($"--to must be {yesterday:yyyy-MM-dd} or earlier: a day is complete only after 00:00 UTC");
    if (from > to)
        throw new ArgumentException("--from is after --to");
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{ex.Message}\n\n{Usage}");
    return 2;
}

void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log("Stopping after the current file (Ctrl+C); a re-run continues where it stopped");
    stop.Cancel();
};

var cacheDir = Path.Combine(dir, "raw");
using var feed = new Feed(cacheDir, interval, Log);
var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
Log($"History {from:yyyy-MM-dd} → {to:yyyy-MM-dd}, {string.Join(", ", instruments.Select(i => i.Name))} → {dir}");

if (!buildOnly)
{
    var todo = (from i in instruments
                from d in days
                where i.Trades(d)
                from s in new[] { Side.Bid, Side.Ask }
                where !feed.Cached(i, d, s)
                select (i, d, s)).ToList();
    Log($"{todo.Count} file(s) to download, one every {interval.TotalSeconds:0.#} s or slower (about {todo.Count * interval.TotalMinutes / 60:0.#} h at best)");

    var clock = Stopwatch.StartNew();
    var failed = 0;
    for (var n = 0; n < todo.Count && !stop.IsCancellationRequested; n++)
    {
        var (i, d, s) = todo[n];
        try
        {
            if (!await feed.FetchAsync(i, d, s, stop.Token))
                failed++;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            break;
        }
        if ((n + 1) % 100 == 0 || n + 1 == todo.Count)
        {
            var left = TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds / (n + 1) * (todo.Count - n - 1));
            Log($"{n + 1}/{todo.Count} ({i.Name} {d:yyyy-MM-dd}), {feed.Bytes / 1e6:0.0} MB, about {left.TotalHours:0.0} h left");
        }
    }
    Log($"Downloaded {feed.Fetched} file(s), {feed.Bytes / 1e6:0.0} MB in {clock.Elapsed.TotalMinutes:0} min; {failed} not fetched now");
    if (stop.IsCancellationRequested)
        return 0;
}

// ---- build the monthly CSV files and the summary ------------------------------------------------------------
var problems = 0;
foreach (var i in instruments)
{
    var outDir = Path.Combine(dir, i.Name);
    Directory.CreateDirectory(outDir);
    var all = new List<Candle>();
    var missing = new List<DateOnly>();
    foreach (var month in days.GroupBy(d => new DateOnly(d.Year, d.Month, 1)))
    {
        var m1 = new List<Candle>();
        foreach (var d in month.Where(i.Trades))
        {
            string bidPath = feed.CachePath(i, d, Side.Bid), askPath = feed.CachePath(i, d, Side.Ask);
            if (!File.Exists(bidPath) || !File.Exists(askPath))
            {
                missing.Add(d);
                continue;
            }
            try
            {
                m1.AddRange(Candles.Combine(Bi5.Read(File.ReadAllBytes(bidPath), d), Bi5.Read(File.ReadAllBytes(askPath), d)));
            }
            catch (Bi5Exception ex)
            {
                Log($"{i.Name} {d:yyyy-MM-dd}: {ex.Message}; delete its files in {cacheDir} and re-run");
                problems++;
            }
        }
        Candles.Write(Path.Combine(outDir, $"{month.Key:yyyy-MM}.m1.csv.gz"), m1, i);
        Candles.Write(Path.Combine(outDir, $"{month.Key:yyyy-MM}.m5.csv"), Candles.Aggregate(m1, 5), i);
        all.AddRange(m1);
    }
    Log(Summary.Of(i, all, missing));
    problems += missing.Count;
}
Log(problems == 0 ? "Done: all days complete" : $"Done with {problems} missing or bad day(s); re-run to fetch them");
return problems == 0 ? 0 : 1;
