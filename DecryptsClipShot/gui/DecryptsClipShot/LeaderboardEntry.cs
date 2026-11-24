using System.Windows;

namespace DecryptsClipShot;

/// <summary>One automatic trickshot on the distance leaderboard.</summary>
public sealed class LeaderboardEntry
{
    public string Clip { get; set; } = "";
    public double DistanceUU { get; set; }
    public string Weapon { get; set; } = "";
    public string Shot { get; set; } = "";
    public string Killer { get; set; } = "";
    public string Victim { get; set; } = "";
    public bool VictimIsBot { get; set; }
    public string Date { get; set; } = "";
    public string Replay { get; set; } = "";

    // Filled in by the app when the list is shown.
    public int Rank { get; set; }
    public string ClipPath { get; set; } = "";
    public bool ClipExists { get; set; }

    public string RankText => $"#{Rank}";
    public string DistanceText => $"{DistanceUU / 100.0:F0}m";
    public string WeaponLine => Shot.Length > 0 ? $"{Weapon} · {Shot}" : Weapon;
    public string VictimLine => Victim.Length > 0 ? $"Eliminated {Victim}" : "";
    public Visibility BotVisibility => VictimIsBot ? Visibility.Visible : Visibility.Collapsed;
    public string DateText => System.DateTime.TryParse(Date, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
        ? when.ToLocalTime().ToString("MMM d, yyyy") : "";
    public string WatchText => ClipExists ? "Watch" : "Clip deleted";
}
