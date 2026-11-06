using System.Net;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

/// <summary>The checklist's K7: a failed sign-in must not carry the password into an error.</summary>
public class SecretProofTests
{
    private const string Password = "SECRET-PASSWORD-1234567890";

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_sign_in_leaves_the_password_out_of_whatever_it_throws(HttpStatusCode code)
    {
        var handler = new FakeHttpHandler(
            noncePage: new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<input name=\"_wpnonce\" value=\"abc123\"/>") },
            loginResponse: new HttpResponseMessage(code));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.fanedit.org") };
        var auth = new FanEditAuthService(http, new CookieContainer(), new FanEditRateLimiter(1000));

        var ex = await Record.ExceptionAsync(async () =>
        {
            var ok = await auth.EnsureSessionAsync("user", Password, CancellationToken.None);
            Assert.False(ok);
        });
        if (ex is not null) Assert.DoesNotContain(Password, ex.ToString());
    }
}
