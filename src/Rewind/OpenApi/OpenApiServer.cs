using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Google.Protobuf;
using Rewind.Broker;
using Rewind.History;
using Rewind.Replay;

namespace Rewind.OpenApi;

/// <summary>
/// Rewind's cTrader Open API server (spec §10): Protobuf over TLS on localhost, answering the part of the API that TM
/// uses, on one account (<see cref="Credentials.AccountId"/>) — the simulated broker of the session in use.
///
/// Times (spec §6.1): TM thinks in real time, so everything is sent as real time: fills and spots carry "now",
/// positions their wall-clock opening time, and past candles are moved from replay time into real time (TM asks for
/// "the last 14 days before now" and gets the 14 days before the replay cursor, dated as if they had just happened).
///
/// Events: a request's own results go back under its clientMsgId (accepted, then filled …, as cTrader does); everything
/// else — the owner's clicks in the browser, stops and orders filled while the replay moves — goes to every connection
/// with the account authenticated, without an ID.
/// </summary>
public sealed class OpenApiServer
{
    private const string Version = "rewind";
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(10);
    private static readonly string[] Assets = ["USD", "EUR", "GBP", "JPY", "AUD", "BTC", "ETH"]; // asset ID = index + 1
    private const int MoneyDigits = 2;

    [ThreadStatic] private static List<BrokerEvent>? _collect; // events of the request being handled on this thread

    private readonly int _port;
    private readonly X509Certificate2 _certificate;
    private readonly Credentials _credentials;
    private readonly SimBroker _broker;
    private readonly ReplayClock _clock;
    private readonly IReadOnlyDictionary<string, SymbolHistory> _history;
    private readonly Action<string> _log;
    private readonly List<SymbolSpec> _symbols;            // symbol ID = index + 1
    private readonly Lock _lock = new();
    private readonly List<Connection> _connections = [];
    private long _nextId = 800_000_000;                    // order and deal IDs the broker does not have (market and closing orders, deals)

    public OpenApiServer(int port, X509Certificate2 certificate, Credentials credentials, SimBroker broker, ReplayClock clock,
        IReadOnlyDictionary<string, SymbolHistory> history, Action<string> log)
    {
        _port = port;
        _certificate = certificate;
        _credentials = credentials;
        _broker = broker;
        _clock = clock;
        _history = history;
        _log = log;
        _symbols = broker.Specs.Values.Where(s => history.ContainsKey(s.Name)).ToList();
        broker.Event += OnBrokerEvent;
        broker.QuoteChanged += OnQuote;
    }

    public int Connections
    {
        get { lock (_lock) return _connections.Count; }
    }

    private long _lastRequestTicks;

    /// <summary>When a client last sent a request, or had it answered (heartbeats not counted): exact mode waits for quiet (spec §6.2).</summary>
    public DateTime LastRequestUtc => new(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);

    /// <summary>A TM (or other client) connected or disconnected.</summary>
    public event Action? ConnectionsChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, _port); // this computer only
        listener.Start();
        _log($"Open API for TM on localhost:{_port} (TLS), account {Credentials.AccountId}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                _ = ServeAsync(tcp, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        var connection = new Connection(this);
        try
        {
            tcp.NoDelay = true;
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, ct);
            lock (_lock)
                _connections.Add(connection);
            _log("TM connected to the Open API");
            ConnectionsChanged?.Invoke();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = connection.WriteLoopAsync(ssl, stop.Token);
            try
            {
                while (await Protocol.ReadAsync(ssl, stop.Token) is { } envelope)
                    connection.Handle(envelope);
            }
            finally
            {
                stop.Cancel();
                await writer.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException or InvalidDataException or InvalidProtocolBufferException)
        {
            if (ex is not OperationCanceledException)
                _log($"Open API connection ended: {ex.Message}");
        }
        finally
        {
            bool removed;
            lock (_lock)
                removed = _connections.Remove(connection);
            if (removed)
            {
                _log("TM disconnected from the Open API");
                ConnectionsChanged?.Invoke();
            }
        }
    }

    // ---- broker → TM ------------------------------------------------------------------------------------------

    private void OnBrokerEvent(BrokerEvent e)
    {
        if (_collect is { } list)
        {
            list.Add(e); // goes back with the request's clientMsgId
            return;
        }
        foreach (var m in Execution(e))
            Broadcast(m);
    }

    private void OnQuote(string symbol, Quote q)
    {
        var id = SymbolId(symbol);
        if (id == 0)
            return;
        List<Connection> targets;
        lock (_lock)
            targets = _connections.Where(c => c.Watches(id)).ToList();
        if (targets.Count == 0)
            return;
        var spot = Spot(id, q);
        foreach (var c in targets)
            c.Send(spot, null);
    }

    private void Broadcast(IMessage m)
    {
        List<Connection> targets;
        lock (_lock)
            targets = _connections.Where(c => c.AccountReady).ToList();
        foreach (var c in targets)
            c.Send(m, null);
    }

    /// <summary>A broker event as cTrader's execution events (accepted, filled, replaced, cancelled).</summary>
    private IEnumerable<ProtoOAExecutionEvent> Execution(BrokerEvent e)
    {
        ProtoOAExecutionEvent Event(ProtoOAExecutionType type) => new() { CtidTraderAccountId = Credentials.AccountId, ExecutionType = type };
        switch (e.Kind)
        {
            case EventKind.OrderPlaced:
                yield return With(Event(ProtoOAExecutionType.OrderAccepted), order: StopOrder(e.Order!, ProtoOAOrderStatus.OrderStatusAccepted));
                break;
            case EventKind.OrderAmended:
                yield return With(Event(ProtoOAExecutionType.OrderReplaced), order: StopOrder(e.Order!, ProtoOAOrderStatus.OrderStatusAccepted));
                break;
            case EventKind.OrderCancelled:
                yield return With(Event(ProtoOAExecutionType.OrderCancelled), order: StopOrder(e.Order!, ProtoOAOrderStatus.OrderStatusCancelled));
                break;
            case EventKind.OrderFilled:
                break; // the PositionOpened that follows carries the fill
            case EventKind.ProtectionChanged:
                yield return With(Event(ProtoOAExecutionType.OrderReplaced), position: PositionOf(e.Position!));
                break;
            case EventKind.PositionOpened:
            {
                var p = e.Position!;
                var order = e.OrderId is { } stopId
                    ? Order(stopId, p.Symbol, p.Dir, e.Volume, ProtoOAOrderType.Stop, ProtoOAOrderStatus.OrderStatusFilled, p.Label, p.Comment, e.Price, positionId: p.Id)
                    : Order(NextId(), p.Symbol, p.Dir, e.Volume, ProtoOAOrderType.Market, ProtoOAOrderStatus.OrderStatusAccepted, p.Label, p.Comment, null, positionId: p.Id);
                if (e.OrderId is null)
                    yield return With(Event(ProtoOAExecutionType.OrderAccepted), order: order.Clone());
                order.OrderStatus = ProtoOAOrderStatus.OrderStatusFilled;
                order.ExecutionPrice = e.Price;
                order.ExecutedVolume = e.Volume;
                yield return With(Event(ProtoOAExecutionType.OrderFilled), order, PositionOf(p), Deal(order.OrderId, p.Id, p.Symbol, p.Dir, e.Volume, e.Price, e.Commission));
                break;
            }
            case EventKind.PositionClosed or EventKind.PositionReduced:
            {
                var p = e.Position!;
                var byStop = e.Reason is CloseReason.StopLoss or CloseReason.TakeProfit;
                var order = Order(NextId(), p.Symbol, -p.Dir, e.Volume, byStop ? ProtoOAOrderType.StopLossTakeProfit : ProtoOAOrderType.Market,
                    ProtoOAOrderStatus.OrderStatusAccepted, p.Label, "", null, positionId: p.Id);
                order.ClosingOrder = true;
                if (!byStop)
                    yield return With(Event(ProtoOAExecutionType.OrderAccepted), order: order.Clone(), position: PositionOf(p));
                order.OrderStatus = ProtoOAOrderStatus.OrderStatusFilled;
                order.ExecutionPrice = e.Price;
                order.ExecutedVolume = e.Volume;
                var deal = Deal(order.OrderId, p.Id, p.Symbol, -p.Dir, e.Volume, e.Price, e.Commission);
                deal.ClosePositionDetail = new ProtoOAClosePositionDetail
                {
                    EntryPrice = p.Entry,
                    GrossProfit = Money(e.GrossProfit),
                    Swap = 0,
                    Commission = -Money(e.Commission),
                    Balance = Money(e.Balance),
                    ClosedVolume = e.Volume,
                    MoneyDigits = MoneyDigits,
                };
                yield return With(Event(ProtoOAExecutionType.OrderFilled), order, PositionOf(p), deal);
                break;
            }
        }
    }

    private static ProtoOAExecutionEvent With(ProtoOAExecutionEvent e, ProtoOAOrder? order = null, ProtoOAPosition? position = null, ProtoOADeal? deal = null)
    {
        if (order is not null) e.Order = order;
        if (position is not null) e.Position = position;
        if (deal is not null) e.Deal = deal;
        return e;
    }

    // ---- message builders -------------------------------------------------------------------------------------

    private long SymbolId(string symbol) => _symbols.FindIndex(s => s.Name == symbol) + 1;
    private SymbolSpec? SymbolOf(long id) => id >= 1 && id <= _symbols.Count ? _symbols[(int)id - 1] : null;
    private long NextId() => Interlocked.Increment(ref _nextId);
    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static long Money(double usd) => (long)Math.Round(usd * 100);
    private static ulong Raw(double price) => (ulong)Math.Round(price * 100_000);
    private static ProtoOATradeSide Side(int dir) => dir > 0 ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell;
    private static long AssetId(string name) => Array.IndexOf(Assets, name) + 1;

    private ProtoOASpotEvent Spot(long symbolId, Quote q) => new()
    {
        CtidTraderAccountId = Credentials.AccountId,
        SymbolId = symbolId,
        Bid = Raw(q.Bid),
        Ask = Raw(q.Ask),
        Timestamp = Ms(DateTime.UtcNow),
    };

    private ProtoOATradeData TradeData(string symbol, int dir, long volume, DateTime openRealUtc, string label, string comment)
    {
        var t = new ProtoOATradeData { SymbolId = SymbolId(symbol), Volume = volume, TradeSide = Side(dir), OpenTimestamp = Ms(openRealUtc) };
        if (label.Length > 0) t.Label = label;
        if (comment.Length > 0) t.Comment = comment;
        return t;
    }

    private ProtoOAPosition PositionOf(Position p)
    {
        var pos = new ProtoOAPosition
        {
            PositionId = p.Id,
            TradeData = TradeData(p.Symbol, p.Dir, p.Volume, p.OpenRealUtc == default ? DateTime.UtcNow : p.OpenRealUtc, p.Label, p.Comment),
            PositionStatus = p.Volume > 0 ? ProtoOAPositionStatus.PositionStatusOpen : ProtoOAPositionStatus.PositionStatusClosed,
            Swap = 0,
            Price = p.Entry,
            Commission = -Money(p.OpenCommission),
            MoneyDigits = MoneyDigits,
            UtcLastUpdateTimestamp = Ms(DateTime.UtcNow),
        };
        if (p.StopLoss is { } sl) pos.StopLoss = sl;
        if (p.TakeProfit is { } tp) pos.TakeProfit = tp;
        return pos;
    }

    private ProtoOAOrder Order(long id, string symbol, int dir, long volume, ProtoOAOrderType type, ProtoOAOrderStatus status, string label, string comment,
        double? stopPrice, long? positionId = null)
    {
        var o = new ProtoOAOrder
        {
            OrderId = id,
            TradeData = TradeData(symbol, dir, volume, DateTime.UtcNow, label, comment),
            OrderType = type,
            OrderStatus = status,
            UtcLastUpdateTimestamp = Ms(DateTime.UtcNow),
        };
        if (stopPrice is { } sp) o.StopPrice = sp;
        if (positionId is { } pid) o.PositionId = pid;
        return o;
    }

    private ProtoOAOrder StopOrder(PendingOrder o, ProtoOAOrderStatus status) =>
        Order(o.Id, o.Symbol, o.Dir, o.Volume, ProtoOAOrderType.Stop, status, o.Label, o.Comment, o.StopPrice);

    private ProtoOADeal Deal(long orderId, long positionId, string symbol, int dir, long volume, double price, double commission)
    {
        var now = Ms(DateTime.UtcNow);
        return new ProtoOADeal
        {
            DealId = NextId(),
            OrderId = orderId,
            PositionId = positionId,
            Volume = volume,
            FilledVolume = volume,
            SymbolId = SymbolId(symbol),
            CreateTimestamp = now,
            ExecutionTimestamp = now,
            TradeSide = Side(dir),
            DealStatus = ProtoOADealStatus.Filled,
            ExecutionPrice = price,
            Commission = -Money(commission),
            MoneyDigits = MoneyDigits,
        };
    }

    // ---- one TM connection ------------------------------------------------------------------------------------

    private sealed class Connection(OpenApiServer server)
    {
        private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly HashSet<long> _spots = [];
        private volatile bool _app;
        private volatile bool _account;

        public bool AccountReady => _account;

        public bool Watches(long symbolId)
        {
            lock (_spots) return _account && _spots.Contains(symbolId);
        }

        public void Send(IMessage m, string? clientMsgId) => _out.Writer.TryWrite(Protocol.Frame(m, clientMsgId));

        public async Task WriteLoopAsync(Stream stream, CancellationToken ct)
        {
            var heartbeat = Task.Delay(HeartbeatEvery, ct);
            var next = _out.Reader.WaitToReadAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                if (await Task.WhenAny(next, heartbeat) == heartbeat)
                {
                    await stream.WriteAsync(Protocol.Frame(new ProtoHeartbeatEvent(), null), ct);
                    heartbeat = Task.Delay(HeartbeatEvery, ct);
                    continue;
                }
                if (!await next)
                    return;
                while (_out.Reader.TryRead(out var frame))
                    await stream.WriteAsync(frame, ct);
                await stream.FlushAsync(ct);
                next = _out.Reader.WaitToReadAsync(ct).AsTask();
            }
        }

        public void Handle(ProtoMessage envelope)
        {
            var id = envelope.HasClientMsgId ? envelope.ClientMsgId : null;
            var message = Protocol.Decode(envelope);
            if (message is null or ProtoHeartbeatEvent)
                return;
            Interlocked.Exchange(ref server._lastRequestTicks, DateTime.UtcNow.Ticks);
            try
            {
                foreach (var reply in Answer(message))
                    Send(reply, id);
            }
            catch (BrokerException ex)
            {
                Send(new ProtoOAOrderErrorEvent { CtidTraderAccountId = Credentials.AccountId, ErrorCode = ex.Code, Description = ex.Message }, id);
            }
            catch (RequestException ex)
            {
                Send(new ProtoOAErrorRes { ErrorCode = ex.Code, Description = ex.Message }, id);
            }
            finally
            {
                Interlocked.Exchange(ref server._lastRequestTicks, DateTime.UtcNow.Ticks);
            }
        }

        private IEnumerable<IMessage> Answer(IMessage m)
        {
            if (m is ProtoOAApplicationAuthReq app)
            {
                if (app.ClientId != server._credentials.ClientId || app.ClientSecret != server._credentials.ClientSecret)
                    throw new RequestException("CH_CLIENT_AUTH_FAILURE", "Unknown client ID or secret (see ~/rewind/tm/secrets.env)");
                _app = true;
                return [new ProtoOAApplicationAuthRes()];
            }
            if (!_app)
                throw new RequestException("CH_CLIENT_NOT_AUTHENTICATED", "Authenticate the application first");

            switch (m)
            {
                case ProtoOAVersionReq:
                    return [new ProtoOAVersionRes { Version = Version }];
                case ProtoOAGetAccountListByAccessTokenReq r:
                    Token(r.AccessToken);
                    return [new ProtoOAGetAccountListByAccessTokenRes
                    {
                        AccessToken = r.AccessToken,
                        PermissionScope = ProtoOAClientPermissionScope.ScopeTrade,
                        CtidTraderAccount = { new ProtoOACtidTraderAccount { CtidTraderAccountId = (ulong)Credentials.AccountId, IsLive = false, TraderLogin = Credentials.AccountId } },
                    }];
                case ProtoOARefreshTokenReq r:
                    Token(r.RefreshToken);
                    return [new ProtoOARefreshTokenRes
                    {
                        AccessToken = Credentials.NewToken(), RefreshToken = Credentials.NewToken(), TokenType = "bearer",
                        ExpiresIn = (long)TimeSpan.FromDays(30).TotalSeconds,
                    }];
                case ProtoOAAccountAuthReq r:
                    Token(r.AccessToken);
                    Account(r.CtidTraderAccountId, needAuth: false);
                    _account = true;
                    return [new ProtoOAAccountAuthRes { CtidTraderAccountId = r.CtidTraderAccountId }];
                case ProtoOAAccountLogoutReq r:
                    Account(r.CtidTraderAccountId);
                    _account = false;
                    return [new ProtoOAAccountLogoutRes { CtidTraderAccountId = r.CtidTraderAccountId }];
                case ProtoOATraderReq r:
                    Account(r.CtidTraderAccountId);
                    return [new ProtoOATraderRes { CtidTraderAccountId = r.CtidTraderAccountId, Trader = Trader() }];
                case ProtoOAAssetListReq r:
                    Account(r.CtidTraderAccountId);
                    var assets = new ProtoOAAssetListRes { CtidTraderAccountId = r.CtidTraderAccountId };
                    assets.Asset.AddRange(Assets.Select((a, i) => new ProtoOAAsset { AssetId = i + 1, Name = a, DisplayName = a, Digits = a is "JPY" ? 0 : 2 }));
                    return [assets];
                case ProtoOASymbolsListReq r:
                    Account(r.CtidTraderAccountId);
                    var list = new ProtoOASymbolsListRes { CtidTraderAccountId = r.CtidTraderAccountId };
                    list.Symbol.AddRange(server._symbols.Select((s, i) => new ProtoOALightSymbol
                    {
                        SymbolId = i + 1, SymbolName = s.Name, Enabled = true, BaseAssetId = AssetId(s.Base), QuoteAssetId = AssetId(s.Quote), SymbolCategoryId = 1,
                    }));
                    return [list];
                case ProtoOASymbolByIdReq r:
                    Account(r.CtidTraderAccountId);
                    var byId = new ProtoOASymbolByIdRes { CtidTraderAccountId = r.CtidTraderAccountId };
                    foreach (var id in r.SymbolId)
                        if (server.SymbolOf(id) is { } s)
                            byId.Symbol.Add(new ProtoOASymbol
                            {
                                SymbolId = id, Digits = s.Digits, PipPosition = s.PipPosition, LotSize = s.LotSize,
                                MinVolume = s.MinVolume, StepVolume = s.StepVolume, MaxVolume = s.MaxVolume,
                                TradingMode = ProtoOATradingMode.Enabled, ScheduleTimeZone = "UTC", EnableShortSelling = true,
                                CommissionType = ProtoOACommissionType.UsdPerMillionUsd, PreciseTradingCommissionRate = (long)(s.CommissionPerMillionUsd * 100_000_000),
                            });
                    return [byId];
                case ProtoOASubscribeSpotsReq r:
                    Account(r.CtidTraderAccountId);
                    lock (_spots)
                        _spots.UnionWith(r.SymbolId);
                    var spots = new List<IMessage> { new ProtoOASubscribeSpotsRes { CtidTraderAccountId = r.CtidTraderAccountId } };
                    foreach (var sid in r.SymbolId)
                        if (server.SymbolOf(sid) is { } s && server._broker.QuoteOf(s.Name) is { Bid: > 0 } q)
                            spots.Add(server.Spot(sid, q));
                    return spots;
                case ProtoOAUnsubscribeSpotsReq r:
                    Account(r.CtidTraderAccountId);
                    lock (_spots)
                        _spots.ExceptWith(r.SymbolId);
                    return [new ProtoOAUnsubscribeSpotsRes { CtidTraderAccountId = r.CtidTraderAccountId }];
                case ProtoOASubscribeLiveTrendbarReq r:
                    Account(r.CtidTraderAccountId);
                    return [new ProtoOASubscribeLiveTrendbarRes { CtidTraderAccountId = r.CtidTraderAccountId }];
                case ProtoOAGetTrendbarsReq r:
                    Account(r.CtidTraderAccountId);
                    return [Trendbars(r)];
                case ProtoOAReconcileReq r:
                    Account(r.CtidTraderAccountId);
                    var rec = new ProtoOAReconcileRes { CtidTraderAccountId = r.CtidTraderAccountId };
                    rec.Position.AddRange(server._broker.Positions().Select(server.PositionOf));
                    rec.Order.AddRange(server._broker.Orders().Select(o => server.StopOrder(o, ProtoOAOrderStatus.OrderStatusAccepted)));
                    return [rec];
                case ProtoOANewOrderReq r:
                    Account(r.CtidTraderAccountId);
                    return Trade(() => NewOrder(r));
                case ProtoOAAmendOrderReq r:
                    Account(r.CtidTraderAccountId);
                    return Trade(() => server._broker.AmendOrder(r.OrderId, r.HasStopPrice ? r.StopPrice
                        : throw new BrokerException("INVALID_REQUEST", "Only the stop price of a stop order can be changed")));
                case ProtoOACancelOrderReq r:
                    Account(r.CtidTraderAccountId);
                    return Trade(() => server._broker.CancelOrder(r.OrderId));
                case ProtoOAAmendPositionSLTPReq r:
                    Account(r.CtidTraderAccountId);
                    return Trade(() => server._broker.AmendPosition(r.PositionId, r.HasStopLoss ? r.StopLoss : null, r.HasTakeProfit ? r.TakeProfit : null));
                case ProtoOAClosePositionReq r:
                    Account(r.CtidTraderAccountId);
                    return Trade(() => server._broker.ClosePosition(r.PositionId, r.Volume, CloseReason.Tm));
                default:
                    server._log($"Open API: {m.GetType().Name} is not supported by Rewind");
                    throw new RequestException("UNSUPPORTED_MESSAGE", $"Rewind does not support {m.GetType().Name} (spec §10)");
            }
        }

        private void NewOrder(ProtoOANewOrderReq r)
        {
            var spec = server.SymbolOf(r.SymbolId) ?? throw new BrokerException("SYMBOL_NOT_FOUND", $"Symbol {r.SymbolId} not found");
            var dir = r.TradeSide == ProtoOATradeSide.Buy ? 1 : -1;
            var label = r.HasLabel ? r.Label : "";
            var comment = r.HasComment ? r.Comment : "";
            switch (r.OrderType)
            {
                case ProtoOAOrderType.Market:
                    // cTrader puts a relative stop-loss / take-profit at that distance from the fill price.
                    var q = server._broker.QuoteOf(spec.Name);
                    var fill = dir > 0 ? q.Ask : q.Bid;
                    double? sl = r.HasRelativeStopLoss ? spec.Round(fill - dir * r.RelativeStopLoss / 100_000.0) : null;
                    double? tp = r.HasRelativeTakeProfit ? spec.Round(fill + dir * r.RelativeTakeProfit / 100_000.0) : null;
                    server._broker.MarketOrder(spec.Name, dir, r.Volume, sl, tp, label, comment);
                    break;
                case ProtoOAOrderType.Stop:
                    server._broker.StopOrder(spec.Name, dir, r.Volume, r.StopPrice, label, comment);
                    break;
                default:
                    throw new BrokerException("INVALID_REQUEST", $"Rewind accepts market and stop orders, not {r.OrderType} (spec §2)");
            }
        }

        /// <summary>Runs a trading request and returns its events as execution events, for its clientMsgId.</summary>
        private List<IMessage> Trade(Action action)
        {
            var events = new List<BrokerEvent>();
            _collect = events;
            try
            {
                action();
            }
            finally
            {
                _collect = null;
            }
            return events.SelectMany(server.Execution).Cast<IMessage>().ToList();
        }

        private ProtoOATrader Trader()
        {
            var balance = server._broker.Balance();
            return new ProtoOATrader
            {
                CtidTraderAccountId = Credentials.AccountId, Balance = Money(balance), DepositAssetId = AssetId("USD"),
                AccessRights = ProtoOAAccessRights.FullAccess, AccountType = ProtoOAAccountType.Hedged, LeverageInCents = 50_000,
                TraderLogin = Credentials.AccountId, BrokerName = "Rewind", MoneyDigits = MoneyDigits,
            };
        }

        /// <summary>Past candles (spec §6.1): the asked real-time window is moved to just before the replay cursor, and back.</summary>
        private ProtoOAGetTrendbarsRes Trendbars(ProtoOAGetTrendbarsReq r)
        {
            var res = new ProtoOAGetTrendbarsRes { CtidTraderAccountId = r.CtidTraderAccountId, Period = r.Period, Timestamp = Ms(DateTime.UtcNow), SymbolId = r.SymbolId };
            Timeframe? tf = r.Period switch
            {
                ProtoOATrendbarPeriod.M5 => Timeframe.M5,
                ProtoOATrendbarPeriod.M15 => Timeframe.M15,
                ProtoOATrendbarPeriod.H1 => Timeframe.H1,
                ProtoOATrendbarPeriod.H4 => Timeframe.H4,
                ProtoOATrendbarPeriod.D1 => Timeframe.D1,
                _ => null,
            };
            if (tf is null || server.SymbolOf(r.SymbolId) is not { } spec || !server._history.TryGetValue(spec.Name, out var h))
                return res;
            var cursor = server._clock.State.CursorUtc;
            var offset = TimeSpan.FromMinutes(Math.Round((cursor - DateTime.UtcNow).TotalMinutes));
            var from = DateTime.UnixEpoch.AddMilliseconds(r.FromTimestamp) + offset;
            var to = DateTime.UnixEpoch.AddMilliseconds(r.ToTimestamp) + offset;
            foreach (var c in Timeframes.Candles(h, tf.Value, cursor, to, 5000).Where(c => c.TimeUtc >= from))
            {
                var low = Raw(c.Low);
                res.Trendbar.Add(new ProtoOATrendbar
                {
                    Volume = 1, Period = r.Period, Low = (long)low,
                    DeltaOpen = Raw(c.Open) - low, DeltaHigh = Raw(c.High) - low, DeltaClose = Raw(c.Close) - low,
                    UtcTimestampInMinutes = (uint)(c.TimeUtc - offset - DateTime.UnixEpoch).TotalMinutes,
                });
            }
            return res;
        }

        private static void Token(string? token)
        {
            if (!Credentials.IsToken(token))
                throw new RequestException("CH_ACCESS_TOKEN_INVALID", "Unknown token (see ~/rewind/tm/secrets.env)");
        }

        private void Account(long ctid, bool needAuth = true)
        {
            if (ctid != Credentials.AccountId)
                throw new RequestException("CH_CTID_TRADER_ACCOUNT_NOT_FOUND", $"Rewind has one account: {Credentials.AccountId}");
            if (needAuth && !_account)
                throw new RequestException("CH_CTID_TRADER_ACCOUNT_NOT_AUTHORIZED", "Authenticate the account first");
        }
    }

    private sealed class RequestException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
