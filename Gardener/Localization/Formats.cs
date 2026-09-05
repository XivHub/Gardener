using System;

namespace Gardener.Localization
{
    /// <summary>
    /// Number and date/time rendering for localized text, always through <see cref="Loc.Culture"/>
    /// rather than the ambient thread culture (see <see cref="Loc"/>'s own remarks). Every
    /// grammatically inert value substituted into a <see cref="Strings"/> template should be
    /// produced by one of these, not by an inline <c>ToString()</c>.
    /// </summary>
    public static class Formats
    {
        public static string Number(int value) => value.ToString(Loc.Culture);

        public static string Number(double value, string format) => value.ToString(format, Loc.Culture);

        public static string LocalDateTime(DateTimeOffset value) => value.ToString("g", Loc.Culture);

        public static string LocalTime(DateTimeOffset value) => value.ToString("t", Loc.Culture);
    }
}
