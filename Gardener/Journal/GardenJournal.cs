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

    /// <summary>The Goal tab's chosen target, a <c>GardeningSeed</c> row; 0 means no goal set. Player
    /// state, not a setting — it lives here rather than in <c>Configuration</c> — but a journal from
    /// before this field existed deserialises it to 0 for free, so <see cref="Version"/> stays 1.</summary>
    public uint GoalSeedRow { get; set; }

    /// <summary>Persisted patch identity, one <see cref="PatchRecord"/> per patch key ever seen. Same
    /// argument as <see cref="GoalSeedRow"/>: a journal from before this list existed deserialises it
    /// to an empty list for free, so this does not bump <see cref="Version"/> either.</summary>
    public List<PatchRecord> Patches { get; set; } = new();
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
    private static readonly Dictionary<string, PatchRecord> patches = new();

    /// <summary>When each bed was last observed empty, in-session only — never persisted, since it
    /// exists solely to date the next planting this session sees and would otherwise be journal
    /// bloat for <see cref="Prune"/> to clean up. Populated by <see cref="Reconcile"/>'s <c>IsEmpty</c>
    /// branch and consumed the moment a record is created for that bed.</summary>
    private static readonly Dictionary<(string PatchKey, int BedNumber), DateTimeOffset> lastSeenEmptyAt = new();

    private static bool dirty;
    private static DateTimeOffset lastSaveAt = DateTimeOffset.MinValue;

    public static Calibration Calibration { get; private set; } = new();

    private static uint goalSeedRow;

    /// <summary>The Goal tab's chosen target; 0 means none. Survives a restart the same way every other
    /// journal fact does, and every character on the account sees the same goal, matching a garden
    /// belonging to the plot rather than to whoever is standing in it.</summary>
    public static uint GoalSeedRow
    {
        get => goalSeedRow;
        set
        {
            if (goalSeedRow == value)
                return;
            goalSeedRow = value;
            dirty = true;
        }
    }

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

    /// <summary>Every bed record the journal holds, across every house on the account. The only
    /// source <see cref="Helpers.Reminders"/> is allowed to read: unlike <see cref="AllForHouse"/>,
    /// this needs no live house to filter by, which is what lets a reminder work from a house, or a
    /// world, the plugin is not currently standing in.</summary>
    public static IReadOnlyList<BedRecord> AllRecords => records.Values.ToList();

    /// <summary>Records whose <see cref="BedRecord.PatchKey"/> is not among the patches currently
    /// discovered live — the patch was placed into storage, physically moved, or its house is
    /// unreachable right now. An explicit list for the UI, never silently dropped.</summary>
    public static IReadOnlyList<BedRecord> Orphans(IEnumerable<string> livePatchKeys)
    {
        var live = new HashSet<string>(livePatchKeys, StringComparer.Ordinal);
        return records.Values.Where(r => !live.Contains(r.PatchKey)).ToList();
    }

    /// <summary>
    /// Sets <see cref="BedRecord.Parked"/> for every record under <paramref name="houseKey"/> whose
    /// patch key is not among <paramref name="livePatchKeys"/>, and clears it for every record whose
    /// patch key is: placing a patch or flowerpot into storage freezes every timer on it and drops it
    /// from discovery entirely, so its beds are parked, never emptied or harvested, and
    /// <see cref="Growth"/> must stop projecting a clock for them until the same patch key is
    /// discovered live again. Call only right after a fresh <see cref="PatchDiscovery.Refresh"/> for
    /// this house — <paramref name="livePatchKeys"/> otherwise cannot tell "confirmed absent" from
    /// "not looked at".
    /// </summary>
    public static void MarkParked(string houseKey, IEnumerable<string> livePatchKeys)
    {
        var live = new HashSet<string>(livePatchKeys, StringComparer.Ordinal);
        var changed = false;

        foreach (var record in records.Values)
        {
            if (!record.PatchKey.StartsWith(houseKey + ":", StringComparison.Ordinal))
                continue;

            var parked = !live.Contains(record.PatchKey);
            if (record.Parked == parked)
                continue;

            record.Parked = parked;
            changed = true;
        }

        if (changed)
            dirty = true;
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
    /// Records that <paramref name="patch"/> was just discovered live, at <paramref name="plotIndex"/>
    /// (zero-based, or null when unresolved). First sighting creates a <see cref="PatchRecord"/> with
    /// <see cref="PatchRecord.Ordinal"/> set to one past the highest ordinal already recorded for that
    /// patch's house — every later sighting only refreshes <see cref="PatchRecord.PlotIndex"/> and
    /// <see cref="PatchRecord.LastSeenAt"/>, and marks the journal dirty only when the plot actually
    /// changed, since <see cref="LastSeenAt"/> alone changing on every call would otherwise defeat the
    /// save debounce.
    /// </summary>
    public static void RecordPatchSeen(Patch patch, int? plotIndex)
    {
        var now = DateTimeOffset.UtcNow;

        if (!patches.TryGetValue(patch.Key, out var record))
        {
            var houseKey = patch.Key.Split(':')[0];
            var ordinal = 1;
            foreach (var existing in patches.Values)
            {
                if (existing.HouseKey == houseKey && existing.Ordinal >= ordinal)
                    ordinal = existing.Ordinal + 1;
            }

            patches[patch.Key] = new PatchRecord
            {
                PatchKey = patch.Key,
                HouseKey = houseKey,
                Kind = patch.Kind,
                BedCount = patch.Kind.BedCount(),
                PlotIndex = plotIndex,
                Ordinal = ordinal,
                LastSeenAt = now,
            };
            dirty = true;
            return;
        }

        if (record.PlotIndex != plotIndex)
        {
            record.PlotIndex = plotIndex;
            dirty = true;
        }
        record.LastSeenAt = now;
    }

    /// <summary>The persisted record for one patch key, or null when that patch has never been seen
    /// live since this field was added.</summary>
    public static PatchRecord? PatchInfo(string patchKey) =>
        patches.TryGetValue(patchKey, out var record) ? record : null;

    /// <summary>Every recorded patch belonging to <paramref name="houseKey"/>, in per-house
    /// <see cref="PatchRecord.Ordinal"/> order.</summary>
    public static IReadOnlyList<PatchRecord> PatchesForHouse(string houseKey) =>
        patches.Values.Where(p => p.HouseKey == houseKey).OrderBy(p => p.Ordinal).ToList();

    /// <summary>Every recorded patch's <see cref="PatchRecord.Kind"/>, across every house on the
    /// account, ordered by patch key for a reproducible result — the away-from-garden capacity
    /// fallback's only source when <see cref="Game.PatchDiscovery"/> itself has nothing live.</summary>
    /// <summary>The patch shapes of the house seen most recently, for sizing a route while the player
    /// is away from every garden. Scoped to one house on purpose: a step plants into the patches at
    /// the house being stood in, so summing an FC estate and a private one would promise a round no
    /// single visit can plant.</summary>
    public static IReadOnlyList<PatchKind> KnownPatchKinds()
    {
        var lastHouse = patches.Values
            .OrderByDescending(p => p.LastSeenAt)
            .Select(p => p.HouseKey)
            .FirstOrDefault();

        return lastHouse is null
            ? Array.Empty<PatchKind>()
            : patches.Values
                .Where(p => p.HouseKey == lastHouse)
                .OrderBy(p => p.PatchKey, StringComparer.Ordinal)
                .Select(p => p.Kind)
                .ToList();
    }

    /// <summary>
    /// Applies a passive <see cref="GardenMemory"/> read to the journal. Every transition below is the
    /// complete set; nothing else changes a record. A bed number this call receives no
    /// <see cref="BedState"/> for — an unread patch, an out-of-range player, a missing
    /// <c>DataMap</c> entry, or a patch parked in storage — is left completely untouched, which is what
    /// keeps a transient read gap from wiping the journal and, for the parked case, is exactly what
    /// keeps its clock frozen. <see cref="MarkParked"/> is the only thing that ever sets or clears
    /// <see cref="BedRecord.Parked"/>; this method never touches it.
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
                // Dated whether or not a record existed: an empty read is what lets the *next*
                // occupied read for this bed date its planting, and a record only existing on some
                // of the polls in between must not blind that dating.
                lastSeenEmptyAt[key] = state.ReadAt;

                // Harvested or removed while away: the harvestable moment was never observed, so no
                // calibration sample is pushed for it either.
                if (hasRecord)
                {
                    records.Remove(key);
                    dirty = true;
                }
                continue;
            }

            var hadEmptyObservation = lastSeenEmptyAt.TryGetValue(key, out var observedEmptyAt);

            if (!hasRecord)
            {
                // The bed was watched empty earlier this session: the planting happened somewhere in
                // [observedEmptyAt, state.ReadAt], so that gap is real evidence, not the plain
                // first-run case below where nothing was ever observed empty.
                records[key] = FreshRecord(state, observer, hadEmptyObservation ? observedEmptyAt : null);
                lastSeenEmptyAt.Remove(key);
                dirty = true;
                continue;
            }

            // A record already exists for this bed; any pending empty-observation for it is stale
            // (the transition it would have dated already produced a record through the branch above).
            lastSeenEmptyAt.Remove(key);

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

    /// <summary>
    /// Folds one classified crop chat line — the <c>TALK_*</c> sentence a bed interaction echoes,
    /// unreachable through the bed menu itself — into the record for the bed
    /// whose menu was open when it arrived. Every transition below is the complete set:
    /// <see cref="MenuKey.TalkNone"/> drops the record the same way an empty <see cref="Reconcile"/>
    /// read does; every other key requires an existing record to attach the observation to, since
    /// nothing here can invent a seed row or a planted-at.
    /// <see cref="MenuKey.TalkDepressed"/> and <see cref="MenuKey.TalkVigorous"/> only move
    /// <see cref="BedRecord.LastTendedAt"/> in the direction the sentence proves — never later than
    /// observed-wilted, never earlier than observed-healthy — and leave it untouched when the seed has
    /// no bundled wilt duration to derive a bound from, rather than inventing one.
    /// </summary>
    public static void ReconcileCropObservation(string patchKey, int bedNumber, MenuKey key, DateTimeOffset observedAt)
    {
        var recordKey = (patchKey, bedNumber);
        var hasRecord = records.TryGetValue(recordKey, out var record);

        if (key == MenuKey.TalkNone)
        {
            // Same as the passive read's IsEmpty branch, and for the same reason: the harvestable
            // moment was never observed, so no calibration sample is pushed either.
            if (hasRecord)
            {
                records.Remove(recordKey);
                dirty = true;
            }
            return;
        }

        if (!hasRecord)
            return; // No record to attach this observation to yet; the next passive Reconcile creates one.

        record!.LastObservedCropState = key;
        record.LastObservedCropStateAt = observedAt;
        dirty = true;

        switch (key)
        {
            case MenuKey.TalkDead:
                record.ObservedWithered = true;
                break;

            case MenuKey.TalkDepressed:
                if (SeedTable.Wilt(record.SeedRow) is { } wiltedFor)
                {
                    var noLaterThan = observedAt - TimeSpan.FromHours(wiltedFor.Hours);
                    if (record.LastTendedAt is not { } existing || noLaterThan < existing)
                        record.LastTendedAt = noLaterThan;
                }
                else
                {
                    Plugin.Logger.Information(
                        $"[GardenJournal] {patchKey} bed {bedNumber}: observed wilted (TALK_DEPRESSED) but " +
                        $"seed row {record.SeedRow} has no bundled wilt duration; LastTendedAt left as-is.");
                }
                break;

            case MenuKey.TalkVigorous:
                if (SeedTable.Wilt(record.SeedRow) is { } healthyFor)
                {
                    var noEarlierThan = observedAt - TimeSpan.FromHours(healthyFor.Hours);
                    if (record.LastTendedAt is { } existing && existing < noEarlierThan)
                        record.LastTendedAt = noEarlierThan;
                }
                break;

            case MenuKey.TalkRipe:
                if (record.FirstSeenHarvestOfferedAt is null)
                    record.FirstSeenHarvestOfferedAt = observedAt;
                RecordHarvestOfferedSample(record);
                break;
        }
    }

    /// <summary>
    /// <paramref name="observedEmptyAt"/> is the last time this bed was seen empty this session, if
    /// any — the only case where <see cref="BedRecord.PlantedAt"/> can be set here. It anchors
    /// <see cref="BedRecord.PlantedAt"/> and <see cref="BedRecord.LastTendedAt"/> (planting counts as
    /// tending) at that observation, with <see cref="BedRecord.PlantedAtUncertainty"/> carrying the
    /// gap to <see cref="BedState.ReadAt"/> — the true planting time is somewhere in that window,
    /// never assumed to be the stage the bed is now in, which is evidence of growth, not of a clock.
    /// </summary>
    private static BedRecord FreshRecord(BedState state, string? observer, DateTimeOffset? observedEmptyAt = null) => new()
    {
        PatchKey = state.PatchKey,
        BedNumber = state.BedNumber,
        SeedRow = state.SeedRow,
        LastSeenStage = state.Stage,
        LastSeenAt = state.ReadAt,
        LastSeenByCharacter = observer,
        PlantedAt = observedEmptyAt,
        PlantedAtEstimated = false,
        PlantedAtUncertainty = observedEmptyAt is { } emptyAt ? state.ReadAt - emptyAt : null,
        LastTendedAt = observedEmptyAt,
        PlantedByGardener = false,
        // SoilItemId, LastFertilizedAt, FertilizerCount, FirstSeenStage4At and FirstSeenHarvestOfferedAt
        // all take their type's default: unknown until this plugin observes the fact itself, never
        // guessed from the seed that used to be here.
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

    /// <summary>
    /// Pushes a <see cref="CalibrationSeriesKind.HarvestOffered"/> sample from
    /// <see cref="BedRecord.FirstSeenHarvestOfferedAt"/> — the act-time fact that a bed menu offered a
    /// <c>Harvest</c> entry, which the passive <see cref="GardenMemory"/> read cannot produce — under
    /// the same eligibility rule <see cref="Reconcile"/> applies to the Stage4 series: only for a bed
    /// this plugin itself planted with a real (not estimated) <see cref="BedRecord.PlantedAt"/>.
    /// </summary>
    public static void RecordHarvestOfferedSample(BedRecord record)
    {
        if (!Plugin.C.CollectGrowSamples)
            return;
        if (!record.PlantedByGardener || record.PlantedAtEstimated)
            return;
        if (record.PlantedAt is not { } plantedAt || record.FirstSeenHarvestOfferedAt is not { } offeredAt)
            return;

        Calibration.Record(record.SeedRow, CalibrationSeriesKind.HarvestOffered, offeredAt - plantedAt);
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
            foreach (var patch in file.Patches)
                patches[patch.PatchKey] = patch;
            Calibration = file.Calibration;
            goalSeedRow = file.GoalSeedRow;
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
                Patches = patches.Values.ToList(),
                Calibration = Calibration,
                GoalSeedRow = goalSeedRow,
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
