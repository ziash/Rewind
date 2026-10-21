using Rewind.History;

namespace Rewind.Replay;

/// <summary>What the replay clock tells the broker (spec §6, §7): prices moved forward, or the cursor jumped (only when flat).</summary>
public interface IMarket
{
    bool IsFlat { get; }

    /// <summary>Walks the candles finishing in (from, to]; <paramref name="afterStep"/> after each price step, free to wait.</summary>
    void AdvanceTo(DateTime fromUtc, DateTime toUtc, Action<PriceStep>? afterStep = null);

    /// <summary>Prices at a new cursor without walking; throws <see cref="ArgumentException"/> unless flat.</summary>
    void JumpTo(DateTime cursorUtc);
}

/// <summary>One price step just walked (spec §6.3): step <paramref name="Index"/> (0–3) of <paramref name="Bar"/>.</summary>
public readonly record struct PriceStep(string Symbol, Bar Bar, int Index, double Bid, double Ask);
