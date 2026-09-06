using System.Collections.Generic;
using System.Linq;
using Gardener.Game;

namespace Gardener.Planner;

/// <summary>One patch's own slice of a multi-patch fill: its <see cref="LayoutPlan"/> and how many
/// pairs that plan represents (<see cref="LayoutPlan.ExpectedTargets"/>'s own count — one entry per
/// cross bed, and every pair has exactly one). Never carries a bed that belongs to any other patch.</summary>
public sealed record PatchLayout(Patch Patch, LayoutPlan Plan, int Pairs);

/// <summary>
/// The whole-route split <see cref="CrossPlanner.PlanFillStepAcross"/> returns: one
/// <see cref="PatchLayout"/> per usable patch that got at least one bed, concatenated rather than
/// merged — see <see cref="CrossPlanner.PlanFillStepAcross"/>'s own doc for why a pair can never
/// straddle two of these. <see cref="Warnings"/> holds split-level warnings (an unconfirmed-layout
/// patch skipped entirely); each patch's own planning warnings stay on its own
/// <see cref="PatchLayout.Plan"/>.
/// </summary>
public sealed record MultiPatchLayout(IReadOnlyList<PatchLayout> Patches, IReadOnlyList<string> Warnings)
{
    /// <summary>Total beds this round will plant, summed across every patch's own
    /// <see cref="LayoutPlan.Steps"/> count.</summary>
    public int TotalBeds => Patches.Sum(p => p.Plan.Steps.Count);
}
