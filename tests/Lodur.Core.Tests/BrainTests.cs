using Lodur;

namespace Lodur.Core.Tests;

public sealed class BrainTests
{
    private const long Latency = 10; // round(0.1 s / 10 ms)

    private static GoalSpec Goal(string id, double priority, double pull = 0.5, params Condition[] when) => new()
    {
        Id = id, Priority = priority, Pull = pull, When = when,
        Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw },
    };

    private static readonly Perception Deaf = new(new NerveParameters(), 0.01, 1);

    private static int Pick(Brain brain, DriveState drive, int current) => brain.Select(drive, Deaf, 0f, 0f, 0f, new Choice(current, 0)).Goal;

    private static Condition Pain(ConditionOp op, double t) => new(DriveQuantity.Pain, op, t);

    private sealed record Run(List<long> Changes, List<int> Goals, long Broke, long[] Held);

    /// <summary>Ticks the pain box; reports the GoalChanged ticks and goals, the WillBroke tick, and per probe the first tick its predicate held after the tick.</summary>
    private static Run Go(PainBoxReplay r, long ticks, params Func<DriveState, bool>[] probes)
    {
        var run = new Run(new List<long>(), new List<int>(), -1, Enumerable.Repeat(-1L, probes.Length).ToArray());
        for (long i = 0; i < ticks; i++)
        {
            var e = r.Tick();
            if ((e & TickEvents.GoalChanged) != 0) { run.Changes.Add(i); run.Goals.Add(r.Actor.GoalIndex); }
            if ((e & TickEvents.WillBroke) != 0 && run.Broke < 0) run = run with { Broke = i };
            for (var p = 0; p < probes.Length; p++)
            {
                if (run.Held[p] < 0 && probes[p](r.Actor.Drive)) run.Held[p] = i;
            }
        }
        return run;
    }

    [Fact]
    public void A_brain_with_no_rule_about_the_will_never_changes_goal_when_the_will_breaks()
    {
        var one = new Brain { Goals = new[] { Goal("only", 0.0) } };
        var r = new PainBoxReplay(1, brain: one);
        var run = Go(r, 2500);
        Assert.True(run.Broke > 0, "the will never broke");
        Assert.Empty(run.Changes);
        Assert.Equal(0, r.Actor.GoalIndex);
    }

    [Fact]
    public void A_release_that_enters_on_pain_switches_long_before_the_will_breaks_exactly_the_latency_after_pain_first_reached_it()
    {
        var brain = new Brain
        {
            Goals = new[] { Goal("objective", 0.0), Goal("release", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.6)) },
        };
        var r = new PainBoxReplay(1, brain: brain);
        var run = Go(r, 2500, d => d.Pain >= 0.6);
        Assert.True(run.Held[0] > 0);
        Assert.Equal(new[] { run.Held[0] + Latency }, run.Changes);
        Assert.Equal(new[] { 1 }, run.Goals);
        Assert.True(run.Broke > run.Changes[0] + 500, $"will broke at {run.Broke}, the switch was at {run.Changes[0]}");
    }

    [Fact]
    public void Of_several_eligible_goals_the_highest_priority_is_taken_ties_stay_put_or_go_to_the_lowest_index()
    {
        var calm = default(DriveState); // all zero, will intact
        Condition Always = Pain(ConditionOp.Below, 1.0);
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("a", 2.0, 0.5, Always), Goal("b", 5.0, 0.5, Always), Goal("c", 5.0, 0.5, Always),
                Goal("d", 9.0, 0.5, Pain(ConditionOp.AtLeast, 0.5)),
            },
        };
        Assert.Equal(1, Pick(brain, calm, 0));  // b and c tie at the top: the lowest index
        Assert.Equal(2, Pick(brain, calm, 2));  // the current goal wins a tie
        Assert.Equal(1, Pick(brain, calm, 3));  // d is ineligible: current is not kept for its own sake
        var none = new Brain { Goals = new[] { Goal("x", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.5)), Goal("y", 0.0, 0.5, Pain(ConditionOp.AtLeast, 0.5)) } };
        Assert.Equal(1, Pick(none, calm, 1));   // nothing eligible: the current goal
        Assert.Equal(0, Pick(none, calm, 0));
    }

    [Fact]
    public void A_three_goal_brain_passes_through_the_goals_as_their_conditions_come_true_each_a_latency_after_its_own()
    {
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("calm", 0.0),
                Goal("wary", 3.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)),
                Goal("hurt", 5.0, 0.5, Pain(ConditionOp.AtLeast, 0.6)),
            },
        };
        var r = new PainBoxReplay(1, brain: brain);
        var run = Go(r, 2500, d => d.Pain >= 0.3, d => d.Pain >= 0.6);
        Assert.Equal(new[] { 1, 2 }, run.Goals);
        Assert.Equal(new[] { run.Held[0] + Latency, run.Held[1] + Latency }, run.Changes);
    }

    [Fact]
    public void A_revert_brain_switches_back_when_the_arousal_that_chose_the_rage_falls_and_each_switch_fires_once()
    {
        var brain = new Brain
        {
            Goals = new[] { Goal("calm", 1.0, 1.0), Goal("rage", 2.0, 1.0, new Condition(DriveQuantity.Arousal, ConditionOp.AtLeast, 0.5)) },
        };
        var rig = new SensoryRig(SensoryRig.One(0.55), brain: brain);
        long rose = -1, fell = -1;
        var changes = new List<(long Tick, int Goal)>();
        for (long i = 0; i < 20000; i++)
        {
            if (i == 6000) rig.Emitters[0].Intensity = 0.0;
            var e = rig.Tick();
            if ((e & TickEvents.GoalChanged) != 0) changes.Add((i, rig.Actor.GoalIndex));
            if (rose < 0 && rig.Actor.Drive.Arousal >= 0.5) rose = i;
            if (rose >= 0 && fell < 0 && i >= 6000 && rig.Actor.Drive.Arousal < 0.5) fell = i;
        }
        Assert.True(rose > 0 && fell > rose);
        Assert.Equal(new[] { (rose + Latency, 1), (fell + Latency, 0) }, changes);
    }

    [Fact]
    public void Resolve_drain_follows_the_pull_of_the_goal_in_force_slower_after_a_switch_to_a_goal_that_pulls_harder()
    {
        Brain Of(double secondPull) => new()
        {
            Goals = new[] { Goal("a", 0.0, 0.0), Goal("b", 1.0, secondPull, Pain(ConditionOp.AtLeast, 0.3)) },
        };
        var plain = new PainBoxReplay(1, brain: Of(0.0));
        var pulled = new PainBoxReplay(1, brain: Of(0.9));
        long switched = -1;
        double before = 0.0, plainLoss = 0.0, pulledLoss = 0.0;
        for (long i = 0; i < 1200; i++)
        {
            var ea = plain.Tick();
            var eb = pulled.Tick();
            if (switched < 0) Assert.Equal(ea, eb);
            if ((eb & TickEvents.GoalChanged) != 0) { switched = i; before = plain.Actor.Drive.Resolve - pulled.Actor.Drive.Resolve; }
            if (switched < 0) Assert.Equal(plain.Actor.Drive.Resolve, pulled.Actor.Drive.Resolve); // same pull until the switch
            if (switched >= 0 && i == switched + 600)
            {
                plainLoss = 1.0 - plain.Actor.Drive.Resolve;
                pulledLoss = 1.0 - pulled.Actor.Drive.Resolve;
            }
        }
        Assert.True(switched > 0);
        Assert.Equal(0.0, before);
        Assert.True(pulled.Actor.Drive.Resolve > plain.Actor.Drive.Resolve);
        Assert.True(pulledLoss < 0.25 * plainLoss, $"pull 0.9 lost {pulledLoss}, pull 0 lost {plainLoss}");
    }

    [Fact]
    public void A_decision_in_flight_commits_at_its_tick_even_if_its_condition_has_lapsed()
    {
        // The band is a tick or two wide while pain rises through it; the latency is ten.
        var brain = new Brain
        {
            Goals = new[] { Goal("a", 0.0), Goal("b", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3), Pain(ConditionOp.Below, 0.305)) },
        };
        var r = new PainBoxReplay(1, brain: brain);
        long inBand = -1, lapsed = -1;
        var changes = new List<(long Tick, int Goal)>();
        for (long i = 0; i < 600; i++)
        {
            var e = r.Tick();
            if ((e & TickEvents.GoalChanged) != 0) changes.Add((i, r.Actor.GoalIndex));
            var p = r.Actor.Drive.Pain;
            if (inBand < 0 && p >= 0.3) inBand = i;
            if (inBand >= 0 && lapsed < 0 && p >= 0.305) lapsed = i;
        }
        Assert.True(inBand > 0 && lapsed > inBand && lapsed < inBand + Latency, $"band {inBand}..{lapsed}");
        Assert.Equal(new[] { (inBand + Latency, 1), (inBand + Latency + 1 + Latency, 0) }, changes); // committed, then the lapse is acted on once
    }

    [Fact]
    public void While_a_decision_is_in_flight_no_second_one_is_made_the_later_goal_waits_for_the_first_to_commit()
    {
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("a", 0.0), Goal("b", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)), Goal("c", 2.0, 0.5, Pain(ConditionOp.AtLeast, 0.31)),
            },
        };
        var r = new PainBoxReplay(1, brain: brain);
        var run = Go(r, 600, d => d.Pain >= 0.3, d => d.Pain >= 0.31);
        Assert.True(run.Held[1] > run.Held[0] && run.Held[1] < run.Held[0] + Latency, $"{run.Held[0]} {run.Held[1]}");
        Assert.Equal(new[] { 1, 2 }, run.Goals);
        Assert.Equal(new[] { run.Held[0] + Latency, run.Held[0] + Latency + 1 + Latency }, run.Changes);
    }

    [Fact]
    public void The_brain_is_checked_when_the_actor_is_built()
    {
        Actor Build(Brain b) => new(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), b);
        var g = Goal("g", 0.0);
        Assert.Throws<ArgumentException>(() => Build(new Brain()));
        Assert.Throws<ArgumentException>(() => Build(new Brain { Goals = new[] { g }, Start = 1 }));
        Assert.Throws<ArgumentException>(() => Build(new Brain { Goals = new[] { g }, DeliberateLatencySeconds = double.NaN }));
        Assert.Throws<ArgumentException>(() => Build(new Brain { Goals = new[] { g with { Pull = 1.5 } } }));
        Assert.Throws<ArgumentException>(() => Build(new Brain { Goals = new[] { g with { When = new[] { Pain(ConditionOp.AtLeast, double.NaN) } } } }));
        Assert.Throws<ArgumentException>(() => Build(new Brain
        {
            Goals = new[] { g, g with { Behaviour = g.Behaviour with { Keys = new[] { new[] { Quat.Identity } }, FlinchPose = new[] { Quat.Identity } } } },
        }));
        Assert.Equal(1, Build(new Brain { Goals = new[] { g, g }, Start = 1 }).GoalIndex);
    }

    [Fact]
    public void A_goal_in_force_writes_its_behaviour_and_seconds_in_goal_count_from_the_tick_it_took_over()
    {
        var spin = new[]
        {
            new[] { Quat.Identity, Quat.Identity },
            new[] { Quat.FromAxisAngle(0f, 0f, 1f, 0.8f), Quat.Identity },
        };
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("a", 0.0),
                Goal("loop", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)) with
                {
                    Behaviour = new PoseBehaviour
                    {
                        Keys = spin, SecondsPerKey = 0.2, FlinchPose = spin[0],
                        Hold = new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 },
                    },
                },
            },
        };
        var r = new PainBoxReplay(1, brain: brain);
        long changed = -1;
        for (long i = 0; i < 600 && changed < 0; i++)
        {
            if ((r.Tick() & TickEvents.GoalChanged) != 0) changed = i;
        }
        Assert.True(changed > 0);
        Assert.Equal(0.01, r.Actor.SecondsInGoal, 12);        // the next tick sees one tick in the goal
        for (var k = 0; k < 10; k++) r.Tick();
        Assert.Equal(0.11, r.Actor.SecondsInGoal, 9);
        Assert.NotEqual(0f, r.Targets[0].Z);                   // partway from key 0 to key 1: the loop is walking
    }

    [Fact]
    public void Tick_with_a_three_goal_brain_and_a_four_key_behaviour_allocates_nothing_and_interleaved_actors_equal_each_alone()
    {
        Brain Make()
        {
            var keys = Enumerable.Range(0, 4).Select(k => new[] { Quat.FromAxisAngle(0f, 0f, 1f, 0.3f * k), Quat.Identity }).ToArray();
            return new Brain
            {
                Goals = new[]
                {
                    Goal("calm", 0.0),
                    Goal("wary", 1.0, 0.4, Pain(ConditionOp.AtLeast, 0.3)) with
                    {
                        Behaviour = new PoseBehaviour { Keys = keys, SecondsPerKey = 0.1, FlinchPose = PainBoxBrain.Withdraw },
                    },
                    Goal("hurt", 2.0, 0.8, Pain(ConditionOp.AtLeast, 0.6)),
                },
            };
        }

        var solo = new PainBoxReplay(1, brain: Make());
        for (var i = 0; i < 300; i++) solo.Tick(); // warm, and through the first switches
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) solo.Tick();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        const int n = 2500;
        var a1 = new PainBoxReplay(1, brain: Make());
        var b1 = new PainBoxReplay(7, intensity: 0.6, brain: Make());
        var a2 = new PainBoxReplay(1, brain: Make());
        var b2 = new PainBoxReplay(7, intensity: 0.6, brain: Make());
        var ea = new TickEvents[n];
        var eb = new TickEvents[n];
        for (var i = 0; i < n; i++) { ea[i] = a1.Tick(); eb[i] = b1.Tick(); }
        for (var i = 0; i < n; i++) Assert.Equal(ea[i], a2.Tick());
        for (var i = 0; i < n; i++) Assert.Equal(eb[i], b2.Tick());
        Assert.Equal(a1.Actor.GoalIndex, a2.Actor.GoalIndex);
        Assert.Equal(b1.Actor.GoalIndex, b2.Actor.GoalIndex);
        Assert.Equal(a1.Targets, a2.Targets);
        Assert.Equal(b1.Targets, b2.Targets);
        Assert.Contains(ea, e => (e & TickEvents.GoalChanged) != 0);
    }

    private static double Read(DriveState d, DriveQuantity q) => q switch
    {
        DriveQuantity.Pain => d.Pain,
        DriveQuantity.Strain => d.Strain,
        DriveQuantity.Arousal => d.Arousal,
        DriveQuantity.Resolve => d.Resolve,
        DriveQuantity.Breaks => d.Breaks,
        DriveQuantity.AirHunger => d.AirHunger,
        _ => d.WillBroken ? 1.0 : 0.0,
    };

    private static Brain Gated(Condition when, double dwell = 0.0, double pull = 0.5) => new()
    {
        Goals = new[] { Goal("calm", 0.0, pull), Goal("gated", 1.0, pull, when) with { MinDwellSeconds = dwell } },
    };

    /// <summary>
    /// Runs a rig whose Brain has one goal gated on the condition; the switch must land the latency after the tick the
    /// quantity itself first crossed, and no other quantity read the same way may cross on that tick (or the scenario
    /// could not tell a misread from the right read).
    /// </summary>
    private static void GateFollowsOnly(Func<Brain, (Func<TickEvents> Tick, Func<DriveState> Drive)> rig, DriveQuantity q, ConditionOp op, double threshold, long ticks, DriveQuantity? sameTickOk = null)
    {
        var (tick, drive) = rig(Gated(new Condition(q, op, threshold)));
        bool Crossed(DriveState d, DriveQuantity of) => op == ConditionOp.AtLeast ? Read(d, of) >= threshold : Read(d, of) < threshold;
        var firstOf = new Dictionary<DriveQuantity, long>();
        long changed = -1;
        for (long i = 0; i < ticks; i++)
        {
            var e = tick();
            if ((e & TickEvents.GoalChanged) != 0 && changed < 0) changed = i;
            foreach (var of in Enum.GetValues<DriveQuantity>())
            {
                if (!firstOf.ContainsKey(of) && Crossed(drive(), of)) firstOf[of] = i;
            }
        }
        Assert.True(firstOf.TryGetValue(q, out var own), $"{q} never crossed {threshold}");
        Assert.Equal(own + Latency, changed);
        foreach (var of in Enum.GetValues<DriveQuantity>())
        {
            if (of != q && of != sameTickOk) Assert.False(firstOf.TryGetValue(of, out var other) && other == own, $"{of} crosses on the same tick as {q}: the scenario cannot tell them apart");
        }
    }

    private static (Func<TickEvents>, Func<DriveState>) PainRig(Brain b) { var r = new PainBoxReplay(1, brain: b); return (() => r.Tick(), () => r.Actor.Drive); }

    private static (Func<TickEvents>, Func<DriveState>) SenseRig(Brain b)
    {
        var r = new SensoryRig(SensoryRig.One(0.55), brain: b);
        return (() => r.Tick(), () => r.Actor.Drive);
    }

    [Theory]
    [InlineData(DriveQuantity.Pain, ConditionOp.AtLeast, 0.5)]
    [InlineData(DriveQuantity.Strain, ConditionOp.AtLeast, 1.0)]
    [InlineData(DriveQuantity.Resolve, ConditionOp.Below, 0.9)]
    [InlineData(DriveQuantity.WillBroken, ConditionOp.AtLeast, 1.0)]
    public void Each_drive_quantity_a_condition_reads_is_that_quantity_in_the_pain_box(DriveQuantity q, ConditionOp op, double threshold) =>
        // The first break moves Breaks and WillBroken on one tick; WillTests reads Breaks at its second break, where they part.
        GateFollowsOnly(PainRig, q, op, threshold, 1700, DriveQuantity.Breaks);

    [Fact]
    public void Arousal_a_condition_reads_is_arousal_where_pain_strain_and_resolve_do_not_move_with_it()
    {
        GateFollowsOnly(SenseRig, DriveQuantity.Arousal, ConditionOp.AtLeast, 0.5, 3000);
    }

    [Fact]
    public void Below_is_strict_and_at_least_is_inclusive_at_the_threshold()
    {
        var zero = default(DriveState); // pain exactly 0
        Assert.True(new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.0).Holds(zero));
        Assert.False(new Condition(DriveQuantity.Pain, ConditionOp.Below, 0.0).Holds(zero));
        Assert.True(new Condition(DriveQuantity.Pain, ConditionOp.Below, 1e-300).Holds(zero));
        Assert.False(new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 1e-300).Holds(zero));
        Assert.False(new Condition(DriveQuantity.WillBroken, ConditionOp.Below, 0.0).Holds(zero));   // unbroken reads 0
        Assert.True(new Condition(DriveQuantity.WillBroken, ConditionOp.Below, 1.0).Holds(zero));
    }

    private static long ChangeTickAfterPain(double latencySeconds)
    {
        var brain = Gated(Pain(ConditionOp.AtLeast, 0.3)) with { DeliberateLatencySeconds = latencySeconds };
        var run = Go(new PainBoxReplay(1, brain: brain), 600, d => d.Pain >= 0.3);
        Assert.Single(run.Changes);
        return run.Changes[0] - run.Held[0];
    }

    [Fact]
    public void The_deliberate_latency_is_rounded_to_ticks_never_less_than_one_and_a_negative_latency_is_refused()
    {
        Assert.Equal(1, ChangeTickAfterPain(0.0));    // a switch never lands on the tick it was decided
        Assert.Equal(1, ChangeTickAfterPain(0.001));
        Assert.Equal(1, ChangeTickAfterPain(0.014));
        Assert.Equal(2, ChangeTickAfterPain(0.016));
        Assert.Equal(10, ChangeTickAfterPain(0.1));
        var g = Goal("g", 0.0);
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            new Brain { Goals = new[] { g }, DeliberateLatencySeconds = -0.01 }));
    }

    [Fact]
    public void The_pending_goals_behaviour_is_not_written_during_the_latency_only_from_the_tick_it_takes_over()
    {
        var loud = Goal("b", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)) with
        {
            Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = new HoldParameters { HoldStrength = 0.25f } },
        };
        var r = new PainBoxReplay(1, brain: new Brain { Goals = new[] { Goal("a", 0.0), loud } });
        long decided = -1;
        var landed = false;
        for (long i = 0; i < 600 && !landed; i++)
        {
            var e = r.Tick();
            if (decided < 0 && r.Actor.Drive.Pain >= 0.3) decided = i;
            if (decided < 0) continue;
            if (i < decided + Latency)
            {
                Assert.Equal(1f, r.Strengths[0]);
                Assert.Equal((i + 1) * 0.01, r.Actor.SecondsInGoal, 9); // the goal in force keeps its own clock through the decision and the latency
                Assert.Equal(0, (int)(e & TickEvents.GoalChanged));
            }
            else
            {
                Assert.Equal(TickEvents.GoalChanged, e & TickEvents.GoalChanged);
                Assert.Equal(0.25f, r.Strengths[0]);
                landed = true;
            }
        }
        Assert.True(landed);
    }

    [Fact]
    public void A_behaviours_phase_restarts_from_the_time_in_the_goal_not_the_clock()
    {
        var keys = Enumerable.Range(0, 4).Select(k => new[] { Quat.FromAxisAngle(0f, 0f, 1f, 0.3f * k), Quat.Identity }).ToArray();
        var loop = Goal("loop", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)) with
        {
            Behaviour = new PoseBehaviour { Keys = keys, SecondsPerKey = 0.1, FlinchPose = keys[0], Hold = new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 } },
        };
        var r = new PainBoxReplay(1, brain: new Brain { Goals = new[] { Goal("a", 0.0), loop } });
        long changed = -1;
        for (long i = 0; i < 600 && changed < 0; i++)
        {
            if ((r.Tick() & TickEvents.GoalChanged) != 0) changed = i;
        }
        Assert.True(changed > 0 && changed % 40 != 0, $"changed at {changed}: the loop is 40 ticks, a clock-driven phase would be hidden");
        Assert.Equal(Quat.Nlerp(keys[0][0], keys[0][0], 0f).Z, r.Targets[0].Z);     // key 0 exactly, the tick it takes over
        for (var k = 0; k < 5; k++) r.Tick();
        Assert.Equal(Quat.Nlerp(keys[0][0], keys[1][0], 0.5f).Z, r.Targets[0].Z, 5); // half way to key 1 after half a key
    }

    private static List<(long Tick, int Goal)> Hover(double dwell, double threshold)
    {
        var rage = Goal("rage", 2.0, 1.0, new Condition(DriveQuantity.Arousal, ConditionOp.AtLeast, threshold)) with { MinDwellSeconds = dwell };
        var brain = new Brain { Goals = new[] { Goal("calm", 1.0, 1.0) with { MinDwellSeconds = dwell }, rage } };
        var rig = new SensoryRig(SensoryRig.One(0.55), brain: brain);
        var changes = new List<(long, int)>();
        for (long i = 0; i < 6000; i++)
        {
            rig.Emitters[0].Intensity = (i / 100) % 2 == 0 ? 0.55 : 0.0; // on 1 s, off 1 s: arousal saws around its middle
            if ((rig.Tick() & TickEvents.GoalChanged) != 0) changes.Add((i, rig.Actor.GoalIndex));
        }
        return changes;
    }

    [Fact]
    public void A_condition_hovering_at_its_threshold_chatters_without_a_dwell_and_with_one_the_entered_goal_is_not_left_for_a_lower_one_inside_its_window()
    {
        double lo = 1, hi = 0;
        var probe = new SensoryRig(SensoryRig.One(0.55), brain: new Brain { Goals = new[] { Goal("only", 0.0, 1.0) } });
        for (long i = 0; i < 6000; i++)
        {
            probe.Emitters[0].Intensity = (i / 100) % 2 == 0 ? 0.55 : 0.0;
            probe.Tick();
            if (i >= 3000) { lo = Math.Min(lo, probe.Actor.Drive.Arousal); hi = Math.Max(hi, probe.Actor.Drive.Arousal); }
        }
        var mid = 0.5 * (lo + hi);

        var free = Hover(0.0, mid);
        Assert.True(free.Count >= 15, $"{free.Count} changes with no dwell: the scenario does not chatter");

        const double dwell = 3.0;
        const long dwellTicks = 300;
        var held = Hover(dwell, mid);
        Assert.True(held.Count >= 2 && held.Count * 2 <= free.Count, $"{held.Count} changes with a dwell of {dwell} s against {free.Count}");
        var leaves = 0;
        for (var i = 1; i < held.Count; i++)
        {
            if (held[i].Goal != 0) continue; // only the return trip (rage to the lower calm) is gated by rage's dwell
            leaves++;
            Assert.True(held[i].Tick - held[i - 1].Tick >= dwellTicks + Latency, $"rage taken at {held[i - 1].Tick} and left at {held[i].Tick}");
        }
        Assert.True(leaves >= 2);
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            new Brain { Goals = new[] { Goal("g", 0.0) with { MinDwellSeconds = -1.0 } } }));
        foreach (var bad in new[] { double.PositiveInfinity, double.NaN })
        {
            Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
                new Brain { Goals = new[] { Goal("g", 0.0) with { MinDwellSeconds = bad } } }));
        }
    }

    /// <summary>
    /// Goal 1 is the start goal, eligible while pain is below 0.3, with the dwell; goal 0 (no dwell) is the lower-priority
    /// goal always eligible. Pain passes 0.3 early, so the decision to leave goal 1 is held to its dwell and lands exactly then.
    /// </summary>
    private static (long Dwell, long Changed, long PainLapsed) LeaveHeldGoal(double dwellSeconds, double priorityOfTarget = 1.0)
    {
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("target", priorityOfTarget, 0.5, Pain(ConditionOp.AtLeast, 0.3)),
                Goal("held", 2.0, 0.5, Pain(ConditionOp.Below, 0.3)) with { MinDwellSeconds = dwellSeconds },
            },
            Start = 1,
        };
        var r = new PainBoxReplay(1, brain: brain);
        long lapsed = -1, changed = -1;
        for (long i = 0; i < 800 && changed < 0; i++)
        {
            if ((r.Tick() & TickEvents.GoalChanged) != 0) changed = i;
            if (lapsed < 0 && r.Actor.Drive.Pain >= 0.3) lapsed = i;
        }
        return (0, changed, lapsed);
    }

    [Fact]
    public void A_goal_not_outranked_is_left_exactly_a_dwell_after_it_took_over_the_decision_held_on_the_dwells_last_tick()
    {
        // 3.0 s is 300 ticks from tick 0 (the start goal is active from there): the decision is taken on tick 300 and lands 10 later.
        var (_, changed, lapsed) = LeaveHeldGoal(3.0);
        Assert.True(lapsed > 0 && lapsed < 290, $"pain lapsed the condition on tick {lapsed}");
        Assert.Equal(300 + Latency, changed);
        // The dwell is the held goal's own (goal 1), not goal 0's, which has none.
        var free = LeaveHeldGoal(0.0);
        Assert.Equal(free.PainLapsed + Latency, free.Changed);
    }

    [Fact]
    public void A_dwell_is_rounded_to_the_nearest_tick_as_the_latency_is()
    {
        Assert.Equal(300 + Latency, LeaveHeldGoal(3.004).Changed); // 300.4 ticks: a ceiling would make it 301
        Assert.Equal(301 + Latency, LeaveHeldGoal(3.006).Changed); // 300.6 ticks: a floor would make it 300
    }

    [Fact]
    public void A_strictly_higher_priority_goal_preempts_during_the_dwell_after_the_latency_and_a_goal_that_ties_or_ranks_below_is_held()
    {
        // Same scene, but the goal it would move to outranks the held one: no wait for the dwell.
        var (_, preempted, lapsed) = LeaveHeldGoal(3.0, priorityOfTarget: 3.0);
        Assert.Equal(lapsed + Latency, preempted);
        Assert.Equal(300 + Latency, LeaveHeldGoal(3.0, priorityOfTarget: 2.0).Changed); // a tie is not strictly higher
        Assert.Equal(300 + Latency, LeaveHeldGoal(3.0, priorityOfTarget: 1.99).Changed);
    }

    [Fact]
    public void The_dwell_of_an_entered_goal_counts_from_the_tick_it_took_over_not_from_the_decision()
    {
        // Held goal b is entered on pain at least 0.3 and its condition lapses at once (a band); its dwell of 2 s runs from the takeover.
        var band = Goal("b", 2.0, 0.5, Pain(ConditionOp.AtLeast, 0.3), Pain(ConditionOp.Below, 0.305)) with { MinDwellSeconds = 2.0 };
        var r = new PainBoxReplay(1, brain: new Brain { Goals = new[] { Goal("a", 1.0), band } });
        long decided = -1, entered = -1, left = -1;
        for (long i = 0; i < 800 && left < 0; i++)
        {
            var e = r.Tick();
            if (decided < 0 && r.Actor.Drive.Pain >= 0.3) decided = i;
            if ((e & TickEvents.GoalChanged) != 0) { if (entered < 0) entered = i; else left = i; }
        }
        Assert.Equal(decided + Latency, entered);
        Assert.Equal(entered + 200 + Latency, left);
    }

    [Fact]
    public void A_built_brains_getters_hand_out_read_only_views_and_writing_through_them_changes_nothing()
    {
        var keys = new[] { new[] { Quat.Identity, Quat.Identity } };
        var brain = new Brain
        {
            Goals = new[]
            {
                Goal("a", 0.0),
                Goal("b", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)) with
                {
                    Behaviour = new PoseBehaviour { Keys = keys, FlinchPose = PainBoxBrain.Withdraw },
                },
            },
        };
        static void Sealed<T>(IReadOnlyList<T> view, T poison)
        {
            Assert.False(view is T[]);
            if (view is IList<T> list)
            {
                Assert.True(list.IsReadOnly);
                Assert.Throws<NotSupportedException>(() => list[0] = poison);
            }
        }
        Sealed(brain.Goals, Goal("poison", 9.0));
        Sealed(brain.Goals[1].When, Pain(ConditionOp.AtLeast, double.NaN));
        Sealed(brain.Goals[1].Behaviour.Keys, (IReadOnlyList<Quat>)new Quat[1]);
        Sealed(brain.Goals[1].Behaviour.Keys[0], new Quat(7f, 7f, 7f, 7f));
        Sealed(brain.Goals[1].Behaviour.FlinchPose, new Quat(7f, 7f, 7f, 7f));

        var a = new PainBoxReplay(1, brain: brain);
        var b = new PainBoxReplay(1, brain: new Brain { Goals = new[] { Goal("a", 0.0), Goal("b", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.3)) with { Behaviour = new PoseBehaviour { Keys = keys, FlinchPose = PainBoxBrain.Withdraw } } } });
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(b.Tick(), a.Tick());
            Assert.Equal(b.Targets, a.Targets);
        }
    }

    [Fact]
    public void A_validated_brain_does_not_follow_later_changes_to_the_lists_and_arrays_it_was_built_from()
    {
        Brain Build(out List<GoalSpec> goals, out Condition[] when, out Quat[][] keys, out Quat[] flinch)
        {
            when = new[] { Pain(ConditionOp.AtLeast, 0.3) };
            keys = new[] { (Quat[])PainBoxBrain.Hold.Clone() };
            flinch = (Quat[])PainBoxBrain.Withdraw.Clone();
            goals = new List<GoalSpec>
            {
                Goal("a", 0.0),
                Goal("b", 1.0, 0.5) with { When = when, Behaviour = new PoseBehaviour { Keys = keys, FlinchPose = flinch } },
            };
            return new Brain { Goals = goals };
        }

        var brain = Build(out var goals, out var when, out var keys, out var flinch);
        var twin = Build(out _, out _, out _, out _);
        goals.Clear();
        when[0] = Pain(ConditionOp.AtLeast, double.NaN);
        keys[0][0] = new Quat(9f, 9f, 9f, 9f);
        keys[0] = new Quat[1];
        flinch[0] = new Quat(5f, 5f, 5f, 5f);

        var a = new PainBoxReplay(1, brain: brain);
        var b = new PainBoxReplay(1, brain: twin);
        for (var i = 0; i < 1500; i++)
        {
            Assert.Equal(b.Tick(), a.Tick());
            Assert.Equal(b.Targets, a.Targets);
        }
        Assert.Equal(2, brain.Goals.Count);
        Assert.Equal(1, a.Actor.GoalIndex);
    }
}
