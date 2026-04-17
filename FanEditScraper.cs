using HtmlAgilityPack;
using Chronicle.Plugin.FanEdit.Models;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.FanEdit;

internal sealed class FanEditScraper
{
    private static readonly Regex _ratingRegex = new(@"([\d.]+)\s*\((\d+)\s*vote", RegexOptions.IgnoreCase);

    public List<FanEditSearchResult> ParseSearchResults(string html)
    {
        var doc     = new HtmlDocument();
        doc.LoadHtml(html);
        var results = new List<FanEditSearchResult>();

        var articleNodes = doc.DocumentNode.SelectNodes("//article[contains(@class,'type-fanedit')]");
        if (articleNodes is null) return results;
        foreach (var article in articleNodes)
        {
            var titleNode = article.SelectSingleNode(".//h2[contains(@class,'entry-title')]/a")
                         ?? article.SelectSingleNode(".//h1/a");
            if (titleNode is null) continue;

            var result = new FanEditSearchResult
            {
                Title        = HtmlEntity.DeEntitize(titleNode.InnerText.Trim()),
                Url          = titleNode.GetAttributeValue("href", string.Empty),
                ThumbnailUrl = article.SelectSingleNode(".//img")?.GetAttributeValue("src", null),
                Excerpt      = article.SelectSingleNode(".//*[contains(@class,'entry-summary')]")
                                      ?.InnerText.Trim(),
            };

            var yearNode = article.SelectSingleNode(".//*[contains(@class,'year')]");
            if (yearNode is not null && int.TryParse(yearNode.InnerText.Trim(), out var y))
                result.Year = y;

            results.Add(result);
        }

        return results;
    }

    public FanEditEntry ParseDetailPage(string html, string url)
    {
        var doc   = new HtmlDocument();
        doc.LoadHtml(html);
        var entry = new FanEditEntry { Url = url };

        // Priority 1: OpenGraph
        entry.Title    = OgMeta(doc, "og:title")    ?? string.Empty;
        entry.Overview = OgMeta(doc, "og:description");
        entry.PosterUrl= OgMeta(doc, "og:image");

        // Priority 2: definition list key-value pairs
        ParseDefinitionList(doc, entry);

        // Priority 3: rating
        var ratingNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'ifdb-rating')]");
        if (ratingNode is not null)
        {
            var m = _ratingRegex.Match(ratingNode.InnerText);
            if (m.Success)
            {
                entry.IfdbRatingRaw   = m.Groups[1].Value;
                entry.IfdbRatingCount = int.TryParse(m.Groups[2].Value, out var rc) ? rc : null;
                entry.Rating          = double.TryParse(m.Groups[1].Value,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var rv) ? rv : null;
            }
        }

        return entry;
    }

    private static string? OgMeta(HtmlDocument doc, string property)
        => doc.DocumentNode
              .SelectSingleNode($"//meta[@property='{property}']")
              ?.GetAttributeValue("content", null);

    private static void ParseDefinitionList(HtmlDocument doc, FanEditEntry entry)
    {
        var dts = doc.DocumentNode.SelectNodes("//dl/dt");
        if (dts is null) return;
        foreach (var dt in dts)
        {
            var key = dt.InnerText.Trim().TrimEnd(':').ToLowerInvariant();
            var dd  = dt.SelectSingleNode("following-sibling::dd[1]");
            var val = dd?.InnerText.Trim();
            if (string.IsNullOrEmpty(val)) continue;

            switch (key)
            {
                case "editor":
                    entry.EditorUsername = val;
                    entry.EditorProfileUrl = dd!.SelectSingleNode(".//a")?.GetAttributeValue("href", null);
                    break;
                case "runtime":
                    var rm = Regex.Match(val, @"(\d+)");
                    if (rm.Success) entry.RuntimeMinutes = int.Parse(rm.Groups[1].Value);
                    break;
                case "video":
                    entry.TechSpecs ??= new();
                    entry.TechSpecs.VideoCodec = val;
                    break;
                case "audio":
                    entry.TechSpecs ??= new();
                    entry.TechSpecs.AudioCodec = val;
                    break;
                case "type":
                    entry.FanEditType = val;
                    break;
                case "original":
                    entry.OriginalTitle = val;
                    break;
            }
        }
    }
}
