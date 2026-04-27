using Chronicle.Plugin.FanEdit.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using System.Net;
using System.Text.Json;

namespace Chronicle.Plugin.FanEdit;

/// <summary>
/// IMetadataProvider implementation for fanedit.org (IFDB).
/// Supports media type "fanedits" only.
/// </summary>
public sealed class FanEditMetadataProvider : IMetadataProvider
{
    private const string BaseUrl       = "https://www.fanedit.org";
    private const int    ScoreThreshold = 50;

    private string?             _username;
    private string?             _password;
    private FanEditRateLimiter? _limiter;
    private FanEditAuthService? _auth;
    private FanEditScraper?     _scraper;
    private HttpClient?         _http;

    // ── Identity ──────────────────────────────────────────────────────────
    public string PluginId => "chronicle.plugin.fanedit";
    public string Name     => "FanEdit";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    // ── Capabilities ──────────────────────────────────────────────────────
    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "fanedits",
            DisplayName     = "Fan Edits",
            HierarchyLevels = 1,
            DefaultPriority = 10,
            SupportedFields = ["title", "overview", "year", "poster_url", "backdrop_url",
                               "runtime_minutes", "genres", "cast", "directors", "rating", "tags"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key      = "username",
                Label    = "fanedit.org Username",
                Type     = SettingType.Text,
                Required = true,
            },
            new SettingDefinition
            {
                Key      = "password",
                Label    = "fanedit.org Password",
                Type     = SettingType.Password,
                Required = true,
            },
            new SettingDefinition
            {
                Key          = "request_delay_ms",
                Label        = "Request Delay (ms)",
                Description  = "Minimum delay between requests. Floor: 1000 ms. Be kind to the server.",
                Type         = SettingType.Number,
                Required     = false,
                DefaultValue = "1000",
            },
            new SettingDefinition
            {
                Key          = "user_agent",
                Label        = "User-Agent String",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            },
        ]
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _username = settings.GetValueOrDefault("username");
        _password = settings.GetValueOrDefault("password");

        var delayMs = settings.TryGetValue("request_delay_ms", out var d) && int.TryParse(d, out var di) ? di : 1000;
        var ua      = settings.GetValueOrDefault("user_agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        _limiter = new FanEditRateLimiter(delayMs);
        _scraper = new FanEditScraper();

        var cookies = new CookieContainer();
        var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        _http       = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("User-Agent", ua);
        _auth = new FanEditAuthService(_http, cookies, _limiter);
    }

    // ── Core operations ───────────────────────────────────────────────────
    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        await EnsureSessionAsync(ct);

        var titlesToTry = new List<string> { context.Name };
        if (context.AltTitles is { Count: > 0 })
            titlesToTry.AddRange(context.AltTitles);

        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ScoredCandidate>();

        foreach (var title in titlesToTry.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var searchUrl = $"{BaseUrl}/ifdb/?s={Uri.EscapeDataString(title)}&post_type=fanedit";
            await _limiter!.ThrottleAsync(ct);
            var resp = await _http!.GetAsync(searchUrl, ct);
            resp.EnsureSuccessStatusCode();
            var html    = await resp.Content.ReadAsStringAsync(ct);
            var results = _scraper!.ParseSearchResults(html);

            foreach (var r in results)
            {
                if (!seen.Add(r.Url)) continue;
                var score = ScoreSearchResult(context, r);
                if (score >= ScoreThreshold)
                    candidates.Add(new ScoredCandidate(
                        Metadata: new MediaMetadata
                        {
                            Title      = r.Title,
                            Year       = r.Year,
                            PosterUrl  = r.ThumbnailUrl,
                            ExternalId = UrlToExternalId(r.Url),
                        },
                        Score: score
                    ));
            }
        }

        return candidates.OrderByDescending(c => c.Score).Take(10).ToList();
    }

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();
        await EnsureSessionAsync(ct);

        var url = ResolveUrl(externalId);
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);

        if (FanEditAuthService.IsSessionExpiredResponse(resp))
        {
            // Re-authenticate once and retry
            await _auth!.EnsureSessionAsync(_username!, _password!, ct);
            await _limiter.ThrottleAsync(ct);
            resp = await _http.GetAsync(url, ct);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"No IFDB entry found at {url}");

        resp.EnsureSuccessStatusCode();
        var html  = await resp.Content.ReadAsStringAsync(ct);
        var entry = _scraper!.ParseDetailPage(html, url);

        return MapToMetadata(entry, url);
    }

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_auth is null || _username is null || _password is null) return false;
        try { return await _auth.EnsureSessionAsync(_username, _password, ct); }
        catch { return false; }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (_limiter is null)
            throw new InvalidOperationException(
                "FanEditMetadataProvider is not configured. Call Configure() first.");
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (!_auth!.IsSessionEstablished)
        {
            var ok = await _auth.EnsureSessionAsync(_username!, _password!, ct);
            if (!ok)
                throw new InvalidOperationException(
                    "Could not log in to fanedit.org. Check your username and password in plugin settings.");
        }
    }

    private static int ScoreSearchResult(MediaSearchContext ctx, FanEditSearchResult r)
    {
        var score = 0;
        var norm  = r.Title.ToLowerInvariant().Trim();
        var query = ctx.Name.ToLowerInvariant().Trim();

        if (norm == query)                              score += 40;
        else if (LevenshteinRatio(norm, query) <= 0.2) score += 20;

        if (ctx.Year.HasValue && r.Year.HasValue)
        {
            var diff = Math.Abs(ctx.Year.Value - r.Year.Value);
            if (diff == 0)      score += 20;
            else if (diff == 1) score += 10;
            else                score -= 10;
        }

        return score;
    }

    private static double LevenshteinRatio(string a, string b)
    {
        if (a == b) return 0;
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0;
        return (double)LevenshteinDistance(a, b) / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[a.Length, b.Length];
    }

    private static string UrlToExternalId(string url)
    {
        // https://www.fanedit.org/blade-runner-the-final-edit/ → fanedit:blade-runner-the-final-edit
        var slug = url.TrimEnd('/').Split('/').LastOrDefault() ?? url;
        return $"fanedit:{slug}";
    }

    private static string ResolveUrl(string externalId)
    {
        if (externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return externalId;
        if (externalId.StartsWith("fanedit:", StringComparison.OrdinalIgnoreCase))
        {
            var id = externalId["fanedit:".Length..];
            return int.TryParse(id, out _)
                ? $"{BaseUrl}/ifdb/{id}/"
                : $"{BaseUrl}/{id}/";
        }
        return int.TryParse(externalId, out _)
            ? $"{BaseUrl}/ifdb/{externalId}/"
            : $"{BaseUrl}/{externalId}/";
    }

    private static MediaMetadata MapToMetadata(FanEditEntry entry, string url)
    {
        var extData = new Dictionary<string, object?>
        {
            ["originalTitle"]     = entry.OriginalTitle,
            ["originalYear"]      = entry.OriginalYear,
            ["originalImdbId"]    = entry.OriginalImdbId,
            ["editorUsername"]    = entry.EditorUsername,
            ["editorProfileUrl"]  = entry.EditorProfileUrl,
            ["fanEditType"]       = entry.FanEditType,
            ["ifdbCategories"]    = entry.IfdbCategories,
            ["techSpecs"]         = entry.TechSpecs is null ? null : new
            {
                videoCodec      = entry.TechSpecs.VideoCodec,
                audioCodec      = entry.TechSpecs.AudioCodec,
                resolution      = entry.TechSpecs.Resolution,
                aspectRatio     = entry.TechSpecs.AspectRatio,
                containerFormat = entry.TechSpecs.ContainerFormat,
                fileSizeGb      = entry.TechSpecs.FileSizeGb,
            },
            ["changesList"]       = entry.ChangesList,
            ["numberOfCuts"]      = entry.NumberOfCuts,
            ["numberOfAdditions"] = entry.NumberOfAdditions,
            ["ifdbRatingRaw"]     = entry.IfdbRatingRaw,
            ["ifdbRatingCount"]   = entry.IfdbRatingCount,
            ["ifdbAwards"]        = entry.IfdbAwards,
            ["ifdbId"]            = entry.IfdbId,
            ["ifdbUrl"]           = url,
            ["ifdbPublishedDate"] = entry.IfdbPublishedDate,
            ["distributionLinks"] = entry.DistributionLinks,
        };

        return new MediaMetadata
        {
            Title            = entry.Title,
            Overview         = entry.Overview,
            Year             = entry.Year,
            RuntimeMinutes   = entry.RuntimeMinutes,
            PosterUrl        = entry.PosterUrl,
            Genres           = entry.Genres,
            Rating           = entry.Rating,
            Tags             = entry.Tags,
            ExternalId       = UrlToExternalId(url),
            ExtendedData     = JsonSerializer.SerializeToElement(extData),
            AdditionalImages = entry.AdditionalImages
                .Select(u => new AdditionalImage { Url = u, Type = "Screenshot" })
                .ToList(),
        };
    }
}
