namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditSearchResult
{
    public string  Title        { get; set; } = string.Empty;
    public string  Url          { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public string? Excerpt      { get; set; }
    public int?    Year         { get; set; }
}
