using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;

namespace Gardener.Helpers;

/// <summary>
/// Environment guards for the scheduler: is the client in a safe state to act this frame, has the
/// player left the plot, and is there anywhere left to act at all. Mirrors
/// SealHunter's <c>Helpers/SealHunterGuard.cs</c>, with <see cref="BlockingReason"/> added because
/// Gardener has no travel step of its own to fall back on — a sweep that cannot start must say why
/// in the window, not just decline silently.
/// </summary>
public static class GardenerGuard
{
    // Deliberately excludes the Occupied* family (OccupiedInQuestEvent and friends): interacting with
    // a garden bed sets OccupiedInQuestEvent for as long as its menu is open, so treating it as a
    // per-frame stop condition would pause every sweep the instant it opened a bed. Those flags gate
    // only whether a new sweep may *start* — see OccupiedBlockingReason — never whether a running one
    // must pause. This is the opposite split from SealHunterGuard, where "occupied" always means
    // something went wrong rather than the plugin's own working state; keep it that way here.
    public static bool IsScreenReady() =>
        GenericHelpers.IsScreenReady()
        && !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.BetweenAreas51]
        && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene78];

    /// <summary>True once the player has moved more than <paramref name="max"/> yalms from
    /// <paramref name="origin"/> — the position recorded when the current sweep started. False
    /// (never triggers an abort) when the player's position cannot be read at all.</summary>
    public static bool PlayerMovedFrom(Vector3 origin, float max)
    {
        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        return position is { } p && Vector3.Distance(p, origin) > max;
    }

    public static bool InHousingTerritory() => HouseKey.Current() is not null;

    /// <summary>
    /// A sentence naming why a sweep cannot start right now, for both the pre-run guard and the
    /// window's button area, or null when nothing is blocking. Checked in order: whether the player is
    /// occupied (<see cref="OccupiedBlockingReason"/>), then <see cref="EnvironmentBlockingReason"/>.
    /// </summary>
    public static string? BlockingReason() => OccupiedBlockingReason() ?? EnvironmentBlockingReason();

    /// <summary>
    /// Pre-run only. A bed interaction sets these same condition flags for as long as its menu stays
    /// open, so the scheduler's per-frame guard deliberately checks <see cref="EnvironmentBlockingReason"/>
    /// instead of this method while a sweep is running — reading
    /// these here would mean a sweep stops the moment its own interaction opened a bed. Names the
    /// specific flag so the sentence is diagnosable rather than a bare "occupied".
    /// </summary>
    private static string? OccupiedBlockingReason()
    {
        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent])
            return "Player is occupied in a quest event (OccupiedInQuestEvent).";
        if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell])
            return "Player is occupied at a summoning bell (OccupiedSummoningBell).";
        if (Plugin.Condition[ConditionFlag.Occupied33])
            return "Player is occupied (Occupied33).";
        if (Plugin.Condition[ConditionFlag.Occupied38])
            return "Player is occupied (Occupied38).";
        if (Plugin.Condition[ConditionFlag.Occupied39])
            return "Player is occupied (Occupied39).";
        return null;
    }

    /// <summary>
    /// No house, no patches discovered on the player's own plot, then reach — beds share the enclosing
    /// patch's own world position, so patch-centre distance is bed distance and there is no nearer
    /// point on the patch to measure to. Safe to check every frame while a sweep is running: none of
    /// these three change because Gardener's own interaction is in progress.
    /// </summary>
    public static string? EnvironmentBlockingReason()
    {
        if (HouseKey.Current() is null)
            return "Not standing in a housing territory.";

        if (PatchDiscovery.Patches.Count == 0)
        {
            var plot = PatchDiscovery.LastDiagnostics.CurrentPlot;
            return plot is { } p
                ? $"No patches discovered on plot {p + 1}."
                : "No patches discovered here.";
        }

        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        if (position is not { } pos)
            return "Player position unavailable.";

        var nearest = PatchDiscovery.Patches.Min(p => Vector3.Distance(pos, p.Center));
        if (nearest > Plugin.C.BedReachDistance)
            return $"Nearest patch is {nearest:F1}y away (limit {Plugin.C.BedReachDistance:F1}y).";

        return null;
    }

    /// <summary>
    /// A warning (never a block) naming FC rank permissions as the likely cause of an automation
    /// failure, shown only while standing on the character's own FC estate. What
    /// <c>HousingManager.HasHousePermissions()</c> actually covers — furniture placement, gardening,
    /// or both — is unverified, so this never blocks a
    /// run on its own.
    /// </summary>
    public static unsafe string? PermissionsWarning()
    {
        if (HouseKey.Current() is not { OwnedEstateType: EstateType.FreeCompanyEstate })
            return null;

        // SAFETY: HousingManager.Instance() is a static client pointer; HasHousePermissions() is
        // safe to call unconditionally once the instance is non-null.
        var housing = HousingManager.Instance();
        if (housing == null)
            return null;

        return housing->HasHousePermissions()
            ? null
            : "This character may lack FC rank permissions here; gardening actions may silently fail.";
    }
}
