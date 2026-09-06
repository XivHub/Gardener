using System;
using System.Collections.Generic;
using Gardener.Game;
using XivHubPluginKit.Inventory;

namespace Gardener.Planner;

/// <summary>
/// The seed and soil bag one <see cref="CrossPlanner.PlanFillStepAcross"/> call threads through every
/// patch it fills. A sealed class over a mutable dictionary, never a struct: the whole point is that
/// one instance is shared by every per-patch <see cref="CrossPlanner.PlanFillStep"/> call, so the
/// second patch sees what the first already spent. A value type with copied counts would compile
/// clean and pass every gate, but each patch would then plan against the same untouched bag — a
/// fill that does not decrement it double-spends the same seeds and soil, and the sweep aborts
/// mid-run the same way an unnoticed soil shortfall already did once.
/// </summary>
public sealed class PlantingStock
{
    private readonly IReadOnlyList<SlotView> bag;
    private readonly Dictionary<uint, int> counts;

    private PlantingStock(IReadOnlyList<SlotView> bag, Dictionary<uint, int> counts)
    {
        this.bag = bag;
        this.counts = counts;
    }

    /// <summary>Sums every slot's <see cref="SlotView.Qty"/> by item id.</summary>
    public static PlantingStock FromBag(IReadOnlyList<SlotView> bag)
    {
        var counts = new Dictionary<uint, int>();
        foreach (var slot in bag)
            counts[slot.ItemId] = counts.GetValueOrDefault(slot.ItemId) + (int)slot.Qty;
        return new PlantingStock(bag, counts);
    }

    /// <summary>The original scan this stock was built from, for callers that only need to know an
    /// item exists at all — <see cref="GardeningItems.BestSoil"/>'s family/grade ranking reads
    /// presence, never quantity. Never re-derived from <see cref="counts"/>, since which soils exist
    /// in the bag does not change mid-fill, only how much of each is left.</summary>
    public IReadOnlyList<SlotView> Bag => bag;

    public int Count(uint itemId) => counts.GetValueOrDefault(itemId);

    /// <summary>How many of a <c>GardeningSeed</c> row's own seed item remain; 0 for a row with no
    /// seed item at all.</summary>
    public int SeedCount(uint seedRow) =>
        SeedItems.SeedItemForRow(seedRow) is { } itemId ? Count(itemId) : 0;

    /// <summary>Spends <paramref name="quantity"/> of <paramref name="itemId"/> against this bag,
    /// clamped at 0 rather than going negative.</summary>
    public void Take(uint itemId, int quantity)
    {
        if (quantity <= 0)
            return;
        counts[itemId] = Math.Max(0, Count(itemId) - quantity);
    }
}
