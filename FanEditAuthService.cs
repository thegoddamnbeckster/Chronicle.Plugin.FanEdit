using HtmlAgilityPack;

namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// Handles WordPress form login against fanedit.org and maintains the session cookie.
/// </summary>
internal sealed class FanEditAuthService
{
    private const string LoginUrl   = "https://www.fanedit.org/wp-login.php";
    private const string CookieName = "wordpress_logged_in_";

    private readonly HttpClient         _http;
    private readonly System.Net.CookieContainer    _cookies;
    private readonly FanEditRateLimiter _limiter;

    /// <summary>How long every caller is held off once fanedit.org has answered with a human-verification check.</summary>
    internal static readonly TimeSpan HumanCheckHold = TimeSpan.FromHours(1);

    private DateTime _humanCheckUntilUtc = DateTime.MinValue;

    public bool IsSessionEstablished { get; private set; }

    /// <summary>True while fanedit.org's last answer was a human-verification page (Cloudflare "Just a moment..."). Chronicle
    /// cannot pass that check and does not try to: it stops sending requests for <see cref="HumanCheckHold"/> and says so, instead of
    /// reporting a wrong password.</summary>
    public bool HumanCheckActive => DateTime.UtcNow < _humanCheckUntilUtc;

    public FanEditAuthService(HttpClient http, System.Net.CookieContainer cookies, FanEditRateLimiter limiter)
    {
        _http    = http;
        _cookies = cookies;
        _limiter = limiter;
    }

    /// <summary>
    /// Attempts to log in. Returns true if a WordPress session cookie is obtained.
    /// </summary>
    public async Task<bool> EnsureSessionAsync(string username, string password, CancellationToken ct)
    {
        IsSessionEstablished = false;

        // A human check was shown a short while ago: send nothing more until the hold is over.
        if (HumanCheckActive) return false;

        // Step 1: fetch login page to extract _wpnonce
        await _limiter.ThrottleAsync(ct);
        var nonceResp = await _http.GetAsync(LoginUrl, ct);
        var nonceHtml = await nonceResp.Content.ReadAsStringAsync(ct);
        if (IsHumanCheck(nonceResp, nonceHtml))
        {
            _humanCheckUntilUtc = DateTime.UtcNow + HumanCheckHold;
            return false;
        }
        var nonce     = ExtractNonce(nonceHtml);

        // Step 2: POST credentials
        await _limiter.ThrottleAsync(ct);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["log"]         = username,
            ["pwd"]         = password,
            ["wp-submit"]   = "Log In",
            ["redirect_to"] = "/",
            ["testcookie"]  = "1",
            ["_wpnonce"]    = nonce ?? string.Empty,
        });

        await _http.PostAsync(LoginUrl, form, ct);

        // With AllowAutoRedirect=true the login 302 is followed automatically.
        // WordPress sets the session cookie on the redirect response; HttpClientHandler
        // stores it in the CookieContainer rather than exposing it in the final response headers.
        var siteUri = new Uri("https://www.fanedit.org/");
        IsSessionEstablished = _cookies.GetCookies(siteUri)
            .Cast<System.Net.Cookie>()
            .Any(c => c.Name.StartsWith(CookieName, StringComparison.OrdinalIgnoreCase));

        return IsSessionEstablished;
    }

    /// <summary>Returns true when a response landed on the login page (session expired).</summary>
    public static bool IsSessionExpiredResponse(HttpResponseMessage response)
    {
        // With AllowAutoRedirect=true the final RequestUri shows where we ended up.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
        return finalUrl.Contains("wp-login.php", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the answer is a bot-protection challenge page rather than the site's own: Cloudflare marks it with
    /// <c>Cf-Mitigated: challenge</c> and serves "Just a moment..." (HTTP 403 or 503).</summary>
    internal static bool IsHumanCheck(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("Cf-Mitigated", out var mitigated) &&
            mitigated.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            return true;
        var blocked = response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.ServiceUnavailable;
        return blocked && (body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                           body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractNonce(string html)
    {
        var doc  = new HtmlDocument();
        doc.LoadHtml(html);
        var node = doc.DocumentNode.SelectSingleNode("//input[@name='_wpnonce']");
        return node?.GetAttributeValue("value", null);
    }
}
