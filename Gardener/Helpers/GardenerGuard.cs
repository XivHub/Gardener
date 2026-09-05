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
    public static bool IsScreenReady() =>
        GenericHelpers.IsScreenReady()
        && !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.BetweenAreas51]
        && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene]
        && !Plugin.Condition[ConditionFlag.WatchingCutscene78]
        && !Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]
        && !Plugin.Condition[ConditionFlag.OccupiedSummoningBell]
        && !Plugin.Condition[ConditionFlag.Occupied33]
        && !Plugin.Condition[ConditionFlag.Occupied38]
        && !Plugin.Condition[ConditionFlag.Occupied39];

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
    /// window's button area, or null when nothing is blocking. Checked in order: no house, no
    /// patches discovered on the player's own plot, then reach — beds share the enclosing patch's own
    /// world position (see docs/RESEARCH.md), so patch-centre distance is bed distance and there is no
    /// nearer point on the patch to measure to.
    /// </summary>
    public static string? BlockingReason()
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
