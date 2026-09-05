using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using ECommons;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Gardener.Game;

namespace Gardener.Helpers;

/// <summary>
/// <c>/gardener dump</c> and <c>/gardener dump menu</c>: writes a complete, readable snapshot of
/// discovery and (for <c>dump menu</c>) the currently open bed menu to a text file under the plugin
/// config directory, echoes it to <see cref="Plugin.Logger"/>, uploads it to the dev log server as
/// one artefact, streams it to the dev telemetry log when enabled, and queues it for the clipboard.
/// Every delivery path is independent and a failure in one never loses the file, which is always
/// written first.
/// </summary>
public static class DebugDump
{
    public static string? LastDumpPath { get; private set; }
    public static string? LastDumpText { get; private set; }
    public static string? LastDumpId { get; private set; }

    private static string? pendingClipboard;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static void Run(bool menu)
    {
        var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var text = menu ? BuildMenuDump(id) : BuildDump(id);
        Deliver(menu ? "dump-menu" : "dump", id, text);
    }

    /// <summary>Re-queues the last dump for the clipboard; backs the Garden tab's Copy button.</summary>
    public static void QueueClipboardCopy()
    {
        if (LastDumpText is { } text)
            pendingClipboard = text;
    }

    /// <summary>Drains any queued clipboard text. ImGui's clipboard is only safe to touch on the draw
    /// thread, so the dump command only queues; <see cref="Plugin.DrawUI"/> calls this every frame.</summary>
    public static void DrainClipboard()
    {
        if (pendingClipboard is not { } text)
            return;
        pendingClipboard = null;
        try
        {
            ImGui.SetClipboardText(text);
        }
        catch (Exception ex)
        {
            Plugin.Logger.Warning(ex, "[Gardener] clipboard copy failed");
            Plugin.ChatGui.PrintError(
                $"[Gardener] clipboard copy failed; dump is still saved at {LastDumpPath ?? "(file write also failed, see /xllog)"}");
        }
    }

    private static void Deliver(string kind, string id, string text)
    {
        LastDumpText = text;
        LastDumpId = id;

        string? path = null;
        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{kind}-{id}.txt");
            File.WriteAllText(path, text);
        }
        catch (Exception ex)
        {
            Plugin.Logger.Error(ex, $"[Gardener] {kind} file write failed");
        }
        LastDumpPath = path;

        foreach (var line in text.Split('\n'))
            Plugin.Logger.Information("{Line}", line.TrimEnd('\r'));

        var sentToDevlog = false;
        if (Plugin.Telemetry.Active)
        {
            try
            {
                Plugin.Telemetry.Log($"[dump {id}] ===== BEGIN {kind} =====");
                foreach (var line in text.Split('\n'))
                    Plugin.Telemetry.Log($"[dump {id}] {line.TrimEnd('\r')}");
                Plugin.Telemetry.Log($"[dump {id}] ===== END {kind} =====");
                sentToDevlog = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.Warning(ex, "[Gardener] devlog dump failed");
            }
        }

        QueueClipboardCopy();
        Upload(kind, text);

        var report = string.Join(", ", new[]
        {
            path != null ? $"dump written to {path}" : "dump FILE WRITE FAILED (see /xllog)",
            sentToDevlog ? "sent to devlog" : (Plugin.Telemetry.Active ? "devlog send failed (see /xllog)" : "devlog off"),
            "copied to clipboard",
        });
        Plugin.ChatGui.Print(report);
    }

    /// <summary>
    /// Posts the whole dump to the dev log server's artefact endpoint, so it lands as one file
    /// rather than as several hundred lines interleaved with whatever else is logging. Off-thread
    /// and best-effort: the local file has already been written by the time this runs.
    /// </summary>
    private static void Upload(string kind, string text)
    {
        if (!Plugin.Telemetry.Active) return;
        if (!Uri.TryCreate(Plugin.C.DevLogUrl, UriKind.Absolute, out var logUrl)) return;

        var target = new UriBuilder(logUrl)
        {
            Path = "/file",
            Query = $"plugin=Gardener&name={Uri.EscapeDataString(kind)}&ext=txt",
        }.Uri;

        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(text, Encoding.UTF8, "text/plain");
                using var resp = await Http.PostAsync(target, content).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var landed = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
                Plugin.Logger.Information("[Gardener] dump uploaded to {Path}", landed);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Warning(ex, "[Gardener] dump upload failed");
            }
        });
    }

    private static string BuildDump(string id)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Gardener dump {id}");
        sb.AppendLine();

        var house = HouseKey.Current();
        sb.AppendLine(house is { } h
            ? $"House: {h.KeyString()} (owned={h.Owned}, estate={(h.OwnedEstateType is { } et ? et.ToString() : "n/a")}, " +
              $"ward={h.HouseId.WardIndex}, room={h.HouseId.RoomNumber}, territory={h.HouseId.TerritoryTypeId})"
            : "House: none (not in a housing territory)");

        PatchDiscovery.Refresh();
        var patches = PatchDiscovery.Patches;
        sb.AppendLine();
        AppendDiscoveryDiagnostics(sb, PatchDiscovery.LastDiagnostics);

        sb.AppendLine();
        sb.AppendLine($"Patches: {patches.Count}");
        foreach (var patch in patches)
            AppendPatch(sb, patch);

        sb.AppendLine();
        AppendDataMap(sb, patches);

        return sb.ToString();
    }

    /// <summary>Says why <see cref="PatchDiscovery.Patches"/> is empty when it is empty, so a
    /// zero-patch dump doesn't cost another in-game round trip to diagnose.</summary>
    private static void AppendDiscoveryDiagnostics(StringBuilder sb, DiscoveryDiagnostics diag)
    {
        sb.AppendLine("== Patch discovery diagnostics ==");
        sb.AppendLine($"Furniture array objects walked: {diag.FurnitureArrayWalked}");
        sb.AppendLine($"Object table objects walked: {diag.ObjectTableWalked}");

        sb.AppendLine($"HousingEventObject BaseIds within 30y ({diag.NearbyHousingEventObjectBaseIds.Count} distinct):");
        if (diag.NearbyHousingEventObjectBaseIds.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var (baseId, count) in diag.NearbyHousingEventObjectBaseIds.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  baseId={baseId}: {count}");

        sb.AppendLine($"EventObj DataIds within 30y ({diag.NearbyEventObjDataIds.Count} distinct):");
        if (diag.NearbyEventObjDataIds.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var (dataId, count) in diag.NearbyEventObjDataIds.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  dataId={dataId}: {count}");

        sb.AppendLine($"Beds matching a bed DataId but associated to no patch ({diag.UnassociatedBeds.Count}):");
        if (diag.UnassociatedBeds.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var (dataId, pos) in diag.UnassociatedBeds)
            sb.AppendLine($"  dataId={dataId} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2})");
    }

    private static void AppendPatch(StringBuilder sb, Patch patch)
    {
        sb.AppendLine(
            $"- {patch.Key} kind={patch.Kind} cols={patch.Cols} baseId=131128 dataId=131128 " +
            $"center=({patch.Center.X:F2},{patch.Center.Y:F2},{patch.Center.Z:F2}) rotation={patch.Rotation:F4} " +
            $"housingObjectId={patch.HousingObjectId} furnitureIndex={patch.FurnitureIndex} entityId=0x{patch.EntityId:X8}");
        // patch.Beds is already row-major by construction (PatchDiscovery.Refresh).
        foreach (var bed in patch.Beds)
            AppendBed(sb, bed);
    }

    private static unsafe void AppendBed(StringBuilder sb, Bed bed)
    {
        var obj = Plugin.ObjectTable.SearchByEntityId(bed.EntityId);
        if (obj is null)
        {
            sb.AppendLine($"    [{bed.SpatialIndex}] entityId=0x{bed.EntityId:X8} (no longer in the object table)");
            return;
        }

        // SAFETY: obj is a live IGameObject just looked up from the object table; EventState and
        // EventId are plain fields on the base GameObject struct every entity kind shares, at fixed
        // offsets independent of the object's subtype.
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        sb.AppendLine(
            $"    [{bed.SpatialIndex}] entityId=0x{bed.EntityId:X8} dataId={obj.BaseId} objectKind={obj.ObjectKind} " +
            $"isTargetable={obj.IsTargetable} eventState=0x{go->EventState:X2} eventId={(uint)go->EventId} " +
            $"pos=({bed.Position.X:F2},{bed.Position.Y:F2},{bed.Position.Z:F2}) " +
            $"local=({bed.LocalPosition.X:F2},{bed.LocalPosition.Y:F2},{bed.LocalPosition.Z:F2})");
    }

    private static unsafe void AppendDataMap(StringBuilder sb, IReadOnlyList<Patch> patches)
    {
        sb.AppendLine("== HousingObjectManager.DataMap ==");

        // SAFETY: HousingManager.Instance() is a static client pointer; OutdoorTerritory is only
        // non-null while standing in an outdoor housing territory, which this dump requires.
        var housing = HousingManager.Instance();
        if (housing == null)
        {
            sb.AppendLine("HousingManager.Instance() is null.");
            return;
        }
        if (housing->OutdoorTerritory == null)
        {
            sb.AppendLine("Not in an outdoor housing territory (OutdoorTerritory is null).");
            return;
        }

        var objectManager = &housing->OutdoorTerritory->FurnitureManager.ObjectManager;
        sb.AppendLine($"ObjectArray.ObjectCount = {objectManager->ObjectArray.ObjectCount}");
        sb.AppendLine("Entries:");
        foreach (var kv in objectManager->DataMap)
        {
            sb.AppendLine($"  key={kv.Item1}:");
            var valueSets = kv.Item2.ValueSets;
            for (var i = 0; i < valueSets.Length; i++)
            {
                var vs = valueSets[i];
                sb.AppendLine($"    [{i}] V1=0x{vs.Value1:X4}({vs.Value1}) V2={vs.Value2} V3={vs.Value3} V4={vs.Value4} V5={vs.Value5}{AnnotateSeed(vs.Value1)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Per-patch correlation (DataMap[patch.FurnitureIndex], labelled by spatial bed index):");
        foreach (var patch in patches)
        {
            sb.AppendLine($"  {patch.Key} furnitureIndex={patch.FurnitureIndex}:");
            if (patch.FurnitureIndex < 0)
            {
                sb.AppendLine("    no furniture index recorded");
                continue;
            }

            if (!objectManager->DataMap.TryGetValuePointer((ushort)patch.FurnitureIndex, out var dataPtr) || dataPtr == null)
            {
                sb.AppendLine("    DataMap has no entry for this furniture index");
                continue;
            }

            var valueSets = dataPtr->ValueSets;
            // patch.Beds is already row-major by construction (PatchDiscovery.Refresh).
            var beds = patch.Beds;
            for (var i = 0; i < valueSets.Length; i++)
            {
                var vs = valueSets[i];
                var label = i < beds.Count ? $"bed[{beds[i].SpatialIndex}]" : $"slot[{i}] (no bed)";
                sb.AppendLine($"    {label} V1=0x{vs.Value1:X4}({vs.Value1}) V2={vs.Value2} V3={vs.Value3} V4={vs.Value4} V5={vs.Value5}{AnnotateSeed(vs.Value1)}");
            }
        }
    }

    private static string AnnotateSeed(ushort v1)
    {
        if (v1 == 0)
            return "";
        if (SeedItems.IsOutdoorSeed(v1))
            return $" (matches GardeningSeed row {v1}, seed item {SeedItems.SeedItemForRow(v1)?.ToString() ?? "?"})";
        if (SeedItems.SeedRowForItem(v1) is { } row)
            return $" (matches seed item {v1}, GardeningSeed row {row})";
        return "";
    }

    private static string BuildMenuDump(string id)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Gardener menu dump {id}");
        sb.AppendLine();

        AppendSelectString(sb);
        sb.AppendLine();
        AppendTalk(sb);

        return sb.ToString();
    }

    private static unsafe void AppendSelectString(StringBuilder sb)
    {
        sb.AppendLine("== SelectString ==");
        var select = AddonFinder.SelectString.FirstOrDefault();
        if (select is null || !select.IsAddonReady)
        {
            sb.AppendLine("(none open)");
            return;
        }

        var prompt = select.Text;
        sb.AppendLine($"Prompt: \"{prompt}\" => {GardenMenuText.Classify(prompt)}");
        var bedPatch = GardenMenuText.ParseBedPatch(prompt);
        sb.AppendLine(bedPatch is { } bp ? $"Parsed: bed={bp.Bed} patch={bp.Patch}" : "Parsed: (no two numbers found)");

        sb.AppendLine("Entries:");
        foreach (var entry in select.Entries)
            sb.AppendLine($"  [{entry.Index}] \"{entry.Text}\" => {GardenMenuText.Classify(entry.Text)}");

        sb.AppendLine("AtkValues:");
        var baseAddon = select.Base;
        if (baseAddon == null)
        {
            sb.AppendLine("  (addon pointer is null)");
            return;
        }

        var values = baseAddon->AtkValuesSpan;
        for (var i = 0; i < values.Length; i++)
            sb.AppendLine($"  [{i}] {values[i].Type}: {values[i].GetValueAsString()}");
    }

    private static unsafe void AppendTalk(StringBuilder sb)
    {
        sb.AppendLine("== Talk ==");

        // SAFETY: GetAddonByName<AddonTalk> returns a possibly-null pointer into live addon memory;
        // guarded below before any field is read.
        var talk = Plugin.GameGui.GetAddonByName<AddonTalk>("Talk");
        if (talk == null || !((AtkUnitBase*)talk)->IsVisible)
        {
            sb.AppendLine("(none open)");
            return;
        }

        var speaker = ReadTalkNode(talk->AtkTextNode220);
        var body = ReadTalkNode(talk->AtkTextNode228);
        sb.AppendLine($"Speaker: \"{speaker}\"");
        sb.AppendLine($"Text: \"{body}\" => {GardenMenuText.Classify(body)}");
    }

    private static unsafe string ReadTalkNode(AtkTextNode* node) =>
        node == null ? "" : GenericHelpers.ReadSeString(&node->NodeText).GetText();
}
