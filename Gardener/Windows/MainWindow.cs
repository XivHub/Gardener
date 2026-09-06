using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;
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

        // Staged hours for the "set all planting times" popup, keyed by patch (never a character):
        // one bulk guess per patch, entered once and applied to every unset bed on it.
        private static readonly Dictionary<string, int> pendingSetAllHours = new();

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
        private static (SoilPreference Cross, SoilPreference Yield) goalPlanSoilCache;
        private static GoalPlan? goalPlanCache;
        private static IReadOnlyDictionary<uint, int>? quickPicksHeldCache;
        private static List<(uint Row, GoalPlan Plan)> quickPicksCache = new();

        public MainWindow(Configuration configuration) : base("Gardener###GardenerMain")
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(420, 200),
                MaximumSize = new Vector2(4000, 4000),
            };
            Size = new Vector2(600, 480);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public void Dispose() { }

        public override void Draw()
        {
            if (!ImGui.BeginTabBar("##gardenerTabs"))
                return;

            // ###-suffixed: BeginTabItem derives tab identity (and so which tab stays selected
            // across a redraw) from the label, and the visible text ahead of "Garden" etc. will
            // change with the UI language while these ids must not.
            if (ImGui.BeginTabItem($"{Strings.Garden_TabLabel}###gardenTab"))
            {
                DrawGardenTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"{Strings.Goal_TabLabel}###goalTab"))
            {
                DrawGoalTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"{Strings.Reminders_TabLabel}###remindersTab"))
            {
                DrawRemindersTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"{Strings.Log_TabLabel}###logTab"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        /// <summary>
        /// Lists discovered patches (already filtered to the plot the player is standing on), one
        /// <see cref="ImGui.CollapsingHeader"/> per patch so two patches at one house both fit without
        /// scrolling. Built entirely around the passive <see cref="GardenMemory"/> read: per patch, one
        /// row per bed number <c>1..BedCount()</c> — the game's own "Nth Bed" numbering, not a spatial
        /// index — showing the crop, growth and readiness. A patch whose <see cref="GardenMemory.Read"/>
        /// came back empty renders as "no data for this patch", never as eight empty beds: those are
        /// different facts.
        /// </summary>
        private static void DrawGardenTab()
        {
            DrawSchedulerStatus();

            var patches = PatchDiscovery.Patches;
            if (patches.Count == 0)
                ImGui.TextColored(HubStyle.Faint, Strings.Garden_NoPatches);

            var plot = PatchDiscovery.LastDiagnostics.CurrentPlot;

            foreach (var patch in patches)
            {
                ImGui.SetNextItemOpen(true, ImGuiCond.FirstUseEver);
                // ###-suffixed: CollapsingHeader keys its own open/collapsed state off GetID(label),
                // and the visible half (PatchLabel.Header) changes with the UI language.
                var open = ImGui.CollapsingHeader($"{PatchLabel.Header(patch, plot)}###patch-{patch.Key}");

                // Drawn regardless of open/collapsed, via the same measure-and-SameLine idiom
                // DrawGoalStepGroup's own marker uses, so a collapsed patch still answers "what needs
                // doing" without expanding it.
                var due = Reminders.SummaryTextFor(patch.Key);
                var dueWidth = ImGui.CalcTextSize(due).X;
                ImGui.SameLine(ImGui.GetContentRegionMax().X - dueWidth - ImGui.GetStyle().FramePadding.X * 2f);
                ImGui.TextColored(HubStyle.Faint, due);

                if (!open)
                    continue;

                var states = GardenMemory.Read(patch);
                if (states.Count == 0)
                {
                    ImGui.TextColored(HubStyle.Faint, Strings.Garden_NoDataForPatch);
                    ImGui.Spacing();
                    continue;
                }

                var byBed = states.ToDictionary(s => s.BedNumber);

                DrawSweepButtons(patch);
                DrawPatchMetaLine(patch, byBed);
                DrawBedTable(patch, byBed);

                ImGui.Spacing();
            }

            DrawOrphans();

            if (patches.Count > 0)
                ImGui.TextColored(HubStyle.Faint, Strings.Garden_HarvestLagNote);

            ImGui.Separator();
            ImGui.TextDisabled(Strings.Garden_DebugDumpHeader);
            if (ImGui.Button(Strings.Garden_DumpButton))
                DebugDump.Run(menu: false);
            ImGui.SameLine();
            if (ImGui.Button(Strings.Garden_DumpMenuButton))
                DebugDump.Run(menu: true);

            if (DebugDump.LastDumpPath != null)
            {
                ImGui.TextColored(HubStyle.Faint, DebugDump.LastDumpPath);
                ImGui.SameLine();
                if (ImGui.Button($"{Strings.Garden_CopyButton}##copyLastDump"))
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
            ImGui.TextColored(HubStyle.Warn, Loc.Format(Strings.Garden_OrphansHeader, Formats.Number(orphans.Count)));
            ImGui.TextColored(HubStyle.Faint, Strings.Garden_OrphansNote);
            foreach (var orphan in orphans)
            {
                ImGui.TextUnformatted(Loc.Format(Strings.Garden_OrphanLine, orphan.PatchKey,
                    Formats.Number(orphan.BedNumber), SeedName(orphan.SeedRow)));
                ImGui.SameLine();
                if (ImGui.Button($"{Strings.Garden_ForgetButton}##orphan-{orphan.PatchKey}-{orphan.BedNumber}"))
                    GardenJournal.Remove(orphan.PatchKey, orphan.BedNumber);
            }
        }

        private static string SeedName(ushort row)
        {
            var produceItemId = SeedItems.ProduceItemForRow(row);
            return produceItemId is { } id
                ? XivHubPluginKit.Inventory.ItemSheet.Name(id)
                : Loc.Format(Strings.Garden_UnresolvedSeedRow, Formats.Number(row));
        }

        /// <summary>The facts that used to repeat on every one of a patch's rows, said once per patch
        /// instead: who last saw it and how long ago, how many beds still need a planting time (with a
        /// bulk setter for the common "I didn't watch it get planted" case), and the bed-order-verified
        /// note — kept here rather than in <see cref="DrawSweepButtons"/> and shown only while it is
        /// still incomplete, since a fully verified patch has nothing left to say about it.</summary>
        private static void DrawPatchMetaLine(Patch patch, IReadOnlyDictionary<int, BedState> byBed)
        {
            var patchRecords = GardenJournal.AllRecords.Where(r => r.PatchKey == patch.Key).ToList();

            var observedRecords = byBed.Values.Where(s => !s.IsEmpty)
                .Select(s => GardenJournal.Get(patch.Key, s.BedNumber))
                .Where(r => r?.LastSeenByCharacter is { Length: > 0 })
                .Select(r => r!)
                .ToList();
            if (observedRecords.Count > 0)
            {
                var observers = observedRecords.Select(r => r.LastSeenByCharacter!).Distinct().ToList();
                var oldest = observedRecords.Min(r => r.LastSeenAt);
                var ageHours = (DateTimeOffset.UtcNow - oldest).TotalHours;
                ImGui.TextColored(HubStyle.Faint,
                    Loc.Format(Strings.Garden_LastSeenLine, TextList.And(observers), Formats.Number(ageHours, "F0")));
            }

            var unknownCount = patchRecords.Count(r => r.PlantedAt is null);
            if (unknownCount > 0)
            {
                ImGui.TextColored(HubStyle.Faint, Loc.Format(
                    unknownCount == 1 ? Strings.Garden_UnknownPlantingTimeCount_One : Strings.Garden_UnknownPlantingTimeCount_Other,
                    Formats.Number(unknownCount)));
                ImGui.SameLine();
                if (ImGui.SmallButton($"{Strings.Garden_SetAllPlantingTimesButton}##setall-{patch.Key}"))
                    ImGui.OpenPopup($"Set all planting times##{patch.Key}");
            }
            DrawSetAllPlantingTimesPopup(patch, patchRecords);

            if (BedTargeting.VerifiedBedCount(patch) < patch.Kind.BedCount())
                ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Sweep_BedOrderVerified,
                    Formats.Number(BedTargeting.VerifiedBedCount(patch)), Formats.Number(patch.Kind.BedCount())));
        }

        private static void DrawSetAllPlantingTimesPopup(Patch patch, IReadOnlyList<BedRecord> patchRecords)
        {
            if (!ImGui.BeginPopup($"Set all planting times##{patch.Key}"))
                return;

            ImGui.TextUnformatted(Strings.Garden_SetAllPlantingTimesBody);

            if (!pendingSetAllHours.TryGetValue(patch.Key, out var hours))
                hours = 1;
            ImGui.SetNextItemWidth(60);
            ImGui.InputInt($"##setAllHours-{patch.Key}", ref hours);
            hours = Math.Max(0, hours);
            pendingSetAllHours[patch.Key] = hours;

            if (ImGui.Button(Strings.Common_Confirm))
            {
                var plantedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);
                foreach (var record in patchRecords.Where(r => r.PlantedAt is null))
                {
                    record.PlantedAt = plantedAt;
                    record.PlantedAtEstimated = true;
                    record.PlantedAtUncertainty = null; // a hand-set guess replaces any transition anchor
                    GardenJournal.Upsert(record);
                }
                pendingSetAllHours.Remove(patch.Key);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Strings.Common_Cancel))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }

        /// <summary>Five columns: bed number, crop, a <see cref="StageIndicator"/> growth meter, one
        /// "when" sentence (<see cref="DrawWhenCell"/>) and a per-row remove-crop action. Hovering the
        /// crop or growth cell opens <see cref="DrawBedTooltip"/> for the facts too dense to keep on
        /// every row: soil, exact wilt time, confidence, uncertainty and the last chat-observed
        /// condition.</summary>
        private static void DrawBedTable(Patch patch, IReadOnlyDictionary<int, BedState> byBed)
        {
            // Stretch rather than fit-to-content for Crop/When: a fitted column sizes itself to the
            // longest sentence and pushes the rest off the window.
            const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
            if (!ImGui.BeginTable($"##bedgrid-{patch.Key}", 5, flags))
                return;

            // ###-suffixed: the table is Resizable, and a translated header label must not read as
            // a different column from the one whose width the player already dragged.
            ImGui.TableSetupColumn($"{Strings.Garden_ColBed}###col-bed", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn($"{Strings.Garden_ColCrop}###col-crop", ImGuiTableColumnFlags.WidthStretch, 4f);
            ImGui.TableSetupColumn($"{Strings.Garden_ColGrowth}###col-growth", ImGuiTableColumnFlags.WidthFixed, StageIndicator.Width());
            ImGui.TableSetupColumn($"{Strings.Garden_ColWhen}###col-when", ImGuiTableColumnFlags.WidthStretch, 5f);
            // No header label: the column holds one icon button, nothing a header word would describe.
            ImGui.TableSetupColumn("##col-action", ImGuiTableColumnFlags.NoHeaderLabel | ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableHeadersRow();

            for (var bedNumber = 1; bedNumber <= patch.Kind.BedCount(); bedNumber++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Formats.Number(bedNumber));
                ImGui.TableNextColumn();

                if (!byBed.TryGetValue(bedNumber, out var state) || state.IsEmpty)
                {
                    ImGui.TextColored(HubStyle.Faint, Strings.Common_Empty);
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    continue;
                }

                var record = GardenJournal.Get(patch.Key, bedNumber);
                var color = BedColor(state, record);

                // Wrap at the cell's own right edge, which is what PushTextWrapPos(0) means inside a
                // table; without it every cell here is one unbroken line and the last column is cut off.
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(color, SeedName(state.SeedRow));
                if (ImGui.IsItemHovered())
                    DrawBedTooltip(patch, bedNumber, state, record);

                ImGui.TableNextColumn();
                StageIndicator.Draw(state.Stage, color);
                if (ImGui.IsItemHovered())
                    DrawBedTooltip(patch, bedNumber, state, record);

                ImGui.TableNextColumn();
                DrawWhenCell(patch.Key, bedNumber, state, record);
                ImGui.PopTextWrapPos();

                ImGui.TableNextColumn();
                DrawRemoveCropButton(patch, state);
            }

            ImGui.EndTable();
        }

        /// <summary>The running sweep's own row: what it is doing and the Stop that ends it. Drawn
        /// regardless of what is blocking a <em>new</em> run, so a sweep in progress can always be
        /// stopped, and drawn not at all when no sweep is running.</summary>
        private static void DrawSchedulerStatus()
        {
            // Nothing running means nothing to stop: the row disappears rather than offering a button
            // that does nothing.
            if (!SchedulerMain.Running)
                return;

            var bedText = SchedulerMain.CurrentBedNumber is { } bed ? Formats.Number(bed) : Strings.Sweep_NoActiveBed;
            ImGui.TextColored(HubStyle.Warn, Loc.Format(Strings.Sweep_StatusRow,
                GameWords.Action(SchedulerMain.CurrentKind!.Value), bedText, Formats.Number(SchedulerMain.Worklist.Count),
                SchedulerMain.State));
            ImGui.SameLine();

            if (ImGui.Button(Strings.Sweep_StopButton))
                SchedulerMain.DisablePlugin("you clicked Stop.");

            ImGui.Separator();
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

            var tendLabel = Loc.Format(Strings.Sweep_AllButton, GameWords.Action(SweepKind.Tend));
            var harvestLabel = Loc.Format(Strings.Sweep_AllButton, GameWords.Action(SweepKind.Harvest));
            var fertilizeLabel = Loc.Format(Strings.Sweep_AllButton, GameWords.Action(SweepKind.Fertilize));

            using (HubStyle.Primary())
            {
                if (ImGui.Button($"{tendLabel}##tend-{patch.Key}"))
                {
                    if (Plugin.C.ConfirmBeforeRun)
                        ImGui.OpenPopup($"Confirm tend##{patch.Key}");
                    else
                        SchedulerMain.EnablePlugin(SweepKind.Tend, patch);
                }
            }
            DrawConfirmPopup(patch, $"Confirm tend##{patch.Key}",
                Loc.Format(Strings.Sweep_ConfirmTend, GameWords.Action(MenuKey.Care)), SweepKind.Tend);

            ImGui.SameLine();
            if (ImGui.Button($"{harvestLabel}##harvest-{patch.Key}"))
            {
                if (Plugin.C.ConfirmBeforeRun)
                    ImGui.OpenPopup($"Confirm harvest##{patch.Key}");
                else
                    SchedulerMain.EnablePlugin(SweepKind.Harvest, patch);
            }
            DrawConfirmPopup(patch, $"Confirm harvest##{patch.Key}",
                Loc.Format(Strings.Sweep_ConfirmHarvest, GameWords.Action(MenuKey.Harvest)), SweepKind.Harvest);

            ImGui.SameLine();
            if (ImGui.Button($"{fertilizeLabel}##fertilize-{patch.Key}"))
            {
                if (Plugin.C.ConfirmBeforeRun)
                    ImGui.OpenPopup($"Confirm fertilize##{patch.Key}");
                else
                    SchedulerMain.EnablePlugin(SweepKind.Fertilize, patch);
            }
            DrawConfirmPopup(patch, $"Confirm fertilize##{patch.Key}",
                Loc.Format(Strings.Sweep_ConfirmFertilize, GameWords.Action(MenuKey.SetFertilizer)), SweepKind.Fertilize);
        }

        private static void DrawConfirmPopup(Patch patch, string popupId, string message, SweepKind kind)
        {
            if (!ImGui.BeginPopup(popupId))
                return;

            ImGui.TextUnformatted(message);
            if (ImGui.Button(Strings.Common_Confirm))
            {
                SchedulerMain.EnablePlugin(kind, patch);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Strings.Common_Cancel))
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

            var held = SeedInventory.Counts();
            var goalRow = GardenJournal.GoalSeedRow;

            if (goalRow == 0 || goalPickerOpen)
            {
                DrawGoalPicker(goalRow, held);
                return;
            }

            ImGui.TextUnformatted(Loc.Format(Strings.Goal_CurrentGoal, SeedItems.ProduceName(goalRow)));
            ImGui.SameLine();
            if (ImGui.Button(Strings.Goal_PickSomethingElseButton))
            {
                goalPickerOpen = true;
                goalPickerSearch = string.Empty;
            }
            ImGui.SameLine();
            if (ImGui.Button(Strings.Goal_ClearGoalButton))
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
            var soil = (Plugin.C.SoilForCross, Plugin.C.SoilForYield);
            if (goalRow == goalPlanRowCache && ReferenceEquals(held, goalPlanHeldCache) && soil == goalPlanSoilCache)
                return goalPlanCache;

            var plan = GoalRoute.Solve(goalRow, held, soil.SoilForCross, soil.SoilForYield);
            goalPlanRowCache = goalRow;
            goalPlanHeldCache = held;
            goalPlanSoilCache = soil;
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
                ImGui.TextWrapped(Strings.Goal_PickerIntro);
                ImGui.Spacing();
                DrawQuickPicks(held);
                ImGui.Spacing();
            }

            ImGui.TextUnformatted(Strings.Goal_PickerHeader);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##goalSearch", Strings.Goal_PickerSearchHint, ref goalPickerSearch, 64);

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

                    var label = Loc.Format(Strings.Goal_PickerRowLabel, produceName, PickerTag(row));
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
                return Strings.Goal_TagGatherable;
            if (SeedTable.CrossableTargets.Contains(row))
                return Strings.Goal_TagCrossbreedOnly;
            return Strings.Goal_TagNoCrossData;
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
                    if (GoalRoute.Solve(row, held, Plugin.C.SoilForCross, Plugin.C.SoilForYield) is { } plan)
                        found.Add((row, plan));
                }
                quickPicksCache = found.OrderBy(f => f.Plan.BestCaseDuration).Take(3).ToList();
                quickPicksHeldCache = held;
            }

            if (quickPicksCache.Count == 0)
                return;

            ImGui.TextUnformatted(Strings.Goal_QuickPicksHeader);
            foreach (var (row, _) in quickPicksCache)
            {
                if (ImGui.Button($"{SeedItems.ProduceName(row)}##quickpick-{row}"))
                    GardenJournal.GoalSeedRow = row;
            }
        }

        private static void DrawGoalAlreadyMet(uint goalRow, int heldCount)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextUnformatted(Loc.Format(Strings.Goal_AlreadyHold, Formats.Number(heldCount), SeedItems.SeedItemName(goalRow)));

            var growHours = SeedTable.Grow(goalRow);
            var yieldSoilName = GardeningItems.SoilName(SoilFamily.Shroud, 3);
            if (growHours is null)
            {
                ImGui.TextColored(HubStyle.Faint, Strings.Goal_GrowTimeUnknown);
                return;
            }

            var days = growHours.Value > 48
                ? Phrases.AboutDays(Math.Round(growHours.Value / 24.0))
                : Phrases.AboutHours(Math.Round((double)growHours.Value));
            var yields = SeedTable.Yields(goalRow);
            var cropText = Phrases.OneNumberOrRange(yields?.Crop);
            var seedText = Phrases.OneNumberOrRange(yields?.Seed);

            ImGui.TextWrapped(Loc.Format(Strings.Goal_AlreadyMetPlan, yieldSoilName, days, cropText, produceName, seedText));

            var seedTiers = yields?.Seed;
            if (seedTiers is { Length: > 0 } && seedTiers.All(y => y == seedTiers[0]))
            {
                ImGui.TextWrapped(seedTiers[0] switch
                {
                    0 => Strings.Goal_SeedReturnNone,
                    1 => Strings.Goal_SeedReturnOne,
                    _ => Loc.Format(Strings.Goal_SeedReturnMany, Formats.Number(seedTiers[0])),
                });
            }
        }

        private static void DrawGoalNoCrossbreedNeeded(uint goalRow)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextUnformatted(Loc.Format(Strings.Goal_NoCrossbreedNeeded, produceName));
            var sources = SeedTable.Sources(goalRow);
            ImGui.TextColored(HubStyle.Faint, sources.Count > 0
                ? Loc.Format(Strings.Goal_SourcesWhere, string.Join("; ", sources))
                : Strings.Goal_SourcesNotRecorded);
        }

        private static void DrawGoalNoRoute(uint goalRow)
        {
            var produceName = SeedItems.ProduceName(goalRow);
            ImGui.TextWrapped(Loc.Format(Strings.Goal_NoRouteCannotGrow, produceName));
            ImGui.TextWrapped(Strings.Goal_NoRouteNoParentPair);
            ImGui.TextWrapped(Strings.Goal_NoRouteCheckSettings);
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
                    ImGui.TextColored(HubStyle.Faint, Strings.Goal_NeverSeenGarden);
                    beds = Array.Empty<BedState>();
                    observedAt = null;
                }
                else
                {
                    beds = records
                        .Select(r => new BedState(r.PatchKey, r.BedNumber, r.SeedRow, r.LastSeenStage, 0, 0, r.LastSeenAt))
                        .ToList();
                    observedAt = records.Max(r => r.LastSeenAt);
                    ImGui.TextColored(HubStyle.Faint, Strings.Goal_NotAtGarden);
                    ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Goal_LastVisitAge, GoalAgePhrase(observedAt.Value)));
                }
            }

            var status = GoalProgress.Evaluate(plan, held, beds, observedAt);

            var totalSteps = plan.Steps.Max(s => s.Number);
            var currentNumber = plan.Steps[status.CurrentStepIndex].Number;
            ImGui.TextUnformatted(Loc.Format(Strings.Goal_StepProgress, Formats.Number(currentNumber), Formats.Number(totalSteps)));
            var bestCase = plan.BestCaseDuration;
            var bestCaseDuration = bestCase.TotalHours > 48
                ? Phrases.Days(Math.Round(bestCase.TotalDays))
                : Phrases.Hours(Math.Round(bestCase.TotalHours));
            ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Goal_TotalStepsCount, Formats.Number(totalSteps)));
            ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Goal_BestCaseDuration, bestCaseDuration));
            ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Goal_PeakBeds, Formats.Number(plan.PeakBeds)));

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
            // ###-suffixed against number rather than title text: title will be translated, and a
            // CollapsingHeader's open/collapsed state is keyed off its own label.
            var open = ImGui.CollapsingHeader(
                $"{Loc.Format(Strings.Goal_StepHeader, Formats.Number(number), members[0].Step.Title)}###goalstep-{number}");

            // Right-aligned against the panel edge rather than trailing the title: a plain SameLine
            // sits the marker hard against however long the step's own title happens to be, so it
            // reads as part of the sentence instead of as a status.
            var marker = isCurrent ? Strings.Goal_MarkerNow : allDone ? Strings.Goal_MarkerDone : null;
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
                        ObtainStep o => Loc.Format(Strings.Goal_StepDoneWithSeed, Formats.Number(held.GetValueOrDefault(o.SeedRow)), SeedItems.SeedItemName(o.SeedRow)),
                        CrossStep c => Loc.Format(Strings.Goal_StepDoneWithSeed, Formats.Number(held.GetValueOrDefault(c.TargetRow)), SeedItems.SeedItemName(c.TargetRow)),
                        _ => Strings.Goal_StepDonePlain,
                    };
                    return (doneText, HubStyle.Good);

                case GoalStepStatus.AttemptUnresolved:
                    return (Loc.Format(Strings.Goal_AttemptUnresolved, Formats.Number(progress.BedNumber ?? 0)), HubStyle.Text);

                case GoalStepStatus.CrossedAndGrowing:
                    return GrowingStatusLine(progress);

                default:
                    return (progress.Blocker ?? Strings.Goal_NotStarted, HubStyle.Text);
            }
        }

        private static (string Text, Vector4 Color) GrowingStatusLine(GoalStepProgress progress)
        {
            var bedNumber = progress.BedNumber ?? 0;
            var bedNumberText = Formats.Number(bedNumber);
            var record = progress.PatchKey is { } key && progress.BedNumber is { } bn ? GardenJournal.Get(key, bn) : null;

            if (record is null || progress.Window is not { Confidence: not HarvestConfidence.Unknown } window)
                return (Loc.Format(Strings.Goal_GrowingUnknownTiming, bedNumberText), HubStyle.Text);

            var now = DateTimeOffset.UtcNow;
            var earliest = window.Earliest;
            if (earliest is { } e0 && now >= e0)
                return (Loc.Format(Strings.Goal_ReadyToHarvest, bedNumberText), HubStyle.Good);

            var wiltsAt = Growth.WiltsAt(record);
            if (wiltsAt is { } wa && now >= wa)
            {
                // Already wilted. Growth.WithersAt is null once the bed reads mature (stage 4, never
                // withers) or wilt data is missing entirely, either of which means there is no real
                // deadline to warn about here.
                if (Growth.WithersAt(record) is { } withersAt && now < withersAt)
                    return (Loc.Format(Strings.Goal_WiltedTendBeforeWithers, bedNumberText), HubStyle.Bad);
            }
            else if (wiltsAt is { } wiltDeadline)
            {
                var untilWilt = wiltDeadline - now;
                if (untilWilt <= TimeSpan.FromHours(6))
                {
                    var hoursLeft = Math.Max(0, (int)Math.Ceiling(untilWilt.TotalHours));
                    var template = hoursLeft == 1 ? Strings.Goal_TendWithinHours_One : Strings.Goal_TendWithinHours_Other;
                    return (Loc.Format(template, bedNumberText, Formats.Number(hoursLeft)), HubStyle.Warn);
                }
            }

            if (earliest is not { } e)
                return (Loc.Format(Strings.Goal_GrowingNoEarliest, bedNumberText), HubStyle.Text);

            var remaining = e - now;
            var remainingPhrase = remaining.TotalHours > 48 ? Phrases.Days(Math.Round(remaining.TotalDays)) : Phrases.Hours(Math.Round(remaining.TotalHours));
            // Two sibling whole sentences rather than a concatenated ", estimated" suffix: a trailing
            // comma-clause does not compose in Spanish, so the confidence note picks a different key
            // instead of being appended to the end of the plain sentence.
            var timeLeftTemplate = window.Confidence == HarvestConfidence.Estimated
                ? Strings.Goal_GrowingTimeLeftEstimated
                : Strings.Goal_GrowingTimeLeft;
            return (Loc.Format(timeLeftTemplate, bedNumberText, remainingPhrase), HubStyle.Text);
        }

        /// <summary>The pair strip, economics and handoff for the one step currently in progress — the
        /// only step whose planting detail matters right now. Renders nothing when the current step is
        /// an obtain step, since there is no bed to strip yet.</summary>
        private static void DrawGoalCurrentStepDetail(
            GoalPlan plan, List<(GoalStep Step, int Index)> members, GoalStatus status, bool atGarden,
            IReadOnlyDictionary<uint, int> held)
        {
            var crossStep = members.Select(m => m.Step).OfType<CrossStep>().FirstOrDefault();
            if (crossStep is null)
                return;

            if (!atGarden)
            {
                ImGui.TextColored(HubStyle.Faint, Strings.Goal_StandAtGardenSeeBeds);
                DrawGoalHandoffDisabled(Strings.Goal_StandAtGardenPlantHandoff);
                return;
            }

            var patch = PatchDiscovery.Patches.First();

            if (MissingSeedForHandoff(crossStep, held) is { } missing)
            {
                DrawGoalHandoffDisabled(missing);
                return;
            }

            // A fill step needs both soils: the yield soil for the parent beds and the cross soil for
            // the beds planted against them.
            var bag = Bags.Scan();
            foreach (var preference in new[] { Plugin.C.SoilForCross, Plugin.C.SoilForYield })
            {
                if (GardeningItems.BestSoil(preference, bag) is not null)
                    continue;

                var reason = GardeningItems.SoilUnavailable(preference) ?? Strings.Goal_NoSoilForStepFallback;
                var hint = preference == SoilPreference.Fixed
                    ? ""
                    : Loc.Format(Strings.Goal_MineHint, SoilSources.Where(SoilFamilyForPreference(preference), 3));
                DrawGoalHandoffDisabled(hint.Length > 0 ? $"{reason} {hint}" : reason);
                return;
            }

            // The seed the cross is meant to produce, not either parent: the planner is being asked
            // what to lay out to obtain it.
            var targetSeed = (ushort)crossStep.TargetRow;

            var memory = GardenMemory.Read(patch);
            var singleStepPlan = CrossPlanner.PlanFillStep(targetSeed, crossStep.Beds, patch, memory, bag);

            if (singleStepPlan.Steps.Count > 0)
            {
                if (ImGui.BeginTable($"##goalpair-{patch.Key}", 2, ImGuiTableFlags.Borders))
                {
                    ImGui.TableNextRow();
                    foreach (var step in singleStepPlan.Steps.OrderBy(s => s.BedNumber))
                    {
                        ImGui.TableNextColumn();
                        var label = singleStepPlan.ExpectedTargets.ContainsKey(step.BedNumber)
                            ? Loc.Format(Strings.Goal_PairBedPlantHere, Formats.Number(step.BedNumber), SeedItems.ProduceName(step.SeedRow))
                            : Loc.Format(Strings.Goal_PairBedExisting, Formats.Number(step.BedNumber), SeedItems.ProduceName(step.SeedRow));
                        ImGui.TextUnformatted($"[{label}]");
                    }
                    ImGui.EndTable();
                }
            }

            foreach (var warning in singleStepPlan.Warnings)
                ImGui.TextColored(HubStyle.Warn, warning);

            DrawGoalHandoff(patch, singleStepPlan);
        }

        /// <summary>The whole disabled-reason sentence for the handoff when one more planting of the
        /// current step needs a seed it does not have — null when both are covered. Returns a complete
        /// sentence rather than a noun-phrase fragment, since "1 more X" and "X and Y" inflect the host
        /// sentence differently in Spanish and so cannot share one template with a caller-built subject.
        /// A route can want several attempts of a pair over its lifetime, but a single "Plant this step"
        /// press only ever plants one, so this checks against 1, not the route's total.</summary>
        private static string? MissingSeedForHandoff(CrossStep step, IReadOnlyDictionary<uint, int> held)
        {
            if (held.GetValueOrDefault(step.FirstSeedRow) < 1 && held.GetValueOrDefault(step.SecondSeedRow) < 1)
                return Loc.Format(Strings.Goal_NeedBothSeeds, SeedItems.SeedItemName(step.FirstSeedRow), SeedItems.SeedItemName(step.SecondSeedRow));
            if (held.GetValueOrDefault(step.FirstSeedRow) < 1)
                return Loc.Format(Strings.Goal_NeedOneMoreSeed, SeedItems.SeedItemName(step.FirstSeedRow));
            if (held.GetValueOrDefault(step.SecondSeedRow) < 1)
                return Loc.Format(Strings.Goal_NeedOneMoreSeed, SeedItems.SeedItemName(step.SecondSeedRow));
            return null;
        }

        private static void DrawGoalHandoffDisabled(string reason) => ImGui.TextColored(HubStyle.Faint, reason);

        /// <summary>The one-step handoff: a single <see cref="HubStyle.Primary"/> control that plants
        /// exactly the beds named above and stops. Never queues a second step and never re-runs itself:
        /// Gardener plants one step, when asked, and stops.</summary>
        private static void DrawGoalHandoff(Patch patch, LayoutPlan singleStepPlan)
        {
            var canPlant = singleStepPlan.Steps.Count > 0 && CrossPlanner.Verify(singleStepPlan, patch);
            if (!canPlant)
            {
                DrawGoalHandoffDisabled(Strings.Goal_CannotFitStep);
                return;
            }

            using (HubStyle.Primary())
            {
                // Borrows the game's own "Plant Seeds" verb (Decision 2): the label then names the same
                // action the bed menu itself offers, correct in every client language for free.
                if (ImGui.Button(Loc.Format(Strings.Goal_PlantStepButton, GameWords.Action(MenuKey.SetSeed))))
                {
                    if (Plugin.C.ConfirmBeforeRun)
                        ImGui.OpenPopup("Confirm plant goal step");
                    else
                        StartGoalStep(singleStepPlan, patch);
                }
            }

            // Popup identifier, not copy: BeginPopup never draws this string, and it must match the
            // OpenPopup call above verbatim, so it stays literal English.
            if (!ImGui.BeginPopup("Confirm plant goal step"))
                return;

            ImGui.TextUnformatted(Strings.Goal_ConfirmPlantStepTitle);

            // Prose in an auto-sized popup has nothing to wrap against, so ImGui sizes the popup to the
            // button row instead and the sentence stacks into a narrow column. A fixed wrap position
            // gives the popup a width to size itself to.
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ConfirmWrapWidth);
            ImGui.TextUnformatted(Loc.Format(Strings.Goal_ConfirmPlantStepBody, PlantingPhrase(singleStepPlan)));
            ImGui.PopTextWrapPos();
            if (ImGui.Button(Strings.Common_Confirm))
            {
                StartGoalStep(singleStepPlan, patch);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Strings.Common_Cancel))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        private static void StartGoalStep(LayoutPlan singleStepPlan, Patch patch)
        {
            SchedulerMain.PendingPlan = singleStepPlan;
            SchedulerMain.EnablePlugin(SweepKind.Plan, patch);
        }

        /// <summary>What a confirm popup's prose wraps at, in the same unscaled pixels as the window's
        /// own <see cref="WindowSizeConstraints"/>.</summary>
        private const float ConfirmWrapWidth = 340f;

        /// <summary>The confirm's roster of what goes where, grouped by seed and soil so a full patch
        /// reads as two clauses rather than eight: "Mandrake Seeds in beds 1, 3, 5 and 7, using Grade 3
        /// Shroud Topsoil; Almond Seeds in beds 2, 4, 6 and 8, using Grade 3 Thanalan Topsoil". A fill
        /// step plants two soils, since only the crossing beds take the soil that moves the intercross
        /// rate, so naming one for the whole plan would misreport half of it.</summary>
        private static string PlantingPhrase(LayoutPlan plan)
        {
            var clauses = plan.Steps
                .GroupBy(s => (s.SeedRow, s.SoilItemId),
                    (key, steps) => (key.SeedRow, key.SoilItemId, Beds: steps.Select(s => s.BedNumber).OrderBy(n => n).ToList()))
                .OrderBy(g => g.Beds[0])
                .Select(g =>
                {
                    var bedList = TextList.And(g.Beds.Select(b => Formats.Number(b)).ToList());
                    var template = g.Beds.Count == 1 ? Strings.Goal_PlantingClause_One : Strings.Goal_PlantingClause_Other;
                    return Loc.Format(template, SeedItems.SeedItemName(g.SeedRow), bedList, ItemSheet.Name(g.SoilItemId));
                });
            return string.Join("; ", clauses);
        }

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

        private static string GoalAgePhrase(DateTimeOffset at)
        {
            var span = DateTimeOffset.UtcNow - at;
            return span.TotalHours < 1
                ? Phrases.Minutes(Math.Max(1, (int)span.TotalMinutes))
                : span.TotalHours > 48 ? Phrases.Days(Math.Round(span.TotalDays)) : Phrases.Hours(Math.Round(span.TotalHours));
        }

        /// <summary>The action column's one control for an occupied bed, never part of a sweep:
        /// <see cref="GardenerGuard.BlockingReason"/> gates starting a sweep, but removing a crop is a
        /// single interaction rather than a worklist, so the only gate here is the confirm itself,
        /// which names the seed being destroyed. Falls back to <c>ImGui.SmallButton("×")</c> (U+00D7,
        /// outside <c>check_loc.py</c>'s letter check) if the FontAwesome trash glyph does not render —
        /// unverifiable offline, see AGENTS.md's "Needs in-game verification".</summary>
        private static void DrawRemoveCropButton(Patch patch, BedState state)
        {
            ImGui.PushID($"removecrop-{patch.Key}-{state.BedNumber}");

            var name = SeedItems.ProduceName(state.SeedRow);
            var popupId = $"Confirm remove crop##{patch.Key}-{state.BedNumber}";
            if (ImGuiComponents.IconButton("removecrop", FontAwesomeIcon.Trash))
                ImGui.OpenPopup(popupId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Format(Strings.Garden_RemoveButtonTooltip,
                    GameWords.Action(MenuKey.Dispose), Formats.Number(state.BedNumber), name));

            if (ImGui.BeginPopup(popupId))
            {
                ImGui.TextColored(HubStyle.Warn,
                    Loc.Format(Strings.RemoveCrop_ConfirmMessage, GameWords.Action(MenuKey.Dispose), name, Formats.Number(state.BedNumber)));
                if (ImGui.Button(Strings.Common_Confirm))
                {
                    Task_RemoveCrop.TryEnqueue(patch, state.BedNumber, state.SeedRow);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button(Strings.Common_Cancel))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        private static void DrawLogTab()
        {
            foreach (var entry in ActivityLog.Entries)
                ImGui.TextColored(entry.Color, Loc.Format(Strings.Log_EntryLine, entry.Time, entry.Message));
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
            ImGui.TextColored(HubStyle.Warn, Strings.Reminders_DueToTendHeader);
            DrawReminderGroup(Reminders.DueToTend, HubStyle.Warn, Strings.Reminders_AllTended,
                e => Loc.Format(Strings.Reminders_TendLine, e.SeedName, Formats.Number(e.BedNumber), Formats.LocalDateTime(e.At.ToLocalTime())),
                showReach: false);

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Bad, Strings.Reminders_AboutToWitherHeader);
            DrawReminderGroup(Reminders.AboutToWither, HubStyle.Bad, Strings.Reminders_NothingWithering,
                e => Loc.Format(Strings.Reminders_WitherLine, e.SeedName, Formats.Number(e.BedNumber), Formats.LocalDateTime(e.At.ToLocalTime())),
                showReach: false);

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Good, Strings.Reminders_ReadyHeader);
            ImGui.TextColored(HubStyle.Faint, Strings.Garden_HarvestLagNote);
            DrawHarvestGroup();

            ImGui.Separator();
            ImGui.TextColored(HubStyle.Faint, Strings.Reminders_TimingUnknownHeader);
            DrawReminderGroup(Reminders.TimingUnknown, HubStyle.Faint, Strings.Reminders_AllTimingKnown,
                e => Loc.Format(Strings.Reminders_TimingUnknownLine, e.SeedName, Formats.Number(e.BedNumber)),
                showReach: true);
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
                ImGui.TextColored(HubStyle.Faint, Strings.Reminders_NothingReady);
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
                            ? Loc.Format(Strings.Reminders_HarvestWindowLine, harvest.Entry.SeedName,
                                Formats.Number(harvest.Entry.BedNumber),
                                Formats.LocalDateTime(harvest.Window.Earliest!.Value.ToLocalTime()),
                                Formats.LocalTime(harvest.Window.Latest!.Value.ToLocalTime()),
                                HarvestConfidenceText(harvest.Window))
                            : Loc.Format(Strings.Reminders_HarvestMatureLine, harvest.Entry.SeedName,
                                Formats.Number(harvest.Entry.BedNumber));
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
            ImGui.TextColored(HubStyle.Faint, Loc.Format(Strings.Reminders_ReachSuffix, Reminders.ReachText(reachableBy)));
        }

        /// <summary>Shared by <see cref="DrawHarvestWindow"/> and <see cref="DrawHarvestGroup"/> so the
        /// four confidence words carry exactly one key set.</summary>
        private static string HarvestConfidenceText(HarvestWindow window) => window.Confidence switch
        {
            HarvestConfidence.Estimated => Strings.Harvest_ConfidenceEstimated,
            HarvestConfidence.Bundled => Strings.Harvest_ConfidenceBundled,
            HarvestConfidence.Calibrated => Loc.Format(Strings.Harvest_ConfidenceObserved, Formats.Number(window.SampleCount)),
            _ => Strings.Common_Unknown,
        };

        private static void DrawHouseHeader(string houseKey)
        {
            var estateType = GardenJournal.EstateTypeFor(houseKey);
            ImGui.TextUnformatted(estateType is { } et ? et.ToString() : Strings.Reminders_HouseFallback);
            ImGui.SameLine();
            ImGui.TextColored(HubStyle.Faint, houseKey);
        }

        /// <summary>Semantic bed-state colour per THEME.md: withered or overdue → Bad, due to tend soon
        /// → Warn, ready or mature → Good, empty or timing-unknown → Faint. Nothing here is a domain
        /// palette; these are the four roles HubStyle already exposes. Colours both the crop name and
        /// the growth meter, so the two cells never disagree about a bed's condition.</summary>
        private static Vector4 BedColor(BedState state, BedRecord? record)
        {
            if (state.IsEmpty)
                return HubStyle.Faint;
            if (record?.ObservedWithered == true)
                return HubStyle.Bad;
            if (record is { } withRecord && Growth.HarvestWindow(withRecord).Earliest is { } earliest && earliest <= DateTimeOffset.UtcNow)
                return HubStyle.Good;
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

        /// <summary>The bed table's "When" column: exactly one whole sentence, in branch order —
        /// withered and wilted take priority over ready, and ready takes priority over the harvest
        /// range, so a bed that needs tending is never upstaged by a stale harvest estimate. Confidence,
        /// uncertainty and the exact window live in <see cref="DrawBedTooltip"/> instead, not here.</summary>
        private static void DrawWhenCell(string patchKey, int bedNumber, BedState state, BedRecord? record)
        {
            if (record is not { } rec)
            {
                ImGui.TextColored(HubStyle.Faint, Strings.Garden_WhenNotRecordedYet);
                return;
            }

            if (rec.ObservedWithered)
            {
                ImGui.TextColored(HubStyle.Bad, Strings.Garden_WhenWithered);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var wiltsAt = Growth.WiltsAt(rec);
            if (wiltsAt is { } wa && wa <= now)
            {
                if (Growth.WithersAt(rec) is { } withersAt && withersAt > now)
                    ImGui.TextColored(HubStyle.Bad,
                        Loc.Format(Strings.Garden_WhenWiltedTendBefore, Formats.LocalDateTime(withersAt.ToLocalTime())));
                else
                    ImGui.TextColored(HubStyle.Warn, Strings.Garden_WhenWiltedTendNow);
                return;
            }

            var window = Growth.HarvestWindow(rec);
            if (window.Earliest is { } earliest && earliest <= now)
            {
                ImGui.TextColored(HubStyle.Good, Strings.Garden_WhenReadyNow);
                return;
            }

            if (state.Maturity == Maturity.MatureCandidate && window.Confidence == HarvestConfidence.Unknown)
            {
                ImGui.TextColored(HubStyle.Good, Strings.Garden_WhenMature);
                return;
            }

            if (wiltsAt is { } upcomingWilt && upcomingWilt <= now + TimeSpan.FromHours(Plugin.C.WiltWarningHours))
            {
                ImGui.TextColored(HubStyle.Warn, Loc.Format(Strings.Garden_WhenTendBy, Formats.LocalDateTime(upcomingWilt.ToLocalTime())));
                return;
            }

            if (window.Confidence != HarvestConfidence.Unknown && window.Earliest is { } e && window.Latest is { } l)
            {
                ImGui.TextUnformatted(Loc.Format(Strings.Garden_WhenReadyRange, Formats.LocalDateTime(e.ToLocalTime()), Formats.LocalTime(l.ToLocalTime())));
                return;
            }

            if (rec.PlantedAt is null)
            {
                var popupId = $"Set planting time##{patchKey}-{bedNumber}";
                if (ImGui.SmallButton($"{Strings.Garden_SetPlantingTimeButton}##setplant-{patchKey}-{bedNumber}"))
                    ImGui.OpenPopup(popupId);
                if (ImGui.BeginPopup(popupId))
                {
                    ImGui.TextUnformatted(Strings.Garden_PlantingTimePopupHeading);
                    DrawPlantedAtEstimate(patchKey, bedNumber, rec);
                    ImGui.EndPopup();
                }
                return;
            }

            ImGui.TextColored(HubStyle.Faint, Strings.Common_Unknown);
        }

        /// <summary>Every fact the density pass moved off the row: growth stage, soil, exact wilt time,
        /// the full harvest window with its confidence and anchor uncertainty, the last chat-observed
        /// crop condition, and who last saw the bed. <see cref="ImGui.BeginTooltip"/>/<c>EndTooltip</c>
        /// rather than <c>SetTooltip</c> so the crop-condition line keeps the Good/Warn/Bad/Faint colour
        /// it has always had — a single joined <c>SetTooltip</c> string would render everything in one
        /// colour and quietly drop that distinction.</summary>
        private static void DrawBedTooltip(Patch patch, int bedNumber, BedState state, BedRecord? record)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(400f);

            ImGui.TextColored(HubStyle.Text, Loc.Format(Strings.Garden_BedTooltipHeading, Formats.Number(bedNumber)));

            var stageText = state.Maturity switch
            {
                Maturity.MatureCandidate => Strings.Garden_StageMature,
                Maturity.Growing => Loc.Format(Strings.Garden_StageGrowing, Formats.Number(state.Stage)),
                _ => Strings.Common_Empty,
            };
            ImGui.TextColored(HubStyle.Text, stageText);

            // Only ever known for a bed Gardener planted itself: nothing in the game's own read says
            // what soil a bed went in with, so a bed planted by hand shows no soil rather than a guess.
            if (record?.SoilItemId is { } soilItemId and not 0)
                ImGui.TextColored(HubStyle.Text, ItemSheet.Name(soilItemId));

            if (record is { } rec)
            {
                if (Growth.WiltsAt(rec) is { } wiltsAt)
                    ImGui.TextColored(HubStyle.Text, Formats.LocalDateTime(wiltsAt.ToLocalTime()));

                var window = Growth.HarvestWindow(rec);
                if (window.Confidence != HarvestConfidence.Unknown)
                {
                    var confidenceText = HarvestConfidenceText(window);
                    var when = window.Earliest is { } e && window.Latest is { } l
                        ? Loc.Format(Strings.Garden_HarvestWindowRange, Formats.LocalDateTime(e.ToLocalTime()), Formats.LocalTime(l.ToLocalTime()))
                        : Strings.Garden_HarvestWindowUnknown;
                    // A hand-set estimate already reads as "estimated" via HarvestConfidenceText, so
                    // this never doubles up with it: AnchorUncertainty is null exactly when the
                    // estimate widget last wrote PlantedAt.
                    var line = window.AnchorUncertainty is { } anchorUncertainty
                        ? Loc.Format(Strings.Garden_HarvestWindowLineWithUncertainty, when, confidenceText, FormatUncertainty(anchorUncertainty))
                        : Loc.Format(Strings.Garden_HarvestWindowLine, when, confidenceText);
                    ImGui.TextColored(HubStyle.Text, line);
                }

                // The most recent TALK_* sentence chat has echoed for this bed (see CropChatState) —
                // the only source for wilted and true harvest-readiness the bed menu itself never
                // offers. An unobserved condition prints nothing, rather than implying healthy.
                if (rec.LastObservedCropState is { } cropState && rec.LastObservedCropStateAt is { } observedAt)
                {
                    // Gardener's own one-word crop-condition summary, not GardenMenuText's TALK_*
                    // sentence - the do-not-translate register does not apply here. An unclassified
                    // MenuKey (the switch's default arm) falls back to the raw enum name, which does.
                    var (text, color) = cropState switch
                    {
                        MenuKey.TalkVigorous => (Strings.Garden_CropVigorous, HubStyle.Good),
                        MenuKey.TalkDepressed => (Strings.Garden_CropWilted, HubStyle.Warn),
                        MenuKey.TalkRipe => (Strings.Garden_CropRipe, HubStyle.Good),
                        MenuKey.TalkDead => (Strings.Garden_CropWithered, HubStyle.Bad),
                        MenuKey.TalkNone => (Strings.Garden_CropEmpty, HubStyle.Faint),
                        _ => (cropState.ToString(), HubStyle.Faint),
                    };
                    var agoHours = (DateTimeOffset.UtcNow - observedAt).TotalHours;
                    ImGui.TextColored(color, Loc.Format(Strings.Garden_CropStateAge, text, Formats.Number(agoHours, "F0")));
                }

                if (rec.LastSeenByCharacter is { Length: > 0 } observer)
                {
                    var agoHours = (DateTimeOffset.UtcNow - rec.LastSeenAt).TotalHours;
                    ImGui.TextColored(HubStyle.Text, Loc.Format(Strings.Garden_SeenBy, observer, Formats.Number(agoHours, "F0")));
                }
            }

            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        /// <summary>Renders a duration the way its own magnitude deserves: seconds for a transition
        /// caught within the same poll, hours for one caught only after a long absence, never a single
        /// unit that misrepresents either end.</summary>
        private static string FormatUncertainty(TimeSpan span)
        {
            if (span.TotalMinutes < 1)
                return Loc.Format(Strings.Garden_UncertaintySeconds, Formats.Number(span.TotalSeconds, "F0"));
            if (span.TotalHours < 1)
                return Loc.Format(Strings.Garden_UncertaintyMinutes, Formats.Number(span.TotalMinutes, "F0"));
            return Loc.Format(Strings.Garden_UncertaintyHours, Formats.Number(span.TotalHours, "F1"));
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
            var setEstimateLabel = Loc.Format(Strings.Garden_PlantedHoursAgoButton, Formats.Number(hours));
            if (ImGui.Button($"{setEstimateLabel}##setEstimate-{patchKey}-{bedNumber}"))
            {
                record.PlantedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(hours);
                record.PlantedAtEstimated = true;
                record.PlantedAtUncertainty = null; // a hand-set guess replaces any transition anchor
                GardenJournal.Upsert(record);
                pendingEstimateHours.Remove(key);

                // The only caller draws this inside a popup, and the estimate is a one-shot: leaving
                // the popup open after the write reads as though nothing happened and invites a
                // second click that sets the time again from a stale box.
                ImGui.CloseCurrentPopup();
            }
        }
    }
}
