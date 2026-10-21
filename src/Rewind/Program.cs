// Rewind (docs/spec.md): replays past prices in the browser and trades them on a simulated account.
// Step 2: the chart — timeframes, scroll back, go to, step / play, levels (spec §5, §6).
// Step 3: the simulated broker and order panel — market orders, SL/TP, P/L, commission, sessions (spec §7, §8).
// Open http://localhost:5180.
//
// Reads ~/rewind/history/<SYMBOL>/*.m5.csv (rewind-download, spec §4) and ~/rewind/config/ (symbols.json,
// rewind.json; the copies next to the program if missing). Each session lives in ~/rewind/sessions/<name>/.
// Listens on localhost only.
//
// Options: --dir <folder> (default ~/rewind), --port <port> (default 5180).

using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rewind.Broker;
using Rewind.History;
using Rewind.Replay;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "rewind");
var port = 5180;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--dir") root = args[++i];
    else if (args[i] == "--port") port = int.Parse(args[++i], CultureInfo.InvariantCulture);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders().AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss'Z' ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
var log = app.Logger;

var history = HistoryStore.Load(Path.Combine(root, "history"), m => log.LogInformation("{Message}", m));
if (history.Count == 0)
{
    log.LogError("No history found in {Dir}. Run rewind-download first (spec §4).", Path.Combine(root, "history"));
    return 1;
}
var configDir = Path.Combine(root, "config");
var specs = ConfigFiles.Symbols(ConfigFiles.Find(configDir, "symbols.json"));
var config = ConfigFiles.Rewind(ConfigFiles.Find(configDir, "rewind.json"));
var broker = new SimBroker(history, specs, config);
var clock = new ReplayClock(history, path: null, broker);
var levels = new Levels(path: null);
var sessions = new Sessions(Path.Combine(root, "sessions"), clock, levels, broker);
var updates = new Updates(clock, history, broker, sessions);
clock.Changed += updates.Broadcast;
broker.Changed += () => updates.Broadcast(clock.State, false);
sessions.OpenLast();

// ---- API -------------------------------------------------------------------------------------------------------
var api = app.MapGroup("/api");

api.MapGet("/info", () => new
{
    Symbols = history.Values.Select(h => new { h.Symbol.Name, h.Symbol.Digits, First = h.FirstUtc, End = h.EndUtc }),
    Timeframes = Enum.GetNames<Timeframe>(),
    clock.MinUtc,
    clock.MaxUtc,
    ReplayClock.Speeds,
    State = Updates.Describe(clock.State),
    Specs = specs.Values.Where(s => history.ContainsKey(s.Name)),
    config.StartBalance,
    config.Leverage,
});

api.MapGet("/candles", (string symbol, string tf, long? before, int? count) =>
{
    if (!history.TryGetValue(symbol, out var h))
        return Results.NotFound();
    var beforeUtc = before is { } b ? DateTime.UnixEpoch.AddSeconds(b) : DateTime.MaxValue;
    var candles = Timeframes.Candles(h, Timeframes.Parse(tf), clock.State.CursorUtc, beforeUtc, Math.Clamp(count ?? 500, 1, 5000));
    return Results.Ok(candles.Select(Updates.ToJson));
});

api.MapPost("/view", (ViewRequest r) => Do(() => clock.SetView(r.Symbol, Timeframes.Parse(r.Timeframe))));
api.MapPost("/step", () => Do(() => clock.Step()));
api.MapPost("/play", (PlayRequest r) => Do(() => clock.Play(r.Speed)));
api.MapPost("/pause", () => Do(clock.Pause));
api.MapPost("/goto", (GoToRequest r) => Do(() => clock.GoTo(
    DateTime.SpecifyKind(DateTime.ParseExact(r.Time.Trim().Replace('T', ' '), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc))));
api.MapPost("/skip", (SkipRequest r) => Do(() => clock.Skip(Enum.Parse<SkipTarget>(r.To, ignoreCase: true))));

// ---- trading (spec §7) and sessions (spec §8) ------------------------------------------------------------------
api.MapPost("/orders/market", (MarketRequest r) => Trade(() =>
{
    var spec = specs.GetValueOrDefault(r.Symbol) ?? throw new BrokerException("SYMBOL_NOT_FOUND", $"Unknown symbol {r.Symbol}");
    broker.MarketOrder(r.Symbol, r.Side == "buy" ? 1 : r.Side == "sell" ? -1 : throw new BrokerException("BAD_SIDE", "side must be buy or sell"),
        spec.Volume(r.Lots), r.StopLoss, r.TakeProfit);
}));
api.MapPost("/positions/{id:long}/protection", (long id, ProtectionRequest r) => Trade(() => broker.AmendPosition(id, r.StopLoss, r.TakeProfit)));
api.MapPost("/positions/{id:long}/close", (long id, CloseRequest r) => Trade(() =>
{
    broker.ClosePosition(id, r.Lots is { } lots ? VolumeOf(id, lots) : null);
}));
api.MapPost("/positions/close-all", () => Trade(broker.CloseAll));
api.MapPost("/orders/{id:long}/amend", (long id, AmendRequest r) => Trade(() => broker.AmendOrder(id, r.Price)));
api.MapDelete("/orders/{id:long}", (long id) => Trade(() => broker.CancelOrder(id)));
api.MapGet("/account", broker.Snapshot);

api.MapGet("/sessions", () => new { sessions.Current, All = sessions.List() });
api.MapPost("/sessions", (NewSessionRequest r) => Do(() => sessions.Create(r.Name,
    DateTime.SpecifyKind(DateTime.ParseExact(r.Start.Trim().Replace('T', ' '), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Utc),
    r.Balance)));
api.MapPost("/sessions/open", (OpenSessionRequest r) => Do(() => sessions.Switch(r.Name)));

api.MapGet("/levels", (string symbol) => levels.For(symbol));
api.MapPost("/levels", (LevelRequest r) => history.ContainsKey(r.Symbol) ? Results.Ok(levels.Add(r.Symbol, r.Price, r.Note ?? "")) : Results.NotFound());
api.MapDelete("/levels/{id}", (string id) => levels.Remove(id) ? Results.NoContent() : Results.NotFound());

app.UseWebSockets();
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await updates.ServeAsync(socket, ctx.RequestAborted);
});

app.UseDefaultFiles();
app.UseStaticFiles();

_ = clock.RunAsync(app.Lifetime.ApplicationStopping);
log.LogInformation("Rewind on http://localhost:{Port} — replay at {Cursor:yyyy-MM-dd HH:mm} UTC, {Symbol} {Tf}", port, clock.State.CursorUtc, clock.State.Symbol, clock.State.Timeframe);
await app.RunAsync();
return 0;

long VolumeOf(long positionId, double lots)
{
    var symbol = broker.PositionSymbol(positionId);
    return specs[symbol].Volume(lots);
}

static IResult Trade(Action action)
{
    try
    {
        action();
        return Results.NoContent();
    }
    catch (BrokerException ex)
    {
        return Results.BadRequest(new { error = ex.Message, code = ex.Code });
    }
}

static IResult Do(Action action)
{
    try
    {
        action();
        return Results.NoContent();
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

record ViewRequest(string Symbol, string Timeframe);
record PlayRequest(int Speed);
record GoToRequest(string Time);
record SkipRequest(string To);
record LevelRequest(string Symbol, double Price, string? Note);
record MarketRequest(string Symbol, string Side, double Lots, double? StopLoss, double? TakeProfit);
record ProtectionRequest(double? StopLoss, double? TakeProfit);
record CloseRequest(double? Lots);
record AmendRequest(double Price);
record NewSessionRequest(string Name, string Start, double? Balance);
record OpenSessionRequest(string Name);

/// <summary>
/// Pushes every replay change to the open browser pages over a WebSocket: the cursor, play state, and the latest
/// (possibly still forming) candle of the symbol and timeframe on screen — or "reload" after a jump or view change.
/// </summary>
sealed class Updates(ReplayClock clock, IReadOnlyDictionary<string, SymbolHistory> history, SimBroker broker, Sessions sessions)
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _lock = new();
    private readonly List<(WebSocket Socket, SemaphoreSlim Gate)> _sockets = [];

    public static object ToJson(ChartCandle c) => new
    {
        time = (long)(c.TimeUtc - DateTime.UnixEpoch).TotalSeconds,
        open = c.Open,
        high = c.High,
        low = c.Low,
        close = c.Close,
    };

    public static object Describe(ReplayState s) => new
    {
        s.CursorUtc,
        s.Symbol,
        s.Timeframe,
        s.Speed,
        s.Playing,
        Utc = s.CursorUtc.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        London = TimeZoneInfo.ConvertTimeFromUtc(s.CursorUtc, London).ToString("ddd HH:mm", CultureInfo.InvariantCulture),
        NewYork = TimeZoneInfo.ConvertTimeFromUtc(s.CursorUtc, NewYork).ToString("ddd HH:mm", CultureInfo.InvariantCulture),
    };

    public void Broadcast(ReplayState s, bool reload)
    {
        var last = reload ? null : Timeframes.Candles(history[s.Symbol], s.Timeframe, s.CursorUtc, DateTime.MaxValue, 1).Select(ToJson).FirstOrDefault();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { state = Describe(s), reload, candle = last, account = broker.Snapshot(), session = sessions.Current }, Json);
        List<(WebSocket Socket, SemaphoreSlim Gate)> sockets;
        lock (_lock)
            sockets = _sockets.ToList();
        foreach (var (socket, gate) in sockets)
            _ = SendAsync(socket, gate, bytes);
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim gate, byte[] bytes)
    {
        await gate.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // page closed; ServeAsync removes it
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var entry = (socket, new SemaphoreSlim(1, 1));
        lock (_lock)
            _sockets.Add(entry);
        try
        {
            var hello = JsonSerializer.SerializeToUtf8Bytes(new { state = Describe(clock.State), reload = false, candle = (object?)null, account = broker.Snapshot(), session = sessions.Current }, Json);
            await SendAsync(socket, entry.Item2, hello);
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await socket.ReceiveAsync(buffer, ct); // the page sends nothing; this only notices it closing
                if (r.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
                _sockets.Remove(entry);
        }
    }
}
