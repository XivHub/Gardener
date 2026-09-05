using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Gardener.Journal;

/// <summary>
/// House-level metadata, keyed by <see cref="HouseKey"/> (the same <c>HouseId</c>-derived prefix
/// <see cref="BedRecord.PatchKey"/> carries). Each of the user's characters can own a separate house —
/// housing is home-world bound, and <c>HouseId</c> already carries <c>WorldId</c>, so a house on
/// another world separates cleanly with no schema change — so this exists to answer "which character
/// can reach this garden" without a live scan: a due reminder for a house the logged-in character
/// cannot enter needs to say who to switch to, not disappear.
/// </summary>
public sealed class HouseRecord
{
    public string HouseKey { get; set; } = string.Empty;

    /// <summary>Set from whichever observing character actually owns or holds an estate slot on this
    /// house; left unset for a house only ever seen through FC/permission access rather than
    /// ownership, since that character's own read cannot tell which estate slot it is.</summary>
    public EstateType? EstateType { get; set; }

    /// <summary>Display names of every character observed standing in this house with the game's own
    /// permission to be there. Never part of any lookup key — purely so a reminder can name who to
    /// log in as.</summary>
    public List<string> ObservedCharacters { get; set; } = new();
}
