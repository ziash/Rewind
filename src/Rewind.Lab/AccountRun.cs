using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rewind.Broker;
using Rewind.History;
using Rewind.OpenApi;
using Rewind.Replay;

namespace Rewind.Lab;

/// <summary>The whole-account run (spec §7.2): the real bots trade one simulated account over all symbols, taking turns with the replay.</summary>
public static class AccountRun
{
    public const string EquityHeader = "time_utc,balance_usd,equity_usd,margin_usd,positions,orders";

    public static int Execute(IReadOnlyDictionary<string, SymbolHistory> history, IReadOnlyDictionary<string, SymbolSpec> specs, RewindConfig config, AccountRules rules,
        DateTime from, DateTime to, int port, int bots, string outDir, string certsDir, double startBalance, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var broker = new SimBroker(history, specs, config) { Rules = rules };
        broker.Open(outDir, from, startBalance, keepAccountFile: false);
        var clock = new ReplayClock(history, path: null, broker, lookback: TimeSpan.Zero);
        var certificate = Certificates.Ensure(certsDir, log);
        var credentials = TmFiles.Ensure(Path.Combine(outDir, "bots"), port, Certificates.CaFile(certsDir), specs.Keys.Where(history.ContainsKey), log);
        var server = new OpenApiServer(port, certificate, credentials, broker, clock, history, log, lockStep: true);
        using var stop = new CancellationTokenSource();
        var listening = Task.Run(() => server.RunAsync(stop.Token));

        var waited = Stopwatch.StartNew();
        while (server.Steppers < bots)
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(15))
                throw new TimeoutException($"Only {server.Steppers} of {bots} bots joined");
            Thread.Sleep(100);
        }

        using var equity = new StreamWriter(Path.Combine(outDir, "equity.csv"), append: false, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        equity.WriteLine(EquityHeader);
        var nextEquity = from;
        var nextLog = from;
        long turns = 0;
        var running = Stopwatch.StartNew();
        log($"Run from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}: {history.Count} symbols, margin {rules.MarginMode}, stop-out {rules.StopOutLevel} %");
        broker.Walk(from, to, (t, step) =>
        {
            if (step == 3 || broker.AnyInTrade)
            {
                server.Turn(t);
                turns++;
            }
            if (t >= nextEquity)
            {
                var m = broker.Money();
                equity.WriteLine(string.Join(',', t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Usd(m.Balance), Usd(m.Equity), Usd(m.Margin), m.Positions, m.Orders));
                nextEquity = t.AddHours(1);
            }
            if (t >= nextLog)
            {
                var m = broker.Money();
                log($"{t:yyyy-MM-dd}: balance {Usd(m.Balance)}, equity {Usd(m.Equity)}, {m.Positions} positions, {m.Orders} orders; {turns:N0} turns in {running.Elapsed.TotalMinutes:0.0} min");
                nextLog = t.AddDays(7);
            }
        });
        var end = broker.Money();
        log($"Finished: balance {Usd(end.Balance)}, equity {Usd(end.Equity)}, {end.Positions} positions and {end.Orders} orders still open; {turns:N0} turns in {running.Elapsed.TotalMinutes:0.0} min");
        stop.Cancel();
        return 0;

        static string Usd(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
