using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// A goal's sensory gain (lodur:ruling:stress-bar-shows-arousal-calm-silences) gates the sense channels' afferents after the conduction delay,
/// builds Load from the gated values, and never touches the world's stimulus or the nociceptive region afferents.
/// </summary>
public sealed class SensoryGainTests
{
    private const double Dt = 0.01;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = System.Array.Empty<BoxStimulus>();
    private static readonly Emitter[] Loud = { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.6), new Emitter(Channel.Visual, 0f, 0f, 0f, 1f, 0.4) };

    private static Perception Senses(NerveParameters senses, double delay = 0.0) =>
        new Perception(new NerveParameters(), Dt, 1, senses with { ConductionDelaySeconds = delay }, 0);

    [Fact]
    public void Gain_one_leaves_every_afferent_and_the_load_bit_identical_to_no_gain()
    {
        var a = Senses(new NerveParameters { Threshold = 0.05, SensitisationGrowth = 2.0 }, 0.03);
        var b = Senses(new NerveParameters { Threshold = 0.05, SensitisationGrowth = 2.0 }, 0.03);
        for (var i = 0; i < 300; i++)
        {
            a.Sense(Zero, Zero, Zero, NoBoxes, Loud);
            b.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
            Assert.Equal(a.Load, b.Load);
            Assert.Equal(a.ChannelAfferent(Channel.Audio), b.ChannelAfferent(Channel.Audio));
            Assert.Equal(a.ChannelAfferent(Channel.Visual), b.ChannelAfferent(Channel.Visual));
        }

        Assert.True(a.Load > 0.0);
    }

    [Fact]
    public void Gain_scales_each_channels_afferent_and_builds_the_load_from_the_scaled_afferents_while_the_stimulus_is_the_worlds()
    {
        var p = Senses(new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 });
        p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 0.5);
        p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 0.5);
        double audio = (0.6 - 0.05) * 0.5, visual = (0.4 - 0.05) * 0.5;
        Assert.Equal(audio, p.ChannelAfferent(Channel.Audio), 12);
        Assert.Equal(visual, p.ChannelAfferent(Channel.Visual), 12);
        Assert.Equal(1.0 - (1.0 - audio) * (1.0 - visual), p.Load, 12);
        Assert.Equal(0.6, p.ChannelStimulus(Channel.Audio), 12);

        p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 0.0);
        Assert.Equal(0.0, p.ChannelAfferent(Channel.Audio));
        Assert.Equal(0.0, p.ChannelAfferent(Channel.Visual));
        Assert.Equal(0.0, p.Load);
        Assert.Equal(0.6, p.ChannelStimulus(Channel.Audio), 12);
        Assert.Equal(0.4, p.ChannelStimulus(Channel.Visual), 12);
    }

    [Fact]
    public void The_gain_in_force_when_the_afferent_leaves_the_delay_applies_not_the_gain_when_the_stimulus_entered()
    {
        var p = Senses(new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 }, 0.05); // 5 ticks in flight
        for (var i = 0; i < 20; i++) p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
        Assert.True(p.ChannelAfferent(Channel.Audio) > 0.5);
        p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 0.0);   // the same signal, gated as it arrives
        Assert.Equal(0.0, p.ChannelAfferent(Channel.Audio));
        p.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
        Assert.Equal(0.55, p.ChannelAfferent(Channel.Audio), 12);
    }

    [Fact]
    public void Sensitisation_follows_the_world_not_the_gain()
    {
        var nerve = new NerveParameters { Threshold = 0.05, SensitisationGrowth = 2.0, SensitisationCap = 4.0 };
        var deaf = Senses(nerve);
        var hearing = Senses(nerve);
        for (var i = 0; i < 200; i++)
        {
            deaf.Sense(Zero, Zero, Zero, NoBoxes, Loud, 0.0);
            hearing.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
        }

        deaf.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
        hearing.Sense(Zero, Zero, Zero, NoBoxes, Loud, 1.0);
        Assert.Equal(hearing.ChannelAfferent(Channel.Audio), deaf.ChannelAfferent(Channel.Audio));
        Assert.True(deaf.ChannelAfferent(Channel.Audio) > 0.55, "the channel sensitised while it was gated");
    }

    private static Brain GainGoal(double gain, string id = "g") => new()
    {
        Goals = new[] { new GoalSpec { Id = id, SensoryGain = gain, Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw } } },
    };

    [Fact]
    public void A_goal_with_gain_zero_silences_load_and_channel_afferents_but_not_the_pain_path_or_the_world_stimulus()
    {
        var events = new List<TickEvents>[2] { new(), new() };
        var afferent = new List<double>[2] { new(), new() };
        var flinches = new int[2];
        var gains = new[] { 1.0, 0.0 };
        var p = new[] { 0f };
        for (var k = 0; k < 2; k++)
        {
            var actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters() with { ResolveDrainPain = 0.0, ResolveDrainStrain = 0.0, ResolveDrainArousal = 0.0 },
                GainGoal(gains[k]), new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 }, 0);
            actor.SetWillSlots(0.4, 1.0);   // a will that holds only part of the reflex down, so flinches leak
            Assert.Equal(gains[k], actor.Drive.SensoryGain);
            var strengths = new float[2];
            var targets = new Quat[2];
            var box = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) { Intensity = 0.8 } };
            for (var i = 0; i < 600; i++)
            {
                var e = actor.Tick(p, p, p, box, strengths, targets, Loud);
                events[k].Add(e);
                afferent[k].Add(actor.Afferent(0));
                if ((e & TickEvents.Flinch) != 0) flinches[k]++;
                if (i >= 10)
                {
                    Assert.Equal(0.6, actor.ChannelStimulus(Channel.Audio), 12);
                    if (k == 1)
                    {
                        Assert.Equal(0.0, actor.Load);
                        Assert.Equal(0.0, actor.ChannelAfferent(Channel.Audio));
                    }
                    else
                    {
                        Assert.True(actor.Load > 0.0);
                    }
                }
            }
        }

        Assert.True(flinches[1] > 0, "the fixture flinches");
        Assert.Equal(events[0], events[1]);
        Assert.Equal(afferent[0], afferent[1]);
    }

    [Fact]
    public void The_start_goals_gain_holds_at_construction_and_a_later_goal_sets_it_the_tick_it_takes_over()
    {
        var pose = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw };
        var brain = new Brain
        {
            Start = 0,
            Goals = new[]
            {
                new GoalSpec { Id = "a", SensoryGain = 0.25, Behaviour = pose },
                new GoalSpec { Id = "b", Priority = 1.0, SensoryGain = 0.75, When = new[] { new Condition(DriveQuantity.Pain, ConditionOp.AtLeast, 0.05) }, Behaviour = pose },
            },
        };
        var actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain);
        Assert.Equal(0.25, actor.Drive.SensoryGain);
        var p = new[] { 0f };
        var box = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) { Intensity = 0.9 } };
        var strengths = new float[2];
        var targets = new Quat[2];
        for (var i = 0; i < 2000; i++)
        {
            var e = actor.Tick(p, p, p, box, strengths, targets);
            if ((e & TickEvents.GoalChanged) != 0)
            {
                Assert.Equal(1, actor.GoalIndex);
                Assert.Equal(0.75, actor.Drive.SensoryGain);
                return;
            }

            Assert.Equal(0.25, actor.Drive.SensoryGain);
        }

        Assert.Fail("the second goal never took over");
    }

    [Fact]
    public void SensoryGain_outside_zero_to_one_or_not_a_number_is_refused_naming_the_goal()
    {
        void Refused(double gain)
        {
            var ex = Assert.Throws<ArgumentException>(() => new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), GainGoal(gain, "bad")));
            Assert.Contains("bad", ex.Message);
        }

        Refused(-0.01);
        Refused(1.01);
        Refused(double.NaN);
        Refused(double.PositiveInfinity);
        _ = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), GainGoal(0.0));
        _ = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), GainGoal(1.0));
    }
}
