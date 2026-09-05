using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;

namespace Gardener.Game;

/// <summary>One chat line classified against <see cref="GardenMenuText"/>'s five <c>TALK_*</c> keys.</summary>
public readonly record struct CropChatLine(DateTimeOffset At, XivChatType ChatType, MenuKey Key, string Text);

/// <summary>
/// Reads the five <c>TALK_*</c> sentences a bed interaction echoes into chat — "This crop is doing
/// well." and its siblings — the only source for wilted (<see cref="MenuKey.TalkDepressed"/>) and true
/// harvest-readiness (<see cref="MenuKey.TalkRipe"/>), neither of which the bed menu itself ever offers
///. Classified by <see cref="GardenMenuText"/>'s stable sheet key, never by
/// English text, so it stays locale-correct and never depends on row order. Every line that does not
/// classify to one of the five is ignored outright — chat carries everything else too.
///
/// Subscribed directly onto <see cref="Dalamud.Plugin.Services.IChatGui.ChatMessage"/> from
/// <c>Plugin.cs</c>, the same place every other Dalamud service event in this plugin is wired and torn
/// down, rather than owning its own subscribe/unsubscribe pair.
/// </summary>
public static class CropChatState
{
    private const int RingSize = 16;

    private static readonly CropChatLine[] ring = new CropChatLine[RingSize];
    private static int head;
    private static int count;

    private static readonly List<(DateTimeOffset At, XivChatType ChatType, string Text)> unclassified = new();

    /// <summary>Whether to keep the text of lines that do not classify, for
    /// <c>DebugDump</c> to print. Set only for the length of a sweep — every bed interaction a sweep
    /// makes echoes one of the five sentences, so that window holds the line being looked for and
    /// almost nothing else, and no chat is retained outside it.</summary>
    public static bool CaptureUnclassified { get; set; }

    /// <summary>The lines seen during the most recent sweep that matched none of the five keys,
    /// oldest first. The evidence for "the sentence is not arriving as chat at all" versus "it is
    /// arriving and the sheet text does not match it".</summary>
    public static IReadOnlyList<(DateTimeOffset At, XivChatType ChatType, string Text)> Unclassified => unclassified;

    /// <summary>The most recently classified line, or null if none has been seen this session.</summary>
    public static CropChatLine? Last { get; private set; }

    /// <summary>Fires once per classified line, after it has been recorded in <see cref="RecentLines"/>.</summary>
    public static event Action<CropChatLine>? Classified;

    /// <summary>The last <see cref="RingSize"/> classified lines, oldest first.</summary>
    public static IReadOnlyList<CropChatLine> RecentLines
    {
        get
        {
            var snapshot = new List<CropChatLine>(count);
            var start = (ring.Length + head - count) % ring.Length;
            for (var i = 0; i < count; i++)
                snapshot.Add(ring[(start + i) % ring.Length]);
            return snapshot;
        }
    }

    public static void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        var key = GardenMenuText.Classify(text);
        if (key is not (MenuKey.TalkNone or MenuKey.TalkVigorous or MenuKey.TalkDepressed
            or MenuKey.TalkRipe or MenuKey.TalkDead))
        {
            if (CaptureUnclassified)
            {
                if (unclassified.Count >= RingSize)
                    unclassified.RemoveAt(0);
                unclassified.Add((DateTimeOffset.UtcNow, message.LogKind, text));
            }
            return;
        }

        var line = new CropChatLine(DateTimeOffset.UtcNow, message.LogKind, key, text);
        Last = line;
        ring[head] = line;
        head = (head + 1) % RingSize;
        if (count < RingSize)
            count++;

        Classified?.Invoke(line);
    }
}
