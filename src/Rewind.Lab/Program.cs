// rewind-lab (spec §7.1): Rewind's history and simulated broker without the browser, for Scout Lens in the Trader
// project (Trader docs/bots/scout/scout-lens.md §8 step 1). Two commands; no network, nothing written outside --out.
//
//   rewind-lab candles  [--symbols A,B] [--out ~/rewind/lab/candles]
//       1H, 4H and daily candles per symbol in Trader's candle-file format (<SYMBOL>-H1.csv …), for Scout's --replay.
//   rewind-lab outcomes <orders.csv> [more.csv …] [--out ~/rewind/lab/outcomes.csv]
//       plays the orders Scout's --replay … --orders wrote on the simulated broker; one row per placed order, with its result in R.
//   rewind-lab run --from <date> --to <date> --out <folder> [--bots 2] [--port 5037] [--balance 5000] [--margin-mode Max] [--stop-out 50]
//       the whole-account run (spec §7.2): the real Scout and TM trade one account over all symbols, in lock-step. Listens on localhost only.
//
// Options: --history <folder> (default ~/rewind/history), --config <folder> (default ~/rewind/config).

using System.Diagnostics;
using Rewind.Broker;
using Rewind.History;
using Rewind.Lab;

const string Usage = """
    rewind-lab — Rewind's history and broker for Scout Lens (docs/spec.md §7.1)

      rewind-lab candles  [--symbols A,B,…] [--out <folder>]
      rewind-lab outcomes <orders.csv> [more.csv …] [--out <file.csv>]
      rewind-lab run --from <yyyy-MM-dd> --to <yyyy-MM-dd> --out <folder> [--bots 2] [--port 5037] [--balance 5000] [--margin-mode Max] [--stop-out 50]

      --history <folder>   default ~/rewind/history
      --config <folder>    default ~/rewind/config (else the copies next to the program)
    """;

var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var historyDir = Path.Combine(home, "rewind", "history");
var configDir = Path.Combine(home, "rewind", "config");
string? outPath = null;
string[]? symbols = null;
var files = new List<string>();
DateTime? from = null, to = null;
var (bots, port, balance, marginMode, stopOut) = (2, 5037, 5000.0, "Max", 50.0);
static DateTime Day(string s) => DateTime.SpecifyKind(DateTime.ParseExact(s, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None), DateTimeKind.Utc); // a day, or a moment inside one (spec §7.2)
if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 2 : 0;
}
var command = args[0];
try
{
    for (var i = 1; i < args.Length; i++)
    {
        string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i])
        {
            case "--history": historyDir = Value(); break;
            case "--config": configDir = Value(); break;
            case "--out": outPath = Value(); break;
            case "--symbols": symbols = Value().Split(',', StringSplitOptions.RemoveEmptyEntries); break;
            case "--from": from = Day(Value()); break;
            case "--to": to = Day(Value()); break;
            case "--bots": bots = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--port": port = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--balance": balance = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--margin-mode": marginMode = Value(); break;
            case "--stop-out": stopOut = double.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture); break;
            case var a when a.StartsWith("--"): throw new ArgumentException($"Unknown option {a}");
            default: files.Add(args[i]); break;
        }
    }
    if (command == "run" && (from is null || to is null || outPath is null))
        throw new ArgumentException("run needs --from, --to and --out");
    if (marginMode is not ("Sum" or "Max" or "Net"))
        throw new ArgumentException("--margin-mode must be Sum, Max or Net");
    if (command is not ("candles" or "outcomes" or "run"))
        throw new ArgumentException($"Unknown command {command}");
    if (command == "outcomes" && files.Count == 0)
        throw new ArgumentException("outcomes needs at least one orders file");
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"{ex.Message}\n\n{Usage}");
    return 2;
}

void Log(string message) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {message}");

if (command == "candles")
{
    var dir = outPath ?? Path.Combine(home, "rewind", "lab", "candles");
    Directory.CreateDirectory(dir);
    foreach (var s in SymbolInfo.All.Where(s => symbols is null || symbols.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
    {
        var history = HistoryStore.Load(historyDir, Log, [s.Name]);
        if (!history.TryGetValue(s.Name, out var h))
            continue;
        foreach (var tf in CandleExport.Timeframes)
        {
            var candles = CandleExport.Closed(h, tf);
            CandleExport.Write(Path.Combine(dir, CandleExport.FileName(s.Name, tf)), candles, s.Digits);
            Log($"{s.Name} {tf}: {candles.Count:N0} candles → {Path.Combine(dir, CandleExport.FileName(s.Name, tf))}");
        }
    }
    return 0;
}

if (command == "run")
{
    var runSpecs = ConfigFiles.Symbols(ConfigFiles.Find(configDir, "symbols.json"));
    var runHistory = HistoryStore.Load(historyDir, Log, symbols);
    return AccountRun.Execute(runHistory, runSpecs, ConfigFiles.Rewind(ConfigFiles.Find(configDir, "rewind.json")), new AccountRules(marginMode, stopOut),
        from!.Value, to!.Value, port, bots, outPath!, Path.Combine(home, "rewind", "certs"), balance, Log);
}

// ---- outcomes ---------------------------------------------------------------------------------------------------
var specs = ConfigFiles.Symbols(ConfigFiles.Find(configDir, "symbols.json"));
var config = ConfigFiles.Rewind(ConfigFiles.Find(configDir, "rewind.json"));
string? header = null;
var rows = new List<OrderRow>();
foreach (var f in files)
{
    var (h, r) = Outcomes.Read(f);
    if (header is not null && h != header)
    {
        Console.Error.WriteLine($"{f}: columns differ from the first file's");
        return 2;
    }
    header = h;
    rows.AddRange(r);
}
var all = new List<Outcome>();
var clock = Stopwatch.StartNew();
foreach (var group in rows.GroupBy(r => r.Symbol).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    var history = HistoryStore.Load(historyDir, Log, [group.Key]);
    if (!history.TryGetValue(group.Key, out var h) || !specs.TryGetValue(group.Key, out var spec))
    {
        Log($"{group.Key}: no history or no symbol details; its {group.Count()} rows are left out");
        continue;
    }
    var outcomes = new Outcomes(h, spec, config).Run([.. group.OrderBy(r => r.TimeUtc)]);
    all.AddRange(outcomes);
    Log(Line(group.Key, outcomes));
}
var outFile = outPath ?? Path.Combine(home, "rewind", "lab", "outcomes.csv");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
Outcomes.Write(outFile, header!, all.OrderBy(o => o.Place.TimeUtc).ThenBy(o => o.Place.Key, StringComparer.Ordinal));
Log(Line("All", all));
Log($"{all.Count:N0} orders → {outFile} in {clock.Elapsed.TotalSeconds:0} s");
return 0;

static string Line(string what, IReadOnlyCollection<Outcome> o)
{
    var traded = o.Where(x => x.R is not null).ToList();
    var r = traded.Sum(x => x.R!.Value);
    var counts = string.Join(", ", o.GroupBy(x => x.Result).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
    return $"{what}: {o.Count} orders ({counts}); {traded.Count} traded, total {r:+0.0;-0.0}R, " +
           $"{(traded.Count > 0 ? r / traded.Count : 0):+0.00;-0.00}R per trade, {(traded.Count > 0 ? 100.0 * traded.Count(x => x.R > 0) / traded.Count : 0):0}% won";
}
