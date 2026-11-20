using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The entry ramp (lodur:ruling:flinch-tail-after-stimulus): a goal's arousal and sensory-gain targets are reached linearly over EntrySeconds from
/// their values at takeover, so the stress bar and the flinch drive tail off rather than cut; after it the drive owns arousal.
/// </summary>
public sealed class EntryRampTests
{
    private const double Dt = 0.01;
    private const double Afferent = 0.5; // an emitter of 0.55 through a 0.05 threshold, no sensitisation, gain 1
    private static readonly PoseBehaviour Pose = new() { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw };

    /// <summary>Stress is the Start goal; calm takes over once arousal reaches 0.2 and holds (its dwell outlasts every test).</summary>
    private static Brain StressThenCalm(double entrySeconds, double gain = 0.0, double? arousal = 0.0) => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "stress", Behaviour = Pose },
            new GoalSpec
            {
                Id = "calm", Priority = 1.0, MinDwellSeconds = 1000.0, SensoryGain = gain, EntryArousal = arousal, EntrySeconds = entrySeconds,
                When = new[] { new Condition(DriveQuantity.Arousal, ConditionOp.AtLeast, 0.2) }, Behaviour = Pose,
            },
        },
    };

    private sealed class Rig
    {
        public readonly Actor Actor;
        private readonly Emitter[] _emitters = { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.55) };
        private readonly BoxStimulus[] _box = { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) };
        private readonly float[] _p = { 0f };
        private readonly float[] _strengths = new float[2];
        private readonly Quat[] _targets = new Quat[2];

        public Rig(Brain brain) =>
            Actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
                new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 }, 0);

        public double Pain { set => _box[0].Intensity = value; }

        public TickEvents Tick() => Actor.Tick(_p, _p, _p, _box, _strengths, _targets, _emitters);

        /// <summary>Ticks until the goal at <paramref name="goal"/> takes over; the takeover tick has just run.</summary>
        public void UntilTakeover(int goal)
        {
            for (var i = 0; i < 20000; i++)
            {
                if ((Tick() & TickEvents.GoalChanged) != 0 && Actor.GoalIndex == goal) return;
            }

            Assert.Fail("the goal never took over");
        }
    }

    [Fact]
    public void A_three_second_ramp_takes_arousal_and_load_linearly_to_exactly_zero_on_its_last_tick_and_not_before_and_they_stay_zero_under_steady_emitters()
    {
        var rig = new Rig(StressThenCalm(3.0));
        rig.UntilTakeover(1);
        var a0 = rig.Actor.Drive.Arousal;
        Assert.True(a0 >= 0.2);
        Assert.Equal(1.0, rig.Actor.Drive.SensoryGain);
        const int ticks = 300;
        for (var k = 1; k <= ticks + 500; k++)
        {
            rig.Tick();
            var ramp = Math.Max(0.0, 1.0 - (double)k / ticks);
            // Sense reads the gain the ramp left at the end of the previous tick.
            var senseGain = Math.Max(0.0, 1.0 - (double)(k - 1) / ticks);
            Assert.Equal(a0 * ramp, rig.Actor.Drive.Arousal, 12);
            Assert.Equal(ramp, rig.Actor.Drive.SensoryGain, 12);
            Assert.Equal(Afferent * senseGain, rig.Actor.Load, 12);
            Assert.Equal(0.55, rig.Actor.ChannelStimulus(Channel.Audio), 12);   // the world keeps sounding
            if (k < ticks) Assert.True(rig.Actor.Drive.Arousal > 0.0 && rig.Actor.Drive.SensoryGain > 0.0, $"still above zero at {k * Dt:0.00} s");
            if (k >= ticks)
            {
                Assert.Equal(0.0, rig.Actor.Drive.Arousal);
                Assert.Equal(0.0, rig.Actor.Drive.SensoryGain);
            }

            if (k > ticks) Assert.Equal(0.0, rig.Actor.Load);
        }
    }

    [Fact]
    public void A_ramp_of_zero_seconds_reaches_the_targets_on_the_takeover_tick()
    {
        var rig = new Rig(StressThenCalm(0.0));
        rig.UntilTakeover(1);
        Assert.Equal(0.0, rig.Actor.Drive.Arousal);
        Assert.Equal(0.0, rig.Actor.Drive.SensoryGain);
        for (var i = 0; i < 300; i++)
        {
            rig.Tick();
            Assert.Equal(0.0, rig.Actor.Load);
            Assert.Equal(0.0, rig.Actor.Drive.Arousal);
        }
    }

    [Fact]
    public void The_ramp_length_is_rounded_to_whole_ticks_so_a_quotient_just_under_an_integer_gets_that_integer()
    {
        var rig = new Rig(StressThenCalm(0.29));   // 0.29 / 0.01 is 28.999999999999996 ticks, not 28
        rig.UntilTakeover(1);
        for (var k = 1; k <= 29; k++)
        {
            rig.Tick();
            if (k < 29) Assert.True(rig.Actor.Drive.SensoryGain > 0.0, $"still ramping at tick {k}");
        }

        Assert.Equal(0.0, rig.Actor.Drive.SensoryGain);
        Assert.Equal(0.0, rig.Actor.Drive.Arousal);
    }

    [Fact]
    public void A_goal_without_an_entry_arousal_ramps_only_the_gain_and_the_drive_keeps_arousal()
    {
        var rig = new Rig(StressThenCalm(2.0, gain: 0.0, arousal: null));
        rig.UntilTakeover(1);
        var before = rig.Actor.Drive.Arousal;
        rig.Tick();
        // One tick of the drive's own law under the gate (the sense still reads gain 1 on the first tick after takeover): arousal moves by it, not by any ramp.
        var p = new DriveParameters();
        var expected = Math.Min(Math.Max(before + Dt * (p.ArousalRise * Afferent * (1.0 - before) - p.ArousalDecay * before), 0.0), 1.0);
        Assert.Equal(expected, rig.Actor.Drive.Arousal, 12);
        for (var i = 0; i < 6000; i++) rig.Tick();
        Assert.Equal(0.0, rig.Actor.Drive.SensoryGain);
        Assert.True(rig.Actor.Drive.Arousal < 0.01, "with the gain at zero the drive decays arousal on its own");
    }

    [Fact]
    public void A_goal_that_takes_over_mid_ramp_ramps_from_where_the_last_ramp_had_got_to()
    {
        var brain = new Brain
        {
            Goals = new[]
            {
                new GoalSpec { Id = "stress", Behaviour = Pose },
                new GoalSpec
                {
                    Id = "calm", Priority = 1.0, MinDwellSeconds = 1000.0, SensoryGain = 0.0, EntryArousal = 0.0, EntrySeconds = 3.0,
                    When = new[] { new Condition(DriveQuantity.Arousal, ConditionOp.AtLeast, 0.2) }, Behaviour = Pose,
                },
                new GoalSpec
                {
                    Id = "alert", Priority = 2.0, SensoryGain = 1.0, EntryArousal = 0.9, EntrySeconds = 2.0,
                    When = new[] { new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.05) }, Behaviour = Pose,
                },
            },
        };
        var rig = new Rig(brain);
        rig.UntilTakeover(1);
        for (var i = 0; i < 100; i++) rig.Tick();   // 1 s into the 3 s ramp
        Assert.True(rig.Actor.Drive.SensoryGain is > 0.0 and < 1.0);
        rig.Pain = 0.8;
        rig.UntilTakeover(2);
        var g0 = rig.Actor.Drive.SensoryGain;
        var a0 = rig.Actor.Drive.Arousal;
        Assert.True(g0 is > 0.0 and < 0.7, "alert took over partway down calm's ramp");
        for (var k = 1; k <= 200; k++)
        {
            rig.Tick();
            var f = k / 200.0;
            Assert.Equal(g0 + (1.0 - g0) * f, rig.Actor.Drive.SensoryGain, 12);
            Assert.Equal(a0 + (0.9 - a0) * f, rig.Actor.Drive.Arousal, 12);
        }

        Assert.Equal(1.0, rig.Actor.Drive.SensoryGain);
    }

    [Fact]
    public void The_start_goals_targets_hold_from_construction_and_under_steady_emitters()
    {
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "calm", SensoryGain = 0.0, EntryArousal = 0.0, EntrySeconds = 3.0, Behaviour = Pose } } };
        var rig = new Rig(brain);
        Assert.Equal(0.0, rig.Actor.Drive.SensoryGain);
        Assert.Equal(0.0, rig.Actor.Drive.Arousal);
        for (var i = 0; i < 600; i++)
        {
            rig.Tick();
            Assert.Equal(0.0, rig.Actor.Load);
            Assert.Equal(0.0, rig.Actor.Drive.Arousal);
        }

        var warm = new Rig(new Brain { Goals = new[] { new GoalSpec { Id = "wound", EntryArousal = 0.6, Behaviour = Pose } } });
        Assert.Equal(0.6, warm.Actor.Drive.Arousal);
    }

    [Fact]
    public void EntryArousal_outside_zero_to_one_and_a_negative_NaN_or_infinite_EntrySeconds_are_refused_naming_the_goal()
    {
        void Refused(GoalSpec g)
        {
            var ex = Assert.Throws<ArgumentException>(() => new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), new Brain { Goals = new[] { g } }));
            Assert.Contains("bad", ex.Message);
        }

        GoalSpec Base() => new() { Id = "bad", Behaviour = Pose };
        Refused(Base() with { EntryArousal = -0.01 });
        Refused(Base() with { EntryArousal = 1.01 });
        Refused(Base() with { EntryArousal = double.NaN });
        Refused(Base() with { EntrySeconds = -0.01 });
        Refused(Base() with { EntrySeconds = double.NaN });
        Refused(Base() with { EntrySeconds = double.PositiveInfinity });
        _ = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), new Brain { Goals = new[] { Base() with { EntryArousal = 0.0, EntrySeconds = 5.0 } } });
        _ = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), new Brain { Goals = new[] { Base() with { EntryArousal = 1.0 } } });
    }
}
