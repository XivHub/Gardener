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
        public int Version { get; set; } = CurrentVersion;

        internal const int CurrentVersion = 2;

        // Automation
        // Fixed settle time where no addon-ready signal exists to poll for instead; jittered at the
        // call site (see SchedulerPacing) so a run's pacing never reads as perfectly metronomic.
        // 800ms clears a server round trip with margin — most of the stalls this plugin has hit came
        // from acting before the game was ready, not from waiting too long.
        public int StepDelayMs { get; set; } = 800;
        // Between finishing one bed and interacting with the next: a bigger boundary than one step to
        // the next within a bed, and where a stale addon from the previous bed is most likely to
        // still be closing.
        public int BedDelayMs { get; set; } = 2200;
        public int MenuTimeoutMs { get; set; } = 5000;        // per-step TimeLimitMS for "wait for addon"
        public bool StopIfPlayerMoves { get; set; } = true;
        public float MoveAbortDistance { get; set; } = 3.0f;  // yalms from the position where the run started
        // Yalms from the player to the nearest patch centre, not to any one bed: every bed reports the
        // enclosing patch's own position, so this is the only distance there is to measure. A player
        // standing at a bed to work it is already several yalms from that centre, so the default has to
        // clear that gap with room to spare rather than sit right at it.
        public float BedReachDistance { get; set; } = 10.0f;
        public bool ConfirmBeforeRun { get; set; } = true;

        // Planting defaults
        public SoilPreference SoilForCross { get; set; } = SoilPreference.HighestThanalan;
        public SoilPreference SoilForYield { get; set; } = SoilPreference.HighestShroud;
        public uint FixedSoilItemId { get; set; } = 0;        // 0 = use the preference; non-zero pins one soil

        // Fertilizer
        public bool FertilizeOnlyGrowing { get; set; } = true;    // never on a ripe bed; it does nothing
        public int FertilizeCooldownMin { get; set; } = 60;       // per bed, game rule
        public uint FixedFertilizerItemId { get; set; } = 0;      // 0 = use the first one held; non-zero pins one

        // Reminders
        public bool DtrEnabled { get; set; } = true;
        public int WiltWarningHours { get; set; } = 6;            // lead time before predicted wilt
        public bool ChatReminders { get; set; } = false;
        public int ReminderIntervalMin { get; set; } = 30;

        // On by default: someone running automation over their own garden wants to see each action
        // as it happens, unlike ChatReminders above, which is an opt-in periodic summary.
        public bool NarrateSweepActions { get; set; } = true;

        // Data
        public bool ShowDataGapWarnings { get; set; } = true;
        public bool CollectGrowSamples { get; set; } = true;      // calibration

        // Developer
        public bool DevLog { get; set; } = false;
        public string DevLogUrl { get; set; } = "";

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        /// <summary>
        /// Raises pacing values that a saved config still holds at a superseded default. A stored
        /// value always wins over a changed default, so a config written before the pacing was
        /// retuned keeps the plugin acting faster than the game answers. Only a value still equal to
        /// the old default is moved, leaving anything the player chose alone.
        /// </summary>
        private void Migrate()
        {
            if (Version >= CurrentVersion)
                return;

            if (StepDelayMs == 250)
                StepDelayMs = 800;
            if (BedDelayMs is 0 or 1200)
                BedDelayMs = 2200;
            if (Math.Abs(BedReachDistance - 5.0f) < 0.01f)
                BedReachDistance = 10.0f;

            Version = CurrentVersion;
        }

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
            Migrate();
            Save();
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}
