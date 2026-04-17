namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// Serialises all outbound HTTP requests to fanedit.org with a minimum
/// inter-request delay. The 1,000 ms floor is hard-coded and cannot be
/// reduced via configuration.
/// </summary>
internal sealed class FanEditRateLimiter
{
    private const int FloorMs = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Diagnostics.Stopwatch _last = System.Diagnostics.Stopwatch.StartNew();

    public int DelayMs { get; }

    public FanEditRateLimiter(int delayMs = FloorMs)
    {
        DelayMs = Math.Max(delayMs, FloorMs);
    }

    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = _last.ElapsedMilliseconds;
            if (elapsed < DelayMs)
                await Task.Delay((int)(DelayMs - elapsed), ct);
            _last.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }
}
