using System;
using System.Collections.Generic;
using System.Linq;
using XivHubPluginKit.Inventory;

namespace Gardener.Game;

/// <summary>The three topsoil families. Thanalan drives intercross rate, Shroud raises yield
/// quantity, La Noscean has been equivalent to Potting Soil since 6.0.</summary>
public enum SoilFamily
{
    LaNoscean,
    Shroud,
    Thanalan,
}

/// <summary>One of the nine topsoil items, resolved and validated against the live <c>Item</c> sheet
/// at load — never guessed from a name or an <c>ItemUICategory</c>.</summary>
public sealed record SoilItem(uint ItemId, SoilFamily Family, int Grade);

/// <summary>A live <c>FilterGroup == 21</c> item id with no matching entry in the bundled soil table.</summary>
public sealed record SoilGap(uint ItemId, string Name);

/// <summary>
/// The soil and fertilizer resolver: <c>Item.FilterGroup</c> names both kinds exactly (21 soil, 22
/// fertilizer), the same discriminator <see cref="SeedItems"/> uses for <c>FilterGroup == 20</c>, so
/// neither table matches a name at runtime. Soil additionally needs a fixed id table, because family
/// and grade have no sheet field of their own; fertilizer needs none, so <see cref="Fertilizers"/> is
/// discovered directly from the live sheet.
/// </summary>
public static class GardeningItems
{
    // Grades 1/2/3 in that order per family; no sheet field carries family or grade, so this table
    // is the only source for either. Fish Meal (the one fertilizer observed so far) needs no such
    // table: FilterGroup alone is enough to gate on for a consumable with no grade of its own.
    private static readonly (uint ItemId, SoilFamily Family, int Grade)[] soilIds =
    {
        (7758u, SoilFamily.LaNoscean, 1), (7759u, SoilFamily.LaNoscean, 2), (7760u, SoilFamily.LaNoscean, 3),
        (7761u, SoilFamily.Shroud, 1), (7762u, SoilFamily.Shroud, 2), (7763u, SoilFamily.Shroud, 3),
        (7764u, SoilFamily.Thanalan, 1), (7765u, SoilFamily.Thanalan, 2), (7766u, SoilFamily.Thanalan, 3),
    };

    private static readonly List<SoilItem> soils = new();
    private static readonly List<uint> fertilizers = new();
    private static readonly List<SoilGap> liveSoilsMissingFromTable = new();

    public static IReadOnlyList<SoilItem> Soils => soils;
    public static IReadOnlyList<uint> Fertilizers => fertilizers;

    /// <summary>Live <c>FilterGroup == 21</c> items the fixed table above has no entry for — a soil a
    /// later patch added, surfaced in the config window's Data health section rather than silently
    /// ignored.</summary>
    public static IReadOnlyList<SoilGap> LiveSoilsMissingFromTable => liveSoilsMissingFromTable;

    static GardeningItems()
    {
        var knownSoilIds = soilIds.Select(s => s.ItemId).ToHashSet();

        foreach (var (itemId, family, grade) in soilIds)
        {
            var item = Sheets.ItemSheet.GetRowOrDefault(itemId);
            if (item is not { FilterGroup: 21 })
            {
                Plugin.Logger.Warning(
                    $"[GardeningItems] soil item {itemId} ({family} grade {grade}) does not resolve to a live " +
                    "FilterGroup==21 item; excluded from the soil table.");
                continue;
            }

            soils.Add(new SoilItem(itemId, family, grade));
        }

        foreach (var item in Sheets.ItemSheet)
        {
            if (item.FilterGroup == 22)
                fertilizers.Add(item.RowId);

            if (item.FilterGroup == 21 && !knownSoilIds.Contains(item.RowId))
                liveSoilsMissingFromTable.Add(new SoilGap(item.RowId, item.Name.ToString()));
        }

        foreach (var gap in liveSoilsMissingFromTable)
            Plugin.Logger.Warning($"[GardeningItems] live soil {gap.ItemId} ({gap.Name}) has no entry in the soil table");

        Plugin.Logger.Information(
            $"[GardeningItems] resolved {soils.Count}/9 soils and {fertilizers.Count} fertilizer item(s) " +
            $"({string.Join(", ", fertilizers)})");
    }

    /// <summary>
    /// The highest grade of <paramref name="preference"/>'s family the player actually holds, scanned
    /// from <paramref name="bag"/>. Null when nothing in that family is held — never a fallback to
    /// another family, because wrong soil silently halves the intercross rate. <see
    /// cref="SoilPreference.Fixed"/> resolves <see cref="Configuration.FixedSoilItemId"/> through the
    /// same "is it a real, live soil, and is it actually held" check rather than trusting the config
    /// value blindly.
    /// </summary>
    public static SoilItem? BestSoil(SoilPreference preference, IEnumerable<SlotView> bag)
    {
        var held = bag as ICollection<SlotView> ?? bag.ToList();

        if (preference == SoilPreference.Fixed)
        {
            var fixedId = Plugin.C.FixedSoilItemId;
            if (fixedId == 0)
                return null;

            var fixedSoil = soils.FirstOrDefault(s => s.ItemId == fixedId);
            if (fixedSoil is null)
                return null;

            return held.Any(slot => slot.ItemId == fixedSoil.ItemId) ? fixedSoil : null;
        }

        var family = FamilyFor(preference);
        return soils
            .Where(s => s.Family == family)
            .Where(s => held.Any(slot => slot.ItemId == s.ItemId))
            .OrderByDescending(s => s.Grade)
            .FirstOrDefault();
    }

    /// <summary>The sentence to show when <see cref="BestSoil"/> returned null for
    /// <paramref name="preference"/> — names the specific soil that is missing rather than saying
    /// "soil unavailable".</summary>
    public static string? SoilUnavailable(SoilPreference preference)
    {
        if (preference == SoilPreference.Fixed)
        {
            var fixedId = Plugin.C.FixedSoilItemId;
            if (fixedId == 0)
                return "No soil is pinned (FixedSoilItemId is 0).";

            var fixedSoil = soils.FirstOrDefault(s => s.ItemId == fixedId);
            return fixedSoil is null
                ? $"Pinned soil item {fixedId} is not a recognised topsoil."
                : $"No {ItemSheet.Name(fixedSoil.ItemId)} in the bag.";
        }

        var family = FamilyFor(preference);
        return $"No {family} Topsoil in the bag.";
    }

    private static SoilFamily FamilyFor(SoilPreference preference) => preference switch
    {
        SoilPreference.HighestThanalan => SoilFamily.Thanalan,
        SoilPreference.HighestShroud => SoilFamily.Shroud,
        SoilPreference.HighestLaNoscean => SoilFamily.LaNoscean,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "unhandled SoilPreference"),
    };
}
