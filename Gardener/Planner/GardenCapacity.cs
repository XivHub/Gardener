using System.Collections.Generic;
using Gardener.Game;

namespace Gardener.Planner;

/// <summary>
/// How many crossbreed pairs the current garden can plant at once, summed over confirmed-layout
/// patches only (<see cref="PatchKindExtensions.HasConfirmedLayout"/>) so a route never promises a
/// round <c>CrossPlanner.PlanFillStep</c> will then refuse to lay out. <see cref="Assumed"/> marks
/// the away-from-garden fallback, so the rendered sentence can say the capacity is a guess rather
/// than a measurement. A readonly record struct: value equality for free, which is what lets it join
/// a plan cache key without a hand-written comparer.
/// </summary>
public readonly record struct GardenCapacity(int Pairs, int PatchCount, int UnusablePatchCount, bool Assumed)
{
    public int Beds => Pairs * 2;

    /// <summary>The fallback before any patch has ever been observed live or recorded in the
    /// journal: one Deluxe patch's worth of pairs, with no unusable patch of its own.</summary>
    public static readonly GardenCapacity AssumedSingleDeluxe =
        new(PatchKind.Deluxe.BedCount() / 2, 1, 0, true);

    /// <summary>
    /// Sums <see cref="PatchKindExtensions.BedCount"/> over every kind whose layout is confirmed,
    /// counting the rest into <see cref="UnusablePatchCount"/> without contributing a single pair for
    /// them. When nothing qualifies, falls back to <see cref="AssumedSingleDeluxe"/> while preserving
    /// the observed unusable count, so the rendered text can say both "assuming one Deluxe patch" and
    /// "your Oblong patch is left out". Pure: no live scan, no journal read.
    /// </summary>
    public static GardenCapacity FromKinds(IEnumerable<PatchKind> kinds)
    {
        var pairs = 0;
        var patchCount = 0;
        var unusable = 0;

        foreach (var kind in kinds)
        {
            if (kind.HasConfirmedLayout())
            {
                pairs += kind.BedCount() / 2;
                patchCount++;
            }
            else
            {
                unusable++;
            }
        }

        return patchCount > 0
            ? new GardenCapacity(pairs, patchCount, unusable, false)
            : AssumedSingleDeluxe with { UnusablePatchCount = unusable };
    }
}
