using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Journal;
using XivHubPluginKit.Inventory;

namespace Gardener.Planner;

/// <summary>
/// Builds a one-cross <see cref="LayoutPlan"/> for a target seed on one patch. Occupancy and seed
/// identity always come from a live <c>GardenMemory.Read(patch)</c> snapshot passed in by the caller —
/// never re-read here — and the journal is consulted only to exclude a withered occupying bed as a
/// parent, since a withered plant cannot be crossbred with. Read-only throughout: nothing here plants
/// anything, it only proposes what <c>SchedulerMain</c>'s Plan sweep would plant.
/// </summary>
public static class CrossPlanner
{
    /// <summary>
    /// One target seed's plan for one patch: at most a parent step followed by the crossing step that
    /// reaches it, chosen from every pair <see cref="SeedTable.Pairs"/> knows for <paramref name="target"/>
    /// and filtered to the ones this patch and this bag can actually start — a pair whose parent is
    /// neither held nor already growing here is not plantable, and a pair whose parents both already
    /// occupy a bed has nothing left to plant to trigger a cross. Among the plantable pairs, one already
    /// occupying a bed is preferred over one needing two fresh plants, and ties break on
    /// <see cref="SeedTable.EfficiencyFor"/> for <paramref name="target"/> where it is known. The chosen
    /// pair's crossing bed is only ever one whose every other neighbour is empty, so the cross is
    /// isolated regardless of which neighbour direction turns out to be physically "right". The plan is
    /// re-checked against itself with <see cref="Verify"/> before it is returned.
    /// </summary>
    public static LayoutPlan PlanSingleStep(
        ushort target, Patch patch, IReadOnlyList<BedState> memory, IReadOnlyList<SlotView> inventory)
    {
        var plan = new LayoutPlan();
        var targetName = SeedItems.ProduceName(target);

        var candidatePairs = SeedTable.Pairs(target);
        if (candidatePairs.Count == 0)
        {
            plan.Warnings.Add($"No known cross produces {targetName}.");
            return plan;
        }

        if (patch.Kind != PatchKind.Deluxe)
        {
            plan.Warnings.Add(
                $"{patch.Kind} bed adjacency has never been confirmed, so a cross can't be placed here yet.");
            return plan;
        }

        var occupiedByBed = memory.Where(s => !s.IsEmpty).ToDictionary(s => s.BedNumber, s => s.SeedRow);
        var emptyBeds = Enumerable.Range(1, patch.Kind.BedCount())
            .Where(b => !occupiedByBed.ContainsKey(b))
            .ToHashSet();

        int? OccupyingBed(uint row)
        {
            foreach (var (bed, seedRow) in occupiedByBed.OrderBy(kv => kv.Key))
            {
                if (seedRow != row)
                    continue;
                if (GardenJournal.Get(patch.Key, bed)?.ObservedWithered == true)
                    continue; // withered plants cannot be crossbred with
                return bed;
            }
            return null;
        }

        bool IsHeld(uint row) =>
            SeedItems.SeedItemForRow(row) is { } itemId && inventory.Any(s => s.ItemId == itemId);

        int? FindIsolatedCrossBed(int parentBed) =>
            Adjacency.Neighbours(patch.Kind, parentBed)
                .Where(candidate => emptyBeds.Contains(candidate))
                .Where(candidate => Adjacency.Neighbours(patch.Kind, candidate)
                    .Where(other => other != parentBed)
                    .All(other => emptyBeds.Contains(other)))
                .Select(candidate => (int?)candidate)
                .FirstOrDefault();

        (int Parent, int Cross)? FindEmptyPairForPlanting()
        {
            foreach (var parentBed in emptyBeds.OrderBy(b => b))
                if (FindIsolatedCrossBed(parentBed) is { } cross)
                    return (parentBed, cross);
            return null;
        }

        var scored = candidatePairs
            .Select(pair => (Pair: pair, Assignment: Assign(pair.A, pair.B, OccupyingBed, IsHeld)))
            .Where(x => x.Assignment is not null)
            .Select(x => (x.Pair, Assignment: x.Assignment!.Value,
                Efficiency: SeedTable.EfficiencyFor(x.Pair.A, x.Pair.B, target)))
            .OrderByDescending(x => x.Assignment.ExistingParentBed is not null)
            .ThenByDescending(x => x.Efficiency ?? -1)
            .ToList();

        if (scored.Count == 0)
        {
            plan.Warnings.Add(
                $"{targetName} needs a pair currently held or already growing on this patch; none is available.");
            return plan;
        }

        var chosen = scored[0];
        var allTargets = SeedTable.TargetsFor(chosen.Pair.A, chosen.Pair.B);

        int parentBed;
        int crossBed;
        var needsParentStep = chosen.Assignment.ExistingParentBed is null;

        if (!needsParentStep)
        {
            var existingBed = chosen.Assignment.ExistingParentBed!.Value;
            if (FindIsolatedCrossBed(existingBed) is not { } cross)
            {
                plan.Warnings.Add(
                    $"No bed next to bed {existingBed} is free enough to isolate the cross " +
                    "(its other neighbour must be empty too).");
                return plan;
            }
            parentBed = existingBed;
            crossBed = cross;
        }
        else
        {
            if (FindEmptyPairForPlanting() is not { } found)
            {
                plan.Warnings.Add("No two adjacent empty beds are free to plant both parents into.");
                return plan;
            }
            (parentBed, crossBed) = found;
        }

        var parentRow = chosen.Assignment.ParentRow;
        var crossRow = chosen.Assignment.CrossRow;
        var parentName = SeedItems.ProduceName(parentRow);

        if (needsParentStep)
        {
            var parentSoil = GardeningItems.BestSoil(Plugin.C.SoilForYield, inventory);
            if (parentSoil is null)
            {
                plan.Warnings.Add(
                    GardeningItems.SoilUnavailable(Plugin.C.SoilForYield) ?? "No soil available for the parent step.");
                return plan;
            }
            plan.Steps.Add(new PlantStep(parentBed, (ushort)parentRow, parentSoil.ItemId,
                $"Parent for {targetName}: plant here first so bed {crossBed} can cross with it."));
        }

        var crossSoil = GardeningItems.BestSoil(Plugin.C.SoilForCross, inventory);
        if (crossSoil is null)
        {
            plan.Steps.Clear(); // the parent step alone plants nothing worth doing without the cross after it
            plan.Warnings.Add(
                GardeningItems.SoilUnavailable(Plugin.C.SoilForCross) ?? "No soil available for the crossing step.");
            return plan;
        }

        var effText = SeedTable.EfficiencyFor(chosen.Pair.A, chosen.Pair.B, target) is { } pct
            ? $"measured {CrossOdds.OddsPhrase(pct / 100.0)}"
            : "efficiency not yet measured for this pair";
        plan.Steps.Add(new PlantStep(crossBed, (ushort)crossRow, crossSoil.ItemId,
            $"Crosses with bed {parentBed}'s {parentName} to reach {targetName} ({effText})."));
        plan.ExpectedTargets[crossBed] = allTargets;

        if (allTargets.Count > 1)
        {
            var others = allTargets.Where(t => t != target).Select(SeedItems.ProduceName);
            plan.Warnings.Add(
                $"This cross can also yield {string.Join(", ", others)} instead of {targetName}; " +
                "the result isn't guaranteed.");
        }

        if (SeedTable.Yields(target)?.Seed is { } targetSeedYield &&
            crossSoil.Grade < targetSeedYield.Length && targetSeedYield[crossSoil.Grade] < 1)
        {
            plan.Warnings.Add(
                $"{targetName} returns no seeds of its own at Grade {crossSoil.Grade} soil; " +
                "harvesting it won't restock this cross.");
        }

        Verify(plan, patch);
        return plan;
    }

    private readonly record struct PairAssignment(uint ParentRow, int? ExistingParentBed, uint CrossRow);

    /// <summary>Which of an unordered pair is the parent already on the ground and which must be freshly
    /// planted to trigger the cross. Null when the pair cannot be started at all right now: both parents
    /// already occupy a bed (nothing left to plant), or the parent that would need planting is not held.</summary>
    private static PairAssignment? Assign(uint a, uint b, Func<uint, int?> occupyingBed, Func<uint, bool> held)
    {
        var aBed = occupyingBed(a);
        var bBed = occupyingBed(b);

        if (aBed is not null && bBed is not null)
            return null;
        if (aBed is not null)
            return held(b) ? new PairAssignment(a, aBed, b) : null;
        if (bBed is not null)
            return held(a) ? new PairAssignment(b, bBed, a) : null;
        return held(a) && held(b) ? new PairAssignment(a, null, b) : null;
    }

    /// <summary>
    /// Re-checks a plan against a live <c>GardenMemory.Read(patch)</c> snapshot, applying each step in
    /// order (a crossing step may depend on the parent step immediately before it) and downgrading any
    /// step to a <see cref="LayoutPlan.Warnings"/> entry rather than trusting it: the bed it targets is
    /// no longer empty, or — for a crossing step — <see cref="Adjacency.FirstValid"/> no longer resolves
    /// to the neighbour the plan expects to be the one and only occupied neighbour holding one of
    /// <see cref="LayoutPlan.ExpectedTargets"/>'s parents. Safe to call twice: once right after
    /// <see cref="PlanSingleStep"/> builds the plan, and again immediately before a Plan sweep runs it,
    /// since either call can be the one that catches drift since the plan was drawn on screen.
    /// </summary>
    public static bool Verify(LayoutPlan plan, Patch patch)
    {
        var occupied = new Dictionary<int, ushort>();
        foreach (var state in GardenMemory.Read(patch))
            if (!state.IsEmpty)
                occupied[state.BedNumber] = state.SeedRow;

        bool Occupied(int bed) => occupied.ContainsKey(bed);
        ushort SeedAt(int bed) => occupied.TryGetValue(bed, out var row) ? row : (ushort)0;
        bool IsDead(ushort s1, ushort s2) => SeedTable.IsDead(s1, s2);

        var kept = new List<PlantStep>();
        var allValid = true;

        foreach (var step in plan.Steps)
        {
            if (Occupied(step.BedNumber))
            {
                var seedName = SeedItems.ProduceName(step.SeedRow);
                plan.Warnings.Add($"Bed {step.BedNumber} is no longer empty; the {seedName} step was dropped.");
                allValid = false;
                continue;
            }

            if (plan.ExpectedTargets.TryGetValue(step.BedNumber, out var expected))
            {
                var intendedParents = Adjacency.Neighbours(patch.Kind, step.BedNumber)
                    .Where(Occupied)
                    .Where(n => SeedTable.TargetsFor(step.SeedRow, SeedAt(n)).Intersect(expected).Any())
                    .ToList();

                var firstValid = Adjacency.FirstValid(patch.Kind, step.BedNumber, step.SeedRow, Occupied, SeedAt, IsDead);

                if (intendedParents.Count != 1 || firstValid != intendedParents[0])
                {
                    var seedName = SeedItems.ProduceName(step.SeedRow);
                    plan.Warnings.Add(
                        $"Bed {step.BedNumber} ({seedName}) no longer isolates its cross the way the plan expected; skipped.");
                    allValid = false;
                    continue;
                }
            }

            occupied[step.BedNumber] = step.SeedRow;
            kept.Add(step);
        }

        plan.Steps.Clear();
        plan.Steps.AddRange(kept);
        return allValid;
    }

}
