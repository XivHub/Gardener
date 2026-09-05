using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        // Combo labels indexed by SoilPreference's own declaration order; a mismatch here would
        // silently pick the wrong family or the wrong pinned-soil mode.
        private static readonly string[] SoilPreferenceLabels =
        {
            "Highest-grade Thanalan Topsoil (best intercross rate)",
            "Highest-grade Shroud Topsoil (best yield)",
            "Highest-grade La Noscean Topsoil (Potting Soil-equivalent)",
            "One specific soil, pinned below",
        };

        private readonly Configuration cfg;

        public ConfigWindow(Configuration configuration) : base("Gardener Settings")
        {
            cfg = configuration;
        }

        public void Dispose() { }

        public override void Draw()
        {
            DrawAutomationSection();
            DrawPlantingSection();
            DrawFertilizerSection();
            DrawRemindersSection();

            ImGui.Separator();
            if (ImGui.CollapsingHeader("Developer"))
            {
                ImGui.TextDisabled("Streams live activity + state snapshots to a local log server.");
                BoolInput("Enable dev telemetry", () => cfg.DevLog, v => cfg.DevLog = v);
                var url = cfg.DevLogUrl;
                if (ImGui.InputText("Log server URL", ref url, 256))
                {
                    cfg.DevLogUrl = url;
                    cfg.Save();
                }
                ImGui.TextDisabled("e.g. http://192.168.88.248:9999/log");
            }

            DrawDataSection();
            DrawThemeSection();
        }

        private void DrawAutomationSection()
        {
            ImGui.TextDisabled("Automation");
            IntSlider("Delay between actions (ms)", () => cfg.StepDelayMs, v => cfg.StepDelayMs = v, 100, 2000);
            ImGui.TextColored(HubStyle.Faint, "Raising this makes runs slower but more reliable.");
            IntSlider("Delay between beds (ms)", () => cfg.BedDelayMs, v => cfg.BedDelayMs = v, 200, 3000);
            ImGui.TextColored(HubStyle.Faint, "Raising this makes runs slower but more reliable.");
            IntSlider("Menu wait timeout (ms)", () => cfg.MenuTimeoutMs, v => cfg.MenuTimeoutMs = v, 1000, 15000);
            BoolInput("Stop a run if I move", () => cfg.StopIfPlayerMoves, v => cfg.StopIfPlayerMoves = v);
            if (cfg.StopIfPlayerMoves)
            {
                ImGui.Indent();
                FloatSlider("Distance that counts as moved away (yalms)",
                    () => cfg.MoveAbortDistance, v => cfg.MoveAbortDistance = v, 1f, 10f);
                ImGui.Unindent();
            }
            FloatSlider("Reach distance to start a run (yalms)",
                () => cfg.BedReachDistance, v => cfg.BedReachDistance = v, 1f, 15f);
            BoolInput("Confirm before running a sweep", () => cfg.ConfirmBeforeRun, v => cfg.ConfirmBeforeRun = v);
        }

        private void DrawPlantingSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Planting defaults");
            ImGui.TextColored(HubStyle.Faint, "Which topsoil to reach for when the plugin plants for you.");
            SoilCombo("Soil for a crossbreed step", () => cfg.SoilForCross, v => cfg.SoilForCross = v);
            SoilCombo("Soil for a yield step", () => cfg.SoilForYield, v => cfg.SoilForYield = v);

            if (cfg.SoilForCross == SoilPreference.Fixed || cfg.SoilForYield == SoilPreference.Fixed)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("Pinned soil");
                DrawFixedSoilPicker();
            }
        }

        /// <summary>Every recognised topsoil, greyed out when the player is not currently holding
        /// it. Still selectable while greyed: pinning a soil the player plans to buy is a normal
        /// setup step, not a mistake to block.</summary>
        private void DrawFixedSoilPicker()
        {
            var heldIds = Bags.Scan().Select(s => s.ItemId).ToHashSet();

            ImGui.Indent();
            foreach (var soil in GardeningItems.Soils)
            {
                var held = heldIds.Contains(soil.ItemId);
                var selected = cfg.FixedSoilItemId == soil.ItemId;

                using (ImRaii.PushColor(ImGuiCol.Text, held ? HubStyle.Text : HubStyle.Faint))
                {
                    if (ImGui.RadioButton($"{ItemSheet.Name(soil.ItemId)}##fixedsoil-{soil.ItemId}", selected))
                    {
                        cfg.FixedSoilItemId = soil.ItemId;
                        cfg.Save();
                    }
                }
                if (!held)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(HubStyle.Faint, "(not held)");
                }
            }
            ImGui.Unindent();
        }


        private void DrawFertilizerSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Fertilizer");
            BoolInput("Only fertilize a growing bed", () => cfg.FertilizeOnlyGrowing, v => cfg.FertilizeOnlyGrowing = v);
            IntSlider("Cooldown per bed (minutes)", () => cfg.FertilizeCooldownMin, v => cfg.FertilizeCooldownMin = v, 30, 180);
            ImGui.TextColored(HubStyle.Faint,
                "The game accepts one application per bed per hour regardless of this setting; a shorter " +
                "cooldown here just tries earlier and gets refused.");

            ImGui.Spacing();
            ImGui.TextUnformatted("Which fertilizer to use");
            DrawFixedFertilizerPicker();
        }

        /// <summary>Every recognised fertilizer, greyed out when the player is not currently holding
        /// it. <see cref="Configuration.FixedFertilizerItemId"/> 0 is "Automatic", the same first-held
        /// default <see cref="GardeningItems.BestFertilizer"/> falls back to.</summary>
        private void DrawFixedFertilizerPicker()
        {
            var heldIds = Bags.Scan().Select(s => s.ItemId).ToHashSet();

            ImGui.Indent();
            if (ImGui.RadioButton("Automatic (first one held)##fixedfertilizer-auto", cfg.FixedFertilizerItemId == 0))
            {
                cfg.FixedFertilizerItemId = 0;
                cfg.Save();
            }

            foreach (var itemId in GardeningItems.Fertilizers)
            {
                var held = heldIds.Contains(itemId);
                var selected = cfg.FixedFertilizerItemId == itemId;

                using (ImRaii.PushColor(ImGuiCol.Text, held ? HubStyle.Text : HubStyle.Faint))
                {
                    if (ImGui.RadioButton($"{ItemSheet.Name(itemId)}##fixedfertilizer-{itemId}", selected))
                    {
                        cfg.FixedFertilizerItemId = itemId;
                        cfg.Save();
                    }
                }
                if (!held)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(HubStyle.Faint, "(not held)");
                }
            }
            ImGui.Unindent();
        }

        private void DrawRemindersSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Reminders");
            BoolInput("Show reminder counts on the server info bar", () => cfg.DtrEnabled, v => cfg.DtrEnabled = v);
            BoolInput("Echo reminders to chat", () => cfg.ChatReminders, v => cfg.ChatReminders = v);
            IntSlider("Warn this many hours before a bed wilts", () => cfg.WiltWarningHours, v => cfg.WiltWarningHours = v, 1, 24);

            if (!cfg.ChatReminders)
                ImGui.BeginDisabled();
            IntSlider("Chat reminder interval (minutes)", () => cfg.ReminderIntervalMin, v => cfg.ReminderIntervalMin = v, 5, 120);
            if (!cfg.ChatReminders)
                ImGui.EndDisabled();

            ImGui.Spacing();
            BoolInput("Narrate sweeps in chat", () => cfg.NarrateSweepActions, v => cfg.NarrateSweepActions = v);
            ImGui.TextColored(HubStyle.Faint,
                "Prints a line in chat as a sweep tends, fertilizes, harvests, plants or skips each bed.");
        }

        private void SoilCombo(string label, Func<SoilPreference> get, Action<SoilPreference> set)
        {
            var v = (int)get();
            if (ImGui.Combo(label, ref v, SoilPreferenceLabels, SoilPreferenceLabels.Length))
            {
                set((SoilPreference)v);
                cfg.Save();
            }
        }

        /// <summary>
        /// Shows the bundled table's provenance and the gaps <see cref="SeedTable.Validate"/> found
        /// against the live sheet, so a stale bundle is visible in settings instead of silently
        /// planting the wrong seed or dropping one from the planner. Also carries the one setting
        /// that feeds the harvest-window calibration in <see cref="Gardener.Journal.Calibration"/>.
        /// </summary>
        private void DrawDataSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Data health");

            BoolInput("Record how long your own crops take to mature and become harvestable",
                () => cfg.CollectGrowSamples, v => cfg.CollectGrowSamples = v);
            ImGui.TextColored(HubStyle.Faint, "Narrows the harvest window in the Reminders tab from your own garden's timing.");

            BoolInput("Show data gap warnings", () => cfg.ShowDataGapWarnings, v => cfg.ShowDataGapWarnings = v);
            if (!cfg.ShowDataGapWarnings)
                return;

            var provenance = SeedTable.Provenance;
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Faint))
            {
                ImGui.TextUnformatted($"Generated {provenance.Generated}");
                ImGui.TextUnformatted($"Lotlab commit {provenance.LotlabCommit}");
                ImGui.TextUnformatted($"nick75g commit {provenance.Nick75gCommit}");
                ImGui.TextUnformatted($"XIVAPI schema {provenance.XivapiSchema}");
                ImGui.TextUnformatted($"XIVAPI version {provenance.XivapiVersion}");
            }

            ImGui.Spacing();
            var gaps = SeedTable.DataGaps;
            DrawGapList("Bundled rows missing from the live sheet", gaps.BundledRowsMissingFromSheet.Count,
                gaps.BundledRowsMissingFromSheet.Select(g => (g.Row, g.Name)));
            DrawGapList("Live outdoor rows missing from the bundle", gaps.LiveRowsMissingFromBundle.Count,
                gaps.LiveRowsMissingFromBundle.Select(g => (g.Row, g.Name)));
            DrawGapList("Rows with no grow time", gaps.RowsWithNoGrowTime.Count,
                gaps.RowsWithNoGrowTime.Select(g => (g.Row, g.Name)));
            DrawGapList("Rows absent from the cross data", gaps.RowsAbsentFromCrossData.Count,
                gaps.RowsAbsentFromCrossData.Select(g => (g.Row, g.Name)));

            ImGui.Spacing();
            var soilGaps = GardeningItems.LiveSoilsMissingFromTable;
            DrawGapList("Live soils missing from the soil table", soilGaps.Count,
                soilGaps.Select(g => (g.ItemId, g.Name)));
        }

        private static void DrawGapList(string label, int count, IEnumerable<(uint Id, string Name)> gaps)
        {
            var header = $"{label} ({count})###gap-{label}";
            bool open;
            if (count > 0)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Warn))
                    open = ImGui.CollapsingHeader(header);
            }
            else
            {
                open = ImGui.CollapsingHeader(header);
            }

            if (!open)
                return;

            foreach (var gap in gaps)
                ImGui.BulletText($"{gap.Id}: {gap.Name}");
        }

        /// <summary>
        /// The theme editor is generated from the kit's option table, so this stays
        /// one call however many themed values the kit grows.
        /// </summary>
        private static void DrawThemeSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Appearance");
            ImGui.TextColored(HubStyle.Faint, "Shared with every XIV Hub plugin.");
            ImGui.Spacing();
            HubThemeEditor.Draw(Plugin.ThemeConfig);
        }

        private void BoolInput(string label, Func<bool> get, Action<bool> set)
        {
            var v = get();
            if (ImGui.Checkbox(label, ref v))
            {
                set(v);
                cfg.Save();
            }
        }

        private void IntSlider(string label, Func<int> get, Action<int> set, int min, int max)
        {
            var v = get();
            if (ImGui.SliderInt(label, ref v, min, max))
            {
                set(v);
                cfg.Save();
            }
        }

        private void FloatSlider(string label, Func<float> get, Action<float> set, float min, float max)
        {
            var v = get();
            if (ImGui.SliderFloat(label, ref v, min, max))
            {
                set(v);
                cfg.Save();
            }
        }
    }
}
