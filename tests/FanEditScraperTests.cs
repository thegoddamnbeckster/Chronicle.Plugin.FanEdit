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

    private const string DetailHtml = """
        <html>
        <head>
          <meta property="og:title" content="Blade Runner: The Final Edit" />
          <meta property="og:description" content="A refined cut." />
          <meta property="og:image" content="https://www.fanedit.org/poster.jpg" />
        </head>
        <body>
          <dl>
            <dt>Editor:</dt><dd>SomeEditor</dd>
            <dt>Runtime:</dt><dd>117 min</dd>
            <dt>Video:</dt><dd>H.264</dd>
            <dt>Audio:</dt><dd>AC3 5.1</dd>
          </dl>
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
}
