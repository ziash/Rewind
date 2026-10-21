namespace Rewind.Broker;

/// <summary>An open position (spec §7). Volume in Open API units; Dir +1 long, -1 short.</summary>
public sealed class Position
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public int Dir { get; set; }
    public long Volume { get; set; }
    public double Entry { get; set; }
    public DateTime OpenUtc { get; set; }
    /// <summary>Wall-clock time of the opening: what TM is told (spec §6.1, TM thinks in real time).</summary>
    public DateTime OpenRealUtc { get; set; }
    public double? StopLoss { get; set; }
    public double? TakeProfit { get; set; }
    /// <summary>Stop-loss given when the position was opened: 1R for the results (spec §8).</summary>
    public double? InitialStopLoss { get; set; }
    public string Label { get; set; } = "";
    public string Comment { get; set; } = "";
    /// <summary>Opening commission not yet booked against a closed part (USD).</summary>
    public double OpenCommission { get; set; }

    public Position Copy() => (Position)MemberwiseClone();
}

/// <summary>A pending stop order (TM's hedges, spec §7): buy-stop above the ask, sell-stop below the bid.</summary>
public sealed class PendingOrder
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public int Dir { get; set; }
    public long Volume { get; set; }
    public double StopPrice { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Label { get; set; } = "";
    public string Comment { get; set; } = "";

    public PendingOrder Copy() => (PendingOrder)MemberwiseClone();
}

/// <summary>Everything the account remembers; saved as account.json in the session folder after every change.</summary>
public sealed class AccountState
{
    public double Balance { get; set; }
    public double StartBalance { get; set; }
    public double CommissionPaid { get; set; }
    public long NextId { get; set; } = 1001;
    public List<Position> Positions { get; set; } = [];
    public List<PendingOrder> Orders { get; set; } = [];
}

/// <summary>Why a position (or part) closed.</summary>
public enum CloseReason { Manual, StopLoss, TakeProfit }

public enum EventKind { PositionOpened, PositionClosed, PositionReduced, ProtectionChanged, OrderPlaced, OrderAmended, OrderCancelled, OrderFilled }

/// <summary>
/// Something that happened on the account, at replay time (spec §5 messages, events.csv). For the Open API server
/// (step 4) it also carries copies of the position / order as they are after the event (a closed position with
/// volume 0), the commission of this fill, and for a close its gross profit and the new balance (USD).
/// </summary>
public sealed record BrokerEvent(DateTime TimeUtc, EventKind Kind, string Symbol, long? PositionId, long? OrderId, int Dir, long Volume,
    double Price, string Detail, string Label = "")
{
    public Position? Position { get; init; }
    public PendingOrder? Order { get; init; }
    public CloseReason? Reason { get; init; }
    public double Commission { get; init; }
    public double GrossProfit { get; init; }
    public double Balance { get; init; }
}

/// <summary>A refused request, with cTrader's error code where there is one (e.g. TRADING_BAD_STOPS).</summary>
public sealed class BrokerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
