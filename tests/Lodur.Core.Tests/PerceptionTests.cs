using Lodur;

namespace Lodur.Core.Tests;

public sealed class PerceptionTests
{
    private static readonly float[] Zero = { 0f };

    private static double AfferentAt(Perception p, BoxStimulus[] src)
    {
        p.Sense(Zero, Zero, Zero, src);
        return p.Afferent(0);
    }

    [Theory]
    [InlineData(0.01, 0.06, 6)]
    [InlineData(0.005, 0.06, 12)]
    [InlineData(0.01, 0.0, 1)]
    public void Afferent_arrives_exactly_the_conduction_delay_after_a_step_stimulus(double dt, double delay, int ticks)
    {
        var p = new Perception(new NerveParameters { ConductionDelaySeconds = delay }, dt, 1);
        var src = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f, 0.05) }; // below the 0.1 threshold: nothing
        for (var i = 0; i < 20; i++) Assert.Equal(0.0, AfferentAt(p, src));

        src[0].Intensity = 0.8;
        for (var i = 0; i < ticks; i++) Assert.Equal(0.0, AfferentAt(p, src)); // still in flight
        Assert.True(AfferentAt(p, src) > 0.0);                                  // arrives on tick `ticks`
    }

    [Fact]
    public void A_sustained_stimulus_drives_sensitisation_to_its_cap_and_it_recovers_after()
    {
        var p = new Perception(new NerveParameters(), 0.01, 1);
        var src = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f, 0.8) };
        double a = 0;
        for (var i = 0; i < 20000; i++) a = AfferentAt(p, src);
        Assert.Equal(0.7 * 2.0, a, 9); // (stimulus - threshold) * SensitisationCap

        // Gone for 300 s: sensitisation is back near 1, so the same weak stimulus reads as weak.
        src[0].Intensity = 0.0;
        for (var i = 0; i < 30000; i++) AfferentAt(p, src);
        src[0].Intensity = 0.15;
        double first = 0;
        for (var i = 0; i < 7; i++) first = AfferentAt(p, src);
        Assert.InRange(first, 0.05, 0.0502);

        // Gone for one second only: still sensitised.
        var q = new Perception(new NerveParameters(), 0.01, 1);
        src[0].Intensity = 0.8;
        for (var i = 0; i < 20000; i++) AfferentAt(q, src);
        src[0].Intensity = 0.0;
        for (var i = 0; i < 100; i++) AfferentAt(q, src);
        src[0].Intensity = 0.15;
        for (var i = 0; i < 7; i++) first = AfferentAt(q, src);
        Assert.True(first > 0.09);
    }

    [Fact]
    public void A_point_outside_every_box_senses_zero_on_a_face_the_intensity_inside_two_the_larger()
    {
        var p = new Perception(new NerveParameters { ConductionDelaySeconds = 0.0 }, 0.01, 3);
        var src = new[]
        {
            new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f, 0.4),
            new BoxStimulus(0.5f, 0f, 0f, 1f, 1f, 1f, 0.9),
        };
        // outside both; on the face x = 1 of the first (inside the second); inside both
        float[] x = { 5f, 1f, 0.2f }, y = { 0f, 0f, 0f }, z = { 0f, 0f, 0f };
        p.Sense(x, y, z, src);
        Assert.Equal(0.0, p.Stimulus(0));
        Assert.Equal(0.9, p.Stimulus(1));
        Assert.Equal(0.9, p.Stimulus(2));

        src[1].Intensity = 0.0; // isolate the first box
        p.Sense(x, y, z, src);
        Assert.Equal(0.4, p.Stimulus(1)); // exactly on a face: inclusive
        Assert.Equal(0.4, p.Stimulus(2));
        x[1] = 1.0001f;
        p.Sense(x, y, z, src);
        Assert.Equal(0.0, p.Stimulus(1));
    }
}
