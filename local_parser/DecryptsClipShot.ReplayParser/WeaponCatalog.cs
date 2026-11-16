using System.Reflection;
using System.Text.Json;

namespace DecryptsClipShot.ReplayParser;

/// <summary>One row of weapons.json.</summary>
internal sealed record WeaponEntry(
    string Name,
    string? MythicName,
    string[] Match,
    string Class,
    string Ammo,
    bool Sniper,
    string? Source);

/// <summary>What the parser decided about a kill's weapon.</summary>
internal sealed record WeaponResult(
    string Name,
    string Class,
    bool IsSniper,
    bool IsHeavyAmmo,
    bool Known,
    string? TagKey);

/// <summary>
/// Turns Fortnite's weapon tags into the names players know.
///
/// Replay tags name weapons by internal codename, e.g.
/// "Weapon.Ranged.Pistol.Slingshot.HarmlessThrift" is Usopp's Slingshot and
/// "Weapon.Ranged.Assault.Standard" is the Assault Rifle. Codename words can't
/// be turned into names with string rules, so they are looked up in
/// weapons.json. Unknown codenames show only the weapon class ("Pistol") and
/// are logged so they can be added to the table.
/// </summary>
internal static class WeaponCatalog
{
    private static readonly List<WeaponEntry> Entries = Load();

    // First segment after "Weapon.Ranged." → class shown when the codename is unknown.
    private static readonly Dictionary<string, string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Assault"] = "Assault Rifle", ["AssaultRifle"] = "Assault Rifle", ["AR"] = "Assault Rifle",
        ["Sniper"] = "Sniper Rifle", ["SniperRifle"] = "Sniper Rifle",
        ["Shotgun"] = "Shotgun", ["SMG"] = "SMG", ["Pistol"] = "Pistol",
        ["DMR"] = "DMR", ["LMG"] = "LMG", ["Minigun"] = "Minigun",
        ["Launcher"] = "Launcher", ["RocketLauncher"] = "Rocket Launcher",
        ["GrenadeLauncher"] = "Grenade Launcher",
        ["Bow"] = "Bow", ["Crossbow"] = "Crossbow", ["Revolver"] = "Revolver",
        ["Slingshot"] = "Slingshot"
    };

    // Fortnite's EDeathCause, used only when a kill has no weapon tag.
    // Values above Trap are reordered between seasons, so names are preferred.
    private static readonly string[] DeathCauseByValue =
    [
        "OutsideSafeZone", "FallDamage", "Pistol", "Shotgun", "Rifle", "SMG", "Sniper",
        "SniperNoScope", "Melee", "InfinityBlade", "Grenade", "C4", "GrenadeLauncher",
        "RocketLauncher", "Minigun", "Bow", "Trap"
    ];

    private static readonly Dictionary<string, string> DeathCauseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OutsideSafeZone"] = "Storm", ["FallDamage"] = "Fall damage", ["Pistol"] = "Pistol",
        ["Shotgun"] = "Shotgun", ["Rifle"] = "Assault Rifle", ["SMG"] = "SMG",
        ["Sniper"] = "Sniper Rifle", ["SniperNoScope"] = "Sniper Rifle", ["Melee"] = "Melee",
        ["Grenade"] = "Grenade", ["C4"] = "C4", ["GrenadeLauncher"] = "Grenade Launcher",
        ["RocketLauncher"] = "Rocket Launcher", ["Minigun"] = "Minigun", ["Bow"] = "Bow",
        ["Trap"] = "Trap"
    };

    private static List<WeaponEntry> Load()
    {
        var entries = new List<WeaponEntry>();
        // A weapons.json beside ClipShotReplayParser.exe (if present) is checked
        // first, so the table can be patched without rebuilding.
        string beside = Path.Combine(AppContext.BaseDirectory, "weapons.json");
        if (File.Exists(beside))
        {
            try { entries.AddRange(Parse(File.ReadAllText(beside))); }
            catch (Exception exception) { Console.Error.WriteLine($"Ignoring weapons.json beside the parser: {exception.Message}"); }
        }
        using Stream? embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("weapons.json");
        if (embedded is not null)
        {
            using var reader = new StreamReader(embedded);
            entries.AddRange(Parse(reader.ReadToEnd()));
        }
        return entries;
    }

    private static IEnumerable<WeaponEntry> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement weapon in document.RootElement.GetProperty("weapons").EnumerateArray())
        {
            string Text(string name) =>
                weapon.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
            string[] match = weapon.TryGetProperty("match", out JsonElement list)
                ? list.EnumerateArray().Select(token => token.GetString() ?? "").Where(token => token.Length > 0).ToArray()
                : [];
            if (match.Length == 0 || Text("name").Length == 0) continue;
            yield return new WeaponEntry(
                Text("name"),
                Text("mythicName") is { Length: > 0 } mythic ? mythic : null,
                match,
                Text("class"),
                Text("ammo"),
                weapon.TryGetProperty("sniper", out JsonElement sniper) && sniper.ValueKind == JsonValueKind.True,
                Text("source"));
        }
    }

    /// <param name="tags">Killer-side tags of the kill.</param>
    /// <param name="rarity">Rarity already read for this kill (for Mythic variants).</param>
    /// <param name="deathCause">KillFeedEntry.DeathCause (enum or number), if the library exposes it.</param>
    public static WeaponResult Resolve(IReadOnlyList<string> tags, string rarity, object? deathCause)
    {
        string? key = WeaponKey(tags);
        if (key is not null)
        {
            string normalized = Normalize(key);
            WeaponEntry? best = null;
            int bestLength = 0;
            foreach (WeaponEntry entry in Entries)
            {
                foreach (string token in entry.Match)
                {
                    string normalizedToken = Normalize(token);
                    if (normalizedToken.Length > bestLength && normalized.Contains(normalizedToken, StringComparison.Ordinal))
                    {
                        best = entry;
                        bestLength = normalizedToken.Length;
                    }
                }
            }
            if (best is not null)
            {
                string name = rarity.Equals("Mythic", StringComparison.OrdinalIgnoreCase) && best.MythicName is not null
                    ? best.MythicName
                    : best.Name;
                return new WeaponResult(name, best.Class, best.Sniper,
                    best.Ammo.Equals("Heavy", StringComparison.OrdinalIgnoreCase), true, key);
            }

            // Not in the table yet: show the class only, never the codename words.
            string first = key.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string className = Classes.TryGetValue(first, out string? mapped)
                ? mapped
                : FromDeathCause(deathCause) ?? "Unknown weapon";
            Console.Error.WriteLine($"UNKNOWN_WEAPON_TAG {key} (rarity {rarity}, death cause {DeathCauseName(deathCause) ?? "n/a"})");
            return new WeaponResult(className, className, className == "Sniper Rifle", false, false, key);
        }

        string? cause = FromDeathCause(deathCause);
        bool sniperCause = DeathCauseName(deathCause) is "Sniper" or "SniperNoScope";
        return new WeaponResult(cause ?? "Unknown", cause ?? "", sniperCause, false, false, null);
    }

    /// <summary>The part of the most specific weapon tag after "Weapon.Ranged." / "Weapon.Melee.".</summary>
    private static string? WeaponKey(IEnumerable<string> tags)
    {
        string? best = null;
        foreach (string tag in tags)
        {
            foreach (string marker in new[] { "Weapon.Ranged.", "Weapon.Melee." })
            {
                int at = tag.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                string key = tag[(at + marker.Length)..].Trim('.');
                if (key.Length > 0 && (best is null || key.Length > best.Length)) best = key;
            }
        }
        return best;
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? DeathCauseName(object? value) => value switch
    {
        null => null,
        Enum e => e.ToString(),
        IConvertible c when int.TryParse(c.ToString(), out int number) &&
                            number >= 0 && number < DeathCauseByValue.Length => DeathCauseByValue[number],
        _ => value.ToString()
    };

    private static string? FromDeathCause(object? value) =>
        DeathCauseName(value) is { } name && DeathCauseNames.TryGetValue(name, out string? display) ? display : null;
}
