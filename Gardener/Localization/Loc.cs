using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gardener.Localization
{
    /// <summary>
    /// Owns the culture every localized format call renders through. Never writes
    /// <see cref="CultureInfo.CurrentCulture"/>: that is the game's own draw thread, shared with
    /// every other plugin, and mutating it here would be hostile to all of them.
    /// </summary>
    public static class Loc
    {
        /// <summary>Every UI language Gardener ships a resx for. A third language is one more
        /// entry here plus one more Strings.&lt;lang&gt;.resx; nothing else in this class changes.</summary>
        public static readonly string[] SupportedLanguages = ["en", "es"];

        public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

        // One warning per bad template, not one per frame: a mistranslated slot index would
        // otherwise throw and get caught here every single ImGui draw call.
        private static readonly HashSet<string> warnedTemplates = new();

        /// <summary>
        /// Resolves a requested language code (Dalamud's <c>UiLanguage</c>, or the player's
        /// override) to one Gardener actually has a resx for, defaulting to English for anything
        /// else — an unsupported or empty code must never leave <see cref="Culture"/> unset.
        /// </summary>
        public static string Resolve(string requested)
        {
            if (string.IsNullOrEmpty(requested))
                return "en";

            var code = requested.Length >= 2 ? requested[..2].ToLowerInvariant() : requested.ToLowerInvariant();
            return Array.IndexOf(SupportedLanguages, code) >= 0 ? code : "en";
        }

        /// <summary>
        /// Sets <see cref="Culture"/> and <see cref="Strings.Culture"/> together so every
        /// <see cref="Strings"/> lookup and every <see cref="Format"/> call agree on the same
        /// language from the same call. English maps to the invariant culture, not
        /// <c>CultureInfo("en")</c>, because the neutral resx's own culture is invariant and
        /// numeric/date formatting should match it exactly. Spanish maps to
        /// <c>CultureInfo("es-ES")</c>, not the neutral <c>CultureInfo("es")</c>, so numbers and
        /// dates render Spain's own conventions (decimal comma, day/month/year, 24-hour clock)
        /// for the peninsular Spanish this UI is translated into; <see cref="Strings"/>'s
        /// <c>ResourceManager</c> still resolves the <c>Strings.es.resx</c> satellite assembly
        /// through .NET's ordinary culture-fallback chain (es-ES -&gt; es -&gt; neutral), so no
        /// second resx or "es-ES" file name is needed.
        /// </summary>
        public static void SetLanguage(string langCode)
        {
            var resolved = Resolve(langCode);
            Culture = resolved switch
            {
                "en" => CultureInfo.InvariantCulture,
                "es" => new CultureInfo("es-ES"),
                _ => new CultureInfo(resolved),
            };
            Strings.Culture = Culture;
        }

        /// <summary>
        /// The only path a localized value may reach <see cref="string.Format(IFormatProvider,string,object?[])"/>
        /// through. A translated value with a slot index the template doesn't have throws
        /// <see cref="FormatException"/>; since that would otherwise fire inside an ImGui draw call
        /// every frame and take the whole window down with it, this returns the template unchanged
        /// instead and logs once.
        /// </summary>
        public static string Format(string template, params object?[] args)
        {
            try
            {
                return string.Format(Culture, template, args);
            }
            catch (FormatException)
            {
                if (warnedTemplates.Add(template))
                    Plugin.Logger.Warning($"[Loc] bad format slot in template: {template}");
                return template;
            }
        }
    }
}
