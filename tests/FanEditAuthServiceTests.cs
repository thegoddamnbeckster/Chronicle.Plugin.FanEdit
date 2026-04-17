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
        var loginResp = new HttpResponseMessage(HttpStatusCode.Found);
        loginResp.Headers.Add("Set-Cookie",
            "wordpress_logged_in_abc=value; Path=/; HttpOnly");

        var handler = new FakeHttpHandler(
            noncePage: new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("<input name=\"_wpnonce\" value=\"abc123\"/>") },
            loginResponse: loginResp);

        var cookies = new CookieContainer();
        var auth = new FanEditAuthService(MakeClient(handler), cookies, new FanEditRateLimiter(1000));

        var result = await auth.EnsureSessionAsync("user", "pass", CancellationToken.None);

        result.Should().BeTrue();
        auth.IsSessionEstablished.Should().BeTrue();
    }

    [Fact]
    public void IsSessionExpired_ReturnsTrue_WhenResponseRedirectsToLogin()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.Location = new Uri("https://www.fanedit.org/wp-login.php");

        FanEditAuthService.IsSessionExpiredResponse(resp).Should().BeTrue();
    }
}

// Minimal fake HTTP handler for testing
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage? _noncePage;
    private readonly HttpResponseMessage _loginResponse;
    private int _callCount;

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
