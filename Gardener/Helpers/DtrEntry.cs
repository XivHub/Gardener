using System.Linq;
using Dalamud.Game.Gui.Dtr;

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

        var total = Reminders.DueToTend.Count + Reminders.AboutToWither.Count
            + Reminders.ReadyToHarvest.Count + Reminders.TimingUnknown.Count;
        var shown = Plugin.C.DtrEnabled && total > 0;
        if (entry.Shown != shown)
            entry.Shown = shown;

        if (!shown)
            return;

        var text = $"Gardener: {Reminders.SummaryText()}";
        if (text == lastText)
            return;

        lastText = text;
        entry.Text = text;
        entry.Tooltip = BuildTooltip();
    }

    public static void Dispose() => entry?.Remove();

    private static string BuildTooltip()
    {
        // Tending and wither risk need no house permission at all, so unlike the harvest and
        // unknown-timing lines below, these two never append a "switch to" note.
        var lines = Reminders.DueToTend.Select(e => $"{e.SeedName} bed {e.BedNumber}: due to tend")
            .Concat(Reminders.AboutToWither.Select(e => $"{e.SeedName} bed {e.BedNumber}: about to wither"))
            .Concat(Reminders.ReadyToHarvest.Select(e => $"{e.Entry.SeedName} bed {e.Entry.BedNumber}: {(e.FromWindow ? "ready to harvest" : "mature")}, {Reminders.ReachText(e.Entry.ReachableBy)}"))
            .Concat(Reminders.TimingUnknown.Select(e => $"{e.SeedName} bed {e.BedNumber}: planting time unknown, {Reminders.ReachText(e.ReachableBy)}"))
            .Take(MaxTooltipEntries)
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : "Nothing due.";
    }
}
