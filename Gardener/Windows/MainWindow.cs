using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Scheduler;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class MainWindow : Window, IDisposable
    {
        // Staged hours for each bed's "planted about N hours ago" control, keyed the same way the
        // journal itself is (patch + bed, never a character) so the widget survives a redraw.
        private static readonly Dictionary<(string PatchKey, int BedNumber), int> pendingEstimateHours = new();

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
                ImGui.EndTabItem();
            if (ImGui.BeginTabItem("Reminders"))
                ImGui.EndTabItem();
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

            var plot = PatchDiscovery.LastDiagnostics.CurrentPlot;
            var plotText = plot is { } p ? $"plot {p + 1}" : "plot unknown";

            foreach (var patch in patches)
            {
                var houseKey = patch.Key.Split(':')[0];
                ImGui.TextUnformatted($"{patch.Kind} patch — {plotText}");
                ImGui.TextColored(HubStyle.Faint, patch.Key);

                var estateType = GardenJournal.EstateTypeFor(houseKey);
                var accessible = GardenJournal.CharactersWithAccess(houseKey);
                if (estateType is not null || accessible.Count > 0)
                {
                    var estateText = estateType is { } et ? et.ToString() : "unknown";
                    var accessText = accessible.Count > 0 ? string.Join(", ", accessible) : "unknown";
                    ImGui.TextColored(HubStyle.Faint, $"Estate: {estateText} — reachable by: {accessText}");
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
                SchedulerMain.DisablePlugin();
        }

        /// <summary>"Tend all" / "Harvest all" for one patch, each behind
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

        private static void DrawLogTab()
        {
            foreach (var entry in ActivityLog.Entries)
                ImGui.TextColored(entry.Color, $"[{entry.Time}] {entry.Message}");
        }

        private static void DrawSeedAndStage(BedState state, BedRecord? record)
        {
            var seedItemId = SeedItems.SeedItemForRow(state.SeedRow);
            var seedName = seedItemId is { } id ? XivHubPluginKit.Inventory.ItemSheet.Name(id) : $"row {state.SeedRow}";
            var stageText = state.Maturity switch
            {
                Maturity.MatureCandidate => "mature (4)",
                Maturity.Growing => $"growing ({state.Stage} of 4)",
                _ => "empty",
            };

            ImGui.TextUnformatted(seedName);
            ImGui.TextColored(StageColor(state, record), stageText);

            if (record?.LastSeenByCharacter is { Length: > 0 } observer)
            {
                var agoHours = (DateTimeOffset.UtcNow - record.LastSeenAt).TotalHours;
                ImGui.TextColored(HubStyle.Faint, $"seen by {observer}, {agoHours:F0}h ago");
            }
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
                ImGui.TextColored(HubStyle.Faint, "planted-at unknown — set an estimate");
                return;
            }

            if (rec.PlantedAt is null)
            {
                ImGui.TextColored(HubStyle.Faint, "planted-at unknown — set an estimate");
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
            var when = window.Earliest is { } e ? e.ToLocalTime().ToString("g") : "?";
            ImGui.TextUnformatted($"{when} ({confidenceText})");
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
                GardenJournal.Upsert(record);
                pendingEstimateHours.Remove(key);
            }
        }
    }
}
