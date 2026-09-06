using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Journal;
using Gardener.Localization;

namespace Gardener.Windows;

/// <summary>
/// Turns a live <see cref="Patch"/> or a persisted patch key into player-facing text. Never returns a
/// raw <see cref="Patch.Key"/> or house key: <see cref="Helpers.DebugDump"/> is the one place those
/// identifiers stay printed, and every other call site goes through here instead.
/// </summary>
public static class PatchLabel
{
    public static string Estate(EstateType? estateType) => estateType switch
    {
        EstateType.FreeCompanyEstate => Strings.Patch_EstateFreeCompany,
        EstateType.PersonalEstate => Strings.Patch_EstatePrivate,
        EstateType.SharedEstate => Strings.Patch_EstateShared,
        _ => Strings.Patch_EstateUnknown,
    };

    /// <summary>The Garden tab's header line for a live patch. Appends the per-house ordinal only
    /// once <see cref="GardenJournal.PatchesForHouse"/> shows more than one recorded patch at this
    /// house — a single-patch house never shows a meaningless "patch 1".</summary>
    public static string Header(Patch patch, int? plotIndex)
    {
        var houseKey = patch.Key.Split(':')[0];
        var estateText = Estate(GardenJournal.EstateTypeFor(houseKey));
        var numbered = GardenJournal.PatchesForHouse(houseKey).Count > 1;
        var ordinal = GardenJournal.PatchInfo(patch.Key)?.Ordinal ?? 1;
        return Compose(estateText, plotIndex, numbered, ordinal, patch.Kind, patch.Kind.BedCount());
    }

    /// <summary>
    /// Reminders' entry point: the same sentence <see cref="Header"/> builds, from a persisted patch
    /// key alone and no live <see cref="Patch"/> to read. Degrades to the estate name alone when the
    /// patch itself was never recorded (its <see cref="PatchRecord"/> — kind, bed count, ordinal — is
    /// unknown), and to <see cref="Strings.Patch_NotRecorded"/> when even the estate is unknown.
    /// </summary>
    public static string FromKey(string patchKey)
    {
        if (GardenJournal.PatchInfo(patchKey) is { } record)
        {
            var estateText = Estate(GardenJournal.EstateTypeFor(record.HouseKey));
            var numbered = GardenJournal.PatchesForHouse(record.HouseKey).Count > 1;
            return Compose(estateText, record.PlotIndex, numbered, record.Ordinal, record.Kind, record.BedCount);
        }

        var houseKey = patchKey.Split(':')[0];
        return GardenJournal.EstateTypeFor(houseKey) is { } estateType
            ? Estate(estateType)
            : Strings.Patch_NotRecorded;
    }

    /// <summary>Reminders' house grouping line. Carries the plot number when any patch of this house
    /// recorded one, because two Free Company estates would otherwise render the same header and the
    /// player could not tell which garden a block of reminders belongs to.</summary>
    public static string HouseHeader(string houseKey)
    {
        var estateText = Estate(GardenJournal.EstateTypeFor(houseKey));
        var plotIndex = GardenJournal.PatchesForHouse(houseKey)
            .Select(p => p.PlotIndex)
            .FirstOrDefault(p => p is not null);

        return plotIndex is { } plot
            ? Loc.Format(Strings.Patch_HouseHeaderWithPlot, estateText, Formats.Number(plot + 1))
            : estateText;
    }

    private static string Compose(string estateText, int? plotIndex, bool numbered, int ordinal, PatchKind kind, int bedCount)
    {
        var bedsText = Formats.Number(bedCount);

        if (plotIndex is { } plot)
        {
            var plotText = Formats.Number(plot + 1);
            return numbered
                ? Loc.Format(Strings.Patch_HeaderWithPlotNumbered, estateText, plotText, Formats.Number(ordinal), kind, bedsText)
                : Loc.Format(Strings.Patch_HeaderWithPlot, estateText, plotText, kind, bedsText);
        }

        return numbered
            ? Loc.Format(Strings.Patch_HeaderNoPlotNumbered, estateText, Formats.Number(ordinal), kind, bedsText)
            : Loc.Format(Strings.Patch_HeaderNoPlot, estateText, kind, bedsText);
    }
}
