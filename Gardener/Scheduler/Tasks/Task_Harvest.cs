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
/// Selects the <see cref="MenuKey.Harvest"/> entry on the bed <see cref="Task_OpenBed"/> already
/// proved is open and correct. A stage-4 bed that offers no <c>Harvest</c> entry is not an error: it
/// is evidence that stage 4 (<see cref="Maturity.MatureCandidate"/>) is a
/// candidate, not a proof of ripeness, so this counts it and moves on rather than treating it as a
/// failure.
/// </summary>
public static class Task_Harvest
{
    public static void Enqueue()
    {
        var tm = Plugin.TaskManager;
        var patch = SchedulerMain.CurrentPatch!;
        var bedNumber = SchedulerMain.CurrentBedNumber!.Value;

        tm.Enqueue(() =>
        {
            var select = AddonFinder.SelectString.FirstOrDefault();
            if (select is not { IsAddonReady: true })
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Harvest_MenuClosed, patch.Key, Formats.Number(bedNumber)),
                    Strings.Harvest_MenuClosedChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.Harvest);
            if (index < 0)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Harvest_NoHarvestEntry, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.Harvest)),
                    Strings.Harvest_NoHarvestEntryChat);
                SchedulerMain.NoHarvestOfferedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            // The calibration signal must not depend on the harvest below actually succeeding, so
            // the timestamp is recorded now, before the entry is selected.
            if (GardenJournal.Get(patch.Key, bedNumber) is { FirstSeenHarvestOfferedAt: null } record)
            {
                record.FirstSeenHarvestOfferedAt = DateTimeOffset.UtcNow;
                GardenJournal.Upsert(record);
            }

            entries[index].Select();
            return true;
        }, $"Harvest: select Harvest (bed {bedNumber})");

        tm.Enqueue(() =>
        {
            var yesno = AddonFinder.YesNo.FirstOrDefault();
            if (yesno is { IsAddonReady: true })
                yesno.Yes();
            return true;
        }, $"Harvest: confirm (bed {bedNumber})",
        new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() => AddonFinder.SelectString.FirstOrDefault() is not { IsAddonReady: true },
            $"Harvest: wait for menu to close (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            var record = GardenJournal.Get(patch.Key, bedNumber);
            if (record is not null)
                GardenJournal.RecordHarvestOfferedSample(record);

            var seedRow = record?.SeedRow ??
                GardenMemory.Read(patch).FirstOrDefault(s => s.BedNumber == bedNumber).SeedRow;
            GardenJournal.Remove(patch.Key, bedNumber);

            SchedulerMain.HarvestedCount++;
            var produce = SeedItems.ProduceName(seedRow);
            ActivityLog.Good_(Loc.Format(Strings.Harvest_Harvested, patch.Key, Formats.Number(bedNumber), produce),
                chatMessage: Loc.Format(Strings.Harvest_HarvestedChat, Formats.Number(bedNumber), produce));
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Harvest: record (bed {bedNumber})");
    }
}
