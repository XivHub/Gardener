using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Gardener.Game;

/// <summary>Lumina sheet handles used by the seed table and discovery code.</summary>
public static class Sheets
{
    public static readonly ExcelSheet<GardeningSeed> GardeningSeedSheet;
    public static readonly ExcelSheet<Item> ItemSheet;
    public static readonly ExcelSheet<ItemUICategory> ItemUICategorySheet;

    static Sheets()
    {
        GardeningSeedSheet = Plugin.DataManager.GetExcelSheet<GardeningSeed>()!;
        ItemSheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        ItemUICategorySheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>()!;
    }
}
