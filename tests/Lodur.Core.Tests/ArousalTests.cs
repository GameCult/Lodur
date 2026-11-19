using Lodur;

namespace Lodur.Core.Tests;

/// <summary>An Actor with one region at the origin, a sensory nerve with sensitisation off, and a settable emitter list.</summary>
internal sealed class SensoryRig
{
    public const double Dt = 0.01;

    public readonly Actor Actor;
    public readonly Emitter[] Emitters;
    public readonly float[] Strengths = new float[2];
    public readonly Quat[] Targets = new Quat[2];
    private readonly float[] _p = { 0f };

    /// <param name="heard">False builds the Actor without senses, so the emitters have no one to reach.</param>
    public SensoryRig(Emitter[] emitters, double pull = 0.5, double drainTrait = 1.0, DriveParameters? drive = null, bool heard = true)
    {
        Emitters = emitters;
        Actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(),
            (drive ?? new DriveParameters()) with { ObjectivePull = pull, DrainTrait = drainTrait }, new HoldParameters(),
            PainBoxReplay.Hold, PainBoxReplay.Withdraw,
            heard ? new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 } : null, 0);
    }

    /// <summary>One audio emitter on the sense region whose afferent settles at (intensity - 0.05).</summary>
    public static Emitter[] One(double intensity) => new[] { new Emitter(Channel.Audio, 0f, 0f, 0f, 1f, intensity) };

    public TickEvents Tick() => Actor.Tick(_p, _p, _p, ReadOnlySpan<BoxStimulus>.Empty, Strengths, Targets, Emitters);

    public long TicksToBreak(double maxSeconds)
    {
        var max = (long)Math.Round(maxSeconds / Dt);
        for (long i = 0; i < max; i++)
        {
            if ((Tick() & TickEvents.WillBroke) != 0) return i;
        }

        return -1;
    }
}

public sealed class ArousalTests
{
    private const double Load = 0.5; // an emitter of 0.55 through a 0.05 threshold, no sensitisation, gain 1

    [Fact]
    public void Arousal_rises_under_constant_load_toward_its_equilibrium_and_decays_at_the_decay_rate_after_it_stops()
    {
        var p = new DriveParameters();
        var rig = new SensoryRig(SensoryRig.One(0.55), pull: 1.0); // pull 1: no drain, so only arousal is watched
        double last = 0;
        for (var i = 0; i < 20000; i++)
        {
            rig.Tick();
            Assert.True(rig.Actor.Drive.Arousal >= last - 1e-15, "arousal never falls under constant load");
            last = rig.Actor.Drive.Arousal;
        }

        Assert.Equal(Load, rig.Actor.Load, 12);
        var equilibrium = p.ArousalRise * Load / (p.ArousalRise * Load + p.ArousalDecay);
        Assert.Equal(equilibrium, rig.Actor.Drive.Arousal, 9);

        rig.Emitters[0].Intensity = 0.0;
        while (rig.Actor.Load > 0.0) rig.Tick(); // the last of the signal in flight arrives
        var a0 = rig.Actor.Drive.Arousal;
        Assert.True(a0 > 0.5 * equilibrium);
        for (var i = 0; i < 1000; i++) rig.Tick();
        Assert.Equal(a0 * Math.Pow(1.0 - SensoryRig.Dt * p.ArousalDecay, 1000), rig.Actor.Drive.Arousal, 12);
    }

    [Fact]
    public void Under_the_same_load_a_weaker_pull_breaks_first_pull_one_never_drains_and_the_trait_scales_the_drain()
    {
        long Break(double pull, double trait = 1.0) => new SensoryRig(SensoryRig.One(0.55), pull, trait).TicksToBreak(3000.0);

        long p3 = Break(0.3), p6 = Break(0.6);
        Assert.True(p3 > 0 && p3 < p6, $"pull: {p3} {p6}");

        var held = new SensoryRig(SensoryRig.One(0.55), pull: 1.0);
        Assert.Equal(-1, held.TicksToBreak(3000.0));
        Assert.Equal(1.0, held.Actor.Drive.Resolve);

        long t2 = Break(0.6, 2.0);
        Assert.True(t2 > 0 && t2 < p6, $"trait: {t2} {p6}");
        Assert.InRange((double)t2 / p6, 0.35, 0.6); // double the drain, about half the time (arousal needs a moment to rise)
    }

    [Fact]
    public void Resolve_does_not_recover_while_arousal_is_at_the_recovery_ceiling_or_above_and_does_after()
    {
        var drive = new DriveParameters { ResolveRecovery = 0.5 }; // strong enough that a gate on pain alone would show at once
        var ceiling = drive.ResolveRecoversBelowArousal;
        var rig = new SensoryRig(SensoryRig.One(0.55), pull: 0.5, drive: drive);
        double minResolve = 1.0;
        for (var i = 0; i < 2000; i++) { rig.Tick(); minResolve = Math.Min(minResolve, rig.Actor.Drive.Resolve); }
        Assert.True(minResolve < 0.95 && !rig.Actor.Drive.WillBroken);

        rig.Emitters[0].Intensity = 0.0;
        var rosePastCeiling = false;
        var settled = false;
        var last = rig.Actor.Drive.Resolve;
        for (var i = 0; i < 20000; i++)
        {
            rig.Tick();
            var a = rig.Actor.Drive.Arousal;
            var r = rig.Actor.Drive.Resolve;
            if (a >= ceiling)
            {
                Assert.True(r <= last, $"resolve rose at arousal {a}");
                settled = true;
            }
            else if (settled && r > last)
            {
                rosePastCeiling = true;
            }

            last = r;
        }

        Assert.True(settled && rosePastCeiling, "resolve recovers once arousal is below the ceiling");
        Assert.True(rig.Actor.Drive.Resolve > minResolve);
    }

    [Fact]
    public void Arousal_updates_before_it_drains_resolve_on_the_same_tick()
    {
        var rig = new SensoryRig(SensoryRig.One(0.55), pull: 0.5);
        for (var i = 0; i < 100; i++)
        {
            rig.Tick();
            if (rig.Actor.Load > 0.0) break;
            Assert.Equal(0.0, rig.Actor.Drive.Arousal);
            Assert.Equal(1.0, rig.Actor.Drive.Resolve);
        }

        Assert.True(rig.Actor.Load > 0.0, "the load arrives within the conduction delay");
        // On the tick the load first arrives, arousal has already risen and already drained: read a tick late, resolve would still be 1.
        Assert.True(rig.Actor.Drive.Arousal > 0.0);
        Assert.True(rig.Actor.Drive.Resolve < 1.0);
    }

    [Fact]
    public void Without_emitters_or_without_senses_arousal_and_load_stay_exactly_zero()
    {
        var silent = new SensoryRig(Array.Empty<Emitter>());
        var deaf = new SensoryRig(SensoryRig.One(1.0), heard: false);
        for (var i = 0; i < 10000; i++)
        {
            Assert.Equal(TickEvents.None, silent.Tick());
            Assert.Equal(TickEvents.None, deaf.Tick());
            Assert.Equal(0.0, silent.Actor.Drive.Arousal);
            Assert.Equal(0.0, deaf.Actor.Drive.Arousal);
            Assert.Equal(0.0, silent.Actor.Load);
            Assert.Equal(0.0, deaf.Actor.Load);
        }

        Assert.Equal(1.0, silent.Actor.Drive.Resolve);
        Assert.Equal(1.0, deaf.Actor.Drive.Resolve);
    }

    private static Emitter[] Three() => new[]
    {
        new Emitter(Channel.Audio, 0.2f, 0f, 0f, 1f, 0.5),
        new Emitter(Channel.Visual, 0f, 0.3f, 0f, 1f, 0.4),
        new Emitter(Channel.Interrupt, 0f, 0f, 0f, 1f, 0.6),
    };

    [Fact]
    public void Tick_with_three_emitters_allocates_nothing_in_steady_state_and_actors_do_not_share_state()
    {
        var r = new SensoryRig(Three());
        r.Tick();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) r.Tick();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);

        const int n = 2500;
        var a1 = new SensoryRig(Three());
        var b1 = new SensoryRig(SensoryRig.One(0.4), pull: 0.2, drainTrait: 1.3);
        var a2 = new SensoryRig(Three());
        var b2 = new SensoryRig(SensoryRig.One(0.4), pull: 0.2, drainTrait: 1.3);
        var ea = new TickEvents[n];
        var eb = new TickEvents[n];
        for (var i = 0; i < n; i++) { ea[i] = a1.Tick(); eb[i] = b1.Tick(); }
        for (var i = 0; i < n; i++) Assert.Equal(ea[i], a2.Tick());
        for (var i = 0; i < n; i++) Assert.Equal(eb[i], b2.Tick());

        static void Same(SensoryRig x, SensoryRig y)
        {
            Assert.Equal(x.Actor.Drive.Arousal, y.Actor.Drive.Arousal);
            Assert.Equal(x.Actor.Drive.Resolve, y.Actor.Drive.Resolve);
            Assert.Equal(x.Actor.Load, y.Actor.Load);
            foreach (var c in new[] { Channel.Audio, Channel.Visual, Channel.Interrupt })
            {
                Assert.Equal(x.Actor.ChannelStimulus(c), y.Actor.ChannelStimulus(c));
                Assert.Equal(x.Actor.ChannelAfferent(c), y.Actor.ChannelAfferent(c));
            }
        }

        Same(a1, a2);
        Same(b1, b2);
        Assert.True(a1.Actor.Drive.Arousal > 0.0);
    }
}
