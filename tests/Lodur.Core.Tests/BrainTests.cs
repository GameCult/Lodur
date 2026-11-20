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
        Assert.Equal(1, brain.Select(calm, 0));  // b and c tie at the top: the lowest index
        Assert.Equal(2, brain.Select(calm, 2));  // the current goal wins a tie
        Assert.Equal(1, brain.Select(calm, 3));  // d is ineligible: current is not kept for its own sake
        var none = new Brain { Goals = new[] { Goal("x", 1.0, 0.5, Pain(ConditionOp.AtLeast, 0.5)), Goal("y", 0.0, 0.5, Pain(ConditionOp.AtLeast, 0.5)) } };
        Assert.Equal(1, none.Select(calm, 1));   // nothing eligible: the current goal
        Assert.Equal(0, none.Select(calm, 0));
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
}
