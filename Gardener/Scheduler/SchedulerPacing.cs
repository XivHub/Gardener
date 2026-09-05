using System;

namespace Gardener.Scheduler;

/// <summary>
/// Jitters the scheduler's fixed pauses (<see cref="Configuration.StepDelayMs"/> and
/// <see cref="Configuration.BedDelayMs"/>) by roughly plus or minus 25%, centralised here so every
/// task gets the same variation rather than each one rolling its own. A perfectly regular interval is
/// both a worse fit for something driving a character and, at the low end, exactly the kind of gap
/// that has raced ahead of an addon that had not opened yet.
/// </summary>
public static class SchedulerPacing
{
    private const double JitterFraction = 0.25;

    private static readonly Random Random = new();

    /// <summary><see cref="Configuration.StepDelayMs"/>, jittered.</summary>
    public static int StepDelay() => Jitter(Plugin.C.StepDelayMs);

    /// <summary><see cref="Configuration.BedDelayMs"/>, jittered.</summary>
    public static int BedDelay() => Jitter(Plugin.C.BedDelayMs);

    /// <summary><paramref name="baseMs"/> randomised within +/-<see cref="JitterFraction"/>, floored
    /// at 1ms so the result can never land at or below zero.</summary>
    private static int Jitter(int baseMs)
    {
        var factor = 1.0 + ((Random.NextDouble() * 2 - 1) * JitterFraction);
        return Math.Max(1, (int)Math.Round(baseMs * factor));
    }
}
