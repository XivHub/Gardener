using System;
using System.Collections.Generic;
using System.IO;
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

    // The one open structural question: whether Value3/Value4 ever carry the wilt state. Every bed
    // observed so far reads 0 on both.
    private static bool wiltLatchFired;

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

            if ((vs.Value3 != 0 || vs.Value4 != 0) && !wiltLatchFired)
            {
                wiltLatchFired = true;
                Plugin.Logger.Warning(
                    $"[GardenMemory] {patch.Key} bed {i + 1} reads V3={vs.Value3} V4={vs.Value4} " +
                    "(both were 0 on every bed observed so far); see the wilt-*.txt snapshot");
                WriteWiltSnapshot(patch, i + 1, seedRow, stage, vs.Value3, vs.Value4);
            }

            result.Add(new BedState(patch.Key, i + 1, seedRow, stage, vs.Value3, vs.Value4, readAt));
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
    private static void WriteWiltSnapshot(Patch patch, int bedNumber, ushort seedRow, byte stage, byte value3, byte value4)
    {
        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"wilt-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
            var seedName = seedRow != 0 && SeedItems.SeedItemForRow(seedRow) is { } itemId
                ? XivHubPluginKit.Inventory.ItemSheet.Name(itemId)
                : "unknown";
            File.WriteAllText(path,
                $"patch={patch.Key} bed={bedNumber} seedRow={seedRow} seedName={seedName} " +
                $"stage={stage} value3={value3} value4={value4}\n");
        }
        catch (Exception ex)
        {
            Plugin.Logger.Warning(ex, "[GardenMemory] failed to write wilt snapshot");
        }
    }
}
