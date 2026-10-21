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
    private string? _path;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new(); // one write at a time: two quick clicks must not race on the temp file
    private ReplayState _state;

    /// <summary>Called after every change, with true when the browser must reload its candles (a jump, a new view).</summary>
    public event Action<ReplayState, bool>? Changed;

    /// <param name="lookback">History kept before the earliest cursor (default a week, so there is something to look back on).</param>
    public ReplayClock(IReadOnlyDictionary<string, SymbolHistory> history, string? path, IMarket? market = null, TimeSpan? lookback = null)
    {
        _history = history;
        Market = market;
        MinUtc = history.Values.Min(h => h.FirstUtc) + (lookback ?? TimeSpan.FromDays(7));
        MaxUtc = history.Values.Max(h => h.EndUtc);
        _state = Initial(path, null);
        _path = path;
    }

    /// <summary>The broker that walks the prices as the cursor moves (step 3); none in the chart-only tests.</summary>
    public IMarket? Market { get; }

    /// <summary>Switches to another session's replay.json (spec §8); <paramref name="startUtc"/> for a new session.</summary>
    public void Open(string path, DateTime? startUtc = null)
    {
        lock (_lock)
        {
            _state = Initial(path, startUtc);
            _path = path;
        }
        Save(_state);
        Changed?.Invoke(_state, true);
    }

    private ReplayState Initial(string? path, DateTime? startUtc)
    {
        var first = _history.Keys.Contains("EURUSD") ? "EURUSD" : _history.Keys.First();
        var current = _path is null ? null : _state;
        if (startUtc is { } start)
            return new ReplayState(Clamp(start), current?.Symbol ?? first, current?.Timeframe ?? Timeframe.M5, current?.Speed ?? Speeds[2]);
        return Load(path) is { } saved && _history.ContainsKey(saved.Symbol)
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
            Market?.AdvanceTo(s.CursorUtc, h.Bars[next].EndUtc);
            return s with { CursorUtc = h.Bars[next].EndUtc };
        }, reload: false);
        return moved;
    }

    /// <summary>Jumps anywhere in the history; with a broker only while the account is flat (spec §8).</summary>
    public void GoTo(DateTime utc) => Update(s =>
    {
        var to = Clamp(utc);
        Market?.JumpTo(to);
        return s with { CursorUtc = to, Playing = false };
    }, reload: true);

    /// <summary>Moves forward to the next day / London / New York open; open positions live through every price on the way.</summary>
    public void Skip(SkipTarget target) => Update(s =>
    {
        var to = Clamp(Next(target, s.CursorUtc));
        if (to > s.CursorUtc)
            Market?.AdvanceTo(s.CursorUtc, to);
        return s with { CursorUtc = to };
    }, reload: true);

    public void Play(int speed) => Update(s => s with { Playing = true, Speed = Speeds.Contains(speed) ? speed : s.Speed }, reload: false);

    public void Pause() => Update(s => s with { Playing = false }, reload: false);

    /// <summary>Tells the pages to reload everything (e.g. after a session switch).</summary>
    public void Refresh() => Changed?.Invoke(State, true);

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
