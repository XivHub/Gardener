using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Gardener.Game;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Configuration cfg;

        public ConfigWindow(Configuration configuration) : base("Gardener Settings")
        {
            cfg = configuration;
        }

        public void Dispose() { }

        public override void Draw()
        {
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

        /// <summary>
        /// Shows the bundled table's provenance and the gaps <see cref="SeedTable.Validate"/> found
        /// against the live sheet, so a stale bundle is visible in settings instead of silently
        /// planting the wrong seed or dropping one from the planner.
        /// </summary>
        private void DrawDataSection()
        {
            ImGui.Separator();
            ImGui.TextDisabled("Data");

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
    }
}
