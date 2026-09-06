using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Gardener.Game;

/// <summary>
/// Ground truth: a passive read of <c>HousingObjectManager.DataMap</c>, the same route
/// <c>Gardener/Helpers/DebugDump.cs:AppendDataMap</c> uses. One producer, no menu, no interaction —
/// for each discovered patch, <c>DataMap[FurnitureIndex]</c> gives eight value sets, slot <c>N</c> is
/// bed <c>N+1</c>, <c>Value1</c> is the seed row and <c>Value2</c> is the stage. Everything downstream
/// reads this for *what* is planted; the journal is read only for *when*.
/// </summary>
public static class GardenMemory
{
    // A2: DataMap value-set slots beyond a patch's bed count are assumed unused padding. Latched so
    // the first patch that falsifies it is reported once, not every poll.
    private static bool tailSlotLatchFired;

    // A5 groundwork / general sanity: an out-of-range stage or a Value1 that doesn't resolve to a
    // real outdoor seed row falsifies the assumed encoding.
    private static bool badValueLatchFired;

    // The one open structural question: whether any value past the seed row and stage carries the
    // wilt state. Every bed observed so far reads 0 on all three of Value3, Value4 and Value5.
    private static bool wiltLatchFired;

    // One condition snapshot per bed per session: a wilted bed is re-read every poll for as long as
    // it stays wilted, and the second file through would say what the first already did.
    private static readonly HashSet<string> conditionSnapshotsTaken = new();

    /// <summary>
    /// The passive read for one patch, for bed numbers <c>1..patch.Kind.BedCount()</c>.
    /// <paramref name="patch"/> must be a value freshly returned by <see cref="PatchDiscovery.Refresh"/>
    /// on this call — <c>FurnitureIndex</c> is a position in a live array that shifts when furniture
    /// is added or removed, and is never persisted. Returns an empty list, never eight empty beds,
    /// when the <c>DataMap</c> has no entry for the patch: "no data" and "the whole patch was
    /// harvested" must not be the same value.
    /// </summary>
    public static unsafe IReadOnlyList<BedState> Read(Patch patch)
    {
        if (patch.FurnitureIndex < 0)
            return Array.Empty<BedState>();

        // SAFETY: called from the framework thread (Plugin.OnFrameworkUpdate via
        // PatchDiscovery.Tick / GardenMemory.Poll); HousingManager.Instance() is a static client
        // pointer, and OutdoorTerritory is only non-null while standing in an outdoor housing
        // territory, checked before the furniture object manager is touched.
        var housing = HousingManager.Instance();
        if (housing == null || housing->OutdoorTerritory == null)
            return Array.Empty<BedState>();

        var objectManager = &housing->OutdoorTerritory->FurnitureManager.ObjectManager;
        if (!objectManager->DataMap.TryGetValuePointer((ushort)patch.FurnitureIndex, out var dataPtr) || dataPtr == null)
            return Array.Empty<BedState>();

        var readAt = DateTimeOffset.UtcNow;
        var bedCount = patch.Kind.BedCount();
        var valueSets = dataPtr->ValueSets;
        var result = new List<BedState>(bedCount);

        for (var i = 0; i < valueSets.Length; i++)
        {
            var vs = valueSets[i];

            if (i >= bedCount)
            {
                if ((vs.Value1 != 0 || vs.Value2 != 0) && !tailSlotLatchFired)
                {
                    tailSlotLatchFired = true;
                    Plugin.Logger.Warning(
                        $"[GardenMemory] {patch.Key} slot {i} is beyond the {patch.Kind} bed count " +
                        $"({bedCount}) but reads V1={vs.Value1} V2={vs.Value2}; tail slots are not padding");
                }
                continue;
            }

            var seedRow = vs.Value1;
            var stage = vs.Value2;

            if (!badValueLatchFired && (stage > 4 || (seedRow != 0 && !SeedItems.IsOutdoorSeed(seedRow))))
            {
                badValueLatchFired = true;
                Plugin.Logger.Warning(
                    $"[GardenMemory] {patch.Key} bed {i + 1} reads V1={seedRow} V2={stage}, which " +
                    "falsifies the assumed stage/seed-row encoding");
            }

            if ((vs.Value3 != 0 || vs.Value4 != 0 || vs.Value5 != 0) && !wiltLatchFired)
            {
                wiltLatchFired = true;
                Plugin.Logger.Warning(
                    $"[GardenMemory] {patch.Key} bed {i + 1} reads V3={vs.Value3} V4={vs.Value4} " +
                    $"V5={vs.Value5} (all three were 0 on every bed observed so far); " +
                    "see the wilt-*.txt snapshot");
                WriteWiltSnapshot(patch, i + 1, seedRow, stage, vs.Value3, vs.Value4, vs.Value5);
            }

            result.Add(new BedState(patch.Key, i + 1, seedRow, stage, vs.Value3, vs.Value4, vs.Value5, readAt));
        }

        return result;
    }

    /// <summary>Every patch in <see cref="PatchDiscovery.Patches"/>, flattened.</summary>
    public static IReadOnlyList<BedState> Poll()
    {
        var result = new List<BedState>();
        foreach (var patch in PatchDiscovery.Patches)
            result.AddRange(Read(patch));
        return result;
    }

    /// <summary>Captures the evidence for the wilt candidate the first time it ever fires, so it
    /// doesn't require the player to be running <c>/gardener dump</c> at that exact moment.</summary>
    private static void WriteWiltSnapshot(Patch patch, int bedNumber, ushort seedRow, byte stage, byte value3, byte value4, byte value5)
    {
        Write($"wilt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            $"patch={patch.Key} bed={bedNumber} seedRow={seedRow} seedName={SeedName(seedRow)} " +
            $"stage={stage} value3={value3} value4={value4} value5={value5}\n");
    }

    /// <summary>
    /// Every bed in one patch, with all five of its <c>DataMap</c> values, written at the one moment
    /// the game itself named a bed's condition — a <c>TALK_*</c> line attributed to
    /// <paramref name="bedNumber"/>. The other beds are the control group: same patch, same read,
    /// a condition the line did not claim. Whichever value carries wilt has to differ between the
    /// named bed and at least one sibling here, and if none of the five does then wilt is not in
    /// <c>ValueSets</c> at all and the search moves to the rest of the entry.
    /// </summary>
    public static void WriteConditionSnapshot(Patch patch, int bedNumber, string condition, IReadOnlyList<BedState> states)
    {
        if (!conditionSnapshotsTaken.Add($"{patch.Key}|{bedNumber}|{condition}"))
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"condition={condition} patch={patch.Key} namedBed={bedNumber} kind={patch.Kind} at={DateTimeOffset.Now:O}");
        foreach (var s in states)
            sb.AppendLine(
                $"  bed={s.BedNumber}{(s.BedNumber == bedNumber ? " <- named by the line" : "")} " +
                $"seedRow={s.SeedRow} seedName={SeedName(s.SeedRow)} stage={s.Stage} " +
                $"value3={s.Value3} value4={s.Value4} value5={s.Value5}");

        Write($"wilt-{condition}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt", sb.ToString());
    }

    private static string SeedName(ushort seedRow) =>
        seedRow != 0 && SeedItems.SeedItemForRow(seedRow) is { } itemId
            ? XivHubPluginKit.Inventory.ItemSheet.Name(itemId)
            : "unknown";

    /// <summary>Writes one snapshot beside the config, so the evidence survives without the player
    /// running <c>/gardener dump</c> at that exact moment.</summary>
    private static void Write(string fileName, string body)
    {
        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), body);
        }
        catch (Exception ex)
        {
            Plugin.Logger.Warning(ex, "[GardenMemory] failed to write snapshot");
        }
    }
}
