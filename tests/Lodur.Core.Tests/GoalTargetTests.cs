using System;
using Lodur;

namespace Lodur.Core.Tests;

public sealed class GoalTargetTests
{
    private const double Dt = 0.01;
    private const uint Thing = 7;
    private const uint Other = 9;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();

    private static GoalSpec Goal(string id, double priority, TargetFilter? target = null, Consideration[]? considerations = null, params Condition[] when) => new()
    {
        Id = id, Priority = priority, When = when, Target = target, Considerations = considerations ?? Array.Empty<Consideration>(),
        Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
    };

    private static TargetFilter Kinds(params uint[] kinds) => new() { Kinds = kinds };

    private static Consideration Nearer(double far) => new(ConsiderationInput.Distance, 0.0, far, 1.0, 0.0);

    private static Emitter At(ulong id, float x, uint kind = Thing, double intensity = 1.0, float radius = 20f) =>
        new(Channel.Audio, x, 0f, 0f, radius, intensity, id, kind);

    private static Perception Ears(int capacity = 4, double memory = 5.0) =>
        new(new NerveParameters(), Dt, 1, new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 },
            0, new PerceptParameters { Capacity = capacity, MemorySeconds = memory });

    private static Perception Hearing(params Emitter[] emitters)
    {
        var p = Ears();
        p.Sense(Zero, Zero, Zero, NoBoxes, emitters, 1.0);
        return p;
    }

    private static Choice Pick(Brain brain, Perception p, Choice current, DriveState drive = default) => brain.Select(drive, p, 0f, 0f, 0f, current);

    private static int SlotOf(Perception p, ulong id)
    {
        for (var s = 0; s < p.PerceptCount; s++)
        {
            if (p.Percept(s).Id == id) return s;
        }

        return -1;
    }

    [Fact]
    public void An_untargeted_brain_selects_as_before()
    {
        var calm = default(DriveState);
        var always = new Condition(DriveQuantity.Pain, ConditionOp.Below, 1.0);
        var hears = Hearing(At(1, 1f));
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("a", 2.0, when: always), Goal("b", 5.0, when: always), Goal("c", 5.0, when: always),
                Goal("d", 9.0, when: new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.5)),
            },
        };
        // Percepts in the room change nothing for goals that name no target.
        Assert.Equal(new Choice(1, 0), Pick(brain, hears, new Choice(0, 0), calm));
        Assert.Equal(new Choice(2, 0), Pick(brain, hears, new Choice(2, 0), calm));
        Assert.Equal(new Choice(1, 0), Pick(brain, hears, new Choice(3, 0), calm));
        var none = new Brain { Goals = new[] { Goal("x", 1.0, when: new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.5)) } };
        Assert.Equal(new Choice(0, 77), Pick(none, hears, new Choice(0, 77), calm));
    }

    [Fact]
    public void A_targeted_goal_with_no_percept_of_its_kinds_is_not_chosen()
    {
        var brain = new Brain { Goals = new[] { Goal("idle", 1.0), Goal("chase", 5.0, Kinds(Other)) } };
        // A percept of another kind, and free slots (Id 0), make no pair.
        var wrongKind = Hearing(At(1, 1f, Thing));
        Assert.Equal(new Choice(0, 0), Pick(brain, wrongKind, new Choice(0, 0)));
        Assert.Equal(new Choice(0, 0), Pick(brain, Ears(), new Choice(0, 0)));

        var rightKind = Hearing(At(1, 1f, Thing), At(2, 3f, Other));
        Assert.Equal(new Choice(1, 2), Pick(brain, rightKind, new Choice(0, 0)));

        // Only targeted goals, none with a percept: the current choice stays.
        var only = new Brain { Goals = new[] { Goal("chase", 5.0, Kinds(Other)) } };
        Assert.Equal(new Choice(0, 0), Pick(only, wrongKind, new Choice(0, 0)));
        Assert.Equal(new Choice(0, 55), Pick(only, wrongKind, new Choice(0, 55)));
    }

    [Fact]
    public void Considerations_multiply_priority_and_the_best_pair_wins()
    {
        // Distance: the nearer of two things.
        var near = Hearing(At(1, 6f), At(2, 2f));
        var go = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(10.0) }) } };
        Assert.Equal(new Choice(0, 2), Pick(go, near, default));

        // Salience: the louder thing wins when the curve rises; the quieter when it falls.
        var loud = Hearing(At(1, 1f, intensity: 0.2), At(2, 1f, intensity: 0.9));
        var rising = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Salience, 0.0, 1.0, 0.0, 1.0) }) } };
        var falling = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Salience, 0.0, 1.0, 1.0, 0.0) }) } };
        Assert.Equal(2UL, Pick(rising, loud, default).Target);
        Assert.Equal(1UL, Pick(falling, loud, default).Target);

        // Priority times the Drive curve: a goal at priority 4 whose Arousal curve zeroes it loses to priority 1.
        var drained = new DriveState { Arousal = 0.0 };
        var duel = new Brain
        {
            Goals = new[]
            {
                Goal("brave", 4.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Drive, 0.0, 1.0, 0.0, 1.0, 1.0, DriveQuantity.Arousal) }),
                Goal("plain", 1.0, Kinds(Thing)),
            },
        };
        Assert.Equal(1, Pick(duel, near, default, drained).Goal);
        Assert.Equal(0, Pick(duel, near, default, new DriveState { Arousal = 1.0 }).Goal);
        Assert.Equal(0, Pick(duel, near, default, new DriveState { Arousal = 0.5 }).Goal); // 2.0 > 1.0

        // The curve itself: clamped, shaped by the exponent, inverted by Y0 > Y1.
        var c = new Consideration(ConsiderationInput.Salience, 2.0, 4.0, 0.0, 1.0, 2.0);
        Assert.Equal(0.0, c.Response(1.0));
        Assert.Equal(0.25, c.Response(3.0));
        Assert.Equal(1.0, c.Response(9.0));
    }

    [Fact]
    public void Ties_go_to_the_current_pair_then_lowest_goal_then_lowest_slot()
    {
        // Equal distance on either side of the observer: two equal scores in slots 0 and 1.
        var twins = Hearing(At(10, 3f), At(20, -3f));
        Assert.True(SlotOf(twins, 10) < SlotOf(twins, 20));
        var one = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(10.0) }) } };
        Assert.Equal(new Choice(0, 10), Pick(one, twins, default));                  // lowest slot
        Assert.Equal(new Choice(0, 20), Pick(one, twins, new Choice(0, 20)));        // the current pair
        Assert.Equal(new Choice(0, 10), Pick(one, twins, new Choice(0, 10)));

        var two = new Brain { Goals = new[] { Goal("a", 1.0, Kinds(Thing), new[] { Nearer(10.0) }), Goal("b", 1.0, Kinds(Thing), new[] { Nearer(10.0) }) } };
        Assert.Equal(new Choice(0, 10), Pick(two, twins, new Choice(1, 99)));        // lowest goal, then slot
        Assert.Equal(new Choice(1, 20), Pick(two, twins, new Choice(1, 20)));        // current beats the lower goal
    }

    [Fact]
    public void Commitment_holds_the_current_target_until_a_challenger_beats_it_by_the_multiplier()
    {
        // Distance 2 scores 0.75, distance 4 scores 0.5 on a curve over 8.
        var hears = Hearing(At(1, 4f), At(2, 2f));
        Brain With(double commitment) => new() { TargetCommitment = commitment, Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(8.0) }) } };
        Assert.Equal(2UL, Pick(With(1.0), hears, new Choice(0, 1)).Target);   // no commitment: the nearer wins
        Assert.Equal(2UL, Pick(With(1.4), hears, new Choice(0, 1)).Target);   // 0.5 * 1.4 = 0.70 < 0.75
        Assert.Equal(1UL, Pick(With(1.5), hears, new Choice(0, 1)).Target);   // 0.75 tie goes to the current pair
        Assert.Equal(1UL, Pick(With(2.0), hears, new Choice(0, 1)).Target);
        Assert.Equal(2UL, Pick(With(2.0), hears, default).Target);            // the multiplier is for the current pair only

        // It does not carry over to another goal's pair on the same target.
        var rival = new Brain
        {
            TargetCommitment = 2.0,
            Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(8.0) }), Goal("flee", 1.2, Kinds(Thing), new[] { Nearer(8.0) }) },
        };
        Assert.Equal(new Choice(0, 1), Pick(rival, hears, new Choice(0, 1)));    // 0.5 * 2.0 = 1.0 beats flee's 1.2 * 0.75 = 0.9, on the same thing or the nearer
        Assert.Equal(new Choice(1, 2), Pick(rival, hears, default));              // uncommitted, flee on the nearer wins
    }

    private static Actor Hunter(Brain brain, int capacity = 4, double memory = 5.0) =>
        new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = capacity, MemorySeconds = memory });

    private static TickEvents Tick(Actor a, params Emitter[] emitters) =>
        a.Tick(Zero, Zero, Zero, NoBoxes, new float[2], new Quat[2], emitters);

    [Fact]
    public void A_retarget_waits_the_latency_and_fires_no_goal_change_or_entry_effect()
    {
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("idle", 0.0),
                new GoalSpec
                {
                    Id = "chase", Priority = 1.0, EntryResolve = 0.9, Effort = 0.1, Target = Kinds(Thing), Considerations = new[] { Nearer(30.0) },
                    When = new[] { new Condition(DriveQuantity.Pain, ConditionOp.Below, 1.0) },
                    Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
                },
            },
        };
        var a = Hunter(brain);
        // Takes over the goal and the nearer thing, a latency after it is first heard.
        var changes = 0;
        for (var i = 0; i < 40; i++) changes += (Tick(a, At(1, 5f), At(2, 9f)) & TickEvents.GoalChanged) != 0 ? 1 : 0;
        Assert.Equal(1, changes);
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(1UL, a.TargetId);
        Assert.Equal(1UL, a.Target.Id);

        for (var i = 0; i < 50; i++) Tick(a, At(1, 5f), At(2, 9f));
        var inGoal = a.SecondsInGoal;
        var resolve = a.Drive.Resolve;
        Assert.True(resolve < 0.89, "the goal's effort should have drained resolve below its entry value by now");

        // Thing 2 comes closer than thing 1: the pair changes a latency later, and nothing else does.
        long first = -1;
        for (var i = 0; i < 30; i++)
        {
            var e = Tick(a, At(1, 5f), At(2, 1f));
            Assert.Equal(TickEvents.None, e & TickEvents.GoalChanged);
            if (first < 0 && a.TargetId == 2) first = i;
            if (first < 0)
            {
                Assert.Equal(1UL, a.TargetId);
                Assert.Equal(1, a.GoalIndex);
            }
        }

        Assert.Equal(10, first); // Latency: round(0.1 s / 10 ms)
        Assert.Equal(2UL, a.Target.Id);
        Assert.Equal(inGoal + 30 * Dt, a.SecondsInGoal, 9);
        Assert.True(a.Drive.Resolve < resolve, "EntryResolve must not be applied again by a retarget: resolve keeps draining");
    }

    private static Emitter AtXyz(ulong id, float x, float y, float z, double intensity = 1.0) =>
        new(Channel.Audio, x, y, z, 40f, intensity, id, Thing);

    private static Perception HearingAt(params Emitter[] emitters)
    {
        var p = Ears(8);
        p.Sense(Zero, Zero, Zero, NoBoxes, emitters, 1.0);
        return p;
    }

    private static ulong Nearest(Perception p, float ox, float oy, float oz)
    {
        var brain = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(60.0) }) } };
        return brain.Select(default, p, ox, oy, oz, default).Target;
    }

    [Fact]
    public void Distance_is_euclidean_over_all_three_axes_from_the_observer_point()
    {
        // Each pair ranks differently under a dropped axis, a Manhattan sum, or a mixed-up observer coordinate.
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 2f, 0f, 0f), AtXyz(2, 1f, 5f, 0f)), 0f, 0f, 0f));   // dy matters
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 2f, 0f, 0f), AtXyz(2, 1f, 0f, 5f)), 0f, 0f, 0f));   // dz matters
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 0f, 2f, 0f), AtXyz(2, 5f, 1f, 0f)), 0f, 0f, 0f));   // dx matters
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 3f, 3f, 0f), AtXyz(2, 5f, 0f, 0f)), 0f, 0f, 0f));   // 4.24 < 5, though 6 > 5
        // The observer is a point, each coordinate its own: swapping any two moves the answer.
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 6f, 3f, 0f), AtXyz(2, 3f, 1f, 0f)), 6f, 1f, 0f));   // x with y
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 6f, 0f, 3f), AtXyz(2, 3f, 0f, 1f)), 6f, 0f, 1f));   // x with z
        Assert.Equal(1UL, Nearest(HearingAt(AtXyz(1, 0f, 6f, 3f), AtXyz(2, 0f, 3f, 1f)), 0f, 6f, 1f));   // y with z
    }

    [Fact]
    public void A_percept_is_scored_by_its_age_and_by_every_consideration()
    {
        // Thing 1 (slot 0) falls silent while thing 2 keeps sounding: the fresher thing wins on a falling age curve.
        var p = Ears(4, 5.0);
        p.Sense(Zero, Zero, Zero, NoBoxes, new[] { At(1, 3f), At(2, 3f) }, 1.0);
        for (var i = 0; i < 100; i++) p.Sense(Zero, Zero, Zero, NoBoxes, new[] { At(2, 3f) }, 1.0);
        Assert.True(p.Percept(SlotOf(p, 1)).SecondsSinceSensed > 0.9);
        var young = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.SecondsSinceSensed, 0.0, 5.0, 1.0, 0.0) }) } };
        Assert.Equal(2UL, Pick(young, p, default).Target);
        var stale = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.SecondsSinceSensed, 0.0, 5.0, 0.0, 1.0) }) } };
        Assert.Equal(1UL, Pick(stale, p, default).Target);

        // Two considerations multiply: the nearer thing is quiet, the farther loud; the second curve decides.
        var mixed = Hearing(At(1, 2f, intensity: 0.05), At(2, 6f, intensity: 1.0));
        Assert.Equal(1UL, Pick(new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(10.0) }) } }, mixed, default).Target);
        var both = new Brain { Goals = new[] { Goal("go", 1.0, Kinds(Thing), new[] { Nearer(10.0), new Consideration(ConsiderationInput.Salience, 0.0, 1.0, 0.0, 1.0) }) } };
        Assert.Equal(2UL, Pick(both, mixed, default).Target);
    }

    [Fact]
    public void A_filter_holds_several_kinds_and_a_free_slot_is_never_a_target_even_of_kind_zero()
    {
        var brain = new Brain { Goals = new[] { Goal("idle", 1.0), Goal("chase", 5.0, Kinds(Other, Thing)) } };
        Assert.Equal(new Choice(1, 1), Pick(brain, Hearing(At(1, 2f, Thing)), default));
        var rev = new Brain { Goals = new[] { Goal("idle", 1.0), Goal("chase", 5.0, Kinds(Thing, Other)) } };
        Assert.Equal(new Choice(1, 1), Pick(rev, Hearing(At(1, 2f, Other)), default));

        // Free slots read Kind 0 and Id 0; a filter naming kind 0 still finds nothing in them.
        var zero = new Brain { Goals = new[] { Goal("idle", 1.0), Goal("chase", 5.0, Kinds(0u)) } };
        Assert.Equal(new Choice(0, 0), Pick(zero, Ears(), new Choice(0, 0)));
        Assert.Equal(new Choice(0, 0), Pick(zero, Hearing(At(1, 2f, Thing)), new Choice(0, 0)));
    }

    private static Actor HunterAt(Brain brain, int regions, int senseRegion) =>
        new(Dt, regions, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, senseRegion,
            new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 });

    [Fact]
    public void The_actor_measures_distance_from_the_sense_region_point()
    {
        var brain = new Brain { Goals = new[] { Goal("idle", 0.0), Goal("chase", 1.0, Kinds(Thing), new[] { Nearer(60.0) }) } };
        var a = HunterAt(brain, 2, 1);
        var x = new[] { 0f, 10f };
        var y = new[] { 0f, 3f };
        var z = new[] { 0f, 2f };
        // Thing 1 is one unit from the sense region's point. Each of the others sits on the point a wrong reading would use:
        // region 0, the point with y and z zeroed, and the point with x and y swapped.
        var emitters = new[] { AtXyz(1, 11f, 3f, 2f), AtXyz(2, 0f, 0f, 0f), AtXyz(3, 10f, 0f, 0f), AtXyz(4, 3f, 10f, 2f) };
        for (var i = 0; i < 40; i++) a.Tick(x, y, z, NoBoxes, new float[2], new Quat[2], emitters);
        Assert.Equal(1UL, a.TargetId);
    }

    [Fact]
    public void A_goal_without_a_target_clears_the_target_and_a_retarget_ignores_the_goals_dwell()
    {
        var brain = new Brain { Goals = new[] { Goal("idle", 0.0), Goal("chase", 1.0, Kinds(Thing), new[] { Nearer(30.0) }) } };
        var a = Hunter(brain, memory: 0.2);
        for (var i = 0; i < 40; i++) Tick(a, At(1, 5f));
        Assert.Equal(1, a.GoalIndex);
        Assert.Equal(1UL, a.TargetId);
        // The thing is forgotten; only the untargeted goal has a pair, and it takes no target with it.
        for (var i = 0; i < 80; i++) Tick(a);
        Assert.Equal(0, a.GoalIndex);
        Assert.Equal(0UL, a.TargetId);

        // Within a long dwell, another thing in the same goal is taken a latency later all the same.
        var held = new Brain
        {
            Goals = new[]
            {
                Goal("idle", 0.0),
                new GoalSpec
                {
                    Id = "chase", Priority = 1.0, MinDwellSeconds = 5.0, Target = Kinds(Thing), Considerations = new[] { Nearer(30.0) },
                    Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
                },
            },
        };
        var b = Hunter(held);
        for (var i = 0; i < 40; i++) Tick(b, At(1, 5f), At(2, 9f));
        Assert.Equal(1UL, b.TargetId);
        for (var i = 0; i < 30; i++) Tick(b, At(1, 5f), At(2, 1f));
        Assert.Equal(2UL, b.TargetId);
        Assert.True(b.SecondsInGoal < 5.0);
    }

    [Fact]
    public void A_forgotten_target_reads_id_zero_and_the_brain_reselects()
    {
        var brain = new Brain { Goals = new[] { Goal("chase", 1.0, Kinds(Thing), new[] { Nearer(30.0) }) } };
        var a = Hunter(brain, capacity: 2, memory: 0.2);
        for (var i = 0; i < 30; i++) Tick(a, At(1, 2f), At(2, 9f));
        Assert.Equal(1UL, a.TargetId);

        // Thing 1 stops sounding and is forgotten after its memory; its slot is taken by thing 3, which must not be read as the target.
        var window = false;
        for (var i = 0; i < 120 && a.TargetId == 1; i++)
        {
            Tick(a, At(2, 9f), At(3, 15f));
            if (a.TargetId == 1 && a.Target.Id == 0) window = true;
            Assert.NotEqual(3UL, a.Target.Id);
        }

        Assert.True(window, "the committed id outlived its percept, which should read as the default percept");
        Assert.Equal(2UL, a.TargetId);
        Assert.Equal(2UL, a.Target.Id);
    }

    [Fact]
    public void Target_filters_and_considerations_are_validated()
    {
        static Actor Make(GoalSpec g, double commitment = 1.0) => new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            new Brain { Goals = new[] { g }, TargetCommitment = commitment });
        var ok = Goal("ok", 1.0, Kinds(Thing), new[] { Nearer(5.0) });
        Make(ok);

        var bad = new Action[]
        {
            () => Make(Goal("g", 1.0, Kinds())),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 0.0, double.NaN, 0.0, 1.0) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, double.PositiveInfinity, 1.0, 0.0, 1.0) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 2.0, 2.0, 0.0, 1.0) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 0.0, 1.0, -0.1, 1.0) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 0.0, 1.0, 0.0, 1.1) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 0.0, 1.0, 0.0, 1.0, 0.0) })),
            () => Make(Goal("g", 1.0, Kinds(Thing), new[] { new Consideration(ConsiderationInput.Distance, 0.0, 1.0, 0.0, 1.0, double.NaN) })),
            () => Make(Goal("g", 1.0, null, new[] { Nearer(5.0) })),
            () => Make(Goal("g", 1.0, null, new[] { new Consideration(ConsiderationInput.Salience, 0.0, 1.0, 0.0, 1.0) })),
            () => Make(Goal("g", 1.0, null, new[] { new Consideration(ConsiderationInput.SecondsSinceSensed, 0.0, 1.0, 0.0, 1.0) })),
            () => Make(Goal("g", -1.0, Kinds(Thing))),
            () => Make(Goal("g", -1.0, null, new[] { new Consideration(ConsiderationInput.Drive, 0.0, 1.0, 0.0, 1.0) })),
            () => Make(ok, 0.99),
            () => Make(ok, double.NaN),
            () => Make(ok, double.PositiveInfinity),
        };
        for (var i = 0; i < bad.Length; i++)
        {
            Assert.True(Throws(bad[i]), $"case {i} was accepted");
        }

        // A negative priority stays legal on a goal with neither.
        Make(Goal("g", -1.0));
        Make(Goal("g", 1.0, null, new[] { new Consideration(ConsiderationInput.Drive, 0.0, 1.0, 0.0, 1.0) }));

        static bool Throws(Action a)
        {
            try { a(); return false; }
            catch (ArgumentException e) { return e.Message.Contains("g") || e.Message.Contains("TargetCommitment"); }
        }
    }

    [Fact]
    public void Ticking_with_targets_allocates_nothing()
    {
        var brain = new Brain
        {
            TargetCommitment = 1.2,
            Goals = new[]
            {
                Goal("idle", 0.0),
                Goal("chase", 1.0, Kinds(Thing), new[] { Nearer(30.0), new Consideration(ConsiderationInput.Salience, 0.0, 1.0, 0.2, 1.0), new Consideration(ConsiderationInput.Drive, 0.0, 1.0, 0.5, 1.0, 1.0, DriveQuantity.Arousal) }),
            },
        };
        var a = Hunter(brain);
        var emitters = new[] { At(1, 5f), At(2, 9f), At(3, 2f, Other) };
        for (var i = 0; i < 50; i++) a.Tick(Zero, Zero, Zero, NoBoxes, new float[2], new Quat[2], emitters);
        var strengths = new float[2];
        var targets = new Quat[2];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            emitters[1] = At(2, 9f - (i % 8));
            a.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, emitters);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0UL, a.TargetId);
    }
}
