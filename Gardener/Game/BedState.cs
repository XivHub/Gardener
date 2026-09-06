using System;

namespace Gardener.Game;

/// <summary>The three growth buckets <c>DataMap.Value2</c> distinguishes. The name is
/// <c>MatureCandidate</c>, not <c>Ripe</c>: stage 4 is proven to mark an established planting, and
/// nothing yet proves it means harvestable. The <c>Harvest</c> menu entry at
/// act time remains the authority for that question.</summary>
public enum Maturity
{
    Empty,
    Growing,
    MatureCandidate,
}

/// <summary>
/// One bed as the game itself reports it: a passive read of one <c>HousingObjectManager.DataMap</c>
/// value set, taken at <see cref="ReadAt"/>. <see cref="BedNumber"/> is one-based and is the game's
/// own "Nth Bed" numbering (slot index <c>N</c> in <c>DataMap</c> is bed <c>N+1</c>), not a spatial
/// index — every bed in a patch reports the patch's own world position, so there is no spatial index
/// to report.
/// </summary>
public readonly record struct BedState(
    string PatchKey,
    int BedNumber,
    ushort SeedRow,
    byte Stage,
    byte Value3,
    byte Value4,
    byte Value5,
    DateTimeOffset ReadAt)
{
    public bool IsEmpty => SeedRow == 0;

    public Maturity Maturity => Stage switch
    {
        0 => Maturity.Empty,
        1 or 2 or 3 => Maturity.Growing,
        4 => Maturity.MatureCandidate,
        _ => Maturity.Growing,
    };
}
