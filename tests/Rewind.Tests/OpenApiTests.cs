using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Google.Protobuf;
using Rewind.Broker;
using Rewind.History;
using Rewind.OpenApi;
using Rewind.Replay;

namespace Rewind.Tests;

/// <summary>spec §10: a client talks to Rewind's Open API server over TLS the way TM does.</summary>
public sealed class OpenApiTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rewind-api-").FullName;
    private readonly CancellationTokenSource _stop = new();
    private static readonly DateTime T0 = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static Bar B(int i, double o, double h, double l, double c) => new(T0.AddMinutes(5 * i), o, h, l, c, o + 0.00002, h + 0.00002, l + 0.00002, c + 0.00002);

    /// <summary>EURUSD flat at 1.10000 for 20 candles, then one candle up to 1.10200.</summary>
    private async Task<(Client Client, ReplayClock Clock, SimBroker Broker)> StartAsync()
    {
        var bars = Enumerable.Range(-20, 20).Select(i => B(i, 1.1, 1.1, 1.1, 1.1)).Append(B(0, 1.1, 1.102, 1.0999, 1.1015)).ToArray();
        var history = new Dictionary<string, SymbolHistory> { ["EURUSD"] = new(new SymbolInfo("EURUSD", 5), bars) };
        var specs = new Dictionary<string, SymbolSpec> { ["EURUSD"] = new("EURUSD", "EUR", "USD", 5, 4, 10_000_000, 100_000, 100_000, 10_000_000_000, 30) };
        var broker = new SimBroker(history, specs, new RewindConfig());
        var clock = new ReplayClock(history, path: null, broker, lookback: TimeSpan.Zero);
        clock.GoTo(T0);
        broker.Open(null, clock.State.CursorUtc);

        var cert = Certificates.Ensure(Path.Combine(_dir, "certs"), _ => { });
        var credentials = new Credentials("test-client", "test-secret");
        var port = FreePort();
        var server = new OpenApiServer(port, cert, credentials, broker, clock, history, _ => { });
        _ = server.RunAsync(_stop.Token);
        var client = await Client.ConnectAsync(port);

        await client.AskAsync(new ProtoOAApplicationAuthReq { ClientId = "test-client", ClientSecret = "test-secret" });
        var token = Credentials.NewToken();
        Assert.IsType<ProtoOAAccountAuthRes>(await client.AskAsync(new ProtoOAAccountAuthReq { CtidTraderAccountId = Credentials.AccountId, AccessToken = token }));
        return (client, clock, broker);
    }

    [Fact]
    public async Task Refuses_a_wrong_client_secret()
    {
        var (client, _, _) = await StartAsync();
        var err = await client.AskAsync(new ProtoOAApplicationAuthReq { ClientId = "test-client", ClientSecret = "wrong" });
        Assert.Equal("CH_CLIENT_AUTH_FAILURE", Assert.IsType<ProtoOAErrorRes>(err).ErrorCode);
    }

    [Fact]
    public async Task Market_order_is_accepted_then_filled_under_its_clientMsgId()
    {
        var (client, _, broker) = await StartAsync();
        var replies = await client.AskAllAsync(new ProtoOANewOrderReq
        {
            CtidTraderAccountId = Credentials.AccountId, SymbolId = 1, OrderType = ProtoOAOrderType.Market, TradeSide = ProtoOATradeSide.Sell,
            Volume = 1_000_000, Label = "TM-abcdef-P1-MIR", RelativeStopLoss = 100, // 0.00100 above the fill
        }, count: 2);
        var accepted = Assert.IsType<ProtoOAExecutionEvent>(replies[0]);
        var filled = Assert.IsType<ProtoOAExecutionEvent>(replies[1]);
        Assert.Equal(ProtoOAExecutionType.OrderAccepted, accepted.ExecutionType);
        Assert.Equal(ProtoOAExecutionType.OrderFilled, filled.ExecutionType);
        Assert.Equal(accepted.Order.OrderId, filled.Deal.OrderId);
        Assert.Equal(1.1, filled.Deal.ExecutionPrice, 5);           // sold at the bid
        Assert.Equal(1.101, filled.Position.StopLoss, 5);           // relative stop-loss from the fill
        Assert.Equal("TM-abcdef-P1-MIR", filled.Position.TradeData.Label);
        Assert.Single(broker.Positions());

        var rec = Assert.IsType<ProtoOAReconcileRes>(await client.AskAsync(new ProtoOAReconcileReq { CtidTraderAccountId = Credentials.AccountId }));
        Assert.Equal(filled.Position.PositionId, rec.Position.Single().PositionId);
        Assert.True(DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(rec.Position[0].TradeData.OpenTimestamp).UtcDateTime < TimeSpan.FromMinutes(1)); // real time
    }

    [Fact]
    public async Task Refusals_come_back_as_order_errors()
    {
        var (client, _, _) = await StartAsync();
        var err = await client.AskAsync(new ProtoOANewOrderReq
        {
            CtidTraderAccountId = Credentials.AccountId, SymbolId = 1, OrderType = ProtoOAOrderType.Stop, TradeSide = ProtoOATradeSide.Buy,
            Volume = 1_000_000, StopPrice = 1.09,
        });
        Assert.Equal("TRADING_BAD_STOPS", Assert.IsType<ProtoOAOrderErrorEvent>(err).ErrorCode);
    }

    [Fact]
    public async Task Replay_moves_prices_and_fills_stop_orders_for_every_client()
    {
        var (client, clock, _) = await StartAsync();
        await client.AskAllAsync(new ProtoOASubscribeSpotsReq { CtidTraderAccountId = Credentials.AccountId, SymbolId = { 1 } }, count: 2); // reply + current price
        var stop = await client.AskAsync(new ProtoOANewOrderReq
        {
            CtidTraderAccountId = Credentials.AccountId, SymbolId = 1, OrderType = ProtoOAOrderType.Stop, TradeSide = ProtoOATradeSide.Buy,
            Volume = 1_000_000, StopPrice = 1.101, Label = "TM-abcdef-P1-HMAIN",
        });
        Assert.Equal(ProtoOAExecutionType.OrderAccepted, Assert.IsType<ProtoOAExecutionEvent>(stop).ExecutionType);

        clock.Step(); // open → low → high → close: the buy-stop fills on the way up
        var pushed = await client.CollectAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(pushed, m => m is ProtoOASpotEvent s && s.Ask == 110_202); // the high, ask side
        var fill = pushed.OfType<ProtoOAExecutionEvent>().Single(e => e.ExecutionType == ProtoOAExecutionType.OrderFilled);
        Assert.Equal(1.101, fill.Deal.ExecutionPrice, 5);
        Assert.Equal("TM-abcdef-P1-HMAIN", fill.Position.TradeData.Label);
    }

    [Fact]
    public async Task Past_candles_are_moved_into_real_time()
    {
        var (client, clock, _) = await StartAsync();
        var now = DateTime.UtcNow;
        var res = Assert.IsType<ProtoOAGetTrendbarsRes>(await client.AskAsync(new ProtoOAGetTrendbarsReq
        {
            CtidTraderAccountId = Credentials.AccountId, SymbolId = 1, Period = ProtoOATrendbarPeriod.M5,
            FromTimestamp = new DateTimeOffset(now.AddHours(-1)).ToUnixTimeMilliseconds(), ToTimestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
        }));
        // The hour before the cursor (10:00), dated as the hour before now. The shift is rounded to whole minutes,
        // so the candle at the window's start may fall just outside it.
        Assert.InRange(res.Trendbar.Count, 11, 12);
        var last = DateTime.UnixEpoch.AddMinutes(res.Trendbar[^1].UtcTimestampInMinutes);
        Assert.InRange(now - last, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(7));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A minimal Open API client: TLS (trusting Rewind's certificate), framing, replies by clientMsgId.</summary>
    private sealed class Client(SslStream ssl)
    {
        private int _next;

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            return new Client(ssl);
        }

        public async Task<IMessage> AskAsync(IMessage request) => (await AskAllAsync(request, 1))[0];

        /// <summary>Sends <paramref name="request"/> and returns the first <paramref name="count"/> replies carrying its clientMsgId.</summary>
        public async Task<List<IMessage>> AskAllAsync(IMessage request, int count)
        {
            var id = $"m{++_next}";
            await ssl.WriteAsync(Protocol.Frame(request, id));
            var replies = new List<IMessage>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (replies.Count < count)
            {
                var envelope = await Protocol.ReadAsync(ssl, timeout.Token) ?? throw new IOException("closed");
                if (envelope.HasClientMsgId && envelope.ClientMsgId == id)
                    replies.Add(Protocol.Decode(envelope)!);
            }
            return replies;
        }

        /// <summary>Everything the server pushes within <paramref name="time"/>, heartbeats left out.</summary>
        public async Task<List<IMessage>> CollectAsync(TimeSpan time)
        {
            var result = new List<IMessage>();
            using var timeout = new CancellationTokenSource(time);
            try
            {
                while (await Protocol.ReadAsync(ssl, timeout.Token) is { } envelope)
                    if (Protocol.Decode(envelope) is { } m and not ProtoHeartbeatEvent)
                        result.Add(m);
            }
            catch (OperationCanceledException)
            {
            }
            return result;
        }
    }
}
