using System.Collections.Generic;

namespace Gardener.Game;

/// <summary>
/// The seed item &lt;-&gt; <c>GardeningSeed</c> row map. Built once from every <c>Item</c> row with
/// <c>FilterGroup == 20</c>, whose <c>AdditionalData</c> names its <c>GardeningSeed</c> row directly by
/// id — no name matching, at build time or at runtime.
/// </summary>
public static class SeedItems
{
    private static readonly Dictionary<uint, uint> seedItemToRow = new();
    private static readonly Dictionary<uint, uint> rowToSeedItem = new();

    static SeedItems()
    {
        foreach (var item in Sheets.ItemSheet)
        {
            if (item.FilterGroup != 20)
                continue;

            var row = item.AdditionalData.RowId;
            if (row == 0)
                continue;

            seedItemToRow[item.RowId] = row;
            rowToSeedItem[row] = item.RowId;
        }
    }

    /// <summary>The <c>GardeningSeed</c> row for a seed item id; null if the item is not a gardening seed.</summary>
    public static uint? SeedRowForItem(uint itemId) =>
        seedItemToRow.TryGetValue(itemId, out var row) ? row : null;

    /// <summary>The seed item id for a <c>GardeningSeed</c> row; null if the row has no seed item.</summary>
    public static uint? SeedItemForRow(uint row) =>
        rowToSeedItem.TryGetValue(row, out var itemId) ? itemId : null;

    /// <summary>Whether a <c>GardeningSeed</c> row is outdoor-plantable, i.e. not a flowerpot flower.</summary>
    public static bool IsOutdoorSeed(uint row)
    {
        var seed = Sheets.GardeningSeedSheet.GetRowOrDefault(row);
        return row > 0 && seed.HasValue && !seed.Value.IsPlantPotFlowerSeed;
    }

    /// <summary>The produce item for a <c>GardeningSeed</c> row (<c>GardeningSeed.Item</c>); null if the row does not exist.</summary>
    public static uint? ProduceItemForRow(uint row)
    {
        var seed = Sheets.GardeningSeedSheet.GetRowOrDefault(row);
        return seed?.Item.RowId;
    }
}
