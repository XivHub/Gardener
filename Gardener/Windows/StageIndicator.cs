using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XivHubPluginKit.UI;

namespace Gardener.Windows;

/// <summary>
/// Four dots on one horizontal line: a progress meter along the 1-4 growth stage
/// (<see cref="Game.Maturity"/>), never a picture of the bed itself. FACTS.md confirms only the
/// Deluxe patch's own eight-bed ring; Oblong and Round layouts are unconfirmed, and AGENTS.md's
/// "the Goal tab draws no patch grid" gotcha applies here too — nothing this class draws may be read
/// as a bed layout, a ring position, or an adjacency claim.
/// </summary>
public static class StageIndicator
{
    private const int PipCount = 4;

    /// <summary>Radius and centre-to-centre spacing, both derived from the current font size so the
    /// meter scales with the player's theme. <see cref="Draw"/> and <see cref="Width"/> both call
    /// this so the two can never disagree about the space the meter occupies.</summary>
    private static (float Radius, float Spacing) Metrics()
    {
        var radius = MathF.Max(2f, ImGui.GetFontSize() * 0.18f);
        return (radius, radius * 3f);
    }

    /// <summary>The pixel width <see cref="Draw"/> consumes, for <c>TableSetupColumn</c> to size the
    /// growth column before any row exists to measure — the column carries
    /// <see cref="ImGuiTableColumnFlags.NoHeaderLabel"/>, so there is no header text to size against
    /// either.</summary>
    public static float Width()
    {
        var (radius, spacing) = Metrics();
        return spacing * (PipCount - 1) + radius * 2f;
    }

    /// <summary>Draws <paramref name="stage"/> filled pips (clamped to 1..4) in <paramref name="color"/>,
    /// followed by hollow pips out to four in <see cref="HubStyle.Faint"/>. <c>Draw(0, ...)</c> draws
    /// nothing at all — an empty bed's growth cell stays blank rather than showing four hollow dots.</summary>
    public static void Draw(int stage, Vector4 color)
    {
        var (radius, spacing) = Metrics();

        // Still submits an item when there is nothing to draw: the caller tests IsItemHovered right
        // after this, and skipping the Dummy would leave that test reading the previous cell.
        if (stage <= 0)
        {
            ImGui.Dummy(new Vector2(Width(), radius * 2f));
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var filled = Math.Clamp(stage, 0, PipCount);
        var filledColor = ImGui.ColorConvertFloat4ToU32(color);
        var hollowColor = ImGui.ColorConvertFloat4ToU32(HubStyle.Faint);

        for (var i = 0; i < PipCount; i++)
        {
            var center = origin + new Vector2(radius + i * spacing, radius);
            if (i < filled)
                drawList.AddCircleFilled(center, radius, filledColor);
            else
                drawList.AddCircle(center, radius, hollowColor);
        }

        ImGui.Dummy(new Vector2(Width(), radius * 2f));
    }
}
