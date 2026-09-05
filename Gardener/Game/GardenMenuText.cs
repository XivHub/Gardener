using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.Evaluator;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;

namespace Gardener.Game;

/// <summary>The fourteen lines of the bed menu's text sheet, classified by the sheet's stable key
/// column rather than by row index or by English, so a sheet reorder or a different client language
/// never breaks matching.</summary>
public enum MenuKey
{
    TalkNone,
    EndEvent,
    SetSeed,
    SetFertilizer,
    Care,
    Dispose,
    Harvest,
    TalkDead,
    TalkVigorous,
    TalkDepressed,
    TalkRipe,
    AskDispose,
    AskDisposeYes,
    AskDisposeNo,
    Unknown,
}

/// <summary>
/// Reads <c>custom/001/CmnDefHousingGardeningPlant_00151</c> and classifies observed <c>SelectString</c>
/// / <c>Talk</c> text against it by key. Also parses the bed and patch numbers the game itself prints
/// into the <c>SelectString</c> prompt (<c>Addon</c> sheet row 6420, "Nth Bed, Mth Patch") — the
/// numbers are the only source of authoritative bed identity, but their order in the sentence is not
/// fixed across client languages, so the order is learned once at load rather than assumed.
/// </summary>
public static class GardenMenuText
{
    // The gardening block of the Addon sheet spells the context-menu action "Fertilize".
    private static readonly uint[] FertilizeAddonRows = { 6417u, 6423u };

    private const string SheetName = "custom/001/CmnDefHousingGardeningPlant_00151";
    private const string KeyPrefix = "TEXT_CMNDEFHOUSINGGARDENINGPLANT";
    private const uint BedPatchPromptRow = 6420;

    // Longest suffix first so "_ASK_DISPOSE_YES" etc. do not fall through to the shorter
    // "_DISPOSE" match that is also a suffix of every _ASK_DISPOSE* key.
    private static readonly (string Suffix, MenuKey Key)[] suffixMap = new (string, MenuKey)[]
    {
        ("_TALK_NONE", MenuKey.TalkNone),
        ("_END_EVENT", MenuKey.EndEvent),
        ("_SET_SEED", MenuKey.SetSeed),
        ("_SET_FERTILIZER", MenuKey.SetFertilizer),
        ("_CARE", MenuKey.Care),
        ("_HARVEST", MenuKey.Harvest),
        ("_TALK_DEAD", MenuKey.TalkDead),
        ("_TALK_VIGOROUS", MenuKey.TalkVigorous),
        ("_TALK_DEPRESSED", MenuKey.TalkDepressed),
        ("_TALK_RIPE", MenuKey.TalkRipe),
        ("_ASK_DISPOSE_YES", MenuKey.AskDisposeYes),
        ("_ASK_DISPOSE_NO", MenuKey.AskDisposeNo),
        ("_ASK_DISPOSE", MenuKey.AskDispose),
        ("_DISPOSE", MenuKey.Dispose),
    }.OrderByDescending(s => s.Item1.Length).ToArray();

    private static readonly Dictionary<MenuKey, string> textByKey = new();
    private static readonly Dictionary<string, MenuKey> keyByText = new();
    private static readonly bool bedNumberIsFirst;

    /// <summary>The five sentences a bed interaction speaks, as opposed to the entries it offers.
    /// The sheet stores each with a leading newline and the game prints it after the crop's own name
    /// in one message — "Almonds\nThis crop is doing well." — so these are matched as the tail of a
    /// line rather than the whole of it.</summary>
    private static readonly MenuKey[] SpokenKeys =
    {
        MenuKey.TalkNone, MenuKey.TalkVigorous, MenuKey.TalkDepressed, MenuKey.TalkRipe, MenuKey.TalkDead,
    };

    private static readonly List<(string Text, MenuKey Key)> spokenTexts = new();

    /// <summary>False if the sheet or its key/text columns could not be identified; every lookup
    /// then returns <see cref="MenuKey.Unknown"/> instead of guessing a column index.</summary>
    public static bool Available { get; }

    static GardenMenuText()
    {
        Available = Load();
        bedNumberIsFirst = ProbeBedPatchOrder();
    }

    private static bool Load()
    {
        ExcelSheet<RawRow> sheet;
        try
        {
            sheet = Plugin.DataManager.GetExcelSheet<RawRow>(null, SheetName);
        }
        catch (Exception ex)
        {
            Plugin.Logger.Warning(ex, $"[GardenMenuText] sheet '{SheetName}' could not be loaded; menu text classification disabled");
            return false;
        }

        if (sheet.Count == 0)
        {
            Plugin.Logger.Warning($"[GardenMenuText] sheet '{SheetName}' has no rows; menu text classification disabled");
            return false;
        }

        var columns = sheet.Columns;
        var stringColumns = Enumerable.Range(0, columns.Count)
            .Where(i => columns[i].Type == ExcelColumnDataType.String)
            .ToList();

        var firstRow = sheet.GetRowAt(0);
        var keyColumn = stringColumns.FirstOrDefault(
            i => firstRow.ReadStringColumn(i).ExtractText().StartsWith(KeyPrefix, StringComparison.Ordinal), -1);
        var textColumn = stringColumns.FirstOrDefault(i => i != keyColumn, -1);

        if (keyColumn < 0 || textColumn < 0)
        {
            Plugin.Logger.Warning(
                $"[GardenMenuText] could not identify the key/text columns of '{SheetName}' " +
                $"(found {stringColumns.Count} string column(s)); menu text classification disabled");
            return false;
        }

        foreach (var row in sheet)
        {
            var key = row.ReadStringColumn(keyColumn).ExtractText();
            var text = row.ReadStringColumn(textColumn).ExtractText();
            var match = suffixMap.FirstOrDefault(s => key.EndsWith(s.Suffix, StringComparison.Ordinal));
            if (match.Suffix is null)
                continue;

            textByKey[match.Key] = text;
            keyByText[text] = match.Key;
        }

        foreach (var key in SpokenKeys)
            if (textByKey.TryGetValue(key, out var spoken) && Normalize(spoken).Length > 0)
                spokenTexts.Add((Normalize(spoken), key));

        var missing = Enum.GetValues<MenuKey>().Where(k => k != MenuKey.Unknown && !textByKey.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            Plugin.Logger.Warning($"[GardenMenuText] sheet '{SheetName}' is missing key(s): {string.Join(", ", missing)}");

        return true;
    }

    /// <summary>Classifies observed <c>SelectString</c> or <c>Talk</c> text by exact match against
    /// the sheet's localised text, never by index and never by English.</summary>
    /// <summary>
    /// True when <paramref name="text"/> is the fertilize entry of an item's inventory context menu.
    /// That menu is worded from the <c>Addon</c> sheet ("Fertilize"), not from the bed menu's own
    /// sheet ("Fertilize Crop"), so both spellings have to be accepted to reach the same action.
    /// </summary>
    public static bool IsFertilizeAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (Classify(text) == MenuKey.SetFertilizer)
            return true;

        foreach (var row in FertilizeAddonRows)
        {
            var label = Sheets.AddonSheet.GetRowOrDefault(row)?.Text.ExtractText();
            if (!string.IsNullOrWhiteSpace(label) &&
                string.Equals(label.Trim(), text.Trim(), StringComparison.CurrentCultureIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The key for one observed line of menu or dialogue text. A menu entry and the bed prompt match
    /// the sheet exactly; a spoken line arrives with the crop's name in front of it, so it is matched
    /// on its tail instead — see <see cref="SpokenKeys"/>. Exact first, so a menu entry can never be
    /// resolved by the looser rule.
    /// </summary>
    public static MenuKey Classify(string text)
    {
        if (!Available)
            return MenuKey.Unknown;

        if (keyByText.TryGetValue(text, out var key))
            return key;

        var normalized = Normalize(text);
        foreach (var (spoken, spokenKey) in spokenTexts)
            if (normalized.EndsWith(spoken, StringComparison.Ordinal))
                return spokenKey;

        return MenuKey.Unknown;
    }

    /// <summary>Line endings and the sheet's own leading newline out, so a spoken line compares the
    /// same whether the newline survives <c>ExtractText</c> or the client sends CRLF.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    /// <summary>The localised text for a key, if the sheet loaded and carries that key.</summary>
    public static string? TextFor(MenuKey key) => textByKey.TryGetValue(key, out var text) ? text : null;

    /// <summary>
    /// Extracts the bed and patch numbers the game itself printed into the <c>SelectString</c>
    /// prompt (e.g. "8th Bed, 1st Patch"), ordered using the position learned at load for the
    /// client's language. Null if the prompt does not contain two numbers.
    /// </summary>
    public static (int Bed, int Patch)? ParseBedPatch(string promptText)
    {
        var numbers = ExtractNumbers(promptText);
        if (numbers.Count < 2)
            return null;
        return bedNumberIsFirst ? (numbers[0], numbers[1]) : (numbers[1], numbers[0]);
    }

    /// <summary>
    /// Evaluates <c>Addon</c> row 6420 with two distinct sentinel numbers — the first standing in
    /// for the bed number, the second for the patch number — and records which one prints first for
    /// the client's current language. Digits stay digits in every locale; only their position in the
    /// sentence moves.
    /// </summary>
    private static bool ProbeBedPatchOrder()
    {
        const int bedProbe = 71;
        const int patchProbe = 53;
        try
        {
            // The client fills <lnum(1)> with the patch number and <lnum(2)> with the bed number,
            // the reverse of the English sentence's own reading order, so the probes go in that
            // order rather than the intuitive one. The binding is client code rather than sheet
            // text and so holds in every locale; only the reading order measured below moves with
            // the language. The startup line logs what this resolved to, since a wrong binding
            // would silently swap every bed number.
            SeStringParameter[] parameters = { patchProbe, bedProbe };
            var evaluated = Plugin.SeStringEvaluator.EvaluateFromAddon(BedPatchPromptRow, parameters).ExtractText();
            var numbers = ExtractNumbers(evaluated);
            var bedIndex = numbers.IndexOf(bedProbe);
            var patchIndex = numbers.IndexOf(patchProbe);
            if (bedIndex < 0 || patchIndex < 0)
            {
                Plugin.Logger.Warning(
                    $"[GardenMenuText] Addon row {BedPatchPromptRow} evaluated to \"{evaluated}\", which does not " +
                    "contain both probe numbers; assuming the bed number prints first");
                return true;
            }

            var bedFirst = bedIndex < patchIndex;
            Plugin.Logger.Information(
                $"[GardenMenuText] bed/patch order probe: language={Plugin.ClientState.ClientLanguage}, " +
                $"probe rendered \"{evaluated}\", position 1={(bedFirst ? "bed" : "patch")}, position 2={(bedFirst ? "patch" : "bed")}");
            return bedFirst;
        }
        catch (Exception ex)
        {
            Plugin.Logger.Warning(ex, $"[GardenMenuText] failed to evaluate Addon row {BedPatchPromptRow}; assuming the bed number prints first");
            return true;
        }
    }

    private static List<int> ExtractNumbers(string text) =>
        Regex.Matches(text, "\\d+").Select(m => int.Parse(m.Value)).ToList();
}
