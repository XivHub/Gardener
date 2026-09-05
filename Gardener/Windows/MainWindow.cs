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

        // The Plan tab's own target picker, shared across every patch drawn below it rather than
        // one per patch: it names what to plant next, not where, and the "where" is a per-patch
        // question the plan itself answers. 0 means nothing chosen yet.
        private static ushort planTargetSeed;

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
            if (ImGui.BeginTabItem("Plan"))
            {
                DrawPlanTab();
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
        /// The crossbreed planner: a target picker over every row <see cref="SeedTable.CrossableTargets"/>
        /// names (greyed with a reason for one <see cref="SeedTable.DataGaps"/> flags), then one block
        /// per discovered patch showing what <see cref="CrossPlanner.PlanSingleStep"/> would plant there
        /// for that target, or <see cref="CrossPlanner.Route"/>'s suggested chain when no single step is
        /// possible from what is held or already growing. <see cref="Task_RemoveCrop"/>'s per-bed button
        /// lives at the bottom of each patch's block, never gated by anything a sweep also gates on.
        /// </summary>
        private static void DrawPlanTab()
        {
            DrawSchedulerStatus();
            ImGui.Separator();

            var targets = SeedTable.CrossableTargets.OrderBy(SeedItems.ProduceName).ToList();
            var preview = planTargetSeed == 0 ? "Choose a seed..." : SeedItems.ProduceName(planTargetSeed);
            if (ImGui.BeginCombo("Target seed", preview))
            {
                foreach (var row in targets)
                {
                    var gapReason = TargetGapReason((ushort)row);
                    ImGui.BeginDisabled(gapReason is not null);
                    if (ImGui.Selectable(SeedItems.ProduceName(row), row == planTargetSeed) && gapReason is null)
                        planTargetSeed = (ushort)row;
                    ImGui.EndDisabled();
                    if (gapReason is { } reason)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(HubStyle.Faint, reason);
                    }
                }
                ImGui.EndCombo();
            }

            if (planTargetSeed == 0)
            {
                ImGui.TextColored(HubStyle.Faint, "Pick a seed to plan a cross for.");
                return;
            }

            var patches = PatchDiscovery.Patches;
            if (patches.Count == 0)
            {
                ImGui.TextColored(HubStyle.Faint, "No patches discovered. Stand in an outdoor housing plot.");
                return;
            }

            foreach (var patch in patches)
                DrawPlanForPatch(patch, planTargetSeed);
        }

        /// <summary>Why <paramref name="row"/> is greyed in the target picker, or null when it is not:
        /// checked against every <see cref="SeedTable.DataGaps"/> list a crossable target could actually
        /// land in, never a blanket "unavailable".</summary>
        private static string? TargetGapReason(ushort row)
        {
            var gaps = SeedTable.DataGaps;
            if (gaps.BundledRowsMissingFromSheet.Any(g => g.Row == row))
                return "not found in the live seed sheet";
            if (gaps.RowsAbsentFromCrossData.Any(g => g.Row == row))
                return "missing cross data";
            if (gaps.RowsWithNoGrowTime.Any(g => g.Row == row))
                return "no grow time data";
            return null;
        }

        private static void DrawPlanForPatch(Patch patch, ushort target)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{patch.Kind} patch");
            ImGui.TextColored(HubStyle.Faint, patch.Key);

            var memory = GardenMemory.Read(patch);
            if (memory.Count == 0)
            {
                ImGui.TextColored(HubStyle.Faint, "no data for this patch");
                return;
            }

            var bag = Bags.Scan();
            var plan = CrossPlanner.PlanSingleStep(target, patch, memory, bag);

            if (plan.Steps.Count == 0)
                DrawRouteView(patch, target, plan, bag);
            else
                DrawPlanSteps(patch, plan);

            DrawRemoveCropButtons(patch, memory);
        }

        private static void DrawRouteView(Patch patch, ushort target, LayoutPlan plan, IReadOnlyList<SlotView> bag)
        {
            foreach (var warning in plan.Warnings)
                ImGui.TextColored(HubStyle.Warn, warning);

            var held = bag
                .Select(s => SeedItems.SeedRowForItem(s.ItemId))
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .Distinct()
                .ToList();
            var route = CrossPlanner.Route(target, held);
            ImGui.TextColored(HubStyle.Faint, route.Summary);

            foreach (var step in route.Steps)
            {
                var a = SeedItems.ProduceName(step.ParentA);
                var b = SeedItems.ProduceName(step.ParentB);
                var t = SeedItems.ProduceName(step.Target);
                var yieldText = step.SeedYield is { } y ? $"{y} seed(s)" : "seed yield unknown";
                var sustainText = step.Sustains ? "sustains itself" : "won't sustain itself alone";
                ImGui.TextUnformatted($"{a} x {b} -> {t} ({yieldText}, {sustainText})");
            }
        }

        private static void DrawPlanSteps(Patch patch, LayoutPlan plan)
        {
            if (ImGui.BeginTable($"##plan-{patch.Key}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Bed");
                ImGui.TableSetupColumn("Plant");
                ImGui.TableSetupColumn("Soil");
                ImGui.TableSetupColumn("Why");
                ImGui.TableHeadersRow();

                foreach (var step in plan.Steps.OrderBy(s => s.BedNumber))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(step.BedNumber.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(SeedItems.ProduceName(step.SeedRow));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(ItemSheet.Name(step.SoilItemId));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(step.Why);
                }

                ImGui.EndTable();
            }

            foreach (var warning in plan.Warnings)
                ImGui.TextColored(HubStyle.Warn, warning);

            if (GardenerGuard.BlockingReason() is { } reason)
            {
                ImGui.TextColored(HubStyle.Faint, reason);
                return;
            }

            using (HubStyle.Primary())
            {
                if (ImGui.Button($"Run plan##runplan-{patch.Key}"))
                {
                    if (Plugin.C.ConfirmBeforeRun)
                        ImGui.OpenPopup($"Confirm run plan##{patch.Key}");
                    else
                        StartPlan(plan, patch);
                }
            }

            if (!ImGui.BeginPopup($"Confirm run plan##{patch.Key}"))
                return;

            ImGui.TextUnformatted($"Plant {plan.Steps.Count} bed(s) on this patch as shown above?");
            if (ImGui.Button("Confirm"))
            {
                StartPlan(plan, patch);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        private static void StartPlan(LayoutPlan plan, Patch patch)
        {
            SchedulerMain.PendingPlan = plan;
            SchedulerMain.EnablePlugin(SweepKind.Plan, patch);
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
