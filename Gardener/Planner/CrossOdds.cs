using System;
using Gardener.Game;
using Gardener.Localization;

namespace Gardener.Planner;

/// <summary>
/// Intercross odds and how they turn into a bed count and a spoken phrase. Every number in
/// <see cref="Intercross"/> is a community estimate, never a measured
/// figure, which is why <see cref="OddsPhrase"/> only ever speaks in tenths and never a percent or a
/// decimal.
/// </summary>
public static class CrossOdds
{
    /// <summary>Community-estimated intercross chance per soil grade. Only Thanalan moves the needle;
    /// every other family is lumped at Shroud's measured-near-zero rate rather than given its own
    /// unmeasured number.</summary>
    public static double Intercross(SoilFamily family, int grade) => (family, grade) switch
    {
        (SoilFamily.Thanalan, 3) => 0.90,
        (SoilFamily.Thanalan, 2) => 0.50,
        (SoilFamily.Thanalan, 1) => 0.25,
        _ => 0.05,
    };

    /// <summary>The chance a single planting lands on one particular outcome of a pair with
    /// <paramref name="pairTargetCount"/> possible offspring, split evenly across them (G4: nobody
    /// publishes the real split).</summary>
    public static double Chance(int pairTargetCount, SoilFamily family, int grade) =>
        Intercross(family, grade) / Math.Max(1, pairTargetCount);

    /// <summary>How many independent attempts at <paramref name="chance"/> it takes to clear a 90%
    /// chance of at least one success: <c>ceil(ln(0.10) / ln(1 - chance))</c>. A chance of zero or
    /// below never resolves, so it reports <see cref="int.MaxValue"/> rather than looping forever or
    /// dividing by zero.</summary>
    public static int BedsForNineInTen(double chance)
    {
        if (chance <= 0)
            return int.MaxValue;
        if (chance >= 1)
            return 1;
        return (int)Math.Ceiling(Math.Log(0.10) / Math.Log(1 - chance));
    }

    /// <summary>"About N in 10", N clamped to 1..10 — the only way odds are ever spoken, since the
    /// intercross estimate (G3) and the even split across outcomes (G4) are both unmeasured and a
    /// percentage or a decimal would claim a precision neither has. Its one call site
    /// (<see cref="GoalRoute"/>'s <c>Goal_OddsOfThemGive</c> sentence) always uses this sentence-initially,
    /// so <c>Odds_AboutNInTen</c> is authored already capitalized rather than capitalized at runtime —
    /// runtime capitalization of an arbitrary localized string is not safe in every script.</summary>
    public static string OddsPhrase(double chance) => Loc.Format(Strings.Odds_AboutNInTen, Formats.Number(Tenths(chance)));

    /// <summary>The clamped tenths count <see cref="OddsPhrase"/> speaks in, split out so a caller that
    /// needs the bare count (rather than the whole "About N in 10" phrase) does not re-derive the
    /// clamp.</summary>
    public static int Tenths(double chance) => Math.Clamp((int)Math.Round(chance * 10), 1, 10);
}
