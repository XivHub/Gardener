using System;

namespace Gardener.Journal;

/// <summary>
/// One bed's persisted state, keyed by <see cref="PatchKey"/> (house + patch position, never a
/// character) and <see cref="BedNumber"/> (the game's own "Nth Bed" numbering). A garden belongs to
/// the plot, not to whichever character is standing in it, so this record is shared across every
/// character on the account — nothing here is ever partitioned by <c>ContentId</c> or character name.
///
/// <see cref="PlantedAt"/> and <see cref="LastTendedAt"/> are the only two facts the game exposes
/// nowhere — no growth timestamp exists anywhere in the structure the passive read decodes — and are
/// the entire reason this journal exists.
///
/// <see cref="SeedRow"/>, <see cref="LastSeenStage"/> and <see cref="LastSeenAt"/> are a cache of the
/// last passive read, written only by <see cref="GardenJournal.Reconcile"/> and never edited by hand:
/// <c>DataMap</c> is unreadable outside the housing territory, and reminders must still work from
/// another world. <see cref="LastSeenByCharacter"/> is a display-only note of which character made
/// that last observation; it carries no identity of its own and is never part of the lookup key.
///
/// There is no <c>Condition</c> enum and no <c>Dead</c> / <c>Depressed</c> / <c>Vigorous</c> state:
/// those five <c>TALK_*</c> sentences are unreachable through the bed menu, and <c>Value3</c>/
/// <c>Value4</c> read 0 on every bed observed, so the plugin has no observable source for any of them.
/// </summary>
public sealed class BedRecord
{
    public string PatchKey { get; set; } = string.Empty;
    public int BedNumber { get; set; }

    public ushort SeedRow { get; set; }
    public uint SoilItemId { get; set; }
    public DateTimeOffset? PlantedAt { get; set; }
    public bool PlantedAtEstimated { get; set; }
    public DateTimeOffset? LastTendedAt { get; set; }
    public DateTimeOffset? LastFertilizedAt { get; set; }
    public int FertilizerCount { get; set; }
    public DateTimeOffset? FirstSeenStage4At { get; set; }
    public DateTimeOffset? FirstSeenHarvestOfferedAt { get; set; }
    public byte LastSeenStage { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool PlantedByGardener { get; set; }

    /// <summary>Display-only: the name of the character that produced the last passive read or
    /// action recorded here. Never used as a key or a filter — a garden belongs to the plot, and an
    /// alt tending a bed tends it exactly as much as the main would.</summary>
    public string? LastSeenByCharacter { get; set; }
}
