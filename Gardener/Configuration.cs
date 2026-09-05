using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace Gardener
{
    public enum SoilPreference
    {
        HighestThanalan,    // intercross rate
        HighestShroud,      // yield quantity
        HighestLaNoscean,   // equivalent to Potting Soil since 6.0
        Fixed,              // pin FixedSoilItemId
    }

    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        // Automation
        public int StepDelayMs { get; set; } = 250;          // between menu actions, one server round-trip of headroom
        public int MenuTimeoutMs { get; set; } = 5000;        // per-step TimeLimitMS for "wait for addon"
        public bool StopIfPlayerMoves { get; set; } = true;
        public float MoveAbortDistance { get; set; } = 3.0f;  // yalms from the position where the run started
        public float BedReachDistance { get; set; } = 5.0f;   // refuse to start if the nearest bed is further
        public bool ConfirmBeforeRun { get; set; } = true;

        // Planting defaults
        public SoilPreference SoilForCross { get; set; } = SoilPreference.HighestThanalan;
        public SoilPreference SoilForYield { get; set; } = SoilPreference.HighestShroud;
        public uint FixedSoilItemId { get; set; } = 0;        // 0 = use the preference; non-zero pins one soil

        // Fertilizer
        public bool FertilizeOnlyGrowing { get; set; } = true;    // never on a ripe bed; it does nothing
        public int FertilizeCooldownMin { get; set; } = 60;       // per bed, game rule

        // Reminders
        public bool DtrEnabled { get; set; } = true;
        public int WiltWarningHours { get; set; } = 6;            // lead time before predicted wilt
        public bool ChatReminders { get; set; } = false;
        public int ReminderIntervalMin { get; set; } = 30;

        // Data
        public bool ShowDataGapWarnings { get; set; } = true;
        public bool CollectGrowSamples { get; set; } = true;      // calibration

        // Developer
        public bool DevLog { get; set; } = false;
        public string DevLogUrl { get; set; } = "";

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}
