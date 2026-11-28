using Lodur;
using static Lodur.Core.Tests.PhysioFixtures;

namespace Lodur.Core.Tests;

/// <summary>The Actor steps its Physiology first, writes the Consciousness slot from it, scales strengths by the motor ceiling, and goes limp when it dies.</summary>
public sealed class ActorPhysiologyTests
{
    private static readonly float[] NoPoint = { 0f };

    private static Brain HoldBrain() => new()
    {
        Goals = new[] { new GoalSpec { Id = "g", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = new HoldParameters { HoldStrength = 0.7f } } } },
    };

    private static Actor Make(Physiology? physiology, DriveParameters? drive = null) =>
        new(0.01, 1, new NerveParameters(), new WithdrawalParameters(), drive ?? new DriveParameters(), HoldBrain(), physiology: physiology);

    private static TickEvents Tick(Actor a, double intensity, float[] strengths, Quat[] targets)
    {
        var box = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) { Intensity = intensity } };
        return a.Tick(NoPoint, NoPoint, NoPoint, box, strengths, targets);
    }

    [Fact]
    public void The_slot_is_the_physiologys_consciousness_every_tick_and_strengths_are_held_to_each_joints_ceiling()
    {
        var p = Make3();
        Assert.True(p.Afflict(Af("Lesion"), 0, BrainLayer, 4));       // consciousness 0.6
        Assert.True(p.Afflict(Af("MotorHalf"), 1, 0, 1));             // joint 0 at half
        var a = Make(p, new DriveParameters { WillCurve = 0.5 });
        var s = new float[2];
        var q = new Quat[2];
        for (var i = 0; i < 50; i++)
        {
            Tick(a, 0.0, s, q);
            Assert.Equal(p.Consciousness, a.Drive.Consciousness);
            Assert.Equal(0.6, p.Consciousness, 12);
            var raw = 0.7f * (float)Math.Sqrt(a.Drive.Will);
            Assert.Equal(raw * (float)p.MotorCeiling(0), s[0]);
            Assert.Equal(raw * (float)p.MotorCeiling(1), s[1]);
            Assert.Equal(0.3, p.MotorCeiling(0), 12);
            Assert.Equal(0.6, p.MotorCeiling(1), 12);
        }
    }

    private static Physiology Make3() => PhysioFixtures.Make();

    [Fact]
    public void An_actor_with_no_physiology_keeps_the_slot_at_one_and_scales_nothing()
    {
        var a = Make(null);
        var s = new float[2];
        var q = new Quat[2];
        for (var i = 0; i < 20; i++) Tick(a, 0.0, s, q);
        Assert.Equal(1.0, a.Drive.Consciousness);
        Assert.Equal(0.7f, s[0]);
        Assert.Null(a.Physiology);
    }

    [Fact]
    public void A_physiology_bound_to_one_actor_is_refused_by_a_second_and_strengths_of_the_wrong_length_throw()
    {
        var p = Make3();
        var a = Make(p);
        Assert.Same(p, a.Physiology);
        Assert.Throws<ArgumentException>(() => Make(p));

        Assert.True(p.Afflict(Af("Bleeder"), 1, 0, 1));
        var q = new Quat[2];
        Assert.Throws<ArgumentException>(() => Tick(a, 0.0, new float[1], q));
        Assert.Throws<ArgumentException>(() => Tick(a, 0.0, new float[3], q));
        Assert.Equal(0, a.TickCount);   // a refused call changes nothing, the physiology included
        Assert.Equal(0.0, p.SlotAge(0));
        Assert.Equal(0.005, p.BloodVolume);
    }

    [Fact]
    public void Death_is_reported_once_and_is_limp_and_the_actor_never_changes_again()
    {
        var p = Make3();
        var a = Make(p);
        var s = new float[2];
        var q = new Quat[2] { Quat.FromAxisAngle(1f, 0f, 0f, 0.5f), Quat.FromAxisAngle(0f, 1f, 0f, 0.5f) };
        for (var i = 0; i < 100; i++) Assert.Equal(TickEvents.None, Tick(a, 0.8, s, q) & TickEvents.Died);
        Assert.True(s[0] > 0f && a.Drive.Pain > 0.0);
        q[0] = Quat.FromAxisAngle(0f, 0f, 1f, 0.3f);       // a value no behaviour writes: a dead actor leaves targets as they are
        q[1] = Quat.FromAxisAngle(0f, 1f, 0f, 0.4f);
        var sentinel = q.ToArray();

        Assert.True(p.Afflict(Af("Lesion"), 0, Heart, 10));
        var died = Tick(a, 0.8, s, q);
        Assert.Equal(TickEvents.Died, died);
        Assert.Equal(DeathCause.VitalTissue, p.Cause);
        Assert.Equal(new float[2], s);
        Assert.Equal(sentinel, q);                          // targets are left as they were
        var frozen = a.Drive;
        var goal = a.GoalIndex;
        var ticks = a.TickCount;
        for (var i = 0; i < 200; i++)
        {
            s[0] = s[1] = 0.5f;
            Assert.Equal(TickEvents.None, Tick(a, 0.8, s, q));
            Assert.Equal(new float[2], s);
            Assert.Equal(frozen, a.Drive);
            Assert.Equal(goal, a.GoalIndex);
        }

        Assert.Equal(ticks + 200, a.TickCount);
        Assert.Equal(sentinel, q);
        Assert.Equal(0.0, frozen.Consciousness);
        Assert.Equal(0.0, frozen.Will);
    }

    [Fact]
    public void A_dead_physiologys_flows_stand_on_the_tick_it_died_and_read_zero_after()
    {
        var p = new Physiology(Body3(), Plan3(blood: new BloodDef(1000.0, 0.5, 1e-4), slots: 9, organism: new[] { Hypoxia }), 20);
        Assert.True(p.Afflict(Af("Bleeder"), 1, 0, 0.8));
        var a = Make(p);
        var s = new float[2];
        var q = new Quat[2];
        for (var i = 0; i < 10; i++) Assert.Equal(TickEvents.None, Tick(a, 0.0, s, q) & TickEvents.Died);
        Assert.True(p.Bleed(1) > 0.0);
        Assert.True(p.Afflict(Af("Lesion"), 0, BrainLayer, 10));   // not the heart: a stopped heart would stop the bleed on the dying step
        Assert.Equal(TickEvents.Died, Tick(a, 0.0, s, q));
        Assert.True(p.Dead);
        Assert.True(p.Bleed(1) > 0.0, "the dying step's bleed was not reported");   // Lodur reports the step it died on
        Assert.Equal(TickEvents.None, Tick(a, 0.0, s, q));
        Assert.Equal(0.0, p.Bleed(1));
        Assert.Equal(0.0, p.AirwayBleed);
        Assert.Equal(0.0, p.DrawnLive);
        Assert.Equal(0.0, p.DrawnPooled);
        Assert.Equal(0.0, p.Coughed);
        Assert.Equal(0.0, p.Drained);
    }

    [Fact]
    public void A_physiology_that_died_before_it_was_bound_reports_the_death_on_the_first_tick()
    {
        var p = Make3();
        Assert.True(p.Afflict(Af("Lesion"), 0, Heart, 10));
        p.Step(0.01);
        Assert.True(p.Dead);
        var a = Make(p);
        var s = new float[2] { 1f, 1f };
        Assert.Equal(TickEvents.Died, Tick(a, 0.0, s, new Quat[2]));
        Assert.Equal(new float[2], s);
        Assert.Equal(TickEvents.None, Tick(a, 0.0, s, new Quat[2]));
    }

    [Fact]
    public void The_actor_reports_its_last_ticks_strength_as_effort()
    {
        double Oxygen(float hold)
        {
            var brain = new Brain
            {
                Goals = new[] { new GoalSpec { Id = "g", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = new HoldParameters { HoldStrength = hold } } } },
            };
            var p = Make3();
            Assert.True(p.Afflict(Af("Choke"), 0, 0, 1e9));
            var a = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain, physiology: p);
            var s = new float[2];
            var q = new Quat[2];
            for (var i = 0; i < 3000; i++) Tick(a, 0.0, s, q);
            Assert.False(p.Dead);
            return p.BloodOxygen;
        }

        var strong = Oxygen(0.7f);
        var idle = Oxygen(0.0f);
        Assert.True(strong < idle, $"strong {strong}, idle {idle}");
    }

    [Fact]
    public void The_actor_steps_its_physiology_on_the_drives_arousal_and_the_strength_it_applied_after_the_ceiling()
    {
        Physiology Body()
        {
            var q = Make3();
            Assert.True(q.Afflict(Af("Choke"), 0, 0, 1e9));      // the stores move every tick
            Assert.True(q.Afflict(Af("MotorHalf"), 1, 0, 1));    // joint 0 at half: the ceiling makes the applied strength differ from the asked one
            return q;
        }

        var p = Body();
        var twin = Body();
        var a = new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), HoldBrain(),
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0 }, 0, physiology: p);
        var loud = SensoryRig.One(0.55);
        var s = new float[2];
        var q = new Quat[2];
        double arousalBefore = 0.0, effortBefore = 0.0, peakArousal = 0.0;
        var peakEffort = 0.0;
        for (var i = 0; i < 1500; i++)
        {
            twin.Step(0.01, new PhysiologyInput(arousalBefore, effortBefore));
            a.Tick(NoPoint, NoPoint, NoPoint, ReadOnlySpan<BoxStimulus>.Empty, s, q, loud);
            Assert.Equal(twin.BloodOxygen, p.BloodOxygen);
            Assert.Equal(twin.BrainOxygen, p.BrainOxygen);
            arousalBefore = a.Drive.Arousal;
            effortBefore = ((double)s[0] + s[1]) / 2.0;
            peakArousal = Math.Max(peakArousal, arousalBefore);
            peakEffort = Math.Max(peakEffort, effortBefore);
        }

        Assert.True(peakArousal > 0.1, $"arousal never rose: {peakArousal}");
        Assert.True(peakEffort > 0.1, $"no strength was applied: {peakEffort}");
    }

    [Fact]
    public void A_healthy_physiology_changes_nothing_bit_for_bit()
    {
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var plain = new PainBoxReplay(seed);
            var with = new PainBoxReplay(seed, physiology: Make3());
            for (var i = 0; i < 4000; i++)
            {
                var on = i < 2500;
                Assert.Equal(plain.Tick(on), with.Tick(on));
                var a = plain.Actor.Drive;
                var b = with.Actor.Drive;
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Pain), BitConverter.DoubleToInt64Bits(b.Pain));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Strain), BitConverter.DoubleToInt64Bits(b.Strain));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Resolve), BitConverter.DoubleToInt64Bits(b.Resolve));
                Assert.Equal(a.Consciousness, b.Consciousness);
                Assert.Equal(plain.Actor.GoalIndex, with.Actor.GoalIndex);
                Assert.Equal(plain.Strengths, with.Strengths);
            }

            Assert.True(plain.Actor.Drive.Breaks > 0 && plain.Actor.GoalIndex == 1, "the replay never reached the withdraw goal");
        }
    }

    /// <summary>The measuring loop lives here, fully optimised from its first call, so tiered compilation and TieredPGO instrumentation cannot allocate inside the measurement; only Actor.Tick's own bytes are counted.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long AllocatedByTicking(Actor a, BoxStimulus[] box, float[] s, Quat[] q, int n)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++) a.Tick(NoPoint, NoPoint, NoPoint, box, s, q);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void A_tick_with_a_physiology_and_eight_bleeding_afflictions_allocates_nothing()
    {
        var p = new Physiology(Body3(), Plan3(blood: new BloodDef(1000.0, 0.5, 1e-4), slots: 9, organism: new[] { Hypoxia }), 20);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(p.Afflict(Af("Bleeder"), 1 + i % 2, 0, 0.5 + 0.1 * i));
            Assert.True(p.Afflict(Af("Clot"), 0, RootMuscle, 1.0));
        }

        Assert.True(p.Afflict(Af("Choke"), 0, 0, 0.8));      // the gases run and the hypoxia curve reads, through the warm-up and into the measured ticks

        var a = Make(p);
        var s = new float[2];
        var q = new Quat[2];
        var box = new[] { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) { Intensity = 0.5 } };
        for (var i = 0; i < 2000; i++) a.Tick(NoPoint, NoPoint, NoPoint, box, s, q);   // warm the JIT and the first-use paths

        Assert.Equal(0L, AllocatedByTicking(a, box, s, q, 10000));
        Assert.False(p.Dead);
        Assert.Equal(8, Live(p));
        Assert.True(p.Bleed(0) > 0.0 && p.Bleed(1) > 0.0);
    }
}
