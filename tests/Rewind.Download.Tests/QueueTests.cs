using Rewind.Download;

namespace Rewind.Download.Tests;

/// <summary>spec §4: a file the server does not give now goes to the end of the queue; polite waits only when the
/// server fails again and again.</summary>
public sealed class QueueTests
{
    private sealed class Run
    {
        public readonly List<string> Fetched = [];
        public readonly List<TimeSpan> Waits = [];
        public readonly List<string> Log = [];
        public int NotFetched;

        /// <summary>Plays the queue; <paramref name="answer"/> gives each request's answer from the file and its try number.</summary>
        public static async Task<Run> Of(string[] files, Func<string, int, Attempt> answer)
        {
            var run = new Run();
            var tries = new Dictionary<string, int>();
            run.NotFetched = await DownloadQueue.RunAsync(files,
                (f, _) =>
                {
                    run.Fetched.Add(f);
                    tries[f] = tries.GetValueOrDefault(f) + 1;
                    return Task.FromResult(answer(f, tries[f]));
                },
                (t, _) => { run.Waits.Add(t); return Task.CompletedTask; },
                f => f, run.Log.Add, (_, _, _) => { }, CancellationToken.None);
            return run;
        }
    }

    private static readonly Attempt Saved = new(FetchStatus.Saved);
    private static readonly Attempt Busy = new(FetchStatus.Busy, "HTTP 503 Service Unavailable");

    [Fact]
    public async Task A_busy_file_goes_to_the_end_and_the_others_are_fetched_first_without_waiting()
    {
        var run = await Run.Of(["A", "B", "C"], (f, n) => f == "A" && n == 1 ? Busy : Saved);
        Assert.Equal(["A", "B", "C", "A"], run.Fetched);
        Assert.Empty(run.Waits);
        Assert.Equal(0, run.NotFetched);
        Assert.Contains("  A: HTTP 503 Service Unavailable; moved to the end of the queue (try 1 of 8)", run.Log);
    }

    [Fact]
    public async Task A_file_that_never_comes_is_given_up_after_8_tries_and_the_rest_still_downloads()
    {
        var run = await Run.Of(["A", "B", "C"], (f, _) => f == "A" ? Busy : Saved);
        Assert.Equal(["A", "B", "C", "A", "A", "A", "A", "A", "A", "A"], run.Fetched);
        Assert.Equal(1, run.NotFetched);
        // B and C reset the count; alone at the end, tries 2–8 fail in a row: waits 1, 2, 4, 8, 15 min, none after the last
        Assert.Equal(new[] { 1, 2, 4, 8, 15 }.Select(m => TimeSpan.FromMinutes(m)), run.Waits);
    }

    [Fact]
    public async Task Failures_in_a_row_mean_the_server_is_busy_and_a_success_resets_the_wait()
    {
        // A and B both fail (2 in a row → 1 min), C comes through, then A and B come through
        var run = await Run.Of(["A", "B", "C"], (f, n) => f != "C" && n == 1 ? Busy : Saved);
        Assert.Equal(["A", "B", "C", "A", "B"], run.Fetched);
        Assert.Equal([TimeSpan.FromMinutes(1)], run.Waits);
        Assert.Equal(0, run.NotFetched);
    }

    [Fact]
    public async Task The_servers_Retry_After_is_obeyed_and_a_bad_file_is_not_tried_again()
    {
        var run = await Run.Of(["A", "B"], (f, n) => f switch
        {
            "A" when n == 1 => new Attempt(FetchStatus.Busy, "HTTP 429 Too Many Requests", TimeSpan.FromSeconds(30)),
            "B" => new Attempt(FetchStatus.Bad, "broken"),
            _ => Saved,
        });
        Assert.Equal(["A", "B", "A"], run.Fetched);
        Assert.Equal([TimeSpan.FromSeconds(30)], run.Waits);
        Assert.Equal(1, run.NotFetched);
    }
}
