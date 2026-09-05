using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Gardener.Game;
using Gardener.Helpers;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Interacts with the bed <see cref="BedTargeting"/> predicts for a bed number and proves it is the
/// right one before anything else touches it. This is the only place in the scheduler that ever opens
/// a bed menu, and the only place that ever acts on one without first reading its "Nth Bed" prompt
/// back: never act on an unparsed or mismatched prompt.
/// </summary>
public static class Task_OpenBed
{
    // Bounded retries per bed per sweep — the prompt disagreeing with the map three times running
    // means something is wrong with this bed specifically, not a one-off correction worth chasing.
    private const int MaxAttemptsPerBed = 3;

    public static void Enqueue(Patch patch, int bedNumber, int attempt = 1)
    {
        var tm = Plugin.TaskManager;
        var targetEntityId = 0u;

        tm.Enqueue(() =>
        {
            var predicted = BedTargeting.Predict(patch, bedNumber);
            if (predicted is not { } entityId)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: no predicted bed entity; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.OpeningBed;
                return null;
            }

            var obj = Plugin.ObjectTable.SearchByEntityId(entityId);
            if (obj is null)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: bed entity 0x{entityId:X8} is no longer in the object table; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.OpeningBed;
                return null;
            }

            targetEntityId = entityId;

            unsafe
            {
                // SAFETY: TargetSystem.Instance() is a static client pointer, checked below before
                // use. obj is a live IGameObject just looked up from the object table; its Address is
                // the game's own GameObject* for this entity, valid for this synchronous call.
                var targetSystem = TargetSystem.Instance();
                if (targetSystem == null)
                {
                    ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: TargetSystem unavailable; skipping.");
                    SchedulerMain.SkippedCount++;
                    SchedulerMain.State = GardenerState.OpeningBed;
                    return null;
                }

                targetSystem->InteractWithObject((GameObject*)obj.Address, checkLineOfSight: false);
            }

            return true;
        }, $"OpenBed: interact bed {bedNumber} (attempt {attempt})");

        tm.Enqueue(() => AddonFinder.SelectString.FirstOrDefault() is { IsAddonReady: true },
            $"OpenBed: wait for menu (bed {bedNumber})",
            new TaskManagerConfiguration { TimeLimitMS = Plugin.C.MenuTimeoutMs, AbortOnTimeout = false });

        tm.Enqueue(() =>
        {
            var select = AddonFinder.SelectString.FirstOrDefault();
            if (select is not { IsAddonReady: true })
            {
                // The wait step above already timed out silently (AbortOnTimeout: false); nothing
                // ever opened.
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: bed menu never opened; skipping.");
                SchedulerMain.SkippedCount++;
                SchedulerMain.State = GardenerState.OpeningBed;
                return null;
            }

            var bedPatch = GardenMenuText.ParseBedPatch(select.Text);
            if (bedPatch is not { } bp)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: prompt \"{select.Text}\" did not parse " +
                                   "into bed/patch numbers; closing without acting.");
                SchedulerMain.SkippedCount++;
                Task_CloseMenu.Enqueue();
                Plugin.TaskManager.Enqueue(() =>
                {
                    SchedulerMain.State = GardenerState.OpeningBed;
                    return true;
                }, "OpenBed: skip after unparseable prompt");
                return true;
            }

            if (bp.Bed == bedNumber)
            {
                BedTargeting.MarkVerified(patch, bedNumber);
                SchedulerMain.State = GardenerState.Acting;
                return true;
            }

            // Mismatch: correct the map from the observed pairing and retry, bounded.
            BedTargeting.Confirm(patch, targetEntityId, bp.Bed);

            if (attempt >= MaxAttemptsPerBed)
            {
                ActivityLog.Warn_($"{patch.Key} bed {bedNumber}: gave up after {attempt} attempts " +
                                   "(the menu kept disagreeing with the map); skipping.");
                SchedulerMain.SkippedCount++;
                Task_CloseMenu.Enqueue();
                Plugin.TaskManager.Enqueue(() =>
                {
                    SchedulerMain.State = GardenerState.OpeningBed;
                    return true;
                }, "OpenBed: skip after max attempts");
                return true;
            }

            Task_CloseMenu.Enqueue();
            var nextAttempt = attempt + 1;
            Plugin.TaskManager.Enqueue(() =>
            {
                Enqueue(patch, bedNumber, nextAttempt);
                return true;
            }, "OpenBed: retry after correction");
            return true;
        }, $"OpenBed: verify prompt (bed {bedNumber})");
    }
}
