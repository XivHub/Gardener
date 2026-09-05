using System;
using System.Linq;
using ECommons.UIHelpers;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Selects the <see cref="MenuKey.Care"/> entry on the bed <see cref="Task_OpenBed"/> already proved
/// is open and correct. There is no re-open confirmation step afterwards: the <c>TALK_VIGOROUS</c>
/// sentence a re-check would read is unreachable through this menu, so success
/// is simply having selected the entry.
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
                    $"{patch.Key} bed {bedNumber}: menu closed before it could be tended.",
                    "the menu closed before it could be tended");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
            }

            var entries = select.Entries;
            var index = Array.FindIndex(entries, e => GardenMenuText.Classify(e.Text) == MenuKey.Care);
            if (index < 0)
            {
                // No Care entry means this is not the menu expected for a growing bed; log what it
                // actually offered rather than guess.
                var seen = string.Join(", ", entries.Select(e => GardenMenuText.Classify(e.Text)));
                ActivityLog.SkippedBed(bedNumber,
                    $"{patch.Key} bed {bedNumber}: no Care entry (offered: {seen}); skipping.",
                    "tending wasn't offered for this bed");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.ClosingMenu;
                return true;
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
            ActivityLog.Good_($"{patch.Key} bed {bedNumber}: tended ({produce}).",
                chatMessage: $"Tended bed {bedNumber} ({produce}).");
            SchedulerMain.State = GardenerState.ClosingMenu;
            return true;
        }, $"Tend: record (bed {bedNumber})");
    }
}
