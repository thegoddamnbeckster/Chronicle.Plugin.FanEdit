namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditEntry
{
    public string   Title               { get; set; } = string.Empty;
    public string   Url                 { get; set; } = string.Empty;
    public string?  Overview            { get; set; }
    public int?     Year                { get; set; }
    public int?     RuntimeMinutes      { get; set; }
    public string?  PosterUrl           { get; set; }
    public List<string> AdditionalImages { get; set; } = [];
    public List<string> Genres          { get; set; } = [];
    public double?  Rating              { get; set; }
    public List<string> Tags            { get; set; } = [];

    // Source material
    public string?  OriginalTitle       { get; set; }
    public int?     OriginalYear        { get; set; }
    public string?  OriginalImdbId      { get; set; }

    // Editor info
    public string?  EditorUsername      { get; set; }
    public string?  EditorProfileUrl    { get; set; }

    // Classification
    public string?  FanEditType         { get; set; }
    public List<string> IfdbCategories  { get; set; } = [];

    // Tech specs
    public FanEditTechSpecs? TechSpecs  { get; set; }

    // Edit details
    public List<string> ChangesList     { get; set; } = [];
    public int?     NumberOfCuts        { get; set; }
    public int?     NumberOfAdditions   { get; set; }

    // Reception
    public string?  IfdbRatingRaw       { get; set; }
    public int?     IfdbRatingCount     { get; set; }
    public List<string> IfdbAwards      { get; set; } = [];

    // Publishing
    public string?  IfdbId              { get; set; }
    public string?  IfdbPublishedDate   { get; set; }
    public List<string> DistributionLinks { get; set; } = [];
}
