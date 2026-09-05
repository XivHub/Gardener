using System;
using System.Collections.Generic;
using System.Linq;

namespace Gardener.Journal;

/// <summary>Which of a seed row's two observed-interval series a sample belongs to.</summary>
public enum CalibrationSeriesKind
{
    /// <summary><c>FirstSeenStage4At - PlantedAt</c>.</summary>
    Stage4,

    /// <summary><c>FirstSeenHarvestOfferedAt - PlantedAt</c>: the moment the bed menu is seen
    /// offering a <c>Harvest</c> entry, the one act-time fact memory does not carry.</summary>
    HarvestOffered,
}

/// <summary>The estimate <see cref="Calibration.Estimate"/> derives from one seed row's samples.
/// <see cref="LowerBound"/> is the tightest honest bound observation alone can give: the minimum
/// interval actually seen, never an upper bound the plugin has no way to know.</summary>
public readonly record struct CalibrationEstimate(TimeSpan? LowerBound, TimeSpan? Median, int Count);

/// <summary>One <c>GardeningSeed</c> row's two sample ring buffers, in hours (a double survives the
/// JSON round trip losslessly at these magnitudes).</summary>
public sealed class SeedCalibration
{
    public List<double> Stage4Hours { get; set; } = new();
    public List<double> HarvestOfferedHours { get; set; } = new();
}

/// <summary>
/// Two per-seed-row sample series — planted-at to first-seen-stage-4, and planted-at to
/// first-seen-harvest-offered — each capped at 20 entries. Samples are recorded only for beds Gardener
/// itself planted with a real (not estimated) planted-at, so a guessed anchor never contaminates the
/// calibration. The two series together are what settles whether stage 4 means harvestable: if they
/// converge, it does; if <see cref="CalibrationSeriesKind.Stage4"/> lands consistently earlier than
/// <see cref="CalibrationSeriesKind.HarvestOffered"/>, it does not.
/// </summary>
public sealed class Calibration
{
    private const int MaxSamples = 20;

    public Dictionary<uint, SeedCalibration> BySeedRow { get; set; } = new();

    public void Record(uint seedRow, CalibrationSeriesKind kind, TimeSpan interval)
    {
        if (!BySeedRow.TryGetValue(seedRow, out var series))
        {
            series = new SeedCalibration();
            BySeedRow[seedRow] = series;
        }

        var samples = kind == CalibrationSeriesKind.Stage4 ? series.Stage4Hours : series.HarvestOfferedHours;
        samples.Add(interval.TotalHours);
        if (samples.Count > MaxSamples)
            samples.RemoveAt(0);
    }

    public CalibrationEstimate Estimate(uint seedRow, CalibrationSeriesKind kind)
    {
        if (!BySeedRow.TryGetValue(seedRow, out var series))
            return new CalibrationEstimate(null, null, 0);

        var samples = kind == CalibrationSeriesKind.Stage4 ? series.Stage4Hours : series.HarvestOfferedHours;
        if (samples.Count == 0)
            return new CalibrationEstimate(null, null, 0);

        var sorted = samples.OrderBy(h => h).ToList();
        var lowerBound = TimeSpan.FromHours(sorted[0]);
        var median = TimeSpan.FromHours(sorted[sorted.Count / 2]);
        return new CalibrationEstimate(lowerBound, median, sorted.Count);
    }
}
