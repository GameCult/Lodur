using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace Lodur.Core.Tests;

public sealed class PlannerTests
{
    // The test world: kinds, then the fact bits the verbs speak of. Lodur reads none of them.
    private const uint Knife = 1, Strap = 2, Switch = 3, Door = 4, Key = 5;
    private const int Held = 0, Cut = 1, Off = 2, Open = 3, Locked = 4, Free = 5;
    private const int Reach = 63;

    private const int ApproachV = 0, GrabV = 1, CutStrapV = 2, SwitchOffV = 3, UnlockV = 4, OpenV = 5;

    private const double Dt = 0.01;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();

    private static Fact Self(int bit, bool v = true) => new(FactSubject.Self, bit, v);
    private static Fact This(int bit, bool v = true) => new(FactSubject.This, bit, v);
    private static Fact Tool(int bit, bool v = true) => new(FactSubject.Tool, bit, v);
    private static Fact[] Many(int n) => Enumerable.Range(0, n).Select(i => This(i)).ToArray();
    private static TargetFilter Kinds(params uint[] kinds) => new TargetFilter { Kinds = kinds };

    private static Verb[] Catalogue() => new[]
    {
        new Verb { Id = "Approach", Needs = new[] { Self(Free) }, Gives = new[] { This(Reach) } },
        new Verb { Id = "Grab", This = Kinds(Knife, Key), Needs = new[] { This(Reach) }, Gives = new[] { This(Held) } },
        new Verb { Id = "CutStrap", This = Kinds(Strap), Tool = Kinds(Knife), Needs = new[] { Tool(Held), This(Reach) }, Gives = new[] { Self(Free), This(Cut) } },
        new Verb { Id = "SwitchOff", This = Kinds(Switch), Needs = new[] { This(Reach) }, Gives = new[] { This(Off) } },
        new Verb { Id = "Unlock", This = Kinds(Door), Tool = Kinds(Key), Needs = new[] { Tool(Held), This(Reach), This(Locked) }, Gives = new[] { This(Locked, false) } },
        new Verb { Id = "Open", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked, false) }, Gives = new[] { This(Open) } },
    };

    private static Perception Make(int capacity = 8, double memory = 5.0) =>
        new Perception(new NerveParameters(), Dt, 1,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = capacity, MemorySeconds = memory });

    private static Emitter Thing(ulong id, uint kind, float x, ulong facts = 0) =>
        new Emitter(Channel.Visual, x, 0f, 0f, 100f, 1.0, id, kind, facts);

    private static void Sense(Perception p, params Emitter[] emitters) => p.Sense(Zero, Zero, Zero, NoBoxes, emitters);

    // Ids in the order sensed: knife 1 and strap 2 within a metre, switch 3, key 4 and door 5 further out, the door locked.
    private static Perception World(float switchX = 5f, ulong doorFacts = 1UL << Locked, ulong keyFacts = 0)
    {
        var p = Make();
        Sense(p, Thing(1, Knife, 0.5f), Thing(2, Strap, 0.5f), Thing(3, Switch, switchX), Thing(4, Key, 8f, keyFacts), Thing(5, Door, 12f, doorFacts));
        return p;
    }

    private static int SlotOf(Perception p, ulong id)
    {
        for (var s = 0; s < p.PerceptCount; s++)
        {
            if (p.Percept(s).Id == id) return s;
        }

        return -1;
    }

    private static Planner Build(Verb[]? verbs = null, int depth = 8, double reach = 1.0) => new Planner(verbs ?? Catalogue(), 8, depth, reach);

    private static (int Length, PlanStep[] Steps) Run(Planner planner, Perception p, ulong id, Fact want, int depth, ulong self = 0UL, float ox = 0f)
    {
        var steps = new PlanStep[8];
        var n = planner.Plan(p, self, ox, 0f, 0f, SlotOf(p, id), want, depth, steps);
        return (n, steps);
    }

    private static void Expect(PlanStep[] steps, params (int Verb, ulong This, ulong Tool)[] expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(new PlanStep(expected[i].Verb, expected[i].This, expected[i].Tool), steps[i]);
        }
    }

    [Fact]
    public void A_want_that_already_holds_is_a_plan_of_length_zero()
    {
        var planner = Build();
        var p = World();
        var steps = new PlanStep[8];
        Array.Fill(steps, new PlanStep(-9, 9, 9));

        // The target's own fact, the self's, and the derived reach all count.
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 4), This(Locked, false), 7, steps));
        Assert.Equal(0, planner.Plan(p, 1UL << Free, 0f, 0f, 0f, SlotOf(p, 3), Self(Free), 7, steps));
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 1), This(Reach), 7, steps));
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 5), This(Locked), 0, steps));
        foreach (var s in steps) Assert.Equal(new PlanStep(-9, 9, 9), s);

        // At depth 0 a want that does not hold is no plan, not a crash.
        Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 3), This(Off), 0, steps));
    }

    [Fact]
    public void Switching_off_needs_four_steps_and_depth_three_finds_none()
    {
        var planner = Build();
        var p = World();
        var (n, steps) = Run(planner, p, 3, This(Off), 4);
        Assert.Equal(4, n);
        Expect(steps, (GrabV, 1, 0), (CutStrapV, 2, 1), (ApproachV, 3, 0), (SwitchOffV, 3, 0));

        Assert.Equal(-1, Run(planner, p, 3, This(Off), 3).Length);

        // A deeper allowance still returns the shallowest.
        var (m, deeper) = Run(planner, p, 3, This(Off), 8);
        Assert.Equal(4, m);
        Expect(deeper, (GrabV, 1, 0), (CutStrapV, 2, 1), (ApproachV, 3, 0), (SwitchOffV, 3, 0));
    }

    [Fact]
    public void FromDepth_searches_exactly_that_depth()
    {
        var planner = Build();
        var p = World();
        var steps = new PlanStep[8];
        var off = SlotOf(p, 3);
        var cut = SlotOf(p, 2);

        // Switching off is four steps: found at exactly four, none at exactly three.
        Assert.Equal(4, planner.Plan(p, 0UL, 0f, 0f, 0f, off, This(Off), 4, steps, Planner.NodeBudget, 4));
        Expect(steps, (GrabV, 1, 0), (CutStrapV, 2, 1), (ApproachV, 3, 0), (SwitchOffV, 3, 0));
        Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, off, This(Off), 3, steps, Planner.NodeBudget, 3));
        Assert.False(planner.LastExhausted);

        // Cutting the strap is two: a search that starts at three returns no shallower plan.
        Assert.Equal(2, planner.Plan(p, 0UL, 0f, 0f, 0f, cut, This(Cut), 2, steps, Planner.NodeBudget, 2));
        Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, cut, This(Cut), 3, steps, Planner.NodeBudget, 3));
        Assert.Equal(2, planner.Plan(p, 0UL, 0f, 0f, 0f, cut, This(Cut), 3, steps));

        // A want that holds is still 0, whatever the first depth.
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 4), This(Locked, false), 3, steps, Planner.NodeBudget, 3));

        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, off, This(Off), 4, steps, Planner.NodeBudget, 0));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, off, This(Off), 4, steps, Planner.NodeBudget, 5));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, off, This(Off), 0, steps, Planner.NodeBudget, 2));
    }

    [Fact]
    public void Opening_the_locked_door_needs_seven_and_six_finds_none()
    {
        var planner = Build();
        var p = World();
        var (n, steps) = Run(planner, p, 5, This(Open), 7);
        Assert.Equal(7, n);
        Expect(steps, (GrabV, 1, 0), (CutStrapV, 2, 1), (ApproachV, 4, 0), (GrabV, 4, 0), (ApproachV, 5, 0), (UnlockV, 5, 4), (OpenV, 5, 0));

        Assert.Equal(-1, Run(planner, p, 5, This(Open), 6).Length);
    }

    [Fact]
    public void The_shallowest_plan_wins_and_ties_go_to_the_lower_verb_then_slot()
    {
        const int a = 20, b = 21, c = 22;

        // A 3-step route through the lowest verb, a 2-step route through the highest: the 2-step one wins.
        var routes = new[]
        {
            new Verb { Id = "Long", Needs = new[] { This(b) }, Gives = new[] { This(a) } },
            new Verb { Id = "Prep1", Needs = new[] { This(c) }, Gives = new[] { This(b) } },
            new Verb { Id = "Prep2", Gives = new[] { This(c) } },
            new Verb { Id = "Direct", Needs = new[] { This(c) }, Gives = new[] { This(a) } },
        };
        var p = Make();
        Sense(p, Thing(7, 1, 0f));
        var (n, steps) = Run(new Planner(routes, 8, 3, 1.0), p, 7, This(a), 3);
        Assert.Equal(2, n);
        Expect(steps, (2, 7, 0), (3, 7, 0));

        // Equal routes: the lower verb, then the lower This slot (the ids run against the slots), then the lower Tool slot.
        var ties = new[]
        {
            new Verb { Id = "A", This = Kinds(2), Tool = Kinds(3), Gives = new[] { Self(1) } },
            new Verb { Id = "B", This = Kinds(2), Tool = Kinds(3), Gives = new[] { Self(1) } },
        };
        var q = Make();
        Sense(q, Thing(30, 2, 0f), Thing(10, 2, 0f), Thing(40, 3, 0f), Thing(20, 3, 0f));
        var tied = new PlanStep[1];
        Assert.Equal(1, new Planner(ties, 8, 1, 1.0).Plan(q, 0UL, 0f, 0f, 0f, 0, Self(1), 1, tied));
        Assert.Equal(new PlanStep(0, 30, 40), tied[0]);

        // The same two verbs the other way round: the plan names the other verb index, and a verb that cannot ground loses to one that can.
        var reordered = new[] { ties[1] with { Id = "B" }, ties[0] with { Id = "A", This = Kinds(9) } };
        Assert.Equal(1, new Planner(reordered, 8, 1, 1.0).Plan(q, 0UL, 0f, 0f, 0f, 0, Self(1), 1, tied));
        Assert.Equal(new PlanStep(0, 30, 40), tied[0]);
        var onlySecond = new[] { ties[0] with { This = Kinds(9) }, ties[1] };
        Assert.Equal(1, new Planner(onlySecond, 8, 1, 1.0).Plan(q, 0UL, 0f, 0f, 0f, 0, Self(1), 1, tied));
        Assert.Equal(new PlanStep(1, 30, 40), tied[0]);
    }

    [Fact]
    public void A_gives_that_contradicts_a_need_is_not_relevant()
    {
        // Drop undoes Held; Grab gives it. The want is Held, so Drop is never a step toward it.
        var verbs = new[]
        {
            new Verb { Id = "Drop", Gives = new[] { This(Held, false) } },
            new Verb { Id = "Grab", Gives = new[] { This(Held) } },
        };
        var p = Make();
        Sense(p, Thing(1, 1, 0f));
        var planner = new Planner(verbs, 8, 3, 1.0);
        var (n, steps) = Run(planner, p, 1, This(Held), 3);
        Assert.Equal(1, n);
        Assert.Equal(new PlanStep(1, 1, 0), steps[0]);

        // The want Held false is the other verb's: Drop.
        var q = Make();
        Sense(q, Thing(1, 1, 0f, 1UL << Held));
        (n, steps) = Run(planner, q, 1, This(Held, false), 3);
        Assert.Equal(1, n);
        Assert.Equal(new PlanStep(0, 1, 0), steps[0]);

        // Smash gives Cut but undoes Off. Where Off is still needed alongside Cut it is skipped for Tidy, which gives Cut alone.
        var both = new[]
        {
            new Verb { Id = "Smash", Gives = new[] { This(Cut), This(Off, false) } },
            new Verb { Id = "Tidy", Gives = new[] { This(Cut) } },
            new Verb { Id = "Finish", Needs = new[] { This(Cut), This(Off) }, Gives = new[] { This(Open) } },
        };
        var r = Make();
        Sense(r, Thing(1, 1, 0f, 1UL << Off));
        (n, steps) = Run(new Planner(both, 8, 3, 1.0), r, 1, This(Open), 3);
        Assert.Equal(2, n);
        Assert.Equal(new PlanStep(1, 1, 0), steps[0]);
        Assert.Equal(new PlanStep(2, 1, 0), steps[1]);

        // Where Off is not needed before the step, Smash is fine: Switch gives Off after it.
        var later = new[]
        {
            new Verb { Id = "Smash", Gives = new[] { This(Cut), This(Off, false) } },
            new Verb { Id = "Switch", Needs = new[] { This(Cut) }, Gives = new[] { This(Off) } },
        };
        var t = Make();
        Sense(t, Thing(1, 1, 0f));
        (n, steps) = Run(new Planner(later, 8, 3, 1.0), t, 1, This(Off), 3);
        Assert.Equal(2, n);
        Assert.Equal(new PlanStep(0, 1, 0), steps[0]);
        Assert.Equal(new PlanStep(1, 1, 0), steps[1]);

        // A verb whose need contradicts a literal still needed is pruned (Sever needs Cut false while Cut is wanted): Mend is the route.
        var absurd = new[]
        {
            new Verb { Id = "Sever", Needs = new[] { This(Cut, false) }, Gives = new[] { This(Off) } },
            new Verb { Id = "Mend", Gives = new[] { This(Off) } },
            new Verb { Id = "Finish", Needs = new[] { This(Cut), This(Off) }, Gives = new[] { This(Open) } },
        };
        var u = Make();
        Sense(u, Thing(1, 1, 0f, 1UL << Cut));
        (n, steps) = Run(new Planner(absurd, 8, 3, 1.0), u, 1, This(Open), 3);
        Assert.Equal(2, n);
        Assert.Equal(new PlanStep(1, 1, 0), steps[0]);
        Assert.Equal(new PlanStep(2, 1, 0), steps[1]);
    }

    [Fact]
    public void Reach_is_derived_from_distance()
    {
        var planner = Build();

        // The switch inside the reach needs no Approach, nor anything else: switching it off is one step.
        var near = World(switchX: 0.8f);
        var (n, steps) = Run(planner, near, 3, This(Off), 4);
        Assert.Equal(1, n);
        Expect(steps, (SwitchOffV, 3, 0));

        Assert.Equal(1, Run(planner, World(switchX: 1f), 3, This(Off), 4).Length);
        Assert.Equal(4, Run(planner, World(switchX: 1.001f), 3, This(Off), 4).Length);
        Assert.Equal(1, Run(planner, World(switchX: 5.5f), 3, This(Off), 4, ox: 4.5f).Length);

        // An emitter's own bit 63 is never read: Sense refuses it.
        var p = Make();
        Assert.ThrowsAny<ArgumentException>(() => Sense(p, Thing(3, Switch, 5f, 1UL << Reach)));
        Assert.Equal(0, Occupied(p));
    }

    private static int Occupied(Perception p)
    {
        var n = 0;
        for (var s = 0; s < p.PerceptCount; s++) if (p.Percept(s).Id != 0) n++;
        return n;
    }

    [Fact]
    public void Self_facts_come_from_the_caller()
    {
        var planner = Build();
        var p = World();
        var (n, steps) = Run(planner, p, 3, This(Off), 4, self: 1UL << Free);
        Assert.Equal(2, n);
        Expect(steps, (ApproachV, 3, 0), (SwitchOffV, 3, 0));

        // The reach bit of self is cleared, not read: a caller passing it gets the same plan.
        (n, steps) = Run(planner, p, 3, This(Off), 4, self: (1UL << Free) | Perception.InReachFact);
        Assert.Equal(2, n);
    }

    [Fact]
    public void Remembered_facts_are_as_last_sensed()
    {
        var planner = Build();
        var p = Make();
        var self = 1UL << Free;

        // Door and held key within a metre; the door sensed locked.
        Sense(p, Thing(4, Key, 0.4f, 1UL << Held), Thing(5, Door, 0.5f, 1UL << Locked));
        var (n, steps) = Run(planner, p, 5, This(Open), 3, self: self);
        Assert.Equal(2, n);
        Expect(steps, (UnlockV, 5, 4), (OpenV, 5, 0));

        // Unsensed but still remembered while the game would now say unlocked: the plan still includes Unlock.
        for (var i = 0; i < 20; i++) Sense(p, Thing(4, Key, 0.4f, 1UL << Held));
        Assert.Equal(5UL, p.Percept(SlotOf(p, 5)).Id);
        (n, steps) = Run(planner, p, 5, This(Open), 3, self: self);
        Assert.Equal(2, n);
        Expect(steps, (UnlockV, 5, 4), (OpenV, 5, 0));

        // Re-sensed unlocked: Open alone.
        Sense(p, Thing(4, Key, 0.4f, 1UL << Held), Thing(5, Door, 0.5f, 0UL));
        (n, steps) = Run(planner, p, 5, This(Open), 3, self: self);
        Assert.Equal(1, n);
        Expect(steps, (OpenV, 5, 0));
    }

    [Fact]
    public void Tool_is_another_slot_of_an_accepted_kind()
    {
        var verbs = new[]
        {
            new Verb { Id = "Combine", This = Kinds(1), Tool = Kinds(1), Needs = new[] { Tool(Held) }, Gives = new[] { This(Cut) } },
        };
        var planner = new Planner(verbs, 8, 2, 1.0);

        // A thing is never its own tool.
        var p = Make();
        Sense(p, Thing(1, 1, 0f, 1UL << Held));
        Assert.Equal(-1, Run(planner, p, 1, This(Cut), 1).Length);

        // A second thing of the kind, holding: it grounds the Tool literal.
        var q = Make();
        Sense(q, Thing(1, 1, 0f), Thing(2, 1, 0f, 1UL << Held));
        var (n, steps) = Run(planner, q, 1, This(Cut), 1);
        Assert.Equal(1, n);
        Assert.Equal(new PlanStep(0, 1, 2), steps[0]);

        // A second thing of another kind is rejected by the Tool filter.
        var r = Make();
        Sense(r, Thing(1, 1, 0f), Thing(2, 9, 0f, 1UL << Held));
        Assert.Equal(-1, Run(planner, r, 1, This(Cut), 1).Length);

        // A free slot reads kind 0 and facts 0, and grounds nothing even where a filter accepts kind 0 and a need is a fact being false.
        var anyThing = new[] { new Verb { Id = "Idle", This = Kinds(0), Gives = new[] { Self(1) } } };
        Assert.Equal(-1, Run(new Planner(anyThing, 8, 1, 1.0), Make(), 0, Self(1), 1).Length);
        var lonelyTool = new[] { new Verb { Id = "Lone", This = Kinds(1), Tool = Kinds(0), Needs = new[] { Tool(Held, false) }, Gives = new[] { Self(1) } } };
        var lone = Make();
        Sense(lone, Thing(1, 1, 0f));
        Assert.Equal(-1, new Planner(lonelyTool, 8, 1, 1.0).Plan(lone, 0UL, 0f, 0f, 0f, 0, Self(1), 1, new PlanStep[1]));

        // A free slot grounds nothing, however its facts read: forgotten, the second thing is no tool.
        var s = Make(memory: 0.05);
        Sense(s, Thing(1, 1, 0f), Thing(2, 1, 0f, 1UL << Held));
        for (var i = 0; i < 20; i++) Sense(s, Thing(1, 1, 0f));
        Assert.Equal(0UL, s.Percept(1).Id);
        Assert.Equal(-1, Run(planner, s, 1, This(Cut), 1).Length);
    }

    [Fact]
    public void Construction_and_Plan_refuse_bad_input()
    {
        var good = Catalogue();
        _ = new Planner(good, 1, 0, 0.001);
        _ = new Planner(good, 256, 8, 1e9);
        _ = new Planner(Array.Empty<Verb>(), 4, 3, 1.0);

        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, -1, 1.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, 9, 1.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, 3, 0.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, 3, -1.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, 3, double.NaN));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 8, 3, double.PositiveInfinity));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 0, 3, 1.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(good, 257, 3, 1.0));
        Assert.ThrowsAny<ArgumentException>(() => new Planner(new Verb[] { null! }, 8, 3, 1.0));

        static void Refused(Verb v) => Assert.ThrowsAny<ArgumentException>(() => new Planner(new[] { v }, 8, 3, 1.0));
        var ok = new Verb { Gives = new[] { This(0) } };
        _ = new Planner(new[] { ok }, 8, 3, 1.0);
        Refused(new Verb());                                                                          // no Gives
        Refused(ok with { Needs = Many(9) });                                // more than 8 Needs
        Refused(ok with { Gives = Many(9) });                                // more than 8 Gives
        _ = new Planner(new[] { ok with { Needs = Many(8), Gives = Many(8) } }, 8, 3, 1.0);
        Refused(ok with { Gives = new[] { This(64) } });                                              // Bit above 63
        Refused(ok with { Gives = new[] { This(-1) } });                                              // Bit below 0
        Refused(ok with { Needs = new[] { This(64) } });
        Refused(ok with { Needs = new[] { Tool(0) } });                                               // Tool fact, no Tool filter
        Refused(ok with { Gives = new[] { Tool(0) } });
        _ = new Planner(new[] { ok with { Tool = Kinds(1), Gives = new[] { Tool(0) } } }, 8, 3, 1.0);
        Refused(ok with { Gives = new[] { Self(63) } });                                              // Self on bit 63
        Refused(ok with { Needs = new[] { Self(63, false) } });
        _ = new Planner(new[] { ok with { Gives = new[] { This(63) } } }, 8, 3, 1.0);                 // This on 63 is the reach
        Refused(ok with { Gives = new[] { This(1), This(1, false) } });                               // both values
        Refused(ok with { Needs = new[] { Self(2), Self(2, false) } });
        _ = new Planner(new[] { ok with { Needs = new[] { This(2), Self(2, false) } } }, 8, 3, 1.0);  // different subjects are different literals
        Refused(ok with { This = Kinds() });                                                          // filter with no kinds
        Refused(ok with { Tool = Kinds() });

        // Plan.
        var planner = new Planner(good, 8, 4, 1.0);
        var p = World();
        var steps = new PlanStep[8];
        var slot = SlotOf(p, 3);
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(Make(4), 0UL, 0f, 0f, 0f, 0, Self(Free), 1, steps));    // wrong capacity
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Off), -1, steps));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Off), 5, steps));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Off), 4, steps.AsSpan(0, 3)));
        Assert.Equal(4, planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Off), 4, steps.AsSpan(0, 4)));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, Tool(Off), 4, steps));          // want on Tool
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, Self(63), 4, steps));          // Self reach
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(64), 4, steps));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, 7, This(Off), 4, steps));            // free slot
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, -1, This(Off), 4, steps));
        Assert.ThrowsAny<ArgumentException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, 8, This(Off), 4, steps));
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, 7, Self(Held, false), 4, steps));                           // a Self want needs no target
        Assert.Equal(0, planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Reach, false), 4, steps));                      // This reach is a want
    }

    [Fact]
    public void Verb_lists_are_copied_and_read_only()
    {
        var needs = new[] { This(1) };
        var verb = new Verb { Needs = needs, Gives = new[] { This(2) } };
        needs[0] = This(9);
        Assert.Equal(This(1), verb.Needs[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Fact>)verb.Needs)[0] = This(3));
    }

    [Fact]
    public void Planning_allocates_nothing()
    {
        var planner = Build();
        var p = World();
        var steps = new PlanStep[8];
        var slot = SlotOf(p, 5);
        Assert.Equal(7, planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Open), 7, steps));
        var wrong = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2000; i++)
        {
            if (planner.Plan(p, 0UL, 0f, 0f, 0f, slot, This(Open), 7, steps) != 7) wrong++;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, wrong);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void A_chain_of_n_steps_is_found_at_depth_n_and_not_at_n_minus_one()
    {
        // Step k gives bit k on the target and needs bit k - 1: the want on bit n - 1 is exactly n steps away.
        var chain = Enumerable.Range(0, 4).Select(k => new Verb { Id = "Link" + k, Needs = k == 0 ? Array.Empty<Fact>() : new[] { This(k - 1) }, Gives = new[] { This(k) } }).ToArray();
        var p = Make();
        Sense(p, Thing(1, 1, 0f));
        var planner = new Planner(chain, 8, 8, 1.0);
        for (var n = 1; n <= 4; n++)
        {
            var (found, steps) = Run(planner, p, 1, This(n - 1), n);
            Assert.Equal(n, found);
            for (var k = 0; k < n; k++) Assert.Equal(new PlanStep(k, 1, 0), steps[k]);

            Assert.Equal(-1, Run(planner, p, 1, This(n - 1), n - 1).Length);
            Assert.Equal(n, Run(planner, p, 1, This(n - 1), 8).Length);
        }
    }

    [Fact]
    public void Ties_in_a_longer_plan_go_to_the_last_step_first()
    {
        // Slots 0 and 1 are things 30 and 10, one kind. Grab gives a thing Held; Use takes Held in its tool and gives Self 1, on a different thing.
        // The last step takes the lowest slot it can, which leaves slot 1 for Grab; taking the first step lowest would put Grab on slot 0.
        var verbs = new[]
        {
            new Verb { Id = "Grab", This = Kinds(2), Gives = new[] { This(Held) } },
            new Verb { Id = "Use", This = Kinds(2), Tool = Kinds(2), Needs = new[] { Tool(Held) }, Gives = new[] { Self(1) } },
        };
        var q = Make();
        Sense(q, Thing(30, 2, 0f), Thing(10, 2, 0f));
        var planner = new Planner(verbs, 8, 3, 1.0);
        var steps = new PlanStep[3];
        Assert.Equal(2, planner.Plan(q, 0UL, 0f, 0f, 0f, 0, Self(1), 3, steps));
        Expect(steps, (0, 10, 0), (1, 30, 10));
        Assert.Equal(-1, planner.Plan(q, 0UL, 0f, 0f, 0f, 0, Self(1), 1, steps));
    }

    [Fact]
    public void A_want_that_cannot_be_reached_is_given_up_within_the_budget_at_any_depth()
    {
        var planner = Build();
        var p = World();
        var steps = new PlanStep[8];
        foreach (var depth in new[] { 4, 5, 6 })
        {
            // The door cannot be opened in fewer than seven steps; relevance is what keeps the search from trying every verb on every thing.
            Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 5), This(Open), depth, steps));
            Assert.True(planner.LastNodes < Planner.NodeBudget / 8, $"depth {depth} regressed {planner.LastNodes} groundings");
        }

        // The longest honest search in this world, the seven-step door, uses a fraction of the budget.
        Assert.Equal(7, planner.Plan(p, 0UL, 0f, 0f, 0f, SlotOf(p, 5), This(Open), 8, steps));
        Assert.True(planner.LastNodes < Planner.NodeBudget / 4, $"the door took {planner.LastNodes} groundings");
    }

    [Fact]
    public void A_search_that_outgrows_the_budget_returns_no_plan_at_the_same_count_every_time()
    {
        // Two verbs ask each other's fact of any of eight things: every level is relevant and nothing ever grounds, so the tree is 16 wide.
        var verbs = new[]
        {
            new Verb { Id = "A", This = Kinds(1), Needs = new[] { Self(2) }, Gives = new[] { Self(1) } },
            new Verb { Id = "B", This = Kinds(1), Needs = new[] { Self(1) }, Gives = new[] { Self(2) } },
        };
        var p = Make();
        Sense(p, Enumerable.Range(1, 8).Select(i => Thing((ulong)i, 1, 0f)).ToArray());
        var planner = new Planner(verbs, 8, 8, 1.0);
        var steps = new PlanStep[8];
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, 0, Self(1), 8, steps));
            Assert.Equal(Planner.NodeBudget + 1, planner.LastNodes);
            Assert.True(planner.LastExhausted);
        }

        // The call's budget is the cut-off, not the constant.
        Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, 0, Self(1), 8, steps, budget: 100));
        Assert.Equal(101, planner.LastNodes);
        Assert.True(planner.LastExhausted);

        // A short plan in the same world is not touched by the budget the long one spent.
        Assert.Equal(1, planner.Plan(p, 1UL << 2, 0f, 0f, 0f, 0, Self(1), 8, steps));
        Assert.Equal(new PlanStep(0, 1, 0), steps[0]);
        Assert.False(planner.LastExhausted);

        // No plan within the depth is not a spent budget.
        Assert.Equal(-1, planner.Plan(p, 0UL, 0f, 0f, 0f, 0, Self(3), 8, steps));
        Assert.False(planner.LastExhausted);
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => planner.Plan(p, 0UL, 0f, 0f, 0f, 0, Self(1), 8, steps, budget: 0));

        // The switch plan needs four steps: a small budget gives up on it, the default finds it.
        var catalogue = Build();
        var world = World();
        var run = new PlanStep[8];
        Assert.Equal(-1, catalogue.Plan(world, 0UL, 0f, 0f, 0f, SlotOf(world, 3), This(Off), 8, run, budget: 5));
        Assert.True(catalogue.LastExhausted);
        Assert.Equal(4, catalogue.Plan(world, 0UL, 0f, 0f, 0f, SlotOf(world, 3), This(Off), 8, run));
        Assert.False(catalogue.LastExhausted);
    }

    // The test world's verbs but for what a test needs: Approach with no needs and Use of a Switch with a Knife that needs both in reach.
    private static Verb[] Movers() => new[]
    {
        new Verb { Id = "Approach", This = Kinds(Switch, Knife), Gives = new[] { This(Reach) } },
        new Verb { Id = "Use", This = Kinds(Switch), Tool = Kinds(Knife), Needs = new[] { This(Reach), Tool(Reach) }, Gives = new[] { This(Cut) } },
    };

    [Fact]
    public void A_move_takes_reach_of_far_things_away()
    {
        var planner = Build(Movers());
        var far = Make();
        Sense(far, Thing(1, Switch, 5f), Thing(2, Knife, -5f));
        Assert.Equal(-1, Run(planner, far, 1, This(Cut), 8).Length);
        Assert.False(planner.LastExhausted);

        var close = Make();
        Sense(close, Thing(1, Switch, 5f), Thing(2, Knife, 5.5f));
        var (n, steps) = Run(planner, close, 1, This(Cut), 8);
        Assert.Equal(2, n);
        Expect(steps, (0, 1, 0), (1, 1, 2));
    }

    [Fact]
    public void A_move_brings_near_things_into_reach()
    {
        // The key is not a thing Approach accepts, but a switch beside it is.
        var planner = Build(new[] { new Verb { Id = "Approach", This = Kinds(Switch), Gives = new[] { This(Reach) } } });
        var p = Make();
        Sense(p, Thing(1, Key, 5f), Thing(2, Switch, 5.3f));
        var (n, steps) = Run(planner, p, 1, This(Reach), 4);
        Assert.Equal(1, n);
        Expect(steps, (0, 2, 0));

        // Far from the key it brings nothing.
        var apart = Make();
        Sense(apart, Thing(1, Key, 5f), Thing(2, Switch, -5f));
        Assert.Equal(-1, Run(planner, apart, 1, This(Reach), 4).Length);

        // And out of reach is wanted just the same: going to the switch takes the key out of reach.
        var kept = Make();
        Sense(kept, Thing(1, Key, 0.5f), Thing(2, Switch, 5f));
        (n, steps) = Run(planner, kept, 1, This(Reach, false), 4);
        Assert.Equal(1, n);
        Expect(steps, (0, 2, 0));
    }

    [Fact]
    public void A_move_away_leaves_the_start_reach_behind()
    {
        var verbs = new[]
        {
            new Verb { Id = "Approach", This = Kinds(Switch, Knife), Gives = new[] { This(Reach) } },
            new Verb { Id = "SwitchOff", This = Kinds(Switch), Needs = new[] { This(Reach) }, Gives = new[] { This(Off) } },
            new Verb { Id = "Pry", This = Kinds(Knife), Tool = Kinds(Switch), Needs = new[] { Tool(Off), This(Reach) }, Gives = new[] { This(Cut) } },
        };
        var planner = Build(verbs);
        var p = Make();
        Sense(p, Thing(1, Knife, 0.5f), Thing(2, Switch, 5f));

        // The knife is in reach at the start, but not after going to the switch: a second Approach.
        var (n, steps) = Run(planner, p, 1, This(Cut), 8);
        Assert.Equal(4, n);
        Expect(steps, (0, 2, 0), (1, 2, 0), (0, 1, 0), (2, 1, 2));
    }
}
