using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Gardener.Game;

/// <summary>
/// The game's <c>Talk</c> box, which a bed interaction opens before the bed's <c>SelectString</c>
/// with the crop's name and one of <see cref="GardenMenuText"/>'s five <c>TALK_*</c> sentences. The
/// same sentence also reaches chat, where <see cref="CropChatState"/> reads it, but the box itself is
/// a modal step: the bed menu does not open until it is clicked through. A dialogue-skipping plugin
/// clicks it within a frame, which is why a client running one appears to go straight to the menu;
/// without one, every bed in a sweep waits out <see cref="Configuration.MenuTimeoutMs"/> and is
/// skipped as "the bed menu never opened".
/// </summary>
public static class TalkPrompt
{
    private const string AddonName = "Talk";

    /// <summary>Whether the box is open and fully loaded, so <see cref="Advance"/> can act on it.</summary>
    public static unsafe bool IsOpen
    {
        get
        {
            // SAFETY: GetAddonByName returns a possibly-null pointer into live addon memory, checked
            // before the cast is dereferenced.
            var talk = Plugin.GameGui.GetAddonByName<AddonTalk>(AddonName);
            return talk != null && GenericHelpers.IsAddonReady((AtkUnitBase*)talk);
        }
    }

    /// <summary>The sentence the box is showing, or an empty string when nothing is open.
    /// Classifiable through <see cref="GardenMenuText.Classify"/> exactly like the chat line.</summary>
    public static unsafe string Text
    {
        get
        {
            // SAFETY: as above; AtkTextNode228 is the body node, null-checked by ReadNode.
            var talk = Plugin.GameGui.GetAddonByName<AddonTalk>(AddonName);
            if (talk == null || !GenericHelpers.IsAddonReady((AtkUnitBase*)talk))
                return "";

            return ReadNode(talk->AtkTextNode228);
        }
    }

    /// <summary>Clicks the box through, the way a player pressing confirm does. Returns whether there
    /// was a box to advance.</summary>
    public static unsafe bool Advance()
    {
        // SAFETY: as above. AddonMaster.Talk wraps the same pointer and sends the addon its own
        // mouse-click event, so the game runs its normal advance path rather than the addon being
        // hidden out from under an interaction the server still thinks is running.
        var talk = Plugin.GameGui.GetAddonByName<AddonTalk>(AddonName);
        if (talk == null || !GenericHelpers.IsAddonReady((AtkUnitBase*)talk))
            return false;

        new AddonMaster.Talk((void*)talk).Click();
        return true;
    }

    private static unsafe string ReadNode(AtkTextNode* node) =>
        node == null ? "" : GenericHelpers.ReadSeString(&node->NodeText).GetText();
}
