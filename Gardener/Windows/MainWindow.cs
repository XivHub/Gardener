using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Gardener.Game;
using Gardener.Helpers;
using XivHubPluginKit.UI;

namespace Gardener.Windows
{
    public class MainWindow : Window, IDisposable
    {
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
                ImGui.EndTabItem();

            ImGui.EndTabBar();
        }

        /// <summary>
        /// Lists discovered patches (already filtered to the plot the player is standing on) and,
        /// per patch, a grid of bed cells laid out <c>Cols</c> wide showing each bed's live
        /// <c>EventState</c> byte. The cell number is the bed's position in <c>Patch.Beds</c>
        /// (sorted by <c>EntityId</c>), not the game's own "Nth Bed" number — see docs/RESEARCH.md
        /// for why the two are not yet known to agree.
        /// </summary>
        private static void DrawGardenTab()
        {
            var patches = PatchDiscovery.Patches;
            if (patches.Count == 0)
            {
                ImGui.TextColored(HubStyle.Faint, "No patches discovered. Stand in an outdoor housing plot.");
            }

            foreach (var patch in patches)
            {
                ImGui.TextUnformatted($"{patch.Kind} patch — {patch.Beds.Count} beds, {patch.Cols} cols");
                ImGui.TextColored(HubStyle.Faint, patch.Key);

                if (ImGui.BeginTable($"##bedgrid-{patch.Key}", patch.Cols, ImGuiTableFlags.Borders))
                {
                    for (var i = 0; i < patch.Beds.Count; i++)
                    {
                        if (i % patch.Cols == 0)
                            ImGui.TableNextRow();
                        ImGui.TableNextColumn();

                        var bed = patch.Beds[i];
                        var state = PatchDiscovery.EventStateFor(bed.EntityId);
                        var stateText = state is { } s ? $"0x{s:X2}" : "?";
                        ImGui.TextUnformatted($"#{i}\nstate={stateText}");
                    }

                    ImGui.EndTable();
                }

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
    }
}
