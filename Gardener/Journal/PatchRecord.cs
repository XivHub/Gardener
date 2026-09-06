using System;
using Gardener.Game;

namespace Gardener.Journal;

/// <summary>
/// Persisted identity for one discovered <see cref="Patch"/>, keyed by <see cref="PatchKey"/> (the
/// same <c>Patch.Key</c> string every <see cref="BedRecord"/> already carries). This is what lets a
/// header, a reminder or an away-from-garden capacity estimate name a patch's kind, bed count and
/// plot from anywhere in the world, without a live <see cref="PatchDiscovery"/> read.
///
/// <see cref="Ordinal"/> is assigned once, the first time a patch key is ever seen, and exists only
/// to tell two patches on the same plot apart in prose ("patch 1" vs "patch 2"). It is never part of
/// any lookup key — <see cref="PatchKey"/> alone is identity everywhere else — and never renumbered
/// once assigned, even if an earlier-ordinal patch is later removed.
/// </summary>
public sealed class PatchRecord
{
    public string PatchKey { get; set; } = string.Empty;
    public string HouseKey { get; set; } = string.Empty;
    public PatchKind Kind { get; set; }
    public int BedCount { get; set; }

    /// <summary>Zero-based, as <see cref="PatchDiscovery.CurrentPlotOrNull"/> reports it; null when
    /// the patch was recorded without a resolved plot (apartment, or the housing manager was
    /// unavailable at the time). Display always adds one.</summary>
    public int? PlotIndex { get; set; }

    public int Ordinal { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
