using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.Dtr;
using Gardener.Localization;

namespace Gardener.Helpers;

/// <summary>
/// The DTR (server info bar) entry: Dalamud chrome drawn outside <c>Plugin.DrawUI</c>'s own
/// <c>HubStyle.Push</c>/<c>Pop</c> wrap, by construction — nothing here touches the theme. Text and
/// tooltip are rewritten only when <see cref="Reminders"/>' counts actually change, never per frame.
/// </summary>
public static class DtrEntry
{
    private const int MaxTooltipEntries = 8;

    private static IDtrBarEntry? entry;
    private static string lastText = string.Empty;
    private static DateTimeOffset lastBuiltFrom = DateTimeOffset.MinValue;

    public static void Init()
    {
        entry = Plugin.DtrBar.Get("Gardener");
        entry.OnClick = _ =>
        {
            if (Plugin.MainWindowInstance is { } window)
                window.IsOpen = true;
        };
    }

    /// <summary>Reads <see cref="Reminders"/>' already-recomputed lists; call after
    /// <see cref="Reminders.Tick"/> on the same frame so the two never disagree.</summary>
    public static void Tick()
    {
        if (entry is null)
            return;

        // Reminders.TimingUnknown is deliberately absent: a bed the plugin never watched being
        // planted has no planting time and never will until the player sets one, so counting it here
        // would leave the bar permanently lit over something no amount of gardening resolves. It is
        // reported in the tooltip's trailing line, in the Reminders tab, and beside the Garden tab's
        // own button that fixes it.
        var total = Reminders.DueToTend.Count + Reminders.AboutToWither.Count
            + Reminders.ReadyToHarvest.Count;
        var shown = Plugin.C.DtrEnabled && total > 0;
        if (entry.Shown != shown)
            entry.Shown = shown;

        // Gated on the recompute, never on the built string: Reminders moves at 1 Hz and the text is
        // a dozen short-lived allocations, so comparing the result would mean rebuilding it every
        // frame to discover it had not changed.
        if (!shown || Reminders.LastRecomputedAt == lastBuiltFrom)
            return;
        lastBuiltFrom = Reminders.LastRecomputedAt;

        var text = Loc.Format(Strings.Dtr_BarText, Reminders.SummaryText());
        if (text == lastText)
            return;

        lastText = text;
        entry.Text = text;
        entry.Tooltip = BuildTooltip();
    }

    public static void Dispose() => entry?.Remove();

    private static string BuildTooltip()
    {
        // Tending and wither risk need no house permission at all, so unlike the harvest lines below,
        // these two never append a "switch to" note.
        var lines = Reminders.DueToTend.Select(e => Loc.Format(Strings.Dtr_TooltipDueToTend, e.SeedName, Formats.Number(e.BedNumber)))
            .Concat(Reminders.AboutToWither.Select(e => Loc.Format(Strings.Dtr_TooltipAboutToWither, e.SeedName, Formats.Number(e.BedNumber))))
            .Concat(Reminders.ReadyToHarvest.Select(e => WithReach(Loc.Format(
                e.FromWindow ? Strings.Dtr_TooltipReadyToHarvest : Strings.Dtr_TooltipMature,
                e.Entry.SeedName, Formats.Number(e.Entry.BedNumber)), e.Entry.ReachableBy)))
            .Take(MaxTooltipEntries)
            .ToList();

        // Outside the Take: one summary line, never one line per bed, so the beds that do need doing
        // something about can never be displaced by the ones that only need a time typed in.
        var unknown = Reminders.TimingUnknown.Count;
        if (unknown > 0)
            lines.Add(Loc.Format(
                unknown == 1 ? Strings.Dtr_TooltipTimingUnknownCount_One : Strings.Dtr_TooltipTimingUnknownCount_Other,
                Formats.Number(unknown)));

        return lines.Count > 0 ? string.Join("\n", lines) : Strings.Dtr_TooltipNothingDue;
    }

    /// <summary>Appends the parenthesised "switch to &lt;name&gt;" note, or nothing at all when the
    /// character already logged in can reach the house.</summary>
    private static string WithReach(string line, IReadOnlyList<string> reachableBy) =>
        Reminders.ReachText(reachableBy) is { } reach
            ? $"{line} {Loc.Format(Strings.Reminders_ReachSuffix, reach)}"
            : line;
}
