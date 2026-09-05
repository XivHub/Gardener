using System;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using Gardener.Game;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Selects the bed menu's <see cref="MenuKey.EndEvent"/> entry and waits for the addon to disappear.
/// Reusable primitive with no state-transition opinion of its own: the caller decides what happens
/// next, whether that is moving to the next worklist bed or retrying the one that was just closed.
/// </summary>
public static class Task_CloseMenu
{
    public static void Enqueue()
    {
        var tm = Plugin.TaskManager;

        tm.Enqueue(() =>
        {
            var select = AddonFinder.SelectString.FirstOrDefault();
            if (select is not { IsAddonReady: true })
                return true; // already closed

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.EndEvent);
            if (index < 0)
            {
                Plugin.Logger.Warning("[Gardener] bed menu has no Quit entry to close with; leaving it open.");
                return true;
            }

            entries[index].Select();
            return true;
        }, "CloseMenu: select Quit");

        tm.Enqueue(() => AddonFinder.SelectString.FirstOrDefault() is not { IsAddonReady: true },
            "CloseMenu: wait for menu to close",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });
    }
}
