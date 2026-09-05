using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Journal;
using XivHubPluginKit.Inventory;

namespace Gardener.Planner;

/// <summary>
/// Builds a <see cref="LayoutPlan"/> that fills a patch with pairs crossing toward a target seed.
/// Occupancy and seed identity always come from a live <c>GardenMemory.Read(patch)</c> snapshot passed
/// in by the caller — never re-read here — and the journal is consulted only to exclude a withered
/// occupying bed as a parent, since a withered plant cannot be crossbred with. Read-only throughout:
/// nothing here plants anything, it only proposes what <c>SchedulerMain</c>'s Plan sweep would plant.
/// </summary>
public static class CrossPlanner
{
    private readonly record struct FillAssignment(uint Anchor, uint Cross, IReadOnlyList<int> ExistingAnchorBeds);

    /// <summary>Which of the pair is the anchor already on the ground (or about to be) and which is
    /// planted fresh to trigger every cross, carrying every bed the anchor already occupies, not just
    /// one, since <see cref="PlanFillStep"/> may be topping up a layout that is already partway
    /// planted.</summary>
    private static FillAssignment? AssignFill(uint a, uint b, Func<uint, IReadOnlyList<int>> occupyingBeds, Func<uint, int> heldCount)
    {
        var aBeds = occupyingBeds(a);
        var bBeds = occupyingBeds(b);

        if (aBeds.Count > 0 && bBeds.Count > 0)
            return null;
        if (aBeds.Count > 0)
            return heldCount(b) > 0 ? new FillAssignment(a, b, aBeds) : null;
        if (bBeds.Count > 0)
            return heldCount(a) > 0 ? new FillAssignment(b, a, bBeds) : null;
        return heldCount(a) > 0 && heldCount(b) > 0 ? new FillAssignment(a, b, Array.Empty<int>()) : null;
    }

    /// <summary>
    /// A Deluxe ring's own bipartition, walked from bed 1 through <see cref="Adjacency.Neighbours"/>
    /// rather than assumed from bed numbering: every bed two colours apart from any neighbour, which an
    /// 8-bed ring always allows since it is an even cycle. The alternating fill layout plants one parent
    /// across an entire colour and the other across the rest, so this is computed once and read by
    /// colour rather than hardcoding which bed numbers land on which side.
    /// </summary>
    private static Dictionary<int, int> TwoColorRing(PatchKind kind)
    {
        var colors = new Dictionary<int, int> { [1] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(1);
        while (queue.Count > 0)
        {
            var bed = queue.Dequeue();
            foreach (var neighbour in Adjacency.Neighbours(kind, bed))
            {
                if (colors.ContainsKey(neighbour))
                    continue;
                colors[neighbour] = 1 - colors[bed];
                queue.Enqueue(neighbour);
            }
        }
        return colors;
    }

    /// <summary>
    /// Fills a Deluxe patch with as many pairs of <paramref name="target"/> as <paramref name="requestedBeds"/>
    /// calls for, bounded by whichever of the step's own bed budget, the patch's free beds or the parents
    /// actually held runs out first. The beds alternate around the ring — one parent across every bed of
    /// one <see cref="TwoColorRing"/> colour, the other parent across every bed of the other — the layout
    /// the official strategy guide's own worked example and the community's 4x4 setup guide both use.
    /// Every cross bed then has the same seed on both ring sides, so the intercross walk's right/down/up/
    /// left order never decides the outcome and no isolated bed is needed. Steps are emitted anchor beds
    /// first and cross beds second: an anchor planted while its ring neighbours are still empty crosses
    /// with nothing and stays itself, so only once every anchor is down does planting a cross bed land it
    /// against a same-seed neighbour on both sides. The plan is re-checked against itself with
    /// <see cref="Verify"/> before it is returned.
    /// </summary>
    public static LayoutPlan PlanFillStep(
        ushort target, int requestedBeds, Patch patch, IReadOnlyList<BedState> memory, IReadOnlyList<SlotView> inventory)
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

        IReadOnlyList<int> OccupyingBeds(uint row) =>
            occupiedByBed
                .Where(kv => kv.Value == row && GardenJournal.Get(patch.Key, kv.Key)?.ObservedWithered != true)
                .Select(kv => kv.Key)
                .OrderBy(b => b)
                .ToList();

        int HeldCount(uint row) =>
            SeedItems.SeedItemForRow(row) is { } itemId
                ? inventory.Where(s => s.ItemId == itemId).Sum(s => (int)s.Qty)
                : 0;

        var requestedPairs = Math.Max(0, requestedBeds / 2);

        var scored = candidatePairs
            .Select(pair => (Pair: pair, Assignment: AssignFill(pair.A, pair.B, OccupyingBeds, HeldCount)))
            .Where(x => x.Assignment is not null)
            .Select(x => (x.Pair, Assignment: x.Assignment!.Value,
                Efficiency: SeedTable.EfficiencyFor(x.Pair.A, x.Pair.B, target)))
            .OrderByDescending(x => x.Assignment.ExistingAnchorBeds.Count > 0)
            .ThenByDescending(x => x.Efficiency ?? -1)
            .ToList();

        if (scored.Count == 0)
        {
            plan.Warnings.Add(
                $"{targetName} needs a pair currently held or already growing on this patch; none is available.");
            return plan;
        }

        var chosen = scored[0];
        var anchorRow = chosen.Assignment.Anchor;
        var crossRow = chosen.Assignment.Cross;
        var existingAnchorBeds = chosen.Assignment.ExistingAnchorBeds;
        var allTargets = SeedTable.TargetsFor(anchorRow, crossRow);
        var anchorName = SeedItems.ProduceName(anchorRow);
        var crossName = SeedItems.ProduceName(crossRow);

        var colors = TwoColorRing(patch.Kind);
        var anchorColor = existingAnchorBeds.Count > 0 ? colors[existingAnchorBeds[0]] : colors[1];
        var crossColor = 1 - anchorColor;

        bool IsFree(int bed) => !occupiedByBed.ContainsKey(bed);

        var anchorFreeSlots = colors.Where(kv => kv.Value == anchorColor && IsFree(kv.Key))
            .Select(kv => kv.Key).OrderBy(b => b).ToList();
        var crossFreeSlots = colors.Where(kv => kv.Value == crossColor && IsFree(kv.Key))
            .Select(kv => kv.Key).OrderBy(b => b).ToList();

        var pairsFromBeds = Math.Min(existingAnchorBeds.Count + anchorFreeSlots.Count, crossFreeSlots.Count);
        var pairsFromSeeds = Math.Min(existingAnchorBeds.Count + HeldCount(anchorRow), HeldCount(crossRow));
        var pairs = new[] { requestedPairs, pairsFromBeds, pairsFromSeeds }.Min();

        string PairWord(int n) => $"{n} pair{(n == 1 ? "" : "s")}";

        if (pairs <= 0)
        {
            plan.Warnings.Add(pairsFromSeeds <= 0
                ? $"You need {anchorName} and {crossName} to start this pair; none held."
                : "No free beds are left on this patch to start a new pair.");
            return plan;
        }

        if (pairs < requestedPairs)
        {
            var reasons = new List<string>();
            if (pairsFromBeds == pairs && pairsFromBeds < requestedPairs)
                reasons.Add($"only {PairWord(pairsFromBeds)} of free beds fit on this patch");
            if (pairsFromSeeds == pairs && pairsFromSeeds < requestedPairs)
                reasons.Add($"you hold enough {anchorName} and {crossName} for {PairWord(pairsFromSeeds)}");
            plan.Warnings.Add($"Planting {PairWord(pairs)} instead of {requestedPairs}: {string.Join(" and ", reasons)}.");
        }

        var newAnchorBeds = anchorFreeSlots.Take(Math.Max(0, pairs - existingAnchorBeds.Count)).ToList();
        var newCrossBeds = crossFreeSlots.Take(pairs).ToList();

        if (newAnchorBeds.Count > 0)
        {
            var parentSoil = GardeningItems.BestSoil(Plugin.C.SoilForYield, inventory);
            if (parentSoil is null)
            {
                plan.Warnings.Add(
                    GardeningItems.SoilUnavailable(Plugin.C.SoilForYield) ?? "No soil available for the parent step.");
                return plan;
            }
            foreach (var bed in newAnchorBeds)
                plan.Steps.Add(new PlantStep(bed, (ushort)anchorRow, parentSoil.ItemId,
                    $"Parent for {targetName}: plant here first so its neighbours can cross with it."));
        }

        var crossSoil = GardeningItems.BestSoil(Plugin.C.SoilForCross, inventory);
        if (crossSoil is null)
        {
            plan.Steps.Clear(); // anchors alone plant nothing worth doing without the crosses after them
            plan.Warnings.Add(
                GardeningItems.SoilUnavailable(Plugin.C.SoilForCross) ?? "No soil available for the crossing step.");
            return plan;
        }

        var effText = SeedTable.EfficiencyFor(anchorRow, crossRow, target) is { } pct
            ? $"ffxivgardening.com rates this pair {pct}/100"
            : "this pair has no rating yet";
        foreach (var bed in newCrossBeds)
        {
            plan.Steps.Add(new PlantStep(bed, (ushort)crossRow, crossSoil.ItemId,
                $"Crosses with its neighbouring {anchorName} to reach {targetName} ({effText})."));
            plan.ExpectedTargets[bed] = allTargets;
        }

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

    /// <summary>
    /// Re-checks a plan against a live <c>GardenMemory.Read(patch)</c> snapshot, applying each step in
    /// order (a crossing step may depend on a parent step immediately before it) and downgrading any
    /// step to a <see cref="LayoutPlan.Warnings"/> entry rather than trusting it: the bed it targets is
    /// no longer empty, or — for a crossing step — <see cref="Adjacency.FirstValid"/> no longer resolves
    /// to one of the occupied neighbours the plan expects to hold one of <see cref="LayoutPlan.ExpectedTargets"/>'s
    /// parents. More than one such neighbour is fine as long as they all hold the same seed, since the
    /// cross is identical whichever one the walk actually lands on; different seeds on either side is
    /// the genuine ambiguity this guards against. Safe to call twice: once right after
    /// <see cref="PlanFillStep"/> builds the plan, and again immediately before a Plan sweep runs it,
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

                // An isolated single-pair cross has exactly one intended parent. A fill layout's cross
                // bed instead has two, one on each ring side, but both hold the same seed by
                // construction, so every intended parent resolves to the identical cross regardless of
                // which one the right/down/up/left walk actually lands on — unambiguous even though the
                // count is not 1.
                var unambiguous = intendedParents.Count > 0 && intendedParents.Select(SeedAt).Distinct().Count() == 1;

                if (!unambiguous || firstValid is not { } resolved || !intendedParents.Contains(resolved))
                {
                    var seedName = SeedItems.ProduceName(step.SeedRow);
                    plan.Warnings.Add(
                        $"Bed {step.BedNumber} ({seedName}) no longer crosses the way the plan expected; skipped.");
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
