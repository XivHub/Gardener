using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gardener.Game;
using Gardener.Localization;
using XivHubPluginKit.Inventory;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        // Combo labels indexed by SoilPreference's own declaration order; a mismatch here would
        // silently pick the wrong family or the wrong pinned-soil mode. A property, not a cached
        // array: Strings.* resolves through the current Loc.Culture on every access, so caching it
        // once would freeze the labels at whatever language was active on first Draw().
        private static string[] SoilPreferenceLabels => new[]
        {
            Strings.Config_SoilPreference_HighestThanalan,
            Strings.Config_SoilPreference_HighestShroud,
            Strings.Config_SoilPreference_HighestLaNoscean,
            Strings.Config_SoilPreference_Fixed,
        };

        // "" / "en" / "es" written to Configuration.UiLanguageOverride, indexed the same as
        // DrawLanguageCombo's own label array.
        private static readonly string[] LanguageCodes = { "", "en", "es" };

        private readonly Configuration cfg;

        public ConfigWindow(Configuration configuration) : base($"{Strings.Config_WindowTitle}###GardenerConfig")
        {
            cfg = configuration;
        }

        public void Dispose() { }

        // WindowName feeds ImGui.Begin before Draw() runs, so a language switch made through
        // DrawLanguageCombo needs its own refresh point here rather than inside Draw() itself.
        public override void PreDraw() => WindowName = $"{Strings.Config_WindowTitle}###GardenerConfig";

        public override void Draw()
        {
            DrawLanguageCombo();
            ImGui.Separator();

            DrawAutomationSection();
            DrawPlantingSection();
            DrawFertilizerSection();
            DrawRemindersSection();

            ImGui.Separator();
            if (ImGui.CollapsingHeader($"{Strings.Config_Developer_Header}###developerHeader"))
            {
                ImGui.TextDisabled(Strings.Config_Developer_TelemetryDesc);
                BoolInput(Strings.Config_Developer_EnableTelemetry, () => cfg.DevLog, v => cfg.DevLog = v);
                var url = cfg.DevLogUrl;
                if (ImGui.InputText(Strings.Config_Developer_LogUrl, ref url, 256))
                {
                    cfg.DevLogUrl = url;
                    cfg.Save();
                }
                ImGui.TextDisabled("e.g. http://127.0.0.1:9999/log");
            }

            DrawDataSection();
            DrawThemeSection();
        }

        /// <summary>
        /// Writes <see cref="Configuration.UiLanguageOverride"/> and applies it immediately through
        /// <see cref="Loc.SetLanguage"/>, so a language switch is visible on the very next frame
        /// without reopening the window or reloading the plugin.
        /// </summary>
        private void DrawLanguageCombo()
        {
            var labels = new[]
            {
                Strings.Config_Language_Automatic,
                Strings.Config_Language_English,
                Strings.Config_Language_Spanish,
            };

            var current = Array.IndexOf(LanguageCodes, cfg.UiLanguageOverride);
            if (current < 0)
                current = 0;

            if (ImGui.Combo(Strings.Config_Language_Label, ref current, labels, labels.Length))
            {
                cfg.UiLanguageOverride = LanguageCodes[current];
                cfg.Save();
                Loc.SetLanguage(string.IsNullOrEmpty(cfg.UiLanguageOverride)
                    ? Plugin.PluginInterface.UiLanguage
                    : cfg.UiLanguageOverride);
            }
        }

        private void DrawAutomationSection()
        {
            ImGui.TextDisabled(Strings.Config_Automation_Header);
            IntSlider(Strings.Config_Automation_StepDelayMs, () => cfg.StepDelayMs, v => cfg.StepDelayMs = v, 100, 4000);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Automation_SlowerReliable);
            IntSlider(Strings.Config_Automation_BedDelayMs, () => cfg.BedDelayMs, v => cfg.BedDelayMs = v, 200, 6000);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Automation_SlowerReliable);
            IntSlider(Strings.Config_Automation_MenuTimeoutMs, () => cfg.MenuTimeoutMs, v => cfg.MenuTimeoutMs = v, 1000, 15000);
            BoolInput(Strings.Config_Automation_StopIfPlayerMoves, () => cfg.StopIfPlayerMoves, v => cfg.StopIfPlayerMoves = v);
            if (cfg.StopIfPlayerMoves)
            {
                ImGui.Indent();
                FloatSlider(Strings.Config_Automation_MoveAbortDistance,
                    () => cfg.MoveAbortDistance, v => cfg.MoveAbortDistance = v, 1f, 10f);
                ImGui.Unindent();
            }
            FloatSlider(Strings.Config_Automation_BedReachDistance,
                () => cfg.BedReachDistance, v => cfg.BedReachDistance = v, 1f, 15f);
            BoolInput(Strings.Config_Automation_ConfirmBeforeRun, () => cfg.ConfirmBeforeRun, v => cfg.ConfirmBeforeRun = v);
        }

        private void DrawPlantingSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled(Strings.Config_Planting_Header);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Planting_Intro);
            SoilCombo(Strings.Config_Planting_SoilForCross, () => cfg.SoilForCross, v => cfg.SoilForCross = v);
            SoilCombo(Strings.Config_Planting_SoilForYield, () => cfg.SoilForYield, v => cfg.SoilForYield = v);

            if (cfg.SoilForCross == SoilPreference.Fixed || cfg.SoilForYield == SoilPreference.Fixed)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(Strings.Config_Planting_PinnedSoilHeader);
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
                    ImGui.TextColored(HubStyle.Faint, Strings.Config_NotHeld);
                }
            }
            ImGui.Unindent();
        }


        private void DrawFertilizerSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled(Strings.Config_Fertilizer_Header);
            BoolInput(Strings.Config_Fertilizer_OnlyGrowing, () => cfg.FertilizeOnlyGrowing, v => cfg.FertilizeOnlyGrowing = v);
            IntSlider(Strings.Config_Fertilizer_CooldownMinutes, () => cfg.FertilizeCooldownMin, v => cfg.FertilizeCooldownMin = v, 30, 180);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Fertilizer_CooldownNote);

            ImGui.Spacing();
            ImGui.TextUnformatted(Strings.Config_Fertilizer_WhichHeader);
            DrawFixedFertilizerPicker();
        }

        /// <summary>Every recognised fertilizer, greyed out when the player is not currently holding
        /// it. <see cref="Configuration.FixedFertilizerItemId"/> 0 is "Automatic", the same first-held
        /// default <see cref="GardeningItems.BestFertilizer"/> falls back to.</summary>
        private void DrawFixedFertilizerPicker()
        {
            var heldIds = Bags.Scan().Select(s => s.ItemId).ToHashSet();

            ImGui.Indent();
            if (ImGui.RadioButton($"{Strings.Config_Fertilizer_AutomaticFirstHeld}##fixedfertilizer-auto", cfg.FixedFertilizerItemId == 0))
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
                    ImGui.TextColored(HubStyle.Faint, Strings.Config_NotHeld);
                }
            }
            ImGui.Unindent();
        }

        private void DrawRemindersSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled(Strings.Config_Reminders_Header);
            BoolInput(Strings.Config_Reminders_ShowDtr, () => cfg.DtrEnabled, v => cfg.DtrEnabled = v);
            BoolInput(Strings.Config_Reminders_ChatEcho, () => cfg.ChatReminders, v => cfg.ChatReminders = v);
            IntSlider(Strings.Config_Reminders_WiltWarningHours, () => cfg.WiltWarningHours, v => cfg.WiltWarningHours = v, 1, 24);

            if (!cfg.ChatReminders)
                ImGui.BeginDisabled();
            IntSlider(Strings.Config_Reminders_ChatIntervalMinutes, () => cfg.ReminderIntervalMin, v => cfg.ReminderIntervalMin = v, 5, 120);
            if (!cfg.ChatReminders)
                ImGui.EndDisabled();

            ImGui.Spacing();
            BoolInput(Strings.Config_Reminders_NarrateSweeps, () => cfg.NarrateSweepActions, v => cfg.NarrateSweepActions = v);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Reminders_NarrateSweepsNote);
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
            ImGui.TextDisabled(Strings.Config_Data_Header);

            BoolInput(Strings.Config_Data_CollectGrowSamples,
                () => cfg.CollectGrowSamples, v => cfg.CollectGrowSamples = v);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Data_CollectGrowSamplesNote);

            BoolInput(Strings.Config_Data_ShowGapWarnings, () => cfg.ShowDataGapWarnings, v => cfg.ShowDataGapWarnings = v);
            if (!cfg.ShowDataGapWarnings)
                return;

            var provenance = SeedTable.Provenance;
            using (ImRaii.PushColor(ImGuiCol.Text, HubStyle.Faint))
            {
                ImGui.TextUnformatted(Loc.Format(Strings.Config_Data_Generated, provenance.Generated));
                ImGui.TextUnformatted(Loc.Format(Strings.Config_Data_LotlabCommit, provenance.LotlabCommit));
                ImGui.TextUnformatted(Loc.Format(Strings.Config_Data_Nick75gCommit, provenance.Nick75gCommit));
                ImGui.TextUnformatted(Loc.Format(Strings.Config_Data_XivapiSchema, provenance.XivapiSchema));
                ImGui.TextUnformatted(Loc.Format(Strings.Config_Data_XivapiVersion, provenance.XivapiVersion));
            }

            ImGui.Spacing();
            var gaps = SeedTable.DataGaps;
            DrawGapList("bundled-missing-sheet", Strings.Config_Data_GapBundledMissing, gaps.BundledRowsMissingFromSheet.Count,
                gaps.BundledRowsMissingFromSheet.Select(g => (g.Row, g.Name)));
            DrawGapList("live-missing-bundle", Strings.Config_Data_GapLiveMissing, gaps.LiveRowsMissingFromBundle.Count,
                gaps.LiveRowsMissingFromBundle.Select(g => (g.Row, g.Name)));
            DrawGapList("no-grow-time", Strings.Config_Data_GapNoGrowTime, gaps.RowsWithNoGrowTime.Count,
                gaps.RowsWithNoGrowTime.Select(g => (g.Row, g.Name)));
            DrawGapList("no-cross-data", Strings.Config_Data_GapNoCrossData, gaps.RowsAbsentFromCrossData.Count,
                gaps.RowsAbsentFromCrossData.Select(g => (g.Row, g.Name)));

            ImGui.Spacing();
            var soilGaps = GardeningItems.LiveSoilsMissingFromTable;
            DrawGapList("soil-missing-table", Strings.Config_Data_GapSoilMissing, soilGaps.Count,
                soilGaps.Select(g => (g.ItemId, g.Name)));
        }

        /// <summary>
        /// <paramref name="id"/> is a stable identifier independent of <paramref name="label"/>:
        /// the header's own visible text will be translated, and a CollapsingHeader's open state
        /// is otherwise keyed off exactly that text.
        /// </summary>
        private static void DrawGapList(string id, string label, int count, IEnumerable<(uint Id, string Name)> gaps)
        {
            var header = $"{Loc.Format(Strings.Config_GapCountHeader, label, Formats.Number(count))}###gap-{id}";
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
            ImGui.TextDisabled(Strings.Config_Theme_Header);
            ImGui.TextColored(HubStyle.Faint, Strings.Config_Theme_SharedNote);
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
