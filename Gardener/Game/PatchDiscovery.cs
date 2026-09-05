using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
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
/// Finds outdoor garden patches and their beds from the object table, associates beds to the nearest
/// patch within 5 yalms, and orders each patch's beds into a stable row-major spatial index. Refreshes
/// on a timer (<see cref="Tick"/>) rather than every frame; <see cref="Refresh"/> is available for
/// on-demand callers such as the debug dump.
/// </summary>
public static class PatchDiscovery
{
    private const uint PatchBaseId = 131128;
    private const uint DeluxeBedDataId = 2003757;
    private const uint OblongBedDataId = 2003756;
    private const uint RoundBedDataId = 2003755;

    // DailyRoutines' rule: a bed belongs to the nearest patch within this many yalms.
    private const float BedAssociationRadius = 5f;

    private const long RefreshIntervalMs = 2000;
    private static long lastRefreshTick;

    public static IReadOnlyList<Patch> Patches { get; private set; } = Array.Empty<Patch>();

    /// <summary>Call once per frame; actually rescans at most once every <see cref="RefreshIntervalMs"/>.</summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now - lastRefreshTick < RefreshIntervalMs)
            return;
        lastRefreshTick = now;
        Refresh();
    }

    /// <summary>Rescans the object table immediately, bypassing the timer.</summary>
    public static void Refresh()
    {
        var house = HouseKey.Current();
        if (house is not { } houseKey)
        {
            Patches = Array.Empty<Patch>();
            return;
        }

        var patchObjects = new List<IGameObject>();
        var bedObjects = new List<IGameObject>();

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind == ObjectKind.HousingEventObject && obj.BaseId == PatchBaseId)
                patchObjects.Add(obj);
            else if (obj.ObjectKind == ObjectKind.EventObj &&
                     (obj.BaseId == DeluxeBedDataId || obj.BaseId == OblongBedDataId || obj.BaseId == RoundBedDataId))
                bedObjects.Add(obj);
        }

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

            var (housingObjectId, furnitureIndex) = ReadHousingIdentity(patchObj);
            var key = $"{houseKey.KeyString()}:{patchObj.Position.X:F1}:{patchObj.Position.Z:F1}";
            result.Add(new Patch(key, kind, cols, patchObj.Position, patchObj.Rotation, furnitureIndex, patchObj.EntityId, housingObjectId, beds));
        }

        Patches = result;
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

    private static unsafe (uint HousingObjectId, short FurnitureIndex) ReadHousingIdentity(IGameObject patchObj)
    {
        // SAFETY: patchObj was matched as ObjectKind.HousingEventObject, which is HousingObject at
        // offset 0 (HousingEventObject : HousingObject : GameObject), so reinterpreting its address
        // is safe and reaches the furniture-identity fields IGameObject does not expose.
        var housing = (HousingObject*)patchObj.Address;
        return (housing->HousingObjectId.Id, housing->HousingFurnitureIndex);
    }

    private static Vector3 RotateAroundY(Vector3 v, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector3(v.X * cos - v.Z * sin, v.Y, v.X * sin + v.Z * cos);
    }
}
