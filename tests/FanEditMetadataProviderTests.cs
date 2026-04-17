using Chronicle.Plugin.FanEdit;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.FanEdit.Tests;

public class FanEditMetadataProviderTests
{
    [Fact]
    public void GetSupportedMediaTypes_ReturnsFaneditsOnly()
    {
        var provider = new FanEditMetadataProvider();
        var types    = provider.GetSupportedMediaTypes();

        types.Should().HaveCount(1);
        types[0].MediaTypeName.Should().Be("fanedits");
    }

    [Fact]
    public void GetSettingsSchema_ContainsRequiredKeys()
    {
        var schema = new FanEditMetadataProvider().GetSettingsSchema();
        var keys   = schema.Settings.Select(s => s.Key).ToList();

        keys.Should().Contain("username");
        keys.Should().Contain("password");
        keys.Should().Contain("request_delay_ms");
    }

    [Fact]
    public void PluginId_IsCorrect()
    {
        new FanEditMetadataProvider().PluginId.Should().Be("chronicle.plugin.fanedit");
    }

    [Fact]
    public async Task SearchAsync_ThrowsInvalidOperation_WhenNotConfigured()
    {
        var provider = new FanEditMetadataProvider();
        var ctx      = new MediaSearchContext("Blade Runner");
        var act      = () => provider.SearchAsync(ctx);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }
}
