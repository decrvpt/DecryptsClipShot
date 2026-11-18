using System.Windows;
using System.Windows.Media;

namespace DecryptsClipShot;

public sealed class ClipItem
{
    public required string Title { get; init; }
    public required string Category { get; init; }
    public required string Details { get; init; }
    public required string FullPath { get; init; }
    public string? ThumbnailPath { get; set; }

    // Trickshot details from the replay, shown under the clip (empty for other clips).
    public string? KillWeaponLine { get; init; }
    public string? KillPlayersLine { get; init; }
    public string? KillRarity { get; init; }
    public Brush? KillRarityBrush { get; init; }
    public Visibility KillInfoVisibility => KillWeaponLine is null ? Visibility.Collapsed : Visibility.Visible;
}
