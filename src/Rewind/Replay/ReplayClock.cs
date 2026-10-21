using System.Text.Json;
using Rewind.History;

namespace Rewind.Replay;

/// <summary>What the browser shows and where the replay stands; saved in sessions/default/replay.json (spec §5, §6, §8).</summary>
public sealed record ReplayState(DateTime CursorUtc, string Symbol, Timeframe Timeframe, int Speed, bool Playing = false);

/// <summary>Where a skip jumps to (spec §5): next trading day (17:00 New York), next London or New York open (08:00 local, weekdays).</summary>
public enum SkipTarget { Day, London, NewYork }

/// <summary>
/// The replay cursor (spec §6): the moment in the past the replay is at. Everything after it is hidden. All symbols share
/// it; stepping moves to the end of the next 5-minute candle of the symbol on screen, so a forex weekend is one step.
/// Thread-safe: commands come from web requests, the play loop runs on its own.
/// </summary>
public sealed class ReplayClock
{
    public static readonly int[] Speeds = [1, 2, 5, 10, 30, 60]; // 5-minute candles per second while playing

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly IReadOnlyDictionary<string, SymbolHistory> _history;
    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new(); // one write at a time: two quick clicks must not race on the temp file
    private ReplayState _state;

    /// <summary>Called after every change, with true when the browser must reload its candles (a jump, a new view).</summary>
    public event Action<ReplayState, bool>? Changed;

    public ReplayClock(IReadOnlyDictionary<string, SymbolHistory> history, string? path)
    {
        _history = history;
        _path = path;
        MinUtc = history.Values.Min(h => h.FirstUtc).AddDays(7);
        MaxUtc = history.Values.Max(h => h.EndUtc);
        var first = history.Keys.Contains("EURUSD") ? "EURUSD" : history.Keys.First();
        _state = Load(path) is { } saved && history.ContainsKey(saved.Symbol)
            ? saved with { Playing = false, CursorUtc = Clamp(saved.CursorUtc) }
            : new ReplayState(Clamp(MinUtc.AddDays(83)), first, Timeframe.M5, Speeds[2]);
    }

    /// <summary>Earliest cursor: a week after the first data, so there is something to look back on.</summary>
    public DateTime MinUtc { get; }
    public DateTime MaxUtc { get; }

    public ReplayState State
    {
        get { lock (_lock) return _state; }
    }

    public void SetView(string symbol, Timeframe tf) => Update(s =>
        _history.ContainsKey(symbol) ? s with { Symbol = symbol, Timeframe = tf } : throw new ArgumentException($"No history for {symbol}"), reload: true);

    /// <summary>One 5-minute candle of the symbol on screen. Returns false at the end of the history.</summary>
    public bool Step()
    {
        var moved = false;
        Update(s =>
        {
            var h = _history[s.Symbol];
            var next = h.RevealedCount(s.CursorUtc);
            if (next >= h.Bars.Length)
                return s with { Playing = false };
            moved = true;
            return s with { CursorUtc = h.Bars[next].EndUtc };
        }, reload: false);
        return moved;
    }

    public void GoTo(DateTime utc) => Update(s => s with { CursorUtc = Clamp(utc), Playing = false }, reload: true);

    public void Skip(SkipTarget target) => Update(s => s with { CursorUtc = Clamp(Next(target, s.CursorUtc)) }, reload: true);

    public void Play(int speed) => Update(s => s with { Playing = true, Speed = Speeds.Contains(speed) ? speed : s.Speed }, reload: false);

    public void Pause() => Update(s => s with { Playing = false }, reload: false);

    /// <summary>Runs while the app runs: steps at the chosen speed whenever playing.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var steps = 0;
        while (!ct.IsCancellationRequested)
        {
            var s = State;
            if (!s.Playing)
            {
                await Task.Delay(50, ct);
                continue;
            }
            Step();
            if (++steps % 100 == 0)
                Save(State);
            await Task.Delay(1000 / s.Speed, ct);
        }
    }

    /// <summary>The next skip target strictly after <paramref name="utc"/>.</summary>
    public static DateTime Next(SkipTarget target, DateTime utc)
    {
        var (zone, hour) = target switch
        {
            SkipTarget.Day => (NewYork, 17),
            SkipTarget.London => (London, 8),
            _ => (NewYork, 8),
        };
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        for (var day = local.Date; ; day = day.AddDays(1))
        {
            if (target != SkipTarget.Day && day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            var at = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddHours(hour), DateTimeKind.Unspecified), zone);
            if (at > utc)
                return at;
        }
    }

    private DateTime Clamp(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        utc = new DateTime(utc.Ticks / Bar.Length.Ticks * Bar.Length.Ticks, DateTimeKind.Utc);
        return utc < MinUtc ? MinUtc : utc > MaxUtc ? MaxUtc : utc;
    }

    private void Update(Func<ReplayState, ReplayState> change, bool reload)
    {
        ReplayState s;
        lock (_lock)
        {
            s = _state = change(_state);
        }
        if (reload || !s.Playing)
            Save(s);
        Changed?.Invoke(s, reload);
    }

    private void Save(ReplayState s)
    {
        if (_path is null)
            return;
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(s with { Playing = false }, Json));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }

    private static ReplayState? Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonSerializer.Deserialize<ReplayState>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null; // a damaged file: start fresh
        }
    }
}
