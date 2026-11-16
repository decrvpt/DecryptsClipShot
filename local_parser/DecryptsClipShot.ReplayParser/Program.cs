using System.Collections;
using System.Reflection;
using System.Text.Json;
using FortniteReplayReader;
using FortniteReplayReader.Models;
using Unreal.Core.Models.Enums;

namespace DecryptsClipShot.ReplayParser;

internal static class Program
{
    private sealed record LocalReplayResult(
        bool Conclusive,
        int Placement,
        bool OwnerFinalKill,
        bool Sniper,
        double DistanceUU,
        string Killer,
        string Victim,
        string Weapon,
        string Rarity,
        string ShotType,
        string Reason)
    {
        // Added for the Meter Checker detail cards. Declared after the original
        // fields so the engine's simple JSON reader still finds the top-level
        // "killer", "distanceUU" etc. first.
        /// <summary>
        /// True when the final kill used a weapon that counts for an automatic
        /// trickshot: any sniper, or any weapon that uses heavy ammo.
        /// </summary>
        public bool TrickshotWeapon { get; init; }
        public string KillerPlatform { get; init; } = "";
        public string VictimPlatform { get; init; } = "";
        public KillDetails? Furthest { get; init; }
    }

    private sealed record KillDetails(
        double DistanceUU,
        string Killer,
        string KillerPlatform,
        string Victim,
        string VictimPlatform,
        string Weapon,
        string Rarity);

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: ClipShotReplayParser.exe <replay-file> <result-json>");
            return 2;
        }

        string replayPath = Path.GetFullPath(args[0]);
        string resultPath = Path.GetFullPath(args[1]);

        try
        {
            // Full mode retains movement/targeting samples used for conservative
            // no-scope detection. Parsing happens after the match, not while gaming.
            var reader = new ReplayReader(null, ParseMode.Full);
            FortniteReplay replay = reader.ReadReplay(replayPath);
            LocalReplayResult result = Analyze(replay, reader);
            result = result with { Furthest = FindFurthestKill(replay) };
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            Console.WriteLine(result.Reason);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Local replay parsing failed: {exception.Message}");
            return 1;
        }
    }

    private static LocalReplayResult Analyze(FortniteReplay replay, ReplayReader reader)
    {
        List<PlayerData> players = replay.PlayerData?.ToList() ?? [];
        List<KillFeedEntry> feed = replay.KillFeed?.ToList() ?? [];
        PlayerData? owner = ResolveReplayOwner(replay, reader, players, out string ownerResolution);

        if (owner is null)
            return Inconclusive(
                "The local parser could not identify the replay owner from either IsReplayOwner " +
                $"or Fortnite's recorder/channel mapping ({ownerResolution}).");

        int placement = owner.Placement is > 0
            ? owner.Placement.Value
            : replay.TeamStats is { Position: > 0 }
                ? (int)replay.TeamStats.Position
                : 0;

        if (placement == 0)
            return Inconclusive("The local parser could not determine match placement.");

        List<KillFeedEntry> completedEvents = feed
            .Where(entry => !entry.IsRevived && !entry.IsDowned)
            .OrderByDescending(EventTime)
            .ToList();

        KillFeedEntry? finalKill = SelectFinalElimination(owner, placement, completedEvents,
            out int ignoredLaterEvents);

        if (finalKill is null)
            return Inconclusive("The local parser found no completed elimination events.", placement);

        bool ownerFinalKill = IsOwnerFinisher(owner, finalKill);
        string killer = ResolvePlayerName(players, finalKill.FinisherOrDowner, finalKill.FinisherOrDownerName);
        string victim = ResolvePlayerName(players, finalKill.PlayerId, finalKill.PlayerName);
        PlayerData? victimData = finalKill.PlayerId.HasValue
            ? players.FirstOrDefault(player => player.Id == finalKill.PlayerId)
            : players.FirstOrDefault(player =>
                string.Equals(player.PlayerName, finalKill.PlayerName, StringComparison.OrdinalIgnoreCase));
        string[] tags = ReadKillTags(finalKill, victimData);
        bool sniper = tags.Any(IsSniperTag);
        // Automatic trickshots count any sniper OR any heavy-ammo weapon
        // (Hand Cannon, Flint-Knock Pistol, ...).
        bool trickshotWeapon = sniper || tags.Any(IsHeavyAmmoTag);
        double distance = finalKill.Distance ?? 0;
        string weaponName = ExtractWeapon(tags);
        string killerPlatform = ResolvePlatform(players, finalKill.FinisherOrDowner, finalKill.FinisherOrDownerName);
        string victimPlatform = ResolvePlatform(players, finalKill.PlayerId, finalKill.PlayerName);

        if (!ownerFinalKill)
        {
            string reason = tags.Length == 0
                ? "The last valid elimination did not contain weapon tags. Automatic clips only count the replay owner's kills."
                : trickshotWeapon && distance > 0
                    ? $"The last valid kill was a {weaponName} elimination by {killer} at " +
                      $"{distance / 100.0:F1} meters. Automatic clips only count the replay owner's kills."
                    : ignoredLaterEvents > 0
                        ? $"After ignoring {ignoredLaterEvents} non-combat or disconnect event(s), " +
                          "the last valid elimination was not the replay owner's sniper or heavy-ammo kill."
                        : "The last valid elimination was not the replay owner's sniper or heavy-ammo kill.";

            return new LocalReplayResult(true, placement, false, sniper,
                distance, killer, victim, weaponName, ExtractRarity(tags),
                sniper ? "Sniper" : trickshotWeapon ? "HeavyAmmo" : "Other",
                reason)
            {
                TrickshotWeapon = trickshotWeapon,
                KillerPlatform = killerPlatform,
                VictimPlatform = victimPlatform
            };
        }

        if (tags.Length == 0)
            return Inconclusive("The final elimination did not contain weapon tags.", placement);

        if (trickshotWeapon && distance <= 0)
            return Inconclusive($"The final {weaponName} elimination did not contain a usable distance.", placement);

        bool targetingKnown = false;
        string shotType = sniper
            ? ClassifySniperShot(owner, finalKill, tags, out targetingKnown)
            : trickshotWeapon ? "HeavyAmmo" : "Other";

        string summary = !trickshotWeapon
            ? $"The replay owner's final elimination used {weaponName}, which is not a sniper or heavy-ammo weapon."
            : sniper
                ? $"Local analysis found a final {FormatShotType(shotType)} elimination at " +
                  $"{distance / 100.0:F1} meters ({ownerResolution}; " +
                  $"aim state {(targetingKnown ? "verified" : "unavailable, kept as Sniper")}; " +
                  $"ignored {ignoredLaterEvents} later non-combat event(s))."
                : $"Local analysis found a final heavy-ammo elimination ({weaponName}) at " +
                  $"{distance / 100.0:F1} meters ({ownerResolution}).";

        return new LocalReplayResult(true, placement, true, sniper, distance,
            killer, victim, weaponName, ExtractRarity(tags), shotType, summary)
        {
            TrickshotWeapon = trickshotWeapon,
            KillerPlatform = killerPlatform,
            VictimPlatform = victimPlatform
        };
    }

    // Longest measured elimination in the whole match, by any player.
    private static KillDetails? FindFurthestKill(FortniteReplay replay)
    {
        List<PlayerData> players = replay.PlayerData?.ToList() ?? [];
        KillFeedEntry? best = (replay.KillFeed ?? [])
            .Where(entry => !entry.IsRevived && !entry.IsDowned &&
                            entry.Distance is > 0 && IsRealCombatElimination(entry))
            .OrderByDescending(entry => entry.Distance)
            .FirstOrDefault();
        if (best is null) return null;

        PlayerData? victimData = FindPlayer(players, best.PlayerId, best.PlayerName);
        string[] tags = ReadKillTags(best, victimData);
        return new KillDetails(
            best.Distance ?? 0,
            ResolvePlayerName(players, best.FinisherOrDowner, best.FinisherOrDownerName),
            ResolvePlatform(players, best.FinisherOrDowner, best.FinisherOrDownerName),
            ResolvePlayerName(players, best.PlayerId, best.PlayerName),
            ResolvePlatform(players, best.PlayerId, best.PlayerName),
            ExtractWeapon(tags),
            ExtractRarity(tags));
    }

    private static PlayerData? FindPlayer(IEnumerable<PlayerData> players, int? id, string? name)
    {
        if (id.HasValue)
        {
            PlayerData? byId = players.FirstOrDefault(player => player.Id == id);
            if (byId is not null) return byId;
        }
        return string.IsNullOrWhiteSpace(name)
            ? null
            : players.FirstOrDefault(player =>
                string.Equals(player.PlayerName, name, StringComparison.OrdinalIgnoreCase));
    }

    // Fortnite platform codes as stored in replays; read by reflection so a
    // replay-reader version without the property simply yields "".
    private static string ResolvePlatform(IEnumerable<PlayerData> players, int? id, string? name)
    {
        PlayerData? player = FindPlayer(players, id, name);
        if (player is null) return "";
        if (ReadMember(player, "IsBot") is true) return "Bot";
        string code = (ReadMember(player, "Platform") as string ?? "").Trim().ToUpperInvariant();
        return code switch
        {
            "WIN" or "PC" => "PC",
            "MAC" => "Mac",
            "PSN" or "PS4" or "PS5" => "PlayStation",
            "XBL" or "XB1" or "XSX" or "XBOX" => "Xbox",
            "SWT" or "SWITCH" or "SW2" => "Switch",
            "AND" or "ANDROID" => "Android",
            "IOS" => "iOS",
            "" => "",
            _ => code
        };
    }

    private static KillFeedEntry? SelectFinalElimination(PlayerData owner, int placement,
                                                           List<KillFeedEntry> completedEvents,
                                                           out int ignoredLaterEvents)
    {
        ignoredLaterEvents = 0;
        if (completedEvents.Count == 0) return null;

        // KillFeed also receives match-exit metadata. In Creative and some current
        // Fortnite replays a player leaving after Game Over can be emitted after
        // the actual winning shot. Such an entry is not a new elimination and must
        // not replace the final combat event.
        List<KillFeedEntry> combatEvents = completedEvents
            .Where(IsRealCombatElimination)
            .ToList();

        KillFeedEntry? selected = combatEvents.FirstOrDefault();
        KillFeedEntry? ownerMeasuredKill = combatEvents
            .Where(entry => IsOwnerFinisher(owner, entry) && HasMeasuredOrWeaponHit(entry))
            .FirstOrDefault();

        // A first-place replay cannot end with an unmeasured, weaponless event by
        // another player replacing the winner's measured shot. Keep genuine later
        // combat kills (for example, a teammate's weapon kill) authoritative.
        if (placement == 1 && ownerMeasuredKill is not null &&
            (selected is null ||
             EventTime(selected) < EventTime(ownerMeasuredKill) ||
             !IsOwnerFinisher(owner, selected) && !HasMeasuredOrWeaponHit(selected)))
        {
            selected = ownerMeasuredKill;
        }

        if (selected is not null)
        {
            double selectedTime = EventTime(selected);
            ignoredLaterEvents = completedEvents.Count(entry =>
                EventTime(entry) > selectedTime && !IsRealCombatElimination(entry));
        }

        return selected;
    }

    private static bool IsRealCombatElimination(KillFeedEntry entry)
    {
        if (!HasFinisher(entry) || IsSelfElimination(entry)) return false;

        string[] entryTags = ReadKillTags(entry, null);
        if (entryTags.Any(IsExitTag)) return false;

        return entry.Distance.HasValue || entryTags.Length > 0;
    }

    private static bool HasMeasuredOrWeaponHit(KillFeedEntry entry)
    {
        if (entry.Distance is > 0) return true;
        return ReadKillTags(entry, null).Any(IsWeaponTag);
    }

    private static bool HasFinisher(KillFeedEntry entry) =>
        entry.FinisherOrDowner.HasValue || !string.IsNullOrWhiteSpace(entry.FinisherOrDownerName);

    private static bool IsSelfElimination(KillFeedEntry entry)
    {
        if (entry.PlayerId.HasValue && entry.FinisherOrDowner.HasValue &&
            entry.PlayerId.Value == entry.FinisherOrDowner.Value)
            return true;

        return !string.IsNullOrWhiteSpace(entry.PlayerName) &&
               !string.IsNullOrWhiteSpace(entry.FinisherOrDownerName) &&
               string.Equals(entry.PlayerName, entry.FinisherOrDownerName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ReadKillTags(KillFeedEntry entry, PlayerData? victim)
    {
        IEnumerable<string> deathTags = entry.DeathTags ?? [];
        IEnumerable<string> finisherTags = ReadStringValues(ReadMember(entry, "FinisherOrDownerTags"));
        IEnumerable<string> victimTags = victim?.DeathTags ?? [];

        return deathTags.Concat(finisherTags).Concat(victimTags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ReadStringValues(object? value)
    {
        if (value is string text)
        {
            yield return text;
            yield break;
        }
        if (value is not IEnumerable values) yield break;
        foreach (object? item in values)
            if (item is string tag) yield return tag;
    }

    private static bool IsExitTag(string tag) =>
        tag.Contains("logout", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("leftgame", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("left.game", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("playerleft", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("player.left", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("quit", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeaponTag(string tag) =>
        tag.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("ranged", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("sniper", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("huntingrifle", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hunting.rifle", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("shotgun", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("pistol", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("assault", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("melee", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("explosive", StringComparison.OrdinalIgnoreCase);

    private static LocalReplayResult Inconclusive(string reason, int placement = 0) =>
        new(false, placement, false, false, 0, "", "", "Unknown", "Unknown", "Unknown", reason);

    private static PlayerData? ResolveReplayOwner(FortniteReplay replay, ReplayReader reader,
                                                   List<PlayerData> players,
                                                   out string resolution)
    {
        PlayerData? owner = players.FirstOrDefault(player => player.IsReplayOwner);
        if (owner is not null)
        {
            resolution = "IsReplayOwner";
            return owner;
        }

        // FortniteReplayReader 3.0.1 only sets IsReplayOwner when RecorderId is
        // already known at the instant PlayerData is created. Some current
        // replays deliver RecorderId later, so recover the owner from the
        // builder's actor-channel map. Reflection is deliberately isolated here
        // and failure simply produces an explicit inconclusive result.
        if (!replay.GameData.RecorderId.HasValue)
        {
            resolution = "RecorderId missing";
            return null;
        }

        try
        {
            object? builder = FindField(reader.GetType(), "Builder")?.GetValue(reader);
            IDictionary? channelToActor = FindField(builder?.GetType(), "_channelToActor")
                ?.GetValue(builder) as IDictionary;
            IDictionary? playersByChannel = FindField(builder?.GetType(), "_players")
                ?.GetValue(builder) as IDictionary;
            if (channelToActor is null || playersByChannel is null)
            {
                resolution = "recorder mapping unavailable";
                return null;
            }

            uint recorderId = replay.GameData.RecorderId.Value;
            foreach (DictionaryEntry mapping in channelToActor)
            {
                if (Convert.ToUInt32(mapping.Value) != recorderId) continue;
                object? mapped = playersByChannel[mapping.Key];
                if (mapped is PlayerData mappedOwner)
                {
                    resolution = "RecorderId/channel fallback";
                    return mappedOwner;
                }
            }
        }
        catch (Exception exception)
        {
            resolution = $"recorder fallback failed: {exception.Message}";
            return null;
        }

        resolution = "RecorderId did not map to a player channel";
        return null;
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type is not null)
        {
            FieldInfo? field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field is not null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static string ClassifySniperShot(PlayerData owner, KillFeedEntry kill,
                                              IEnumerable<string> tags,
                                              out bool targetingKnown)
    {
        targetingKnown = false;
        if (tags.Any(IsExplicitNoScopeTag))
        {
            targetingKnown = true;
            return "NoScope";
        }

        double killTime = EventTime(kill);
        if (killTime == double.MinValue) return "Sniper";

        var samples = ReadTargetingSamples(owner)
            .Where(sample => sample.Time >= killTime - 1.5 && sample.Time <= killTime + 0.10)
            .OrderBy(sample => sample.Time)
            .ToList();
        if (samples.Count < 2 || samples[^1].Time - samples[0].Time < 0.10)
            return "Sniper";

        targetingKnown = true;
        // A scoped shot can be released before its projectile reaches the victim,
        // so any targeting sample in the pre-elimination window wins. We only call
        // it a no-scope when every nearby sample says targeting was false.
        if (samples.Any(sample => sample.Targeting)) return "Sniper";
        return "NoScope";
    }

    private static IEnumerable<(double Time, bool Targeting)> ReadTargetingSamples(PlayerData player)
    {
        object? locations = player.GetType().GetProperty("Locations")?.GetValue(player);
        if (locations is not IEnumerable enumerable) yield break;

        foreach (object? movement in enumerable)
        {
            if (movement is null) continue;
            object? targetingValue = ReadMember(movement, "bIsTargeting");
            if (targetingValue is not bool targeting) continue;
            object? timeValue = ReadMember(movement, "ReplicatedWorldTimeSecondsDouble")
                ?? ReadMember(movement, "ReplicatedWorldTimeSeconds");
            if (timeValue is null) continue;
            double time;
            try { time = Convert.ToDouble(timeValue); }
            catch { continue; }
            yield return (time, targeting);
        }
    }

    private static object? ReadMember(object instance, string name) =>
        instance.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
        ?? instance.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static bool IsExplicitNoScopeTag(string tag) =>
        tag.Contains("noscope", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("no.scope", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hipfire", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hip.fire", StringComparison.OrdinalIgnoreCase);

    private static string FormatShotType(string shotType) =>
        shotType == "NoScope" ? "no-scope sniper" : "sniper";

    private static double EventTime(KillFeedEntry entry) =>
        entry.ReplicatedWorldTimeSecondsDouble ?? entry.ReplicatedWorldTimeSeconds ?? double.MinValue;

    private static bool IsOwnerFinisher(PlayerData owner, KillFeedEntry entry)
    {
        if (owner.Id.HasValue && entry.FinisherOrDowner == owner.Id.Value) return true;

        string[] ownerNames =
        [
            owner.PlayerName ?? "",
            owner.PlayerNameCustomOverride ?? "",
            owner.StreamerModeName ?? "",
            owner.PlayerId ?? ""
        ];

        return !string.IsNullOrWhiteSpace(entry.FinisherOrDownerName) &&
               ownerNames.Any(name => !string.IsNullOrWhiteSpace(name) &&
                   string.Equals(name, entry.FinisherOrDownerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePlayerName(IEnumerable<PlayerData> players, int? id, string? fallback)
    {
        PlayerData? player = id.HasValue ? players.FirstOrDefault(candidate => candidate.Id == id) : null;
        return player?.PlayerName
            ?? player?.PlayerNameCustomOverride
            ?? player?.StreamerModeName
            ?? player?.PlayerId
            ?? fallback
            ?? "Unknown";
    }

    // Heavy ammo is matched by explicit ammo tags or by specific heavy-ammo
    // weapons. The word "heavy" alone is NOT enough: the Heavy Shotgun and
    // Heavy Assault Rifle use other ammo. Snipers are covered by IsSniperTag.
    // Add new heavy-ammo weapons here when Fortnite introduces them.
    private static readonly string[] HeavyAmmoMarkers =
    [
        "ammo.heavy", "ammoheavy", "heavyammo", "heavybullets", "heavy.bullets", "ammodataheavy",
        "handcannon", "hand.cannon", "hand_cannon", "deagle",
        "flintknock", "flint.knock", "flint_knock"
    ];

    private static bool IsHeavyAmmoTag(string tag) =>
        HeavyAmmoMarkers.Any(marker => tag.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsSniperTag(string tag) =>
        tag.Contains("sniper", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("huntingrifle", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hunting.rifle", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hunting rifle", StringComparison.OrdinalIgnoreCase);

    private static string ExtractWeapon(IEnumerable<string> tags)
    {
        foreach (string tag in tags)
        {
            string[] parts = tag.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (tag.StartsWith("Item.Weapon.Ranged.", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4)
                return PrettyWeapon(parts.Skip(3));
            if (tag.StartsWith("Weapon.Ranged.", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                return PrettyWeapon(parts.Skip(2));
        }

        string? sniperTag = tags.FirstOrDefault(IsSniperTag);
        return sniperTag is null
            ? "Unknown"
            : PrettyWeapon(sniperTag.Split('.', StringSplitOptions.RemoveEmptyEntries).TakeLast(2));
    }

    private static string Capitalize(string word) =>
        word.Equals("smg", StringComparison.OrdinalIgnoreCase) || word.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? word.ToUpperInvariant()
            : char.ToUpperInvariant(word[0]) + word[1..];

    private static readonly string[] WeaponClasses =
        ["Sniper", "Shotgun", "Pistol", "SMG", "Rifle", "Launcher", "Bow", "Crossbow", "Revolver"];

    private static readonly string[] RarityWords =
        ["Mythic", "Exotic", "Legendary", "Epic", "Rare", "Uncommon", "Common"];

    // Tag parts are ordered class-first ("Sniper", "Heavy"); players read the
    // variant first ("Heavy Sniper"). Rarity is shown on its own row.
    private static string PrettyWeapon(IEnumerable<string> parts)
    {
        List<string> words = parts
            .SelectMany(part => System.Text.RegularExpressions.Regex
                .Replace(part, "(?<=[a-z])(?=[A-Z])", " ")
                .Split(' ', '_', '-'))
            .Where(word => word.Length > 0 &&
                           !RarityWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            .Select(Capitalize)
            .ToList();
        if (words.Count == 0) return "Unknown";
        if (words.Count > 1 && WeaponClasses.Contains(words[0], StringComparer.OrdinalIgnoreCase))
        {
            string weaponClass = words[0];
            words.RemoveAt(0);
            // "Sniper.HuntingRifle" already names its class: "Hunting Rifle".
            if (!WeaponClasses.Contains(words[^1], StringComparer.OrdinalIgnoreCase) &&
                !words[^1].Equals("Cannon", StringComparison.OrdinalIgnoreCase))
                words.Add(weaponClass);
        }
        return string.Join(" ", words);
    }

    private static string ExtractRarity(IEnumerable<string> tags)
    {
        string[] rarities = ["Mythic", "Exotic", "Legendary", "Epic", "Rare", "Uncommon", "Common"];
        foreach (string rarity in rarities)
        {
            if (tags.Any(tag => tag.Contains(rarity, StringComparison.OrdinalIgnoreCase)))
                return rarity;
        }
        return "Unknown";
    }
}
