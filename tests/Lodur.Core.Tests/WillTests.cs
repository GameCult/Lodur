using Lodur;

namespace Lodur.Core.Tests;

/// <summary>The will breaks when resolve reaches zero, mends when it climbs back to WillMendsAt, and can break again.</summary>
public sealed class WillTests
{
    private static Brain Gate(DriveQuantity q, double threshold) => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "objective", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw } },
            new GoalSpec
            {
                Id = "release", Priority = 1.0,
                When = new[] { new Condition(q, ConditionOp.AtLeast, threshold) },
                Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Withdraw }, FlinchPose = PainBoxBrain.Withdraw },
            },
        },
    };

    /// <summary>Ticks the box on (or off) until the predicate holds or the cap runs out; counts WillBroke events on the way.</summary>
    private static long Until(PainBoxReplay r, bool boxOn, Func<DriveState, bool> done, ref int breaks, long cap = 40000)
    {
        for (long i = 0; i < cap; i++)
        {
            if ((r.Tick(boxOn) & TickEvents.WillBroke) != 0) breaks++;
            if (done(r.Actor.Drive)) return i;
        }

        Assert.Fail("the predicate never held");
        return -1;
    }

    [Fact]
    public void A_will_that_mends_can_break_again_and_each_break_is_reported_and_counted_but_resolve_parked_at_zero_breaks_once()
    {
        var r = new PainBoxReplay(1, brain: Gate(DriveQuantity.Breaks, 99.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        Assert.Equal(1, breaks);
        Assert.Equal(1, r.Actor.Drive.Breaks);

        for (var i = 0; i < 3000; i++) r.Tick(true);   // held under the box, resolve parked at zero
        Assert.Equal(0.0, r.Actor.Drive.Resolve);
        Assert.True(r.Actor.Drive.WillBroken);

        for (var i = 0; i < 3000; i++) if ((r.Tick(true) & TickEvents.WillBroke) != 0) breaks++;
        Assert.Equal(1, breaks);

        Until(r, false, d => !d.WillBroken, ref breaks);
        Assert.True(r.Actor.Drive.Resolve >= new DriveParameters().WillMendsAt);
        Assert.Equal(1, r.Actor.Drive.Breaks);

        Until(r, true, d => d.WillBroken, ref breaks);
        Assert.Equal(2, breaks);
        Assert.Equal(2, r.Actor.Drive.Breaks);
    }

    [Fact]
    public void A_mend_waits_for_resolve_to_reach_WillMendsAt_not_merely_to_leave_zero()
    {
        var mends = new DriveParameters { WillMendsAt = 0.6 };
        var r = new PainBoxReplay(1, drive: mends, brain: Gate(DriveQuantity.Breaks, 99.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        Until(r, false, d => d.Resolve > 0.0, ref breaks);
        var mended = Until(r, false, d => !d.WillBroken, ref breaks);
        Assert.True(mended > 0, "the will mended the moment resolve left zero");
        Assert.True(r.Actor.Drive.Resolve >= 0.6);
    }

    [Theory]
    [InlineData(DriveQuantity.Breaks, true)]
    [InlineData(DriveQuantity.WillBroken, false)]
    public void A_release_gated_on_Breaks_holds_after_the_will_mends_and_one_gated_on_WillBroken_returns_to_the_objective(DriveQuantity q, bool holds)
    {
        var r = new PainBoxReplay(1, brain: Gate(q, 1.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        for (var i = 0; i < 50; i++) r.Tick(true);
        Assert.Equal(1, r.Actor.GoalIndex);

        Until(r, false, d => !d.WillBroken, ref breaks);
        for (var i = 0; i < 100; i++) r.Tick(false);
        Assert.Equal(holds ? 1 : 0, r.Actor.GoalIndex);
    }

    [Fact]
    public void A_condition_on_Breaks_at_two_follows_the_second_break_by_the_latency_not_the_first()
    {
        var r = new PainBoxReplay(1, brain: Gate(DriveQuantity.Breaks, 2.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        for (var i = 0; i < 100; i++) r.Tick(true);
        Assert.Equal(0, r.Actor.GoalIndex);

        Until(r, false, d => !d.WillBroken, ref breaks);
        Until(r, true, d => d.Breaks == 2, ref breaks);
        var second = r.Actor.TickCount;
        long changed = -1;
        for (var i = 0; i < 30 && changed < 0; i++)
        {
            if ((r.Tick(true) & TickEvents.GoalChanged) != 0) changed = r.Actor.TickCount;
        }

        Assert.Equal(second + 10, changed);
    }

    [Fact]
    public void Will_is_resolve_bit_for_bit_while_attention_and_consciousness_are_slots_that_read_one()
    {
        var r = new PainBoxReplay(3);
        for (var i = 0; i < 2500; i++)
        {
            r.Tick(true);
            var d = r.Actor.Drive;
            Assert.Equal(1.0, d.Attention);
            Assert.Equal(1.0, d.Consciousness);
            Assert.Equal(BitConverter.DoubleToInt64Bits(d.Resolve), BitConverter.DoubleToInt64Bits(d.Will));
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_WillMendsAt_outside_zero_exclusive_to_one_is_refused(double mendsAt) =>
        Assert.Throws<ArgumentException>(() => new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = mendsAt }));

    [Fact]
    public void A_WillMendsAt_of_one_is_allowed() => _ = new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = 1.0 });

    private static PoseBehaviour Pose => new() { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw };

    private static Brain Rage(double? entry) => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "composure", Behaviour = Pose },
            new GoalSpec
            {
                Id = "rage", Priority = 1.0, EntryResolve = entry,
                When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) },
                Behaviour = Pose,
            },
        },
    };

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.1, true)]
    public void A_goals_entry_resolve_is_set_on_the_tick_it_takes_over_and_mends_the_will_only_if_it_reaches_WillMendsAt(double entry, bool stillBroken)
    {
        var r = new PainBoxReplay(1, brain: Rage(entry));
        TickEvents e;
        do { e = r.Tick(true); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
        Assert.NotEqual(0, (int)(e & TickEvents.GoalChanged));
        Assert.Equal(1, r.Actor.GoalIndex);
        Assert.Equal(entry, r.Actor.Drive.Resolve);
        Assert.Equal(stillBroken, r.Actor.Drive.WillBroken);
        Assert.Equal(1, r.Actor.Drive.Breaks);
    }

    [Fact]
    public void A_goal_with_no_entry_resolve_leaves_resolve_alone()
    {
        var r = new PainBoxReplay(1, brain: Rage(null));
        TickEvents e;
        do { e = r.Tick(true); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
        Assert.Equal(0.0, r.Actor.Drive.Resolve);
        Assert.True(r.Actor.Drive.WillBroken);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.5)]
    [InlineData(1.0, 0.4, 2.0)]
    [InlineData(0.0, 0.0, 0.5)]
    public void Effort_drains_resolve_only_after_the_involuntary_onset_by_effort_times_one_minus_pull_times_the_trait(double onset, double pull, double effort)
    {
        var drive = new DriveParameters { ResolveRecovery = 0.0, DrainTrait = 1.0 };
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Pull = pull, Effort = effort, InvoluntarySeconds = onset, Behaviour = Pose } } };
        var r = new PainBoxReplay(1, drive: drive, brain: brain, jitter: 0.0);
        var first = -1L;
        var last = 1.0;
        var steps = 0;
        for (long i = 0; i < 400; i++)
        {
            r.Tick(false);
            var now = r.Actor.Drive.Resolve;
            if (now <= 0.0) break;   // the floor clips the last step
            if (now < 1.0 && first < 0)
            {
                first = i;
                last = 1.0;
            }

            if (first >= 0)
            {
                Assert.Equal(0.01 * effort * (1.0 - pull), last - now, 12);
                steps++;
            }

            last = now;
        }

        Assert.Equal((long)Math.Round(onset / 0.01), first);
        Assert.True(steps > 20);
    }

    [Fact]
    public void An_infinitely_involuntary_goal_never_pays_its_effort()
    {
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Effort = 5.0, InvoluntarySeconds = double.PositiveInfinity, Behaviour = Pose } } };
        var r = new PainBoxReplay(1, brain: brain);
        for (var i = 0; i < 500; i++) r.Tick(false);
        Assert.Equal(1.0, r.Actor.Drive.Resolve);
    }

    [Fact]
    public void Onset_is_counted_from_the_tick_the_goal_takes_over_not_from_the_start()
    {
        var late = new GoalSpec { Id = "late", Priority = 1.0, Effort = 1.0, InvoluntarySeconds = 0.5, When = new[] { new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.3) }, Behaviour = Pose };
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "calm", Behaviour = Pose }, late } };
        var r = new PainBoxReplay(1, brain: brain, drive: new DriveParameters { ResolveRecovery = 0.0 });
        long took = -1;
        long firstEffort = -1;
        for (long i = 0; i < 3000 && firstEffort < 0; i++)
        {
            var before = r.Actor.Drive;
            var e = r.Tick(true);
            if ((e & TickEvents.GoalChanged) != 0) took = i;
            var drainedWithoutEffort = before.Resolve - r.Actor.Drive.Resolve;
            if (took >= 0 && drainedWithoutEffort > 0.01 * 1.0 * 0.5 * 0.99) firstEffort = i;
        }

        Assert.True(took > 0);
        Assert.True(firstEffort - took >= 50, $"effort began {firstEffort - took} ticks after the goal took over, under the 50 of the onset");
    }

    [Fact]
    public void Goal_values_outside_their_ranges_are_refused_naming_the_goal()
    {
        Brain Of(GoalSpec g) => new() { Goals = new[] { g } };
        GoalSpec Base() => new() { Id = "bad", Behaviour = Pose };
        void Refused(GoalSpec g)
        {
            var ex = Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Of(g)));
            Assert.Contains("bad", ex.Message);
        }

        Refused(Base() with { EntryResolve = -0.01 });
        Refused(Base() with { EntryResolve = 1.01 });
        Refused(Base() with { EntryResolve = double.NaN });
        Refused(Base() with { Effort = -1.0 });
        Refused(Base() with { Effort = double.PositiveInfinity });
        Refused(Base() with { Effort = double.NaN });
        Refused(Base() with { InvoluntarySeconds = -0.1 });
        Refused(Base() with { InvoluntarySeconds = double.NaN });

        _ = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            Of(Base() with { EntryResolve = 0.0, Effort = 0.0, InvoluntarySeconds = double.PositiveInfinity }));
        _ = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Of(Base() with { EntryResolve = 1.0 }));
    }

    private static HoldParameters Hold07 => new() { HoldStrength = 0.7f };

    [Fact]
    public void A_voluntary_goal_writes_its_hold_strength_times_Will_to_the_WillCurve_with_no_leak()
    {
        // Effort drains resolve at a known rate with recovery off and the box off, so the leak is zero and Will is a clean ramp.
        var drive = new DriveParameters { ResolveRecovery = 0.0, WillCurve = 0.5 };
        var pose = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = Hold07 };
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Effort = 0.5, Behaviour = pose } } };
        var r = new PainBoxReplay(1, drive: drive, brain: brain, jitter: 0.0);
        var last = float.MaxValue;
        for (var i = 0; i < 150; i++)
        {
            r.Tick(false);
            Assert.Equal(0.0, r.Actor.Afferent(0));
            var will = r.Actor.Drive.Will;
            Assert.Equal(0.7f * (float)Math.Sqrt(will), r.Strengths[0]);
            Assert.Equal(r.Strengths[0], r.Strengths[1]);
            Assert.True(r.Strengths[0] <= last);
            last = r.Strengths[0];
        }

        Assert.True(last < 0.6f, "the strength never fell with the will");
    }

    [Fact]
    public void A_goal_that_takes_over_at_resolve_zero_writes_full_hold_strength_while_involuntary_and_what_leaks_while_voluntary()
    {
        float AtBreak(double involuntary)
        {
            var release = new GoalSpec
            {
                Id = "release", Priority = 1.0, InvoluntarySeconds = involuntary,
                When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) },
                Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Withdraw }, FlinchPose = PainBoxBrain.Withdraw, Hold = Hold07 },
            };
            var brain = new Brain { Goals = new[] { new GoalSpec { Id = "objective", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = Hold07 } }, release } };
            var r = new PainBoxReplay(1, brain: brain);
            TickEvents e;
            do { e = r.Tick(true); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
            Assert.Equal(0.0, r.Actor.Drive.Resolve);
            Assert.True(r.Actor.Afferent(0) > 0.3);
            return r.Strengths[0];
        }

        Assert.Equal(0.7f, AtBreak(double.PositiveInfinity));
        Assert.True(AtBreak(0.0) < 0.7f * 0.5f, "a voluntary goal at zero will held its strength");
    }

    [Theory]
    [InlineData(1.0, 0.7f)]
    [InlineData(0.3, 0.7f * 0.7401198f)]   // 0.3 ^ 0.25
    public void The_tick_a_goal_takes_over_its_entry_resolve_is_the_will_its_first_write_is_weighted_by(double entry, float expected)
    {
        var pose = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = Hold07 };
        var brain = new Brain
        {
            Goals = new[]
            {
                new GoalSpec { Id = "composure", Behaviour = pose },
                new GoalSpec { Id = "rage", Priority = 1.0, EntryResolve = entry, When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) }, Behaviour = pose },
            },
        };
        var r = new PainBoxReplay(1, brain: brain);
        TickEvents e;
        do { e = r.Tick(r.Actor.Drive.Breaks == 0); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
        Assert.Equal(0.0, r.Actor.Afferent(0));
        Assert.Equal(expected, r.Strengths[0], 4);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_WillCurve_that_is_not_finite_and_above_zero_is_refused(double curve) =>
        Assert.Throws<ArgumentException>(() => new PainBoxReplay(1, drive: new DriveParameters { WillCurve = curve }));

    public static IEnumerable<object[]> BadDrives() => new[]
    {
        new object[] { new DriveParameters { DrainTrait = double.NaN } },
        new object[] { new DriveParameters { DrainTrait = -1.0 } },
        new object[] { new DriveParameters { PainRise = double.PositiveInfinity } },
        new object[] { new DriveParameters { PainDecay = double.NaN } },
        new object[] { new DriveParameters { StrainRecover = -0.1 } },
        new object[] { new DriveParameters { ResolveDrainPain = double.NaN } },
        new object[] { new DriveParameters { ResolveDrainStrain = double.NegativeInfinity } },
        new object[] { new DriveParameters { ResolveRecovery = double.NaN } },
        new object[] { new DriveParameters { ResolveRecoversBelowPain = double.NaN } },
        new object[] { new DriveParameters { ArousalRise = -1.0 } },
        new object[] { new DriveParameters { ArousalDecay = double.NaN } },
        new object[] { new DriveParameters { ResolveDrainArousal = double.PositiveInfinity } },
        new object[] { new DriveParameters { ResolveRecoversBelowArousal = double.NaN } },
    };

    [Theory]
    [MemberData(nameof(BadDrives))]
    public void A_drive_value_the_drive_cannot_run_on_is_refused_at_construction_before_any_tick(DriveParameters bad) =>
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), bad, PainBoxBrain.Make(0.5)));

    [Theory]
    [InlineData(1.0 / 120.0, 1.85, 222)]   // 222 x (1/120) is just under 1.85 in floating point
    [InlineData(1.0 / 60.0, 0.9, 54)]
    [InlineData(0.01, 0.5, 50)]
    [InlineData(0.01, 0.29, 29)]            // 0.29 / 0.01 is 28.999999999999996: rounds to 29, floors to 28
    public void The_involuntary_onset_is_the_requested_seconds_in_whole_ticks_whatever_the_tick_length(double dt, double onset, long ticks)
    {
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Effort = 1.0, InvoluntarySeconds = onset, Behaviour = Pose } } };
        var actor = new Actor(dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters { ResolveRecovery = 0.0 }, brain);
        float[] p = { 0f }, strengths = new float[2];
        var targets = new Quat[2];
        long firstDrain = -1, firstWeighted = -1;
        for (long i = 0; i < ticks + 20; i++)
        {
            var before = actor.Drive.Resolve;
            actor.Tick(p, p, p, System.Array.Empty<BoxStimulus>(), strengths, targets);
            if (firstDrain < 0 && actor.Drive.Resolve < before) firstDrain = i;
            if (firstWeighted < 0 && strengths[0] < 1f) firstWeighted = i;
        }

        Assert.Equal(ticks, firstDrain);
        Assert.Equal(ticks, firstWeighted);
    }

    [Theory]
    [InlineData(1e300)]
    [InlineData(1e19)]
    [InlineData(double.PositiveInfinity)]
    public void An_onset_past_the_range_of_a_tick_count_is_involuntary_throughout_not_a_wrapped_negative(double onset)
    {
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Effort = 1.0, InvoluntarySeconds = onset, Behaviour = Pose } } };
        var actor = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters { ResolveRecovery = 0.0 }, brain);
        float[] p = { 0f }, strengths = new float[2];
        var targets = new Quat[2];
        for (var i = 0; i < 500; i++)
        {
            actor.Tick(p, p, p, System.Array.Empty<BoxStimulus>(), strengths, targets);
            Assert.Equal(1.0, actor.Drive.Resolve);
            Assert.Equal(1f, strengths[0]);
        }
    }

    [Fact]
    public void A_puppet_that_starts_in_a_goal_with_an_entry_resolve_begins_at_it()
    {
        var brain = new Brain { Start = 1, Goals = new[] { new GoalSpec { Id = "calm", Behaviour = Pose }, new GoalSpec { Id = "rage", EntryResolve = 0.6, Behaviour = Pose } } };
        var actor = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain);
        Assert.Equal(0.6, actor.Drive.Resolve);

        var calm = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain with { Start = 0 });
        Assert.Equal(1.0, calm.Drive.Resolve);
    }

    // The slots read 1 in the core today; these tests lower them through the internal seam and watch every consumer of Will move.

    private static Actor Hold(double attention, double consciousness, DriveParameters? drive = null)
    {
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = Hold07 } } } };
        var actor = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), drive ?? new DriveParameters(), brain);
        actor.SetWillSlots(attention, consciousness);
        return actor;
    }

    private static readonly float[] NoPoint = { 0f };

    private static TickEvents Step(Actor a, double boxIntensity, float[] strengths, Quat[] targets)
    {
        var box = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) { Intensity = boxIntensity } };
        return a.Tick(NoPoint, NoPoint, NoPoint, box, strengths, targets);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 0.3)]
    [InlineData(0.5, 0.3)]
    [InlineData(0.5, 0.9)]
    [InlineData(0.0, 1.0)]
    public void Will_is_resolve_times_attention_capped_by_consciousness(double attention, double consciousness)
    {
        var a = Hold(attention, consciousness);
        var s = new float[2];
        var q = new Quat[2];
        for (var i = 0; i < 300; i++)
        {
            Step(a, 0.6, s, q);
            var d = a.Drive;
            Assert.Equal(Math.Min(d.Resolve * attention, consciousness), d.Will);
        }
    }

    [Fact]
    public void Reflex_suppression_reads_Will_so_a_lowered_slot_releases_it_though_resolve_is_full()
    {
        // A held reflex accrues strain; with attention gone nothing is held, whatever resolve says.
        double StrainAfter(double attention, double consciousness)
        {
            var a = Hold(attention, consciousness);
            var s = new float[2];
            var q = new Quat[2];
            for (var i = 0; i < 100; i++) Step(a, 0.8, s, q);
            Assert.True(a.Drive.Resolve > 0.9);
            return a.Drive.Strain;
        }

        var full = StrainAfter(1.0, 1.0);
        Assert.True(full > 0.1);
        Assert.Equal(0.0, StrainAfter(0.0, 1.0));
        Assert.Equal(0.0, StrainAfter(1.0, 0.0));
        var half = StrainAfter(0.5, 1.0);
        Assert.True(half > 0.0 && half < full);
        var capped = StrainAfter(1.0, 0.5);
        Assert.True(capped > 0.0 && capped < full);
    }

    [Fact]
    public void The_flinch_gate_reads_Will_so_no_flinch_is_reported_when_there_is_no_will_to_leak_from()
    {
        int Flinches(double attention)
        {
            var a = Hold(attention, 1.0);
            var s = new float[2];
            var q = new Quat[2];
            var n = 0;
            for (var i = 0; i < 400; i++) if ((Step(a, 0.9, s, q) & TickEvents.Flinch) != 0) n++;
            Assert.True(a.Drive.Resolve > 0.5);
            return n;
        }

        Assert.True(Flinches(0.3) > 0);
        Assert.Equal(0, Flinches(0.0));
    }

    [Theory]
    [InlineData(0.5, 1.0)]
    [InlineData(1.0, 0.2)]
    [InlineData(0.5, 0.4)]
    public void The_voluntary_weight_reads_Will_so_a_lowered_slot_slackens_the_hold(double attention, double consciousness)
    {
        var a = Hold(attention, consciousness, new DriveParameters { WillCurve = 0.5 });
        var s = new float[2];
        var q = new Quat[2];
        for (var i = 0; i < 20; i++)
        {
            Step(a, 0.0, s, q);   // no stimulus, no leak
            Assert.Equal(0.7f * (float)Math.Sqrt(Math.Min(a.Drive.Resolve * attention, consciousness)), s[0]);
        }
    }

    [Fact]
    public void An_entered_goals_resolve_is_set_once_on_the_takeover_so_a_rage_with_effort_drains_the_will_it_was_given()
    {
        var rage = new GoalSpec
        {
            Id = "rage", Priority = 1.0, EntryResolve = 1.0, Effort = 0.5, InvoluntarySeconds = 0.3,
            When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) }, Behaviour = Pose,
        };
        var r = new PainBoxReplay(1, brain: new Brain { Goals = new[] { new GoalSpec { Id = "composure", Behaviour = Pose }, rage } });
        TickEvents e;
        do { e = r.Tick(true); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
        Assert.Equal(1.0, r.Actor.Drive.Resolve);

        var onsetTicks = 30;
        for (var i = 0; i < onsetTicks - 1; i++)
        {
            r.Tick(false);
            Assert.True(r.Actor.Drive.Resolve > 0.95);   // the onset runs on the refilled will without paying its effort
        }

        for (var i = 0; i < 170; i++) r.Tick(false);
        Assert.Equal(1, r.Actor.GoalIndex);
        Assert.True(r.Actor.Drive.Resolve < 0.3, $"resolve is {r.Actor.Drive.Resolve}: the entry effect ran again, or the effort never drained it");
    }

    [Fact]
    public void Each_entry_to_a_goal_sets_resolve_once_and_a_re_entry_sets_it_again()
    {
        var calm = new GoalSpec { Id = "calm", Behaviour = Pose };
        var rage = new GoalSpec { Id = "rage", Priority = 1.0, EntryResolve = 0.5, When = new[] { new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.5) }, Behaviour = Pose };
        var r = new PainBoxReplay(1, brain: new Brain { Goals = new[] { calm, rage } });
        var entries = 0;
        long sinceEntry = -1;
        var checkedInside = 0;
        for (var i = 0; i < 8000; i++)
        {
            var on = i < 800 || (i >= 4500 && i < 5300);
            var e = r.Tick(on);
            if ((e & TickEvents.GoalChanged) != 0 && r.Actor.GoalIndex == 1)
            {
                entries++;
                sinceEntry = 0;
                Assert.Equal(0.5, r.Actor.Drive.Resolve);
            }
            else if (sinceEntry >= 0 && r.Actor.GoalIndex == 1 && ++sinceEntry == 100)
            {
                Assert.True(r.Actor.Drive.Resolve < 0.499, $"resolve is {r.Actor.Drive.Resolve} 100 ticks into the goal: it was set again");
                checkedInside++;
            }
        }

        Assert.Equal(2, entries);
        Assert.Equal(2, checkedInside);
    }

    [Fact]
    public void A_goal_whose_entry_resolve_is_exactly_WillMendsAt_mends_the_will()
    {
        var rage = new GoalSpec { Id = "rage", Priority = 1.0, EntryResolve = 0.5, When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) }, Behaviour = Pose };
        var r = new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = 0.5 }, brain: new Brain { Goals = new[] { new GoalSpec { Id = "composure", Behaviour = Pose }, rage } });
        TickEvents e;
        do { e = r.Tick(true); } while ((e & TickEvents.GoalChanged) == 0 && r.Actor.TickCount < 5000);
        Assert.Equal(0.5, r.Actor.Drive.Resolve);
        Assert.False(r.Actor.Drive.WillBroken);
    }

    [Fact]
    public void Recovery_that_lands_exactly_on_WillMendsAt_mends_the_will_on_that_tick()
    {
        // Run once with a will that never mends to record resolve; then set WillMendsAt to a value that trace reaches exactly.
        var trace = new List<double>();
        var first = new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = 1.0 }, brain: Gate(DriveQuantity.Breaks, 99.0));
        while (!first.Actor.Drive.WillBroken) first.Tick(true);
        for (var i = 0; i < 7000; i++) { first.Tick(false); trace.Add(first.Actor.Drive.Resolve); }
        var j = 6500;
        var at = trace[j];
        Assert.True(at > trace[j - 1] && at > 0.0 && at < 1.0, "the trace is not recovering at the probe tick");
        var k = trace.FindIndex(x => x >= at);
        Assert.Equal(j, k);

        var second = new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = at }, brain: Gate(DriveQuantity.Breaks, 99.0));
        while (!second.Actor.Drive.WillBroken) second.Tick(true);
        for (var i = 0; i < 7000; i++)
        {
            second.Tick(false);
            Assert.Equal(i < k, second.Actor.Drive.WillBroken);
        }
    }
}
