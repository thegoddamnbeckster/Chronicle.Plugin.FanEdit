using Chronicle.Plugin.FanEdit;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditMetadataProviderTests
{
    [Fact]
    public void GetSupportedMediaTypes_ReturnsFaneditsOnly()
    {
        var provider = new FanEditMetadataProvider();
        var types    = provider.GetSupportedMediaTypes();

        types.Should().HaveCount(1);
        types[0].MediaTypeName.Should().Be("fanedits");
    }

    [Fact]
    public void GetSettingsSchema_ContainsRequiredKeys()
    {
        var schema = new FanEditMetadataProvider().GetSettingsSchema();
        var keys   = schema.Settings.Select(s => s.Key).ToList();

        keys.Should().Contain("username");
        keys.Should().Contain("password");
        keys.Should().Contain("request_delay_ms");
    }

    [Fact]
    public void PluginId_IsCorrect()
    {
        new FanEditMetadataProvider().PluginId.Should().Be("chronicle.plugin.fanedit");
    }

    [Fact]
    public async Task SearchAsync_ThrowsInvalidOperation_WhenNotConfigured()
    {
        var provider = new FanEditMetadataProvider();
        var ctx      = new MediaSearchContext("Blade Runner");
        var act      = () => provider.SearchAsync(ctx);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    // ── Rate-limit / backoff (GetWithRateLimitAsync via GetImageAsync) ────────

    private static HttpResponseMessage RateLimited(HttpStatusCode status)
    {
        var resp = new HttpResponseMessage(status);
        // Explicit short Retry-After so tests don't wait on GetWithRateLimitAsync's
        // request_delay_ms*3 fallback (3s at the limiter's floor) — matches TmdbClientTests'
        // pattern of forcing a fast, deterministic wait.
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return resp;
    }

    [Fact]
    public async Task GetImageAsync_429ThenSuccess_RetriesOnceAndReturnsResult()
    {
        var handler = new SequencedHttpHandler(
            RateLimited(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        var provider = new FanEditMetadataProvider();
        provider.ConfigureForTesting(new HttpClient(handler), new FanEditRateLimiter(1));

        var bytes = await provider.GetImageAsync("https://fanedit.org/image.jpg");

        bytes.Should().Equal([1, 2, 3]);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetImageAsync_503ThenSuccess_RetriesOnceAndReturnsResult()
    {
        var handler = new SequencedHttpHandler(
            RateLimited(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) });

        var provider = new FanEditMetadataProvider();
        provider.ConfigureForTesting(new HttpClient(handler), new FanEditRateLimiter(1));

        var bytes = await provider.GetImageAsync("https://fanedit.org/image.jpg");

        bytes.Should().Equal([9]);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetImageAsync_PersistentRateLimit_StopsAfterOneRetryAndThrows()
    {
        var handler = new SequencedHttpHandler(
            RateLimited(HttpStatusCode.TooManyRequests),
            RateLimited(HttpStatusCode.TooManyRequests));

        var provider = new FanEditMetadataProvider();
        provider.ConfigureForTesting(new HttpClient(handler), new FanEditRateLimiter(1));

        var act = () => provider.GetImageAsync("https://fanedit.org/image.jpg");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetImageAsync_NoRateLimit_MakesExactlyOneCall()
    {
        var handler = new SequencedHttpHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7]) });

        var provider = new FanEditMetadataProvider();
        provider.ConfigureForTesting(new HttpClient(handler), new FanEditRateLimiter(1));

        var bytes = await provider.GetImageAsync("https://fanedit.org/image.jpg");

        bytes.Should().Equal([7]);
        handler.CallCount.Should().Be(1);
    }
}

/// <summary>Returns queued responses in order, repeating the last one once exhausted.</summary>
internal sealed class SequencedHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    public int CallCount { get; private set; }

    public SequencedHttpHandler(params HttpResponseMessage[] responses)
        => _responses = new Queue<HttpResponseMessage>(responses);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        var resp = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return Task.FromResult(resp);
    }
}
