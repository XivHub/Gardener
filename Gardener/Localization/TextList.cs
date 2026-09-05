using System.Collections.Generic;
using System.Linq;

namespace Gardener.Localization
{
    /// <summary>
    /// Joins a list of names with a localized conjunction: "A, B and C" / "A, B or C". Every list this
    /// joins holds client-language item, produce or character names, so the standard Spanish euphonic
    /// switch (y -&gt; e before an /i/ sound, o -&gt; u before an /o/ sound) is deliberately not
    /// implemented here: that rule keys off pronunciation, and a foreign proper noun's Spanish
    /// pronunciation cannot be derived from its spelling. A spelling-based heuristic would fire
    /// inconsistently and read worse than a uniform y/o, so this stays uniform on purpose — do not
    /// "fix" it without a real pronunciation source for every name it might join.
    /// </summary>
    public static class TextList
    {
        public static string And(IReadOnlyList<string> items) => Join(items, Strings.List_And);

        public static string Or(IReadOnlyList<string> items) => Join(items, Strings.List_Or);

        private static string Join(IReadOnlyList<string> items, string conjunction)
        {
            if (items.Count == 0)
                return string.Empty;
            if (items.Count == 1)
                return items[0];

            var head = string.Join(Strings.List_Separator, items.Take(items.Count - 1));
            return Loc.Format(conjunction, head, items[^1]);
        }
    }
}
