using System;
using System.Linq;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Gardener.Game;
using Gardener.Localization;

namespace Gardener.Helpers;

/// <summary>
/// Closes whatever gardening UI the plugin itself opened when a run ends outside the normal
/// <see cref="Scheduler.Tasks.Task_CloseMenu"/> path: a guard trip or the Stop button both end a run
/// through <see cref="Scheduler.SchedulerMain.DisablePlugin"/>, which can land mid-interaction on the
/// bed's own <c>SelectString</c>, the <c>HousingGardening</c> planting dialog, or an item's
/// <c>ContextMenu</c> — <see cref="Scheduler.Tasks.Task_Fertilize"/>'s own path to fertilizing. Every
/// check here is safe to call with nothing open: a surface that was never open is skipped without a
/// log line, since only an actual close is worth telling the player about.
/// </summary>
public static class GardeningUiCleanup
{
    private const string PlantDialogAddonName = "HousingGardening";
    private const string ContextMenuAddonName = "ContextMenu";

    public static unsafe void CloseAll()
    {
        CloseBedMenu();
        CloseAddonByName(PlantDialogAddonName);
        CloseAddonByName(ContextMenuAddonName);
    }

    /// <summary>The bed's own <c>SelectString</c>, closed the way the game itself expects: selecting
    /// its <see cref="MenuKey.EndEvent"/> entry properly ends the housing interaction rather than only
    /// hiding the addon and leaving <c>OccupiedInQuestEvent</c> set server-side. Falls back to a plain
    /// <see cref="AtkUnitBase.Close"/> when the menu is open but offers no Quit entry to select — a
    /// state the bed's <c>SelectString</c> is never expected to be in — logging that the fallback ran
    /// rather than leaving the player to wonder why the menu vanished a different way.</summary>
    private static unsafe void CloseBedMenu()
    {
        var select = AddonFinder.SelectString.FirstOrDefault();
        if (select is not { IsAddonReady: true })
            return;

        var entries = select.Entries;
        var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.EndEvent);
        if (index < 0)
        {
            ActivityLog.Warn_(Loc.Format(Strings.Cleanup_NoQuitEntry, nameof(MenuKey.EndEvent)), chat: false);
            select.Base->Close(true);
            return;
        }

        entries[index].Select();
        ActivityLog.Notify(Strings.Cleanup_ClosedBedMenu, chat: false);
    }

    /// <summary>Generic dismiss for a surface with no menu entry of its own to select — the same
    /// <see cref="AtkUnitBase.Close"/> call <see cref="Scheduler.Tasks.Task_Fertilize"/> already uses to
    /// back out of a context menu it opened but decided not to act on.</summary>
    private static unsafe void CloseAddonByName(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name);
        if (addon == null || !addon->IsVisible)
            return;

        if (addon->Close(true))
            ActivityLog.Notify(Loc.Format(Strings.Cleanup_ClosedAddon, name), chat: false);
        else
            ActivityLog.Warn_(Loc.Format(Strings.Cleanup_AddonWouldNotClose, name), chat: false);
    }
}
