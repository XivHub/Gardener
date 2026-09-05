using System;
using System.Collections.Generic;
using System.Linq;

namespace Gardener.Game;

/// <summary>
/// Bed number -&gt; bed <c>EventObj</c> map, and the one place that states the reverse-<c>EntityId</c>
/// ordering rule: every bed <c>EventObj</c> of a patch reports the enclosing
/// patch's own world position, so <c>EntityId</c> is the only thing that tells them apart, and the
/// pairing proven at the two endpoints of one Deluxe patch is bed <c>n</c> = <c>Beds[Beds.Count - n]</c>,
/// where <c>Beds</c> is <see cref="PatchDiscovery"/>'s own ascending-<c>EntityId</c> order. This is a
/// starting guess, never a fact acted on directly: every caller must confirm it against the bed
/// menu's own "Nth Bed" prompt before acting, and correct it here when the prompt disagrees.
/// </summary>
public static class BedTargeting
{
    private sealed class PatchMap
    {
        public uint[] ByBedNumber = Array.Empty<uint>();
        public uint[] BuiltFromSortedIds = Array.Empty<uint>();
        public bool[] Verified = Array.Empty<bool>();
    }

    private static readonly Dictionary<string, PatchMap> maps = new();

    /// <summary>Entity ids are per-session; call on <c>IClientState.TerritoryChanged</c>.</summary>
    public static void Invalidate() => maps.Clear();

    /// <summary>
    /// The predicted <c>EntityId</c> for <paramref name="bedNumber"/>, rebuilding the patch's map
    /// first if its live bed <c>EntityId</c> set no longer matches the set the map was built from.
    /// Null when <paramref name="bedNumber"/> is out of range for the patch's own bed count.
    /// </summary>
    public static uint? Predict(Patch patch, int bedNumber)
    {
        if (bedNumber < 1 || bedNumber > patch.Kind.BedCount())
            return null;

        var map = GetOrBuild(patch);
        var index = bedNumber - 1;
        return index < map.ByBedNumber.Length ? map.ByBedNumber[index] : null;
    }

    /// <summary>
    /// The happy path: a live interaction proved <paramref name="bedNumber"/> reads correctly for
    /// the entity the map already predicted it as. Called after <see cref="GardenMenuText.ParseBedPatch"/>
    /// agrees with what <see cref="Predict"/> targeted, so <see cref="VerifiedBedCount"/> can count it.
    /// </summary>
    public static void MarkVerified(Patch patch, int bedNumber)
    {
        var map = GetOrBuild(patch);
        var index = bedNumber - 1;
        if (index >= 0 && index < map.Verified.Length)
            map.Verified[index] = true;
    }

    /// <summary>
    /// Applies the correction when the menu prompt disagrees with the prediction: swaps the two bed
    /// numbers' entries so the map stays a bijection, logs one warning naming the patch, the entity id
    /// and both numbers, and marks the observed bed number verified. The bed the entity was wrongly
    /// predicted as is left unverified again, since its own entry just changed under it.
    /// </summary>
    public static void Confirm(Patch patch, uint targetedEntityId, int observedBedNumber)
    {
        var map = GetOrBuild(patch);
        if (observedBedNumber < 1 || observedBedNumber > map.ByBedNumber.Length)
        {
            Plugin.Logger.Warning(
                $"[BedTargeting] {patch.Key}: observed bed number {observedBedNumber} is out of range " +
                $"for a {patch.Kind} patch ({map.ByBedNumber.Length} beds); cannot correct");
            return;
        }

        var wrongIndex = Array.IndexOf(map.ByBedNumber, targetedEntityId);
        if (wrongIndex < 0)
        {
            Plugin.Logger.Warning(
                $"[BedTargeting] {patch.Key}: targeted entity 0x{targetedEntityId:X8} is not in the " +
                "current map; cannot correct");
            return;
        }

        var observedIndex = observedBedNumber - 1;
        if (wrongIndex == observedIndex)
        {
            // Already correct: the caller's mismatch came from a stale predicted bed number, not a
            // wrong map entry. Nothing to swap.
            map.Verified[observedIndex] = true;
            return;
        }

        var displaced = map.ByBedNumber[observedIndex];
        map.ByBedNumber[observedIndex] = targetedEntityId;
        map.ByBedNumber[wrongIndex] = displaced;
        map.Verified[observedIndex] = true;
        map.Verified[wrongIndex] = false;

        Plugin.Logger.Warning(
            $"[BedTargeting] {patch.Key}: corrected — entity 0x{targetedEntityId:X8} was predicted as " +
            $"bed {wrongIndex + 1} but the menu reports it as bed {observedBedNumber}; swapped with bed " +
            $"{observedBedNumber}'s prior prediction 0x{displaced:X8}");
    }

    /// <summary>How many of the patch's beds have had their predicted <c>EntityId</c> proven correct
    /// by a live prompt read, for the Log tab.</summary>
    public static int VerifiedBedCount(Patch patch) =>
        maps.TryGetValue(patch.Key, out var map) ? map.Verified.Count(v => v) : 0;

    private static PatchMap GetOrBuild(Patch patch)
    {
        var liveSortedIds = patch.Beds.Select(b => b.EntityId).OrderBy(id => id).ToArray();

        if (maps.TryGetValue(patch.Key, out var map) && map.BuiltFromSortedIds.SequenceEqual(liveSortedIds))
            return map;

        // Bed n = Beds[Beds.Count - n]: the proven pairing at both endpoints of one Deluxe patch is
        // exactly the reverse of PatchDiscovery's ascending-EntityId order.
        var byBedNumber = new uint[patch.Beds.Count];
        for (var n = 1; n <= patch.Beds.Count; n++)
            byBedNumber[n - 1] = patch.Beds[patch.Beds.Count - n].EntityId;

        map = new PatchMap
        {
            ByBedNumber = byBedNumber,
            BuiltFromSortedIds = liveSortedIds,
            Verified = new bool[patch.Beds.Count],
        };
        maps[patch.Key] = map;
        return map;
    }
}
