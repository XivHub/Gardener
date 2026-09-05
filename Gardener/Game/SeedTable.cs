using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Gardener.Game;

/// <summary>The four source stamps embedded in <c>seeds.json</c> / <c>crossbreeds.json</c> by <c>tools/build_data.py</c>.</summary>
public sealed record SeedDataProvenance(
    string Generated,
    string LotlabCommit,
    string Nick75gCommit,
    string XivapiSchema,
    string XivapiVersion);

/// <summary>One bundled <c>GardeningSeed</c> row's timings, yields and cross flags.</summary>
public sealed record SeedEntry(
    uint Row,
    int? GrowHours,
    int? WiltHours,
    bool WiltDisputed,
    int[]? CropYield,
    int[]? SeedYield,
    bool Gatherable,
    bool CrossOnly);

/// <summary>An unordered cross of rows <see cref="A"/> x <see cref="B"/> and the row ids it can yield.</summary>
public sealed record CrossPair(uint A, uint B, uint[] Targets);

/// <summary>A row flagged by <see cref="SeedTable.Validate"/>, carrying both the row id and the seed item name.</summary>
public sealed record SeedGap(uint Row, string Name);

/// <summary>
/// Bundled-vs-live mismatches found at load. Every list is empty when the bundled table and the
/// live sheets agree completely; a non-empty list is a fact to show the user, never a reason to guess.
/// </summary>
public sealed record DataGaps(
    IReadOnlyList<SeedGap> BundledRowsMissingFromSheet,
    IReadOnlyList<SeedGap> LiveRowsMissingFromBundle,
    IReadOnlyList<SeedGap> RowsWithNoGrowTime,
    IReadOnlyList<SeedGap> RowsAbsentFromCrossData);

/// <summary>
/// The bundled seed and crossbreed tables, deserialised from the embedded <c>Data/seeds.json</c> and
/// <c>Data/crossbreeds.json</c> resources and cross-checked against the live <c>GardeningSeed</c> sheet.
/// Every accessor returns null (or an empty list) for a row the bundle has nothing to say about; nothing
/// here ever substitutes a guessed value for a missing one.
/// </summary>
public static class SeedTable
{
    private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Dictionary<uint, SeedEntry> bySeedRow = new();
    private static readonly List<CrossPair> pairs = new();
    private static readonly Dictionary<(uint, uint), CrossPair> pairByKey = new();
    private static readonly HashSet<(uint, uint)> deadPairs = new();

    public static SeedDataProvenance Provenance { get; }
    public static DataGaps DataGaps { get; }

    static SeedTable()
    {
        var seedsFile = LoadResource<SeedsFile>("Gardener.Data.seeds.json");
        var crossFile = LoadResource<CrossbreedsFile>("Gardener.Data.crossbreeds.json");

        Provenance = seedsFile.Provenance;

        foreach (var entry in seedsFile.Seeds)
            bySeedRow[entry.Row] = entry;

        foreach (var pair in crossFile.Pairs)
        {
            pairs.Add(pair);
            pairByKey[NormalizeKey(pair.A, pair.B)] = pair;
        }

        foreach (var dead in crossFile.Dead)
            deadPairs.Add(NormalizeKey(dead[0], dead[1]));

        DataGaps = Validate();
    }

    /// <summary>Grow hours for a bundled row; null if the row is not bundled or the source has no value.</summary>
    public static int? Grow(uint row) => bySeedRow.TryGetValue(row, out var e) ? e.GrowHours : null;

    /// <summary>Wilt hours and whether the two wilt sources disagreed (the minimum ships); null if unknown.</summary>
    public static (int Hours, bool Disputed)? Wilt(uint row)
    {
        if (!bySeedRow.TryGetValue(row, out var e) || e.WiltHours is not { } hours)
            return null;
        return (hours, e.WiltDisputed);
    }

    /// <summary>Crop and seed yield per soil tier; null if the row is not bundled.</summary>
    public static (int[]? Crop, int[]? Seed)? Yields(uint row) =>
        bySeedRow.TryGetValue(row, out var e) ? (e.CropYield, e.SeedYield) : null;

    /// <summary>Whether the row is gatherable (as opposed to cross-only); null if the row is not bundled.</summary>
    public static bool? Gatherable(uint row) => bySeedRow.TryGetValue(row, out var e) ? e.Gatherable : null;

    /// <summary>Every parent pair known to be able to yield <paramref name="target"/>.</summary>
    public static IReadOnlyList<(uint A, uint B)> Pairs(uint target) =>
        pairs.Where(p => p.Targets.Contains(target)).Select(p => (p.A, p.B)).ToList();

    /// <summary>The offspring row ids for an unordered parent pair; empty if the pair is not a known cross.</summary>
    public static IReadOnlyList<uint> TargetsFor(uint a, uint b) =>
        pairByKey.TryGetValue(NormalizeKey(a, b), out var pair) ? pair.Targets : Array.Empty<uint>();

    /// <summary>Whether the unordered parent pair is a known dead cross.</summary>
    public static bool IsDead(uint a, uint b) => deadPairs.Contains(NormalizeKey(a, b));

    private static (uint, uint) NormalizeKey(uint a, uint b) => a <= b ? (a, b) : (b, a);

    private static T LoadResource<T>(string resourceName)
    {
        var assembly = typeof(SeedTable).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing; run tools/build_data.py.");
        return JsonSerializer.Deserialize<T>(stream, jsonOptions)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' deserialised to null.");
    }

    /// <summary>
    /// Cross-checks the bundled tables against the live <c>GardeningSeed</c> sheet in both directions,
    /// logging every mismatch by row id and by seed item name so a stale bundle degrades visibly instead
    /// of silently planting the wrong seed or omitting a real one from the planner.
    /// </summary>
    private static DataGaps Validate()
    {
        var bundledMissing = new List<SeedGap>();
        var liveMissing = new List<SeedGap>();
        var noGrowTime = new List<SeedGap>();
        var noCrossData = new List<SeedGap>();

        var referenced = new HashSet<uint>();
        foreach (var pair in pairs)
        {
            referenced.Add(pair.A);
            referenced.Add(pair.B);
            foreach (var target in pair.Targets)
                referenced.Add(target);
        }
        foreach (var (a, b) in deadPairs)
        {
            referenced.Add(a);
            referenced.Add(b);
        }

        foreach (var (row, entry) in bySeedRow)
        {
            if (!SeedItems.IsOutdoorSeed(row))
                bundledMissing.Add(new SeedGap(row, SeedName(row)));

            if (entry.GrowHours is null)
                noGrowTime.Add(new SeedGap(row, SeedName(row)));

            if (!referenced.Contains(row))
                noCrossData.Add(new SeedGap(row, SeedName(row)));
        }

        foreach (var seed in Sheets.GardeningSeedSheet)
        {
            if (seed.RowId == 0 || seed.IsPlantPotFlowerSeed)
                continue;
            if (!bySeedRow.ContainsKey(seed.RowId))
                liveMissing.Add(new SeedGap(seed.RowId, SeedName(seed.RowId)));
        }

        foreach (var gap in bundledMissing)
            Plugin.Logger.Warning($"[SeedTable] bundled row {gap.Row} ({gap.Name}) has no live outdoor GardeningSeed row");
        foreach (var gap in liveMissing)
            Plugin.Logger.Warning($"[SeedTable] live outdoor row {gap.Row} ({gap.Name}) is missing from the bundled table");
        foreach (var gap in noGrowTime)
            Plugin.Logger.Warning($"[SeedTable] row {gap.Row} ({gap.Name}) has no grow time");
        foreach (var gap in noCrossData)
            Plugin.Logger.Warning($"[SeedTable] row {gap.Row} ({gap.Name}) is absent from the cross data");

        return new DataGaps(bundledMissing, liveMissing, noGrowTime, noCrossData);
    }

    private static string SeedName(uint row)
    {
        var itemId = SeedItems.SeedItemForRow(row);
        return itemId is { } id && id != 0 ? XivHubPluginKit.Inventory.ItemSheet.Name(id) : "unknown";
    }

    private sealed record SeedsFile(SeedDataProvenance Provenance, SeedEntry[] Seeds);

    private sealed record CrossbreedsFile(SeedDataProvenance Provenance, CrossPair[] Pairs, uint[][] Dead);
}
