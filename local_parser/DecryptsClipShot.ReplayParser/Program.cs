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
        /// <summary>Raw Fortnite tags of the final kill, shown on hover in Meter Checker.</summary>
        public string[] WeaponTags { get; init; } = [];
        /// <summary>False when the weapon isn't in weapons.json yet (only its class is shown).</summary>
        public bool WeaponKnown { get; init; }
        /// <summary>Codename part of the weapon tag, used by "Name this weapon".</summary>
        public string WeaponKey { get; init; } = "";
        /// <summary>Scoped / No-scope / Aimed (iron sights) / Not recorded; empty for non-snipers.</summary>
        public string ShotLabel { get; init; } = "";
        public string KillerPlatform { get; init; } = "";
        public string VictimPlatform { get; init; } = "";
        /// <summary>True when the final kill's weapon has a scope (used to tell scoped/no-scope from video).</summary>
        public bool WeaponHasScope { get; init; }
        /// <summary>True when the final kill was on an AI bot (bots don't get the kill streak effect).</summary>
        public bool VictimIsBot { get; init; }
        /// <summary>
        /// How many seconds before the end of the replay the final kill happened, or -1 if
        /// unknown. The app uses it to find the kill inside the saved clip for its screenshot.
        /// </summary>
        public double KillSecondsBeforeEnd { get; init; } = -1;
        public KillDetails? Furthest { get; init; }
    }

    private sealed record KillDetails(
        double DistanceUU,
        string Killer,
        string KillerPlatform,
        string Victim,
        string VictimPlatform,
        string Weapon,
        string Rarity,
        string[] WeaponTags,
        bool WeaponKnown,
        string WeaponKey,
        string ShotLabel);

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
            result = result with
            {
                KillSecondsBeforeEnd = LastEliminationSecondsBeforeEnd(replay),
                Furthest = FindFurthestKill(replay)
            };
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
        string rarity = KillRarity(tags, finalKill, victimData);
        WeaponResult weapon = WeaponCatalog.Resolve(tags, rarity, ReadMember(finalKill, "DeathCause"));
        rarity = CheckRarity(rarity, weapon);
        // Automatic trickshots count any sniper OR any heavy-ammo weapon
        // (Usopp's Slingshot, Flint-Knock Pistol, ...). Known weapons use
        // weapons.json; unknown ones fall back to tag keywords.
        bool sniper = weapon.IsSniper || (!weapon.Known && tags.Any(IsSniperTag));
        bool trickshotWeapon = sniper || weapon.IsHeavyAmmo || (!weapon.Known && tags.Any(IsHeavyAmmoTag));
        double distance = finalKill.Distance ?? 0;
        string weaponName = weapon.Name;
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
                distance, killer, victim, weaponName, rarity,
                sniper ? "Sniper" : trickshotWeapon ? "HeavyAmmo" : "Other",
                reason)
            {
                TrickshotWeapon = trickshotWeapon,
                WeaponTags = tags,
                WeaponKnown = weapon.Known,
                WeaponKey = weapon.TagKey ?? "",
                ShotLabel = ShotLabel(sniper, weapon.HasScope,
                    FindPlayer(players, finalKill.FinisherOrDowner, finalKill.FinisherOrDownerName), finalKill, tags),
                KillerPlatform = killerPlatform,
                VictimPlatform = victimPlatform
            };
        }

        if (tags.Length == 0 && weapon.Name == "Unknown")
            return Inconclusive("The final elimination did not contain weapon details.", placement);

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
            killer, victim, weaponName, rarity, shotType, summary)
        {
            TrickshotWeapon = trickshotWeapon,
            WeaponTags = tags,
            WeaponKnown = weapon.Known,
            WeaponKey = weapon.TagKey ?? "",
            ShotLabel = ShotLabel(sniper, weapon.HasScope, owner, finalKill, tags),
            KillerPlatform = killerPlatform,
            VictimPlatform = victimPlatform,
            WeaponHasScope = sniper && weapon.HasScope,
            VictimIsBot = IsBot(victimData) || ReadMember(finalKill, "PlayerIsBot") is true
        };
    }

    private static bool IsBot(PlayerData? player) =>
        player is not null && ReadMember(player, "IsBot") is true;

    // The replay's own clock (milliseconds from the start of the recording) for the last
    // elimination event, compared with the replay's length. Read by reflection so a reader
    // version without these members just reports "unknown" (-1).
    private static double LastEliminationSecondsBeforeEnd(FortniteReplay replay)
    {
        try
        {
            object? info = ReadMember(replay, "Info");
            object? lengthValue = info is null ? null : ReadMember(info, "LengthInMs");
            if (lengthValue is null) return -1;
            double length = Convert.ToDouble(lengthValue);
            if (ReadMember(replay, "Eliminations") is not IEnumerable eliminations) return -1;
            double last = -1;
            foreach (object? elimination in eliminations)
            {
                if (elimination is null) continue;
                object? eventInfo = ReadMember(elimination, "Info");
                object? start = eventInfo is null ? null : ReadMember(eventInfo, "StartTime");
                if (start is null) continue;
                double time = Convert.ToDouble(start);
                if (time > last) last = time;
            }
            if (last < 0 || length <= 0 || last > length) return -1;
            double seconds = (length - last) / 1000.0;
            return seconds <= 900 ? Math.Round(seconds, 3) : -1;
        }
        catch
        {
            return -1;
        }
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
        string rarity = KillRarity(tags, best, victimData);
        WeaponResult weapon = WeaponCatalog.Resolve(tags, rarity, ReadMember(best, "DeathCause"));
        rarity = CheckRarity(rarity, weapon);
        bool furthestSniper = weapon.IsSniper || (!weapon.Known && tags.Any(IsSniperTag));
        return new KillDetails(
            best.Distance ?? 0,
            ResolvePlayerName(players, best.FinisherOrDowner, best.FinisherOrDownerName),
            ResolvePlatform(players, best.FinisherOrDowner, best.FinisherOrDownerName),
            ResolvePlayerName(players, best.PlayerId, best.PlayerName),
            ResolvePlatform(players, best.PlayerId, best.PlayerName),
            weapon.Name,
            rarity,
            tags,
            weapon.Known,
            weapon.TagKey ?? "",
            ShotLabel(furthestSniper, weapon.HasScope,
                FindPlayer(players, best.FinisherOrDowner, best.FinisherOrDownerName), best, tags));
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

    // The kill's own tags (the hit that killed) describe the weapon. The
    // victim's DeathTags can include unrelated status tags, so they are only
    // used when the kill carries no tags at all.
    private static string[] ReadKillTags(KillFeedEntry entry, PlayerData? victim)
    {
        string[] killerSide = Clean((entry.DeathTags ?? [])
            .Concat(ReadStringValues(ReadMember(entry, "FinisherOrDownerTags"))));
        return killerSide.Length > 0 ? killerSide : Clean(victim?.DeathTags ?? []);
    }

    private static string[] ReadAllTags(KillFeedEntry entry, PlayerData? victim) =>
        Clean((entry.DeathTags ?? [])
            .Concat(ReadStringValues(ReadMember(entry, "FinisherOrDownerTags")))
            .Concat(victim?.DeathTags ?? []));

    private static string[] Clean(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string KillRarity(string[] tags, KillFeedEntry entry, PlayerData? victim)
    {
        string rarity = ExtractRarity(tags);
        return rarity != "Unknown" ? rarity : ExtractRarity(ReadAllTags(entry, victim));
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

    // Only gameplay/event tags can say a shot was a no-scope. Weapon identity
    // tags can't: the Hunting Rifle's codename is literally "Sniper.NoScope".
    private static bool IsExplicitNoScopeTag(string tag) =>
        !tag.Contains("Weapon.", StringComparison.OrdinalIgnoreCase) &&
        (tag.Contains("noscope", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("no.scope", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hipfire", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains("hip.fire", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Player-facing shot description for sniper kills: whether the shooter was
    /// aiming down sights in the 1.5 s before the kill (read from the replay's
    /// movement data). Empty for non-snipers.
    /// </summary>
    private static string ShotLabel(bool sniper, bool hasScope, PlayerData? shooter,
                                    KillFeedEntry kill, IEnumerable<string> tags)
    {
        if (!sniper) return "";
        if (shooter is null) return "Not recorded";
        string shot = ClassifySniperShot(shooter, kill, tags, out bool known);
        if (!known) return "Not recorded";
        if (shot == "NoScope") return "No-scope";
        return hasScope ? "Scoped" : "Aimed (iron sights)";
    }

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

    // A rarity only counts when a tag names it as a whole word
    // ("Item.Rarity.Epic"), not when the letters appear inside another word.
    private static string ExtractRarity(IEnumerable<string> tags)
    {
        string[] rarities = ["Mythic", "Exotic", "Legendary", "Epic", "Rare", "Uncommon", "Common"];
        string[][] words = tags
            .Select(tag => tag.Split(['.', '_', '-', ' ', '/', ':'], StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        foreach (string rarity in rarities)
        {
            if (words.Any(parts => parts.Any(part => part.Equals(rarity, StringComparison.OrdinalIgnoreCase))))
                return rarity;
        }
        return "Unknown";
    }

    // Weapons that only come in certain rarities (weapons.json "rarities") never
    // show one they don't come in. If the replay doesn't say which, the possible
    // ones are shown ("Epic or Legendary").
    private static string CheckRarity(string rarity, WeaponResult weapon)
    {
        string[] allowed = weapon.Rarities ?? [];
        if (allowed.Length == 0) return rarity;
        string? match = allowed.FirstOrDefault(r => r.Equals(rarity, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        return allowed.Length == 1 ? allowed[0] : string.Join(" or ", allowed);
    }
}
