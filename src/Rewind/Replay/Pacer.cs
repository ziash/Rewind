namespace Rewind.Replay;

/// <summary>
/// Exact mode's wait for TM (spec §6.2). After a price step of a symbol in a trade, while TM is connected: wait at least
/// <see cref="Step"/> (TM looks once a second) and until TM has sent nothing for <see cref="Quiet"/> (its orders of that look
/// are answered), never longer than <see cref="Max"/>.
/// </summary>
/// <param name="tmConnected">Is a TM connected to the Open API?</param>
/// <param name="inTrade">Has the symbol an open position or pending order?</param>
/// <param name="lastRequestUtc">When TM last sent a request (heartbeats not counted).</param>
public sealed class Pacer(Func<bool> tmConnected, Func<string, bool> inTrade, Func<DateTime> lastRequestUtc, TimeSpan step, TimeSpan quiet, TimeSpan max)
{
    public TimeSpan Step { get; } = step;
    public TimeSpan Quiet { get; } = quiet;
    public TimeSpan Max { get; } = max;

    public bool Applies(string symbol) => tmConnected() && inTrade(symbol);

    public void Wait()
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            var now = DateTime.UtcNow;
            if (now - start >= Max || now - start >= Step && now - lastRequestUtc() >= Quiet)
                return;
            Thread.Sleep(10);
        }
    }
}
