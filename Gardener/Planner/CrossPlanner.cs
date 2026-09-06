using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Journal;
using Gardener.Localization;
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
    /// calls for, bounded by whichever of the step's own bed budget, the patch's free beds, the parents
    /// actually held or the topsoil actually held runs out first. The beds alternate around the ring —
    /// one parent across every bed of one <see cref="TwoColorRing"/> colour, the other parent across
    /// every bed of the other — the layout the official strategy guide's own worked example and the
    /// community's 4x4 setup guide both use.
    /// Every cross bed then has the same seed on both ring sides, so the intercross walk's right/down/up/
    /// left order never decides the outcome and no isolated bed is needed. Steps are emitted anchor beds
    /// first and cross beds second: an anchor planted while its ring neighbours are still empty crosses
    /// with nothing and stays itself, so only once every anchor is down does planting a cross bed land it
    /// against a same-seed neighbour on both sides. That also decides the soil: an anchor crosses with
    /// nothing and is harvested for its own seed, so it takes <c>SoilForYield</c>, and only the cross beds
    /// take <c>SoilForCross</c>, the one that moves the intercross rate. The plan is re-checked against
    /// itself with <see cref="Verify"/> before it is returned.
    /// </summary>
    public static LayoutPlan PlanFillStep(
        ushort target, int requestedBeds, Patch patch, IReadOnlyList<BedState> memory, IReadOnlyList<SlotView> inventory)
    {
        var plan = new LayoutPlan();
        var targetName = SeedItems.ProduceName(target);

        var candidatePairs = SeedTable.Pairs(target);
        if (candidatePairs.Count == 0)
        {
            plan.Warnings.Add(Loc.Format(Strings.Plan_NoKnownCross, targetName));
            return plan;
        }

        if (patch.Kind != PatchKind.Deluxe)
        {
            // PatchKind names stay literal (do-not-translate register: no EObj-to-item mapping resolves
            // Deluxe/Oblong/Round to the client's own furniture name), wrapped in a whole-sentence template.
            plan.Warnings.Add(Loc.Format(Strings.Plan_AdjacencyUnconfirmed, patch.Kind));
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
            plan.Warnings.Add(Loc.Format(Strings.Plan_NoPairAvailable, targetName));
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

        // Anchors and crosses take different soil: an anchor is planted with empty neighbours, so it
        // crosses with nothing and is harvested for its own seeds, which is what SoilForYield is for;
        // only the cross bed's soil moves the intercross rate.
        var crossSoil = GardeningItems.BestSoil(Plugin.C.SoilForCross, inventory);
        if (crossSoil is null)
        {
            plan.Warnings.Add(
                GardeningItems.SoilUnavailable(Plugin.C.SoilForCross) ?? Strings.Plan_NoSoilForCrossingFallback);
            return plan;
        }
        var parentSoil = GardeningItems.BestSoil(Plugin.C.SoilForYield, inventory);

        int HeldItemCount(uint itemId) => inventory.Where(s => s.ItemId == itemId).Sum(s => (int)s.Qty);

        var pairsFromBeds = Math.Min(existingAnchorBeds.Count + anchorFreeSlots.Count, crossFreeSlots.Count);
        var pairsFromSeeds = Math.Min(existingAnchorBeds.Count + HeldCount(anchorRow), HeldCount(crossRow));
        var parentStock = parentSoil is null ? 0 : HeldItemCount(parentSoil.ItemId);
        var crossStock = HeldItemCount(crossSoil.ItemId);

        // Both halves draw on the same bag, so when the two preferences resolve to the same item its
        // stock has to cover the anchors and the crosses together rather than each on its own.
        var pairsFromSoil = parentSoil is not null && parentSoil.ItemId == crossSoil.ItemId
            ? (crossStock + existingAnchorBeds.Count) / 2
            : Math.Min(crossStock, existingAnchorBeds.Count + parentStock);

        var pairs = new[] { requestedPairs, pairsFromBeds, pairsFromSeeds, pairsFromSoil }.Min();

        // "N pair"/"N pairs" as a standalone noun phrase (composition contract rule 1), reused by every
        // reason sentence below rather than composed inline, since the plural word itself would
        // otherwise be a fragment substituted into someone else's sentence.
        string PairCount(int n) => n == 1
            ? Loc.Format(Strings.Plan_PairCount_One, Formats.Number(n))
            : Loc.Format(Strings.Plan_PairCount_Other, Formats.Number(n));

        string SoilShortfall()
        {
            if (parentSoil is null)
                return GardeningItems.SoilUnavailable(Plugin.C.SoilForYield) ?? Strings.Plan_NoSoilForParentFallback;
            if (parentSoil.ItemId == crossSoil.ItemId)
                return Loc.Format(Strings.Plan_SoilSharedStock, Formats.Number(crossStock), ItemSheet.Name(crossSoil.ItemId));
            return crossStock <= 0
                ? Loc.Format(Strings.Plan_NoCrossSoilLeft, ItemSheet.Name(crossSoil.ItemId))
                : Loc.Format(Strings.Plan_ParentSoilStock, Formats.Number(parentStock), ItemSheet.Name(parentSoil.ItemId));
        }

        if (pairs <= 0)
        {
            plan.Warnings.Add(
                pairsFromSeeds <= 0 ? Loc.Format(Strings.Plan_NeedParentsToStart, anchorName, crossName)
                : pairsFromSoil <= 0 ? SoilShortfall()
                : Strings.Plan_NoFreeBedsLeft);
            return plan;
        }

        // One header sentence naming the shortfall, then each limiting reason as its own independent
        // warning — the reasons are separate observations about the patch, not clauses of one sentence,
        // and "and"-gluing them would inflect wrongly the moment there is more than one in Spanish.
        if (pairs < requestedPairs)
        {
            plan.Warnings.Add(Loc.Format(Strings.Plan_PlantingFewerPairs, PairCount(pairs), PairCount(requestedPairs)));
            if (pairsFromBeds == pairs && pairsFromBeds < requestedPairs)
                plan.Warnings.Add(Loc.Format(Strings.Plan_ReasonBeds, PairCount(pairsFromBeds)));
            if (pairsFromSeeds == pairs && pairsFromSeeds < requestedPairs)
                plan.Warnings.Add(Loc.Format(Strings.Plan_ReasonSeeds, anchorName, crossName, PairCount(pairsFromSeeds)));
            if (pairsFromSoil == pairs && pairsFromSoil < requestedPairs)
                plan.Warnings.Add(Loc.Format(Strings.Plan_ReasonSoil, PairCount(pairsFromSoil)));
        }

        var newAnchorBeds = anchorFreeSlots.Take(Math.Max(0, pairs - existingAnchorBeds.Count)).ToList();
        var newCrossBeds = crossFreeSlots.Take(pairs).ToList();

        if (newAnchorBeds.Count > 0 && parentSoil is not null)
        {
            foreach (var bed in newAnchorBeds)
                plan.Steps.Add(new PlantStep(bed, (ushort)anchorRow, parentSoil.ItemId,
                    Loc.Format(Strings.Plan_WhyParent, targetName)));
        }

        var why = SeedTable.EfficiencyFor(anchorRow, crossRow, target) is { } pct
            ? Loc.Format(Strings.Plan_WhyCrossRated, anchorName, targetName, Formats.Number(pct))
            : Loc.Format(Strings.Plan_WhyCrossUnrated, anchorName, targetName);
        foreach (var bed in newCrossBeds)
        {
            plan.Steps.Add(new PlantStep(bed, (ushort)crossRow, crossSoil.ItemId, why));
            plan.ExpectedTargets[bed] = allTargets;
        }

        if (allTargets.Count > 1)
        {
            var others = allTargets.Where(t => t != target).Select(SeedItems.ProduceName).ToList();
            plan.Warnings.Add(Loc.Format(Strings.Plan_AlsoYields, TextList.Or(others), targetName));
        }

        if (SeedTable.Yields(target)?.Seed is { } targetSeedYield &&
            crossSoil.Grade < targetSeedYield.Length && targetSeedYield[crossSoil.Grade] < 1)
        {
            plan.Warnings.Add(Loc.Format(Strings.Plan_NoSeedReturnAtGrade, targetName, Formats.Number(crossSoil.Grade)));
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
                plan.Warnings.Add(Loc.Format(Strings.Plan_BedNoLongerEmpty, Formats.Number(step.BedNumber), seedName));
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
                    plan.Warnings.Add(Loc.Format(Strings.Plan_BedCrossMismatch, Formats.Number(step.BedNumber), seedName));
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
