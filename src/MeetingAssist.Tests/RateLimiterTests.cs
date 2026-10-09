using System.Diagnostics;
using MeetingAssist.Core.Transcription;

namespace MeetingAssist.Tests;

/// <summary>
/// The pacer exists because Groq caps Whisper at 20 requests/minute on every plan. Short
/// windows keep these tests fast; the behaviour under test is the shape of the window, not
/// the specific minute.
/// </summary>
public class RateLimiterTests
{
    [Fact]
    public async Task Requests_within_the_limit_do_not_wait()
    {
        var limiter = new RateLimiter(5, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++)
            Assert.Equal(TimeSpan.Zero, await limiter.AcquireAsync(default));

        Assert.Equal(0, limiter.PacedRequests);
        Assert.Equal(TimeSpan.Zero, limiter.TotalWaited);
    }

    [Fact]
    public async Task The_request_past_the_limit_waits_for_the_window_to_roll()
    {
        var window = TimeSpan.FromMilliseconds(400);
        var limiter = new RateLimiter(3, window);

        for (var i = 0; i < 3; i++) await limiter.AcquireAsync(default);

        var clock = Stopwatch.StartNew();
        var waited = await limiter.AcquireAsync(default);
        clock.Stop();

        // The fourth request cannot proceed until the first falls out of the window.
        Assert.True(waited > TimeSpan.Zero, "fourth request should have been paced");
        Assert.True(clock.Elapsed >= window * 0.7,
            $"expected to wait about {window.TotalMilliseconds}ms, waited {clock.ElapsedMilliseconds}ms");
        Assert.Equal(1, limiter.PacedRequests);
    }

    [Fact]
    public async Task The_window_slides_rather_than_resetting_in_fixed_blocks()
    {
        var window = TimeSpan.FromMilliseconds(300);
        var limiter = new RateLimiter(2, window);

        await limiter.AcquireAsync(default);
        await limiter.AcquireAsync(default);

        // Once the window has fully rolled past both, two more slots are free immediately.
        await Task.Delay(window + TimeSpan.FromMilliseconds(80));

        Assert.Equal(TimeSpan.Zero, await limiter.AcquireAsync(default));
        Assert.Equal(TimeSpan.Zero, await limiter.AcquireAsync(default));
    }

    [Fact]
    public async Task Concurrent_callers_never_exceed_the_limit_in_one_window()
    {
        const int limit = 4;
        var window = TimeSpan.FromMilliseconds(500);
        var limiter = new RateLimiter(limit, window);
        var clock = Stopwatch.StartNew();
        var issued = new System.Collections.Concurrent.ConcurrentBag<TimeSpan>();

        // Both channel pipelines share one transcriber, so contention is the normal case.
        await Task.WhenAll(Enumerable.Range(0, limit * 2).Select(async _ =>
        {
            await limiter.AcquireAsync(default);
            issued.Add(clock.Elapsed);
        }));

        var early = issued.Count(at => at < window * 0.7);
        Assert.True(early <= limit, $"{early} requests went out in the first window, cap is {limit}");
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_a_slot_propagates()
    {
        var limiter = new RateLimiter(1, TimeSpan.FromSeconds(30));
        await limiter.AcquireAsync(default);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.AcquireAsync(cts.Token));
    }

    [Theory]
    [InlineData(20, 20, 0)]   // fits in one window
    [InlineData(20, 21, 1)]   // one over spills into a second window
    [InlineData(20, 77, 3)]   // the four-config sweep that started all this
    public void Estimate_reports_the_windows_a_batch_will_span(int limit, int requests, int expectedMinutes)
    {
        var limiter = new RateLimiter(limit);
        Assert.Equal(expectedMinutes, (int)limiter.EstimateFor(requests).TotalMinutes);
    }
}
