using System.Collections.Generic;

namespace Gardener.Planner;

/// <summary>One bed a plan wants planted: <see cref="SeedRow"/> in <see cref="SoilItemId"/>'s soil,
/// with <see cref="Why"/> naming the reason a player reads on the Plan tab — "parent, so the next step
/// can cross with it" or "crosses with bed N's parent to reach the target".</summary>
public readonly record struct PlantStep(int BedNumber, ushort SeedRow, uint SoilItemId, string Why);

/// <summary>
/// One target seed's plan for one patch: an ordered list of <see cref="PlantStep"/>s — the first step
/// is always a parent, since a first-seed-in-an-empty-patch cross has no neighbour to land on — plus
/// every outcome each crossing step could actually land on (most crosses have two, so a plan never
/// implies one is guaranteed), and the warnings that explain a step that could not be emitted at all.
/// </summary>
public sealed class LayoutPlan
{
    public List<PlantStep> Steps { get; } = new();

    /// <summary>Every row a crossing step at <c>BedNumber</c> could actually produce, keyed by that bed
    /// number rather than carried positionally on <see cref="PlantStep"/> — a step that only places a
    /// parent has no entry here, since planting it alone produces nothing.</summary>
    public Dictionary<int, IReadOnlyList<uint>> ExpectedTargets { get; } = new();

    public List<string> Warnings { get; } = new();
}
