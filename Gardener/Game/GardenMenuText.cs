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

        var missing = Enum.GetValues<MenuKey>().Where(k => k != MenuKey.Unknown && !textByKey.ContainsKey(k)).ToList();
        if (missing.Count > 0)
            Plugin.Logger.Warning($"[GardenMenuText] sheet '{SheetName}' is missing key(s): {string.Join(", ", missing)}");

        return true;
    }

    /// <summary>Classifies observed <c>SelectString</c> or <c>Talk</c> text by exact match against
    /// the sheet's localised text, never by index and never by English.</summary>
    public static MenuKey Classify(string text) =>
        Available && keyByText.TryGetValue(text, out var key) ? key : MenuKey.Unknown;

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
            SeStringParameter[] parameters = { bedProbe, patchProbe };
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

            return bedIndex < patchIndex;
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
