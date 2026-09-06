using System;
using System.Collections.Generic;
using System.Linq;
using Gardener.Game;
using Gardener.Journal;
using Gardener.Localization;
using Gardener.Scheduler;

namespace Gardener.Helpers;

/// <summary>One journal record's worth of reminder: which house and patch it belongs to, its bed
/// number and seed, the time value that put it on the list, and which of the account's characters
/// have been seen able to reach the house it is in — so a reminder for an action that needs house
/// permission (harvesting, planting, fertilizing, removing) can read "switch to &lt;name&gt;" rather
/// than naming a garden the logged-in character cannot act on. Tending needs no such permission — any
/// character can tend or water any outdoor garden — so <see cref="Reminders.DueToTend"/> and
/// <see cref="Reminders.AboutToWither"/> carry this field but its consumers never display it.</summary>
public readonly record struct ReminderEntry(
    string HouseKey,
    string PatchKey,
    int BedNumber,
    string SeedName,
    DateTimeOffset At,
    IReadOnlyList<string> ReachableBy);

/// <summary>A <see cref="ReminderEntry"/> for <see cref="Reminders.ReadyToHarvest"/>, carrying the
/// full <see cref="Growth.HarvestWindow"/> alongside it: <see cref="FromWindow"/> is false when stage
/// 4 alone put the bed on the list and the window itself has not yet passed, which is what tells the
/// UI to say "mature" rather than naming a harvest time it cannot back up.</summary>
public readonly record struct HarvestReminderEntry(ReminderEntry Entry, bool FromWindow, HarvestWindow Window);

/// <summary>
/// Journal-only due lists, readable from anywhere in the world: the passive <c>DataMap</c> read and
/// the object table are both unavailable outside the housing territory a bed sits in, so this reads
/// nothing but <see cref="GardenJournal"/>. Recomputed at most once a second from
/// <see cref="Tick"/>, never per frame.
/// </summary>
public static class Reminders
{
    private static readonly TimeSpan RecomputeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AboutToWitherLeadTime = TimeSpan.FromHours(6);

    private static DateTimeOffset lastRecomputedAt = DateTimeOffset.MinValue;
    private static DateTimeOffset lastChatEchoAt = DateTimeOffset.MinValue;

    /// <summary>The character logged in as of the last <see cref="Recompute"/>, so
    /// <see cref="ReachText"/> can stay off the object table on the draw thread.</summary>
    private static string? currentCharacter;

    public static IReadOnlyList<ReminderEntry> DueToTend { get; private set; } = Array.Empty<ReminderEntry>();
    public static IReadOnlyList<ReminderEntry> AboutToWither { get; private set; } = Array.Empty<ReminderEntry>();
    public static IReadOnlyList<HarvestReminderEntry> ReadyToHarvest { get; private set; } = Array.Empty<HarvestReminderEntry>();
    public static IReadOnlyList<ReminderEntry> TimingUnknown { get; private set; } = Array.Empty<ReminderEntry>();

    /// <summary>Call once per framework tick; recomputes and, when due, echoes to chat at most once
    /// every <see cref="Configuration.ReminderIntervalMin"/> minutes.</summary>
    public static void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastRecomputedAt < RecomputeInterval)
            return;
        lastRecomputedAt = now;

        Recompute(now);
        MaybeEchoToChat(now);
    }

    private static void Recompute(DateTimeOffset now)
    {
        var dueToTend = new List<ReminderEntry>();
        var aboutToWither = new List<ReminderEntry>();
        var readyToHarvest = new List<HarvestReminderEntry>();
        var timingUnknown = new List<ReminderEntry>();

        var wiltWarning = TimeSpan.FromHours(Plugin.C.WiltWarningHours);
        currentCharacter = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;

        foreach (var record in GardenJournal.AllRecords)
        {
            // Storage freezes every timer and the crop cannot be reached at all, so a parked bed is
            // on no list here. Growth already returns null for the three time-based ones; this is
            // what keeps the raw stage-4 and missing-timestamp reads below from contradicting it.
            if (record.Parked)
                continue;

            var wiltsAt = Growth.WiltsAt(record);
            if (wiltsAt is { } wa && wa <= now + wiltWarning)
                dueToTend.Add(BuildEntry(record, wa));

            var withersAt = Growth.WithersAt(record);
            if (withersAt is { } wia && wia <= now + AboutToWitherLeadTime)
                aboutToWither.Add(BuildEntry(record, wia));

            var window = Growth.HarvestWindow(record);
            var windowPassed = window.Earliest is { } earliest && earliest <= now;
            if (windowPassed || record.LastSeenStage == 4)
            {
                var at = windowPassed ? window.Earliest!.Value : record.FirstSeenStage4At ?? record.LastSeenAt;
                readyToHarvest.Add(new HarvestReminderEntry(BuildEntry(record, at), windowPassed, window));
            }

            if (record.PlantedAt is null || record.LastTendedAt is null)
                timingUnknown.Add(BuildEntry(record, record.LastSeenAt));
        }

        DueToTend = dueToTend;
        AboutToWither = aboutToWither;
        ReadyToHarvest = readyToHarvest;
        TimingUnknown = timingUnknown;
    }

    private static ReminderEntry BuildEntry(BedRecord record, DateTimeOffset at)
    {
        var houseKey = HouseKeyOf(record.PatchKey);
        return new ReminderEntry(
            houseKey,
            record.PatchKey,
            record.BedNumber,
            SeedItems.ProduceName(record.SeedRow),
            at,
            GardenJournal.CharactersWithAccess(houseKey));
    }

    private static string HouseKeyOf(string patchKey)
    {
        var separator = patchKey.IndexOf(':');
        return separator < 0 ? patchKey : patchKey[..separator];
    }

    /// <summary>The reachability text for one entry: who to switch to, or that no character has been
    /// seen able to reach this house yet. For an action that needs house permission — harvesting,
    /// planting, fertilizing, removing — never for tending, which needs none. Null when the character
    /// already logged in is one of them: naming the character the player is standing on is the one
    /// answer that helps nobody, and callers render no note at all for it.</summary>
    public static string? ReachText(IReadOnlyList<string> reachableBy)
    {
        if (currentCharacter is { Length: > 0 } here && reachableBy.Contains(here, StringComparer.Ordinal))
            return null;

        return reachableBy.Count > 0
            ? Loc.Format(Strings.Reminders_ReachSwitch, TextList.Or(reachableBy))
            : Strings.Reminders_ReachNone;
    }

    /// <summary>The short summary both the DTR entry and the chat echo read, e.g. "3 to tend, 1
    /// ready" — built here once so the two surfaces never drift apart. The joining ", " itself stays a
    /// literal separator rather than a keyed value: each fragment it joins is already a whole,
    /// grammatically inert noun phrase (composition contract rule 2).</summary>
    public static string SummaryText()
    {
        var parts = new List<string>();
        if (DueToTend.Count > 0) parts.Add(CountFragment(DueToTend.Count, Strings.Reminders_SummaryToTend_One, Strings.Reminders_SummaryToTend_Other));
        if (AboutToWither.Count > 0) parts.Add(CountFragment(AboutToWither.Count, Strings.Reminders_SummaryWither_One, Strings.Reminders_SummaryWither_Other));
        if (ReadyToHarvest.Count > 0) parts.Add(CountFragment(ReadyToHarvest.Count, Strings.Reminders_SummaryReady_One, Strings.Reminders_SummaryReady_Other));
        return parts.Count > 0 ? string.Join(", ", parts) : Strings.Reminders_SummaryNothingDue;
    }

    /// <summary>The same summary as <see cref="SummaryText"/>, filtered to one patch — the Garden
    /// tab's per-patch header line, so a collapsed patch still answers "what needs doing" without
    /// expanding it.</summary>
    public static string SummaryTextFor(string patchKey)
    {
        var parts = new List<string>();
        var dueToTend = DueToTend.Count(e => e.PatchKey == patchKey);
        var aboutToWither = AboutToWither.Count(e => e.PatchKey == patchKey);
        var readyToHarvest = ReadyToHarvest.Count(h => h.Entry.PatchKey == patchKey);
        var timingUnknown = TimingUnknown.Count(e => e.PatchKey == patchKey);
        if (dueToTend > 0) parts.Add(CountFragment(dueToTend, Strings.Reminders_SummaryToTend_One, Strings.Reminders_SummaryToTend_Other));
        if (aboutToWither > 0) parts.Add(CountFragment(aboutToWither, Strings.Reminders_SummaryWither_One, Strings.Reminders_SummaryWither_Other));
        if (readyToHarvest > 0) parts.Add(CountFragment(readyToHarvest, Strings.Reminders_SummaryReady_One, Strings.Reminders_SummaryReady_Other));
        return parts.Count > 0 ? string.Join(", ", parts) : Strings.Reminders_SummaryNothingDue;
    }

    private static string CountFragment(int count, string oneTemplate, string otherTemplate) =>
        Loc.Format(count == 1 ? oneTemplate : otherTemplate, Formats.Number(count));

    /// <summary>Never while a sweep is running, so a chat echo never lands mid-narration of the
    /// scheduler's own activity log.</summary>
    private static void MaybeEchoToChat(DateTimeOffset now)
    {
        if (!Plugin.C.ChatReminders)
            return;
        if (SchedulerMain.Running)
            return;
        if (DueToTend.Count == 0 && AboutToWither.Count == 0 && ReadyToHarvest.Count == 0)
            return;
        if (now - lastChatEchoAt < TimeSpan.FromMinutes(Plugin.C.ReminderIntervalMin))
            return;

        lastChatEchoAt = now;
        Plugin.ChatGui.Print(Loc.Format(Strings.Chat_Prefix, SummaryText()));
    }
}
