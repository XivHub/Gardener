using System;

namespace Gardener.Planner;

/// <summary>
/// One step of a <see cref="GoalPlan"/>. <see cref="Number"/> groups steps for display — every
/// <see cref="ObtainStep"/> in a plan shares <c>Number == 1</c>, since the whole route's shopping list
/// is one step, not one per seed — and <see cref="Body"/> / <see cref="Notes"/> are the exact strings
/// the Goal tab renders, generated once by <see cref="GoalRoute.Solve"/> rather than re-derived at
/// draw time.
/// </summary>
public abstract record GoalStep(int Number, string Title, string[] Body, string[] Notes);

/// <summary>One seed to buy or gather before any crossing step can start. Every obtain in a route is a
/// leaf — held already or reachable through <see cref="Game.SeedTable.Sources"/> — so all of them merge
/// into the route's single opening step.</summary>
public sealed record ObtainStep(int Number, string Title, string[] Body, string[] Notes, uint SeedRow, int Needed)
    : GoalStep(Number, Title, Body, Notes);

/// <summary><see cref="FirstSeedRow"/> is the anchor: planted first, and the bed it occupies stays
/// what it is (G1). <see cref="SecondSeedRow"/> is planted beside it and its bed becomes the cross,
/// landing on one of <see cref="AllOutcomes"/> with <see cref="TargetRow"/> the one this route wants.
/// <see cref="Beds"/> is sized by <see cref="CrossOdds.BedsForNineInTen"/> and is always an even number
/// of physical beds (one anchor, one cross, per attempt).</summary>
public sealed record CrossStep(
    int Number, string Title, string[] Body, string[] Notes,
    uint FirstSeedRow, uint SecondSeedRow, uint TargetRow, uint[] AllOutcomes, int Beds, SoilPreference Soil)
    : GoalStep(Number, Title, Body, Notes);

/// <summary>Grows more copies of a seed the route already reached by crossing, planted alone so
/// nothing else can cross with it, in the yield-boosting soil rather than the intercross one. Emitted
/// only when a later step needs more of <see cref="SeedRow"/> than one harvest of it returns at the
/// lowest soil tier.</summary>
public sealed record MultiplyStep(int Number, string Title, string[] Body, string[] Notes, uint SeedRow, int Beds, SoilPreference Soil)
    : GoalStep(Number, Title, Body, Notes);

/// <summary>
/// The route <see cref="GoalRoute.Solve"/> found for one goal seed: an ordered set of steps, the sum of
/// <see cref="Game.SeedTable.Grow"/> hours along the route's critical path (parallel branches counted
/// once, not summed), the most beds any single step occupies, and any data-gap warnings collected along
/// the way.
/// </summary>
public sealed record GoalPlan(uint GoalRow, GoalStep[] Steps, TimeSpan BestCaseDuration, int PeakBeds, string[] Warnings);
