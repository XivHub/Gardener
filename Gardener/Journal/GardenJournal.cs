using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;

namespace Gardener.Journal;

/// <summary>The persisted shape of <c>journal.json</c>.</summary>
internal sealed class JournalFile
{
    public int Version { get; set; } = 1;
    public List<BedRecord> Records { get; set; } = new();
    public List<HouseRecord> Houses { get; set; } = new();
    public Calibration Calibration { get; set; } = new();
}

/// <summary>
/// Loads, saves and reconciles the persistent journal at <c>&lt;pluginConfigDir&gt;/journal.json</c>.
/// Keyed by <c>(PatchKey, BedNumber)</c> — house and bed, never a character — because a garden belongs
/// to the plot it sits on: whichever character last tended or observed a bed, the plant itself does
/// not know or care, so records are shared across every character on the account. Saves are debounced
/// and written atomically (temp file, then <see cref="File.Move"/> over the target) with one rolling
/// backup, so a client kill mid-save leaves the previous <c>journal.json</c> intact.
/// </summary>
public static class GardenJournal
{
    private const string FileName = "journal.json";
    private const string BackupFileName = "journal.bak.json";
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    private static readonly Dictionary<(string PatchKey, int BedNumber), BedRecord> records = new();
    private static readonly Dictionary<string, HouseRecord> houses = new();
    private static bool dirty;
    private static DateTimeOffset lastSaveAt = DateTimeOffset.MinValue;

    public static Calibration Calibration { get; private set; } = new();

    static GardenJournal()
    {
        Load();
    }

    public static BedRecord? Get(string patchKey, int bedNumber) =>
        records.TryGetValue((patchKey, bedNumber), out var record) ? record : null;

    public static void Upsert(BedRecord record)
    {
        records[(record.PatchKey, record.BedNumber)] = record;
        dirty = true;
    }

    public static void Remove(string patchKey, int bedNumber)
    {
        if (records.Remove((patchKey, bedNumber)))
            dirty = true;
    }

    /// <summary>Every record for a house, identified by its <see cref="HouseKeyInfo.KeyString"/>
    /// prefix on <see cref="BedRecord.PatchKey"/> — not by which character can currently reach it,
    /// so a garden on another character's FC still shows up here.</summary>
    public static IReadOnlyList<BedRecord> AllForHouse(string houseKey) =>
        records.Values.Where(r => r.PatchKey.StartsWith(houseKey + ":", StringComparison.Ordinal)).ToList();

    public static IReadOnlyList<string> AllPatchKeys =>
        records.Values.Select(r => r.PatchKey).Distinct().ToList();

    /// <summary>Records whose <see cref="BedRecord.PatchKey"/> is not among the patches currently
    /// discovered live — the patch was physically moved, or its house is unreachable right now. An
    /// explicit list for the UI, never silently dropped.</summary>
    public static IReadOnlyList<BedRecord> Orphans(IEnumerable<string> livePatchKeys)
    {
        var live = new HashSet<string>(livePatchKeys, StringComparer.Ordinal);
        return records.Values.Where(r => !live.Contains(r.PatchKey)).ToList();
    }

    /// <summary>Drops records whose <see cref="BedRecord.LastSeenAt"/> is older than
    /// <paramref name="maxAge"/> — a bed emptied while the plugin was watching is already dropped by
    /// <see cref="Reconcile"/> the moment it is seen empty, so what this clears is records for houses
    /// the player has stopped visiting on any character.</summary>
    public static void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var stale = records.Where(kv => kv.Value.LastSeenAt < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
            records.Remove(key);
        if (stale.Count > 0)
            dirty = true;
    }

    /// <summary>Characters observed standing in this house with permission to be there — enough to
    /// tell a reminder who to log in as, without a live scan.</summary>
    public static IReadOnlyList<string> CharactersWithAccess(string houseKey) =>
        houses.TryGetValue(houseKey, out var house) ? house.ObservedCharacters : Array.Empty<string>();

    public static EstateType? EstateTypeFor(string houseKey) =>
        houses.TryGetValue(houseKey, out var house) ? house.EstateType : null;

    /// <summary>
    /// Records that <paramref name="characterName"/> was just observed standing in
    /// <paramref name="houseKey"/> with the game's own permission to be there (reaching this call at
    /// all already proves that), and, when known, which estate slot owns it. House-level, never
    /// character-scoped: each of the user's characters can own a separate house, and this is what
    /// lets a reminder for one character's garden survive being checked from another's session.
    /// </summary>
    public static void RecordHouseAccess(string houseKey, EstateType? estateType, string? characterName)
    {
        if (!houses.TryGetValue(houseKey, out var house))
        {
            house = new HouseRecord { HouseKey = houseKey };
            houses[houseKey] = house;
        }

        if (estateType is { } et && house.EstateType != et)
        {
            house.EstateType = et;
            dirty = true;
        }

        if (!string.IsNullOrEmpty(characterName) && !house.ObservedCharacters.Contains(characterName, StringComparer.Ordinal))
        {
            house.ObservedCharacters.Add(characterName);
            dirty = true;
        }
    }

    /// <summary>
    /// Applies a passive <see cref="GardenMemory"/> read to the journal. Every transition below is the
    /// complete set; nothing else changes a record. A bed number this call receives no
    /// <see cref="BedState"/> for — an unread patch, an out-of-range player, a missing
    /// <c>DataMap</c> entry — is left completely untouched, which is what keeps a transient read gap
    /// from wiping the journal.
    /// </summary>
    public static void Reconcile(IReadOnlyList<BedState> states)
    {
        var observer = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;

        foreach (var state in states)
        {
            var key = (state.PatchKey, state.BedNumber);
            var hasRecord = records.TryGetValue(key, out var record);

            if (state.IsEmpty)
            {
                // Harvested or removed while away: the harvestable moment was never observed, so no
                // calibration sample is pushed for it either.
                if (hasRecord)
                {
                    records.Remove(key);
                    dirty = true;
                }
                continue;
            }

            if (!hasRecord)
            {
                // The normal first-run case for a garden Gardener did not plant, and no longer a
                // seed-identity problem: memory names the seed, only the clock is missing.
                records[key] = FreshRecord(state, observer);
                dirty = true;
                continue;
            }

            if (record!.SeedRow != state.SeedRow)
            {
                var oldSeedName = SeedName(record.SeedRow);
                var newSeedName = SeedName(state.SeedRow);
                records[key] = FreshRecord(state, observer);
                dirty = true;
                Plugin.Logger.Information(
                    $"[GardenJournal] {state.PatchKey} bed {state.BedNumber}: {oldSeedName} was replaced " +
                    $"with {newSeedName} while unobserved");
                continue;
            }

            if (state.Stage < record.LastSeenStage)
            {
                // Growth is monotone within a cycle, so a decrease means the cycle changed underneath
                // the plugin (harvested and replanted with the same seed while away): the same reset
                // as a seed change, minus the seed itself.
                records[key] = FreshRecord(state, observer);
                dirty = true;
                Plugin.Logger.Warning(
                    $"[GardenJournal] {state.PatchKey} bed {state.BedNumber}: stage went " +
                    $"{record.LastSeenStage} -> {state.Stage}, which never happens within one cycle; " +
                    "treating it as a cycle change the plugin missed");
                continue;
            }

            record.LastSeenStage = state.Stage;
            record.LastSeenAt = state.ReadAt;
            record.LastSeenByCharacter = observer;
            if (state.Stage == 4 && record.FirstSeenStage4At is null)
            {
                record.FirstSeenStage4At = state.ReadAt;
                RecordStage4Sample(record, state.ReadAt);
            }
            dirty = true;
        }
    }

    private static BedRecord FreshRecord(BedState state, string? observer) => new()
    {
        PatchKey = state.PatchKey,
        BedNumber = state.BedNumber,
        SeedRow = state.SeedRow,
        LastSeenStage = state.Stage,
        LastSeenAt = state.ReadAt,
        LastSeenByCharacter = observer,
        PlantedAt = null,
        PlantedAtEstimated = false,
        PlantedByGardener = false,
        // SoilItemId, LastTendedAt, LastFertilizedAt, FertilizerCount, FirstSeenStage4At and
        // FirstSeenHarvestOfferedAt all take their type's default: unknown until this plugin
        // observes the fact itself, never guessed from the seed that used to be here.
    };

    private static void RecordStage4Sample(BedRecord record, DateTimeOffset observedAt)
    {
        if (!Plugin.C.CollectGrowSamples)
            return;
        if (!record.PlantedByGardener || record.PlantedAtEstimated)
            return;
        if (record.PlantedAt is not { } plantedAt)
            return;

        Calibration.Record(record.SeedRow, CalibrationSeriesKind.Stage4, observedAt - plantedAt);
    }

    private static string SeedName(ushort row)
    {
        if (row != 0 && SeedItems.SeedItemForRow(row) is { } itemId)
            return XivHubPluginKit.Inventory.ItemSheet.Name(itemId);
        return $"row {row}";
    }

    /// <summary>Saves at most once every <see cref="SaveDebounce"/> while dirty; called every
    /// framework tick, cheap when there is nothing to do.</summary>
    public static void Tick()
    {
        if (!dirty)
            return;
        if (DateTimeOffset.UtcNow - lastSaveAt < SaveDebounce)
            return;
        Save();
    }

    /// <summary>Saves immediately regardless of the debounce window. Called from dispose and logout.</summary>
    public static void Flush()
    {
        if (dirty)
            Save();
    }

    private static void Load()
    {
        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<JournalFile>(json, jsonOptions);
            if (file is null)
            {
                Plugin.Logger.Warning("[GardenJournal] journal.json deserialised to null; starting with an empty journal");
                return;
            }

            foreach (var record in file.Records)
                records[(record.PatchKey, record.BedNumber)] = record;
            foreach (var house in file.Houses)
                houses[house.HouseKey] = house;
            Calibration = file.Calibration;
        }
        catch (Exception ex)
        {
            Plugin.Logger.Error(ex, "[GardenJournal] load failed; starting with an empty journal");
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, FileName);
            var backupPath = Path.Combine(dir, BackupFileName);
            var tempPath = path + ".tmp";

            var file = new JournalFile
            {
                Version = 1,
                Records = records.Values.ToList(),
                Houses = houses.Values.ToList(),
                Calibration = Calibration,
            };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, jsonOptions));

            if (File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);
            File.Move(tempPath, path, overwrite: true);

            dirty = false;
            lastSaveAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Logger.Error(ex, "[GardenJournal] save failed");
        }
    }
}
