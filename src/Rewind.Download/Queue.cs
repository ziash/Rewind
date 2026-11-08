namespace Rewind.Download;

internal enum FetchStatus { Saved, Busy, Bad }

/// <summary>One request for one file: saved (or "no data that day"), busy (server error, 429, no answer: try again
/// later) or bad (a broken file: not kept, not tried again in this run).</summary>
internal readonly record struct Attempt(FetchStatus Status, string Problem = "", TimeSpan? RetryAfter = null);

/// <summary>
/// The download queue (spec §4). A file Dukascopy does not give now goes to the end of the queue and the next file is
/// fetched, so one stubborn file never holds up the rest (owner, 08-Nov-2026); after <see cref="MaxTries"/> tries it is
/// left for a re-run. Still polite: a single failure costs no wait, but failures in a row mean the server itself is
/// busy, so the queue waits 1 min, then doubles up to 15 min, until a file comes through; a Retry-After is obeyed.
/// </summary>
internal static class DownloadQueue
{
    public const int MaxTries = 8;
    public static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    /// <summary>Pause after the <paramref name="busyInARow"/>-th failure in a row: none after the first.</summary>
    public static TimeSpan Pause(int busyInARow, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra;
        if (busyInARow < 2)
            return TimeSpan.Zero;
        var wait = FirstWait * Math.Pow(2, Math.Min(busyInARow - 2, 10));
        return wait < MaxWait ? wait : MaxWait;
    }

    /// <summary>Fetches every file, one at a time. <paramref name="finished"/> is called each time a file is done with
    /// (saved, bad or given up), with the count so far and how many are waiting at the end of the queue for a retry.
    /// Returns how many could not be fetched now.</summary>
    public static async Task<int> RunAsync<T>(IReadOnlyList<T> files, Func<T, CancellationToken, Task<Attempt>> fetch,
        Func<TimeSpan, CancellationToken, Task> wait, Func<T, string> name, Action<string> log,
        Action<int, T, int> finished, CancellationToken ct)
    {
        var queue = new Queue<(T File, int Tries)>(files.Select(f => (f, 0)));
        int done = 0, notFetched = 0, busyInARow = 0, retrying = 0;
        try
        {
            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var (file, tries) = queue.Dequeue();
                if (tries > 0)
                    retrying--;
                var attempt = await fetch(file, ct);
                if (attempt.Status != FetchStatus.Busy)
                {
                    busyInARow = 0;
                    if (attempt.Status == FetchStatus.Bad)
                        notFetched++;
                    finished(++done, file, retrying);
                    continue;
                }

                busyInARow++;
                if (++tries < MaxTries)
                {
                    queue.Enqueue((file, tries));
                    retrying++;
                    log($"  {name(file)}: {attempt.Problem}; moved to the end of the queue (try {tries} of {MaxTries})");
                }
                else
                {
                    notFetched++;
                    log($"  {name(file)}: {attempt.Problem}; giving up for now after {MaxTries} tries; a re-run fetches it");
                    finished(++done, file, retrying);
                }

                var pause = Pause(busyInARow, attempt.RetryAfter);
                if (pause > TimeSpan.Zero && queue.Count > 0)
                {
                    log($"  Dukascopy busy ({busyInARow} failure(s) in a row); waiting {pause.TotalMinutes:0.#} min");
                    await wait(pause, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        return notFetched + queue.Count;
    }
}
