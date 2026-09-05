using System;
using Gardener.Game;

namespace Gardener.Journal;

/// <summary>Evidential standing of a <see cref="HarvestWindow"/>: <c>Unknown</c> when there is
/// nothing to compute from, <c>Estimated</c> when the anchor itself (<c>PlantedAt</c>) is a guess,
/// <c>Bundled</c> when it rests on the single-sourced grow-hours table, and <c>Calibrated</c> once the
/// player's own garden has produced samples.</summary>
public enum HarvestConfidence
{
    Unknown,
    Estimated,
    Bundled,
    Calibrated,
}

/// <summary>A harvest-readiness window, never a countdown to the second: observed time always exceeds
/// true grow time, because growth advances on a server tick and readiness is only observable when the
/// player visits. <see cref="SampleCount"/> is the calibration sample count
/// behind <see cref="Confidence"/> == <see cref="HarvestConfidence.Calibrated"/>, 0 otherwise.
/// <see cref="AnchorUncertainty"/> is <see cref="BedRecord.PlantedAtUncertainty"/> passed through
/// unchanged: how much later than the anchor the actual planting could have happened, independent of
/// <see cref="Confidence"/>, which grades the grow-duration source and says nothing about the anchor
/// itself.</summary>
public readonly record struct HarvestWindow(
    DateTimeOffset? Earliest,
    DateTimeOffset? Estimate,
    HarvestConfidence Confidence,
    int SampleCount,
    TimeSpan? AnchorUncertainty);

/// <summary>
/// Pure functions over a <see cref="BedRecord"/> and the bundled <see cref="SeedTable"/>. Every field
/// this reports is null when the journal has nothing to compute it from — no invented default ever
/// substitutes for an unset <c>PlantedAt</c> or an unbundled seed row.
/// </summary>
public static class Growth
{
    // Fixed by the game since patch 4.0: the gap between wilting and withering is a day regardless
    // of the seed's own wilt time, and a mature (stage 4) planting never withers at all.
    private static readonly TimeSpan WitherAfterWilt = TimeSpan.FromHours(24);

    /// <summary>Null when <c>LastTendedAt</c> is unset, the seed does not wilt, or
    /// <see cref="BedRecord.ObservedWithered"/> is set — a withered plant has nothing left to tend.</summary>
    public static DateTimeOffset? WiltsAt(BedRecord record)
    {
        if (record.ObservedWithered)
            return null;
        if (record.LastTendedAt is not { } tendedAt)
            return null;
        if (SeedTable.Wilt(record.SeedRow) is not { } wilt)
            return null;
        return tendedAt + TimeSpan.FromHours(wilt.Hours);
    }

    /// <summary>Null once <see cref="BedRecord.FirstSeenStage4At"/> is set — a mature planting never
    /// withers since patch 4.0, and this therefore keys off the passive stage read and is only as
    /// good as that stage-4 reading is. Also null whenever <see cref="WiltsAt"/>
    /// is null.</summary>
    public static DateTimeOffset? WithersAt(BedRecord record)
    {
        if (record.FirstSeenStage4At is not null)
            return null;
        if (WiltsAt(record) is not { } wiltsAt)
            return null;
        return wiltsAt + WitherAfterWilt;
    }

    /// <summary>
    /// The bundled grow-hours table has no per-application timing model: the journal records only a
    /// running <see cref="BedRecord.FertilizerCount"/> and the single most recent
    /// <see cref="BedRecord.LastFertilizedAt"/>, never each application's own moment. Every
    /// application is therefore folded at that one recorded instant — each of the N applications
    /// removes 1% of whatever remains there, compounding to <c>0.99^N</c> of the time remaining at
    /// that point — rather than a flat <c>N%</c> off the total, which is what the game's own
    /// "1% of the remaining time" rule would understate. This is exact when the applications landed
    /// close together and an approximation otherwise; it is the most the recorded data supports.
    /// </summary>
    private static TimeSpan ApplyFertilizer(TimeSpan totalDuration, BedRecord record)
    {
        if (record.FertilizerCount <= 0)
            return totalDuration;
        if (record.PlantedAt is not { } plantedAt || record.LastFertilizedAt is not { } appliedAt)
            return totalDuration;

        var elapsedAtApplication = appliedAt - plantedAt;
        if (elapsedAtApplication < TimeSpan.Zero || elapsedAtApplication > totalDuration)
            return totalDuration;

        var remainingAtApplication = totalDuration - elapsedAtApplication;
        var factor = Math.Pow(0.99, record.FertilizerCount);
        return elapsedAtApplication + remainingAtApplication * factor;
    }

    /// <summary>
    /// Every field is null and <see cref="HarvestConfidence.Unknown"/> when <c>PlantedAt</c> is null
    /// or when neither calibration nor the bundled table has a duration for the seed row.
    /// <see cref="HarvestConfidence.Estimated"/> overrides calibration and the bundled table whenever
    /// <see cref="BedRecord.PlantedAtEstimated"/> is set, because a guessed anchor makes the resulting
    /// window only as good as that guess regardless of how good the duration estimate feeding it is.
    /// </summary>
    public static HarvestWindow HarvestWindow(BedRecord record)
    {
        if (record.PlantedAt is not { } plantedAt)
            return new HarvestWindow(null, null, HarvestConfidence.Unknown, 0, null);

        var calibrated = GardenJournal.Calibration.Estimate(record.SeedRow, CalibrationSeriesKind.HarvestOffered);
        var growHours = SeedTable.Grow(record.SeedRow);

        TimeSpan? earliestDuration;
        TimeSpan? estimateDuration;
        var sampleCount = 0;

        if (calibrated.Count > 0)
        {
            earliestDuration = calibrated.LowerBound;
            estimateDuration = calibrated.Median;
            sampleCount = calibrated.Count;
        }
        else if (growHours is { } hours)
        {
            earliestDuration = TimeSpan.FromHours(hours);
            estimateDuration = TimeSpan.FromHours(hours);
        }
        else
        {
            earliestDuration = null;
            estimateDuration = null;
        }

        if (earliestDuration is null)
            return new HarvestWindow(null, null, HarvestConfidence.Unknown, 0, null);

        var earliest = plantedAt + ApplyFertilizer(earliestDuration.Value, record);
        var estimate = estimateDuration is { } ed ? plantedAt + ApplyFertilizer(ed, record) : (DateTimeOffset?)null;

        var confidence = record.PlantedAtEstimated
            ? HarvestConfidence.Estimated
            : sampleCount > 0
                ? HarvestConfidence.Calibrated
                : HarvestConfidence.Bundled;

        return new HarvestWindow(earliest, estimate, confidence, sampleCount, record.PlantedAtUncertainty);
    }
}
