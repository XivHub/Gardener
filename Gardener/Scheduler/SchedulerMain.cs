using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;
using Gardener.Scheduler.Tasks;
using XivHubPluginKit.Inventory;

namespace Gardener.Scheduler;

/// <summary>Which automation a run is carrying out. <see cref="Plan"/> is the odd one out: its worklist
/// comes from <see cref="SchedulerMain.PendingPlan"/> rather than a live <see cref="GardenMemory"/>
/// scan, so <see cref="SchedulerMain.EnablePlugin"/> refuses to start it when nothing is pending.</summary>
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
/// is 0. One patch, one sweep kind, one bed at a time — a menu is only ever opened to act, never to
/// build the worklist, which for every kind but <see cref="SweepKind.Plan"/> comes straight from
/// <see cref="GardenMemory.Read"/> and for Plan comes from <see cref="PendingPlan"/> instead.
/// </summary>
public static class SchedulerMain
{
    public static GardenerState State = GardenerState.Idle;

    public static SweepKind? CurrentKind;
    public static Patch? CurrentPatch;
    public static readonly Queue<int> Worklist = new();
    public static int? CurrentBedNumber;

    // A TALK_* line the player produced by interacting with a bed themselves, waiting for the bed menu
    // that interaction opened to become readable so it can be tied to a bed number.
    private static CropChatLine? pendingManualLine;

    // How long a hand-interaction's chat line stays eligible for attribution. Long enough to cover the
    // menu opening a few frames after the line, short enough that a line can never be attached to a
    // different bed the player opens afterwards.
    private static readonly TimeSpan ManualObservationWindow = TimeSpan.FromSeconds(5);

    /// <summary>Player position when the current run started; <see cref="GardenerGuard.PlayerMovedFrom"/>
    /// measures against this, never against the previous frame's position.</summary>
    public static Vector3 RunOrigin;

    public static int TendedCount;
    public static int HarvestedCount;
    public static int FertilizedCount;
    public static int PlantedCount;
    public static int SkippedCount;

    /// <summary>The plan a <see cref="SweepKind.Plan"/> sweep runs, set by the Plan tab immediately
    /// before calling <see cref="EnablePlugin"/> — Plan is the one sweep kind whose worklist is not
    /// derived from <see cref="GardenMemory"/> alone, so it needs a way in that the two-argument
    /// <c>EnablePlugin(kind, patch)</c> call every other sweep shares does not carry.</summary>
    public static Planner.LayoutPlan? PendingPlan;

    private static readonly Dictionary<int, Planner.PlantStep> planStepsByBed = new();

    /// <summary>How many stage-4 beds the current run found with no <c>Harvest</c> entry — the direct
    /// evidence on whether stage 4 means harvestable.</summary>
    public static int NoHarvestOfferedCount;

    private static GardenerState resumeState = GardenerState.OpeningBed;

    static SchedulerMain()
    {
        CropChatState.Classified += OnCropChatClassified;
    }

    public static bool Running => State != GardenerState.Idle;

    // The harvest pre-flight guard's own threshold, named rather than inlined twice: once in the
    // check below, once in Scheduler_NeedFreeSlotsToHarvest's {0}.
    private const int MinFreeSlotsToHarvest = 2;

    public static bool EnablePlugin(SweepKind kind, Patch patch)
    {
        if (State != GardenerState.Idle)
        {
            ActivityLog.Warn_(Strings.Scheduler_AlreadyRunning);
            return false;
        }

        // Plan carries its worklist in PendingPlan rather than deriving it from GardenMemory, so it
        // needs its own up-front check: nothing to run is refused here, the same shape as the harvest
        // and fertilize guards below, rather than discovered bed by bed once the sweep has started.
        if (kind == SweepKind.Plan && PendingPlan is not { Steps.Count: > 0 })
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, Strings.Scheduler_NoPlanToRun));
            return false;
        }

        if (GardenerGuard.BlockingReason() is { } reason)
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, reason));
            return false;
        }

        // Named-patch reach, on top of BlockingReason's own "some patch is close enough": a plot with
        // two patches can pass that check on the near one while the far one — the one this call was
        // actually asked to run — is still out of reach.
        if (GardenerGuard.ReachBlockingReason(patch) is { } reachReason)
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, reachReason));
            return false;
        }

        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        if (position is not { } pos)
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, Strings.Guard_PlayerPositionUnavailable));
            return false;
        }

        // Refuse to start a harvest sweep at all when there is nowhere to put the produce, rather
        // than discovering that mid-sweep on whichever bed happens to fill the bag.
        if (kind == SweepKind.Harvest && InventoryScan.FreeSlotsInBag() < MinFreeSlotsToHarvest)
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix,
                Loc.Format(Strings.Scheduler_NeedFreeSlotsToHarvest, Formats.Number(MinFreeSlotsToHarvest))));
            return false;
        }

        // Same shape as the harvest guard above: refuse the whole sweep up front rather than
        // opening every bed only to fail identically on each one.
        if (kind == SweepKind.Fertilize && !HasAnyFertilizer())
        {
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, Strings.Scheduler_NoFertilizerInBag));
            return false;
        }

        CurrentKind = kind;
        CurrentPatch = patch;
        RunOrigin = pos;
        CropChatState.CaptureUnclassified = true;
        CurrentBedNumber = null;
        TendedCount = 0;
        HarvestedCount = 0;
        FertilizedCount = 0;
        PlantedCount = 0;
        SkippedCount = 0;
        NoHarvestOfferedCount = 0;
        Worklist.Clear();
        planStepsByBed.Clear();

        State = GardenerState.Scanning;
        var verb = GameWords.Action(kind);
        ActivityLog.Good_(Loc.Format(Strings.Scheduler_Started, verb, patch.Kind),
            chatMessage: Loc.Format(Strings.Scheduler_StartedChat, verb, patch.Kind));
        return true;
    }

    /// <summary>Stops whatever is running. <paramref name="reason"/> is the player-facing sentence
    /// naming why, printed to chat as "Stopped: {reason}"; null (a plain Stop-button click with no
    /// guard behind it, a logout, plugin unload) stays in the Log tab only, since nothing chose to
    /// stop the run on the player's behalf that isn't already about to be said some other way.</summary>
    public static bool DisablePlugin(string? reason = null)
    {
        Plugin.TaskManager.Abort();
        State = GardenerState.Idle;
        CropChatState.CaptureUnclassified = false;
        CurrentKind = null;
        CurrentPatch = null;
        CurrentBedNumber = null;
        Worklist.Clear();
        PendingPlan = null;
        planStepsByBed.Clear();
        // A guard trip or the Stop button can both land here mid-interaction, with a bed menu,
        // planting dialog or item context menu still open from whatever step Abort() just cut off.
        GardeningUiCleanup.CloseAll();
        if (reason is not null)
            ActivityLog.Warn_(Loc.Format(Strings.Scheduler_StoppedWithReason, reason));
        else
            ActivityLog.Warn_(Strings.Scheduler_StoppedNoReason, chat: false);
        return true;
    }

    public static void Tick()
    {
        Plugin.Telemetry?.Snapshot(BuildSnapshot);

        AttributeManualObservation();

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
            DisablePlugin(Strings.Scheduler_LeftHousingArea);
            return;
        }

        if (Plugin.C.StopIfPlayerMoves && GardenerGuard.PlayerMovedFrom(RunOrigin, Plugin.C.MoveAbortDistance))
        {
            DisablePlugin(Strings.Scheduler_MovedAwayFromPatch);
            return;
        }

        // EnvironmentBlockingReason, not BlockingReason: the latter also folds in whether the player
        // is "occupied", and Gardener's own bed interaction sets that same condition flag for as long
        // as a bed menu it opened stays open — checking it here would stop a sweep on its own working
        // state. See GardenerGuard.OccupiedBlockingReason.
        if (GardenerGuard.EnvironmentBlockingReason() is { } reason)
        {
            DisablePlugin(reason);
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
        if (CurrentKind == SweepKind.Plan)
        {
            RunScanningPlan();
            return;
        }

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
            // Key selection rather than lower-casing a shared verb string: CurrentKind is Tend,
            // Harvest or Fertilize here (Plan already branched off above), and none of the three
            // player-facing verbs is safe to case-fold in every language.
            var (log, chat) = CurrentKind switch
            {
                SweepKind.Tend => (Strings.Scheduler_NothingToTend, Strings.Scheduler_NothingToTendChat),
                SweepKind.Harvest => (Strings.Scheduler_NothingToHarvest, Strings.Scheduler_NothingToHarvestChat),
                SweepKind.Fertilize => (Strings.Scheduler_NothingToFertilize, Strings.Scheduler_NothingToFertilizeChat),
                _ => throw new ArgumentOutOfRangeException(nameof(CurrentKind), CurrentKind, "unhandled SweepKind in RunScanning"),
            };
            ActivityLog.Notify(Loc.Format(log, CurrentPatch!.Kind), chatMessage: Loc.Format(chat, CurrentPatch!.Kind));
            State = GardenerState.Done;
            return;
        }

        State = GardenerState.OpeningBed;
    }

    /// <summary>
    /// Re-checks <see cref="PendingPlan"/> against a fresh <see cref="GardenMemory.Read"/> right before
    /// running it — the plan may be minutes old by the time the player clicked Run plan, and
    /// <see cref="Planner.CrossPlanner.Verify"/> is exactly the check that already caught this once,
    /// at the moment the plan was built. Anything it downgrades here was already logged into the
    /// plan's own <c>Warnings</c>; this only decides what actually gets queued.
    /// </summary>
    private static void RunScanningPlan()
    {
        var plan = PendingPlan!;
        Planner.CrossPlanner.Verify(plan, CurrentPatch!);

        planStepsByBed.Clear();
        foreach (var step in plan.Steps)
            planStepsByBed[step.BedNumber] = step;

        foreach (var bed in plan.Steps.Select(s => s.BedNumber))
            Worklist.Enqueue(bed);

        if (Worklist.Count == 0)
        {
            ActivityLog.Warn_(Strings.Scheduler_PlanNothingLeft,
                chatMessage: Strings.Scheduler_PlanNothingLeftChat);
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
            case SweepKind.Plan:
                var step = planStepsByBed[CurrentBedNumber!.Value];
                Task_Plant.Enqueue(step.SeedRow, step.SoilItemId);
                break;
            default:
                // Every SweepKind has a case above; reaching here means a new one was added without one.
                Plugin.Logger.Warning($"[Gardener] Acting reached with unsupported sweep kind {CurrentKind}");
                State = GardenerState.Error;
                break;
        }
    }

    private static void RunClosingMenu()
    {
        Task_CloseMenu.Enqueue();
        // The pause between beds: a bigger boundary than one step to the next within a bed, and where
        // a stale addon from the previous bed is most likely to still be closing.
        Plugin.TaskManager.EnqueueDelay(SchedulerPacing.BedDelay());
        Plugin.TaskManager.Enqueue(() =>
        {
            State = GardenerState.OpeningBed;
            return true;
        }, "Sweep: next bed");
    }

    private static void RunDone()
    {
        var kind = CurrentKind;
        var skipped = CountFragment(SkippedCount, Strings.Scheduler_CountSkipped_One, Strings.Scheduler_CountSkipped_Other);
        string logSummary, chatSummary;
        switch (kind)
        {
            case SweepKind.Tend:
            {
                var tended = CountFragment(TendedCount, Strings.Scheduler_CountTended_One, Strings.Scheduler_CountTended_Other);
                logSummary = Loc.Format(Strings.Scheduler_Complete, GameWords.Action(kind.Value), string.Join(", ", tended, skipped));
                chatSummary = Loc.Format(Strings.Scheduler_Finished, GameWords.Action(kind.Value), string.Join(", ", tended, skipped));
                break;
            }
            case SweepKind.Harvest:
            {
                var harvested = CountFragment(HarvestedCount, Strings.Scheduler_CountHarvested_One, Strings.Scheduler_CountHarvested_Other);
                // Chat drops the stage-4/no-harvest-entry detail: that count is calibration evidence
                // for GardenMemory, not something the player can act on.
                var noHarvestEntry = CountFragment(NoHarvestOfferedCount, Strings.Scheduler_CountNoHarvestEntry_One, Strings.Scheduler_CountNoHarvestEntry_Other);
                logSummary = Loc.Format(Strings.Scheduler_Complete, GameWords.Action(kind.Value), string.Join(", ", harvested, noHarvestEntry, skipped));
                chatSummary = Loc.Format(Strings.Scheduler_Finished, GameWords.Action(kind.Value), string.Join(", ", harvested, skipped));
                break;
            }
            case SweepKind.Fertilize:
            {
                var fertilized = CountFragment(FertilizedCount, Strings.Scheduler_CountFertilized_One, Strings.Scheduler_CountFertilized_Other);
                logSummary = Loc.Format(Strings.Scheduler_Complete, GameWords.Action(kind.Value), string.Join(", ", fertilized, skipped));
                chatSummary = Loc.Format(Strings.Scheduler_Finished, GameWords.Action(kind.Value), string.Join(", ", fertilized, skipped));
                break;
            }
            case SweepKind.Plan:
            {
                var planted = CountFragment(PlantedCount, Strings.Scheduler_CountPlanted_One, Strings.Scheduler_CountPlanted_Other);
                logSummary = Loc.Format(Strings.Scheduler_Complete, GameWords.Action(kind.Value), string.Join(", ", planted, skipped));
                chatSummary = Loc.Format(Strings.Scheduler_Finished, GameWords.Action(kind.Value), string.Join(", ", planted, skipped));
                break;
            }
            default:
                logSummary = Strings.Scheduler_GenericComplete;
                chatSummary = Strings.Scheduler_GenericFinished;
                break;
        }
        ActivityLog.Good_(logSummary, chatMessage: chatSummary);
        DisablePlugin();
    }

    private static string CountFragment(int count, string oneTemplate, string otherTemplate) =>
        Loc.Format(count == 1 ? oneTemplate : otherTemplate, Formats.Number(count));

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
    /// open. A line that arrives with no sweep bed open came from the player interacting by hand, and
    /// is handed to <see cref="AttributeManualObservation"/> rather than dropped — the bed menu that
    /// interaction opened names the bed just as well as a sweep's own does.
    /// </summary>
    private static void OnCropChatClassified(CropChatLine line)
    {
        if (CurrentPatch is not { } patch || CurrentBedNumber is not { } bedNumber)
        {
            pendingManualLine = line;
            AttributeManualObservation();
            return;
        }

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

    /// <summary>Attributes a hand-interaction's <c>TALK_*</c> line to the bed whose menu that
    /// interaction opened. The line and the menu do not arrive in a fixed order — the chat message can
    /// land a frame before the addon is ready — so the line is held and retried each tick until the
    /// menu can be read or <see cref="ManualObservationWindow"/> passes.</summary>
    private static void AttributeManualObservation()
    {
        if (pendingManualLine is not { } line)
            return;

        if (DateTimeOffset.UtcNow - line.At > ManualObservationWindow)
        {
            pendingManualLine = null;
            return;
        }

        if (OpenBedFromMenu() is not { } open)
            return;

        pendingManualLine = null;
        Plugin.Logger.Information(
            $"[Gardener] {open.Patch.Key} bed {open.BedNumber}: {line.Key} from a hand interaction.");
        GardenJournal.ReconcileCropObservation(open.Patch.Key, open.BedNumber, line.Key, line.At);
    }

    /// <summary>The patch and bed number of the bed menu open right now, from the menu's own "Nth Bed"
    /// prompt plus the bed <c>EventObj</c> the player is targeting — the same two facts
    /// <see cref="Task_OpenBed"/> reads back during a sweep, read outside one. Falls back to the only
    /// discovered patch when nothing is targeted, since a plot with one patch leaves nothing to
    /// confuse it with. Null when no menu is open, the prompt does not parse, or the patch cannot be
    /// pinned down.</summary>
    private static (Patch Patch, int BedNumber)? OpenBedFromMenu()
    {
        var select = AddonFinder.SelectString.FirstOrDefault();
        if (select is not { IsAddonReady: true })
            return null;

        if (GardenMenuText.ParseBedPatch(select.Text) is not { } bedPatch)
            return null;

        var patches = PatchDiscovery.Patches;
        var patch = Plugin.TargetManager.Target is { } target
            ? patches.FirstOrDefault(p => p.Beds.Any(b => b.EntityId == target.EntityId))
            : patches.Count == 1 ? patches[0] : null;

        return patch is null ? null : (patch, bedPatch.Bed);
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
               $"harvested={HarvestedCount} fertilized={FertilizedCount} planted={PlantedCount} skipped={SkippedCount} " +
               $"noHarvestOffered={NoHarvestOfferedCount} " +
               $"pos=({pos.X:0},{pos.Y:0},{pos.Z:0}) queued={Plugin.TaskManager.NumQueuedTasks}";
    }
}
