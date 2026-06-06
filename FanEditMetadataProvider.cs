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
    private const string BaseUrl        = "https://fanedit.org";
    private const string SearchBase    = "https://fanedit.org/fanedit-search/tag/originalmovietitle";
    private const int    ScoreThreshold = 25;

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
        var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
        _http       = new HttpClient(handler);
        _http.DefaultRequestHeaders.Add("User-Agent", ua);
        _auth = new FanEditAuthService(_http, cookies, _limiter);
    }

    // ── Core operations ───────────────────────────────────────────────────
    private static readonly System.Text.RegularExpressions.Regex _trailingYear =
        new(@"\s*\(\d{4}\)\s*$");
    private static readonly System.Text.RegularExpressions.Regex _slugNoise =
        new(@"[^a-z0-9\s-]");
    // Primary separator patterns — anything to the right of these is "edit name", not movie title.
    private static readonly System.Text.RegularExpressions.Regex _editSeparator =
        new(@"\s+[-–—:]\s+|\s*:\s*");

    // IFDB often uses full canonical episode titles rather than short franchise names.
    // This map expands short slugs we'd generate naturally to the IFDB canonical forms.
    private static readonly Dictionary<string, string[]> _slugExpansions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["star-wars"]                  = ["star-wars-episode-iv-a-new-hope", "star-wars-a-new-hope"],
            ["the-empire-strikes-back"]    = ["star-wars-episode-v-the-empire-strikes-back"],
            ["empire-strikes-back"]        = ["star-wars-episode-v-the-empire-strikes-back"],
            ["return-of-the-jedi"]         = ["star-wars-episode-vi-return-of-the-jedi"],
            ["the-phantom-menace"]         = ["star-wars-episode-i-the-phantom-menace"],
            ["attack-of-the-clones"]       = ["star-wars-episode-ii-attack-of-the-clones"],
            ["revenge-of-the-sith"]        = ["star-wars-episode-iii-revenge-of-the-sith"],
            ["the-force-awakens"]          = ["star-wars-the-force-awakens", "star-wars-episode-vii-the-force-awakens"],
            ["the-last-jedi"]              = ["star-wars-the-last-jedi", "star-wars-episode-viii-the-last-jedi"],
            ["the-rise-of-skywalker"]      = ["star-wars-the-rise-of-skywalker", "star-wars-episode-ix-the-rise-of-skywalker"],
        };

    /// <summary>
    /// Generates JReviews originalmovietitle slug candidates from a raw title.
    /// "Alien - Darksteel Cut (2023)" → ["alien", "alien-darksteel-cut", "alien-darksteel"]
    /// </summary>
    private static List<string> BuildSlugCandidates(string rawTitle)
    {
        var clean = _trailingYear.Replace(rawTitle, "").Trim();
        var candidates = new List<string>();

        // First candidate: part before the first edit separator (likely the original movie title).
        // Also add the article-toggled variant because IFDB tags vary — some use "the-empire-strikes-back",
        // others use "empire-strikes-back", and we can't know which without fetching.
        var separatorMatch = _editSeparator.Match(clean);
        if (separatorMatch.Success && separatorMatch.Index > 0)
        {
            var prefix = clean[..separatorMatch.Index].Trim();
            var prefixSlug = ToSlug(prefix);
            candidates.Add(prefixSlug);
            candidates.Add(ToggleLeadingArticle(prefixSlug));

            // Inject canonical IFDB episode titles for known franchise short slugs.
            foreach (var slug in new[] { prefixSlug, ToggleLeadingArticle(prefixSlug) })
            {
                if (_slugExpansions.TryGetValue(slug, out var expansions))
                    foreach (var exp in expansions)
                        if (!candidates.Contains(exp, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(exp);
            }
        }

        // Full cleaned title as a slug — covers edits that don't use a separator.
        var fullSlug = ToSlug(clean);
        if (!candidates.Contains(fullSlug, StringComparer.OrdinalIgnoreCase))
            candidates.Add(fullSlug);

        // Progressively shorter word groups from the full title.
        var words = fullSlug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (var take = words.Length - 1; take >= 1; take--)
        {
            var shorter = string.Join("-", words[..take]);
            if (!candidates.Contains(shorter, StringComparer.OrdinalIgnoreCase))
                candidates.Add(shorter);
        }

        // After truncation, inject canonical expansions for any franchise short slugs that landed
        // in the list (covers titles with no separator like "The Empire Strikes Back Revisited").
        var expansionsToAdd = new List<string>();
        foreach (var c in candidates.ToList())
            if (_slugExpansions.TryGetValue(c, out var exps))
                foreach (var exp in exps)
                    if (!candidates.Contains(exp, StringComparer.OrdinalIgnoreCase) &&
                        !expansionsToAdd.Contains(exp, StringComparer.OrdinalIgnoreCase))
                        expansionsToAdd.Add(exp);
        candidates.AddRange(expansionsToAdd);

        return candidates;
    }

    /// <summary>
    /// Returns the slug with its leading article toggled.
    /// "the-empire-strikes-back" → "empire-strikes-back"
    /// "empire-strikes-back"     → "the-empire-strikes-back"
    /// </summary>
    private static string ToggleLeadingArticle(string slug)
    {
        foreach (var article in new[] { "the-", "a-", "an-" })
        {
            if (slug.StartsWith(article, StringComparison.Ordinal))
                return slug[article.Length..];
        }
        return "the-" + slug;
    }

    private static string ToSlug(string text)
    {
        text = text.ToLowerInvariant();
        text = _slugNoise.Replace(text, " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "-").Trim('-');
        return text;
    }

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        await EnsureSessionAsync(ct);

        var rawTitles = new List<string> { context.Name };
        if (context.AltTitles is { Count: > 0 })
            rawTitles.AddRange(context.AltTitles);

        // Expand each raw title into JReviews originalmovietitle slug candidates.
        var slugsToTry = rawTitles
            .SelectMany(BuildSlugCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ScoredCandidate>();

        foreach (var slug in slugsToTry)
        {
            if (candidates.Count >= 5) break; // enough results, stop searching

            await _limiter!.ThrottleAsync(ct);
            var url  = $"{SearchBase}/{slug}/?criteria=2";
            var resp = await _http!.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;

            // Session may expire server-side mid-batch; a redirect to wp-login.php
            // returns 200 but contains no fan-edit listings.
            if (FanEditAuthService.IsSessionExpiredResponse(resp))
            {
                await _auth!.EnsureSessionAsync(_username!, _password!, ct);
                await _limiter.ThrottleAsync(ct);
                resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
            }

            var html    = await resp.Content.ReadAsStringAsync(ct);
            var results = _scraper!.ParseSearchResults(html);

            // JReviews redirects directly to the fan-edit detail page when a tag has
            // exactly one match (HttpClient follows the redirect transparently, so
            // resp.RequestMessage.RequestUri is still the original URL — use the
            // canonical <link> in the HTML to detect where we actually landed).
            if (results.Count == 0)
            {
                var canonicalUrl = FanEditScraper.GetCanonicalUrl(html);
                if (!string.IsNullOrWhiteSpace(canonicalUrl) &&
                    !canonicalUrl.Equals(url, StringComparison.OrdinalIgnoreCase) &&
                    !FanEditAuthService.IsSessionExpiredResponse(resp) &&
                    canonicalUrl.Contains("fanedit.org", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var entry = _scraper!.ParseDetailPage(html, canonicalUrl);
                        results = [new FanEditSearchResult
                        {
                            Title        = entry.Title ?? string.Empty,
                            Year         = entry.Year,
                            ThumbnailUrl = entry.PosterUrl,
                            Url          = canonicalUrl,
                        }];
                    }
                    catch { /* ignore — page wasn't parseable as a detail page */ }
                }
                if (results.Count == 0) continue;
            }

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

            // Stop early only on a high-confidence match; a single marginal hit should not
            // prevent more-specific slug candidates (e.g. "the-empire-strikes-back") from running.
            if (candidates.Any(c => c.Score >= 60) || candidates.Count >= 3) break;
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

        // Normalise both sides: strip punctuation, lowercase, collapse spaces.
        var norm  = NormaliseForScore(r.Title);
        var query = NormaliseForScore(_trailingYear.Replace(ctx.Name, ""));

        if (norm == query)
            score += 60;
        else if (norm.Length > 0 && query.Length > 0 && (norm.Contains(query) || query.Contains(norm)))
            score += 40;
        else if (LevenshteinRatio(norm, query) <= 0.3)
            score += 30;
        else
        {
            // Partial word overlap: count shared words
            var queryWords  = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var normWords   = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var shared      = queryWords.Count(w => normWords.Contains(w));
            if (shared > 0)
                score += Math.Min(30, shared * 10);
        }

        if (ctx.Year.HasValue && r.Year.HasValue)
        {
            var diff = Math.Abs(ctx.Year.Value - r.Year.Value);
            if (diff == 0)      score += 20;
            else if (diff == 1) score += 10;
            else                score -= 10;
        }

        return score;
    }

    private static string NormaliseForScore(string s)
    {
        s = _trailingYear.Replace(s, "");
        s = s.ToLowerInvariant();
        s = _slugNoise.Replace(s, " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.Trim();
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
        {
            // Validate host before using the URL — prevent SSRF by ensuring only
            // fanedit.org URLs are fetched. Any other host is rejected outright.
            if (!Uri.TryCreate(externalId, UriKind.Absolute, out var uri) ||
                (!uri.Host.Equals("www.fanedit.org", StringComparison.OrdinalIgnoreCase) &&
                 !uri.Host.Equals("fanedit.org",     StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"URL must be a fanedit.org address: '{externalId}'");
            return externalId;
        }
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
