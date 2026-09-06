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
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Gardener.Game;
using Gardener.Journal;

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
        AppendPlotAttribution(sb, PatchDiscovery.LastDiagnostics);

        sb.AppendLine();
        sb.AppendLine($"Patches (own plot only): {patches.Count}");
        foreach (var patch in patches)
            AppendPatch(sb, patch);

        sb.AppendLine();
        AppendDataMap(sb, patches);

        return sb.ToString();
    }

    /// <summary>Shows which plot each discovered patch was attributed to and whether it was kept
    /// (player's own plot) or rejected, plus the raw marker/plot table so a wrong attribution above
    /// can be cross-checked by hand against the in-game map's plot numbers.</summary>
    private static unsafe void AppendPlotAttribution(StringBuilder sb, DiscoveryDiagnostics diag)
    {
        sb.AppendLine("== Plot attribution ==");
        sb.AppendLine(diag.CurrentPlot is { } p
            ? $"Current plot: plot {p + 1} (index {p}) (owned={diag.CurrentPlotOwned})"
            : $"Current plot: none (apartment or unresolved) (owned={diag.CurrentPlotOwned})");

        sb.AppendLine("Per-patch attribution:");
        if (diag.AttributedPatches.Count == 0)
            sb.AppendLine("  (no patches discovered)");
        foreach (var a in diag.AttributedPatches)
        {
            var plotText = a.PlotIndex is { } pi ? $"plot {pi + 1} (index {pi})" : "unresolved";
            var distText = a.DistanceToMarker is { } d ? $"{d:F2}y" : "n/a";
            sb.AppendLine($"  {a.Patch.Key}: nearest plot marker={plotText} distance={distText} " +
                          $"=> {(a.Kept ? "KEPT (player's own plot)" : "REJECTED")}");
        }

        // SAFETY: HousingManager.Instance() is a static client pointer; OutdoorTerritory is only
        // non-null while standing in an outdoor housing territory.
        var housing = HousingManager.Instance();
        if (housing == null || housing->OutdoorTerritory == null)
        {
            sb.AppendLine("Markers/plots: unavailable (not in an outdoor housing territory)");
            return;
        }

        // Marker index == plot index is inferred from array cardinality (62 markers = 60 plots + 2
        // apartment-building icons, matching _apartmentBuildings's own count of 2), not yet
        // confirmed by a live capture of marker positions against known plot numbers.
        var markers = housing->OutdoorTerritory->HousingMapMarkerInfos;
        var plots = housing->OutdoorTerritory->Plots;
        sb.AppendLine($"Markers ({markers.Length}) / Plots ({plots.Length}):");
        for (var i = 0; i < markers.Length; i++)
        {
            var m = markers[i];
            var plotText = i < plots.Length
                ? $"plot {i + 1} (index {i}) state={plots[i].State} size={plots[i].Size} owner={plots[i].OwnerType}"
                : $"index {i} (apartment building marker)";
            sb.AppendLine($"  marker=({m.X:F2},{m.Y:F2},{m.Z:F2}) {plotText}");
        }
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
            $"- {patch.Key} kind={patch.Kind} baseId=131128 dataId=131128 " +
            $"center=({patch.Center.X:F2},{patch.Center.Y:F2},{patch.Center.Z:F2}) rotation={patch.Rotation:F4} " +
            $"housingObjectId={patch.HousingObjectId} furnitureIndex={patch.FurnitureIndex} entityId=0x{patch.EntityId:X8}");
        sb.AppendLine($"  layout: {DescribeLayout(patch.Kind)}");
        sb.AppendLine($"  journal: {DescribePatchRecord(patch.Key)}");
        // patch.Beds is sorted by EntityId (PatchDiscovery.Refresh); the bracketed index below is
        // that array position, not the game's own "Nth Bed" number.
        for (var i = 0; i < patch.Beds.Count; i++)
            AppendBed(sb, i, patch.Beds[i]);
    }

    /// <summary>The persisted <see cref="PatchRecord"/> for this patch key, if any — the raw key,
    /// kind, plot and ordinal the UI's <see cref="Gardener.Windows.PatchLabel"/> is never allowed to
    /// print, kept alive here as the one place they still do.</summary>
    private static string DescribePatchRecord(string patchKey)
    {
        var record = GardenJournal.PatchInfo(patchKey);
        if (record is null)
            return "no PatchRecord (never seen since this field was added)";

        var plotText = record.PlotIndex is { } p ? $"plot {p + 1} (index {p})" : "plot unknown";
        return $"patchKey={record.PatchKey} houseKey={record.HouseKey} kind={record.Kind} " +
               $"bedCount={record.BedCount} {plotText} ordinal={record.Ordinal} lastSeenAt={record.LastSeenAt:O}";
    }

    /// <summary>Bed-number adjacency, straight from <see cref="PatchKindExtensions.Neighbours"/> so the
    /// dump can never drift from what a crossbreed walk would actually use.</summary>
    private static string DescribeLayout(PatchKind kind)
    {
        if (kind != PatchKind.Deluxe)
            return "not confirmed; Oblong and Round have no adjacency model";

        var perBed = Enumerable.Range(1, kind.BedCount())
            .Select(bed => $"{bed}:[{string.Join(",", kind.Neighbours(bed))}]");
        return $"8-bed ring, clockwise from top-left, wraps (8-1). neighbours {string.Join(" ", perBed)}";
    }

    private static unsafe void AppendBed(StringBuilder sb, int index, Bed bed)
    {
        var obj = Plugin.ObjectTable.SearchByEntityId(bed.EntityId);
        if (obj is null)
        {
            sb.AppendLine($"    [{index}] entityId=0x{bed.EntityId:X8} (no longer in the object table)");
            return;
        }

        // SAFETY: obj is a live IGameObject just looked up from the object table; EventState and
        // EventId are plain fields on the base GameObject struct every entity kind shares, at fixed
        // offsets independent of the object's subtype.
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        sb.AppendLine(
            $"    [{index}] entityId=0x{bed.EntityId:X8} dataId={obj.BaseId} objectKind={obj.ObjectKind} " +
            $"isTargetable={obj.IsTargetable} eventState=0x{go->EventState:X2} eventId={(uint)go->EventId} " +
            $"pos=({bed.Position.X:F2},{bed.Position.Y:F2},{bed.Position.Z:F2})");
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
        sb.AppendLine(
            "Per-patch correlation (DataMap[patch.FurnitureIndex]; slot index N is bed \"(N+1)th Bed\"; " +
            "proven live. The DataMap has no link to the patch's EventObj beds; " +
            "which EventObj is which bed is a separate, still-unverified question, see the menu dump):");
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
            for (var i = 0; i < valueSets.Length; i++)
            {
                var vs = valueSets[i];
                sb.AppendLine($"    bed {i + 1} (slot {i}) V1=0x{vs.Value1:X4}({vs.Value1}) V2={vs.Value2} V3={vs.Value3} V4={vs.Value4} V5={vs.Value5}{AnnotateSeed(vs.Value1)}");
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
        AppendHousingGardening(sb);
        sb.AppendLine();
        AppendAgentHousingPlant(sb);
        sb.AppendLine();
        AppendContextMenu(sb);
        sb.AppendLine();
        AppendTarget(sb);
        sb.AppendLine();
        AppendTalk(sb);
        sb.AppendLine();
        AppendCropChat(sb);

        return sb.ToString();
    }

    /// <summary>The last <see cref="CropChatState"/> ring buffer entries — the five <c>TALK_*</c>
    /// sentences a bed interaction echoes into chat, the only source for wilted and true
    /// harvest-readiness the bed menu itself never offers.</summary>
    private static void AppendCropChat(StringBuilder sb)
    {
        sb.AppendLine("== Crop chat ==");

        // The sentence the sheet says to look for, next to whatever actually arrived. An empty
        // observed list with a populated unclassified list means the text does not match the sheet;
        // both empty means the sentence never reached chat at all.
        sb.AppendLine($"GardenMenuText.Available={GardenMenuText.Available}");
        foreach (var key in new[]
                 {
                     MenuKey.TalkNone, MenuKey.TalkVigorous, MenuKey.TalkDepressed,
                     MenuKey.TalkRipe, MenuKey.TalkDead,
                 })
        {
            sb.AppendLine($"  sheet {key}: \"{GardenMenuText.TextFor(key) ?? "(missing)"}\"");
        }

        var lines = CropChatState.RecentLines;
        sb.AppendLine($"Observed ({lines.Count}):");
        if (lines.Count == 0)
            sb.AppendLine("  (none observed yet)");
        foreach (var line in lines)
            sb.AppendLine($"  {line.At:O} [{line.ChatType}] {line.Key} \"{line.Text}\"");

        var unclassified = CropChatState.Unclassified;
        sb.AppendLine($"Unclassified during the last sweep ({unclassified.Count}):");
        if (unclassified.Count == 0)
            sb.AppendLine("  (none)");
        foreach (var line in unclassified)
            sb.AppendLine($"  {line.At:O} [{line.ChatType}] \"{line.Text}\"");
    }

    /// <summary>
    /// Records the current target's <c>EntityId</c> alongside the sorted <c>EntityId</c> list of its
    /// enclosing patch's beds and the index the target holds in that list. Sorted-EntityId order is
    /// the candidate for the game's own bed numbering, unconfirmed; this
    /// capture is what two dumps at known different beds can use to prove or refute it. Record and
    /// report only — nothing here drives targeting.
    /// </summary>
    private static void AppendTarget(StringBuilder sb)
    {
        sb.AppendLine("== Target ==");
        var target = Plugin.TargetManager.Target;
        if (target is null)
        {
            sb.AppendLine("(no target)");
            return;
        }

        sb.AppendLine(
            $"EntityId: 0x{target.EntityId:X8} dataId={target.BaseId} objectKind={target.ObjectKind} " +
            $"pos=({target.Position.X:F2},{target.Position.Y:F2},{target.Position.Z:F2})");

        PatchDiscovery.Refresh();
        var patch = PatchDiscovery.Patches.FirstOrDefault(p => p.Beds.Any(b => b.EntityId == target.EntityId));
        if (patch is null)
        {
            sb.AppendLine("Enclosing patch: not found among discovered patches (target may be on a plot " +
                          "that was filtered out, or is not a bed)");
            return;
        }

        sb.AppendLine($"Enclosing patch: {patch.Key} furnitureIndex={patch.FurnitureIndex}");
        var sortedIds = patch.Beds.Select(b => b.EntityId).OrderBy(id => id).ToList();
        sb.AppendLine($"Sorted bed EntityIds ({sortedIds.Count}): {string.Join(", ", sortedIds.Select(id => $"0x{id:X8}"))}");
        var index = sortedIds.IndexOf(target.EntityId);
        sb.AppendLine(index >= 0
            ? $"Target's index in sorted list: {index}"
            : "Target's index in sorted list: not found");
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
        var entryKeys = new List<MenuKey>();
        foreach (var entry in select.Entries)
        {
            var key = GardenMenuText.Classify(entry.Text);
            entryKeys.Add(key);
            sb.AppendLine($"  [{entry.Index}] \"{entry.Text}\" => {key}");
        }

        // The menu offers SetSeed only on an empty bed and Harvest only on a ripe one; every other
        // entry set (Fertilize/Tend/Remove/Quit) means something is growing.
        var bedState = entryKeys.Contains(MenuKey.SetSeed) ? "empty (SetSeed entry present)"
            : entryKeys.Contains(MenuKey.Harvest) ? "ripe (Harvest entry present)"
            : "growing (no SetSeed or Harvest entry)";
        sb.AppendLine($"Bed state: {bedState}");

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

    /// <summary>
    /// The seed/soil dialog opened by "Plant Seeds". No <c>AddonHousingGardening</c> struct exists in
    /// FFXIVClientStructs (only its resolved address in <c>ida/data.yml</c>), so it is read the same
    /// way <see cref="AppendTalk"/> reads <c>AddonTalk</c> before typed fields exist for an addon:
    /// through the generic <c>AtkUnitBase</c> layout every addon shares.
    /// </summary>
    private static unsafe void AppendHousingGardening(StringBuilder sb)
    {
        sb.AppendLine("== HousingGardening ==");

        // SAFETY: GetAddonByName<AtkUnitBase> returns a possibly-null pointer into live addon
        // memory; guarded below before any field is read.
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>("HousingGardening");
        if (addon == null || !addon->IsVisible)
        {
            sb.AppendLine("(none open)");
            return;
        }

        sb.AppendLine("AtkValues:");
        var values = addon->AtkValuesSpan;
        for (var i = 0; i < values.Length; i++)
            sb.AppendLine($"  [{i}] {values[i].Type}: {values[i].GetValueAsString()}");

        sb.AppendLine("Visible text nodes:");
        var nodes = addon->UldManager.Nodes;
        var any = false;
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i].Value;
            if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                continue;

            var textNode = (AtkTextNode*)node;
            var text = GenericHelpers.ReadSeString(&textNode->NodeText).GetText();
            sb.AppendLine($"  nodeId={node->NodeId} \"{text}\"");
            any = true;
        }
        if (!any)
            sb.AppendLine("  (none)");
    }

    /// <summary>
    /// <c>AgentHousingPlant</c> drives the seed/soil dialog: <c>SelectedItems[0]</c> is soil,
    /// <c>[1]</c> is seed, and <c>ConfirmSeedAndSoilSelection()</c> submits them. <c>SelectedItems2</c>
    /// is a second, separately-offset array of the same element type whose role is not yet known —
    /// dumped alongside <c>SelectedItems</c> so a live capture can show whether it mirrors it or
    /// carries something else.
    /// </summary>
    private static unsafe void AppendAgentHousingPlant(StringBuilder sb)
    {
        sb.AppendLine("== AgentHousingPlant ==");

        // SAFETY: AgentModule.Instance() and GetAgentByInternalId return possibly-null pointers into
        // live agent memory; guarded below before any field is read. AgentHousingPlant is reached by
        // reinterpreting the AgentInterface* the module returns for AgentId.HousingPlant, matched by
        // that id rather than assumed from call order.
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentHousingPlant*)agentModule->GetAgentByInternalId(AgentId.HousingPlant);
        if (agent == null)
        {
            sb.AppendLine("(unavailable)");
            return;
        }

        sb.AppendLine(
            $"PlotType={agent->PlotType} ContextAddonId={agent->ContextAddonId} " +
            $"SelectableItemCount={agent->SelectableItemCount}");

        sb.AppendLine("SelectedItems (soil, seed):");
        AppendSelectedItems(sb, agent->SelectedItems);

        sb.AppendLine("SelectedItems2 (offset 0x68, role unknown):");
        AppendSelectedItems(sb, agent->SelectedItems2);

        // Both the plant and the fertilize capture recorded SelectableItemCount == 0 throughout —
        // fertilizing opens no dialog for this list to populate, see the Context menu section below.
        // Dumped anyway: it is part of AgentHousingPlant's live state either way.
        sb.AppendLine($"SelectableItems (first {agent->SelectableItemCount}):");
        AppendSelectableItems(sb, agent->SelectableItems, agent->SelectableItemCount);
    }

    /// <summary>
    /// The generic item context menu <c>AgentInventoryContext.OpenForItemSlot</c> opens: fertilizing
    /// drives an item's own context menu rather than a dialog, so this is what a live capture needs to
    /// confirm which entry the automation should select and that the menu opened at all.
    /// <c>AgentHousingPlant.PlotType</c> is recorded alongside it because 14 (planting) and 15
    /// (fertilizing) are the only signal that either mode is active once the bed's own
    /// <c>SelectString</c> has already closed.
    /// </summary>
    private static unsafe void AppendContextMenu(StringBuilder sb)
    {
        sb.AppendLine("== Context menu ==");

        // SAFETY: AgentModule.Instance() and GetAgentByInternalId return possibly-null pointers into
        // live agent memory; guarded below before any field is read.
        var agentModule = AgentModule.Instance();
        var housingPlant = agentModule == null ? null : (AgentHousingPlant*)agentModule->GetAgentByInternalId(AgentId.HousingPlant);
        sb.AppendLine(housingPlant == null
            ? "AgentHousingPlant.PlotType: (unavailable)"
            : $"AgentHousingPlant.PlotType: {housingPlant->PlotType}");

        // SAFETY: GetAddonByName<AtkUnitBase> returns a possibly-null pointer into live addon memory;
        // guarded below before any field is read.
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>("ContextMenu");
        if (addon == null || !addon->IsVisible)
        {
            sb.AppendLine("Open: no");
            return;
        }

        sb.AppendLine("Open: yes");

        var contextMenu = new AddonMaster.ContextMenu(addon);
        sb.AppendLine("Entries:");
        foreach (var entry in contextMenu.Entries)
            sb.AppendLine($"  [{entry.Index}] \"{entry.Text}\" => {GardenMenuText.Classify(entry.Text)} enabled={entry.Enabled}");

        sb.AppendLine("AtkValues:");
        var values = addon->AtkValuesSpan;
        for (var i = 0; i < values.Length; i++)
            sb.AppendLine($"  [{i}] {values[i].Type}: {values[i].GetValueAsString()}");
    }

    private static unsafe void AppendSelectedItems(StringBuilder sb, Span<AgentHousingPlant.SelectedItem> items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var name = item.ItemId != 0 ? XivHubPluginKit.Inventory.ItemSheet.Name(item.ItemId) : "";
            sb.AppendLine(
                $"  [{i}] InventoryType={item.InventoryType} InventorySlot={item.InventorySlot} " +
                $"ItemId={item.ItemId}{(name.Length > 0 ? $" ({name})" : "")}");
        }
    }

    private static unsafe void AppendSelectableItems(StringBuilder sb, Span<AgentHousingPlant.SelectableItem> items, byte count)
    {
        var shown = Math.Min((int)count, items.Length);
        for (var i = 0; i < shown; i++)
        {
            var item = items[i];
            // SAFETY: item is a copy of a live SelectableItem from the agent's own fixed array;
            // ItemCache is a raw pointer into client memory, guarded below before any field is read.
            var cache = item.ItemCache;
            var cacheText = cache == null ? "(no ItemCache)" : $"id={cache->Id} name=\"{cache->Name.ToString()}\"";
            sb.AppendLine($"  [{i}] InventoryType={item.InventoryType} InventorySlot={item.InventorySlot} {cacheText}");
        }
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
