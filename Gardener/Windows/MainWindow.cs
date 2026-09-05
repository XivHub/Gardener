using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Planner;
using Gardener.Scheduler;
using Gardener.Scheduler.Tasks;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class MainWindow : Window, IDisposable
    {
        // Staged hours for each bed's "planted about N hours ago" control, keyed the same way the
        // journal itself is (patch + bed, never a character) so the widget survives a redraw.
        private static readonly Dictionary<(string PatchKey, int BedNumber), int> pendingEstimateHours = new();

        // Whether the goal picker is open over the current goal rather than the step list — toggled
        // by "Pick something else" / a fresh goal choice / "Clear goal". Search text is separate so it
        // survives across draws without being tied to any one goal.
        private static bool goalPickerOpen;
        private static string goalPickerSearch = string.Empty;

        // GoalRoute.Solve's own fixpoint relaxation is cheap but not free, and both the current goal
        // and the "quickest from what you hold" empty-state picks call it; cached by reference against
        // SeedInventory.Counts()' own once-a-second cache so neither reruns every frame.
        private static IReadOnlyDictionary<uint, int>? goalPlanHeldCache;
        private static uint goalPlanRowCache;
        private static GoalPlan? goalPlanCache;
        private static IReadOnlyDictionary<uint, int>? quickPicksHeldCache;
        private static List<(uint Row, GoalPlan Plan)> quickPicksCache = new();

        // Housing wards recheck harvest readiness roughly once an hour rather than the instant a
        // plant matures, so a harvest estimate always lags behind the plant's own clock.
        private const string HarvestLagNote =
            "Harvest readiness is checked roughly once an hour, so a bed can stay unlisted for a while after it matures.";

        public MainWindow(Configuration configuration) : base("Gardener###GardenerMain")
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(380, 380),
                MaximumSize = new Vector2(900, 1400),
            };
        }

        public void Dispose() { }

        public override void Draw()
        {
            if (!ImGui.BeginTabBar("##gardenerTabs"))
                return;

            if (ImGui.BeginTabItem("Garden"))
            {
                DrawGardenTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Goal"))
            {
                DrawGoalTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Reminders"))
            {
                DrawRemindersTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Log"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <summary>
        /// Lists discovered patches (already filtered to the plot the player is standing on), built
        /// entirely around the passive <see cref="GardenMemory"/> read: per patch, one row per bed
        /// number <c>1..BedCount()</c> — the game's own "Nth Bed" numbering, not a spatial index —
        /// showing the seed, stage, wilt time and harvest window. A patch whose <see
        /// cref="GardenMemory.Read"/> came back empty renders as "no data for this patch", never as
        /// eight empty beds: those are different facts.
        /// </summary>
        private static void DrawGardenTab()
        {
            DrawSchedulerStatus();
            ImGui.Separator();

            var patches = PatchDiscovery.Patches;
            if (patches.Count == 0)
                ImGui.TextColored(HubStyle.Faint, "No patches discovered. Stand in an outdoor housing plot.");
            else
                ImGui.TextColored(HubStyle.Faint, HarvestLagNote);

            var plot = PatchDiscovery.LastDiagnostics.CurrentPlot;
            var plotText = plot is { } p ? $"plot {p + 1}" : "plot unknown";

            foreach (var patch in patches)
            {
                var houseKey = patch.Key.Split(':')[0];
                ImGui.TextUnformatted($"{patch.Kind} patch, {plotText}");
                ImGui.TextColored(HubStyle.Faint, patch.Key);

                var estateType = GardenJournal.EstateTypeFor(houseKey);
                var accessible = GardenJournal.CharactersWithAccess(houseKey);
                if (estateType is not null || accessible.Count > 0)
                {
                    var estateText = estateType is { } et ? et.ToString() : "unknown";
                    var accessText = accessible.Count > 0 ? string.Join(", ", accessible) : "unknown";
                    ImGui.TextColored(HubStyle.Faint, $"Estate: {estateText}. Reachable by: {accessText}");
                }

                var states = GardenMemory.Read(patch);
                if (states.Count == 0)
                {
                    ImGui.TextColored(HubStyle.Faint, "no data for this patch");
                    ImGui.Spacing();
                    continue;
                }

                var byBed = states.ToDictionary(s => s.BedNumber);
                DrawBedTable(patch, byBed);

                DrawSweepButtons(patch);

                ImGui.Spacing();
            }

            DrawOrphans();

            ImGui.Separator();
            ImGui.TextDisabled("Debug dump");
            if (ImGui.Button("Dump"))
                DebugDump.Run(menu: false);
            ImGui.SameLine();
            if (ImGui.Button("Dump menu"))
                DebugDump.Run(menu: true);

            if (DebugDump.LastDumpPath != null)
            {
                ImGui.TextColored(HubStyle.Faint, DebugDump.LastDumpPath);
                ImGui.SameLine();
                if (ImGui.Button("Copy##copyLastDump"))
                    DebugDump.QueueClipboardCopy();
            }
        }

        /// <summary>
        /// Journal records for the house the player is currently standing in whose patch key is not
        /// among the patches discovery just found — the patch was placed into storage (which freezes
        /// its timers and drops it from discovery entirely) or physically moved; the two look the same
        /// from here. Never lists a record for a house the player is not currently in: without a live
        /// patch list to compare against, "not found" and "not looked at" are indistinguishable, so a
        /// house that was never scanned this visit must not show up here at all.
        /// </summary>
        private static void DrawOrphans()
        {
            if (HouseKey.Current() is not { } house)
                return;

            var houseKey = house.KeyString();
            var livePatchKeys = PatchDiscovery.Patches.Select(p => p.Key);
            var orphans = GardenJournal.Orphans(livePatchKeys)
                .Where(r => r.PatchKey.StartsWith(houseKey + ":", StringComparison.Ordinal))
                .OrderBy(r => r.PatchKey).ThenBy(r => r.BedNumber)
                .ToList();
            if (orphans.Count == 0)
                return;

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Warn, $"Beds recorded here before, not found this visit ({orphans.Count})");
            ImGui.TextColored(HubStyle.Faint,
                "This patch may be in storage, or it may have moved; either way the record stays and its timers are paused. Forget a record to stop tracking it.");
            foreach (var orphan in orphans)
            {
                ImGui.TextUnformatted($"{orphan.PatchKey}, bed {orphan.BedNumber}: {SeedName(orphan.SeedRow)}");
                ImGui.SameLine();
                if (ImGui.Button($"Forget##orphan-{orphan.PatchKey}-{orphan.BedNumber}"))
                    GardenJournal.Remove(orphan.PatchKey, orphan.BedNumber);
            }
        }

        private static string SeedName(ushort row)
        {
            var produceItemId = SeedItems.ProduceItemForRow(row);
            return produceItemId is { } id ? XivHubPluginKit.Inventory.ItemSheet.Name(id) : $"row {row}";
        }

        private static void DrawBedTable(Patch patch, IReadOnlyDictionary<int, BedState> byBed)
        {
            if (!ImGui.BeginTable($"##bedgrid-{patch.Key}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                return;

            ImGui.TableSetupColumn("Bed");
            ImGui.TableSetupColumn("Seed / stage");
            ImGui.TableSetupColumn("Wilt");
            ImGui.TableSetupColumn("Harvest window");
            ImGui.TableHeadersRow();

            for (var bedNumber = 1; bedNumber <= patch.Kind.BedCount(); bedNumber++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(bedNumber.ToString());
                ImGui.TableNextColumn();

                if (!byBed.TryGetValue(bedNumber, out var state) || state.IsEmpty)
                {
                    ImGui.TextColored(HubStyle.Faint, "empty");
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    continue;
                }

                var record = GardenJournal.Get(patch.Key, bedNumber);
                DrawSeedAndStage(state, record);

                ImGui.TableNextColumn();
                DrawWilt(record);

                ImGui.TableNextColumn();
                DrawHarvestWindow(patch.Key, bedNumber, record);
            }

            ImGui.EndTable();

            DrawRemoveCropButtons(patch, byBed.Values.ToList());
        }

        /// <summary>Always visible regardless of what is blocking a new run, so a sweep already in
        /// progress can always be stopped.</summary>
        private static void DrawSchedulerStatus()
        {
            if (SchedulerMain.Running)
            {
                ImGui.TextColored(HubStyle.Warn,
                    $"{SchedulerMain.CurrentKind}: bed {SchedulerMain.CurrentBedNumber?.ToString() ?? "-"}, " +
                    $"{SchedulerMain.Worklist.Count} left ({SchedulerMain.State})");
                ImGui.SameLine();
            }

            if (ImGui.Button("Stop"))
                SchedulerMain.DisablePlugin("you clicked Stop.");
        }

        /// <summary>"Tend all" / "Harvest all" / "Fertilize all" for one patch, each behind
        /// <see cref="Configuration.ConfirmBeforeRun"/>, replaced by <see cref="GardenerGuard.BlockingReason"/>
        /// when a sweep cannot start at all. <see cref="GardenerGuard.PermissionsWarning"/> renders
        /// above them regardless, since it is independent of whether a run can start.</summary>
        private static void DrawSweepButtons(Patch patch)
        {
            if (GardenerGuard.PermissionsWarning() is { } permissionsWarning)
                ImGui.TextColored(HubStyle.Warn, permissionsWarning);

            if (GardenerGuard.BlockingReason() is { } reason)
            {
                ImGui.TextColored(HubStyle.Faint, reason);
                return;
            }

            using (HubStyle.Primary())
            {
                if (ImGui.Button($"Tend all##tend-{patch.Key}"))
                {
                    if (Plugin.C.ConfirmBeforeRun)
                        ImGui.OpenPopup($"Confirm tend##{patch.Key}");
                    else
                        SchedulerMain.EnablePlugin(SweepKind.Tend, patch);
                }
            }
            DrawConfirmPopup(patch, $"Confirm tend##{patch.Key}", "Tend every occupied bed on this patch?", SweepKind.Tend);

            ImGui.SameLine();
            if (ImGui.Button($"Harvest all##harvest-{patch.Key}"))
            {
                if (Plugin.C.ConfirmBeforeRun)
                    ImGui.OpenPopup($"Confirm harvest##{patch.Key}");
                else
                    SchedulerMain.EnablePlugin(SweepKind.Harvest, patch);
            }
            DrawConfirmPopup(patch, $"Confirm harvest##{patch.Key}", "Harvest every mature bed on this patch?", SweepKind.Harvest);

            ImGui.SameLine();
            if (ImGui.Button($"Fertilize all##fertilize-{patch.Key}"))
            {
                if (Plugin.C.ConfirmBeforeRun)
                    ImGui.OpenPopup($"Confirm fertilize##{patch.Key}");
                else
                    SchedulerMain.EnablePlugin(SweepKind.Fertilize, patch);
            }
            DrawConfirmPopup(patch, $"Confirm fertilize##{patch.Key}", "Fertilize every eligible bed on this patch?", SweepKind.Fertilize);

            ImGui.TextColored(HubStyle.Faint,
                $"Bed order verified: {BedTargeting.VerifiedBedCount(patch)}/{patch.Kind.BedCount()}");
        }

        private static void DrawConfirmPopup(Patch patch, string popupId, string message, SweepKind kind)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            ImGui.TextUnformatted(message);
            if (ImGui.Button("Confirm"))
            {
                SchedulerMain.EnablePlugin(kind, patch);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        /// <summary>
        /// The Goal tab: pick a produce, and Gardener works out the cheapest route to it
        /// (<see cref="GoalRoute.Solve"/>) and which step the player is standing on
        /// (<see cref="GoalProgress.Evaluate"/>). Held once per draw rather than cached across frames
        /// beyond <see cref="GetOrSolveGoalPlan"/>'s own reference-equality cache, since ImGui redraws
        /// this whole tab every frame it is open regardless.
        /// </summary>
        private static void DrawGoalTab()
        {
            DrawSchedulerStatus();
            ImGui.Separator();

            var held = SeedInventory.Counts();
            var goalRow = GardenJournal.GoalSeedRow;

            if (goalRow == 0 || goalPickerOpen)
            {
                DrawGoalPicker(goalRow, held);
                return;
            }

            ImGui.TextUnformatted($"Goal: {SeedItems.ProduceName(goalRow)}");
            ImGui.SameLine();
            if (ImGui.Button("Pick something else"))
            {
                goalPickerOpen = true;
                goalPickerSearch = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear goal"))
                GardenJournal.GoalSeedRow = 0;

            var heldGoalCount = held.GetValueOrDefault(goalRow);
            if (heldGoalCount > 0)
            {
                DrawGoalAlreadyMet(goalRow, heldGoalCount);
                return;
            }

            if (SeedTable.Gatherable(goalRow) == true)
            {
                DrawGoalNoCrossbreedNeeded(goalRow);
                return;
            }

            var plan = GetOrSolveGoalPlan(goalRow, held);
            if (plan is null)
            {
                DrawGoalNoRoute(goalRow);
                return;
            }

            DrawGoalPlanBody(plan, held);
        }

        /// <summary><see cref="GoalRoute.Solve"/> runs a fixpoint relaxation over the whole cross
        /// table; cached by (goal row, held-dictionary reference) so it reruns only when the goal
        /// changes or <see cref="SeedInventory.Counts"/> actually recomputes (at most once a second),
        /// never every frame the tab happens to be open.</summary>
        private static GoalPlan? GetOrSolveGoalPlan(uint goalRow, IReadOnlyDictionary<uint, int> held)
        {
            if (goalRow == goalPlanRowCache && ReferenceEquals(held, goalPlanHeldCache))
                return goalPlanCache;

            var plan = GoalRoute.Solve(goalRow, held, Plugin.C.SoilForCross);
            goalPlanRowCache = goalRow;
            goalPlanHeldCache = held;
            goalPlanCache = plan;
            return plan;
        }

        /// <summary>The picker: search plus every outdoor seed by produce name, tagged with how it is
        /// obtained. Doubles as the empty state (no goal set) and the "Pick something else" overlay
        /// (goal set, player wants to change it) — the only difference is the paragraph and quick
        /// picks above the list, shown only in the empty case.</summary>
        private static void DrawGoalPicker(uint currentGoal, IReadOnlyDictionary<uint, int> held)
        {
            if (currentGoal == 0)
            {
                ImGui.TextWrapped("Pick what you want to grow and Gardener works out the steps from what you already hold.");
                ImGui.Spacing();
                DrawQuickPicks(held);
                ImGui.Spacing();
            }

            ImGui.TextUnformatted("Pick a seed");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##goalSearch", "Search", ref goalPickerSearch, 64);

            var rows = Sheets.GardeningSeedSheet
                .Where(s => s.RowId != 0 && !s.IsPlantPotFlowerSeed)
                .Select(s => s.RowId)
                .OrderBy(SeedItems.ProduceName)
                .ToList();

            if (ImGui.BeginChild("##goalPickerList", new Vector2(0, 260), true))
            {
                foreach (var row in rows)
                {
                    var produceName = SeedItems.ProduceName(row);
                    if (goalPickerSearch.Length > 0 &&
                        produceName.IndexOf(goalPickerSearch, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var label = $"{produceName}   {PickerTag(row)}";
                    if (ImGui.Selectable($"{label}##goalpick-{row}", row == currentGoal))
                    {
                        GardenJournal.GoalSeedRow = row;
                        goalPickerOpen = false;
                    }
                }
            }
            ImGui.EndChild();
        }

        /// <summary>"you can buy or gather this" / "crossbreed only" / "no crossbreed data, see
        /// Settings" — the third covers both a row absent from the bundle entirely and one present but
        /// missing its cross data, since either way the picker has nothing to route from.</summary>
        private static string PickerTag(uint row)
        {
            if (SeedTable.Gatherable(row) == true)
                return "you can buy or gather this";
            if (SeedTable.CrossableTargets.Contains(row))
                return "crossbreed only";
            return "no crossbreed data, see Settings";
        }

        /// <summary>The three cheapest cross-only goals reachable from what is already held, ranked by
        /// <see cref="GoalPlan.BestCaseDuration"/> — "quickest" is what the header promises, and
        /// duration is the number that actually answers it. Cached the same way
        /// <see cref="GetOrSolveGoalPlan"/> is, since it runs <see cref="GoalRoute.Solve"/> once per
        /// crossable target.</summary>
        private static void DrawQuickPicks(IReadOnlyDictionary<uint, int> held)
        {
            if (!ReferenceEquals(held, quickPicksHeldCache))
            {
                var found = new List<(uint Row, GoalPlan Plan)>();
                foreach (var row in SeedTable.CrossableTargets)
                {
                    if (SeedTable.Gatherable(row) == true)
                        continue;
                    if (GoalRoute.Solve(row, held, Plugin.C.SoilForCross) is { } plan)
                        found.Add((row, plan));
                }
                quickPicksCache = found.OrderBy(f => f.Plan.BestCaseDuration).Take(3).ToList();
                quickPicksHeldCache = held;
            }

            if (quickPicksCache.Count == 0)
                return;

            ImGui.TextUnformatted("Quickest from what you hold right now:");
            foreach (var (row, _) in quickPicksCache)
            {
                if (ImGui.Button($"{SeedItems.ProduceName(row)}##quickpick-{row}"))
                    GardenJournal.GoalSeedRow = row;
            }
        }

        private static void DrawGoalAlreadyMet(uint goalRow, int heldCount)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextUnformatted($"You already hold {heldCount} {SeedItemName(goalRow)}.");

            var growHours = SeedTable.Grow(goalRow);
            var yieldSoil = SoilFamily.Shroud;
            if (growHours is null)
            {
                ImGui.TextColored(HubStyle.Faint, "Gardener does not know how long this seed takes to grow.");
                return;
            }

            var days = GoalDaysPhrase(growHours.Value);
            var yields = SeedTable.Yields(goalRow);
            var cropText = GoalYieldText(yields?.Crop);
            var seedText = GoalYieldText(yields?.Seed);

            ImGui.TextWrapped(
                $"Plant one in Grade 3 {yieldSoil} Topsoil and harvest in {days} for {cropText} {produceName} and " +
                $"{seedText} seed back.");

            var seedTiers = yields?.Seed;
            if (seedTiers is { Length: > 0 } && seedTiers.All(y => y == seedTiers[0]))
            {
                ImGui.TextWrapped(seedTiers[0] switch
                {
                    0 => "It gives back no seeds when harvested, so plant another from your stock each time.",
                    1 => "It gives back the one seed you planted, at any soil grade, so a bed sustains itself but never multiplies.",
                    _ => $"It gives back {seedTiers[0]} seeds at any soil grade, so a bed of these multiplies over time.",
                });
            }
        }

        private static void DrawGoalNoCrossbreedNeeded(uint goalRow)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextUnformatted($"You do not need to crossbreed {produceName}.");
            var sources = SeedTable.Sources(goalRow);
            ImGui.TextColored(HubStyle.Faint, sources.Count > 0
                ? $"Where: {string.Join("; ", sources)}"
                : "Where: not recorded.");
        }

        private static void DrawGoalNoRoute(uint goalRow)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextWrapped(
                $"Gardener cannot find a way to grow {produceName} from seeds you can buy or gather. Its crossbreed " +
                "data has no parent pair for it, so there is nothing to plan. Open Settings and check Data health.");
        }

        /// <summary>
        /// The step list plus the current step's detail and handoff. <see cref="GoalProgress.Evaluate"/>
        /// needs a live snapshot when the player is at a discovered patch, and the journal's cached
        /// <c>SeedRow</c> / <c>LastSeenStage</c> otherwise — the away-from-garden and never-seen-a-garden
        /// states are named here, above everything else, so the player never reads a stale current-step
        /// guess as if it were live.
        /// </summary>
        private static void DrawGoalPlanBody(GoalPlan plan, IReadOnlyDictionary<uint, int> held)
        {
            var patches = PatchDiscovery.Patches;
            IReadOnlyList<BedState> beds;
            DateTimeOffset? observedAt;
            var atGarden = patches.Count > 0;

            if (atGarden)
            {
                beds = patches.SelectMany(GardenMemory.Read).ToList();
                observedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var records = GardenJournal.AllRecords;
                if (records.Count == 0)
                {
                    ImGui.TextColored(HubStyle.Faint,
                        "Gardener has never seen a garden of yours. Stand at your plot once and it will know which " +
                        "step you are on.");
                    beds = Array.Empty<BedState>();
                    observedAt = null;
                }
                else
                {
                    beds = records
                        .Select(r => new BedState(r.PatchKey, r.BedNumber, r.SeedRow, r.LastSeenStage, 0, 0, r.LastSeenAt))
                        .ToList();
                    observedAt = records.Max(r => r.LastSeenAt);
                    ImGui.TextColored(HubStyle.Faint,
                        $"You are not at the garden. This is what Gardener saw on your last visit, {GoalAgePhrase(observedAt.Value)} ago.");
                }
            }

            var status = GoalProgress.Evaluate(plan, held, beds, observedAt);

            var totalSteps = plan.Steps.Max(s => s.Number);
            var currentNumber = plan.Steps[status.CurrentStepIndex].Number;
            ImGui.TextUnformatted($"Step {currentNumber} of {totalSteps}");
            ImGui.TextColored(HubStyle.Faint,
                $"{totalSteps} steps. About {FormatDays(plan.BestCaseDuration)} if the gamble lands first time. " +
                $"{plan.PeakBeds} beds at the busiest step.");

            foreach (var warning in plan.Warnings)
                ImGui.TextColored(HubStyle.Warn, warning);

            ImGui.Separator();

            var groups = plan.Steps
                .Select((step, index) => (Step: step, Index: index))
                .GroupBy(x => x.Step.Number)
                .OrderBy(g => g.Key);

            foreach (var group in groups)
                DrawGoalStepGroup(group.Key, group.ToList(), status, plan, atGarden, held);
        }

        private static void DrawGoalStepGroup(
            int number, List<(GoalStep Step, int Index)> members, GoalStatus status, GoalPlan plan, bool atGarden,
            IReadOnlyDictionary<uint, int> held)
        {
            var isCurrent = members.Any(m => m.Index == status.CurrentStepIndex);
            var allDone = members.All(m => status.Steps[m.Index].Status == GoalStepStatus.Done);

            ImGui.PushID($"goalstep-{number}");
            // The current step is always forced open — its detail and handoff live inside — since
            // which step is current moves over time as the player makes progress, and ImGui's own
            // FirstUseEver default would only ever expand whichever step happened to be current the
            // very first time this header was drawn. Every other step defaults collapsed once and is
            // then left to the player's own toggle, matching "steps after the current one render
            // collapsed to their title" without fighting a manual expand to look something up.
            if (isCurrent)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            else
                ImGui.SetNextItemOpen(false, ImGuiCond.FirstUseEver);
            var open = ImGui.CollapsingHeader($"Step {number}: {members[0].Step.Title}");

            // Right-aligned against the panel edge rather than trailing the title: a plain SameLine
            // sits the marker hard against however long the step's own title happens to be, so it
            // reads as part of the sentence instead of as a status.
            var marker = isCurrent ? "Now" : allDone ? "Done" : null;
            if (marker is not null)
            {
                var markerWidth = ImGui.CalcTextSize(marker).X;
                ImGui.SameLine(ImGui.GetContentRegionMax().X - markerWidth - ImGui.GetStyle().FramePadding.X * 2f);
                ImGui.TextColored(isCurrent ? HubStyle.Accent : HubStyle.Good, marker);
            }

            if (open)
            {
                foreach (var (step, index) in members)
                    DrawGoalStepBody(step, status.Steps[index], isCurrent, held);

                if (isCurrent)
                    DrawGoalCurrentStepDetail(plan, members, status, atGarden, held);
            }

            ImGui.PopID();
        }

        /// <summary>Body text dims to <see cref="HubStyle.Faint"/> once a step is behind or ahead of
        /// the current one; the current step always reads at full strength, whatever its own status.</summary>
        private static void DrawGoalStepBody(GoalStep step, GoalStepProgress progress, bool isCurrent, IReadOnlyDictionary<uint, int> held)
        {
            var color = isCurrent || progress.Status != GoalStepStatus.NotStarted ? HubStyle.Text : HubStyle.Faint;

            // The goal step text is prose, not labels, and a narrow window otherwise clips it at the
            // right edge with no way to read the rest.
            ImGui.PushTextWrapPos(0f);
            foreach (var line in step.Body)
                ImGui.TextColored(color, line);
            foreach (var note in step.Notes)
                ImGui.TextColored(HubStyle.Faint, note);

            var (text, statusColor) = StatusLine(step, progress, held);
            ImGui.TextColored(statusColor, text);
            ImGui.PopTextWrapPos();
        }

        /// <summary>The exact phrasing from the copy's "Status lines" set, chosen by
        /// <see cref="GoalStepProgress.Status"/> and, for the two growing statuses, by
        /// <see cref="GoalStepProgress.Window"/>'s confidence and how close its bed is to wilting or
        /// withering.</summary>
        private static (string Text, Vector4 Color) StatusLine(GoalStep step, GoalStepProgress progress, IReadOnlyDictionary<uint, int> held)
        {
            switch (progress.Status)
            {
                case GoalStepStatus.Done:
                    var doneText = step switch
                    {
                        ObtainStep o => $"Done. You hold {held.GetValueOrDefault(o.SeedRow)} {SeedItemName(o.SeedRow)}.",
                        CrossStep c => $"Done. You hold {held.GetValueOrDefault(c.TargetRow)} {SeedItemName(c.TargetRow)}.",
                        MultiplyStep m => $"Done. You hold {held.GetValueOrDefault(m.SeedRow)} {SeedItemName(m.SeedRow)}.",
                        _ => "Done.",
                    };
                    return (doneText, HubStyle.Good);

                case GoalStepStatus.AttemptUnresolved:
                    return ($"Bed {progress.BedNumber} is growing. You will know whether it crossed when it is ready to harvest.", HubStyle.Text);

                case GoalStepStatus.CrossedAndGrowing:
                    return GrowingStatusLine(progress);

                default:
                    return (progress.Blocker ?? "Not started.", HubStyle.Text);
            }
        }

        private static (string Text, Vector4 Color) GrowingStatusLine(GoalStepProgress progress)
        {
            var bedNumber = progress.BedNumber;
            var record = progress.PatchKey is { } key && bedNumber is { } bn ? GardenJournal.Get(key, bn) : null;

            if (record is null || progress.Window is not { Confidence: not HarvestConfidence.Unknown } window)
                return ($"Growing in bed {bedNumber}. Gardener does not know when this one is ready. Set when you " +
                       "planted it on the Garden tab.", HubStyle.Text);

            var now = DateTimeOffset.UtcNow;
            var earliest = window.Earliest;
            if (earliest is { } e0 && now >= e0)
                return ($"Ready to harvest in bed {bedNumber}.", HubStyle.Good);

            var wiltsAt = Growth.WiltsAt(record);
            if (wiltsAt is { } wa && now >= wa)
            {
                // Already wilted. Growth.WithersAt is null once the bed reads mature (stage 4, never
                // withers) or wilt data is missing entirely, either of which means there is no real
                // deadline to warn about here.
                if (Growth.WithersAt(record) is { } withersAt && now < withersAt)
                    return ($"Growing in bed {bedNumber}. It has wilted; tend it before it withers.", HubStyle.Bad);
            }
            else if (wiltsAt is { } wiltDeadline)
            {
                var untilWilt = wiltDeadline - now;
                if (untilWilt <= TimeSpan.FromHours(6))
                    return ($"Growing in bed {bedNumber}. Tend it within {Math.Max(0, (int)Math.Ceiling(untilWilt.TotalHours))} hours or it wilts.", HubStyle.Warn);
            }

            var confidenceNote = window.Confidence == HarvestConfidence.Estimated ? ", estimated" : "";
            return earliest is { } e
                ? ($"Growing in bed {bedNumber}. About {FormatDays(e - now)} left, at the earliest{confidenceNote}.", HubStyle.Text)
                : ($"Growing in bed {bedNumber}.", HubStyle.Text);
        }

        /// <summary>The pair strip, economics and handoff for the one step currently in progress — the
        /// only step whose planting detail matters right now. Renders nothing when the current step is
        /// an obtain step, since there is no bed to strip yet.</summary>
        private static void DrawGoalCurrentStepDetail(
            GoalPlan plan, List<(GoalStep Step, int Index)> members, GoalStatus status, bool atGarden,
            IReadOnlyDictionary<uint, int> held)
        {
            var crossOrMultiply = members.Select(m => m.Step).FirstOrDefault(s => s is CrossStep or MultiplyStep);
            if (crossOrMultiply is null)
                return;

            if (!atGarden)
            {
                ImGui.TextColored(HubStyle.Faint, "Stand at your garden to see which beds this uses.");
                DrawGoalHandoffDisabled("Stand at your garden to plant this step.");
                return;
            }

            var patch = PatchDiscovery.Patches.First();

            if (MissingSeedForHandoff(crossOrMultiply, held) is { } missing)
            {
                DrawGoalHandoffDisabled($"You need {missing} to plant this step.");
                return;
            }

            var stepSoilPreference = crossOrMultiply is MultiplyStep ? SoilPreference.HighestShroud : Plugin.C.SoilForCross;
            var bag = Bags.Scan();
            if (GardeningItems.BestSoil(stepSoilPreference, bag) is null)
            {
                var family = SoilFamilyForPreference(stepSoilPreference);
                DrawGoalHandoffDisabled(
                    $"You have no {family} Topsoil. Mine Grade 3 in {SoilSources.Where(family, 3)}.");
                return;
            }

            var targetSeed = crossOrMultiply switch
            {
                // The seed the cross is meant to produce, not either parent: the planner is being
                // asked what to lay out to obtain it.
                CrossStep c => (ushort)c.TargetRow,
                MultiplyStep m => (ushort)m.SeedRow,
                _ => (ushort)0,
            };

            var memory = GardenMemory.Read(patch);
            var singleStepPlan = crossOrMultiply is CrossStep crossStep
                ? CrossPlanner.PlanFillStep(targetSeed, crossStep.Beds, patch, memory, bag)
                : CrossPlanner.PlanSingleStep(targetSeed, patch, memory, bag);

            if (singleStepPlan.Steps.Count > 0)
            {
                if (ImGui.BeginTable($"##goalpair-{patch.Key}", 2, ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    foreach (var step in singleStepPlan.Steps.OrderBy(s => s.BedNumber))
                    {
                        ImGui.TableNextColumn();
                        var label = singleStepPlan.ExpectedTargets.ContainsKey(step.BedNumber)
                            ? $"bed {step.BedNumber}: plant {SeedItems.ProduceName(step.SeedRow)} here"
                            : $"bed {step.BedNumber}: {SeedItems.ProduceName(step.SeedRow)}";
                        ImGui.TextUnformatted($"[{label}]");
                    }
                    ImGui.EndTable();
                }
            }

            foreach (var warning in singleStepPlan.Warnings)
                ImGui.TextColored(HubStyle.Warn, warning);

            DrawGoalHandoff(patch, singleStepPlan);
        }

        /// <summary>The seed one more planting of the current step needs and does not have, worded for
        /// the handoff's disabled reason — null when both are covered. A route can want several
        /// attempts of a pair over its lifetime, but a single "Plant this step" press only ever plants
        /// one, so this checks against 1, not the route's total.</summary>
        private static string? MissingSeedForHandoff(GoalStep step, IReadOnlyDictionary<uint, int> held) => step switch
        {
            CrossStep c when held.GetValueOrDefault(c.FirstSeedRow) < 1 && held.GetValueOrDefault(c.SecondSeedRow) < 1 =>
                $"{SeedItemName(c.FirstSeedRow)} and {SeedItemName(c.SecondSeedRow)}",
            CrossStep c when held.GetValueOrDefault(c.FirstSeedRow) < 1 => $"1 more {SeedItemName(c.FirstSeedRow)}",
            CrossStep c when held.GetValueOrDefault(c.SecondSeedRow) < 1 => $"1 more {SeedItemName(c.SecondSeedRow)}",
            MultiplyStep m when held.GetValueOrDefault(m.SeedRow) < 1 => $"1 more {SeedItemName(m.SeedRow)}",
            _ => null,
        };

        private static void DrawGoalHandoffDisabled(string reason) => ImGui.TextColored(HubStyle.Faint, reason);

        /// <summary>The one-step handoff: a single <see cref="HubStyle.Primary"/> control that plants
        /// exactly the beds named above and stops. Never queues a second step and never re-runs itself:
        /// Gardener plants one step, when asked, and stops.</summary>
        private static void DrawGoalHandoff(Patch patch, LayoutPlan singleStepPlan)
        {
            var canPlant = singleStepPlan.Steps.Count > 0 && CrossPlanner.Verify(singleStepPlan, patch);
            if (!canPlant)
            {
                DrawGoalHandoffDisabled("Gardener cannot fit this step into the free beds on this patch.");
                return;
            }

            using (HubStyle.Primary())
            {
                if (ImGui.Button("Plant this step"))
                {
                    if (Plugin.C.ConfirmBeforeRun)
                        ImGui.OpenPopup("Confirm plant goal step");
                    else
                        StartGoalStep(singleStepPlan, patch);
                }
            }

            if (!ImGui.BeginPopup("Confirm plant goal step"))
                return;

            ImGui.TextUnformatted("Plant this step?");
            var lines = singleStepPlan.Steps.OrderBy(s => s.BedNumber)
                .Select(s => $"{SeedItemName(s.SeedRow)} in bed {s.BedNumber}");
            ImGui.TextWrapped(
                $"Gardener will plant {string.Join(" and ", lines)}, using {ItemSheet.Name(singleStepPlan.Steps[0].SoilItemId)}. " +
                "It plants this one step and stops.");
            if (ImGui.Button("Plant it"))
            {
                StartGoalStep(singleStepPlan, patch);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        private static void StartGoalStep(LayoutPlan singleStepPlan, Patch patch)
        {
            SchedulerMain.PendingPlan = singleStepPlan;
            SchedulerMain.EnablePlugin(SweepKind.Plan, patch);
        }

        private static string SeedItemName(uint row) =>
            SeedItems.SeedItemForRow(row) is { } itemId ? XivHubPluginKit.Inventory.ItemSheet.Name(itemId) : $"row {row}'s seed";

        /// <summary>Which family drives a <see cref="SoilPreference"/>'s odds, for
        /// <see cref="SoilSources.Where"/>'s sake — <see cref="SoilPreference.Fixed"/> pins one item
        /// rather than a family, so it falls back to the one that actually moves intercross odds, the
        /// same choice <see cref="GoalRoute"/> makes when it has no live pin to read either.</summary>
        private static SoilFamily SoilFamilyForPreference(SoilPreference preference) => preference switch
        {
            SoilPreference.HighestThanalan => SoilFamily.Thanalan,
            SoilPreference.HighestShroud => SoilFamily.Shroud,
            SoilPreference.HighestLaNoscean => SoilFamily.LaNoscean,
            SoilPreference.Fixed => SoilFamily.Thanalan,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "unhandled SoilPreference"),
        };

        /// <summary>Rule 1 of the Goal tab's generated copy: one number only when every soil tier
        /// agrees, a range otherwise.</summary>
        private static string GoalYieldText(int[]? tiers)
        {
            if (tiers is not { Length: > 0 })
                return "an unknown number of";
            return tiers.All(t => t == tiers[0]) ? tiers[0].ToString() : $"{tiers.Min()} to {tiers.Max()}";
        }

        private static string GoalDaysPhrase(int growHours) =>
            growHours > 48 ? $"about {Math.Round(growHours / 24.0):F0} days" : $"about {Math.Round((double)growHours):F0} hours";

        /// <summary>Durations round to days above 48 hours and to hours below, and never to the
        /// minute — the same rule the harvest window and wilt cadence follow.</summary>
        private static string FormatDays(TimeSpan span) =>
            span.TotalHours > 48 ? $"{Math.Round(span.TotalDays):F0} days" : $"{Math.Round(span.TotalHours):F0} hours";

        private static string GoalAgePhrase(DateTimeOffset at)
        {
            var span = DateTimeOffset.UtcNow - at;
            return span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} minutes" : $"{FormatDays(span)}";
        }

        /// <summary>One button per occupied bed, never part of a sweep: <see cref="GardenerGuard.BlockingReason"/>
        /// gates starting a sweep, but tending needs no permission and removing a crop is a single
        /// interaction rather than a worklist, so the only gate here is the confirm itself, which names
        /// the seed being destroyed.</summary>
        private static void DrawRemoveCropButtons(Patch patch, IReadOnlyList<BedState> memory)
        {
            var occupied = memory.Where(s => !s.IsEmpty).OrderBy(s => s.BedNumber).ToList();
            if (occupied.Count == 0)
                return;

            ImGui.Spacing();
            ImGui.TextColored(HubStyle.Faint, "Remove a crop");
            foreach (var state in occupied)
            {
                var name = SeedItems.ProduceName(state.SeedRow);
                var popupId = $"Confirm remove crop##{patch.Key}-{state.BedNumber}";
                if (ImGui.Button($"Remove##removecrop-{patch.Key}-{state.BedNumber}"))
                    ImGui.OpenPopup(popupId);
                ImGui.SameLine();
                ImGui.TextUnformatted($"Bed {state.BedNumber}: {name}");

                if (!ImGui.BeginPopup(popupId))
                    continue;

                ImGui.TextColored(HubStyle.Warn, $"Destroy {name} in bed {state.BedNumber}? This can't be undone.");
                if (ImGui.Button("Confirm"))
                {
                    Task_RemoveCrop.TryEnqueue(patch, state.BedNumber, state.SeedRow);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }


        private static void DrawLogTab()
        {
            foreach (var entry in ActivityLog.Entries)
                ImGui.TextColored(entry.Color, $"[{entry.Time}] {entry.Message}");
        }

        /// <summary>
        /// The four journal-only due lists from <see cref="Reminders"/>, grouped by house then patch,
        /// coloured per THEME.md's semantic roles. Wilt and wither times are stated as exact local
        /// times, never hedged; a harvest reminder built only from <c>LastSeenStage == 4</c> says
        /// "mature", never "ready", until a passed harvest window backs that claim up.
        /// </summary>
        private static void DrawRemindersTab()
        {
            // Tending and wither risk both need no house permission at all: any character can water
            // any outdoor garden, so unlike harvesting these two never carry a "switch to" note.
            ImGui.TextColored(HubStyle.Warn, "Due to tend");
            DrawReminderGroup(Reminders.DueToTend, HubStyle.Warn, "Every bed is tended.",
                e => $"{e.SeedName}, bed {e.BedNumber}: wilts {e.At.ToLocalTime():g}", showReach: false);

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Bad, "About to wither");
            DrawReminderGroup(Reminders.AboutToWither, HubStyle.Bad, "Nothing is close to withering.",
                e => $"{e.SeedName}, bed {e.BedNumber}: withers {e.At.ToLocalTime():g}", showReach: false);

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Good, "Ready to harvest");
            ImGui.TextColored(HubStyle.Faint, HarvestLagNote);
            DrawHarvestGroup();

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Faint, "Planting time unknown");
            DrawReminderGroup(Reminders.TimingUnknown, HubStyle.Faint, "Every bed's planting time is known.",
                e => $"{e.SeedName}, bed {e.BedNumber}: planting time unknown", showReach: true);
        }

        private static void DrawReminderGroup(
            IReadOnlyList<ReminderEntry> entries, Vector4 color, string emptyText, Func<ReminderEntry, string> lineText,
            bool showReach)
        {
            if (entries.Count == 0)
            {
                ImGui.TextColored(HubStyle.Faint, emptyText);
                return;
            }

            foreach (var houseGroup in entries.GroupBy(e => e.HouseKey))
            {
                DrawHouseHeader(houseGroup.Key);
                foreach (var patchGroup in houseGroup.GroupBy(e => e.PatchKey))
                {
                    ImGui.Indent();
                    ImGui.TextColored(HubStyle.Faint, patchGroup.Key);
                    foreach (var entry in patchGroup.OrderBy(e => e.BedNumber))
                        DrawReminderLine(color, lineText(entry), showReach ? entry.ReachableBy : null);
                    ImGui.Unindent();
                }
            }
        }

        private static void DrawHarvestGroup()
        {
            if (Reminders.ReadyToHarvest.Count == 0)
            {
                ImGui.TextColored(HubStyle.Faint, "Nothing is ready to harvest yet.");
                return;
            }

            foreach (var houseGroup in Reminders.ReadyToHarvest.GroupBy(h => h.Entry.HouseKey))
            {
                DrawHouseHeader(houseGroup.Key);
                foreach (var patchGroup in houseGroup.GroupBy(h => h.Entry.PatchKey))
                {
                    ImGui.Indent();
                    ImGui.TextColored(HubStyle.Faint, patchGroup.Key);
                    foreach (var harvest in patchGroup.OrderBy(h => h.Entry.BedNumber))
                    {
                        var text = harvest.FromWindow
                            ? $"{harvest.Entry.SeedName}, bed {harvest.Entry.BedNumber}: ready between " +
                              $"{harvest.Window.Earliest!.Value.ToLocalTime():g} and " +
                              $"{harvest.Window.Latest!.Value.ToLocalTime():t} ({HarvestConfidenceText(harvest.Window)})"
                            : $"{harvest.Entry.SeedName}, bed {harvest.Entry.BedNumber}: mature (stage 4)";
                        DrawReminderLine(HubStyle.Good, text, harvest.Entry.ReachableBy);
                    }
                    ImGui.Unindent();
                }
            }
        }

        /// <summary>The reach note is omitted (<paramref name="reachableBy"/> null) for actions any
        /// character can perform without house permission — tending and wither risk; see
        /// <see cref="Reminders.ReachText"/> for the actions that still need it.</summary>
        private static void DrawReminderLine(Vector4 color, string text, IReadOnlyList<string>? reachableBy)
        {
            ImGui.TextColored(color, text);
            if (reachableBy is null)
                return;
            ImGui.SameLine();
            ImGui.TextColored(HubStyle.Faint, $"({Reminders.ReachText(reachableBy)})");
        }

        private static string HarvestConfidenceText(HarvestWindow window) => window.Confidence switch
        {
            HarvestConfidence.Estimated => "estimated",
            HarvestConfidence.Bundled => "bundled",
            HarvestConfidence.Calibrated => $"observed, {window.SampleCount} sample(s)",
            _ => "unknown",
        };

        private static void DrawHouseHeader(string houseKey)
        {
            var estateType = GardenJournal.EstateTypeFor(houseKey);
            ImGui.TextUnformatted(estateType is { } et ? et.ToString() : "House");
            ImGui.SameLine();
            ImGui.TextColored(HubStyle.Faint, houseKey);
        }

        private static void DrawSeedAndStage(BedState state, BedRecord? record)
        {
            // What is in the ground is the produce, not the seed that grew it — "La Noscean Lettuce",
            // not "La Noscean Lettuce Seeds". The seed name belongs where the plugin talks about what
            // to plant or what is in the bag, not here.
            var produceItemId = SeedItems.ProduceItemForRow(state.SeedRow);
            var produceName = produceItemId is { } id ? XivHubPluginKit.Inventory.ItemSheet.Name(id) : $"row {state.SeedRow}";
            var stageText = state.Maturity switch
            {
                Maturity.MatureCandidate => "mature (4)",
                Maturity.Growing => $"growing ({state.Stage} of 4)",
                _ => "empty",
            };

            ImGui.TextUnformatted(produceName);
            ImGui.TextColored(StageColor(state, record), stageText);
            DrawCropState(record);

            if (record?.LastSeenByCharacter is { Length: > 0 } observer)
            {
                var agoHours = (DateTimeOffset.UtcNow - record.LastSeenAt).TotalHours;
                ImGui.TextColored(HubStyle.Faint, $"seen by {observer}, {agoHours:F0}h ago");
            }
        }

        /// <summary>The most recent <c>TALK_*</c> sentence chat has echoed for this bed (see
        /// <see cref="CropChatState"/>) — the only source for wilted and true harvest-readiness the
        /// bed menu itself never offers. A bed whose state has never been observed says so, rather
        /// than rendering blank or implying healthy.</summary>
        private static void DrawCropState(BedRecord? record)
        {
            if (record?.LastObservedCropState is not { } cropState || record.LastObservedCropStateAt is not { } observedAt)
            {
                ImGui.TextColored(HubStyle.Faint, "crop state never observed");
                return;
            }

            var (text, color) = cropState switch
            {
                MenuKey.TalkVigorous => ("vigorous", HubStyle.Good),
                MenuKey.TalkDepressed => ("wilted", HubStyle.Warn),
                MenuKey.TalkRipe => ("ripe", HubStyle.Good),
                MenuKey.TalkDead => ("withered", HubStyle.Bad),
                MenuKey.TalkNone => ("empty", HubStyle.Faint),
                _ => (cropState.ToString(), HubStyle.Faint),
            };

            var agoHours = (DateTimeOffset.UtcNow - observedAt).TotalHours;
            ImGui.TextColored(color, $"{text}, {agoHours:F0}h ago");
        }

        /// <summary>Semantic bed-state colour per THEME.md: mature → Good, due to tend → Warn, about
        /// to wither → Bad, empty or timing-unknown → Faint. Nothing here is a domain palette; these
        /// are the four roles HubStyle already exposes.</summary>
        private static Vector4 StageColor(BedState state, BedRecord? record)
        {
            if (state.IsEmpty)
                return HubStyle.Faint;
            if (state.Maturity == Maturity.MatureCandidate)
                return HubStyle.Good;

            if (record is { } r)
            {
                var wiltsAt = Growth.WiltsAt(r);
                if (wiltsAt is { } wa)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now >= wa)
                        return HubStyle.Bad; // already wilted; on the wither clock
                    if (now >= wa - TimeSpan.FromHours(Plugin.C.WiltWarningHours))
                        return HubStyle.Warn; // due to tend soon
                }
            }

            return HubStyle.Text;
        }

        private static void DrawWilt(BedRecord? record)
        {
            if (record is { } r && Growth.WiltsAt(r) is { } wiltsAt)
                ImGui.TextUnformatted(wiltsAt.ToLocalTime().ToString("g"));
            else
                ImGui.TextColored(HubStyle.Faint, "unknown");
        }

        private static void DrawHarvestWindow(string patchKey, int bedNumber, BedRecord? record)
        {
            if (record is not { } rec)
            {
                ImGui.TextColored(HubStyle.Faint, "planted-at unknown; set an estimate");
                return;
            }

            if (rec.PlantedAt is null)
            {
                ImGui.TextColored(HubStyle.Faint, "planted-at unknown; set an estimate");
                DrawPlantedAtEstimate(patchKey, bedNumber, rec);
                return;
            }

            var window = Growth.HarvestWindow(rec);
            if (window.Confidence == HarvestConfidence.Unknown)
            {
                ImGui.TextColored(HubStyle.Faint, "no timing data for this seed");
                return;
            }

            var confidenceText = window.Confidence switch
            {
                HarvestConfidence.Estimated => "estimated",
                HarvestConfidence.Bundled => "bundled",
                HarvestConfidence.Calibrated => $"observed, {window.SampleCount} sample(s)",
                _ => "unknown",
            };
            // A hand-set estimate already reads as "estimated" above, so this never doubles up with
            // it: AnchorUncertainty is null exactly when the estimate widget last wrote PlantedAt.
            if (window.AnchorUncertainty is { } anchorUncertainty)
                confidenceText += $", planted-at ±{FormatUncertainty(anchorUncertainty)}";
            var when = window.Earliest is { } e && window.Latest is { } l
                ? $"{e.ToLocalTime():g} to {l.ToLocalTime():t}"
                : "?";
            ImGui.TextUnformatted($"{when} ({confidenceText})");
        }

        /// <summary>Renders a duration the way its own magnitude deserves: seconds for a transition
        /// caught within the same poll, hours for one caught only after a long absence, never a single
        /// unit that misrepresents either end.</summary>
        private static string FormatUncertainty(TimeSpan span)
        {
            if (span.TotalMinutes < 1)
                return $"{span.TotalSeconds:F0}s";
            if (span.TotalHours < 1)
                return $"{span.TotalMinutes:F0}m";
            return $"{span.TotalHours:F1}h";
        }

        private static void DrawPlantedAtEstimate(string patchKey, int bedNumber, BedRecord record)
        {
            var key = (patchKey, bedNumber);
            if (!pendingEstimateHours.TryGetValue(key, out var hours))
                hours = 1;

            ImGui.SetNextItemWidth(60);
            ImGui.InputInt($"##estimateHours-{patchKey}-{bedNumber}", ref hours);
            hours = Math.Max(0, hours);
            pendingEstimateHours[key] = hours;

            ImGui.SameLine();
            if (ImGui.Button($"Planted {hours}h ago##setEstimate-{patchKey}-{bedNumber}"))
            {
                record.PlantedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);
                record.PlantedAtEstimated = true;
                record.PlantedAtUncertainty = null; // a hand-set guess replaces any transition anchor
                GardenJournal.Upsert(record);
                pendingEstimateHours.Remove(key);
            }
        }
    }
}
