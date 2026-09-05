using System;
using Gardener.Game;

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
/// The bed menu itself never offers a <c>Condition</c>: <see cref="LastObservedCropState"/> and
/// <see cref="LastObservedCropStateAt"/> instead cache the most recent of the five <c>TALK_*</c>
/// sentences a bed interaction echoes into chat, written only by
/// <see cref="GardenJournal.ReconcileCropObservation"/>. <see cref="ObservedWithered"/> latches once
/// <see cref="MenuKey.TalkDead"/> has ever been observed for this bed.
/// </summary>
public sealed class BedRecord
{
    public string PatchKey { get; set; } = string.Empty;
    public int BedNumber { get; set; }

    public ushort SeedRow { get; set; }
    public uint SoilItemId { get; set; }
    public DateTimeOffset? PlantedAt { get; set; }
    public bool PlantedAtEstimated { get; set; }

    /// <summary>Set only when <see cref="PlantedAt"/> was derived from an empty-bed observation
    /// followed by an occupied one: the width of the gap between the two polls, i.e. how much later
    /// than <see cref="PlantedAt"/> the planting could actually have happened. Null for every other
    /// anchor — a hand-set <see cref="PlantedAtEstimated"/> guess, an exact planting time, or no
    /// anchor at all — never a synthesized confidence bucket standing in for this number.</summary>
    public TimeSpan? PlantedAtUncertainty { get; set; }

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

    /// <summary>The most recent <c>TALK_*</c> sentence observed for this bed, and when. Null until
    /// the first crop chat line for this bed is ever read.</summary>
    public MenuKey? LastObservedCropState { get; set; }
    public DateTimeOffset? LastObservedCropStateAt { get; set; }

    /// <summary>Set once a <see cref="MenuKey.TalkDead"/> line is observed for this bed. Growth.WiltsAt
    /// stops projecting a wilt time once this is set — there is nothing left to tend.</summary>
    public bool ObservedWithered { get; set; }
}
