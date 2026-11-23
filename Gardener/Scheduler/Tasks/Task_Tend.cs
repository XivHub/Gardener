using System;
using System.Linq;
using ECommons.UIHelpers;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Selects the <see cref="MenuKey.Care"/> entry on the bed <see cref="Task_OpenBed"/> already proved
/// is open and correct. There is no re-open confirmation step afterwards: the <c>TALK_VIGOROUS</c>
/// sentence a re-check would read is unreachable through this menu, so success
/// is simply having selected the entry.
/// <para>Every skip branch returns <c>null</c>, not <c>true</c>: <c>true</c> only completes the
/// current step, and the record step queued after it would then still run and log a tend that never
/// happened. <c>null</c> aborts the rest of this bed's queue, the same way <see cref="Task_OpenBed"/>
/// gives up; the <see cref="GardenerState.ClosingMenu"/> state set just before is what runs next.</para>
/// </summary>
public static class Task_Tend
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
                    Loc.Format(Strings.Tend_MenuClosed, patch.Key, Formats.Number(bedNumber)),
                    Strings.Tend_MenuClosedChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null; // abort the rest of this bed's queue; see the class summary
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.Care);
            if (index < 0)
            {
                // No Care entry means this is not the menu expected for a growing bed; log what it
                // actually offered rather than guess.
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.Tend_NoCareEntry, patch.Key, Formats.Number(bedNumber), GameWords.Action(MenuKey.Care), seen),
                    Strings.Tend_NoCareEntryChat);
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return null; // abort the rest of this bed's queue; see the class summary
            }

            entries[index].Select();
            return true;
        }, $"Tend: select Care (bed {bedNumber})");

        tm.EnqueueDelay(SchedulerPacing.StepDelay());

        tm.Enqueue(() =>
        {
            var record = GardenJournal.Get(patch.Key, bedNumber);
            if (record is null)
            {
                // Defensive: the worklist came from a fresh GardenMemory.Read, but the journal's own
                // 2s reconcile cadence may not have caught up to it yet. Seed the record from that
                // same live state rather than a bare default, so a later Reconcile never sees a
                // SeedRow mismatch and discards the tend timestamp being set below.
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

            record.LastTendedAt = DateTimeOffset.UtcNow;
            GardenJournal.Upsert(record);

            SchedulerMain.TendedCount++;
            var produce = SeedItems.ProduceName(record.SeedRow);
            ActivityLog.Good_(Loc.Format(Strings.Tend_Tended, patch.Key, Formats.Number(bedNumber), produce),
                chatMessage: Loc.Format(Strings.Tend_TendedChat, Formats.Number(bedNumber), produce));
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Tend: record (bed {bedNumber})");
    }
}
