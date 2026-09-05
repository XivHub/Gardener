using System;
using System.Collections.Generic;

namespace Gardener.Game;

/// <summary>
/// How many of each seed the player is carrying, summed from <see cref="Bags.Scan"/> — the four main
/// inventory containers only. Saddlebag and retainer contents are excluded on purpose: a seed sitting
/// in either cannot be planted without first being moved to a main bag, so counting it here would tell
/// the Goal tab a step is covered when the player still has an errand to run first. Recomputed at most
/// once a second and served from cache otherwise, since the route search and the tab's own redraw both
/// read this every frame the Goal tab is open.
/// </summary>
public static class SeedInventory
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);

    private static DateTimeOffset lastComputedAt = DateTimeOffset.MinValue;
    private static IReadOnlyDictionary<uint, int> cached = new Dictionary<uint, int>();

    /// <summary>Held count by <c>GardeningSeed</c> row, zero for anything not held rather than an
    /// absent key — callers use <c>GetValueOrDefault</c> either way, but a real zero here is a fact,
    /// not a gap.</summary>
    public static IReadOnlyDictionary<uint, int> Counts()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastComputedAt < CacheDuration)
            return cached;

        var counts = new Dictionary<uint, int>();
        foreach (var slot in Bags.Scan())
        {
            if (SeedItems.SeedRowForItem(slot.ItemId) is not { } row)
                continue;
            counts[row] = counts.GetValueOrDefault(row) + (int)slot.Qty;
        }

        cached = counts;
        lastComputedAt = now;
        return cached;
    }
}
