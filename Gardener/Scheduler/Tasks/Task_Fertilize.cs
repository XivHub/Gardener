using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using XivHubPluginKit.Inventory;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Selects the <see cref="MenuKey.SetFertilizer"/> entry on the bed <see cref="Task_OpenBed"/> already
/// proved is open and correct, fills the <c>HousingGardening</c> dialog and confirms it. The dialog's
/// shape for this flow has never been captured live — the seed/soil capture in
/// <c>AgentHousingPlant.SelectedItems</c> is the only confirmed layout, and this task assumes the
/// fertilizer sits in the same slot 0 a soil would occupy, since the struct offers no other confirm
/// entry point (<c>ConfirmSeedAndSoilSelection()</c> is the only exposed member function). Confirm this
/// assignment against a real capture (<c>/gardener dump menu</c> with Fertilize Crop selected) before
/// relying on it.
/// </summary>
public static class Task_Fertilize
{
    private const string AddonName = "HousingGardening";

    public static void Enqueue()
    {
        var tm = Plugin.TaskManager;
        var patch = SchedulerMain.CurrentPatch!;
        var bedNumber = SchedulerMain.CurrentBedNumber!.Value;

        var addonSeen = false;
        void OnAddonPostSetup(AddonEvent type, AddonArgs args) => addonSeen = true;

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

            addonSeen = false;
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);
            entries[index].Select();
            return true;
        }, $"Fertilize: select SetFertilizer (bed {bedNumber})");

        tm.Enqueue(() => addonSeen, $"Fertilize: wait for {AddonName} (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);

            if (!addonSeen)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: {AddonName} never opened; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var bag = ScanBag();
            var fertilizerSlot = bag.FirstOrDefault(s => GardeningItems.Fertilizers.Contains(s.ItemId));
            if (fertilizerSlot is null)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: ran out of fertilizer mid-sweep; stopping.");
                SchedulerMain.State = GardenerState.Error;
                return true;
            }

            unsafe
            {
                // SAFETY: AgentModule.Instance() is a static client pointer; GetAgentByInternalId's
                // result is reinterpreted only after a null check, matching DebugDump's own read of
                // this agent.
                var agentModule = AgentModule.Instance();
                var agent = agentModule == null ? null : (AgentHousingPlant*)agentModule->GetAgentByInternalId(AgentId.HousingPlant);
                if (agent == null)
                {
                    ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: AgentHousingPlant unavailable; skipping.");
                    SchedulerMain.SkippedCount++;
                    SchedulerMain.State = GardenerState.ClosingMenu;
                    return true;
                }

                agent->SelectedItems[0] = new AgentHousingPlant.SelectedItem
                {
                    InventoryType = fertilizerSlot.Container,
                    InventorySlot = (ushort)fertilizerSlot.SlotIndex,
                    ItemId = fertilizerSlot.ItemId,
                };
                agent->ConfirmSeedAndSoilSelection();
            }

            return true;
        }, $"Fertilize: fill and confirm (bed {bedNumber})");

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
            if (SchedulerMain.State == GardenerState.Error)
                return true; // already terminal; nothing left to record

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

    private static List<SlotView> ScanBag() =>
        InventoryScan.ScanContainer(InventoryType.Inventory1)
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory2))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory3))
            .Concat(InventoryScan.ScanContainer(InventoryType.Inventory4))
            .ToList();
}
