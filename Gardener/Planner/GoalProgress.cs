using System;
using System.Collections.Generic;
using System.Linq;
using Gardener.Game;
using Gardener.Journal;

namespace Gardener.Planner;

/// <summary>Which of the four rules a <see cref="GoalPlan"/> step currently reads as. <see
/// cref="CrossedAndGrowing"/> and <see cref="AttemptUnresolved"/> both mean "a bed is already working
/// on this"; they differ only in whether the bed already shows the crossed identity or still shows one
/// of its parents (G1's companion open question: whether a crossbreed's true identity is visible before
/// harvest).</summary>
public enum GoalStepStatus
{
    Done,
    CrossedAndGrowing,
    AttemptUnresolved,
    NotStarted,
}

/// <summary><see cref="PatchKey"/>, <see cref="BedNumber"/> and <see cref="Window"/> are set only for
/// the two growing statuses — <see cref="PatchKey"/> is what lets a renderer fetch the same
/// <see cref="BedRecord"/> back for a wilt deadline, the way the Garden tab already does for that bed;
/// <see cref="Blocker"/> only for <see cref="GoalStepStatus.NotStarted"/> — carries what is actually
/// missing (a seed count) rather than a bare "not started" whenever something concrete can be named.</summary>
public readonly record struct GoalStepProgress(GoalStepStatus Status, string? PatchKey, int? BedNumber, HarvestWindow? Window, string? Blocker);

/// <summary><see cref="CurrentStepIndex"/> is the first step that is not <see
/// cref="GoalStepStatus.Done"/> — the one the tab expands and offers to hand off — or the last step
/// when every step already reads Done.</summary>
public sealed record GoalStatus(IReadOnlyList<GoalStepProgress> Steps, int CurrentStepIndex);

/// <summary>
/// Works out which step of a <see cref="GoalPlan"/> the player is standing on from live inventory and
/// what is in the ground, per the design's status table. Takes <paramref name="beds"/> as a plain list
/// rather than reading <see cref="GardenMemory"/> itself: the caller supplies a live
/// <see cref="GardenMemory.Read"/> snapshot when the player is at a discovered patch, or synthesises one
/// from <see cref="GardenJournal.AllForHouse"/>'s cached <c>SeedRow</c>/<c>LastSeenStage</c> when away —
/// this class does not care which, only that <paramref name="observedAt"/> carries the age of whichever
/// it got.
/// </summary>
public static class GoalProgress
{
    public static GoalStatus Evaluate(
        GoalPlan plan, IReadOnlyDictionary<uint, int> held, IReadOnlyList<BedState> beds, DateTimeOffset? observedAt)
    {
        var raw = plan.Steps.Select(step => EvaluateStep(step, held, beds)).ToArray();

        // Later-step evidence promotes every earlier step to Done: a player who bought a later step's
        // seed from another player, or crossed for it before ever opening this tab, is not told to go
        // and redo work already done.
        var promoted = new GoalStepProgress[raw.Length];
        var sawEvidence = false;
        for (var i = raw.Length - 1; i >= 0; i--)
        {
            promoted[i] = sawEvidence ? new GoalStepProgress(GoalStepStatus.Done, null, null, null, null) : raw[i];
            if (raw[i].Status != GoalStepStatus.NotStarted)
                sawEvidence = true;
        }

        var currentIndex = Array.FindIndex(promoted, p => p.Status != GoalStepStatus.Done);
        if (currentIndex < 0)
            currentIndex = promoted.Length - 1;

        return new GoalStatus(promoted, currentIndex);
    }

    private static GoalStepProgress EvaluateStep(GoalStep step, IReadOnlyDictionary<uint, int> held, IReadOnlyList<BedState> beds) =>
        step switch
        {
            ObtainStep o => EvaluateObtain(o, held),
            CrossStep c => EvaluateCross(c, held, beds),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "unhandled GoalStep"),
        };

    private static GoalStepProgress EvaluateObtain(ObtainStep step, IReadOnlyDictionary<uint, int> held)
    {
        var heldCount = held.GetValueOrDefault(step.SeedRow);
        if (heldCount >= step.Needed)
            return new GoalStepProgress(GoalStepStatus.Done, null, null, null, null);

        var shortBy = step.Needed - heldCount;
        return new GoalStepProgress(GoalStepStatus.NotStarted, null, null, null,
            $"You need {shortBy} more {SeedItemName(step.SeedRow)}.");
    }

    private static GoalStepProgress EvaluateCross(CrossStep step, IReadOnlyDictionary<uint, int> held, IReadOnlyList<BedState> beds)
    {
        var heldCount = held.GetValueOrDefault(step.TargetRow);
        if (heldCount >= step.Needed)
            return new GoalStepProgress(GoalStepStatus.Done, null, null, null, null);

        if (FirstOccupied(beds, b => b.SeedRow == step.TargetRow) is { } crossedBed)
            return new GoalStepProgress(GoalStepStatus.CrossedAndGrowing, crossedBed.PatchKey, crossedBed.BedNumber, HarvestWindowFor(crossedBed), null);

        // The crossbreed's true identity may not be legible before harvest, so a bed still reading as
        // the second-planted parent counts as the attempt in flight too — provided the anchor is
        // present somewhere on the same patch. This does not check ring adjacency (unlike
        // CrossPlanner.Verify, which the handoff still goes through before anything is planted); it is
        // a status hint, not a plantability check.
        if (FirstOccupied(beds, b => b.SeedRow == step.SecondSeedRow) is { } secondBed &&
            beds.Any(b => !b.IsEmpty && b.SeedRow == step.FirstSeedRow && b.PatchKey == secondBed.PatchKey))
        {
            return new GoalStepProgress(GoalStepStatus.AttemptUnresolved, secondBed.PatchKey, secondBed.BedNumber, HarvestWindowFor(secondBed), null);
        }

        var missing = new List<string>();
        if (held.GetValueOrDefault(step.FirstSeedRow) < 1 && !beds.Any(b => !b.IsEmpty && b.SeedRow == step.FirstSeedRow))
            missing.Add(SeedItemName(step.FirstSeedRow));
        if (held.GetValueOrDefault(step.SecondSeedRow) < 1)
            missing.Add(SeedItemName(step.SecondSeedRow));

        if (missing.Count > 0)
            return new GoalStepProgress(GoalStepStatus.NotStarted, null, null, null,
                $"You need {string.Join(" and ", missing)} to plant this step.");

        var shortBy = step.Needed - heldCount;
        return new GoalStepProgress(GoalStepStatus.NotStarted, null, null, null,
            $"You need {shortBy} more {SeedItemName(step.TargetRow)}.");
    }

    private static BedState? FirstOccupied(IReadOnlyList<BedState> beds, Func<BedState, bool> predicate)
    {
        foreach (var bed in beds)
            if (!bed.IsEmpty && predicate(bed))
                return bed;
        return null;
    }

    private static HarvestWindow? HarvestWindowFor(BedState bed) =>
        GardenJournal.Get(bed.PatchKey, bed.BedNumber) is { } record ? Growth.HarvestWindow(record) : null;

    private static string SeedItemName(uint row) =>
        SeedItems.SeedItemForRow(row) is { } itemId ? XivHubPluginKit.Inventory.ItemSheet.Name(itemId) : $"row {row}'s seed";
}
