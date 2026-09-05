using System;
using Gardener.Game;
using Gardener.Scheduler;

namespace Gardener.Localization
{
    /// <summary>
    /// The player-facing verb for a bed action, borrowed from the game's own bed-menu wording wherever
    /// it is available so the label matches what she clicks in the SelectString, and reads correctly
    /// in every client language for free. Falls back to a keyed English word only when
    /// <see cref="GardenMenuText"/> could not resolve that key, which already means the automation
    /// depending on the same sheet is disabled.
    /// </summary>
    public static class GameWords
    {
        public static string Action(MenuKey key)
        {
            var gameText = GardenMenuText.TextFor(key);
            if (!string.IsNullOrEmpty(gameText))
                return gameText;

            return key switch
            {
                MenuKey.Care => Strings.Action_Tend,
                MenuKey.Harvest => Strings.Action_Harvest,
                MenuKey.SetSeed => Strings.Action_PlantSeeds,
                MenuKey.SetFertilizer => Strings.Action_Fertilize,
                MenuKey.Dispose => Strings.Action_RemoveCrop,
                _ => throw new ArgumentOutOfRangeException(nameof(key), key, "unhandled action MenuKey"),
            };
        }

        /// <summary>The verb for a whole-patch sweep, mapped onto the single-bed action it repeats
        /// across every bed.</summary>
        public static string Action(SweepKind kind) => kind switch
        {
            SweepKind.Tend => Action(MenuKey.Care),
            SweepKind.Harvest => Action(MenuKey.Harvest),
            SweepKind.Fertilize => Action(MenuKey.SetFertilizer),
            SweepKind.Plan => Action(MenuKey.SetSeed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unhandled SweepKind"),
        };
    }
}
