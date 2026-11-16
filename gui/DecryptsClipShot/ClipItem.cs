namespace DecryptsClipShot;

public sealed class ClipItem
{
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Details { get; init; }
    public required string FullPath { get; init; }
    public string? ThumbnailPath { get; set; }
}
