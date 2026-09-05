using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivHubPluginKit.Inventory;

namespace Gardener.Game;

/// <summary>The player's four main inventory containers as one list.</summary>
public static class Bags
{
    /// <summary>
    /// Every occupied slot across the four main bags. The planting agent takes a container and a
    /// slot rather than an item id, so callers scan immediately before they act: a slot recorded
    /// even seconds earlier can hold something else by the time the game reads it.
    /// </summary>
    public static List<SlotView> Scan() =>
        InventoryScan.ScanContainer(InventoryType.Inventory1)
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory2))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory3))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory4))
            .ToList();
}
