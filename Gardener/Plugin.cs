using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using XivHubPluginKit;
using XivHubPluginKit.UI;
using Gardener.Game;
using Gardener.Helpers;
using Gardener.Journal;
using Gardener.Localization;
using Gardener.Scheduler;
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
        [PluginService] public static ISeStringEvaluator SeStringEvaluator { get; private set; } = null!;

        public static TaskManager TaskManager { get; private set; } = null!;
        public static DevTelemetry Telemetry { get; private set; } = null!;

        /// <summary>Shared across every XIV Hub plugin; see XivHubPluginKit/UI/THEME.md.</summary>
        public static HubThemeConfigService ThemeConfig { get; private set; } = null!;

        public Configuration Configuration { get; init; }
        public static Configuration C { get; private set; } = null!;
        public WindowSystem WindowSystem = new("Gardener");
        private readonly MainWindow mainWindow;
        private readonly ConfigWindow configWindow;

        /// <summary>What the DTR entry's click handler opens; it has no window reference of its own.</summary>
        public static MainWindow? MainWindowInstance { get; private set; }

        public Plugin()
        {
            ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector);
            KitServices.Init(DataManager, Logger, ChatGui, $"[{Name}]");

            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(PluginInterface);
            C = this.Configuration;

            // Before any window is constructed, so the first frame ever drawn already resolves
            // Strings through the right culture instead of flashing English for one tick.
            ApplyLanguage(PluginInterface.UiLanguage);

            ThemeConfig = new HubThemeConfigService(
                PluginInterface.GetPluginConfigDirectory(),
                (msg, ex) => Logger.Warning(ex, msg));
            HubStyle.Init(ThemeConfig);

            // Touch SeedTable now so its load-time validation against the live GardeningSeed
            // sheet runs at startup, not lazily the first time a window happens to read it.
            var gaps = SeedTable.DataGaps;
            Logger.Information(
                $"[Gardener] seed table loaded (generated {SeedTable.Provenance.Generated}); " +
                $"gaps: bundled={gaps.BundledRowsMissingFromSheet.Count} " +
                $"live={gaps.LiveRowsMissingFromBundle.Count} " +
                $"grow={gaps.RowsWithNoGrowTime.Count} " +
                $"cross={gaps.RowsAbsentFromCrossData.Count}");

            TaskManager = new TaskManager(new TaskManagerConfiguration { TimeLimitMS = 20000, ShowDebug = false });

            Telemetry = new DevTelemetry("Gardener", () => C.DevLog, () => C.DevLogUrl,
                err => Logger.Debug($"DevLog post failed: {err}"));

            mainWindow = new MainWindow(this.Configuration);
            configWindow = new ConfigWindow(this.Configuration);
            MainWindowInstance = mainWindow;
            WindowSystem.AddWindow(mainWindow);
            WindowSystem.AddWindow(configWindow);
            DtrEntry.Init();

            CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                // Resolved once at load. A mid-session language switch leaves this one string on the
                // old language until the plugin reloads; the command manager never re-reads it, and
                // chasing it would mean re-registering the handler from two separate call paths.
                HelpMessage = Strings.Command_HelpMessage,
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
            PluginInterface.LanguageChanged += OnLanguageChanged;
            Framework.Update += OnFrameworkUpdate;
            ClientState.Logout += OnLogout;
            ClientState.TerritoryChanged += OnTerritoryChanged;
            ChatGui.ChatMessage += CropChatState.OnChatMessage;
        }

        // An explicit UiLanguageOverride always wins over Dalamud's own language, whether it's
        // being applied for the first time or Dalamud just changed under it.
        private void ApplyLanguage(string dalamudLangCode) =>
            Loc.SetLanguage(string.IsNullOrEmpty(C.UiLanguageOverride) ? dalamudLangCode : C.UiLanguageOverride);

        private void OnLanguageChanged(string langCode) => ApplyLanguage(langCode);

        private void OnFrameworkUpdate(IFramework framework)
        {
            // The passive DataMap read runs at PatchDiscovery's own 2-second discovery cadence, on
            // the ticks where it actually rescanned, never per frame.
            if (PatchDiscovery.Tick())
            {
                GardenJournal.Reconcile(GardenMemory.Poll());

                // Standing inside a house at all already proves the game granted this character
                // permission to be here; record that access at the house level so a reminder for
                // another of the player's characters' gardens can say who to switch to.
                if (HouseKey.Current() is { } house)
                {
                    var characterName = ObjectTable.LocalPlayer?.Name.TextValue;
                    GardenJournal.RecordHouseAccess(house.KeyString(), house.OwnedEstateType, characterName);

                    // PatchDiscovery only lists patches on a plot this character owns, so only an
                    // owned house's absence is trustworthy enough to call a patch confirmed parked
                    // rather than merely not currently visible from here.
                    if (house.Owned)
                        GardenJournal.MarkParked(house.KeyString(), PatchDiscovery.Patches.Select(p => p.Key));
                }
            }

            GardenJournal.Tick();
            SchedulerMain.Tick();

            // Reminders reads the journal only, so this runs regardless of whether the player is
            // anywhere near a housing territory; DtrEntry reads Reminders' just-recomputed lists on
            // the same tick so the bar and the window never show different counts.
            Reminders.Tick();
            DtrEntry.Tick();
        }

        // Stop on logout so a character switch never resumes a sweep on a different character.
        private void OnLogout(int type, int code)
        {
            SchedulerMain.DisablePlugin();
            GardenJournal.Flush();
        }

        // Bed EntityIds are per-session; a zone change invalidates every patch's predicted map.
        private void OnTerritoryChanged(uint territoryType) => BedTargeting.Invalidate();

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            ClientState.Logout -= OnLogout;
            ClientState.TerritoryChanged -= OnTerritoryChanged;
            ChatGui.ChatMessage -= CropChatState.OnChatMessage;
            SchedulerMain.DisablePlugin();
            GardenJournal.Flush();
            Telemetry.Dispose();
            DtrEntry.Dispose();

            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            PluginInterface.LanguageChanged -= OnLanguageChanged;

            WindowSystem.RemoveAllWindows();
            mainWindow.Dispose();
            configWindow.Dispose();

            CommandManager.RemoveHandler(commandName);

            ECommonsMain.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            var arg = args.Trim().ToLowerInvariant();
            switch (arg)
            {
                case "dump":
                    DebugDump.Run(menu: false);
                    break;
                case "dump menu":
                    DebugDump.Run(menu: true);
                    break;
                default:
                    mainWindow.IsOpen = true;
                    break;
            }
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
            // ImGui's clipboard is only safe to touch on the draw thread; the dump command queues
            // text for this to pick up rather than writing it directly.
            DebugDump.DrainClipboard();

            HubStyle.Push();
            try { WindowSystem.Draw(); }
            finally { HubStyle.Pop(); }
        }
    }
}
