using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;
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
/// <para>Every skip branch returns <c>null</c>, not <c>true</c>: <c>true</c> only completes the
/// current step, so the two timed waits queued after it (fertilize mode, then the context menu) used
/// to run out their full <see cref="Configuration.MenuTimeoutMs"/> each for a bed already given up
/// on. <c>null</c> aborts the rest of this bed's queue, the same way <see cref="Task_OpenBed"/> gives
/// up; the <see cref="GardenerState.ClosingMenu"/> state set just before is what runs next.</para>
/// <para>When the game itself refuses the fertilizer ("This crop has already been sufficiently
/// fertilized." for one applied by hand, which the journal's cooldown cannot know about) it says so
/// with an error toast and fertilize mode simply never engages. <see cref="StartListening"/> catches
/// that toast so the waits below end on it instead of on their timeouts; any error toast in that
/// window counts, so no language's wording is matched.</para>
/// </summary>
public static class Task_Fertilize
{
    private const string ContextMenuAddonName = "ContextMenu";

    // FFXIVClientStructs marks AgentHousingPlant.PlotType's meaning unknown; a live capture resolved
    // it: 14 is the seed/soil dialog, 15 is this item-context-menu flow, and there is no third value
    // to confuse it with.
    private const uint FertilizePlotType = 15;

    // The text of the error toast the game showed while the current bed was being fertilized, or
    // null. Static because the toast events are, and only one bed is ever in progress at a time.
    private static string? refusal;

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
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_MenuClosed, patch.Key, Formats.Number(bedNumber)),
                    Strings.Fertilize_MenuClosedChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            var record = GardenJournal.Get(patch.Key, bedNumber);
            if (record?.LastFertilizedAt is { } lastFertilizedAt &&
                DateTimeOffset.UtcNow - lastFertilizedAt < TimeSpan.FromMinutes(Plugin.C.FertilizeCooldownMin))
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_RecentlyFertilized, patch.Key, Formats.Number(bedNumber), Formats.Number(Plugin.C.FertilizeCooldownMin)),
                    Strings.Fertilize_RecentlyFertilizedChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            var state = GardenMemory.Read(patch).FirstOrDefault(s => s.BedNumber == bedNumber);
            if (Plugin.C.FertilizeOnlyGrowing && state.Maturity == Maturity.MatureCandidate)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_AlreadyMature, patch.Key, Formats.Number(bedNumber)),
                    Strings.Fertilize_AlreadyMatureChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.SetFertilizer);
            if (index < 0)
            {
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_NoFertilizeEntry, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.SetFertilizer), seen),
                    Strings.Fertilize_NoFertilizeEntryChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            StartListening();
            entries[index].Select();
            return true;
        }, $"Fertilize: select SetFertilizer (bed {bedNumber})");

        // No addon opens for this flow, so PlotType is the only readiness signal there is: it is 0
        // before the entry is accepted and 15 once fertilize mode is active.
        tm.Enqueue(() => refusal is not null || IsInFertilizeMode(), $"Fertilize: wait for fertilize mode (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            if (refusal is { } refusedBeforeMode)
            {
                SkipRefused(patch.Key, bedNumber, refusedBeforeMode);
                return null;
            }

            if (!IsInFertilizeMode())
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_ModeNeverEngaged, patch.Key, Formats.Number(bedNumber)),
                    Strings.Fertilize_ModeNeverEngagedChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            var bag = Bags.Scan();
            var fertilizerId = GardeningItems.BestFertilizer(bag);
            var fertilizerSlot = fertilizerId is { } id ? bag.FirstOrDefault(s => s.ItemId == id) : null;
            if (fertilizerSlot is null)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_NoLongerInBag, patch.Key, Formats.Number(bedNumber)),
                    Strings.Fertilize_NoLongerInBagChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
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
                    ActivityLog.SkippedBed(bedNumber,
                        Loc.Format(Strings.Fertilize_AgentUnavailable, patch.Key, Formats.Number(bedNumber)),
                        Strings.Fertilize_AgentUnavailableChat);
                    SchedulerMain.SkippedCount++;
                    SchedulerMain.State = GardenerState.ClosingMenu;
                    return null;
                }

                // OpenForItemSlot creates ContextMenu synchronously, before this PostSetup listener's
                // registration is live, so the listener is a diagnostic cross-check only; the poll in
                // FindReadyContextMenu below is what actually gates the next two steps.
                addonSeen = false;
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuPostSetup);
                inventoryContext->OpenForItemSlot(fertilizerSlot.Container, fertilizerSlot.SlotIndex, 0, inventoryAgent->AddonId);
            }

            return true;
        }, $"Fertilize: open the fertilizer's context menu (bed {bedNumber})");

        tm.Enqueue(() => refusal is not null || addonSeen || FindReadyContextMenu() is not null,
            $"Fertilize: wait for {ContextMenuAddonName} (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuPostSetup);

            var contextMenu = FindReadyContextMenu();
            if (refusal is { } refusedBeforeMenu)
            {
                if (contextMenu is not null)
                    unsafe { contextMenu.Base->Close(true); }
                SkipRefused(patch.Key, bedNumber, refusedBeforeMenu);
                return null;
            }

            if (contextMenu is null)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_ContextMenuNotOpen, patch.Key, Formats.Number(bedNumber), ContextMenuAddonName),
                    Strings.Fertilize_ContextMenuNotOpenChat);
                LogContextAddonsForDiagnosis(patch.Key, bedNumber);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            if (!addonSeen)
                Plugin.Logger.Information(
                    $"[Gardener] {patch.Key} bed {bedNumber}: {ContextMenuAddonName} PostSetup never fired; " +
                    "found by polling instead.");

            var entries = contextMenu.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.IsFertilizeAction(e.Text));
            if (index < 0)
            {
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_NoFertilizeOnItem, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.SetFertilizer), seen),
                    Strings.Fertilize_NoFertilizeOnItemChat);
                SchedulerMain.SkippedCount++;
                unsafe { contextMenu.Base->Close(true); }
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
            }

            if (!entries[index].Select())
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Fertilize_EntryDisabledOnItem, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.SetFertilizer)),
                    Strings.Fertilize_EntryDisabledOnItemChat);
                SchedulerMain.SkippedCount++;
                unsafe { contextMenu.Base->Close(true); }
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null;
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

        // A refusal can also arrive after the item is used; give its toast a moment to land so it is
        // never recorded as a success.
        tm.EnqueueDelay(SchedulerPacing.StepDelay());

        tm.Enqueue(() =>
        {
            StopListening();

            if (!fertilizeSent)
                return true; // an earlier step already skipped this bed; nothing to record

            if (refusal is { } refusedAfterUse)
            {
                SkipRefused(patch.Key, bedNumber, refusedAfterUse);
                return null;
            }

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
            var produce = SeedItems.ProduceName(record.SeedRow);
            ActivityLog.Good_(Loc.Format(Strings.Fertilize_Fertilized, patch.Key, Formats.Number(bedNumber), produce),
                chatMessage: Loc.Format(Strings.Fertilize_FertilizedChat, Formats.Number(bedNumber), produce));
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Fertilize: record (bed {bedNumber})");
    }

    /// <summary>Arms the toast listeners for one bed, clearing any refusal left from the last one.
    /// Called right before the bed's Fertilize Crop entry is selected; <see cref="StopListening"/>
    /// runs at the end of the bed and from <see cref="SchedulerMain.DisablePlugin"/>, so an aborted
    /// sweep never leaves them attached.</summary>
    internal static void StartListening()
    {
        StopListening();
        refusal = null;
        Plugin.ToastGui.ErrorToast += OnErrorToast;
        Plugin.ToastGui.Toast += OnNormalToast;
        Plugin.ToastGui.QuestToast += OnQuestToast;
    }

    internal static void StopListening()
    {
        Plugin.ToastGui.ErrorToast -= OnErrorToast;
        Plugin.ToastGui.Toast -= OnNormalToast;
        Plugin.ToastGui.QuestToast -= OnQuestToast;
    }

    private static void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        refusal ??= message.TextValue;
        Plugin.Logger.Information($"[Gardener] error toast while fertilizing: \"{message.TextValue}\"");
    }

    // Diagnostic only: logged so a refusal shown as a non-error toast is visible in the devlog, but
    // never treated as one, since ordinary toasts are not refusals.
    private static void OnNormalToast(ref SeString message, ref ToastOptions options, ref bool isHandled) =>
        Plugin.Logger.Information($"[Gardener] toast while fertilizing: \"{message.TextValue}\"");

    private static void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled) =>
        Plugin.Logger.Information($"[Gardener] quest toast while fertilizing: \"{message.TextValue}\"");

    private static void SkipRefused(string patchKey, int bedNumber, string toastText)
    {
        StopListening();
        ActivityLog.SkippedBed(bedNumber,
            Loc.Format(Strings.Fertilize_GameRefused, patchKey, Formats.Number(bedNumber), toastText),
            Strings.Fertilize_GameRefusedChat);
        SchedulerMain.SkippedCount++;
        SchedulerMain.State = GardenerState.ClosingMenu;
    }

    private static unsafe bool IsInFertilizeMode()
    {
        // SAFETY: AgentModule.Instance() is a static client pointer; the agent pointer is reinterpreted
        // only after a null check, matching DebugDump's own read of this agent.
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentHousingPlant*)agentModule->GetAgentByInternalId(AgentId.HousingPlant);
        return agent != null && agent->PlotType == FertilizePlotType;
    }


    /// <summary>The live <see cref="ContextMenuAddonName"/> addon if one is open and ready, found by
    /// name rather than by the <c>PostSetup</c> listener: <c>AgentInventoryContext.OpenForItemSlot</c>
    /// creates the addon synchronously, before a listener registered around that same call is
    /// guaranteed to be live, so polling by name is the only check this step can trust.</summary>
    private static unsafe AddonMaster.ContextMenu? FindReadyContextMenu()
    {
        var addon = Plugin.GameGui.GetAddonByName(ContextMenuAddonName, 1);
        if (addon.IsNull)
            return null;

        var contextMenu = new AddonMaster.ContextMenu(addon);
        return contextMenu.IsAddonReady ? contextMenu : null;
    }

    /// <summary>Every currently loaded addon whose name contains "Context", logged when
    /// <see cref="FindReadyContextMenu"/> comes up empty so a stall is diagnosable from the devlog
    /// rather than only from a screenshot: whether the menu is genuinely closed, open under a name
    /// this class does not poll for, or open but unreadable. For an open <see cref="ContextMenuAddonName"/>,
    /// also logs its entry count and text so a misread is visible immediately.</summary>
    private static unsafe void LogContextAddonsForDiagnosis(string patchKey, int bedNumber)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            // Log tab only (chat: false): the player-facing skip line already printed above.
            ActivityLog.Warn_(Loc.Format(Strings.Fertilize_ManagerUnavailable, patchKey, Formats.Number(bedNumber)),
                chat: false);
            return;
        }

        var units = manager->AllLoadedUnitsList;
        var found = new List<string>();
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units.Entries[i].Value;
            if (unit == null || !unit->NameString.Contains("Context", StringComparison.OrdinalIgnoreCase))
                continue;

            var detail = $"{unit->NameString} (visible={unit->IsVisible})";
            if (unit->NameString == ContextMenuAddonName && unit->IsVisible)
            {
                var values = unit->AtkValuesSpan;
                var reportedCount = values.Length > 0 ? (int)values[0].UInt : -1;
                var entries = new AddonMaster.ContextMenu(unit).Entries;
                var texts = string.Join(", ", entries.Select(e => $"\"{e.Text}\""));
                detail += $" entryCount(reported={reportedCount}, read={entries.Length}) entries=[{texts}]";
            }

            found.Add(detail);
        }

        // Log tab only (chat: false): the player-facing skip line already printed above.
        ActivityLog.Warn_(found.Count == 0
            ? Loc.Format(Strings.Fertilize_NoContextAddonOpen, patchKey, Formats.Number(bedNumber))
            : Loc.Format(Strings.Fertilize_ContextAddonsFound, patchKey, Formats.Number(bedNumber), string.Join("; ", found)),
            chat: false);
    }
}
