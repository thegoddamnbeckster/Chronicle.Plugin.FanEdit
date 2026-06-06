using HtmlAgilityPack;
using Chronicle.Plugin.FanEdit.Models;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.FanEdit;

internal sealed class FanEditScraper
{
    private static readonly Regex _ratingRegex    = new(@"([\d.]+)\s*\((\d+)\s*vote", RegexOptions.IgnoreCase);
    private static readonly Regex _numberRegex    = new(@"(\d+)");
    private static readonly Regex _fileSizeRegex  = new(@"([\d.]+)\s*(GB|MB)", RegexOptions.IgnoreCase);
    private static readonly Regex _imdbIdRegex    = new(@"tt\d+", RegexOptions.IgnoreCase);
    private static readonly Regex _yearRegex      = new(@"\b(19|20)\d{2}\b");
    private static readonly Regex _ifdbNumericId  = new(@"/ifdb/(\d+)/?");

    // ── Search results ────────────────────────────────────────────────────────

    // Matches fanedit.org post permalink slugs: /some-slug/ at the root (no subdirectory).
    // Excludes wp-admin, wp-content, category, tag, author, page, search paths.
    private static readonly Regex _postSlugPattern = new(
        @"^https?://(?:www\.)?fanedit\.org/([a-z0-9][a-z0-9\-]+[a-z0-9])/?$",
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> _skipSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "wp-admin", "wp-content", "wp-login.php", "feed", "sitemap", "category",
        "tag", "author", "page", "search", "about", "contact", "register",
        "ifdb", "forum", "forums", "shop", "news", "members", "faq", "privacy-policy",
        "terms", "terms-of-service", "cookie-policy", "rules", "guidelines",
    };

    /// <summary>
    /// Returns true when the URL is a root-level fanedit.org post permalink
    /// (e.g. https://fanedit.org/some-slug/), not a search/tag/category URL.
    /// </summary>
    public static bool IsDetailPageUrl(string url) => _postSlugPattern.IsMatch(url);

    /// <summary>
    /// Returns the canonical URL embedded in the page, or null if not present.
    /// Used to detect single-result JReviews tag redirects that land on a detail page.
    /// </summary>
    public static string? GetCanonicalUrl(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode
            .SelectSingleNode("//link[@rel='canonical']")
            ?.GetAttributeValue("href", null);
    }

    public List<FanEditSearchResult> ParseSearchResults(string html)
    {
        var doc     = new HtmlDocument();
        doc.LoadHtml(html);
        var results = new List<FanEditSearchResult>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Strategy 0: JReviews listing structure (fanedit-search tag pages) ─
        // e.g. fanedit.org/fanedit-search/tag/originalmovietitle/alien/
        var jrTitles = doc.DocumentNode.SelectNodes("//*[contains(@class,'jrListingTitle')]//a[@href]");
        if (jrTitles is not null)
        {
            foreach (var a in jrTitles)
            {
                var href = a.GetAttributeValue("href", string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(href) || !seen.Add(href)) continue;

                // Poster image — look in the nearest parent jrListing container
                var container = a.Ancestors().FirstOrDefault(n =>
                    n.GetAttributeValue("class", "").Contains("jrListing"));
                var img = container?.SelectSingleNode(".//img[@src]")
                        ?? a.ParentNode?.SelectSingleNode("..//img[@src]");

                results.Add(new FanEditSearchResult
                {
                    Title        = HtmlEntity.DeEntitize(a.InnerText).Trim(),
                    Url          = href,
                    ThumbnailUrl = img?.GetAttributeValue("src", null),
                });
            }
        }

        if (results.Count > 0) return results;

        // ── Strategy 1: standard WordPress article structure ─────────────────
        var articleNodes =
            doc.DocumentNode.SelectNodes("//article[contains(@class,'type-fanedit')]") ??
            doc.DocumentNode.SelectNodes("//article[.//h2[contains(@class,'entry-title')]/a]") ??
            doc.DocumentNode.SelectNodes("//article[.//h1[contains(@class,'entry-title')]/a]") ??
            doc.DocumentNode.SelectNodes("//article");

        if (articleNodes is not null)
        {
            foreach (var article in articleNodes)
            {
                var titleNode = article.SelectSingleNode(".//h2[contains(@class,'entry-title')]/a")
                             ?? article.SelectSingleNode(".//h1[contains(@class,'entry-title')]/a")
                             ?? article.SelectSingleNode(".//h2/a")
                             ?? article.SelectSingleNode(".//h1/a");
                if (titleNode is null) continue;

                var href = titleNode.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(href) || !seen.Add(href)) continue;

                var result = new FanEditSearchResult
                {
                    Title        = HtmlEntity.DeEntitize(titleNode.InnerText.Trim()),
                    Url          = href,
                    ThumbnailUrl = article.SelectSingleNode(".//img")?.GetAttributeValue("src", null),
                    Excerpt      = article.SelectSingleNode(".//*[contains(@class,'entry-summary')]")
                                          ?.InnerText.Trim(),
                };

                var yearNode = article.SelectSingleNode(".//*[contains(@class,'year')]");
                if (yearNode is not null && int.TryParse(yearNode.InnerText.Trim(), out var y))
                    result.Year = y;

                results.Add(result);
            }
        }

        if (results.Count > 0) return results;

        // ── Strategy 2: any link on the page that looks like a fanedit permalink ─
        // Catches non-standard themes and future site redesigns.
        var allLinks = doc.DocumentNode.SelectNodes("//a[@href]");
        if (allLinks is null) return results;

        foreach (var a in allLinks)
        {
            var href = a.GetAttributeValue("href", string.Empty);
            if (!_postSlugPattern.IsMatch(href)) continue;

            var slug = new Uri(href).AbsolutePath.Trim('/');
            if (_skipSlugs.Contains(slug)) continue;
            if (!seen.Add(href)) continue;

            var title = HtmlEntity.DeEntitize(a.InnerText).Trim();
            if (title.Length < 3) continue;

            // Look for an image near this link (sibling or parent img)
            var img = a.SelectSingleNode(".//img")
                   ?? a.ParentNode?.SelectSingleNode(".//img");

            results.Add(new FanEditSearchResult
            {
                Title        = title,
                Url          = href,
                ThumbnailUrl = img?.GetAttributeValue("src", null),
            });
        }

        return results;
    }

    // ── Detail page ───────────────────────────────────────────────────────────

    public FanEditEntry ParseDetailPage(string html, string url)
    {
        var doc   = new HtmlDocument();
        doc.LoadHtml(html);
        var entry = new FanEditEntry { Url = url };

        // 1. Title — prefer itemprop="name" (JReviews), fall back to og:title / page title
        entry.Title = HtmlEntity.DeEntitize(
            doc.DocumentNode.SelectSingleNode("//meta[@itemprop='name']")
                             ?.GetAttributeValue("content", null)
            ?? OgMeta(doc, "og:title")
            ?? PageTitle(doc)
            ?? string.Empty);

        // Poster: try <meta itemprop="image" content="..."> first (clean schema.org).
        // JReviews may render this as <img itemprop="image" data-jr-src="..."> instead,
        // so fall back to that, then to the first real image in the jrMediaPhoto container.
        // og:image is absent on fanedit.org.
        var posterMeta = doc.DocumentNode
            .SelectSingleNode("//meta[@itemprop='image']")?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(posterMeta) && !posterMeta.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            entry.PosterUrl = posterMeta;
        }
        else
        {
            // <img itemprop="image"> — JReviews lazy-loads with data-jr-src
            var itempropImg = doc.DocumentNode.SelectSingleNode("//*[@itemprop='image'][@src or @data-jr-src]");
            var src = itempropImg?.GetAttributeValue("data-jr-src", null)
                   ?? itempropImg?.GetAttributeValue("src", null);
            if (!string.IsNullOrWhiteSpace(src) && !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                entry.PosterUrl = src;
            }
            else
            {
                // Last resort: first image in the JReviews media gallery
                var photoImg = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'jrMediaPhoto')]//img");
                var photoSrc = photoImg?.GetAttributeValue("data-jr-src", null)
                            ?? photoImg?.GetAttributeValue("src", null);
                if (!string.IsNullOrWhiteSpace(photoSrc) && !photoSrc.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    entry.PosterUrl = photoSrc;
            }
        }

        // Overview — will be populated from jrBriefsynopsis in ParseDefinitionList
        entry.Overview = null;

        // 2. Structured definition list — most IFDB data lives here
        ParseDefinitionList(doc, entry);

        // 3. Full overview from post body if OG description is absent/short
        if (string.IsNullOrWhiteSpace(entry.Overview) || entry.Overview.Length < 80)
            entry.Overview = ParseEntryContent(doc) ?? entry.Overview;

        // 4. IFDB rating widget
        ParseRating(doc, entry);

        // 5. WordPress taxonomies — categories & tags
        ParseTaxonomies(doc, entry);

        // 6. In-post gallery / screenshot images
        ParseImages(doc, entry);

        // 7. Published date from WordPress post metadata
        entry.IfdbPublishedDate ??= ParsePublishedDate(doc);

        // 8. Year the fan edit was released — from published date or OG article:published_time
        if (!entry.Year.HasValue)
            entry.Year = ParseEditYear(doc, entry.IfdbPublishedDate);

        // 9. IFDB numeric ID from page URL or canonical link
        entry.IfdbId ??= ParseIfdbId(doc, url);

        return entry;
    }

    // ── JReviews field rows ───────────────────────────────────────────────────
    // fanedit.org uses JReviews plugin. Each field is a div like:
    //   <div class="jrFoo jrFieldRow">
    //     <div class="jrFieldLabel">Label:</div>
    //     <div class="jrFieldValue">Value</div>
    //   </div>

    private static void ParseDefinitionList(HtmlDocument doc, FanEditEntry entry)
    {
        var fieldRows = doc.DocumentNode.SelectNodes("//*[contains(@class,'jrFieldRow')]");
        if (fieldRows is null) return;

        foreach (var row in fieldRows)
        {
            var labelNode = row.SelectSingleNode(".//*[contains(@class,'jrFieldLabel')]");
            var valueNode = row.SelectSingleNode(".//*[contains(@class,'jrFieldValue')]");
            if (labelNode is null || valueNode is null) continue;

            var rawKey   = HtmlEntity.DeEntitize(labelNode.InnerText).Trim().TrimEnd(':').ToLowerInvariant();
            var val      = HtmlEntity.DeEntitize(valueNode.InnerText).Trim();
            var valClean = Regex.Replace(val, @"\s+", " ").Trim();

            // Also use the CSS class on the row itself as a reliable key
            var rowClass = row.GetAttributeValue("class", "");
            var dd       = valueNode; // alias for downstream code that references dd

            if (string.IsNullOrEmpty(valClean)) continue;

            bool IsClass(string cls) => rowClass.Contains(cls, StringComparison.OrdinalIgnoreCase);

            switch (true)
            {
                // ── Editor
                case true when IsClass("jrFaneditorname") || rawKey is "editor" or "edited by" or "fanedit by" or "faneditor name":
                    entry.EditorUsername   = valClean;
                    entry.EditorProfileUrl = dd.SelectSingleNode(".//a")?.GetAttributeValue("href", null);
                    break;

                // ── Original source material
                case true when IsClass("jrOriginalmovietitle") || rawKey is "original movie/show title" or "original movie" or "original film" or "original title" or "based on":
                    entry.OriginalTitle ??= valClean;
                    break;

                case true when IsClass("jrOriginalreleasedate") || rawKey is "original release date" or "original release year" or "release year" or "original year":
                {
                    var ym2 = _yearRegex.Match(valClean);
                    if (ym2.Success) entry.OriginalYear = int.Parse(ym2.Value);
                    break;
                }

                // ── IMDB link hidden inside jrAdditionallinks
                case true when IsClass("jrAdditionallinks") || rawKey is "additional links":
                {
                    var imdbHref = dd.SelectNodes(".//a[@href]")
                        ?.Select(a => a.GetAttributeValue("href", ""))
                        .FirstOrDefault(h => h.Contains("imdb.com/title/tt", StringComparison.OrdinalIgnoreCase));
                    if (imdbHref is not null)
                    {
                        var imdbM = _imdbIdRegex.Match(imdbHref);
                        if (imdbM.Success) entry.OriginalImdbId = imdbM.Value;
                    }
                    ParseDistributionLinks(dd, entry);
                    break;
                }

                // ── Edit classification
                case true when IsClass("jrFanedittype") || rawKey is "fanedit type" or "type of edit" or "edit type":
                    entry.FanEditType = valClean;
                    break;

                // ── Duration of the fan edit
                case true when IsClass("jrFaneditrunningtimemin") || rawKey is "fanedit running time" or "edit running time" or "running time" or "runtime" or "length" or "duration":
                {
                    var rm = _numberRegex.Match(valClean);
                    if (rm.Success) entry.RuntimeMinutes = int.Parse(rm.Groups[1].Value);
                    break;
                }

                // ── Fan edit release date / year
                case true when IsClass("jrFaneditreleasedate") || rawKey is "fanedit release date" or "release date" or "date released":
                    entry.IfdbPublishedDate ??= valClean;
                    if (!entry.Year.HasValue)
                    {
                        var ym3 = _yearRegex.Match(valClean);
                        if (ym3.Success) entry.Year = int.Parse(ym3.Value);
                    }
                    break;

                // ── Edit overview / synopsis
                case true when IsClass("jrBriefsynopsis") || rawKey is "synopsis" or "brief synopsis" or "overview" or "edit overview" or "description":
                    if (string.IsNullOrWhiteSpace(entry.Overview) || entry.Overview.Length < valClean.Length)
                        entry.Overview = valClean;
                    break;

                // ── Intention (fallback overview)
                case true when IsClass("jrIntention") || rawKey is "intention":
                    if (string.IsNullOrWhiteSpace(entry.Overview))
                        entry.Overview = valClean;
                    break;

                // ── Changes / cut list
                case true when IsClass("jrCutlist") || rawKey is "cuts and additions" or "cut list" or "changes":
                    if (!string.IsNullOrWhiteSpace(valClean))
                        entry.ChangesList = SplitLines(valClean);
                    break;

                case true when IsClass("jrEditingdetails") || rawKey is "editing details":
                    if (entry.ChangesList.Count == 0 && !string.IsNullOrWhiteSpace(valClean))
                        entry.ChangesList = SplitLines(valClean);
                    break;

                // ── Technical specs
                case true when IsClass("jrVideo") || rawKey is "video" or "video codec" or "video format":
                    entry.TechSpecs ??= new FanEditTechSpecs();
                    entry.TechSpecs.VideoCodec = valClean;
                    break;

                case true when IsClass("jrAudio") || rawKey is "audio" or "audio codec" or "audio format":
                    entry.TechSpecs ??= new FanEditTechSpecs();
                    entry.TechSpecs.AudioCodec = valClean;
                    break;

                // ── Genre (from jrGenre field row)
                case true when IsClass("jrGenre") || rawKey is "genre" or "genres":
                    if (entry.Genres.Count == 0)
                    {
                        var genreItems = dd.SelectNodes(".//li | .//a");
                        if (genreItems is { Count: > 0 })
                            entry.Genres = genreItems
                                .Select(n => HtmlEntity.DeEntitize(n.InnerText).Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        else
                            entry.Genres = SplitCommaList(valClean);
                    }
                    break;

                // ── Franchise as tag
                case true when IsClass("jrFranchise") || rawKey is "franchise":
                    if (entry.Tags.Count == 0)
                        entry.Tags = SplitCommaList(valClean);
                    break;

                // ── IFDB category (fanedit type taxonomy)
                case true when IsClass("jrIfdbcategory") || rawKey is "category" or "categories" or "ifdb category" or "ifdb categories":
                    entry.IfdbCategories = SplitCommaList(valClean);
                    break;

                // ── Release information
                case true when IsClass("jrReleaseinformation") || rawKey is "release information":
                    break; // informational only

                // ── Ignore noise fields
                case true when IsClass("jrSubtitles") || IsClass("jrHd") || IsClass("jrTimecut") || IsClass("jrTimeaddedmin") || IsClass("jrSpecialnotesthanks") || IsClass("jrGeneral"):
                    break;
            }
        }
    }

    // ── Rating widget ─────────────────────────────────────────────────────────

    private static void ParseRating(HtmlDocument doc, FanEditEntry entry)
    {
        // JReviews renders overall rating in the first jrRatingValue cell of the ratings table.
        // The first row is always "Overall rating". We also look for itemprop="ratingValue".
        var itempropNode = doc.DocumentNode.SelectSingleNode("//*[@itemprop='ratingValue']");
        if (itempropNode is not null)
        {
            var ratingText = itempropNode.InnerText.Trim();
            if (double.TryParse(ratingText,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var rv))
            {
                entry.Rating        = rv;
                entry.IfdbRatingRaw = ratingText;
                // Count votes from "(N)" pattern next to the rating in the first row
                var countNode = itempropNode.ParentNode?.SelectSingleNode(
                    "following-sibling::*[contains(@class,'jrRatingValue')]");
                var countM = Regex.Match(countNode?.InnerText ?? "", @"\((\d+)\)");
                if (countM.Success) entry.IfdbRatingCount = int.Parse(countM.Groups[1].Value);
                return;
            }
        }

        // Fallback: look for any jrRatingValue containing a decimal number
        var ratingNode = doc.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'jrRatingValue') and contains(@class,'fwd-table-cell')]");
        if (ratingNode is null) return;

        var m = _ratingRegex.Match(ratingNode.InnerText);
        if (!m.Success) return;

        entry.IfdbRatingRaw   = m.Groups[1].Value;
        entry.IfdbRatingCount = int.TryParse(m.Groups[2].Value, out var rc) ? rc : null;
        entry.Rating          = double.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rv2) ? rv2 : null;
    }

    // ── Post body overview (fallback only) ───────────────────────────────────

    private static string? ParseEntryContent(HtmlDocument doc)
    {
        // JReviews stores the synopsis in jrBriefsynopsis — ParseDefinitionList handles it.
        // This fallback only runs if the jrFieldRow parsing found nothing.
        var synopsisNode = doc.DocumentNode.SelectSingleNode(
            "//*[contains(@class,'jrBriefsynopsis')]//*[contains(@class,'jrFieldValue')]");
        if (synopsisNode is not null)
        {
            var t = HtmlEntity.DeEntitize(synopsisNode.InnerText).Trim();
            if (t.Length > 40) return t;
        }

        // Last resort: first substantial paragraph inside the listing content area
        var content = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'jrListingDetailContent')] | //div[contains(@class,'entry-content')]");
        if (content is null) return null;

        foreach (var strong in content.SelectNodes(".//strong | .//b") ?? Enumerable.Empty<HtmlNode>())
        {
            var label = strong.InnerText.Trim().TrimEnd(':').ToLowerInvariant();
            if (label is "edit overview" or "overview" or "edit intent" or "synopsis" or "description")
            {
                // Text is usually the next sibling or parent's following text
                var parent = strong.ParentNode;
                var text   = Regex.Replace(
                    HtmlEntity.DeEntitize(parent.InnerText), @"\s+", " ").Trim();
                // Strip the label prefix
                var colon = text.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0 && colon < 40)
                    text = text[(colon + 1)..].Trim();
                if (text.Length > 40) return text;
            }
        }

        // Fallback: first substantial paragraph
        var paras = content.SelectNodes(".//p");
        if (paras is null) return null;
        foreach (var p in paras)
        {
            var t = HtmlEntity.DeEntitize(p.InnerText).Trim();
            if (t.Length > 60) return t;
        }

        return null;
    }

    // ── WordPress taxonomies (categories / tags) ──────────────────────────────

    private static void ParseTaxonomies(HtmlDocument doc, FanEditEntry entry)
    {
        // Tags: <a rel="tag">...</a>  or  class containing 'tag-links'
        var tagLinks = doc.DocumentNode.SelectNodes(
            "//a[@rel='tag'] | //*[contains(@class,'tags-links')]//a | //*[contains(@class,'tag-links')]//a");
        if (tagLinks is { Count: > 0 } && entry.Tags.Count == 0)
            entry.Tags = tagLinks
                .Select(a => HtmlEntity.DeEntitize(a.InnerText).Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Categories: <a rel="category tag"> or class 'cat-links'
        var catLinks = doc.DocumentNode.SelectNodes(
            "//*[contains(@class,'cat-links')]//a | //*[contains(@class,'category-links')]//a");
        if (catLinks is { Count: > 0 } && entry.Genres.Count == 0)
            entry.Genres = catLinks
                .Select(a => HtmlEntity.DeEntitize(a.InnerText).Trim())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    // ── Gallery images ────────────────────────────────────────────────────────

    private static void ParseImages(HtmlDocument doc, FanEditEntry entry)
    {
        // JReviews uses data-jr-src for lazy-loaded images; also check data-thumburl on the container.
        // The first image (the poster) is already captured via itemprop="image"; collect the rest as screenshots.
        var imgs = doc.DocumentNode.SelectNodes(
            "//*[contains(@class,'jrMediaPhoto')] | " +
            "//div[contains(@class,'gallery')]//img | " +
            "//figure[contains(@class,'wp-block-image')]//img");

        if (imgs is null) return;

        // Normalise the poster URL (strip WP size suffixes) so the dedup comparison
        // works even when the itemprop URL and the jrMediaPhoto URL use different sizes.
        var poster = entry.PosterUrl;
        var posterNorm = poster is null ? null : Regex.Replace(poster, @"-\d+x\d+(\.\w+)$", "$1");

        foreach (var img in imgs)
        {
            // JReviews lazy-loads with data-jr-src; fall back to src
            var src = img.GetAttributeValue("data-jr-src", null)
                   ?? img.GetAttributeValue("src", null)
                   ?? img.GetAttributeValue("data-src", null);
            if (string.IsNullOrWhiteSpace(src)) continue;
            // Skip base64 placeholders and tracker/pixel images
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (src.Contains("gravatar") || src.Contains("avatar")) continue;
            if (Regex.IsMatch(src, @"-1x1\.", RegexOptions.IgnoreCase)) continue;
            if (src.Contains("pixel", StringComparison.OrdinalIgnoreCase)) continue;
            // Prefer full-size over thumbnail variants (WordPress appends -150x150 etc.)
            var full = Regex.Replace(src, @"-\d+x\d+(\.\w+)$", "$1");
            // Skip if this is the same image as the poster (compare normalised)
            if (full == posterNorm) continue;
            if (!entry.AdditionalImages.Contains(full))
                entry.AdditionalImages.Add(full);
        }
    }

    // ── Published date ────────────────────────────────────────────────────────

    private static string? ParsePublishedDate(HtmlDocument doc)
    {
        // WordPress outputs <time class="entry-date published" datetime="2024-03-15T...">
        var timeNode = doc.DocumentNode.SelectSingleNode(
            "//time[contains(@class,'published')] | //time[contains(@class,'entry-date')]");
        if (timeNode is not null)
        {
            var dt = timeNode.GetAttributeValue("datetime", null);
            if (!string.IsNullOrWhiteSpace(dt)) return dt.Split('T')[0]; // date part only
            var t = timeNode.InnerText.Trim();
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }

        // OG article:published_time
        var ogDate = doc.DocumentNode
            .SelectSingleNode("//meta[@property='article:published_time']")
            ?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(ogDate))
            return ogDate.Split('T')[0];

        return null;
    }

    // ── Edit year ─────────────────────────────────────────────────────────────

    private static int? ParseEditYear(HtmlDocument doc, string? publishedDate)
    {
        if (!string.IsNullOrWhiteSpace(publishedDate))
        {
            var ym = _yearRegex.Match(publishedDate);
            if (ym.Success && int.TryParse(ym.Value, out var y)) return y;
        }
        // OG article:published_time
        var ogDate = doc.DocumentNode
            .SelectSingleNode("//meta[@property='article:published_time']")
            ?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(ogDate))
        {
            var ym = _yearRegex.Match(ogDate);
            if (ym.Success && int.TryParse(ym.Value, out var y)) return y;
        }
        return null;
    }

    // ── IFDB numeric ID ───────────────────────────────────────────────────────

    private static string? ParseIfdbId(HtmlDocument doc, string url)
    {
        // Old IFDB URL format: /ifdb/1234/
        var m = _ifdbNumericId.Match(url);
        if (m.Success) return m.Groups[1].Value;

        // Canonical link may use the numeric form even if we followed a slug redirect
        var canonical = doc.DocumentNode
            .SelectSingleNode("//link[@rel='canonical']")
            ?.GetAttributeValue("href", null);
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            var cm = _ifdbNumericId.Match(canonical);
            if (cm.Success) return cm.Groups[1].Value;
        }

        // WordPress post ID in body class: post-1234
        var bodyClass = doc.DocumentNode.SelectSingleNode("//body")
            ?.GetAttributeValue("class", string.Empty) ?? string.Empty;
        var pm = Regex.Match(bodyClass, @"\bpost-(\d+)\b");
        if (pm.Success) return pm.Groups[1].Value;

        return null;
    }

    // ── Distribution links ────────────────────────────────────────────────────

    private static void ParseDistributionLinks(HtmlNode dd, FanEditEntry entry)
    {
        var anchors = dd.SelectNodes(".//a");
        if (anchors is { Count: > 0 })
        {
            foreach (var a in anchors)
            {
                var href = a.GetAttributeValue("href", null);
                if (!string.IsNullOrWhiteSpace(href) && !entry.DistributionLinks.Contains(href))
                    entry.DistributionLinks.Add(href);
            }
        }
        else
        {
            // Plain text — could be a site name or multiple entries separated by commas
            var text = HtmlEntity.DeEntitize(dd.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                entry.DistributionLinks.Add(text);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? OgMeta(HtmlDocument doc, string property)
        => doc.DocumentNode
              .SelectSingleNode($"//meta[@property='{property}']")
              ?.GetAttributeValue("content", null);

    private static string? PageTitle(HtmlDocument doc)
    {
        return doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'entry-title')]")?.InnerText.Trim()
            ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();
    }

    private static List<string> SplitLines(string s) =>
        s.Split(['\n', '\r', '|', '•', '·'], StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim())
         .Where(x => x.Length > 1)
         .ToList();

    private static List<string> SplitCommaList(string s) =>
        s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim())
         .Where(x => x.Length > 0)
         .ToList();
}
