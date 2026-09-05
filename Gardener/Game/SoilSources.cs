using System;
using System.Collections.Generic;
using Gardener.Localization;

namespace Gardener.Game;

/// <summary>The two kinds of node topsoil is mined from, matching the CSV's "Additional Info" column:
/// a hidden, rare node with no fixed schedule, or an unspoiled node that opens at one hour of the
/// Eorzean day.</summary>
public enum NodeKind
{
    HiddenRare,
    Unspoiled,
}

/// <summary>
/// Where to mine each of the nine topsoils, transcribed from <c>data/sources/topsoil-gathering.csv</c>,
/// and what each family does for a garden. Keyed by <see cref="SoilFamily"/> and grade, never by item
/// id: <see cref="GardeningItems"/> alone carries the soil item ids, and this table exists so that
/// stays true.
/// </summary>
public static class SoilSources
{
    // Location and node kind per (family, grade), straight off the CSV's "Mining Locations" and
    // "Additional Info" columns. Hour is the Eorzean clock hour an Unspoiled node opens; null for
    // HiddenRare, which has no fixed schedule.
    private readonly record struct Entry(string Location, NodeKind Node, int? Hour);

    private static readonly Dictionary<(SoilFamily Family, int Grade), Entry> table = new()
    {
        [(SoilFamily.LaNoscean, 1)] = new Entry("Lower La Noscea (26, 15)", NodeKind.HiddenRare, null),
        [(SoilFamily.LaNoscean, 2)] = new Entry("Lower La Noscea (21, 35)", NodeKind.HiddenRare, null),
        [(SoilFamily.LaNoscean, 3)] = new Entry("Middle La Noscea (24, 27)", NodeKind.Unspoiled, 19),

        [(SoilFamily.Shroud, 1)] = new Entry("East Shroud (20, 27)", NodeKind.HiddenRare, null),
        [(SoilFamily.Shroud, 2)] = new Entry("East Shroud (18, 25)", NodeKind.HiddenRare, null),
        [(SoilFamily.Shroud, 3)] = new Entry("South Shroud (15, 29)", NodeKind.Unspoiled, 7),

        [(SoilFamily.Thanalan, 1)] = new Entry("Eastern Thanalan (24, 19)", NodeKind.HiddenRare, null),
        [(SoilFamily.Thanalan, 2)] = new Entry("Western Thanalan (17, 28)", NodeKind.HiddenRare, null),
        [(SoilFamily.Thanalan, 3)] = new Entry("Western Thanalan (17, 28)", NodeKind.Unspoiled, 6),
    };

    /// <summary>Where to mine this soil, e.g. "Western Thanalan (17, 28), at the unspoiled node at
    /// 6am"; a fallback naming the real item for a (family, grade) the table has no entry for. The
    /// mined location stays untranslated bundled English zone text (Decision 3, out of scope for this
    /// pass); only the sentence wrapped around it is localized.</summary>
    public static string Where(SoilFamily family, int grade)
    {
        if (!table.TryGetValue((family, grade), out var e))
            return Loc.Format(Strings.Soil_WhereUnrecorded, GardeningItems.SoilName(family, grade));

        return e.Node switch
        {
            NodeKind.HiddenRare => Loc.Format(Strings.Soil_WhereHiddenRare, e.Location),
            NodeKind.Unspoiled => Loc.Format(Strings.Soil_WhereUnspoiled, e.Location, ClockHour(e.Hour ?? 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(e.Node), e.Node, "unhandled NodeKind"),
        };
    }

    /// <summary>What this soil family is for, in the player's terms, as a whole sentence naming the
    /// real resolved item so the sentence's subject is never a bare enum value.</summary>
    public static string Does(SoilFamily family, string soilName) => family switch
    {
        SoilFamily.Thanalan => Loc.Format(Strings.Soil_DoesThanalan, soilName),
        SoilFamily.Shroud => Loc.Format(Strings.Soil_DoesShroud, soilName),
        SoilFamily.LaNoscean => Loc.Format(Strings.Soil_DoesLaNoscean, soilName),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "unhandled SoilFamily"),
    };

    /// <summary>An Eorzean clock hour as a compact "7am"/"7pm" label. A game-clock fact, not a
    /// real-world time, so it always renders this way rather than through <see cref="Formats"/> and
    /// <see cref="Loc.Culture"/>.</summary>
    private static string ClockHour(int hour) => hour switch
    {
        0 => "12am",
        < 12 => $"{hour}am",
        12 => "12pm",
        _ => $"{hour - 12}pm",
    };
}
