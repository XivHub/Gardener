using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace Gardener.Game;

/// <summary>The three outdoor patch shapes, identified from the associated beds' <c>DataId</c> —
/// the patch furniture itself carries no size discriminator.</summary>
public enum PatchKind
{
    Deluxe,
    Oblong,
    Round,
}

/// <summary>Bed columns per patch shape, used only to derive crossbreed neighbour direction
/// (right/left vs up/down). Beds share the enclosing patch's own world position (see
/// docs/RESEARCH.md), so this has no spatial meaning of its own — it is purely a property of the
/// patch kind.</summary>
public static class PatchKindExtensions
{
    public static int Cols(this PatchKind kind) => kind switch
    {
        PatchKind.Deluxe => 4,
        PatchKind.Oblong => 3,
        PatchKind.Round => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unhandled PatchKind"),
    };
}

/// <summary>
/// One <c>EventObj</c> belonging to a discovered patch. Every bed in a patch reports the patch's own
/// position (proven live: patch-local position is (0,0,0) for all eight), so <see cref="EntityId"/>
/// is the only field that distinguishes one bed from another. Bed identity — which bed a player means
/// by "Nth Bed" — comes from the <c>HousingObjectManager.DataMap</c> slot index, not from this record
/// or its position (see docs/RESEARCH.md); this type exists only to say which <c>EventObj</c>s belong
/// to which patch and to let the debug dump probe their live <c>EventState</c>.
/// </summary>
public sealed record Bed(uint EntityId, Vector3 Position);

/// <summary>A discovered outdoor garden patch and its beds.</summary>
public sealed record Patch(
    string Key,
    PatchKind Kind,
    int Cols,
    Vector3 Center,
    float Rotation,
    short FurnitureIndex,
    uint EntityId,
    uint HousingObjectId,
    IReadOnlyList<Bed> Beds);

/// <summary>Ties a discovered <see cref="Game.Patch"/> to the plot it was attributed to, and whether
/// that plot is the one the player is standing on. Carries the rejected patches too, not only the
/// kept ones, so a wrong attribution is visible in the dump instead of silently vanishing.</summary>
public readonly record struct PatchAttribution(Patch Patch, int? PlotIndex, float? DistanceToMarker, bool Kept);

/// <summary>
/// Counters from the most recent <see cref="PatchDiscovery.Refresh"/>, kept so a zero-patch result
/// can say why it is zero without another round trip into the game.
/// </summary>
public readonly record struct DiscoveryDiagnostics(
    int FurnitureArrayWalked,
    int ObjectTableWalked,
    IReadOnlyDictionary<uint, int> NearbyHousingEventObjectBaseIds,
    IReadOnlyDictionary<uint, int> NearbyEventObjDataIds,
    IReadOnlyList<(uint DataId, Vector3 Position)> UnassociatedBeds,
    sbyte? CurrentPlot,
    bool CurrentPlotOwned,
    IReadOnlyList<PatchAttribution> AttributedPatches)
{
    public static readonly DiscoveryDiagnostics Empty = new(
        0, 0, new Dictionary<uint, int>(), new Dictionary<uint, int>(), Array.Empty<(uint, Vector3)>(),
        null, false, Array.Empty<PatchAttribution>());
}

/// <summary>
/// Finds outdoor garden patches and their beds, associates beds to the nearest patch within 5 yalms,
/// and keeps only the patches that sit on the plot the player is standing on. Refreshes on a timer
/// (<see cref="Tick"/>) rather than every frame; <see cref="Refresh"/> is available for on-demand
/// callers such as the debug dump.
/// </summary>
public static class PatchDiscovery
{
    private const uint PatchBaseId = 131128;
    private const uint DeluxeBedDataId = 2003757;
    private const uint OblongBedDataId = 2003756;
    private const uint RoundBedDataId = 2003755;

    // DailyRoutines' rule: a bed belongs to the nearest patch within this many yalms.
    private const float BedAssociationRadius = 5f;

    // Radius for the "what's actually out here" diagnostic breakdowns, wide enough to catch a
    // whole yard from wherever the player is standing in it.
    private const float DiagnosticRadius = 30f;

    // HousingManager.GetCurrentPlot() sentinels for standing in an apartment building rather than
    // on a numbered land plot.
    private const sbyte ApartmentMainDivisionPlot = -128;
    private const sbyte ApartmentSubdivisionPlot = -127;

    // Every housing ward has exactly 60 numbered plots. OutdoorTerritory._housingMapMarkerInfos
    // holds 62 entries — 60 plot markers followed by 2 apartment-building markers, matching
    // _apartmentBuildings's own count of 2 (main and sub division). That cardinality match is the
    // basis for treating marker index == plot index below; it has not yet been confirmed by a live
    // capture of marker positions against known plot numbers (see docs/RESEARCH.md).
    private const int PlotsPerWard = 60;

    private const long RefreshIntervalMs = 2000;
    private static long lastRefreshTick;

    public static IReadOnlyList<Patch> Patches { get; private set; } = Array.Empty<Patch>();

    public static DiscoveryDiagnostics LastDiagnostics { get; private set; } = DiscoveryDiagnostics.Empty;

    /// <summary>Call once per frame; actually rescans at most once every <see cref="RefreshIntervalMs"/>.</summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now - lastRefreshTick < RefreshIntervalMs)
            return;
        lastRefreshTick = now;
        Refresh();
    }

    /// <summary>Rescans immediately, bypassing the timer.</summary>
    public static unsafe void Refresh()
    {
        var house = HouseKey.Current();
        if (house is not { } houseKey)
        {
            Patches = Array.Empty<Patch>();
            LastDiagnostics = DiscoveryDiagnostics.Empty;
            return;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position;

        var (patchObjects, furnitureWalked, nearbyPatchBaseIds) = FindPatchObjects(playerPosition);

        var bedObjects = new List<IGameObject>();
        var objectTableWalked = 0;
        var nearbyBedDataIds = new Dictionary<uint, int>();
        foreach (var obj in Plugin.ObjectTable)
        {
            objectTableWalked++;
            if (obj.ObjectKind != ObjectKind.EventObj)
                continue;

            if (obj.BaseId == DeluxeBedDataId || obj.BaseId == OblongBedDataId || obj.BaseId == RoundBedDataId)
                bedObjects.Add(obj);

            if (playerPosition is { } pp && Vector3.Distance(obj.Position, pp) <= DiagnosticRadius)
                nearbyBedDataIds[obj.BaseId] = nearbyBedDataIds.GetValueOrDefault(obj.BaseId) + 1;
        }

        var unassociatedBeds = bedObjects
            .Where(b => !patchObjects.Any(p => Vector3.Distance(b.Position, p.Position) <= BedAssociationRadius))
            .Select(b => (b.BaseId, b.Position))
            .ToList();

        var builtPatches = new List<Patch>();
        foreach (var patchObj in patchObjects)
        {
            var associated = bedObjects
                .Where(b => Vector3.Distance(b.Position, patchObj.Position) <= BedAssociationRadius)
                .ToList();
            if (associated.Count == 0)
            {
                Plugin.Logger.Debug($"[PatchDiscovery] patch at {patchObj.Position} has no beds within {BedAssociationRadius}y; skipping");
                continue;
            }

            var dataIds = associated.Select(b => b.BaseId).Distinct().ToList();
            if (dataIds.Count != 1)
            {
                Plugin.Logger.Warning(
                    $"[PatchDiscovery] patch at {patchObj.Position} has beds with mixed DataIds " +
                    $"({string.Join(",", dataIds)}); skipping rather than guessing a shape");
                continue;
            }

            PatchKind kind;
            int expectedCount;
            switch (dataIds[0])
            {
                case DeluxeBedDataId: kind = PatchKind.Deluxe; expectedCount = 8; break;
                case OblongBedDataId: kind = PatchKind.Oblong; expectedCount = 6; break;
                case RoundBedDataId: kind = PatchKind.Round; expectedCount = 4; break;
                default:
                    Plugin.Logger.Warning($"[PatchDiscovery] patch at {patchObj.Position} has beds with unrecognised DataId {dataIds[0]}; skipping");
                    continue;
            }

            if (associated.Count != expectedCount)
            {
                Plugin.Logger.Warning(
                    $"[PatchDiscovery] patch at {patchObj.Position} looks like {kind} ({associated.Count} beds) but " +
                    $"expected {expectedCount}; skipping rather than guessing a shape");
                continue;
            }

            // Sorted by EntityId only for a stable, reproducible list order (and because it is the
            // unverified candidate bed ordering under investigation — see docs/RESEARCH.md and
            // DebugDump's target capture). It is not itself the bed's identity.
            var beds = associated
                .OrderBy(b => b.EntityId)
                .Select(b => new Bed(b.EntityId, b.Position))
                .ToList();

            var key = $"{houseKey.KeyString()}:{patchObj.Position.X:F1}:{patchObj.Position.Z:F1}";
            builtPatches.Add(new Patch(
                key, kind, kind.Cols(), patchObj.Position, patchObj.Rotation,
                patchObj.FurnitureIndex, patchObj.EntityId, patchObj.HousingObjectId, beds));
        }

        var currentPlot = CurrentPlotOrNull();
        var ownsCurrentPlot = houseKey.Owned;

        var kept = new List<Patch>();
        var attributions = new List<PatchAttribution>();
        foreach (var patch in builtPatches)
        {
            var (plotIndex, distance) = AttributeToPlot(patch.Center);
            var isKept = ownsCurrentPlot && currentPlot is { } cp && plotIndex == cp;
            attributions.Add(new PatchAttribution(patch, plotIndex, distance, isKept));
            if (isKept)
                kept.Add(patch);
        }

        LastDiagnostics = new DiscoveryDiagnostics(
            furnitureWalked, objectTableWalked, nearbyPatchBaseIds, nearbyBedDataIds, unassociatedBeds,
            currentPlot, ownsCurrentPlot, attributions);

        Patches = kept;
    }

    /// <summary>The plot the player is standing on, or null while in an apartment (no numbered plot)
    /// or when the housing manager is unavailable.</summary>
    private static unsafe sbyte? CurrentPlotOrNull()
    {
        // SAFETY: HousingManager.Instance() is a static client pointer; GetCurrentPlot() is safe to
        // call unconditionally once the instance is non-null.
        var housing = HousingManager.Instance();
        if (housing == null)
            return null;

        var plot = housing->GetCurrentPlot();
        return plot is ApartmentMainDivisionPlot or ApartmentSubdivisionPlot ? null : plot;
    }

    /// <summary>
    /// Attributes a patch to the plot whose map marker is nearest to it. Only the first
    /// <see cref="PlotsPerWard"/> markers are considered, since the remaining two are the
    /// apartment-building markers, not plots a garden patch can sit on.
    /// </summary>
    private static unsafe (int? PlotIndex, float? Distance) AttributeToPlot(Vector3 patchPosition)
    {
        // SAFETY: HousingManager.Instance() is a static client pointer; OutdoorTerritory is only
        // non-null while standing in an outdoor housing territory, checked before the marker array
        // is touched.
        var housing = HousingManager.Instance();
        if (housing == null || housing->OutdoorTerritory == null)
            return (null, null);

        var markers = housing->OutdoorTerritory->HousingMapMarkerInfos;
        var plotMarkerCount = Math.Min(PlotsPerWard, markers.Length);

        var best = -1;
        var bestDistSq = float.MaxValue;
        for (var i = 0; i < plotMarkerCount; i++)
        {
            var marker = markers[i];
            var distSq = Vector3.DistanceSquared(patchPosition, new Vector3(marker.X, marker.Y, marker.Z));
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = i;
            }
        }

        return best < 0 ? (null, null) : (best, MathF.Sqrt(bestDistSq));
    }

    /// <summary>A <c>HousingEventObject</c> read straight from the furniture object array.</summary>
    private readonly record struct PatchObject(uint EntityId, Vector3 Position, float Rotation, uint HousingObjectId, short FurnitureIndex);

    /// <summary>
    /// Finds outdoor patch furniture (<c>HousingEventObject</c>, <c>BaseId</c> 131128) by walking
    /// <c>HousingManager.OutdoorTerritory.FurnitureManager.ObjectManager.ObjectArray</c> directly,
    /// the same route DailyRoutines' <c>AutoGardensWork</c> uses. Housing furniture is not reliably
    /// present in Dalamud's <see cref="Plugin.ObjectTable"/> — that gap is what left
    /// <see cref="Patches"/> empty despite a populated <c>DataMap</c>.
    /// </summary>
    private static unsafe (List<PatchObject> Patches, int Walked, Dictionary<uint, int> NearbyBaseIdCounts) FindPatchObjects(Vector3? playerPosition)
    {
        var patches = new List<PatchObject>();
        var nearbyBaseIdCounts = new Dictionary<uint, int>();

        // SAFETY: HousingManager.Instance() is a static client pointer; OutdoorTerritory is only
        // non-null while standing in an outdoor housing territory, checked before the furniture
        // array is touched.
        var housing = HousingManager.Instance();
        if (housing == null || housing->OutdoorTerritory == null)
            return (patches, 0, nearbyBaseIdCounts);

        var objectManager = &housing->OutdoorTerritory->FurnitureManager.ObjectManager;
        var objects = objectManager->ObjectArray.Objects;
        var walked = Math.Min((int)objectManager->ObjectArray.ObjectCount, objects.Length);

        for (var i = 0; i < walked; i++)
        {
            var obj = objects[i].Value;
            if (obj == null)
                continue;
            if ((ObjectKind)obj->ObjectKind != ObjectKind.HousingEventObject)
                continue;

            if (playerPosition is { } pp && Vector3.Distance(obj->Position, pp) <= DiagnosticRadius)
                nearbyBaseIdCounts[obj->BaseId] = nearbyBaseIdCounts.GetValueOrDefault(obj->BaseId) + 1;

            if (obj->BaseId != PatchBaseId)
                continue;

            // SAFETY: HousingEventObject is HousingObject at offset 0 (HousingEventObject :
            // HousingObject : GameObject), matched by ObjectKind above, so reinterpreting the
            // pointer reaches HousingFurnitureIndex and HousingObjectId safely.
            var housingObj = (HousingObject*)obj;
            patches.Add(new PatchObject(obj->EntityId, obj->Position, obj->Rotation, housingObj->HousingObjectId.Id, housingObj->HousingFurnitureIndex));
        }

        return (patches, walked, nearbyBaseIdCounts);
    }

    /// <summary>The live <c>GameObject.EventState</c> byte for a bed, if it is still in the object
    /// table. Shared by the debug dump and the Garden tab so neither re-derives the read.</summary>
    public static unsafe byte? EventStateFor(uint entityId)
    {
        var obj = Plugin.ObjectTable.SearchByEntityId(entityId);
        if (obj is null)
            return null;

        // SAFETY: obj was just looked up live from the object table; EventState is a plain byte
        // field on the base GameObject struct that every entity kind shares, at a fixed offset
        // independent of the object's subtype.
        return ((GameObject*)obj.Address)->EventState;
    }
}
