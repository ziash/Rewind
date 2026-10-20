using System.Net;

namespace Rewind.Download;

/// <summary>
/// Fetches one day file at a time from the Dukascopy data feed, politely (spec §4): one request at a time with a pause
/// between them; on "too many requests" (HTTP 429) or a server error it waits (the server's Retry-After, else 1 minute,
/// doubling up to 15 minutes) and tries again. Each file is kept in the raw cache, so nothing is fetched twice.
/// </summary>
internal sealed class Feed(string cacheDir, TimeSpan interval, Action<string> log) : IDisposable
{
    public const string BaseUrl = "https://datafeed.dukascopy.com/datafeed";
    private const int MaxTries = 8;
    private static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = CreateClient();
    private DateTime _nextRequest = DateTime.MinValue;

    public int Fetched { get; private set; }
    public long Bytes { get; private set; }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rewind-download/0.1 (personal research; one request at a time)");
        return http;
    }

    /// <summary>Feed URL of a day file. The month in the path counts from 00 (January = 00).</summary>
    public static string Url(Instrument i, DateOnly day, Side side) =>
        $"{BaseUrl}/{i.Name}/{day.Year:0000}/{day.Month - 1:00}/{day.Day:00}/{(side == Side.Bid ? "BID" : "ASK")}_candles_min_1.bi5";

    /// <summary>Raw cache path; an empty file means "no data that day".</summary>
    public string CachePath(Instrument i, DateOnly day, Side side) =>
        Path.Combine(cacheDir, i.Name, day.ToString("yyyy-MM"), $"{day:yyyy-MM-dd}.{side.ToString().ToUpperInvariant()}.bi5");

    public bool Cached(Instrument i, DateOnly day, Side side) => File.Exists(CachePath(i, day, side));

    /// <summary>Downloads the file into the cache. Returns false if it could not be fetched now (a re-run tries again).</summary>
    public async Task<bool> FetchAsync(Instrument i, DateOnly day, Side side, CancellationToken ct)
    {
        var url = Url(i, day, side);
        var wait = FirstWait;
        for (var attempt = 1; attempt <= MaxTries; attempt++)
        {
            var delay = _nextRequest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            _nextRequest = DateTime.UtcNow + interval;

            byte[]? body = null;
            string problem;
            TimeSpan? retryAfter = null;
            try
            {
                using var response = await _http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                    body = await response.Content.ReadAsByteArrayAsync(ct);
                else if (response.StatusCode == HttpStatusCode.NotFound)
                    body = []; // no file for that day: no data
                retryAfter = response.Headers.RetryAfter?.Delta;
                problem = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                problem = ex is TaskCanceledException ? "no answer within 60 s" : ex.Message;
            }

            if (body is not null)
            {
                if (body.Length > 0)
                {
                    try
                    {
                        Bi5.Read(body, day); // check before keeping it
                    }
                    catch (Bi5Exception ex)
                    {
                        log($"  {i.Name} {day:yyyy-MM-dd} {side}: bad file ({ex.Message}); not kept");
                        return false;
                    }
                }
                var path = CachePath(i, day, side);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path + ".tmp", body, ct);
                File.Move(path + ".tmp", path, overwrite: true);
                Fetched++;
                Bytes += body.Length;
                return true;
            }

            if (attempt == MaxTries)
                break;
            var pause = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : wait;
            log($"  {i.Name} {day:yyyy-MM-dd} {side}: {problem}; waiting {pause.TotalMinutes:0.#} min (try {attempt} of {MaxTries})");
            await Task.Delay(pause, ct);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
        }
        log($"  {i.Name} {day:yyyy-MM-dd} {side}: giving up for now; a re-run fetches it");
        return false;
    }

    public void Dispose() => _http.Dispose();
}
