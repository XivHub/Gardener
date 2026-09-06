using System;
using System.Collections.Generic;
using System.Linq;
using Gardener.Game;
using Gardener.Localization;

namespace Gardener.Planner;

/// <summary>
/// Finds the cheapest route to a target seed and turns it into the ordered, worded steps the Goal tab
/// renders. "Cheapest" means fewest expected bed-hours, not fewest crosses: two routes to the same seed
/// can both be two generations deep and differ by a third in bed time, which a shortest-chain search
/// cannot tell apart. Pure over its four arguments, including <see cref="GardenCapacity"/> — no live
/// scan of the garden and no config read; display language only (<see cref="Localization.Loc.Culture"/>)
/// — so it is exercised without the game. Capacity arrives already resolved by the caller; nothing in
/// this file reads the player's real patches, the journal, or the plugin's live configuration.
/// </summary>
public static class GoalRoute
{
    /// <summary>Who draws on a cross-only node's output, driving which whole sentence
    /// <see cref="ClassifyConsumer"/> selects for the arithmetic paragraph's demand sentence: the verb
    /// agrees with the subject in Spanish ("el cruce final necesita" vs "los pasos posteriores
    /// necesitan"), so the subject is picked here in C#, never substituted into a shared template.</summary>
    private enum ConsumerKind
    {
        FinalCross,
        NamedStep,
        LaterSteps,
    }

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

    public static GoalPlan? Solve(
        uint goalRow, IReadOnlyDictionary<uint, int> held, SoilPreference crossSoil, SoilPreference yieldSoil,
        GardenCapacity capacity)
    {
        if (SeedTable.Gatherable(goalRow) == true)
            return null; // nothing to plan: the tab shows "you do not need to crossbreed this" instead

        var family = FamilyFor(crossSoil);
        var yieldFamily = FamilyFor(yieldSoil);
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
            roundsNeeded[target] = Math.Max(1, (int)Math.Ceiling(totalAttempts / (double)capacity.Pairs));

            foreach (var parent in new[] { first, second })
            {
                demand[parent] = demand.GetValueOrDefault(parent) + totalAttempts;

                // A full patch is planted every round regardless of how many attempts this step
                // strictly needs — beds cost nothing extra and a later cross always has use for the
                // surplus. A parent whose own harvest gives seed back only needs enough to start the
                // first round; one that gives nothing back is a fresh purchase every round.
                var parentYield = SeedYieldAtAssumedGrade(parent);
                var contribution = parentYield is > 0 ? capacity.Pairs : capacity.Pairs * roundsNeeded[target];
                obtainNeed[parent] = obtainNeed.GetValueOrDefault(parent) + contribution;
            }
        }

        var warnings = new List<string>();
        AddDataGapWarnings(warnings, leafOrder.Concat(order));
        if (capacity.UnusablePatchCount > 0)
            warnings.Add(Loc.Format(Strings.Goal_CapacityIgnoresUnconfirmed, Formats.Number(capacity.UnusablePatchCount)));

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
            var (consumerKind, namedConsumer) = ClassifyConsumer(target, goalRow, consumersOf);

            steps.Add(BuildCrossStep(
                stepNumber++, first, second, target, outcomes, crossSoil, family, yieldFamily, isFinal,
                attemptsNeeded[target], roundsNeeded[target], ownDemand, SeedYieldAtAssumedGrade(target),
                consumerKind, namedConsumer, explainedFamilies, capacity));
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
                warnings.Add(Loc.Format(Strings.Goal_WarningNoGrowTime, SeedItems.ProduceName(row)));
            if (gaps.RowsAbsentFromCrossData.Any(g => g.Row == row))
                warnings.Add(Loc.Format(Strings.Goal_WarningMissingCrossData, SeedItems.ProduceName(row)));
        }
    }

    private static void AddObtainSteps(
        List<GoalStep> steps, IReadOnlyList<uint> leaves, IReadOnlyDictionary<uint, int> obtainNeed,
        IReadOnlyDictionary<uint, int> held)
    {
        foreach (var row in leaves)
        {
            var needCount = Math.Max(1, obtainNeed.GetValueOrDefault(row));
            var heldCount = held.GetValueOrDefault(row);
            var seedItemName = SeedItems.SeedItemName(row);
            var produceName = SeedItems.ProduceName(row);

            var body = new List<string>
            {
                Loc.Format(Strings.Goal_ObtainGet, Formats.Number(needCount), seedItemName),
                heldCount >= needCount
                    ? Loc.Format(Strings.Goal_ObtainHeldCovered, Formats.Number(heldCount))
                    : Loc.Format(Strings.Goal_ObtainHeldShort, Formats.Number(heldCount)),
            };

            var sources = SeedTable.Sources(row);
            if (sources.Count > 0)
                body.Add(Loc.Format(Strings.Goal_ObtainWhere, string.Join("; ", sources)));

            var notes = new List<string>
            {
                SeedYieldAtAssumedGrade(row) switch
                {
                    null => Loc.Format(Strings.Goal_ObtainYieldUnrecorded, produceName),
                    0 => Loc.Format(Strings.Goal_ObtainYieldZero, produceName),
                    _ => Loc.Format(Strings.Goal_ObtainYieldRestocks, produceName),
                },
            };

            steps.Add(new ObtainStep(1, Strings.Goal_ObtainTitle, body.ToArray(), notes.ToArray(), row, needCount));
        }
    }

    private static CrossStep BuildCrossStep(
        int number, uint first, uint second, uint target, uint[] outcomes, SoilPreference soilPreference,
        SoilFamily family, SoilFamily yieldFamily, bool isFinal, int attempts, int rounds, int ownDemand, int? yieldAtGrade,
        ConsumerKind consumerKind, uint? namedConsumer, HashSet<SoilFamily> explainedFamilies, GardenCapacity capacity)
    {
        var targetName = SeedItems.ProduceName(target);
        var body = new List<string>
        {
            Loc.Format(Strings.Goal_CrossPlantLayout, SeedItems.SeedItemName(first), SeedItems.SeedItemName(second)),
        };

        var singleOutcome = outcomes.Length <= 1;
        var targetGrowHours = SeedTable.Grow(target) ?? 0;
        var roundDays = Math.Max(1, (int)Math.Round(targetGrowHours / 24.0));

        if (singleOutcome)
        {
            body.Add(Loc.Format(Strings.Goal_CrossSingleOutcome, targetName));
            body.Add(Strings.Goal_CrossSingleOutcomeOnly);
        }
        else
        {
            var outcomeNames = new List<string> { targetName };
            outcomeNames.AddRange(outcomes.Where(o => o != target).Select(SeedItems.ProduceName));
            var chance = ResolveChance(outcomes.Length, family, AssumedSoilGrade);
            body.Add(Loc.Format(Strings.Goal_CrossMultiOutcome, TextList.Or(outcomeNames)));
            body.Add(Loc.Format(Strings.Goal_OddsOfThemGive, CrossOdds.OddsPhrase(chance), targetName));
            body.Add(Strings.Goal_CrossCoinToss);
        }

        // The arithmetic behind the bed count: a full patch's worth of capacity.Pairs pairs plants a
        // round, and either that covers what the rest of the route draws from this seed or it has to
        // repeat.
        if (isFinal)
        {
            if (rounds > 1)
            {
                body.Add(Loc.Format(Strings.Goal_CrossFinalRoundsPerRound, Formats.Number(capacity.Pairs)));
                body.Add(Loc.Format(Strings.Goal_CrossFinalRoundsPlan,
                    Formats.Number(attempts), Formats.Number(rounds), Formats.Number(roundDays * rounds)));
            }
        }
        else if (yieldAtGrade is { } y)
        {
            body.Add(DemandSentence(consumerKind, namedConsumer, ownDemand, targetName));
            body.Add(YieldRoundsSentence(rounds, y, targetName, roundDays, capacity.Beds));
        }
        else
        {
            body.Add(DemandSentence(consumerKind, namedConsumer, ownDemand, targetName));
            body.Add(Loc.Format(Strings.Goal_YieldUnknown, targetName));
        }

        // Two soils, not one: a bed planted while its ring neighbours are still empty crosses with
        // nothing, so it is harvested for its own seed and takes the yield soil, and only the beds
        // planted against it take the soil that moves the intercross rate.
        var crossSoilName = GardeningItems.SoilName(family, AssumedSoilGrade);
        var explainCross = explainedFamilies.Add(family);
        if (yieldFamily == family)
        {
            body.Add(Loc.Format(Strings.Goal_SoilSameFamily, crossSoilName));
            if (explainCross)
                body.Add(SoilSources.Does(family, crossSoilName));
        }
        else
        {
            var yieldSoilName = GardeningItems.SoilName(yieldFamily, AssumedSoilGrade);
            var explainYield = explainedFamilies.Add(yieldFamily);
            body.Add(Loc.Format(Strings.Goal_SoilTwoFamilies, crossSoilName, yieldSoilName));
            if (explainCross)
                body.Add(SoilSources.Does(family, crossSoilName));
            if (explainYield)
                body.Add(SoilSources.Does(yieldFamily, yieldSoilName));
        }

        body.Add(capacity.Assumed
            ? Loc.Format(Strings.Goal_BedsCountAssumed, Formats.Number(capacity.Beds), Formats.Number(capacity.Pairs))
            : capacity.PatchCount == 1
                ? Loc.Format(Strings.Goal_BedsCountOnePatch, Formats.Number(capacity.Beds), Formats.Number(capacity.Pairs))
                : Loc.Format(Strings.Goal_BedsCountManyPatches,
                    Formats.Number(capacity.Beds), Formats.Number(capacity.PatchCount), Formats.Number(capacity.Pairs)));
        body.Add(Loc.Format(Strings.Goal_ReadyAfterPlant,
            targetGrowHours > 48 ? Phrases.AboutDays(Math.Round(targetGrowHours / 24.0)) : Phrases.AboutHours(Math.Round((double)targetGrowHours))));

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
                    body.Add(Loc.Format(Strings.Goal_TendOrWilt, Phrases.EveryDay(w)));
            }
        }
        else if (isFinal)
        {
            AppendFinalWiltAndHarvest(body, target);
        }

        var notes = new List<string>();
        if (SeedTable.EfficiencyFor(first, second, target) is { } rating)
        {
            notes.Add(Loc.Format(Strings.Goal_NoteRating, Formats.Number(rating)));
            notes.Add(Strings.Goal_NoteRatingMeaning);
        }
        if (SeedTable.Wilt(target)?.Disputed == true)
            notes.Add(Loc.Format(Strings.Goal_NoteWiltDisputed, targetName));

        return new CrossStep(number, Loc.Format(Strings.Goal_CrossStepTitle, targetName), body.ToArray(), notes.ToArray(),
            first, second, target, outcomes, capacity.Beds, soilPreference, ownDemand);
    }

    /// <summary>Sentence one of the arithmetic paragraph: who wants <paramref name="ownDemand"/> of
    /// <paramref name="targetName"/>. Selected by <see cref="ConsumerKind"/> rather than substituted,
    /// because "the final cross needs" and "later steps need" inflect their verb differently in
    /// Spanish and the subject can never be a slot (composition contract rule 3).</summary>
    private static string DemandSentence(ConsumerKind kind, uint? namedConsumer, int ownDemand, string targetName)
    {
        var demand = Formats.Number(ownDemand);
        return kind switch
        {
            ConsumerKind.FinalCross => Loc.Format(Strings.Goal_DemandFinalCross, demand, targetName),
            ConsumerKind.NamedStep => Loc.Format(Strings.Goal_DemandNamedStep, SeedItems.ProduceName(namedConsumer!.Value), demand, targetName),
            ConsumerKind.LaterSteps => Loc.Format(Strings.Goal_DemandLaterSteps, demand, targetName),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unhandled ConsumerKind"),
        };
    }

    /// <summary>Sentence two of the arithmetic paragraph: what a bed of <paramref name="targetName"/>
    /// gives back and whether one round of the patch covers <paramref name="rounds"/> worth of
    /// attempts. Crossed on rounds &lt;= 1 and seed-yield plural (<paramref name="y"/> == 1), four
    /// keys total, each phrased "a bed of {0}" so no gendered article precedes the item name
    /// (composition contract rule 6).</summary>
    private static string YieldRoundsSentence(int rounds, int y, string targetName, int roundDays, int beds)
    {
        if (rounds <= 1)
            return y == 1
                ? Loc.Format(Strings.Goal_YieldCoversOne_One, targetName, Formats.Number(beds))
                : Loc.Format(Strings.Goal_YieldCoversOne_Other, targetName, Formats.Number(y), Formats.Number(beds));

        return y == 1
            ? Loc.Format(Strings.Goal_YieldNeedsRounds_One, targetName, Formats.Number(rounds),
                Formats.Number(beds), Formats.Number(roundDays * rounds))
            : Loc.Format(Strings.Goal_YieldNeedsRounds_Other, targetName, Formats.Number(y), Formats.Number(rounds),
                Formats.Number(beds), Formats.Number(roundDays * rounds));
    }

    private static void AppendFinalWiltAndHarvest(List<string> body, uint target)
    {
        var targetName = SeedItems.ProduceName(target);
        if (SeedTable.Wilt(target) is { } wilt)
        {
            var growHours = SeedTable.Grow(target) ?? 0;
            var visits = wilt.Hours > 0 ? (int)Math.Ceiling(growHours / (double)wilt.Hours) : 1;
            body.Add(Loc.Format(Strings.Goal_WiltCadence, targetName,
                Phrases.Days(Math.Round(wilt.Hours / 24.0, 1)), Phrases.EveryDay(wilt.Hours)));
            body.Add(visits == 1
                ? Loc.Format(Strings.Goal_WiltVisitCount_One, Formats.Number(visits))
                : Loc.Format(Strings.Goal_WiltVisitCount_Other, Formats.Number(visits)));
        }

        var yields = SeedTable.Yields(target);
        var cropText = Phrases.OneNumberOrRange(yields?.Crop);
        var seedText = Phrases.OneNumberOrRange(yields?.Seed);
        var allAgree = yields?.Crop is { } cy && cy.Length > 0 && cy.All(v => v == cy[0]) &&
                        yields?.Seed is { } sy && sy.Length > 0 && sy.All(v => v == sy[0]);
        body.Add(allAgree
            ? Loc.Format(Strings.Goal_HarvestYieldFixed, cropText, targetName, seedText)
            : Loc.Format(Strings.Goal_HarvestYieldVaries, cropText, targetName, seedText));
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

    /// <summary>Who draws on <paramref name="target"/>'s output, for the arithmetic sentence: the final
    /// cross when the goal is its only consumer, the step that grows a named later seed (returned
    /// through <paramref name="namedConsumer"/>'s out value), or the generic plural when more than one
    /// step reaches back to the same row. A <see cref="ConsumerKind"/> rather than a phrase, since the
    /// Spanish verb agrees with which of these the subject is (composition contract rule 3).</summary>
    private static (ConsumerKind Kind, uint? NamedConsumer) ClassifyConsumer(
        uint target, uint goalRow, IReadOnlyDictionary<uint, List<uint>> consumersOf)
    {
        if (!consumersOf.TryGetValue(target, out var consumers) || consumers.Count != 1)
            return (ConsumerKind.LaterSteps, null);
        return consumers[0] == goalRow
            ? (ConsumerKind.FinalCross, null)
            : (ConsumerKind.NamedStep, consumers[0]);
    }

    private static int? MinWiltHours(uint anchorRow, uint targetRow)
    {
        int? a = SeedTable.Wilt(anchorRow)?.Hours;
        int? b = SeedTable.Wilt(targetRow)?.Hours;
        if (a is null) return b;
        if (b is null) return a;
        return Math.Min(a.Value, b.Value);
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
