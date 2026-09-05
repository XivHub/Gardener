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
                ImGui.EndTabItem();
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
                SchedulerMain.DisablePlugin();
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
