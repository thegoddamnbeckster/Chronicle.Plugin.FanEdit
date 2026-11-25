using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using System.Net;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditAuthServiceTests
{
    private static HttpClient MakeClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://www.fanedit.org") };

    [Fact]
    public async Task EnsureSessionAsync_ReturnsFalse_WhenLoginResponseMissesCookie()
    {
        var handler = new FakeHttpHandler(loginResponse: new HttpResponseMessage(HttpStatusCode.OK)
        {
            // No Set-Cookie header
        });
        var cookies = new CookieContainer();
        var auth = new FanEditAuthService(MakeClient(handler), cookies, new FanEditRateLimiter(1000));

        var result = await auth.EnsureSessionAsync("user", "pass", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureSessionAsync_ReturnsTrue_WhenLoginSetsWordPressLoggedInCookie()
    {
        var handler = new FakeHttpHandler(
            noncePage: new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<input name=\"_wpnonce\" value=\"abc123\"/>") },
            loginResponse: new HttpResponseMessage(HttpStatusCode.OK));

        var cookies = new CookieContainer();
        // EnsureSessionAsync reads the session cookie from the CookieContainer, not from
        // response headers -- with AllowAutoRedirect=true a real HttpClientHandler stores
        // Set-Cookie there automatically (see that method's own comment). FakeHttpHandler is
        // a raw HttpMessageHandler and bypasses that machinery, so the test seeds the
        // container directly to simulate what the real handler would already have done.
        cookies.Add(new Uri("https://www.fanedit.org/"),
            new Cookie("wordpress_logged_in_abc", "value") { HttpOnly = true });

        var auth = new FanEditAuthService(MakeClient(handler), cookies, new FanEditRateLimiter(1000));

        var result = await auth.EnsureSessionAsync("user", "pass", CancellationToken.None);

        result.Should().BeTrue();
        auth.IsSessionEstablished.Should().BeTrue();
    }

    private static HttpResponseMessage CloudflareChallenge()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("<html><title>Just a moment...</title><script src=\"/cdn-cgi/challenge-platform/h/b\"></script></html>") };
        r.Headers.Add("Cf-Mitigated", "challenge");
        return r;
    }

    [Fact]
    public async Task EnsureSessionAsync_StopsAtAHumanCheck_SendsNoCredentials_AndHoldsFurtherRequests()
    {
        var handler = new FakeHttpHandler(noncePage: CloudflareChallenge());
        var auth = new FanEditAuthService(MakeClient(handler), new CookieContainer(), new FanEditRateLimiter(1000));

        (await auth.EnsureSessionAsync("user", "pass", CancellationToken.None)).Should().BeFalse();
        auth.HumanCheckActive.Should().BeTrue();
        handler.CallCount.Should().Be(1);                       // the login page only: no credentials were posted

        (await auth.EnsureSessionAsync("user", "pass", CancellationToken.None)).Should().BeFalse();
        handler.CallCount.Should().Be(1);                       // and nothing at all while the hold lasts
    }

    [Fact]
    public async Task EnsureSessionAsync_ANormalFailure_IsNotMistakenForAHumanCheck()
    {
        var handler = new FakeHttpHandler(loginResponse: new HttpResponseMessage(HttpStatusCode.OK));
        var auth = new FanEditAuthService(MakeClient(handler), new CookieContainer(), new FanEditRateLimiter(1000));

        (await auth.EnsureSessionAsync("user", "wrong", CancellationToken.None)).Should().BeFalse();
        auth.HumanCheckActive.Should().BeFalse();
        handler.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "<title>Just a moment...</title>", false, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<script src=\"/cdn-cgi/challenge-platform/x\"></script>", false, true)]
    [InlineData(HttpStatusCode.OK, "<form id=\"loginform\"></form>", false, false)]
    [InlineData(HttpStatusCode.Forbidden, "Forbidden by the site rules", false, false)]
    [InlineData(HttpStatusCode.OK, "<form id=\"loginform\"></form>", true, true)]
    public void IsHumanCheck_RecognisesCloudflaresChallengeOnly(HttpStatusCode status, string body, bool header, bool expected)
    {
        var resp = new HttpResponseMessage(status);
        if (header) resp.Headers.Add("Cf-Mitigated", "challenge");
        FanEditAuthService.IsHumanCheck(resp, body).Should().Be(expected);
    }

    [Fact]
    public void IsSessionExpired_ReturnsTrue_WhenResponseRedirectsToLogin()
    {
        // IsSessionExpiredResponse checks the final RequestUri (where AllowAutoRedirect=true
        // already landed), not a raw 302's Location header -- see its own comment.
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://www.fanedit.org/wp-login.php"),
        };

        FanEditAuthService.IsSessionExpiredResponse(resp).Should().BeTrue();
    }
}

// Minimal fake HTTP handler for testing
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _noncePage;
    private readonly HttpResponseMessage _loginResponse;
    private int _callCount;
    public int CallCount => _callCount;

    public FakeHttpHandler(
        HttpResponseMessage? noncePage = null,
        HttpResponseMessage? loginResponse = null)
    {
        _noncePage     = noncePage ?? new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<input name=\"_wpnonce\" value=\"nonce\"/>") };
        _loginResponse = loginResponse ?? new HttpResponseMessage(HttpStatusCode.OK);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        _callCount++;
        // First call = nonce page GET, second = login POST
        return Task.FromResult(_callCount == 1 ? _noncePage! : _loginResponse);
    }
}
