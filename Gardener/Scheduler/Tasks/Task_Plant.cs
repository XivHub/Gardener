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
/// Selects the <see cref="MenuKey.SetSeed"/> entry on the bed <see cref="Task_OpenBed"/> already
/// proved is open and correct, fills the <c>HousingGardening</c> dialog and confirms it. The addon
/// carries no <c>AtkValues</c>, no visible text nodes and <c>SelectableItemCount == 0</c> — it is a
/// shell holding two slots the player fills from the bag, so it is only ever waited for, never read.
/// Because the agent's entries carry a container and slot rather than only an item id, both are
/// re-located from a live scan at the moment the dialog opens; a slot recorded earlier can hold
/// something else by the time this runs.
/// </summary>
public static class Task_Plant
{
    private const string AddonName = "HousingGardening";

    /// <summary>The everyday caller: soil is chosen fresh from whatever the bag holds when the dialog
    /// opens, by family preference.</summary>
    public static void Enqueue(ushort seedRow, SoilPreference soilPreference) =>
        Enqueue(seedRow, bag => (GardeningItems.BestSoil(soilPreference, bag),
            GardeningItems.SoilUnavailable(soilPreference) ?? "No soil available."));

    /// <summary>A crossbreed plan's caller: the plan already resolved a specific soil item at plan-build
    /// time and told the player which one it would use, so this re-verifies that exact item is still
    /// held rather than silently substituting whatever family preference is highest-graded now — the
    /// two could differ if the bag changed between reviewing the plan and running it.</summary>
    public static void Enqueue(ushort seedRow, uint soilItemId) =>
        Enqueue(seedRow, bag =>
        {
            var soil = GardeningItems.Soils.FirstOrDefault(s => s.ItemId == soilItemId);
            var held = soil is not null && bag.Any(slot => slot.ItemId == soilItemId);
            var reason = $"{ItemSheet.Name(soilItemId)} from the plan is no longer in the bag.";
            return (held ? soil : null, reason);
        });

    private static void Enqueue(ushort seedRow, Func<List<SlotView>, (SoilItem? Soil, string Reason)> resolveSoil)
    {
        var tm = Plugin.TaskManager;
        var patch = SchedulerMain.CurrentPatch!;
        var bedNumber = SchedulerMain.CurrentBedNumber!.Value;

        var addonSeen = false;
        void OnAddonPostSetup(AddonEvent type, AddonArgs args) => addonSeen = true;

        // Set once planting actually writes the agent, so the record-writing step below names the
        // soil that was planted rather than re-deriving BestSoil against a bag the planting itself
        // just changed (the soil stack may now be one smaller, or gone).
        uint plantedSoilItemId = 0;

        tm.Enqueue(() =>
        {
            var select = AddonFinder.SelectString.FirstOrDefault();
            if (select is not { IsAddonReady: true })
            {
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: menu closed before it could be planted.",
                    "the menu closed before it could be planted");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.SetSeed);
            if (index < 0)
            {
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: no Plant Seeds entry (offered: {seen}); skipping.",
                    "planting wasn't offered for this bed");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            addonSeen = false;
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);
            entries[index].Select();
            return true;
        }, $"Plant: select SetSeed (bed {bedNumber})");

        tm.Enqueue(() => addonSeen, $"Plant: wait for {AddonName} (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonPostSetup);

            if (!addonSeen)
            {
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: {AddonName} never opened; skipping.",
                    "the planting dialog never opened");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var bag = Bags.Scan();

            var (soil, soilReason) = resolveSoil(bag);
            if (soil is null)
            {
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: {soilReason}",
                    soilReason.TrimEnd('.'));
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var seedItemId = SeedItems.SeedItemForRow(seedRow);
            var seedSlot = seedItemId is { } id ? bag.FirstOrDefault(s => s.ItemId == id) : null;
            if (seedSlot is null)
            {
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: seed for row {seedRow} is no longer in the bag; skipping.",
                    "that seed is no longer in your bags");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var soilSlot = bag.FirstOrDefault(s => s.ItemId == soil.ItemId);
            if (soilSlot is null)
            {
                // BestSoil already confirmed it was held moments ago; re-checked here because the two
                // scans above ran against the same snapshot, but a slot recorded earlier can still be
                // stale by the time this writes the agent.
                var soilName = ItemSheet.Name(soil.ItemId);
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: {soilName} is no longer in the bag; skipping.",
                    $"your {soilName} is gone");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
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
                    ActivityLog.SkippedBed(bedNumber,
                        $"{patch.Key} bed {bedNumber}: AgentHousingPlant unavailable; skipping.",
                        "a game error while planting");
                    SchedulerMain.SkippedCount++;
                    SchedulerMain.State = GardenerState.ClosingMenu;
                    return true;
                }

                agent->SelectedItems[0] = new AgentHousingPlant.SelectedItem
                {
                    InventoryType = soilSlot.Container,
                    InventorySlot = (ushort)soilSlot.SlotIndex,
                    ItemId = soilSlot.ItemId,
                };
                agent->SelectedItems[1] = new AgentHousingPlant.SelectedItem
                {
                    InventoryType = seedSlot.Container,
                    InventorySlot = (ushort)seedSlot.SlotIndex,
                    ItemId = seedSlot.ItemId,
                };
                agent->ConfirmSeedAndSoilSelection();
            }

            plantedSoilItemId = soil.ItemId;
            return true;
        }, $"Plant: fill and confirm (bed {bedNumber})");

        tm.Enqueue(() =>
        {
            var yesno = AddonFinder.YesNo.FirstOrDefault();
            if (yesno is { IsAddonReady: true })
                yesno.Yes();
            return true;
        }, $"Plant: confirm (bed {bedNumber})",
        new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() => AddonFinder.SelectString.FirstOrDefault() is not { IsAddonReady: true },
            $"Plant: wait for menu to close (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.EnqueueDelay(SchedulerPacing.StepDelay());

        tm.Enqueue(() =>
        {
            if (SchedulerMain.State == GardenerState.Error)
                return true; // an earlier step already gave up on this bed

            var now = DateTimeOffset.UtcNow;
            var record = new BedRecord
            {
                PatchKey = patch.Key,
                BedNumber = bedNumber,
                SeedRow = seedRow,
                SoilItemId = plantedSoilItemId,
                PlantedAt = now,
                PlantedAtEstimated = false,
                LastTendedAt = now,
                LastSeenStage = 1, // a fresh planting reads stage 1 on the next passive read
                LastSeenAt = now,
                LastSeenByCharacter = Plugin.ObjectTable.LocalPlayer?.Name.TextValue,
                PlantedByGardener = true,
            };
            GardenJournal.Upsert(record);

            // The passive read is a free receipt for what actually landed: a mismatch here means the
            // bed number the interaction targeted was wrong, which is a reason to stop the whole
            // sweep rather than continue planting into beds the map may also have wrong.
            var confirmed = GardenMemory.Read(patch).Any(s => s.BedNumber == bedNumber && s.SeedRow == seedRow);
            if (!confirmed)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: planting could not be confirmed by the next " +
                                   "passive read; stopping the sweep.",
                    chatMessage: $"Stopped: bed {bedNumber}'s planting couldn't be confirmed.");
                SchedulerMain.State = GardenerState.Error;
                return true;
            }

            SchedulerMain.PlantedCount++;
            var produce = SeedItems.ProduceName(seedRow);
            var soilName = ItemSheet.Name(plantedSoilItemId);
            ActivityLog.Good_($"{patch.Key} bed {bedNumber}: planted {produce} in {soilName}.",
                chatMessage: $"Planted {produce} in bed {bedNumber}, {soilName}.");
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Plant: record and confirm (bed {bedNumber})");
    }

}
