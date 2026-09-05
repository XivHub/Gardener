using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Scheduler.Tasks;
using XivHubPluginKit.Inventory;

namespace Gardener.Scheduler;

/// <summary>Which automation a run is carrying out. <see cref="Plan"/> exists so the shape is settled
/// now; its caller lands in a later phase, and <see cref="SchedulerMain.EnablePlugin"/> refuses to
/// start it until then rather than silently running a sweep with nothing behind it.</summary>
public enum SweepKind
{
    Tend,
    Fertilize,
    Harvest,
    Plan,
}

/// <summary>
/// Frame-driven state machine (ICE/SealHunter <c>SchedulerMain</c> pattern): top-of-tick guards run
/// every frame, and the per-state dispatch below only advances once <c>TaskManager.NumQueuedTasks</c>
/// is 0. One patch, one sweep kind, one bed at a time — the worklist for a sweep always comes from
/// <see cref="GardenMemory.Read"/>, never from a menu, so a menu is only ever opened to act.
/// </summary>
public static class SchedulerMain
{
    public static GardenerState State = GardenerState.Idle;

    public static SweepKind? CurrentKind;
    public static Patch? CurrentPatch;
    public static readonly Queue<int> Worklist = new();
    public static int? CurrentBedNumber;

    /// <summary>Player position when the current run started; <see cref="GardenerGuard.PlayerMovedFrom"/>
    /// measures against this, never against the previous frame's position.</summary>
    public static Vector3 RunOrigin;

    public static int TendedCount;
    public static int HarvestedCount;
    public static int FertilizedCount;
    public static int SkippedCount;

    /// <summary>How many stage-4 beds the current run found with no <c>Harvest</c> entry — the direct
    /// evidence on whether stage 4 means harvestable.</summary>
    public static int NoHarvestOfferedCount;

    private static GardenerState resumeState = GardenerState.OpeningBed;

    static SchedulerMain()
    {
        CropChatState.Classified += OnCropChatClassified;
    }

    public static bool Running => State != GardenerState.Idle;

    public static bool EnablePlugin(SweepKind kind, Patch patch)
    {
        if (State != GardenerState.Idle)
        {
            ActivityLog.Warn_("Already running.");
            return false;
        }

        if (kind == SweepKind.Plan)
        {
            Plugin.ChatGui.PrintError($"[Gardener] {kind} sweeps are not implemented yet.");
            return false;
        }

        if (GardenerGuard.BlockingReason() is { } reason)
        {
            Plugin.ChatGui.PrintError($"[Gardener] {reason}");
            return false;
        }

        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        if (position is not { } pos)
        {
            Plugin.ChatGui.PrintError("[Gardener] Player position unavailable.");
            return false;
        }

        // Refuse to start a harvest sweep at all when there is nowhere to put the produce, rather
        // than discovering that mid-sweep on whichever bed happens to fill the bag.
        if (kind == SweepKind.Harvest && InventoryScan.FreeSlotsInBag() < 2)
        {
            Plugin.ChatGui.PrintError("[Gardener] Need at least 2 free inventory slots to harvest.");
            return false;
        }

        // Same shape as the harvest guard above: refuse the whole sweep up front rather than
        // opening every bed only to fail identically on each one.
        if (kind == SweepKind.Fertilize && !HasAnyFertilizer())
        {
            Plugin.ChatGui.PrintError("[Gardener] No fertilizer in the bag.");
            return false;
        }

        CurrentKind = kind;
        CurrentPatch = patch;
        RunOrigin = pos;
        CurrentBedNumber = null;
        TendedCount = 0;
        HarvestedCount = 0;
        FertilizedCount = 0;
        SkippedCount = 0;
        NoHarvestOfferedCount = 0;
        Worklist.Clear();

        State = GardenerState.Scanning;
        ActivityLog.Good_($"Started {kind} on {patch.Kind} patch.");
        return true;
    }

    public static bool DisablePlugin()
    {
        Plugin.TaskManager.Abort();
        State = GardenerState.Idle;
        CurrentKind = null;
        CurrentPatch = null;
        CurrentBedNumber = null;
        Worklist.Clear();
        ActivityLog.Warn_("Stopped.", chat: false);
        return true;
    }

    public static void Tick()
    {
        Plugin.Telemetry?.Snapshot(BuildSnapshot);

        if (State == GardenerState.Idle)
            return;

        // --- Top-of-tick guards (every frame) ---

        if (!GardenerGuard.IsScreenReady())
        {
            EnterPause();
            return;
        }

        if (!GardenerGuard.InHousingTerritory())
        {
            ActivityLog.Warn_("Stopped: left the housing territory.");
            DisablePlugin();
            return;
        }

        if (Plugin.C.StopIfPlayerMoves && GardenerGuard.PlayerMovedFrom(RunOrigin, Plugin.C.MoveAbortDistance))
        {
            ActivityLog.Warn_("Stopped: moved away from where the sweep started.");
            DisablePlugin();
            return;
        }

        // EnvironmentBlockingReason, not BlockingReason: the latter also folds in whether the player
        // is "occupied", and Gardener's own bed interaction sets that same condition flag for as long
        // as a bed menu it opened stays open — checking it here would stop a sweep on its own working
        // state. See GardenerGuard.OccupiedBlockingReason.
        if (GardenerGuard.EnvironmentBlockingReason() is { } reason)
        {
            ActivityLog.Warn_($"Stopped: {reason}");
            DisablePlugin();
            return;
        }

        // Resume from a transient pause (loading screen, cutscene) once the world is interactive
        // again.
        if (State == GardenerState.PausedForPlayer)
        {
            State = resumeState;
            return;
        }

        // --- Per-state dispatch (only when nothing is queued) ---

        if (Plugin.TaskManager.NumQueuedTasks != 0)
            return;

        switch (State)
        {
            case GardenerState.Scanning:
                RunScanning();
                break;
            case GardenerState.OpeningBed:
                RunOpeningBed();
                break;
            case GardenerState.Acting:
                RunActing();
                break;
            case GardenerState.ClosingMenu:
                RunClosingMenu();
                break;
            case GardenerState.Done:
                RunDone();
                break;
            case GardenerState.Error:
                RunError();
                break;
        }
    }

    private static void RunScanning()
    {
        var states = GardenMemory.Read(CurrentPatch!);
        IEnumerable<int> beds = CurrentKind switch
        {
            SweepKind.Tend => states.Where(s => !s.IsEmpty).Select(s => s.BedNumber),
            SweepKind.Harvest => states.Where(s => s.Maturity == Maturity.MatureCandidate).Select(s => s.BedNumber),
            // Every occupied bed is visited; Task_Fertilize itself applies the cooldown and
            // FertilizeOnlyGrowing skips per bed, the same split Tend uses.
            SweepKind.Fertilize => states.Where(s => !s.IsEmpty).Select(s => s.BedNumber),
            _ => Enumerable.Empty<int>(),
        };

        foreach (var bed in beds.OrderBy(b => b))
            Worklist.Enqueue(bed);

        if (Worklist.Count == 0)
        {
            ActivityLog.Notify($"Nothing to {CurrentKind.ToString()!.ToLowerInvariant()} on {CurrentPatch!.Kind} patch.");
            State = GardenerState.Done;
            return;
        }

        State = GardenerState.OpeningBed;
    }

    private static void RunOpeningBed()
    {
        if (Worklist.Count == 0)
        {
            State = GardenerState.Done;
            return;
        }

        CurrentBedNumber = Worklist.Dequeue();
        Task_OpenBed.Enqueue(CurrentPatch!, CurrentBedNumber.Value);
    }

    private static void RunActing()
    {
        switch (CurrentKind)
        {
            case SweepKind.Tend:
                Task_Tend.Enqueue();
                break;
            case SweepKind.Harvest:
                Task_Harvest.Enqueue();
                break;
            case SweepKind.Fertilize:
                Task_Fertilize.Enqueue();
                break;
            default:
                // EnablePlugin already refuses Plan, so reaching here means that guard was bypassed.
                // Fail loud rather than silently doing nothing to the bed that is open.
                Plugin.Logger.Warning($"[Gardener] Acting reached with unsupported sweep kind {CurrentKind}");
                State = GardenerState.Error;
                break;
        }
    }

    private static void RunClosingMenu()
    {
        Task_CloseMenu.Enqueue();
        Plugin.TaskManager.Enqueue(() =>
        {
            State = GardenerState.OpeningBed;
            return true;
        }, "Sweep: next bed");
    }

    private static void RunDone()
    {
        var kind = CurrentKind;
        var summary = kind switch
        {
            SweepKind.Tend => $"Tend complete: {TendedCount} tended, {SkippedCount} skipped.",
            SweepKind.Harvest => $"Harvest complete: {HarvestedCount} harvested, {NoHarvestOfferedCount} " +
                                  $"stage-4 with no Harvest entry, {SkippedCount} skipped.",
            SweepKind.Fertilize => $"Fertilize complete: {FertilizedCount} fertilized, {SkippedCount} skipped.",
            _ => "Sweep complete.",
        };
        ActivityLog.Good_(summary);
        DisablePlugin();
    }

    private static void RunError()
    {
        Plugin.Logger.Warning("[Gardener] scheduler entered Error state; stopping.");
        DisablePlugin();
    }

    /// <summary>Whether the bag holds any item <see cref="GardeningItems.Fertilizers"/> names —
    /// scanned fresh rather than cached, since this only ever runs once, right before a fertilize
    /// sweep starts.</summary>
    private static bool HasAnyFertilizer()
    {
        var ids = GardeningItems.Fertilizers;
        return InventoryScan.ScanContainer(InventoryType.Inventory1).Any(s => ids.Contains(s.ItemId))
            || InventoryScan.ScanContainer(InventoryType.Inventory2).Any(s => ids.Contains(s.ItemId))
            || InventoryScan.ScanContainer(InventoryType.Inventory3).Any(s => ids.Contains(s.ItemId))
            || InventoryScan.ScanContainer(InventoryType.Inventory4).Any(s => ids.Contains(s.ItemId));
    }

    /// <summary>
    /// Attributes a classified <c>TALK_*</c> chat line to whichever bed this scheduler currently has
    /// open — the one place a chat line can be tied to a specific bed with certainty, since an
    /// interaction outside of a sweep gives this plugin no addon or event to key off. Lines that
    /// arrive with no bed open are still classified and buffered by <see cref="CropChatState"/>, just
    /// never attributed here.
    /// </summary>
    private static void OnCropChatClassified(CropChatLine line)
    {
        if (CurrentPatch is not { } patch || CurrentBedNumber is not { } bedNumber)
            return;

        // The two open questions the chat sentences settle, each read straight off the live DataMap
        // value alongside the sentence rather than guessed: whether Value3/Value4 ever carry a wilt
        // flag (TalkDepressed with both still 0 is the negative result that closes that search — a
        // non-zero byte here is already caught and snapshotted by GardenMemory's own anomaly latch),
        // and whether stage 4 (Maturity.MatureCandidate) means harvestable (settled by the Stage4 and
        // HarvestOffered calibration series converging over time, not by any one observation — the
        // MatureCandidate naming stays until they do).
        foreach (var state in GardenMemory.Read(patch))
        {
            if (state.BedNumber != bedNumber)
                continue;

            if (line.Key == MenuKey.TalkDepressed && state.Value3 == 0 && state.Value4 == 0)
                Plugin.Logger.Information(
                    $"[Gardener] {patch.Key} bed {bedNumber}: TALK_DEPRESSED with Value3=0 Value4=0; " +
                    "DataMap carries no wilt flag here.");

            if (line.Key == MenuKey.TalkRipe)
                Plugin.Logger.Information(
                    $"[Gardener] {patch.Key} bed {bedNumber}: TALK_RIPE observed at stage {state.Stage} " +
                    $"(Maturity={state.Maturity}).");

            break;
        }

        GardenJournal.ReconcileCropObservation(patch.Key, bedNumber, line.Key, line.At);
    }

    private static void EnterPause()
    {
        if (State is GardenerState.Idle or GardenerState.PausedForPlayer)
            return;

        resumeState = State;
        Plugin.TaskManager.Abort();
        State = GardenerState.PausedForPlayer;
    }

    private static string BuildSnapshot()
    {
        var pos = Plugin.ObjectTable.LocalPlayer?.Position ?? default;
        return $"state={State} kind={CurrentKind?.ToString() ?? "-"} patch={CurrentPatch?.Key ?? "-"} " +
               $"bed={CurrentBedNumber?.ToString() ?? "-"} worklist={Worklist.Count} tended={TendedCount} " +
               $"harvested={HarvestedCount} fertilized={FertilizedCount} skipped={SkippedCount} " +
               $"noHarvestOffered={NoHarvestOfferedCount} " +
               $"pos=({pos.X:0},{pos.Y:0},{pos.Z:0}) queued={Plugin.TaskManager.NumQueuedTasks}";
    }
}
