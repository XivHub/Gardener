using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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

            DrawThemeSection();
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
