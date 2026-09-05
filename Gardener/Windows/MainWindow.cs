using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

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
                ImGui.EndTabItem();
            if (ImGui.BeginTabItem("Plan"))
                ImGui.EndTabItem();
            if (ImGui.BeginTabItem("Reminders"))
                ImGui.EndTabItem();
            if (ImGui.BeginTabItem("Log"))
                ImGui.EndTabItem();

            ImGui.EndTabBar();
        }
    }
}
