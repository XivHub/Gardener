using System;
using System.Collections.Generic;
using System.Linq;
using Gardener.Game;

namespace Gardener.Planner;

/// <summary>
/// Finds the cheapest route to a target seed and turns it into the ordered, worded steps the Goal tab
/// renders. "Cheapest" means fewest expected bed-hours, not fewest crosses: two routes to the same seed
/// can both be two generations deep and differ by a third in bed time, which a shortest-chain search
/// cannot tell apart. Pure over its three arguments — no live scan, no <c>Plugin.C</c> read — so it is
/// exercised without the game.
/// </summary>
public static class GoalRoute
{
    /// <summary>The notional cost of one gathering or vendor trip: far below any grow time, so the
    /// search never invents a cross for a seed that can simply be bought, but not zero, so a route
    /// needing several different gathered seeds scores above one needing fewer. A dial with a derived
    /// default, not a config field — nothing outside this file needs to know it exists.</summary>
    private const double GatherCostHours = 6.0;

    // The route search has no live bag to read a soil grade from, so it costs and sizes every crossing
    // step against the top grade of whichever family crossSoil resolves to — the same "Grade 3
    // Thanalan assumption" the acceptance route is measured against. Task_Plant and the handoff at
    // render time still resolve the soil actually held.
    private const int AssumedSoilGrade = 3;

    // A crossing bed wants the intercross family; a bed planted only to bulk up a seed's own count
    // wants the yield family instead. This is a fact about what each family does, not a user
    // preference, so it is never threaded through as a parameter.
    private const SoilPreference MultiplySoil = SoilPreference.HighestShroud;

    public static GoalPlan? Solve(uint goalRow, IReadOnlyDictionary<uint, int> held, SoilPreference crossSoil)
    {
        if (SeedTable.Gatherable(goalRow) == true)
            return null; // nothing to plan: the tab shows "you do not need to crossbreed this" instead

        var family = FamilyFor(crossSoil);
        var (cost, winner) = Relax(held, family);
        if (!cost.TryGetValue(goalRow, out var goalCost) || double.IsPositiveInfinity(goalCost))
            return null;

        // Walk the winning pairs from the goal, parents before children (post-order), splitting what
        // is reached into cross-only nodes this route must plant for (order) and leaves it must obtain
        // (leaves) — a leaf is anything with no winner entry, i.e. already held or bought/gathered.
        var order = new List<uint>();
        var leafOrder = new List<uint>();
        var visited = new HashSet<uint>();

        void Visit(uint row)
        {
            if (!visited.Add(row))
                return;
            if (winner.TryGetValue(row, out var parents))
            {
                Visit(parents.A);
                Visit(parents.B);
                order.Add(row);
            }
            else
            {
                leafOrder.Add(row);
            }
        }
        Visit(goalRow);

        // Attempts and FirstSeedRow/SecondSeedRow per cross-only node, and how many of each parent the
        // whole route needs, before any text is generated — the obtain and multiply copy both depend
        // on totals that are only known once every cross step in the route has been sized.
        var attemptsByTarget = new Dictionary<uint, int>();
        var firstByTarget = new Dictionary<uint, uint>();
        var secondByTarget = new Dictionary<uint, uint>();
        var needed = new Dictionary<uint, int>();

        foreach (var target in order)
        {
            var (a, b) = winner[target];
            var outcomeCount = SeedTable.TargetsFor(a, b).Count;
            var attempts = CrossOdds.BedsForNineInTen(ResolveChance(outcomeCount, family, AssumedSoilGrade));
            attemptsByTarget[target] = attempts;

            var heldA = held.GetValueOrDefault(a);
            var heldB = held.GetValueOrDefault(b);
            uint first, second;
            if (heldA != heldB)
                (first, second) = heldA < heldB ? (a, b) : (b, a);
            else
                (first, second) = cost[a] >= cost[b] ? (a, b) : (b, a); // tie: protect the costlier parent as the anchor
            firstByTarget[target] = first;
            secondByTarget[target] = second;

            needed[first] = needed.GetValueOrDefault(first) + attempts;
            needed[second] = needed.GetValueOrDefault(second) + attempts;
        }

        // A multiply step is needed wherever a cross-only node the route itself produced is then
        // consumed by a later step in more copies than a single harvest of it returns at the lowest
        // soil tier — the BedsForNineInTen sizing above only promises one successful harvest, never more.
        var multiplyRows = new HashSet<uint>();
        foreach (var target in order)
        {
            if (!needed.TryGetValue(target, out var neededCount))
                continue;
            var lowestTierYield = SeedTable.Yields(target)?.Seed is { Length: > 0 } y ? y[0] : 0;
            if (neededCount > lowestTierYield)
                multiplyRows.Add(target);
        }

        var warnings = new List<string>();
        AddDataGapWarnings(warnings, leafOrder.Concat(order));

        var steps = new List<GoalStep>();
        var explainedFamilies = new HashSet<SoilFamily>();

        AddObtainSteps(steps, leafOrder, needed, held);

        var stepNumber = 2;
        foreach (var target in order)
        {
            var first = firstByTarget[target];
            var second = secondByTarget[target];
            var attempts = attemptsByTarget[target];
            var beds = attempts * 2;
            var outcomes = SeedTable.TargetsFor(first, second).ToArray();
            var isFinal = target == goalRow;

            steps.Add(BuildCrossStep(
                stepNumber++, first, second, target, outcomes, beds, crossSoil, family, isFinal, explainedFamilies));

            if (multiplyRows.Contains(target))
                steps.Add(BuildMultiplyStep(stepNumber++, target, MultiplySoil, explainedFamilies));
        }

        var bestCaseHours = CriticalPathHours(goalRow, winner) +
            steps.OfType<MultiplyStep>().Sum(m => SeedTable.Grow(m.SeedRow) ?? 0);

        var peakBeds = steps.Select(s => s switch
        {
            CrossStep c => c.Beds,
            MultiplyStep m => m.Beds,
            _ => 0,
        }).DefaultIfEmpty(0).Max();

        return new GoalPlan(goalRow, steps.ToArray(), TimeSpan.FromHours(bestCaseHours), peakBeds, warnings.ToArray());
    }

    /// <summary>
    /// Relaxes <c>cost(seed) = min over pairs (a,b) -> seed of cost(a) + cost(b) + expectedBeds(pair) *
    /// growHours(seed)</c> to a fixpoint, starting every held or gatherable seed at its base cost.
    /// Bounded at <see cref="Game.SeedTable.CrossableTargets"/>'s own count (81 seeds), since a pass
    /// that improves nothing means every reachable cost has already settled.
    /// </summary>
    private static (Dictionary<uint, double> Cost, Dictionary<uint, (uint A, uint B)> Winner) Relax(
        IReadOnlyDictionary<uint, int> held, SoilFamily family)
    {
        var cost = new Dictionary<uint, double>();
        var winner = new Dictionary<uint, (uint A, uint B)>();

        foreach (var row in SeedTable.GatherableRows)
            cost[row] = GatherCostHours;
        foreach (var (row, count) in held)
            if (count > 0)
                cost[row] = 0; // held always beats a gather trip

        var targets = SeedTable.CrossableTargets;
        for (var iteration = 0; iteration < targets.Count; iteration++)
        {
            var changed = false;
            foreach (var target in targets)
            {
                if (SeedTable.Grow(target) is not { } growHours)
                    continue; // no bundled grow time: this target can never be costed by a cross

                foreach (var (a, b) in SeedTable.Pairs(target))
                {
                    if (!cost.TryGetValue(a, out var costA) || !cost.TryGetValue(b, out var costB))
                        continue;

                    var outcomeCount = SeedTable.TargetsFor(a, b).Count;
                    var p = ResolveChance(outcomeCount, family, AssumedSoilGrade);
                    var expectedBeds = p > 0 ? 1.0 / p : double.PositiveInfinity;
                    var candidate = costA + costB + expectedBeds * growHours;

                    if (cost.TryGetValue(target, out var existing) && !(candidate < existing))
                        continue;

                    cost[target] = candidate;
                    winner[target] = (a, b);
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        return (cost, winner);
    }

    /// <summary>
    /// The chance one planting lands on one particular outcome of a pair with
    /// <paramref name="outcomeCount"/> possible offspring, sized purely from the soil's
    /// community-estimated intercross rate split evenly across those outcomes.
    /// <see cref="Game.SeedTable.EfficiencyFor"/> is not usable here: it is a per-pair rating with an
    /// unpublished definition, not a measured chance, so it ranks candidate pairs against each other
    /// elsewhere in this file but never sizes a step.
    /// </summary>
    private static double ResolveChance(int outcomeCount, SoilFamily family, int grade) =>
        CrossOdds.Chance(outcomeCount, family, grade);

    /// <summary>Longest path in growHours from the goal back to its leaves, counting sibling branches
    /// once (the max, not the sum) since independent crosses run in parallel beds — the number the
    /// header's "About N days if the gamble lands first time" quotes.</summary>
    private static double CriticalPathHours(uint row, IReadOnlyDictionary<uint, (uint A, uint B)> winner)
    {
        if (!winner.TryGetValue(row, out var parents))
            return 0; // a leaf: obtaining it is not grow-day time

        var growHours = SeedTable.Grow(row) ?? 0;
        return Math.Max(CriticalPathHours(parents.A, winner), CriticalPathHours(parents.B, winner)) + growHours;
    }

    private static void AddDataGapWarnings(List<string> warnings, IEnumerable<uint> rows)
    {
        var gaps = SeedTable.DataGaps;
        foreach (var row in rows.Distinct())
        {
            if (gaps.RowsWithNoGrowTime.Any(g => g.Row == row))
                warnings.Add($"{SeedItems.ProduceName(row)} has no bundled grow time; its timing in this plan is a guess.");
            if (gaps.RowsAbsentFromCrossData.Any(g => g.Row == row))
                warnings.Add($"{SeedItems.ProduceName(row)} is missing from the cross data; this route may be incomplete.");
        }
    }

    private static void AddObtainSteps(
        List<GoalStep> steps, IReadOnlyList<uint> leaves, IReadOnlyDictionary<uint, int> needed,
        IReadOnlyDictionary<uint, int> held)
    {
        const string title = "Get the starting seeds";

        foreach (var row in leaves)
        {
            var needCount = Math.Max(1, needed.GetValueOrDefault(row));
            var heldCount = held.GetValueOrDefault(row);
            var seedItemName = SeedItemName(row);
            var produceName = SeedItems.ProduceName(row);

            var body = new List<string>
            {
                heldCount >= needCount
                    ? $"Get {needCount} {seedItemName}. You hold {heldCount}, so this one is covered."
                    : $"Get {needCount} {seedItemName}. You hold {heldCount}.",
            };

            var sources = SeedTable.Sources(row);
            if (sources.Count > 0)
                body.Add($"Where: {string.Join("; ", sources)}");

            var notes = new List<string>();
            if (SeedTable.Yields(row)?.Seed is { Length: > 0 } seedYield && seedYield.All(y => y == 0))
                notes.Add($"{produceName} gives no seeds back when you harvest it, so buy one for every attempt.");

            steps.Add(new ObtainStep(1, title, body.ToArray(), notes.ToArray(), row, needCount));
        }
    }

    private static CrossStep BuildCrossStep(
        int number, uint first, uint second, uint target, uint[] outcomes, int beds, SoilPreference soilPreference,
        SoilFamily family, bool isFinal, HashSet<SoilFamily> explainedFamilies)
    {
        var targetName = SeedItems.ProduceName(target);
        var body = new List<string>
        {
            $"Plant {SeedItemName(first)} in one bed. Then plant {SeedItemName(second)} in the bed next to it.",
        };

        var singleOutcome = outcomes.Length <= 1;
        var attempts = beds / 2;
        var targetGrowHours = SeedTable.Grow(target) ?? 0;

        if (singleOutcome)
        {
            body.Add($"The second bed becomes {targetName}. This pair makes nothing else.");
        }
        else
        {
            var others = outcomes.Where(o => o != target).Select(SeedItems.ProduceName);
            var chance = ResolveChance(outcomes.Length, family, AssumedSoilGrade);
            body.Add(
                $"The second bed becomes {targetName} or {string.Join(" or ", others)}, and you cannot pick which. " +
                $"{Capitalize(CrossOdds.OddsPhrase(chance))} of these beds give {targetName}.");
            var roundDays = DurationDays(targetGrowHours);
            body.Add(
                $"Plant {attempts} pairs if you have the beds. About 9 rounds in 10 then give you at least one {targetName}.");
            body.Add($"Fewer pairs is fine, it just means more rounds of {roundDays} days each.");
            body.Add("Nobody has published the split between the two, so Gardener treats it as a coin toss.");
        }

        var explain = explainedFamilies.Add(family) ? $" {family} soil {SoilSources.Does(family)}." : "";
        body.Add(singleOutcome
            ? $"Soil: Grade {AssumedSoilGrade} {family} Topsoil in both beds.{explain}"
            : $"Soil: Grade {AssumedSoilGrade} {family} Topsoil in every bed.{explain}");

        body.Add($"Beds: {(singleOutcome ? beds.ToString() : $"up to {beds}")}. Ready {DurationPhrase(targetGrowHours)} after you plant.");

        if (singleOutcome)
        {
            if (isFinal)
            {
                AppendFinalWiltAndHarvest(body, target);
            }
            else
            {
                var wiltHours = MinWiltHours(first, target);
                if (wiltHours is { } w)
                    body.Add($"Tend both beds {EveryDayPhrase(w)} or they wilt.");
                AppendYieldNote(body, target, targetName);
            }
        }
        else if (isFinal)
        {
            AppendFinalWiltAndHarvest(body, target);
        }

        var notes = new List<string>();
        if (SeedTable.EfficiencyFor(first, second, target) is { } rating)
        {
            notes.Add(
                $"ffxivgardening.com rates this pair {rating} out of 100. That rating is per pair, not per " +
                "result, and its meaning isn't published, so Gardener only uses it to prefer one pair over " +
                "another and sizes this step from the soil estimate instead.");
        }
        if (SeedTable.Wilt(target)?.Disputed == true)
            notes.Add($"{targetName}'s wilt time is disputed between sources; the cadence above is the shorter, safer figure.");

        return new CrossStep(number, $"Grow {targetName}", body.ToArray(), notes.ToArray(),
            first, second, target, outcomes, beds, soilPreference);
    }

    private static void AppendFinalWiltAndHarvest(List<string> body, uint target)
    {
        var targetName = SeedItems.ProduceName(target);
        if (SeedTable.Wilt(target) is { } wilt)
        {
            var growHours = SeedTable.Grow(target) ?? 0;
            var visits = wilt.Hours > 0 ? (int)Math.Ceiling(growHours / (double)wilt.Hours) : 1;
            body.Add(
                $"{targetName} wilts after {DayPhrase(wilt.Hours)}, so visit it {EveryDayPhrase(wilt.Hours)} " +
                $"until you harvest it. That is {visits} visits.");
        }

        var yields = SeedTable.Yields(target);
        var cropText = OneNumberOrRange(yields?.Crop);
        var seedText = OneNumberOrRange(yields?.Seed);
        var allAgree = yields?.Crop is { } cy && cy.Length > 0 && cy.All(v => v == cy[0]) &&
                        yields?.Seed is { } sy && sy.Length > 0 && sy.All(v => v == sy[0]);
        body.Add(allAgree
            ? $"Harvest gives {cropText} {targetName} and {seedText} seed back at any soil grade."
            : $"Harvest gives {cropText} {targetName} and {seedText} seed back, depending on your soil.");
    }

    private static void AppendYieldNote(List<string> body, uint target, string targetName)
    {
        var seedYield = SeedTable.Yields(target)?.Seed;
        if (seedYield is not { Length: > 0 })
            return;

        if (seedYield.All(y => y == 0))
        {
            body.Add($"{targetName} gives no seeds back when you harvest it, so buy or grow another for every attempt.");
            return;
        }

        var text = OneNumberOrRange(seedYield);
        body.Add($"{targetName} gives back {text} seeds depending on your soil, so one harvest funds the rest.");
    }

    private static MultiplyStep BuildMultiplyStep(int number, uint row, SoilPreference soil, HashSet<SoilFamily> explainedFamilies)
    {
        var produceName = SeedItems.ProduceName(row);
        var family = SoilFamily.Shroud;
        var explain = explainedFamilies.Add(family) ? $" {family} soil {SoilSources.Does(family)}." : "";
        var yieldText = OneNumberOrRange(SeedTable.Yields(row)?.Seed);
        var growHours = SeedTable.Grow(row) ?? 0;

        var body = new[]
        {
            $"Plant 1 {SeedItemName(row)} in a bed with nothing beside it, in Grade {AssumedSoilGrade} {family} Topsoil.{explain}",
            $"Beds: 1. Ready in {DurationPhrase(growHours)} for {yieldText} seeds.",
        };

        return new MultiplyStep(number, $"Grow more {produceName}", body, Array.Empty<string>(), row, 1, soil);
    }

    private static int? MinWiltHours(uint anchorRow, uint targetRow)
    {
        int? a = SeedTable.Wilt(anchorRow)?.Hours;
        int? b = SeedTable.Wilt(targetRow)?.Hours;
        if (a is null) return b;
        if (b is null) return a;
        return Math.Min(a.Value, b.Value);
    }

    private static string SeedItemName(uint row) =>
        SeedItems.SeedItemForRow(row) is { } itemId ? XivHubPluginKit.Inventory.ItemSheet.Name(itemId) : $"row {row}'s seed";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int DurationDays(int hours) => Math.Max(1, (int)Math.Round(hours / 24.0));

    /// <summary>"about N days" above 48 hours, "about N hours" below — never a countdown to the
    /// minute, matching how <see cref="Journal.Growth"/> speaks about a harvest window.</summary>
    private static string DurationPhrase(double hours) =>
        hours > 48 ? $"about {Math.Round(hours / 24.0):F0} days" : $"about {Math.Round(hours):F0} hours";

    /// <summary>Wilt cycles are always day-scale (24, 36 or 48 hours), so the tend cadence is always
    /// spoken in days regardless of <see cref="DurationPhrase"/>'s 48-hour threshold: "1 day", "1.5
    /// days", "2 days".</summary>
    private static string DayPhrase(int hours)
    {
        var days = Math.Round(hours / 24.0, 1);
        var number = days % 1 == 0 ? ((int)days).ToString() : days.ToString("0.#");
        return days == 1 ? $"{number} day" : $"{number} days";
    }

    /// <summary>"every day" reads better than "every 1 day"; every other cadence keeps the count.</summary>
    private static string EveryDayPhrase(int hours) =>
        Math.Round(hours / 24.0, 1) == 1 ? "every day" : $"every {DayPhrase(hours)}";

    /// <summary>Rule 1 of the Goal tab's generated copy: a single number only when every soil tier
    /// agrees, a range otherwise — the yield-tier meaning itself is unrecorded (G2), so quoting one
    /// tier by guesswork would claim precision nobody has.</summary>
    private static string OneNumberOrRange(int[]? tiers)
    {
        if (tiers is not { Length: > 0 })
            return "an unknown number of";
        return tiers.All(t => t == tiers[0]) ? tiers[0].ToString() : $"{tiers.Min()} to {tiers.Max()}";
    }

    private static SoilFamily FamilyFor(SoilPreference preference) => preference switch
    {
        SoilPreference.HighestThanalan => SoilFamily.Thanalan,
        SoilPreference.HighestShroud => SoilFamily.Shroud,
        SoilPreference.HighestLaNoscean => SoilFamily.LaNoscean,
        // Fixed pins one item, not a family; the route search still needs a family to cost intercross
        // odds against, so it assumes the one that actually drives them regardless of what is pinned.
        SoilPreference.Fixed => SoilFamily.Thanalan,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "unhandled SoilPreference"),
    };
}
