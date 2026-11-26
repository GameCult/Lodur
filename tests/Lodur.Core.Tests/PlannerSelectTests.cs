using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The want gate of Brain.Select and the plan on the Actor: a satisfied want is not offered, a planned pair is scored by its length, an unplanned one is tried direct
/// at a discount, one think budget serves a whole Select, and a Select with no pair lets go of the target.
/// </summary>
public sealed class PlannerSelectTests
{
    private const uint Door = 1, Key = 2, Knife = 3, Junk = 4;
    private const int Open = 0, Locked = 1, Latched = 2, Held = 3, Fed = 4;
    private const int Reach = 63;
    private const double Dt = 0.01;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();

    private static Fact Self(int bit, bool v = true) => new(FactSubject.Self, bit, v);
    private static Fact This(int bit, bool v = true) => new(FactSubject.This, bit, v);
    private static Fact Tool(int bit, bool v = true) => new(FactSubject.Tool, bit, v);
    private static TargetFilter Kinds(params uint[] kinds) => new() { Kinds = kinds };

    // Approach (anything, no needs: a move), Unlock and Unlatch (a door, in reach), Open (a door in reach, neither locked nor latched), Grab (a knife or key in reach), Calm (a knife in reach: fed).
    private static Verb[] Catalogue() => new[]
    {
        new Verb { Id = "Approach", Gives = new[] { This(Reach) } },
        new Verb { Id = "Unlock", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked) }, Gives = new[] { This(Locked, false) } },
        new Verb { Id = "Unlatch", This = Kinds(Door), Needs = new[] { This(Reach), This(Latched) }, Gives = new[] { This(Latched, false) } },
        new Verb { Id = "Open", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked, false), This(Latched, false) }, Gives = new[] { This(Open) } },
        new Verb { Id = "Grab", This = Kinds(Knife, Key), Needs = new[] { This(Reach) }, Gives = new[] { This(Held) } },
        new Verb { Id = "Calm", This = Kinds(Knife), Needs = new[] { This(Reach) }, Gives = new[] { Self(Fed) } },
    };

    private static GoalSpec Goal(string id, double priority, TargetFilter? target = null, Fact? want = null, params Consideration[] considerations) => new()
    {
        Id = id, Priority = priority, Target = target, Want = want, Considerations = considerations,
        Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
    };

    // 1 at plan length 1 falling to 0 at length 9: a longer plan is worth less, in exact eighths.
    private static Consideration Shorter() => new(ConsiderationInput.PlanSteps, 1.0, 9.0, 1.0, 0.0);

    private static Emitter Thing(ulong id, uint kind, float x, ulong facts = 0UL, float y = 0f, float z = 0f) =>
        new(Channel.Visual, x, y, z, 100f, 1.0, id, kind, facts);

    private static Perception Ears(int capacity)
    {
        return new Perception(new NerveParameters(), Dt, 1,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = capacity, MemorySeconds = 5.0 });
    }

    private static Perception See(int capacity, params Emitter[] emitters)
    {
        var p = Ears(capacity);
        p.Sense(Zero, Zero, Zero, NoBoxes, emitters);
        return p;
    }

    private static Actor Build(Brain brain, int capacity = 8, double memory = 5.0, IReadOnlyList<Verb>? verbs = null) =>
        new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = capacity, MemorySeconds = memory }, verbs: verbs ?? Catalogue());

    private static void Run(Actor a, int ticks, Emitter[] emitters, ulong selfFacts = 0UL)
    {
        var strengths = new float[2];
        var targets = new Quat[2];
        for (var i = 0; i < ticks; i++)
        {
            a.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, emitters, selfFacts: selfFacts);
        }
    }

    private const int Settle = 40;   // well past the deliberate latency (10 ticks)

    // The doors: a plain one two steps away (approach, open), a locked and latched one four (approach, unlock, unlatch, open).
    private static Emitter PlainDoor(ulong id, float x = 6f) => Thing(id, Door, x);
    private static Emitter BoltedDoor(ulong id, float x = 6f) => Thing(id, Door, x, (1UL << Locked) | (1UL << Latched));

    [Fact]
    public void A_satisfied_want_is_not_offered()
    {
        var brain = new Brain { PlanDepth = 6, Goals = new[] { Goal("idle", 1.0), Goal("open", 5.0, Kinds(Door), This(Open)) } };
        var done = Build(brain);
        Run(done, Settle, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(0, done.GoalIndex);
        Assert.Equal(0UL, done.TargetId);
        Assert.Equal(0, done.PlanLength);

        // Flip the door's facts: the pair is offered, planned in two steps, and wins.
        var todo = Build(brain);
        Run(todo, Settle, new[] { PlainDoor(1) });
        Assert.Equal(1, todo.GoalIndex);
        Assert.Equal(1UL, todo.TargetId);
        Assert.Equal(2, todo.PlanLength);

        // The same Actor, the door opened on the way: the pair is withdrawn and the goal lets go.
        Run(todo, Settle, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(0, todo.GoalIndex);
        Assert.Equal(0, todo.PlanLength);
    }

    [Fact]
    public void A_satisfied_self_want_drops_every_pair_of_its_goal()
    {
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("calm", 5.0, Kinds(Knife), Self(Fed)) } };
        var hungry = Build(brain);
        Run(hungry, Settle, new[] { Thing(1, Knife, 5f), Thing(2, Knife, 3f) });
        Assert.Equal(1, hungry.GoalIndex);
        Assert.Equal(2, hungry.PlanLength);
        var fed = Build(brain);
        Run(fed, Settle, new[] { Thing(1, Knife, 5f), Thing(2, Knife, 3f) }, 1UL << Fed);
        Assert.Equal(0, fed.GoalIndex);
        Assert.Equal(0UL, fed.TargetId);
    }

    [Fact]
    public void A_planned_pair_scores_by_its_length()
    {
        // The bolted door is first in the slots, so only its longer plan can lose it the tie.
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("open", 4.0, Kinds(Door), This(Open), Shorter()) } };
        var a = Build(brain);
        Run(a, Settle, new[] { BoltedDoor(1), PlainDoor(2, 9f) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(2UL, a.TargetId);
        Assert.Equal(2, a.PlanLength);
        Assert.Equal(new PlanStep(0, 2, 0), a.PlanStep(0));
        Assert.Equal(new PlanStep(3, 2, 0), a.PlanStep(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.PlanStep(2));

        // Without the length consideration the tie goes to the lowest slot: the longer plan is not worse.
        var flat = Build(new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("open", 4.0, Kinds(Door), This(Open)) } });
        Run(flat, Settle, new[] { BoltedDoor(1), PlainDoor(2, 9f) });
        Assert.Equal(1UL, flat.TargetId);
        Assert.Equal(4, flat.PlanLength);
        Assert.Equal(new PlanStep(2, 1, 0), flat.PlanStep(1));
        Assert.Equal(new PlanStep(1, 1, 0), flat.PlanStep(2));
    }

    [Fact]
    public void Too_dim_to_plan_tries_direct_at_the_discount()
    {
        // The bolted door needs four steps. Direct is raw x DirectDiscount (the length factor at depth + 1 is 1 with no consideration).
        Brain Make(int depth, double priority) => new()
        {
            PlanDepth = depth, DirectDiscount = 0.5,
            Goals = new[] { Goal("idle", 1.0), Goal("open", priority, Kinds(Door), This(Open)) },
        };

        var tries = Build(Make(3, 3.0));      // 3 x 0.5 = 1.5 beats idle's 1
        Run(tries, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(1, tries.GoalIndex);
        Assert.Equal(1UL, tries.TargetId);
        Assert.Equal(-1, tries.PlanLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => tries.PlanStep(0));

        var gives = Build(Make(3, 1.5));      // 1.5 x 0.5 = 0.75 loses to idle
        Run(gives, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(0, gives.GoalIndex);
        Assert.Equal(0, gives.PlanLength);

        var plans = Build(Make(4, 1.5));      // depth 4 finds the plan: 1.5 beats idle
        Run(plans, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(1, plans.GoalIndex);
        Assert.Equal(4, plans.PlanLength);

        // The factor at depth + 1 reads the length consideration: direct at depth 3 is x(4) = 0.625 of raw, then the discount.
        var curve = Build(new Brain { PlanDepth = 3, DirectDiscount = 0.5, Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open), Shorter()) } });
        Run(curve, Settle, new[] { BoltedDoor(1) });     // 3 x 0.625 x 0.5 = 0.9375 < 1
        Assert.Equal(0, curve.GoalIndex);
        var curveWins = Build(new Brain { PlanDepth = 3, DirectDiscount = 0.5, Goals = new[] { Goal("idle", 0.9), Goal("open", 3.0, Kinds(Door), This(Open), Shorter()) } });
        Run(curveWins, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(1, curveWins.GoalIndex);
        Assert.Equal(-1, curveWins.PlanLength);
    }

    [Fact]
    public void Depth_zero_tries_every_unsatisfied_want_direct()
    {
        var brain = new Brain { PlanDepth = 0, Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open)) } };
        var a = Build(brain);
        Run(a, Settle, new[] { PlainDoor(1) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(-1, a.PlanLength);

        // A satisfied want is still not offered at depth 0.
        var done = Build(brain);
        Run(done, Settle, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(0, done.GoalIndex);

        // A Brain with no want selects as the public Select does at any depth, and never plans.
        var free = new Brain { PlanDepth = 5, Goals = new[] { Goal("idle", 1.0), Goal("chase", 3.0, Kinds(Door)) } };
        var p = See(4, PlainDoor(1));
        var viaActor = Build(free);
        Run(viaActor, Settle, new[] { PlainDoor(1) });
        Assert.Equal(free.Select(default, p, 0f, 0f, 0f, new Choice(0, 0)), new Choice(viaActor.GoalIndex, viaActor.TargetId));
        Assert.Equal(0, viaActor.PlanLength);
    }

    [Fact]
    public void The_public_select_refuses_a_brain_with_wants()
    {
        var brain = new Brain { Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open)) } };
        Assert.Throws<InvalidOperationException>(() => brain.Select(default, See(4, PlainDoor(1)), 0f, 0f, 0f, new Choice(0, 0)));
    }

    [Fact]
    public void Deepening_finds_short_plans_first()
    {
        // One want goal on two doors at equal raw 4: the bolted door (slot 0, four steps) and the plain one (slot 1, two steps).
        var percepts = See(4, BoltedDoor(1), PlainDoor(2, 9f));
        var probe = new Planner(Catalogue(), 4, 4, 0.6);
        var steps = new PlanStep[4];
        var rounds = 0;
        for (var k = 1; k <= 2; k++)
        {
            for (var slot = 0; slot < 2; slot++)
            {
                probe.Plan(percepts, 0UL, 0f, 0f, 0f, slot, This(Open), k, steps, Planner.NodeBudget, k);
                rounds += probe.LastNodes;
            }
        }

        (Choice Pick, int Spent) Select(int budget, double idle = 1.5)
        {
            var brain = new Brain
            {
                PlanDepth = 4, ThinkBudget = budget, DirectDiscount = 0.5,
                Goals = new[] { Goal("idle", idle), Goal("open", 4.0, Kinds(Door), This(Open), Shorter()) },
            };
            var d = new Deliberation(brain, Catalogue(), 4);
            return (brain.Select(default, percepts, 0f, 0f, 0f, new Choice(0, 0), d), d.Spent);
        }

        // The plain door (4 x 0.875 = 3.5) wins in round 2 and the bolted door, bounded by 4 x 0.75 after it, is pruned before round 3:
        // the budget paid for round 1 and round 2 of both pairs and nothing more, not the four-step search.
        Assert.Equal((new Choice(1, 2), rounds), Select(rounds));
        Assert.Equal((new Choice(1, 2), rounds), Select(int.MaxValue));
        // One grounding short, the plain door's round 2 runs the budget out: it goes direct (4 x 0.25 = 1 < idle), and so does the bolted one.
        Assert.Equal((new Choice(0, 0), rounds - 1), Select(rounds - 1));
        Assert.Equal(new Choice(1, 1), Select(rounds - 1, idle: 0.5).Pick);
    }

    [Fact]
    public void A_dead_end_does_not_starve_a_short_plan()
    {
        // Two doors with four bolts (approach, four unbolts, open: six steps, past PlanDepth 4) at raw 6, a knife with a two-step want (approach, calm) at raw 3.
        const int Barred = 5, Chained = 6;
        var verbs = new[]
        {
            Catalogue()[0], Catalogue()[1], Catalogue()[2],
            new Verb { Id = "Unbar", This = Kinds(Door), Needs = new[] { This(Reach), This(Barred) }, Gives = new[] { This(Barred, false) } },
            new Verb { Id = "Unchain", This = Kinds(Door), Needs = new[] { This(Reach), This(Chained) }, Gives = new[] { This(Chained, false) } },
            new Verb { Id = "Open", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked, false), This(Latched, false), This(Barred, false), This(Chained, false) }, Gives = new[] { This(Open) } },
            Catalogue()[5],
        };
        const ulong Bolts = (1UL << Locked) | (1UL << Latched) | (1UL << Barred) | (1UL << Chained);
        var percepts = See(4, Thing(1, Door, 6f, Bolts), Thing(2, Door, 8f, Bolts), Thing(3, Knife, 3f));
        var probe = new Planner(verbs, 4, 4, 0.6);
        var steps = new PlanStep[4];
        var rounds = 0;
        for (var k = 1; k <= 2; k++)
        {
            for (var slot = 0; slot < 3; slot++)
            {
                probe.Plan(percepts, 0UL, 0f, 0f, 0f, slot == 2 ? 0 : slot, slot == 2 ? Self(Fed) : This(Open), k, steps, Planner.NodeBudget, k);
                rounds += probe.LastNodes;
            }
        }

        Assert.Equal(-1, probe.Plan(percepts, 0UL, 0f, 0f, 0f, 0, This(Open), 4, steps));
        Assert.True(probe.LastNodes > rounds, $"one full dead-end search ({probe.LastNodes}) must cost more than rounds 1 and 2 of all three pairs ({rounds})");

        var brain = new Brain
        {
            PlanDepth = 4, ThinkBudget = rounds, DirectDiscount = 0.5,
            Goals = new[] { Goal("idle", 0.5), Goal("dead", 6.0, Kinds(Door), This(Open), Shorter()), Goal("calm", 3.0, Kinds(Knife), Self(Fed), Shorter()) },
        };
        var d = new Deliberation(brain, verbs, 4);
        // The doors go direct at 6 x 0.25 = 1.5; the knife's plan, 3 x 0.875 = 2.625, wins. Best raw first would have spent the whole budget on the first door.
        Assert.Equal(new Choice(2, 3), brain.Select(default, percepts, 0f, 0f, 0f, new Choice(0, 0), d));
        Assert.Equal(rounds, d.Spent);
    }

    [Fact]
    public void A_self_want_is_planned_once_for_all_its_pairs()
    {
        // Three knives, one budget just over one plan: every pair of the goal shares the plan found for the first.
        var percepts = See(4, Thing(1, Knife, 5f), Thing(2, Knife, 3f), Thing(3, Knife, 4f));
        var probe = new Planner(Catalogue(), 4, 4, 0.6);
        var steps = new PlanStep[4];
        Assert.Equal(2, probe.Plan(percepts, 0UL, 0f, 0f, 0f, 0, Self(Fed), 4, steps));
        var brain = new Brain { PlanDepth = 4, ThinkBudget = probe.LastNodes, Goals = new[] { Goal("idle", 1.0), Goal("calm", 3.0, Kinds(Knife), Self(Fed), Shorter()) } };
        var d = new Deliberation(brain, Catalogue(), 4);
        // 3 x 0.875 = 2.625 for every pair, the lowest slot wins, and the budget paid for one plan, not three.
        Assert.Equal(new Choice(1, 1), brain.Select(default, percepts, 0f, 0f, 0f, new Choice(0, 0), d));
        Assert.Equal(probe.LastNodes, d.Spent);
        // The commitment breaks the tie toward the current pair.
        var committed = brain with { TargetCommitment = 1.1 };
        var d2 = new Deliberation(committed, Catalogue(), 4);
        Assert.Equal(new Choice(1, 3), committed.Select(default, percepts, 0f, 0f, 0f, new Choice(1, 3), d2));
    }

    [Fact]
    public void Ties_resolve_by_key_not_by_the_order_pairs_are_offered()
    {
        // An untargeted goal at index 2 and a want pair at index 1 tie at 2: the want pair is offered later (pass 2) and wins on the lowest goal index.
        var percepts = See(4, PlainDoor(1));
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 0.5), Goal("open", 2.0, Kinds(Door), This(Open)), Goal("also", 2.0) } };
        var d = new Deliberation(brain, Catalogue(), 4);
        Assert.Equal(new Choice(1, 1), brain.Select(default, percepts, 0f, 0f, 0f, new Choice(0, 0), d));
        // The same tie with the untargeted pair current: current wins.
        Assert.Equal(new Choice(2, 0), brain.Select(default, percepts, 0f, 0f, 0f, new Choice(2, 0), d));
    }

    [Fact]
    public void No_pair_lets_go_of_the_target()
    {
        // A want goal whose door stands open, and then a plain one whose door is forgotten.
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("open", 3.0, Kinds(Door), This(Open)) } };
        var a = Build(brain, memory: 0.5);
        Run(a, Settle, new[] { PlainDoor(1) });
        Assert.Equal(1UL, a.TargetId);
        Run(a, Settle, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(0, a.GoalIndex);
        Assert.Equal(0UL, a.TargetId);

        var forgetful = Build(brain, memory: 0.5);
        Run(forgetful, Settle, new[] { PlainDoor(1) });
        Assert.Equal(1UL, forgetful.TargetId);
        Run(forgetful, 120, Array.Empty<Emitter>());      // the memory runs out, the door is gone
        Assert.Equal(0, forgetful.GoalIndex);
        Assert.Equal(0UL, forgetful.TargetId);
        Assert.Equal(0, forgetful.PlanLength);

        // The Brain-level rule: nothing offered keeps the goal in force and drops the target.
        var none = See(4);
        var d = new Deliberation(brain, Catalogue(), 4);
        Assert.Equal(new Choice(0, 0), brain.Select(default, none, 0f, 0f, 0f, new Choice(0, 55), d));
    }

    [Fact]
    public void The_plan_is_made_with_the_decision_and_carried()
    {
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open)) } };
        var a = Build(brain);
        Run(a, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(4, a.PlanLength);

        // The plan in force is the one the Select made, step for step.
        var probe = new Planner(Catalogue(), 8, 4, 0.6);
        var expected = new PlanStep[4];
        var seen = See(8, BoltedDoor(1));
        Assert.Equal(4, probe.Plan(seen, 0UL, 0f, 0f, 0f, 0, This(Open), 4, expected));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i], a.PlanStep(i));
        }

        // The door is sensed unlocked while the Select re-chooses the pair: the plan shortens the same tick.
        Run(a, 1, new[] { PlainDoor(1) });
        Assert.Equal(2, a.PlanLength);
        // The door is seen open: the pair is no longer offered and the switch is pending, so this tick only checks the plan, and the want holds.
        Run(a, 1, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(1UL, a.TargetId);
        Assert.Equal(0, a.PlanLength);
        Run(a, Settle, new[] { Thing(1, Door, 6f, 1UL << Open) });
        Assert.Equal(0, a.GoalIndex);
        Assert.Equal(0, a.PlanLength);
    }

    [Fact]
    public void A_carried_plan_goes_direct_when_a_step_s_thing_is_forgotten()
    {
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("calm", 3.0, Kinds(Knife), Self(Fed)) } };
        var a = Build(brain, memory: 0.5);
        Run(a, Settle, new[] { Thing(1, Knife, 5f) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(2, a.PlanLength);

        // The knife leaves memory: the Select lets the goal go, a latency later; in between the plan in force is checked, and its steps have nothing to act on.
        var lengths = new HashSet<int>();
        for (var i = 0; i < 120; i++)
        {
            Run(a, 1, Array.Empty<Emitter>());
            lengths.Add(a.PlanLength);
        }

        Assert.Contains(-1, lengths);
        Assert.Equal(0, a.GoalIndex);
        Assert.Equal(0, a.PlanLength);
    }

    [Fact]
    public void A_refused_select_replans_the_pair_in_force()
    {
        // The open goal is in force and inside its minimum dwell; a knife appears that a second goal of equal priority would take at once, so the Select is refused.
        Brain Make(int budget) => new()
        {
            PlanDepth = 4, ThinkBudget = budget,
            Goals = new[]
            {
                Goal("idle", 0.5),
                Goal("open", 3.0, Kinds(Door), This(Open), Shorter()) with { MinDwellSeconds = 50.0 },
                Goal("grab", 3.0, Kinds(Knife), This(Held), Shorter()),
            },
        };

        var a = Build(Make(Planner.NodeBudget));
        Run(a, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(4, a.PlanLength);
        Run(a, 1, new[] { PlainDoor(1), Thing(2, Knife, 0.3f) });
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(2, a.PlanLength);       // the refused Select planned the pair in force again, from this tick's percepts

        // With the budget spent by the Select itself nothing is left to plan the pair in force: its plan is carried, only checked.
        var b = Build(Make(1));
        Run(b, Settle, new[] { BoltedDoor(1) });
        Assert.Equal(1, b.GoalIndex);
        Assert.Equal(-1, b.PlanLength);      // no budget to plan: he goes direct
        Run(b, 1, new[] { PlainDoor(1), Thing(2, Knife, 0.3f) });
        Assert.Equal(1, b.GoalIndex);
        Assert.Equal(-1, b.PlanLength);
        Run(b, 1, new[] { Thing(1, Door, 6f, 1UL << Open), Thing(2, Knife, 0.3f) });
        Assert.Equal(0, b.PlanLength);
    }

    [Fact]
    public void Self_facts_reach_the_planner_and_bit_63_is_refused()
    {
        var brain = new Brain { PlanDepth = 4, Goals = new[] { Goal("idle", 1.0), Goal("calm", 3.0, Kinds(Knife), Self(Fed)) } };
        var a = Build(brain);
        var strengths = new float[2];
        var targets = new Quat[2];
        Assert.Throws<ArgumentException>(() => a.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, selfFacts: 1UL << 63));
        Assert.Equal(0L, a.TickCount);
        Run(a, Settle, new[] { Thing(1, Knife, 5f) }, 1UL << Fed);
        Assert.Equal(0, a.GoalIndex);
    }

    [Fact]
    public void Validation_refuses_bad_wants_and_fields()
    {
        static Actor Make(Brain b) => Build(b);
        void Refuses(Brain b) => Assert.Throws<ArgumentException>(() => Make(b));
        Brain With(params GoalSpec[] goals) => new() { PlanDepth = 2, Goals = goals };

        Make(With(Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open))));                         // the baseline is fine
        Refuses(With(Goal("a", 1.0, Kinds(Door), Tool(Open))));                                           // Tool refused
        Refuses(With(Goal("a", 1.0, null, This(Open))));                                                  // This needs a Target
        Refuses(With(Goal("a", 1.0, null, Self(Reach))));                                                 // a Self want is not on bit 63
        Refuses(With(Goal("a", 1.0, Kinds(Door), new Fact(FactSubject.This, 64, true))));                 // bit out of range
        Refuses(With(Goal("a", -1.0, Kinds(Door), This(Open))));                                          // a want goal's Priority >= 0
        Refuses(With(Goal("a", 1.0, Kinds(Door), null, Shorter())));                                      // PlanSteps only on want goals
        Make(With(Goal("a", 1.0, null, Self(Fed), Shorter())));                                           // ... and a Self want needs no target
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { PlanDepth = 9 });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { PlanDepth = -1 });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { DirectDiscount = 1.5 });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { DirectDiscount = double.NaN });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { ThinkBudget = 0 });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { ReachMeters = 0.0 });
        Refuses(With(Goal("a", 1.0, Kinds(Door), This(Open))) with { ReachMeters = double.PositiveInfinity });

        // A want needs percepts to plan over.
        Assert.Throws<ArgumentException>(() => new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), With(Goal("a", 1.0, null, Self(Fed)))));
    }

    // ----- reference: every pair planned on its own, the original index-order tournament

    private static Choice Reference(Brain brain, Perception p, Planner planner, ulong selfFacts, Choice current)
    {
        var buf = new PlanStep[Math.Max(1, brain.PlanDepth)];
        var best = default(Choice);
        var bestScore = 0.0;
        var bestIsCurrent = false;
        var found = false;
        for (var g = 0; g < brain.Goals.Count; g++)
        {
            var goal = brain.Goals[g];
            if (goal.Target is null)
            {
                Consider(g, goal, -1, 0UL, goal.Score(default, default, 0f, 0f, 0f));
                continue;
            }

            for (var s = 0; s < p.PerceptCount; s++)
            {
                var pc = p.Percept(s);
                if (pc.Id != 0UL && goal.Target.Accepts(pc.Kind))
                {
                    Consider(g, goal, s, pc.Id, goal.Score(default, pc, 0f, 0f, 0f));
                }
            }
        }

        return found ? best : new Choice(current.Goal, 0UL);

        void Consider(int g, GoalSpec goal, int slot, ulong target, double raw)
        {
            var isCurrent = g == current.Goal && target == current.Target;
            if (isCurrent)
            {
                raw *= brain.TargetCommitment;
            }

            if (goal.Want is { } want)
            {
                var n = planner.Plan(p, selfFacts, 0f, 0f, 0f, want.Subject == FactSubject.Self ? 0 : slot, want, brain.PlanDepth, buf);
                if (n == 0)
                {
                    return;
                }

                raw *= n > 0 ? goal.PlanFactor(n) : goal.PlanFactor(brain.PlanDepth + 1) * brain.DirectDiscount;
            }

            if (!found || raw > bestScore || (raw == bestScore && isCurrent && !bestIsCurrent))
            {
                best = new Choice(g, target);
                bestScore = raw;
                bestIsCurrent = isCurrent;
                found = true;
            }
        }
    }

    [Fact]
    public void Ordered_selection_equals_the_exhaustive_reference()
    {
        var rng = new Random(20261126);
        var wantChosen = 0;
        var depthsSeen = new HashSet<int>();
        for (var w = 0; w < 300; w++)
        {
            const int Slots = 16;
            var emitters = new List<Emitter>();
            var things = 5 + rng.Next(12);
            for (var i = 0; i < things; i++)
            {
                var kind = (uint)(1 + rng.Next(4));
                var facts = (ulong)rng.Next(8) << 1;      // bits 1..3: locked, latched, held, never Open
                var x = new[] { 0.3f, 0.5f, 1.0f, 2.0f, 5.0f }[rng.Next(5)];
                emitters.Add(Thing((ulong)(10 + i), kind, x, facts, y: new[] { 0f, 0.4f, 3f }[rng.Next(3)]));
            }

            var percepts = See(Slots, emitters.ToArray());
            var priorities = new[] { 1.0, 2.0, 3.0, 4.0 };
            var goals = new List<GoalSpec>
            {
                Goal("idle", priorities[rng.Next(4)]),
                Goal("open", priorities[rng.Next(4)], Kinds(Door), This(Open), rng.Next(2) == 0 ? new[] { Shorter() } : Array.Empty<Consideration>()),
                Goal("grab", priorities[rng.Next(4)], Kinds(Knife, Key), This(Held), rng.Next(2) == 0 ? new[] { Shorter() } : Array.Empty<Consideration>()),
                Goal("calm", priorities[rng.Next(4)], Kinds(Knife), Self(Fed), Shorter()),
                Goal("rest", priorities[rng.Next(4)], null, Self(Fed), rng.Next(2) == 0 ? new[] { Shorter() } : Array.Empty<Consideration>()),
                Goal("junk", priorities[rng.Next(4)], Kinds(Junk)),
            };
            var depth = rng.Next(6);
            depthsSeen.Add(depth);
            var brain = new Brain
            {
                Goals = goals, PlanDepth = depth, ThinkBudget = int.MaxValue, DirectDiscount = new[] { 0.25, 0.5, 0.75 }[rng.Next(3)],
                TargetCommitment = new[] { 1.0, 1.25 }[rng.Next(2)], ReachMeters = 1.0,
            };
            var selfFacts = rng.Next(4) == 0 ? 1UL << Fed : 0UL;
            var cur = new Choice(rng.Next(goals.Count), rng.Next(3) == 0 ? (ulong)(10 + rng.Next(things)) : 0UL);
            var d = new Deliberation(brain, Catalogue(), Slots) { SelfFacts = selfFacts };
            var planner = new Planner(Catalogue(), Slots, depth, 1.0);
            var expected = Reference(brain, percepts, planner, selfFacts, cur);
            var actual = brain.Select(default, percepts, 0f, 0f, 0f, cur, d);
            Assert.True(expected == actual, $"world {w} depth {depth}: reference {expected}, select {actual}");
            if (brain.Goals[actual.Goal].Want is not null)
            {
                wantChosen++;
            }
        }

        Assert.True(wantChosen > 100 && depthsSeen.Count == 6, $"the worlds are too one-sided: {wantChosen} want pairs chosen, depths {depthsSeen.Count}");
    }

    // ----- the crowded room: how often a bounded budget changes the decision

    private static (Perception Percepts, Verb[] Verbs, Emitter[] Emitters) Crowd(Random rng, int things)
    {
        const uint Strap = 5, Switch = 6;
        var emitters = new List<Emitter> { Thing(1, Strap, 0.3f), Thing(2, Knife, 0.5f) };
        for (var i = 2; i < things; i++)
        {
            // Knives, keys, doors (70% locked), switches and four junk kinds, all within 12 m.
            var pick = rng.Next(100);
            var kind = pick switch { < 6 => Knife, < 12 => Key, < 20 => Door, < 26 => Switch, _ => (uint)(10 + rng.Next(4)) };
            var facts = kind == Door && rng.Next(10) < 7 ? 1UL << Locked : 0UL;
            emitters.Add(Thing((ulong)(1 + i), kind, (float)(rng.NextDouble() * 12.0), facts, (float)(rng.NextDouble() * 6.0 - 3.0)));
        }

        var verbs = new[]
        {
            new Verb { Id = "Approach", Needs = new[] { Self(Fed) }, Gives = new[] { This(Reach) } },
            new Verb { Id = "Grab", This = Kinds(Knife, Key), Needs = new[] { This(Reach) }, Gives = new[] { This(Held) } },
            new Verb { Id = "Cut", This = Kinds(Strap), Tool = Kinds(Knife), Needs = new[] { Tool(Held), This(Reach) }, Gives = new[] { Self(Fed), This(Open) } },
            new Verb { Id = "Pry", This = Kinds(Strap), Tool = Kinds(Door), Needs = new[] { Tool(Reach), This(Reach) }, Gives = new[] { Self(Fed), This(Open) } },
            new Verb { Id = "Switch", This = Kinds(Switch), Needs = new[] { This(Reach) }, Gives = new[] { This(Open) } },
            new Verb { Id = "Unlock", This = Kinds(Door), Tool = Kinds(Key), Needs = new[] { Tool(Held), This(Reach), This(Locked) }, Gives = new[] { This(Locked, false) } },
            new Verb { Id = "Open", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked, false) }, Gives = new[] { This(Open) } },
            new Verb { Id = "Fiddle", This = Kinds(10, 11), Needs = new[] { This(Reach) }, Gives = new[] { This(Latched) } },
            new Verb { Id = "Stack", This = Kinds(12, 13), Tool = Kinds(10, 11), Needs = new[] { Tool(Latched), This(Reach) }, Gives = new[] { This(Latched) } },
        };
        var all = emitters.ToArray();
        return (See(things, all), verbs, all);
    }

    private static Brain Crowded(int depth, int budget) => new()
    {
        PlanDepth = depth, ThinkBudget = budget, DirectDiscount = 0.5, ReachMeters = 1.0, TargetCommitment = 1.1,
        Goals = new[]
        {
            Goal("idle", 0.5),
            Goal("free", 2.0, Kinds(5), This(Open), Shorter()),
            Goal("door", 3.0, Kinds(Door), This(Open), Shorter()),
            Goal("switch", 2.5, Kinds(6), This(Open), Shorter()),
            Goal("knife", 1.5, Kinds(Knife), This(Held), Shorter()),
        },
    };

    private static int Differing(int things, int depth, int seeds)
    {
        var differ = 0;
        for (var seed = 0; seed < seeds; seed++)
        {
            var rng = new Random(seed);
            var (p, verbs, _) = Crowd(rng, things);
            var bounded = Crowded(depth, Planner.NodeBudget);
            var unbounded = Crowded(depth, int.MaxValue);
            var cur = new Choice(0, 0);
            var db = new Deliberation(bounded, verbs, things);
            var du = new Deliberation(unbounded, verbs, things);
            var a = bounded.Select(default, p, 0f, 0f, 0f, cur, db);
            var b = unbounded.Select(default, p, 0f, 0f, 0f, cur, du);
            if (a != b)
            {
                differ++;
            }
        }

        return differ;
    }

    [Fact]
    public void A_crowded_room_changes_few_decisions()
    {
        foreach (var depth in new[] { 4, 5, 6, 8 })
        {
            Assert.Equal(0, Differing(48, depth, 100));
        }

        Assert.Equal(CrowdedDepth6At64Differing, Differing(64, 6, 100));
    }

    private const int CrowdedDepth6At64Differing = 2;
}
