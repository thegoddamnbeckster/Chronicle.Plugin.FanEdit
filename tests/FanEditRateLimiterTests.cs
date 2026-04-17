using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditRateLimiterTests
{
    [Fact]
    public async Task ThrottleAsync_EnforcesMinimumDelay()
    {
        var limiter = new FanEditRateLimiter(delayMs: 200);
        var sw = Stopwatch.StartNew();

        await limiter.ThrottleAsync(CancellationToken.None); // first call — no wait
        await limiter.ThrottleAsync(CancellationToken.None); // second — must wait ~200ms (clamped to 1000ms)

        sw.Elapsed.TotalMilliseconds.Should().BeGreaterThan(800);
    }

    [Fact]
    public void Constructor_ClampsDelayToFloor()
    {
        // Cannot set below 1000ms floor
        var limiter = new FanEditRateLimiter(delayMs: 100);
        limiter.DelayMs.Should().Be(1000);
    }

    [Fact]
    public async Task ThrottleAsync_RespectsCancellation()
    {
        var limiter = new FanEditRateLimiter(delayMs: 5000);
        await limiter.ThrottleAsync(CancellationToken.None); // seed last-request time

        using var cts = new CancellationTokenSource(50);
        var act = () => limiter.ThrottleAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
