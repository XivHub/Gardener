using System.Linq;
using Gardener.Journal;
using Gardener.Planner;

namespace Gardener.Game;

/// <summary>
/// The one impure resolution of route capacity: a live <see cref="PatchDiscovery"/> read when the
/// player is standing in a garden, else the journal's own recorded patch kinds, else a single
/// assumed Deluxe patch. <see cref="Planner.GoalRoute.Solve"/> must never call this directly — it
/// takes the resulting <see cref="GardenCapacity"/> as a parameter instead, resolved once per draw by
/// a caller that is allowed to read live state.
/// </summary>
public static class PatchCapacity
{
    /// <summary>A live read always outranks the journal: a patch's kind only changes by demolition and
    /// rebuild, so whatever <see cref="PatchDiscovery"/> sees right now is at least as current as what
    /// was last recorded, and the journal only has to speak for a house nobody is standing in.</summary>
    public static GardenCapacity Current()
    {
        if (PatchDiscovery.Patches.Count > 0)
            return GardenCapacity.FromKinds(PatchDiscovery.Patches.Select(p => p.Kind));

        var known = GardenJournal.KnownPatchKinds();
        return known.Count > 0 ? GardenCapacity.FromKinds(known) : GardenCapacity.AssumedSingleDeluxe;
    }
}
