using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using XivHubPluginKit;
using XivHubPluginKit.UI;
using Gardener.Windows;

namespace Gardener
{
    public sealed class Plugin : IDalamudPlugin
    {
        public static string Name => "Gardener";

        private const string commandName = "/gardener";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static IDataManager DataManager { get; private set; } = null!;
        [PluginService] public static IPluginLog Logger { get; private set; } = null!;
        [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
        [PluginService] public static IClientState ClientState { get; private set; } = null!;
        [PluginService] public static ICondition Condition { get; private set; } = null!;
        [PluginService] public static IFramework Framework { get; private set; } = null!;
        [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
        [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
        [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
        [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
        [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
        [PluginService] public static IGameGui GameGui { get; private set; } = null!;

        public static TaskManager TaskManager { get; private set; } = null!;
        public static DevTelemetry Telemetry { get; private set; } = null!;

        /// <summary>Shared across every XIV Hub plugin; see XivHubPluginKit/UI/THEME.md.</summary>
        public static HubThemeConfigService ThemeConfig { get; private set; } = null!;

        public Configuration Configuration { get; init; }
        public static Configuration C { get; private set; } = null!;
        public WindowSystem WindowSystem = new("Gardener");
        private readonly MainWindow mainWindow;
        private readonly ConfigWindow configWindow;

        public Plugin()
        {
            ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector);
            KitServices.Init(DataManager, Logger, ChatGui, $"[{Name}]");

            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(PluginInterface);
            C = this.Configuration;

            ThemeConfig = new HubThemeConfigService(
                PluginInterface.GetPluginConfigDirectory(),
                (msg, ex) => Logger.Warning(ex, msg));
            HubStyle.Init(ThemeConfig);

            TaskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 20000, ShowDebug = false });

            Telemetry = new DevTelemetry("Gardener", () => C.DevLog, () => C.DevLogUrl,
                err => Logger.Debug($"DevLog post failed: {err}"));

            mainWindow = new MainWindow(this.Configuration);
            configWindow = new ConfigWindow(this.Configuration);
            WindowSystem.AddWindow(mainWindow);
            WindowSystem.AddWindow(configWindow);

            CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the Gardener window."
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
            Framework.Update += OnFrameworkUpdate;
            ClientState.Logout += OnLogout;
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
        }

        private void OnLogout(int type, int code)
        {
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            ClientState.Logout -= OnLogout;
            Telemetry.Dispose();

            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

            WindowSystem.RemoveAllWindows();
            mainWindow.Dispose();
            configWindow.Dispose();

            CommandManager.RemoveHandler(commandName);

            ECommonsMain.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            mainWindow.IsOpen = true;
        }

        private void ToggleMainUi() => mainWindow.Toggle();

        private void ToggleConfigUi() => configWindow.Toggle();

        /// <summary>
        /// One wrap point for the whole plugin: no window class knows the theme
        /// exists, and the pop is guaranteed even if a window throws mid-draw —
        /// ImGui's style stack is global, so an unbalanced push corrupts every
        /// plugin drawing after this one.
        /// </summary>
        private void DrawUI()
        {
            HubStyle.Push();
            try { WindowSystem.Draw(); }
            finally { HubStyle.Pop(); }
        }
    }
}
