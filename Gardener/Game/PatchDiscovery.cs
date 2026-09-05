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

/// <summary>
/// One bed's position inside a discovered patch. <see cref="SpatialIndex"/> orders beds row-major
/// (row 0 then row 1, each sorted west-to-east in patch-local space) so the crossbreed planner has a
/// consistent right/down/up/left direction to work from; it is not the game's own "Nth Bed" number
/// (see <see cref="GardenMenuText.ParseBedPatch"/> for that authoritative identity).
/// </summary>
public sealed record Bed(int SpatialIndex, uint EntityId, Vector3 Position, Vector3 LocalPosition);

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

/// <summary>
/// Counters from the most recent <see cref="PatchDiscovery.Refresh"/>, kept so a zero-patch result
/// can say why it is zero without another round trip into the game.
/// </summary>
public readonly record struct DiscoveryDiagnostics(
    int FurnitureArrayWalked,
    int ObjectTableWalked,
    IReadOnlyDictionary<uint, int> NearbyHousingEventObjectBaseIds,
    IReadOnlyDictionary<uint, int> NearbyEventObjDataIds,
    IReadOnlyList<(uint DataId, Vector3 Position)> UnassociatedBeds)
{
    public static readonly DiscoveryDiagnostics Empty = new(
        0, 0, new Dictionary<uint, int>(), new Dictionary<uint, int>(), Array.Empty<(uint, Vector3)>());
}

/// <summary>
/// Finds outdoor garden patches and their beds, associates beds to the nearest patch within 5 yalms,
/// and orders each patch's beds into a stable row-major spatial index. Refreshes on a timer
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
    public static void Refresh()
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

        LastDiagnostics = new DiscoveryDiagnostics(furnitureWalked, objectTableWalked, nearbyPatchBaseIds, nearbyBedDataIds, unassociatedBeds);

        var result = new List<Patch>();
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
            int expectedCount, cols;
            switch (dataIds[0])
            {
                case DeluxeBedDataId: kind = PatchKind.Deluxe; expectedCount = 8; cols = 4; break;
                case OblongBedDataId: kind = PatchKind.Oblong; expectedCount = 6; cols = 3; break;
                case RoundBedDataId: kind = PatchKind.Round; expectedCount = 4; cols = 2; break;
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

            var rotated = associated
                .Select(b => (Obj: b, Local: RotateAroundY(b.Position - patchObj.Position, -patchObj.Rotation)))
                .OrderBy(x => x.Local.Z)
                .ToList();

            var beds = new List<Bed>();
            for (var row = 0; row < 2; row++)
            {
                var rowBeds = rotated.Skip(row * cols).Take(cols).OrderBy(x => x.Local.X).ToList();
                for (var col = 0; col < rowBeds.Count; col++)
                {
                    var (obj, local) = rowBeds[col];
                    beds.Add(new Bed(row * cols + col, obj.EntityId, obj.Position, local));
                }
            }

            var key = $"{houseKey.KeyString()}:{patchObj.Position.X:F1}:{patchObj.Position.Z:F1}";
            result.Add(new Patch(
                key, kind, cols, patchObj.Position, patchObj.Rotation,
                patchObj.FurnitureIndex, patchObj.EntityId, patchObj.HousingObjectId, beds));
        }

        Patches = result;
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

    private static Vector3 RotateAroundY(Vector3 v, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector3(v.X * cos - v.Z * sin, v.Y, v.X * sin + v.Z * cos);
    }
}
