using Lodur;

namespace Lodur.Core.Tests;

public sealed class SensoryPerceptionTests
{
    private const double Dt = 0.01;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = System.Array.Empty<BoxStimulus>();

    /// <summary>A sensory nerve with sensitisation off, so afferent = (stimulus - threshold) * gain.</summary>
    private static NerveParameters Plain(double threshold = 0.05, double delay = 0.0, double gain = 1.0) =>
        new NerveParameters { Threshold = threshold, SensitisationGrowth = 0.0, ConductionDelaySeconds = delay, Gain = gain };

    private static Perception Senses(NerveParameters senses, int regions = 1, int senseRegion = 0) =>
        new Perception(new NerveParameters(), Dt, regions, senses, senseRegion);

    private static void Sense(Perception p, Emitter[] emitters) => p.Sense(Zero, Zero, Zero, NoBoxes, emitters);

    [Fact]
    public void An_emitter_is_full_at_its_position_half_at_half_its_radius_and_zero_at_and_beyond_the_radius()
    {
        var e = new Emitter(Channel.Audio, 1f, 2f, 3f, 4f, 0.8);
        Assert.Equal(0.8, e.Received(1f, 2f, 3f), 12);
        Assert.Equal(0.4, e.Received(3f, 2f, 3f), 12);   // 2 m along x: half the radius
        Assert.Equal(0.4, e.Received(1f, 2f, 5f), 12);   // distance is Euclidean, not per axis
        Assert.Equal(0.0, e.Received(5f, 2f, 3f));       // exactly at the radius
        Assert.Equal(0.0, e.Received(9f, 2f, 3f));
        Assert.Equal(0.0, new Emitter(Channel.Audio, 0f, 0f, 0f, 0f, 0.8).Received(0f, 0f, 0f)); // no radius, no reach
    }

    [Fact]
    public void Within_a_channel_the_larger_emitter_wins_and_across_channels_the_signals_saturate()
    {
        var p = Senses(Plain());
        Sense(p, new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.3), new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.5) });
        Sense(p, new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.3), new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.5) });
        Assert.Equal(0.5, p.ChannelStimulus(Channel.Audio), 12);
        Assert.Equal(0.45, p.ChannelAfferent(Channel.Audio), 12); // not 0.3 + 0.5, not the sum of two afferents

        var q = Senses(Plain());
        var both = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.5), new Emitter(Channel.Visual, 0f, 0f, 0f, 1f, 0.35) };
        Sense(q, both);
        Sense(q, both);
        double a = 0.45, b = 0.30;
        Assert.Equal(a, q.ChannelAfferent(Channel.Audio), 12);
        Assert.Equal(b, q.ChannelAfferent(Channel.Visual), 12);
        Assert.Equal(0.0, q.ChannelAfferent(Channel.Interrupt));
        Assert.Equal(1.0 - (1.0 - a) * (1.0 - b), q.Load, 12);
        Assert.True(q.Load < a + b);

        // One afferent past 1 is capped before combining, so the load never leaves 0..1.
        var r = Senses(Plain(gain: 5.0));
        var loud = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.5), new Emitter(Channel.Interrupt, 0f, 0f, 0f, 1f, 0.2) };
        Sense(r, loud);
        Sense(r, loud);
        Assert.Equal(1.0, r.Load);
    }

    [Theory]
    [InlineData(0.01, 0.06, 6)]
    [InlineData(0.005, 0.06, 12)]
    [InlineData(0.01, 0.0, 1)]
    public void A_step_emitters_afferent_arrives_exactly_the_conduction_delay_later(double dt, double delay, int ticks)
    {
        var p = new Perception(new NerveParameters(), dt, 1, Plain(delay: delay), 0);
        var quiet = new[] { new Emitter(Channel.Visual, 0f, 0f, 0f, 1f, 0.01) }; // under the threshold
        for (var i = 0; i < 20; i++) { Sense(p, quiet); Assert.Equal(0.0, p.Load); }

        var loud = new[] { new Emitter(Channel.Visual, 0f, 0f, 0f, 1f, 0.8) };
        for (var i = 0; i < ticks; i++) { Sense(p, loud); Assert.Equal(0.0, p.Load); } // still in flight
        Sense(p, loud);
        Assert.True(p.Load > 0.0);
        Assert.Equal(0.0, p.ChannelAfferent(Channel.Audio));
    }

    [Fact]
    public void A_sustained_emitter_sensitises_its_channel_as_a_noxious_stimulus_does()
    {
        var p = Senses(new NerveParameters { Threshold = 0.1 });
        var e = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.8) };
        for (var i = 0; i < 20000; i++) Sense(p, e);
        Assert.Equal(0.7 * 2.0, p.ChannelAfferent(Channel.Audio), 9); // (stimulus - threshold) * SensitisationCap
    }

    [Fact]
    public void Only_the_sense_regions_point_decides_what_the_emitters_reach()
    {
        float[] x = { 0f, 0f }, y = { 0f, 0f }, z = { 0f, 0f };
        var emitters = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.8) };
        Perception Make() => Senses(Plain(), regions: 2, senseRegion: 1);

        // The sense region (1) is far from the emitter; region 0 sitting on it changes nothing.
        var far = Make();
        x[0] = 0f; x[1] = 5f;
        for (var i = 0; i < 5; i++) far.Sense(x, y, z, NoBoxes, emitters);
        Assert.Equal(0.0, far.ChannelStimulus(Channel.Audio));
        Assert.Equal(0.0, far.Load);

        // Moving the sense region next to it does.
        x[1] = 0f;
        for (var i = 0; i < 5; i++) far.Sense(x, y, z, NoBoxes, emitters);
        Assert.Equal(0.8, far.ChannelStimulus(Channel.Audio), 12);
        Assert.True(far.Load > 0.0);

        // And with region 0 far away and region 1 on it, it still reaches.
        var near = Make();
        x[0] = 5f; x[1] = 0f;
        for (var i = 0; i < 5; i++) near.Sense(x, y, z, NoBoxes, emitters);
        Assert.Equal(0.8, near.ChannelStimulus(Channel.Audio), 12);
    }

    [Fact]
    public void Without_senses_or_without_emitters_the_load_and_every_channel_stay_zero()
    {
        var deaf = new Perception(new NerveParameters(), Dt, 1);
        var loud = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 1.0) };
        var hearing = Senses(Plain());
        for (var i = 0; i < 10000; i++)
        {
            deaf.Sense(Zero, Zero, Zero, NoBoxes, loud);
            hearing.Sense(Zero, Zero, Zero, NoBoxes);
            Assert.Equal(0.0, deaf.Load);
            Assert.Equal(0.0, hearing.Load);
        }

        foreach (var c in new[] { Channel.Audio, Channel.Visual, Channel.Interrupt })
        {
            Assert.Equal(0.0, deaf.ChannelStimulus(c));
            Assert.Equal(0.0, deaf.ChannelAfferent(c));
            Assert.Equal(0.0, hearing.ChannelAfferent(c));
        }
    }

    [Fact]
    public void A_sense_region_outside_the_body_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Senses(Plain(), regions: 2, senseRegion: 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_sense_point_is_the_sense_regions_and_the_distance_counts_every_axis(int axis)
    {
        // Three regions; the sense region is the middle one. The first and last sit exactly on the emitter, the middle one half a radius away along one axis.
        float[] x = { 0f, 0f, 0f }, y = { 0f, 0f, 0f }, z = { 0f, 0f, 0f };
        var offset = new[] { x, y, z }[axis];
        offset[1] = 0.5f;
        var p = Senses(Plain(), regions: 3, senseRegion: 1);
        var emitters = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.8) };
        p.Sense(x, y, z, NoBoxes, emitters);
        Assert.Equal(0.4, p.ChannelStimulus(Channel.Audio), 12);
    }

    [Fact]
    public void An_emitter_with_a_channel_outside_the_three_is_refused_up_front_and_leaves_the_tick_unrun()
    {
        var p = Senses(Plain());
        var good = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.8) };
        for (var i = 0; i < 3; i++) Sense(p, good);
        var stimulus = p.ChannelStimulus(Channel.Audio);
        var afferent = p.ChannelAfferent(Channel.Audio);
        var load = p.Load;

        var bad = new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, 0.8), new Emitter((Channel)7, 0f, 0f, 0f, 1f, 0.8) };
        var e = Assert.Throws<ArgumentException>(() => Sense(p, bad));
        Assert.Contains("Emitter 1", e.Message);
        Assert.Equal(stimulus, p.ChannelStimulus(Channel.Audio));
        Assert.Equal(afferent, p.ChannelAfferent(Channel.Audio));
        Assert.Equal(load, p.Load);

        // The tick did not advance: a twin that never saw the bad call stays identical from here on.
        var twin = Senses(Plain());
        for (var i = 0; i < 3; i++) Sense(twin, good);
        for (var i = 0; i < 20; i++) { Sense(p, good); Sense(twin, good); Assert.Equal(twin.Load, p.Load); }
    }
}
