using System;
using System.Collections.Generic;
using Gardener.Game;

namespace Gardener.Planner;

/// <summary>
/// The intercross walk over one patch's beds: right, down, up, left, first valid wins, an empty bed
/// or a dead cross skipped rather than blocking the walk. Bed adjacency itself is
/// <see cref="PatchKindExtensions.Neighbours"/>'s: a Deluxe patch's eight-bed ring, confirmed by the
/// official strategy guide and the community's own worked example. That method throws for Oblong and
/// Round, whose layout has never been measured; <see cref="Neighbours"/> below turns that throw into
/// an empty result so a planner never has to guard the throw at every call site, and so an unconfirmed
/// shape degrades to "nothing plannable here" rather than propagating an exception into a draw or tick
/// path.
/// </summary>
public static class Adjacency
{
    /// <summary><paramref name="bedNumber"/>'s ring-adjacent beds in walk order, or empty for a patch
    /// shape with no confirmed layout.</summary>
    public static IReadOnlyList<int> Neighbours(PatchKind kind, int bedNumber)
    {
        try
        {
            return kind.Neighbours(bedNumber);
        }
        catch (NotSupportedException)
        {
            return Array.Empty<int>();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// The first neighbour a seed newly planted in <paramref name="bedNumber"/> actually crosses with:
    /// walks <see cref="Neighbours"/> in order, skipping a neighbour <paramref name="occupied"/> calls
    /// empty and one <paramref name="isDead"/> calls a dead pairing with <paramref name="plantedSeed"/>
    /// — both skip the walk continues past, never a block that stops it — and returns the first bed
    /// that clears both. Null when every neighbour is empty or dead-paired, or when the patch's layout
    /// is not confirmed.
    /// </summary>
    public static int? FirstValid(
        PatchKind kind,
        int bedNumber,
        ushort plantedSeed,
        Func<int, bool> occupied,
        Func<int, ushort> seedAt,
        Func<ushort, ushort, bool> isDead)
    {
        foreach (var candidate in Neighbours(kind, bedNumber))
        {
            if (!occupied(candidate))
                continue;

            if (isDead(plantedSeed, seedAt(candidate)))
                continue;

            return candidate;
        }

        return null;
    }
}
