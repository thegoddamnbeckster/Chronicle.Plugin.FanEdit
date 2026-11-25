using Chronicle.Plugin.FanEdit;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditScraperTests
{
    private static FanEditScraper Scraper() => new();

    private const string SearchHtml = """
        <html><body>
        <article class="post type-fanedit">
          <h2 class="entry-title"><a href="https://www.fanedit.org/blade-runner-the-final-edit/">Blade Runner: The Final Edit</a></h2>
          <div class="entry-summary"><p>A refined cut of Blade Runner.</p></div>
          <img src="https://www.fanedit.org/wp-content/uploads/br.jpg" />
          <span class="year">1982</span>
        </article>
        </body></html>
        """;

    // JReviews field-row markup (the plugin fanedit.org actually runs) -- a plain <dl> was
    // never real fanedit.org markup, see FanEditScraper.ParseDefinitionList's own doc.
    private const string DetailHtml = """
        <html>
        <head>
          <meta property="og:title" content="Blade Runner: The Final Edit" />
          <meta itemprop="image" content="https://www.fanedit.org/poster.jpg" />
        </head>
        <body>
          <div class="jrFieldRow jrBriefsynopsis">
            <div class="jrFieldLabel">Synopsis:</div>
            <div class="jrFieldValue">A refined cut.</div>
          </div>
          <div class="jrFieldRow jrFaneditrunningtimemin">
            <div class="jrFieldLabel">Runtime:</div>
            <div class="jrFieldValue">117 min</div>
          </div>
          <div class="jrFieldRow jrVideo">
            <div class="jrFieldLabel">Video:</div>
            <div class="jrFieldValue">H.264</div>
          </div>
          <div class="jrFieldRow jrAudio">
            <div class="jrFieldLabel">Audio:</div>
            <div class="jrFieldValue">AC3 5.1</div>
          </div>
          <div class="ifdb-rating">8.5 (42 votes)</div>
        </body></html>
        """;

    [Fact]
    public void ParseSearchResults_ExtractsTitle_Url_Year()
    {
        var results = Scraper().ParseSearchResults(SearchHtml);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Blade Runner: The Final Edit");
        results[0].Url.Should().Be("https://www.fanedit.org/blade-runner-the-final-edit/");
        results[0].Year.Should().Be(1982);
    }

    [Fact]
    public void ParseDetailPage_ExtractsOgTitle_And_Overview()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/blade-runner-the-final-edit/");

        entry.Title.Should().Be("Blade Runner: The Final Edit");
        entry.Overview.Should().Be("A refined cut.");
        entry.PosterUrl.Should().Be("https://www.fanedit.org/poster.jpg");
    }

    [Fact]
    public void ParseDetailPage_ExtractsRuntime_FromDefinitionList()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/x/");
        entry.RuntimeMinutes.Should().Be(117);
    }

    [Fact]
    public void ParseDetailPage_ExtractsTechSpecs()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.fanedit.org/x/");
        entry.TechSpecs.Should().NotBeNull();
        entry.TechSpecs!.VideoCodec.Should().Be("H.264");
        entry.TechSpecs.AudioCodec.Should().Be("AC3 5.1");
    }

    [Fact]
    public void ParseDetailPage_HandlesMissingFields_Gracefully()
    {
        // Nearly empty page — no fields should throw
        var entry = Scraper().ParseDetailPage("<html><body></body></html>", "https://www.fanedit.org/x/");
        entry.Should().NotBeNull();
        entry.Title.Should().BeEmpty();
    }

    private const string RatingsHeaderHtml = """
        <html><body><div class="jrOverallRatings">
          <div class="jrOverallEditor jrRatingsLarge"><span class="jrIconEditor jrRatingLabel"></span>
            <div class="jrRatingStars"><div class="jrRatingStarsEditor"><div></div></div></div>
            <span class="jrRatingValue"><span>9.9</span> <span>(<span>5</span>)</span></span></div>
          <div class="jrOverallUser jrRatingsLarge"><span class="jrIconUsers jrRatingLabel"></span>
            <div class="jrRatingStars"><div class="jrRatingStarsUser"><div></div></div></div>
            <span class="jrRatingValue"><span>10.0</span> <span class="jrReviewCount">(<span>42</span>)</span></span></div>
        </div>
        <div itemprop="ratingValue" class="jrRatingValue fwd-table-cell">9.9</div></body></html>
        """;

    [Fact]
    public void ParseDetailPage_KeepsTheReviewersAndTheUsersScoresApart()
    {
        var entry = Scraper().ParseDetailPage(RatingsHeaderHtml, "https://www.fanedit.org/x/");
        entry.EditorRating.Should().Be(9.9);
        entry.EditorRatingCount.Should().Be(5);
        entry.UserRating.Should().Be(10.0);
        entry.UserRatingCount.Should().Be(42);
        entry.Rating.Should().Be(10.0);              // the users' score, the larger population
        entry.IfdbRatingCount.Should().Be(42);
    }

    [Fact]
    public void ParseDetailPage_OnlyReviewersRated_UsesTheirScoreAndOmitsTheOther()
    {
        var html = RatingsHeaderHtml.Replace("10.0", "").Replace("<span>(<span>42</span>)</span>", "").Replace("jrOverallUser", "jrOverallNone");
        var entry = Scraper().ParseDetailPage(html, "https://www.fanedit.org/x/");
        entry.UserRating.Should().BeNull();
        entry.Rating.Should().Be(9.9);
        entry.IfdbRatingCount.Should().Be(5);
    }

    [Fact]
    public void ParseDetailPage_WithoutTheHeaderScores_ReportsNoRating_RatherThanGuessingWhoseItIs()
    {
        var entry = Scraper().ParseDetailPage("<html><body><div itemprop=\"ratingValue\" class=\"jrRatingValue\">9.9</div></body></html>", "https://www.fanedit.org/x/");
        entry.Rating.Should().BeNull();
        entry.EditorRating.Should().BeNull();
        entry.UserRating.Should().BeNull();
    }

    [Fact]
    public void ARatingOutsideZeroToTen_IsNotAScore()
    {
        var html = RatingsHeaderHtml.Replace("<span>10.0</span>", "<span>97</span>");
        var entry = Scraper().ParseDetailPage(html, "https://www.fanedit.org/x/");
        entry.UserRating.Should().BeNull();
    }

    [Fact]
    public void TheTwoScores_AreReportedAsSeparateContractEntries_EachOutOfTen_WithItsOwnReviewCount()
    {
        var ratings = FanEditMetadataProvider.FanEditRatings(new Chronicle.Plugin.FanEdit.Models.FanEditEntry { EditorRating = 9.9, EditorRatingCount = 5, UserRating = 10.0, UserRatingCount = 42 });
        ratings.Should().HaveCount(2);
        ratings[0]["variant"].Should().Be("reviewers");
        ratings[0]["votes"].Should().Be(5);
        ratings[1]["variant"].Should().Be("users");
        ratings[1]["value"].Should().Be(10.0);
        ratings.Should().OnlyContain(r => (string)r["key"]! == "ifdb" && (int)r["scale"]! == 10);
        FanEditMetadataProvider.FanEditRatings(new Chronicle.Plugin.FanEdit.Models.FanEditEntry()).Should().BeEmpty();
    }
}
