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

    // Deluxe is the only patch shape with a confirmed bed layout (see FACTS.md), so every crossing
    // step in a route is sized against its capacity: PlanFillStep already refuses to lay a fill out on
    // an Oblong or Round patch, and the handoff bounds this down to whatever the real patch and the
    // player's real seed count allow.
    private static readonly int AssumedPatchBeds = PatchKind.Deluxe.BedCount();
    private static readonly int AssumedPatchPairs = AssumedPatchBeds / 2;

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

        // Which of each winning pair is the anchor (stays itself) and which is the cross (consumed into
        // the target), and who draws on each row as a parent — both fixed before any demand math, since
        // neither depends on how many beds anything ends up wanting.
        var firstByTarget = new Dictionary<uint, uint>();
        var secondByTarget = new Dictionary<uint, uint>();
        var consumersOf = new Dictionary<uint, List<uint>>();

        void AddConsumer(uint parent, uint consumer)
        {
            if (!consumersOf.TryGetValue(parent, out var list))
                consumersOf[parent] = list = new List<uint>();
            list.Add(consumer);
        }

        foreach (var target in order)
        {
            var (a, b) = winner[target];
            var heldA = held.GetValueOrDefault(a);
            var heldB = held.GetValueOrDefault(b);
            uint first, second;
            if (heldA != heldB)
                (first, second) = heldA < heldB ? (a, b) : (b, a);
            else
                (first, second) = cost[a] >= cost[b] ? (a, b) : (b, a); // tie: protect the costlier parent as the anchor
            firstByTarget[target] = first;
            secondByTarget[target] = second;
            AddConsumer(first, target);
            AddConsumer(second, target);
        }

        // Sized back from the goal: the final step needs enough attempts for its own gamble, and every
        // attempt of it consumes one of each parent, so a target that is itself a cross-only node must
        // produce that many — the larger of its own gamble and what every later step draws from it.
        // Reversed because a target's demand depends on the attempts of the children the forward walk
        // already put after it in `order`.
        var demand = new Dictionary<uint, int>();
        var attemptsNeeded = new Dictionary<uint, int>();
        var roundsNeeded = new Dictionary<uint, int>();
        var obtainNeed = new Dictionary<uint, int>();

        for (var i = order.Count - 1; i >= 0; i--)
        {
            var target = order[i];
            var first = firstByTarget[target];
            var second = secondByTarget[target];
            var isFinal = target == goalRow;

            var outcomeCount = SeedTable.TargetsFor(first, second).Count;
            var oddsAttempts = CrossOdds.BedsForNineInTen(ResolveChance(outcomeCount, family, AssumedSoilGrade));

            // The goal itself is wanted once, so its own gamble already covers it; a cross-only node
            // upstream of it is wanted however many times its consumers draw on it, converted to
            // attempts through its own seed yield — a seed whose yield isn't bundled cannot be sized
            // this way, so its step falls back to the gamble alone and says so.
            var ownDemand = isFinal ? 1 : demand.GetValueOrDefault(target);
            var totalAttempts = oddsAttempts;
            if (!isFinal && SeedYieldAtAssumedGrade(target) is { } targetYield)
            {
                var demandAttempts = (int)Math.Ceiling(ownDemand / (double)Math.Max(targetYield, 1));
                totalAttempts = Math.Max(oddsAttempts, demandAttempts);
            }

            attemptsNeeded[target] = totalAttempts;
            roundsNeeded[target] = Math.Max(1, (int)Math.Ceiling(totalAttempts / (double)AssumedPatchPairs));

            foreach (var parent in new[] { first, second })
            {
                demand[parent] = demand.GetValueOrDefault(parent) + totalAttempts;

                // A full patch is planted every round regardless of how many attempts this step
                // strictly needs — beds cost nothing extra and a later cross always has use for the
                // surplus. A parent whose own harvest gives seed back only needs enough to start the
                // first round; one that gives nothing back is a fresh purchase every round.
                var parentYield = SeedYieldAtAssumedGrade(parent);
                var contribution = parentYield is > 0 ? AssumedPatchPairs : AssumedPatchPairs * roundsNeeded[target];
                obtainNeed[parent] = obtainNeed.GetValueOrDefault(parent) + contribution;
            }
        }

        var warnings = new List<string>();
        AddDataGapWarnings(warnings, leafOrder.Concat(order));

        var steps = new List<GoalStep>();
        var explainedFamilies = new HashSet<SoilFamily>();

        AddObtainSteps(steps, leafOrder, obtainNeed, held);

        var stepNumber = 2;
        foreach (var target in order)
        {
            var first = firstByTarget[target];
            var second = secondByTarget[target];
            var outcomes = SeedTable.TargetsFor(first, second).ToArray();
            var isFinal = target == goalRow;
            var ownDemand = isFinal ? 1 : demand.GetValueOrDefault(target);
            var consumerPhrase = ConsumerPhrase(target, goalRow, consumersOf);

            steps.Add(BuildCrossStep(
                stepNumber++, first, second, target, outcomes, crossSoil, family, isFinal,
                attemptsNeeded[target], roundsNeeded[target], ownDemand, SeedYieldAtAssumedGrade(target),
                consumerPhrase, explainedFamilies));
        }

        // A step that needs more than one round waits through the extra grow cycles sequentially, on
        // top of the critical path's own first round.
        var bestCaseHours = CriticalPathHours(goalRow, winner) +
            order.Sum(t => (roundsNeeded[t] - 1) * (SeedTable.Grow(t) ?? 0));

        var peakBeds = steps.OfType<CrossStep>().Select(c => c.Beds).DefaultIfEmpty(0).Max();

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
        List<GoalStep> steps, IReadOnlyList<uint> leaves, IReadOnlyDictionary<uint, int> obtainNeed,
        IReadOnlyDictionary<uint, int> held)
    {
        const string title = "Get the starting seeds";

        foreach (var row in leaves)
        {
            var needCount = Math.Max(1, obtainNeed.GetValueOrDefault(row));
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

            var notes = new List<string>
            {
                SeedYieldAtAssumedGrade(row) switch
                {
                    null => $"{produceName}'s seed yield isn't recorded, so Gardener assumes it gives nothing back; every one you plant is a fresh purchase.",
                    0 => $"{produceName} gives no seeds back when you harvest it, so every one you plant is a fresh purchase.",
                    _ => $"{produceName} gives seeds back when you harvest it, so this is enough to start; the first harvest funds the rest.",
                },
            };

            steps.Add(new ObtainStep(1, title, body.ToArray(), notes.ToArray(), row, needCount));
        }
    }

    private static CrossStep BuildCrossStep(
        int number, uint first, uint second, uint target, uint[] outcomes, SoilPreference soilPreference,
        SoilFamily family, bool isFinal, int attempts, int rounds, int ownDemand, int? yieldAtGrade,
        string consumerPhrase, HashSet<SoilFamily> explainedFamilies)
    {
        var targetName = SeedItems.ProduceName(target);
        var body = new List<string>
        {
            $"Plant {SeedItemName(first)} in half the beds around the ring, then plant {SeedItemName(second)} in " +
            $"the beds between them, so every {SeedItemName(second)} has a {SeedItemName(first)} neighbour on both sides.",
        };

        var singleOutcome = outcomes.Length <= 1;
        var targetGrowHours = SeedTable.Grow(target) ?? 0;
        var roundDays = DurationDays(targetGrowHours);

        if (singleOutcome)
        {
            body.Add($"Every one of these beds becomes {targetName}. This pair makes nothing else.");
        }
        else
        {
            var others = outcomes.Where(o => o != target).Select(SeedItems.ProduceName);
            var chance = ResolveChance(outcomes.Length, family, AssumedSoilGrade);
            body.Add(
                $"Each of these beds becomes {targetName} or {string.Join(" or ", others)}, and you cannot pick which. " +
                $"{Capitalize(CrossOdds.OddsPhrase(chance))} of them give {targetName}.");
            body.Add("Nobody has published the split between the two, so Gardener treats it as a coin toss.");
        }

        // The arithmetic behind the bed count: a full patch plants AssumedPatchPairs pairs a round, and
        // either that covers what the rest of the route draws from this seed or it has to repeat.
        if (isFinal)
        {
            if (rounds > 1)
            {
                body.Add(
                    $"One round plants {AssumedPatchPairs} pairs, but this cross wants about {attempts} attempts, " +
                    $"so plan on {rounds} rounds, about {roundDays * rounds} days total.");
            }
        }
        else if (yieldAtGrade is { } y)
        {
            var bedNoun = y == 1 ? "returns 1 seed" : $"returns {y} seeds";
            body.Add(rounds <= 1
                ? $"{consumerPhrase} wants {ownDemand} {targetName}, and a {targetName} bed {bedNoun}, so one round of {AssumedPatchBeds} beds covers it."
                : $"{consumerPhrase} wants {ownDemand} {targetName}, and a {targetName} bed {bedNoun}, so this needs " +
                  $"{rounds} rounds of {AssumedPatchBeds} beds, about {roundDays * rounds} days total.");
        }
        else
        {
            body.Add(
                $"{consumerPhrase} wants {ownDemand} {targetName}, but {targetName}'s seed yield isn't recorded, so " +
                "Gardener could not work out how many rounds that takes; watch your seed count and plant another round if you come up short.");
        }

        var explain = explainedFamilies.Add(family) ? $" {family} soil {SoilSources.Does(family)}." : "";
        body.Add($"Soil: Grade {AssumedSoilGrade} {family} Topsoil in every bed.{explain}");

        body.Add($"Beds: {AssumedPatchBeds} ({AssumedPatchPairs} pairs). Ready {DurationPhrase(targetGrowHours)} after you plant.");

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
                    body.Add($"Tend these beds {EveryDayPhrase(w)} or they wilt.");
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
            first, second, target, outcomes, AssumedPatchBeds, soilPreference, ownDemand);
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

    /// <summary>The number of <paramref name="row"/>'s own seeds one harvest returns at
    /// <see cref="AssumedSoilGrade"/>, the same grade every step in this route plants at. Null when the
    /// bundled yield table has nothing for this row at that grade, which every caller treats as unknown,
    /// never as zero.</summary>
    private static int? SeedYieldAtAssumedGrade(uint row)
    {
        var seedYield = SeedTable.Yields(row)?.Seed;
        return seedYield is { } y && AssumedSoilGrade < y.Length ? y[AssumedSoilGrade] : null;
    }

    /// <summary>Names who draws on <paramref name="target"/>'s output, for the arithmetic sentence: the
    /// final cross when the goal is its only consumer, the step that grows a named later seed, or the
    /// generic plural when more than one step reaches back to the same row.</summary>
    private static string ConsumerPhrase(uint target, uint goalRow, IReadOnlyDictionary<uint, List<uint>> consumersOf)
    {
        if (!consumersOf.TryGetValue(target, out var consumers) || consumers.Count != 1)
            return "Later steps";
        return consumers[0] == goalRow ? "The final cross" : $"Growing {SeedItems.ProduceName(consumers[0])}";
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
