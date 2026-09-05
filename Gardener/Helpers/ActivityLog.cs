using System;
using System.Collections.Generic;
using System.Numerics;
using XivHubPluginKit.UI;

namespace Gardener.Helpers;

/// <summary>
/// Rolling log of scheduler activity, surfaced in the Log tab. Chat echo is gated on
/// <see cref="Configuration.ChatReminders"/> as well as the per-call <c>chat</c> flag, so a run stays
/// quiet in chat by default and only narrates itself there when the user opts in.
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

    public static void Notify(string message, bool chat = true)
    {
        Add(message, HubStyle.Text);
        if (chat && Plugin.C.ChatReminders)
            Plugin.ChatGui.Print($"[Gardener] {message}");
    }

    public static void Good_(string message, bool chat = true)
    {
        Add(message, HubStyle.Good);
        if (chat && Plugin.C.ChatReminders)
            Plugin.ChatGui.Print($"[Gardener] {message}");
    }

    public static void Warn_(string message, bool chat = true)
    {
        Add(message, HubStyle.Warn);
        if (chat && Plugin.C.ChatReminders)
            Plugin.ChatGui.PrintError($"[Gardener] {message}");
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
