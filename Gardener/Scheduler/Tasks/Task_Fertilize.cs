using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using XivHubPluginKit.Inventory;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Selects the <see cref="MenuKey.SetFertilizer"/> entry on the bed <see cref="Task_OpenBed"/> already
/// proved is open and correct, then follows the item's own path rather than the bed's: choosing
/// Fertilize Crop opens no dialog. It closes the bed's <c>SelectString</c> and flips
/// <c>AgentHousingPlant.PlotType</c> to 15, and the game waits for the fertilizer to be used from the
/// bag's own context menu (<c>AgentInventoryContext.OpenForItemSlot</c>) — the same route DailyRoutines
/// takes. Confirmed live: no <c>HousingGardening</c> addon opens, <c>SelectableItemCount</c> stays 0,
/// and both <c>SelectedItems</c> entries stay zeroed throughout.
/// </summary>
public static class Task_Fertilize
{
    private const string ContextMenuAddonName = "ContextMenu";

    // FFXIVClientStructs marks AgentHousingPlant.PlotType's meaning unknown; a live capture resolved
    // it: 14 is the seed/soil dialog, 15 is this item-context-menu flow, and there is no third value
    // to confuse it with.
    private const uint FertilizePlotType = 15;

    public static void Enqueue()
    {
        var tm = Plugin.TaskManager;
        var patch = SchedulerMain.CurrentPatch!;
        var bedNumber = SchedulerMain.CurrentBedNumber!.Value;

        var addonSeen = false;
        void OnContextMenuPostSetup(AddonEvent type, AddonArgs args) => addonSeen = true;

        // Set once the context-menu entry is actually clicked, so the record-writing step at the end
        // only counts a fertilizer that was actually sent, never a bed an earlier step already gave up
        // on.
        var fertilizeSent = false;

        tm.Enqueue(() =>
        {
            var select = AddonFinder.SelectString.FirstOrDefault();
            if (select is not { IsAddonReady: true })
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: menu closed before it could be fertilized.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var record = GardenJournal.Get(patch.Key, bedNumber);
            if (record?.LastFertilizedAt is { } lastFertilizedAt &&
                DateTimeOffset.UtcNow - lastFertilizedAt < TimeSpan.FromMinutes(Plugin.C.FertilizeCooldownMin))
            {
                ActivityLog.Notify($"{patch.Key} bed {bedNumber}: fertilized within the last " +
                                    $"{Plugin.C.FertilizeCooldownMin}min; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var state = GardenMemory.Read(patch).FirstOrDefault(s => s.BedNumber == bedNumber);
            if (Plugin.C.FertilizeOnlyGrowing && state.Maturity == Maturity.MatureCandidate)
            {
                ActivityLog.Notify($"{patch.Key} bed {bedNumber}: already mature; fertilizer would do nothing.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.SetFertilizer);
            if (index < 0)
            {
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: no Fertilize entry (offered: {seen}); skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            entries[index].Select();
            return true;
        }, $"Fertilize: select SetFertilizer (bed {bedNumber})");

        // No addon opens for this flow, so PlotType is the only readiness signal there is: it is 0
        // before the entry is accepted and 15 once fertilize mode is active.
        tm.Enqueue(() => IsInFertilizeMode(), $"Fertilize: wait for fertilize mode (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            if (!IsInFertilizeMode())
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: fertilize mode never engaged; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var bag = ScanBag();
            var fertilizerId = GardeningItems.BestFertilizer(bag);
            var fertilizerSlot = fertilizerId is { } id ? bag.FirstOrDefault(s => s.ItemId == id) : null;
            if (fertilizerSlot is null)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: fertilizer is no longer in the bag; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            unsafe
            {
                // SAFETY: AgentInventoryContext.Instance() already null-checks AgentModule internally.
                // AgentModule.Instance() here is the same static client pointer, and the AgentInventory
                // it returns is reinterpreted only after a null check, matching DebugDump's own reads
                // of agent memory.
                var inventoryContext = AgentInventoryContext.Instance();
                var agentModule = AgentModule.Instance();
                var inventoryAgent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.Inventory);
                if (inventoryContext == null || inventoryAgent == null)
                {
                    ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: inventory context agent unavailable; skipping.");
                    SchedulerMain.SkippedCount++;
                    SchedulerMain.State = GardenerState.ClosingMenu;
                    return true;
                }

                addonSeen = false;
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuPostSetup);
                inventoryContext->OpenForItemSlot(fertilizerSlot.Container, fertilizerSlot.SlotIndex, 0, inventoryAgent->AddonId);
            }

            return true;
        }, $"Fertilize: open the fertilizer's context menu (bed {bedNumber})");

        tm.Enqueue(() => addonSeen, $"Fertilize: wait for {ContextMenuAddonName} (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuPostSetup);

            if (!addonSeen)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: the fertilizer's context menu never opened; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var addon = Plugin.GameGui.GetAddonByName(ContextMenuAddonName, 1);
            var contextMenu = new AddonMaster.ContextMenu(addon);
            if (addon.IsNull || !contextMenu.IsAddonReady)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: the fertilizer's context menu closed before it could be read; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var entries = contextMenu.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.IsFertilizeAction(e.Text));
            if (index < 0)
            {
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: no Fertilize entry on the item's context menu " +
                                   $"(offered: {seen}); closing and skipping.");
                SchedulerMain.SkippedCount++;
                unsafe { contextMenu.Base->Close(true); }
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            if (!entries[index].Select())
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: the Fertilize entry was disabled on the item's " +
                                   "context menu; closing and skipping.");
                SchedulerMain.SkippedCount++;
                unsafe { contextMenu.Base->Close(true); }
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            fertilizeSent = true;
            return true;
        }, $"Fertilize: select the item's Fertilize entry (bed {bedNumber})");

        tm.Enqueue(() =>
        {
            var yesno = AddonFinder.YesNo.FirstOrDefault();
            if (yesno is { IsAddonReady: true })
                yesno.Yes();
            return true;
        }, $"Fertilize: confirm (bed {bedNumber})",
        new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            if (!fertilizeSent)
                return true; // an earlier step already skipped this bed; nothing to record

            var record = GardenJournal.Get(patch.Key, bedNumber);
            if (record is null)
            {
                var state = GardenMemory.Read(patch).FirstOrDefault(s => s.BedNumber == bedNumber);
                record = new BedRecord
                {
                    PatchKey = patch.Key,
                    BedNumber = bedNumber,
                    SeedRow = state.SeedRow,
                    LastSeenStage = state.Stage,
                    LastSeenAt = state.ReadAt,
                };
            }

            record.LastFertilizedAt = DateTimeOffset.UtcNow;
            record.FertilizerCount++;
            GardenJournal.Upsert(record);

            SchedulerMain.FertilizedCount++;
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Fertilize: record (bed {bedNumber})");
    }

    private static unsafe bool IsInFertilizeMode()
    {
        // SAFETY: AgentModule.Instance() is a static client pointer; the agent pointer is reinterpreted
        // only after a null check, matching DebugDump's own read of this agent.
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentHousingPlant*)agentModule->GetAgentByInternalId(AgentId.HousingPlant);
        return agent != null && agent->PlotType == FertilizePlotType;
    }

    private static List<SlotView> ScanBag() =>
        InventoryScan.ScanContainer(InventoryType.Inventory1)
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory2))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory3))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory4))
            .ToList();
}
