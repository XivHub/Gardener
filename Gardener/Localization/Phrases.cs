using System;
using System.Linq;

namespace Gardener.Localization
{
    /// <summary>
    /// Duration and quantity noun phrases shared by every generated route and status line. Each of
    /// these is a complete, grammatically inert noun phrase in every language it is translated into —
    /// never a fragment a caller embeds as a sentence's own subject or verb — so a template only ever
    /// substitutes the whole result of one of these, never composes text around it.
    /// </summary>
    public static class Phrases
    {
        public static string Days(double days) =>
            days == 1
                ? Loc.Format(Strings.Duration_Days_One, Formats.Number(days, "0.#"))
                : Loc.Format(Strings.Duration_Days_Other, Formats.Number(days, "0.#"));

        public static string Hours(double hours) =>
            hours == 1
                ? Loc.Format(Strings.Duration_Hours_One, Formats.Number(hours, "0.#"))
                : Loc.Format(Strings.Duration_Hours_Other, Formats.Number(hours, "0.#"));

        public static string Minutes(int minutes) =>
            minutes == 1
                ? Loc.Format(Strings.Duration_Minutes_One, Formats.Number(minutes))
                : Loc.Format(Strings.Duration_Minutes_Other, Formats.Number(minutes));

        /// <summary>How long until a moment, as an inert noun phrase: whole minutes under an hour,
        /// whole hours out to two days, then days to one decimal. A span that has already elapsed
        /// reads as one minute rather than as negative time, since every caller has its own branch
        /// for a deadline that has passed.</summary>
        public static string Until(TimeSpan span)
        {
            if (span.TotalHours < 1)
                return Minutes(Math.Max(1, (int)Math.Round(span.TotalMinutes)));

            return span.TotalHours < 48
                ? Hours(Math.Round(span.TotalHours))
                : Days(Math.Round(span.TotalDays, 1));
        }

        /// <summary>"About" plus <see cref="Until"/>, for a span read off the harvest window rather
        /// than the wilt cadence: that window is a range and its planting anchor is often inferred,
        /// so the phrase must not read as a promise of a single moment.</summary>
        public static string AboutUntil(TimeSpan span) => Loc.Format(Strings.Duration_About, Until(span));

        public static string AboutDays(double days) => Loc.Format(Strings.Duration_About, Days(days));

        public static string AboutHours(double hours) => Loc.Format(Strings.Duration_About, Hours(hours));

        /// <summary>"every day" for a 24-hour cadence, "every {n} days" for anything else — the wilt
        /// and tend cadence this wraps is always day-scale, never hour-scale.</summary>
        public static string EveryDay(int hours)
        {
            var days = Math.Round(hours / 24.0, 1);
            return days == 1 ? Strings.Duration_EveryDay : Loc.Format(Strings.Duration_Every, Days(days));
        }

        /// <summary>One number when every soil tier agrees, a range otherwise, and an inert
        /// "unknown count" phrase when the bundled data has nothing at all — the yield-tier meaning
        /// itself is unrecorded, so quoting one tier by guesswork would claim precision nobody has.</summary>
        public static string OneNumberOrRange(int[]? tiers)
        {
            if (tiers is not { Length: > 0 })
                return Strings.Phrase_UnknownCount;
            return tiers.All(t => t == tiers[0])
                ? Formats.Number(tiers[0])
                : Loc.Format(Strings.Phrase_NumberRange, Formats.Number(tiers.Min()), Formats.Number(tiers.Max()));
        }
    }
}
