using System;
using System.Collections.Generic;
using System.Numerics;
using Gardener.Localization;
using XivHubPluginKit.UI;

namespace Gardener.Helpers;

/// <summary>
/// Rolling log of scheduler activity, surfaced in the Log tab. Chat echo is gated on
/// <see cref="Configuration.NarrateSweepActions"/> as well as the per-call <c>chat</c> flag — a
/// separate switch from <see cref="Configuration.ChatReminders"/>, which only governs the periodic
/// due-list summary in <see cref="Reminders"/>. <paramref name="chatMessage"/>-less calls print the
/// same text to both surfaces; callers that need the Log tab's fuller diagnostic detail without
/// carrying it into chat pass a separate, shorter <c>chatMessage</c>.
/// </summary>
public static class ActivityLog
{
    public readonly record struct Entry(string Time, string Message, Vector4 Color);

    private const int MaxEntries = 60;

    // Ring buffer: oldest entry sits at `head`; `count` tracks the live size. Avoids the O(n)
    // shift that List.RemoveRange(0, k) does on every overflow.
    private static readonly Entry[] buffer = new Entry[MaxEntries];
    private static int head;
    private static int count;

    // A run of consecutive skips sharing one reason collapses into a single chat line instead of one
    // per bed, so a sweep that skips every bed for the same cause reads as one line rather than a
    // wall. Any other chat line — a success, a different skip reason, or the run's own finish or stop
    // — flushes whatever is pending first, so lines still appear in the order the sweep produced them.
    private static string? pendingSkipChatReason;
    private static int pendingSkipCount;
    private static int pendingSkipFirstBed;

    public static IReadOnlyList<Entry> Entries
    {
        get
        {
            var snapshot = new List<Entry>(count);
            var start = (buffer.Length + head - count) % buffer.Length;
            for (var i = 0; i < count; i++)
                snapshot.Add(buffer[(start + i) % buffer.Length]);
            return snapshot;
        }
    }

    public static void Notify(string message, bool chat = true, string? chatMessage = null)
    {
        Add(message, HubStyle.Text);
        if (chat && Plugin.C.NarrateSweepActions)
        {
            FlushPendingSkip();
            Plugin.ChatGui.Print(Loc.Format(Strings.Chat_Prefix, chatMessage ?? message));
        }
    }

    public static void Good_(string message, bool chat = true, string? chatMessage = null)
    {
        Add(message, HubStyle.Good);
        if (chat && Plugin.C.NarrateSweepActions)
        {
            FlushPendingSkip();
            Plugin.ChatGui.Print(Loc.Format(Strings.Chat_Prefix, chatMessage ?? message));
        }
    }

    public static void Warn_(string message, bool chat = true, string? chatMessage = null)
    {
        Add(message, HubStyle.Warn);
        if (chat && Plugin.C.NarrateSweepActions)
        {
            FlushPendingSkip();
            Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, chatMessage ?? message));
        }
    }

    /// <summary>One bed skipped during a sweep. Always recorded in full in the Log tab; in chat, a
    /// skip sharing <paramref name="chatReason"/> with the one immediately before it just extends the
    /// pending count instead of printing another line — see the class summary.</summary>
    public static void SkippedBed(int bedNumber, string logMessage, string chatReason)
    {
        Add(logMessage, HubStyle.Warn);

        if (!Plugin.C.NarrateSweepActions)
            return;

        if (chatReason == pendingSkipChatReason)
        {
            pendingSkipCount++;
            return;
        }

        FlushPendingSkip();
        pendingSkipChatReason = chatReason;
        pendingSkipCount = 1;
        pendingSkipFirstBed = bedNumber;
    }

    /// <summary>Prints whatever skip run is pending, then clears it. A no-op when nothing is pending,
    /// so every other chat-emitting call can call this unconditionally before printing its own line.</summary>
    public static void FlushPendingSkip()
    {
        if (pendingSkipChatReason is not { } reason)
            return;

        var line = pendingSkipCount == 1
            ? Loc.Format(Strings.ActivityLog_SkippedBeds_One, Formats.Number(pendingSkipFirstBed), reason)
            : Loc.Format(Strings.ActivityLog_SkippedBeds_Other, Formats.Number(pendingSkipCount), reason);
        Plugin.ChatGui.PrintError(Loc.Format(Strings.Chat_Prefix, line));

        pendingSkipChatReason = null;
        pendingSkipCount = 0;
    }

    public static void Clear()
    {
        head = 0;
        count = 0;
    }

    private static void Add(string message, Vector4 color)
    {
        buffer[head] = new Entry(DateTime.Now.ToString("HH:mm:ss"), message, color);
        head = (head + 1) % buffer.Length;
        if (count < buffer.Length) count++;
        Plugin.Telemetry?.Log(message);
    }
}
