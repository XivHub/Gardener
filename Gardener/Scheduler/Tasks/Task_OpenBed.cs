using System;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Localization;

namespace Gardener.Scheduler.Tasks;

/// <summary>
/// Interacts with the bed <see cref="BedTargeting"/> predicts for a bed number and proves it is the
/// right one before anything else touches it. This is the only place in the scheduler that ever opens
/// a bed menu, and the only place that ever acts on one without first reading its "Nth Bed" prompt
/// back: never act on an unparsed or mismatched prompt. <paramref name="onOpened"/> and
/// <paramref name="onGiveUp"/> default to the sweep's own state transitions (<see cref="GardenerState.Acting"/>
/// and <see cref="GardenerState.OpeningBed"/>), so every sweep caller is unchanged; <see cref="Task_RemoveCrop"/>
/// supplies its own instead, since it drives one bed outside the sweep worklist entirely.
/// </summary>
public static class Task_OpenBed
{
    // Bounded retries per bed per sweep — the prompt disagreeing with the map three times running
    // means something is wrong with this bed specifically, not a one-off correction worth chasing.
    private const int MaxAttemptsPerBed = 3;

    public static void Enqueue(Patch patch, int bedNumber, Action? onOpened = null, Action? onGiveUp = null, int attempt = 1)
    {
        onOpened ??= () => SchedulerMain.State = GardenerState.Acting;
        onGiveUp ??= () => SchedulerMain.State = GardenerState.OpeningBed;

        var tm = Plugin.TaskManager;
        var targetEntityId = 0u;

        tm.Enqueue(() =>
        {
            var predicted = BedTargeting.Predict(patch, bedNumber);
            if (predicted is not { } entityId)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.OpenBed_NoPredictedEntity, patch.Key, Formats.Number(bedNumber)),
                    Strings.OpenBed_NoPredictedEntityChat);
                SchedulerMain.SkippedCount++;
                onGiveUp();
                return null;
            }

            var obj = Plugin.ObjectTable.SearchByEntityId(entityId);
            if (obj is null)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.OpenBed_EntityGone, patch.Key, Formats.Number(bedNumber), entityId.ToString("X8")),
                    Strings.OpenBed_EntityGoneChat);
                SchedulerMain.SkippedCount++;
                onGiveUp();
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
                    ActivityLog.SkippedBed(bedNumber,
                        Loc.Format(Strings.OpenBed_TargetSystemUnavailable, patch.Key, Formats.Number(bedNumber)),
                        Strings.OpenBed_TargetSystemUnavailableChat);
                    SchedulerMain.SkippedCount++;
                    onGiveUp();
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
                // ever opened. Terminal for this bed: complete the step rather than requesting a
                // retry, matching every other terminal branch below.
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.OpenBed_MenuNeverOpened, patch.Key, Formats.Number(bedNumber)),
                    Strings.OpenBed_MenuNeverOpenedChat);
                SchedulerMain.SkippedCount++;
                onGiveUp();
                return true;
            }

            var bedPatch = GardenMenuText.ParseBedPatch(select.Text);
            if (bedPatch is not { } bp)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.OpenBed_PromptUnparsed, patch.Key, Formats.Number(bedNumber), select.Text),
                    Strings.OpenBed_PromptUnparsedChat);
                SchedulerMain.SkippedCount++;
                Task_CloseMenu.Enqueue();
                Plugin.TaskManager.Enqueue(() =>
                {
                    onGiveUp();
                    return true;
                }, "OpenBed: skip after unparseable prompt");
                return true;
            }

            if (bp.Bed == bedNumber)
            {
                BedTargeting.MarkVerified(patch, bedNumber);
                onOpened();
                return true;
            }

            // Mismatch: correct the map from the observed pairing and retry, bounded.
            BedTargeting.Confirm(patch, targetEntityId, bp.Bed);

            if (attempt >= MaxAttemptsPerBed)
            {
                ActivityLog.SkippedBed(bedNumber,
                    Loc.Format(Strings.OpenBed_GaveUpAfterAttempts, patch.Key, Formats.Number(bedNumber), Formats.Number(attempt)),
                    Strings.OpenBed_GaveUpAfterAttemptsChat);
                SchedulerMain.SkippedCount++;
                Task_CloseMenu.Enqueue();
                Plugin.TaskManager.Enqueue(() =>
                {
                    onGiveUp();
                    return true;
                }, "OpenBed: skip after max attempts");
                return true;
            }

            Task_CloseMenu.Enqueue();
            var nextAttempt = attempt + 1;
            Plugin.TaskManager.Enqueue(() =>
            {
                Enqueue(patch, bedNumber, onOpened, onGiveUp, nextAttempt);
                return true;
            }, "OpenBed: retry after correction");
            return true;
        }, $"OpenBed: verify prompt (bed {bedNumber})");
    }
}
