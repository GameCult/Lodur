using System;
using System.Collections.Generic;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// A futile direct try ends (ruling bds-unplannable-tries-direct): after FrustrationSeconds at PlanLength -1 the Actor bars the pair's direct try, reports Frustrated and decides again
/// past the dwell; the bar ends when the target is forgotten or after RetrySeconds, and a pair that becomes plannable is offered through it; a changed fact does not lift it (ruling frustration-lifts-without-fact-changes).
/// </summary>
public sealed class PlannerFrustrationTests
{
    private const uint Door = 1, Knife = 3;
    private const int Open = 0, Locked = 1, Latched = 2, Held = 3, Fed = 4, Noise = 5;
    private const int Reach = 63;
    private const double Dt = 0.01;
    private const int Latency = 10;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();
    private static readonly Emitter[] None = Array.Empty<Emitter>();

    private static Fact Self(int bit, bool v = true) => new(FactSubject.Self, bit, v);
    private static Fact This(int bit, bool v = true) => new(FactSubject.This, bit, v);
    private static Fact Tool(int bit, bool v = true) => new(FactSubject.Tool, bit, v);
    private static TargetFilter Kinds(params uint[] kinds) => new() { Kinds = kinds };

    private static Verb[] Catalogue() => new[]
    {
        new Verb { Id = "Approach", Gives = new[] { This(Reach) } },
        new Verb { Id = "Unlock", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked) }, Gives = new[] { This(Locked, false) } },
        new Verb { Id = "Unlatch", This = Kinds(Door), Needs = new[] { This(Reach), This(Latched) }, Gives = new[] { This(Latched, false) } },
        new Verb { Id = "Open", This = Kinds(Door), Needs = new[] { This(Reach), This(Locked, false), This(Latched, false) }, Gives = new[] { This(Open) } },
        new Verb { Id = "Calm", This = Kinds(Knife), Needs = new[] { This(Reach) }, Gives = new[] { Self(Fed) } },
    };

    // A knife held in the hand pries any door open in two steps (approach, pry); with no knife there is no plan at all.
    private static Verb[] WithPry() => new[]
    {
        new Verb { Id = "Approach", Gives = new[] { This(Reach) } },
        new Verb { Id = "Pry", This = Kinds(Door), Tool = Kinds(Knife), Needs = new[] { Tool(Held), This(Reach) }, Gives = new[] { This(Open) } },
    };

    private static GoalSpec Goal(string id, double priority, TargetFilter? target = null, Fact? want = null, double dwell = 0.0) => new()
    {
        Id = id, Priority = priority, Target = target, Want = want, MinDwellSeconds = dwell,
        Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
    };

    private static Emitter Thing(ulong id, uint kind, float x, ulong facts = 0UL) =>
        new(Channel.Visual, x, 0f, 0f, 100f, 1.0, id, kind, facts);

    private static Emitter BoltedDoor(ulong facts = 0UL) => Thing(1, Door, 6f, (1UL << Locked) | (1UL << Latched) | facts);

    // Idle (1) against the door goal (3, direct at the discount 0.5: 1.5); the bolted door is four steps from open, past a depth of 3.
    private static Brain Make(double frustration = 1.0, double retry = 30.0, int depth = 3, double dwell = 0.0) => new()
    {
        PlanDepth = depth, DirectDiscount = 0.5, FrustrationSeconds = frustration, RetrySeconds = retry,
        Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open), dwell) },
    };

    private static Actor Build(Brain brain, double memory = 5.0, IReadOnlyList<Verb>? verbs = null) =>
        new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = 8, MemorySeconds = memory }, verbs: verbs ?? Catalogue());

    private sealed class Bench
    {
        public readonly Actor A;
        public readonly float[] Strengths = new float[2];
        public readonly Quat[] Targets = new Quat[2];
        public readonly List<long> Frustrated = new();

        public Bench(Actor a) => A = a;

        public long Now => A.TickCount;

        public TickEvents Step(Emitter[] emitters, ulong selfFacts = 0UL)
        {
            var t = A.TickCount;
            var events = A.Tick(Zero, Zero, Zero, NoBoxes, Strengths, Targets, emitters, selfFacts: selfFacts);
            if ((events & TickEvents.Frustrated) != 0)
            {
                Frustrated.Add(t);
            }

            return events;
        }

        public void Run(int ticks, Emitter[] emitters, ulong selfFacts = 0UL)
        {
            for (var i = 0; i < ticks; i++)
            {
                Step(emitters, selfFacts);
            }
        }

        // Runs until the first tick that leaves PlanLength -1 and returns that tick's number (the pair's first direct tick).
        public long RunToDirect(Emitter[] emitters, ulong selfFacts = 0UL)
        {
            for (var i = 0; i < 500; i++)
            {
                var t = A.TickCount;
                Step(emitters, selfFacts);
                if (A.PlanLength == -1)
                {
                    return t;
                }
            }

            throw new InvalidOperationException("He never went direct.");
        }

        // Runs until Frustrated and returns its tick.
        public long RunToFrustration(Emitter[] emitters, ulong selfFacts = 0UL)
        {
            for (var i = 0; i < 2000; i++)
            {
                if ((Step(emitters, selfFacts) & TickEvents.Frustrated) != 0)
                {
                    return Frustrated[^1];
                }
            }

            throw new InvalidOperationException("He never frustrated.");
        }
    }

    [Fact]
    public void A_bolted_door_tried_direct_frustrates_exactly_on_time()
    {
        var b = new Bench(Build(Make()));
        var door = new[] { BoltedDoor() };
        var direct = b.RunToDirect(door);
        Assert.Equal(1, b.A.GoalIndex);
        Assert.Equal(-1, b.A.PlanLength);
        var frustrated = direct + 100;                       // FrustrationSeconds 1.0 is 100 ticks
        while (b.Now < frustrated)                           // none a tick early
        {
            Assert.Equal(TickEvents.None, b.Step(door) & TickEvents.Frustrated);
            Assert.Equal(1, b.A.GoalIndex);
        }

        Assert.Equal(TickEvents.Frustrated, b.Step(door) & TickEvents.Frustrated);   // once, on the tick
        Assert.Equal(new[] { frustrated }, b.Frustrated);
        for (var i = 0; i < Latency - 1; i++)
        {
            b.Step(door);
            Assert.Equal(1, b.A.GoalIndex);                  // the decision commits a latency later, not before
        }

        b.Step(door);
        Assert.Equal(0, b.A.GoalIndex);
        Assert.Equal(0UL, b.A.TargetId);
        b.Run(300, door);
        Assert.Equal(0, b.A.GoalIndex);                      // barred: he does not go back
        Assert.Single(b.Frustrated);
    }

    [Fact]
    public void Frustration_decides_again_past_the_dwell()
    {
        var b = new Bench(Build(Make(dwell: 60.0)));
        var door = new[] { BoltedDoor() };
        b.RunToFrustration(door);
        b.Run(Latency, door);
        Assert.Equal(0, b.A.GoalIndex);                      // MinDwellSeconds 60 would have held the goal for 6000 ticks
    }

    [Fact]
    public void Alone_he_lets_go_of_the_target_and_does_not_frustrate_again()
    {
        var brain = new Brain
        {
            PlanDepth = 3, FrustrationSeconds = 1.0, RetrySeconds = 30.0,
            Goals = new[] { Goal("open", 3.0, Kinds(Door), This(Open)) },
        };
        var b = new Bench(Build(brain));
        var door = new[] { BoltedDoor() };
        b.RunToFrustration(door);
        b.Run(Latency, door);
        Assert.Equal(0, b.A.GoalIndex);
        Assert.Equal(0UL, b.A.TargetId);
        Assert.Equal(0, b.A.PlanLength);
        b.Run(1000, door);
        Assert.Equal(0UL, b.A.TargetId);
        Assert.Single(b.Frustrated);
    }

    [Fact]
    public void The_door_is_tried_again_after_the_retry()
    {
        var b = new Bench(Build(Make(retry: 2.0)));
        var door = new[] { BoltedDoor() };
        var first = b.RunToFrustration(door);
        b.Run(1000, door);
        // The bar lifts 200 ticks on, the decision commits a latency later, and 100 ticks of direct try end in the next frustration.
        Assert.Equal(first + 200 + Latency + 100, b.Frustrated[1]);
        Assert.Equal(first + 2 * (200 + Latency + 100), b.Frustrated[2]);
    }

    [Fact]
    public void Infinite_seconds_never_frustrate_and_never_retry()
    {
        var door = new[] { BoltedDoor() };
        var never = new Bench(Build(Make(frustration: double.PositiveInfinity)));
        never.Run(3000, door);
        Assert.Empty(never.Frustrated);
        Assert.Equal(1, never.A.GoalIndex);
        Assert.Equal(-1, never.A.PlanLength);

        var stuck = new Bench(Build(Make(retry: double.PositiveInfinity)));
        stuck.Run(5000, door);
        Assert.Single(stuck.Frustrated);
        Assert.Equal(0, stuck.A.GoalIndex);
    }

    [Fact]
    public void A_changed_fact_or_self_fact_does_not_lift_the_bar_but_a_forgotten_door_does()
    {
        // Barred, idle in force; a change of the door's facts or his own leaves the bar standing (ruling frustration-lifts-without-fact-changes).
        void Holds(Action<Bench, Emitter[]> change)
        {
            var b = new Bench(Build(Make(), memory: 0.5));
            var door = new[] { BoltedDoor() };
            b.RunToFrustration(door);
            b.Run(Latency + 20, door);
            Assert.Equal(0, b.A.GoalIndex);
            change(b, door);
            b.Run(Latency + 200, new[] { BoltedDoor(1UL << Noise) }, 1UL << Noise);
            Assert.Equal(0, b.A.GoalIndex);
            Assert.Single(b.Frustrated);
        }

        Holds((b, _) => b.Run(Latency + 2, new[] { BoltedDoor(1UL << Noise) }));      // an unrelated bit of the door's facts
        Holds((b, door) => b.Run(Latency + 2, door, 1UL << Noise));                   // an unrelated self fact

        // The door leaving memory (0.5 s) ends the bar: sensed again, unchanged, the try resumes.
        var f = new Bench(Build(Make(), memory: 0.5));
        var same = new[] { BoltedDoor() };
        f.RunToFrustration(same);
        f.Run(Latency + 20, same);
        Assert.Equal(0, f.A.GoalIndex);
        f.Run(60, None);
        f.Run(Latency + 2, same);
        Assert.Equal(1, f.A.GoalIndex);
        Assert.Equal(1UL, f.A.TargetId);
    }

    [Fact]
    public void A_fact_that_flips_for_200_seconds_frustrates_a_bounded_number_of_times()
    {
        // His own futile try may rattle the door's facts; with the bar standing the count is that of the retries (30 s), not of the flips (every 0.5 s).
        var b = new Bench(Build(Make(frustration: 1.0, retry: 30.0)));
        for (var i = 0; i < 20000; i++)
        {
            b.Step(new[] { BoltedDoor((i / 50) % 2 == 0 ? 0UL : 1UL << Noise) });
        }

        Assert.InRange(b.Frustrated.Count, 1, 7);
    }

    [Fact]
    public void A_barred_pair_that_now_plans_is_offered()
    {
        var b = new Bench(Build(Make(), verbs: WithPry()));
        var door = new[] { BoltedDoor() };
        b.RunToFrustration(door);
        b.Run(Latency + 20, door);
        Assert.Equal(0, b.A.GoalIndex);
        // A held knife appears: the door's facts and his own are unchanged, so the bar stands, but the pair now plans (approach, pry) and is not a direct try.
        b.Run(Latency + 2, new[] { BoltedDoor(), Thing(2, Knife, 1f, 1UL << Held) });
        Assert.Equal(1, b.A.GoalIndex);
        Assert.Equal(1UL, b.A.TargetId);
        Assert.Equal(2, b.A.PlanLength);
        Assert.Single(b.Frustrated);
    }

    [Fact]
    public void Impatience_lasts_one_select()
    {
        // Calm and idle tie on priority (1), so a switch between them is never accepted on priority; calm scores 1 direct, idle 0.9.
        var brain = new Brain
        {
            PlanDepth = 3, DirectDiscount = 1.0, FrustrationSeconds = 1.0, RetrySeconds = 30.0, Start = 0,
            Goals = new[]
            {
                Goal("calm", 1.0, null, Self(Fed), 60.0),
                Goal("idle", 1.0, dwell: 60.0) with { Considerations = new[] { new Consideration(ConsiderationInput.Drive, 0.0, 1.0, 0.9, 0.9, 1.0, DriveQuantity.Pain) } },
            },
        };
        var b = new Bench(Build(brain));
        b.RunToFrustration(None);
        b.Run(Latency, None);
        Assert.Equal(1, b.A.GoalIndex);                      // past calm's 60 s dwell, by impatience
        b.Run(Latency + 2, None, 1UL << Held);               // the bar lifts, calm scores 1 against idle's 0.9, but idle's own dwell holds and there is no impatience left
        Assert.Equal(1, b.A.GoalIndex);
    }

    [Fact]
    public void The_timer_starts_again_when_one_direct_try_replaces_another()
    {
        var brain = new Brain
        {
            PlanDepth = 3, DirectDiscount = 0.5, FrustrationSeconds = 1.0, RetrySeconds = 30.0,
            Goals = new[] { Goal("idle", 1.0), Goal("open", 3.0, Kinds(Door), This(Open)), Goal("calm", 4.0, null, Self(Fed)) },
        };
        var b = new Bench(Build(brain));
        var door = new[] { BoltedDoor() };
        b.RunToDirect(door, 1UL << Fed);                     // fed, so only the door is tried
        b.Run(60, door, 1UL << Fed);
        long calmSince;
        for (; ; )
        {
            var t = b.Now;
            b.Step(door);                                    // hungry: calm outranks and replaces him at the door, direct as well
            if (b.A.GoalIndex == 2)
            {
                calmSince = t;
                break;
            }
        }

        Assert.Equal(-1, b.A.PlanLength);
        Assert.Equal(calmSince + 100, b.RunToFrustration(door));
    }

    [Fact]
    public void Depth_zero_frustrates_like_any_direct_try()
    {
        var b = new Bench(Build(Make(depth: 0)));
        var door = new[] { Thing(1, Door, 6f) };             // even a plain door is direct at depth 0
        var direct = b.RunToDirect(door);
        var f = b.RunToFrustration(door);
        Assert.Equal(direct + 100, f);
        b.Run(Latency, door);
        Assert.Equal(0, b.A.GoalIndex);
    }

    [Fact]
    public void A_self_want_with_no_target_frustrates_and_lifts_only_after_the_retry()
    {
        var brain = new Brain
        {
            PlanDepth = 3, DirectDiscount = 0.5, FrustrationSeconds = 1.0, RetrySeconds = 30.0,
            Goals = new[] { Goal("idle", 1.0), Goal("calm", 3.0, null, Self(Fed)) },
        };
        var b = new Bench(Build(brain));
        b.RunToFrustration(None);
        b.Run(Latency + 20, None);
        Assert.Equal(0, b.A.GoalIndex);
        b.Run(Latency + 200, None, 1UL << Held);              // a self fact changes: the bar stands
        Assert.Equal(0, b.A.GoalIndex);
        for (var i = 0; i < 3000 && b.A.GoalIndex == 0; i++)  // the retry passes: the try resumes
        {
            b.Step(None);
        }

        Assert.Equal(1, b.A.GoalIndex);
        Assert.Equal(0UL, b.A.TargetId);
        Assert.Equal(-1, b.A.PlanLength);
    }

    [Fact]
    public void Zero_seconds_frustrate_once_not_every_tick()
    {
        var b = new Bench(Build(Make(frustration: 0.0)));
        var door = new[] { BoltedDoor() };
        b.Run(400, door);
        Assert.Single(b.Frustrated);
        Assert.Equal(0, b.A.GoalIndex);
    }

    [Fact]
    public void Frustration_is_deterministic_and_allocates_nothing_in_a_cycle()
    {
        List<long> Trace(out int goal)
        {
            var b = new Bench(Build(Make(frustration: 0.5, retry: 1.0)));
            b.Run(1500, new[] { BoltedDoor() });
            goal = b.A.GoalIndex;
            return b.Frustrated;
        }

        var one = Trace(out var goalOne);
        var two = Trace(out var goalTwo);
        Assert.True(one.Count >= 5);
        Assert.Equal(one, two);
        Assert.Equal(goalOne, goalTwo);

        // Frustrate, lift by retry, try again: the cycle repeats every 160 ticks. Nothing is allocated once it has run.
        var a = Build(Make(frustration: 0.5, retry: 1.0));
        var door = new[] { BoltedDoor() };
        var strengths = new float[2];
        var targets = new Quat[2];
        for (var i = 0; i < 1000; i++)
        {
            a.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, door);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var events = TickEvents.None;
        for (var i = 0; i < 1000; i++)
        {
            events |= a.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, door);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True((events & TickEvents.Frustrated) != 0);   // the window held at least one full cycle
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void Validation_refuses_NaN_and_negatives_and_accepts_infinity()
    {
        foreach (var bad in new[] { double.NaN, -1.0, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentException>(() => Build(Make(frustration: bad)));
            Assert.Throws<ArgumentException>(() => Build(Make(retry: bad)));
        }

        Assert.Throws<ArgumentException>(() => Build(Make(frustration: 0.5, retry: 0.0)));

        Build(Make(frustration: double.PositiveInfinity, retry: double.PositiveInfinity));
        Build(Make(frustration: 0.0, retry: 1.0));
    }
}
