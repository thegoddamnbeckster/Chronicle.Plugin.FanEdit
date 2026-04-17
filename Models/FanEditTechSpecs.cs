namespace Chronicle.Plugin.FanEdit.Models;

internal sealed class FanEditTechSpecs
{
    public string? VideoCodec      { get; set; }
    public string? AudioCodec      { get; set; }
    public string? Resolution      { get; set; }
    public string? AspectRatio     { get; set; }
    public string? ContainerFormat { get; set; }
    public double? FileSizeGb      { get; set; }
}
