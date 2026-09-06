using System;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Removes whatever is planted in one bed. Never part of a sweep: reuses <see cref="Task_OpenBed"/>'s
/// predict-then-verify interaction for the same reason every other bed action does, but drives its own
/// short sequence afterwards rather than handing off to <see cref="SchedulerMain"/>'s state machine, and
/// refuses to start at all while a sweep is running rather than fight it for the same bed menu. Checks
/// <see cref="Helpers.GardenerGuard.BlockingReason"/> up front the same way <see cref="SchedulerMain.EnablePlugin"/>
/// does for a sweep, since nothing here re-checks it per frame the way a running sweep's own tick does. Because
/// this is destructive and irreversible, the confirmation dialog's own text is classified against
/// <see cref="MenuKey.AskDispose"/> before it is answered — every other bed action's confirm step
/// answers whatever <c>SelectYesno</c> happens to be open, which is fine for a repeatable action but not
/// for one that destroys a crop.
/// </summary>
public static class Task_RemoveCrop
{
    public static bool TryEnqueue(Patch patch, int bedNumber, ushort seedRow)
    {
        if (SchedulerMain.Running)
        {
            ActivityLog.Warn_(Strings.RemoveCrop_SweepRunning);
            return false;
        }

        if (GardenerGuard.BlockingReason() is { } reason)
        {
            ActivityLog.Warn_(reason);
            return false;
        }

        var tm = Plugin.TaskManager;
        var seedName = SeedItems.ProduceName(seedRow);

        Task_OpenBed.Enqueue(patch, bedNumber, onOpened: () =>
        {
            tm.Enqueue(() =>
            {
                var select = AddonFinder.SelectString.FirstOrDefault();
                if (select is not { IsAddonReady: true })
                {
                    ActivityLog.Warn_(Loc.Format(Strings.RemoveCrop_MenuClosed, patch.Key, Formats.Number(bedNumber)));
                    return true;
                }

                var entries = select.Entries;
                var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.Dispose);
                if (index < 0)
                {
                    var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                    ActivityLog.Warn_(Loc.Format(Strings.RemoveCrop_NoEntry, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.Dispose), seen));
                    return true;
                }

                entries[index].Select();
                return true;
            }, $"RemoveCrop: select Dispose (bed {bedNumber})");

            tm.Enqueue(() =>
            {
                var yesno = AddonFinder.YesNo.FirstOrDefault();
                if (yesno is not { IsAddonReady: true })
                    return false; // keep waiting for the confirmation to open

                if (GardenMenuText.Classify(yesno.Text) != MenuKey.AskDispose)
                {
                    ActivityLog.Warn_(Loc.Format(Strings.RemoveCrop_WrongConfirmation,
                        patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.Dispose), yesno.Text));
                    return true;
                }

                yesno.Yes();
                return true;
            }, $"RemoveCrop: confirm (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

            tm.Enqueue(() => AddonFinder.SelectString.FirstOrDefault() is not { IsAddonReady: true },
                $"RemoveCrop: wait for menu to close (bed {bedNumber})",
                new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

            tm.EnqueueDelay(SchedulerPacing.StepDelay());

            tm.Enqueue(() =>
            {
                var stillThere = GardenMemory.Read(patch).Any(s => s.BedNumber == bedNumber && !s.IsEmpty);
                if (stillThere)
                {
                    ActivityLog.Warn_(Loc.Format(Strings.RemoveCrop_StillOccupied,
                        patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.Dispose)));
                }
                else
                {
                    GardenJournal.Remove(patch.Key, bedNumber);
                    ActivityLog.Good_(Loc.Format(Strings.RemoveCrop_Removed, patch.Key, Formats.Number(bedNumber), seedName),
                        chatMessage: Loc.Format(Strings.RemoveCrop_RemovedChat, seedName, Formats.Number(bedNumber)));
                }
                return true;
            }, $"RemoveCrop: record (bed {bedNumber})");

            Task_CloseMenu.Enqueue();
        }, onGiveUp: () => { }); // Task_OpenBed already logged and counted the skip; nothing else to unwind

        return true;
    }
}
