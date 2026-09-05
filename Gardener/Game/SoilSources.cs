using System;
using System.Collections.Generic;

namespace Gardener.Game;

/// <summary>
/// Where to mine each of the nine topsoils, transcribed from <c>data/sources/topsoil-gathering.csv</c>,
/// and what each family does for a garden. Keyed by <see cref="SoilFamily"/> and grade, never by item
/// id: <see cref="GardeningItems"/> alone carries the soil item ids, and this table exists so that
/// stays true.
/// </summary>
public static class SoilSources
{
    private readonly record struct Entry(string Location, string Node);

    // Location and node note per (family, grade), straight off the CSV's "Mining Locations" and
    // "Additional Info" columns. The unspoiled-node slot times come from the same rows.
    private static readonly Dictionary<(SoilFamily Family, int Grade), Entry> table = new()
    {
        [(SoilFamily.LaNoscean, 1)] = new Entry("Lower La Noscea (26, 15)", "a hidden, rare item node"),
        [(SoilFamily.LaNoscean, 2)] = new Entry("Lower La Noscea (21, 35)", "a hidden, rare item node"),
        [(SoilFamily.LaNoscean, 3)] = new Entry("Middle La Noscea (24, 27)", "the unspoiled node at 7pm"),

        [(SoilFamily.Shroud, 1)] = new Entry("East Shroud (20, 27)", "a hidden, rare item node"),
        [(SoilFamily.Shroud, 2)] = new Entry("East Shroud (18, 25)", "a hidden, rare item node"),
        [(SoilFamily.Shroud, 3)] = new Entry("South Shroud (15, 29)", "the unspoiled node at 7am"),

        [(SoilFamily.Thanalan, 1)] = new Entry("Eastern Thanalan (24, 19)", "a hidden, rare item node"),
        [(SoilFamily.Thanalan, 2)] = new Entry("Western Thanalan (17, 28)", "a hidden, rare item node"),
        [(SoilFamily.Thanalan, 3)] = new Entry("Western Thanalan (17, 28)", "the unspoiled node at 6am"),
    };

    /// <summary>Where to mine this soil, e.g. "Western Thanalan (17, 28), at the unspoiled node at
    /// 6am"; a generic fallback for a (family, grade) the table has no entry for.</summary>
    public static string Where(SoilFamily family, int grade) =>
        table.TryGetValue((family, grade), out var e)
            ? $"{e.Location}, at {e.Node}"
            : $"Grade {grade} {family} Topsoil: mining location not recorded.";

    /// <summary>What this soil family is for, in the player's terms.</summary>
    public static string Does(SoilFamily family) => family switch
    {
        SoilFamily.Thanalan => "makes crossing likely",
        SoilFamily.Shroud => "makes a harvest bigger",
        SoilFamily.LaNoscean => "improves quality",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unhandled SoilFamily"),
    };
}
