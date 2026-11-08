using System.Net;

namespace Rewind.Download;

/// <summary>
/// Fetches one day file at a time from the Dukascopy data feed, politely (spec §4): one request at a time with a pause
/// between them. A file the server does not give now ("too many requests", a server error, no answer) is tried again by
/// <see cref="DownloadQueue"/>, after the other files. Each file is kept in the raw cache, so nothing is fetched twice.
/// </summary>
internal sealed class Feed(string cacheDir, TimeSpan interval, Action<string> log) : IDisposable
{
    public const string BaseUrl = "https://datafeed.dukascopy.com/datafeed";
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
        $"{BaseUrl}/{i.Feed}/{day.Year:0000}/{day.Month - 1:00}/{day.Day:00}/{(side == Side.Bid ? "BID" : "ASK")}_candles_min_1.bi5";

    /// <summary>Raw cache path; an empty file means "no data that day".</summary>
    public string CachePath(Instrument i, DateOnly day, Side side) =>
        Path.Combine(cacheDir, i.Name, day.ToString("yyyy-MM"), $"{day:yyyy-MM-dd}.{side.ToString().ToUpperInvariant()}.bi5");

    public bool Cached(Instrument i, DateOnly day, Side side) => File.Exists(CachePath(i, day, side));

    /// <summary>One request for the file; saved into the cache when it comes through.</summary>
    public async Task<Attempt> TryOnceAsync(Instrument i, DateOnly day, Side side, CancellationToken ct)
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
            using var response = await _http.GetAsync(Url(i, day, side), ct);
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
        if (body is null)
            return new Attempt(FetchStatus.Busy, problem, retryAfter);

        if (body.Length > 0)
        {
            try
            {
                Bi5.Read(body, day); // check before keeping it
            }
            catch (Bi5Exception ex)
            {
                log($"  {i.Name} {day:yyyy-MM-dd} {side}: bad file ({ex.Message}); not kept");
                return new Attempt(FetchStatus.Bad, ex.Message);
            }
        }
        var path = CachePath(i, day, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path + ".tmp", body, ct);
        File.Move(path + ".tmp", path, overwrite: true);
        Fetched++;
        Bytes += body.Length;
        return new Attempt(FetchStatus.Saved);
    }

    public void Dispose() => _http.Dispose();
}
