namespace Rewind.Replay;

/// <summary>What the replay clock tells the broker (spec §6, §7): prices moved forward, or the cursor jumped (only when flat).</summary>
public interface IMarket
{
    bool IsFlat { get; }

    /// <summary>Walks the candles finishing in (from, to].</summary>
    void AdvanceTo(DateTime fromUtc, DateTime toUtc);

    /// <summary>Prices at a new cursor without walking; throws <see cref="ArgumentException"/> unless flat.</summary>
    void JumpTo(DateTime cursorUtc);
}
