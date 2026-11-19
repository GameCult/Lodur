using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>
/// The pain box on the real body: a ground, a static stool welded to the pelvis (the PD ragdoll cannot balance for
/// 15 s), the ragdoll, a stimulus box around the right hand's hold point, and an Actor at 1/120 s driving the
/// ragdoll through strengths and targets only. The game (this scenario) owns the world and steps it.
/// </summary>
internal sealed class ConnectedPainBox : IDisposable
{
    public const double Dt = 1.0 / 120.0;
    public static readonly (float X, float Y, float Z) Box = (-0.21f, 1.19f, 0.46f);

    public readonly Box3DSoaWorld World;
    public readonly HumanoidRagdoll Ragdoll;
    public readonly Actor Actor;
    public readonly BoxStimulus[] Sources = { new BoxStimulus(Box.X, Box.Y, Box.Z, 0.12f, 0.12f, 0.12f) };
    public readonly float[] X = new float[2], Y = new float[2], Z = new float[2];
    public readonly float[] Strength = new float[HumanoidRagdoll.MuscleCount];
    public readonly Quat[] Target = new Quat[HumanoidRagdoll.MuscleCount];
    private SplitMix64 _rng;

    public ConnectedPainBox(ulong seed, HoldParameters? hold = null)
    {
        World = new Box3DSoaWorld(capacity: HumanoidRagdoll.LimbCount + 2, jointCapacity: HumanoidRagdoll.MuscleCount + 1);
        World.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = World.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        Ragdoll = HumanoidRagdoll.Build(World, (0f, 0f), firstStableId: 100);
        World.AddJoint(Box3DSoaJointKind.Weld, stool, Ragdoll.BodyLane(Limb.Pelvis), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

        var holdPose = new Quat[HumanoidRagdoll.MuscleCount];
        var withdrawPose = new Quat[HumanoidRagdoll.MuscleCount];
        Array.Fill(holdPose, Quat.Identity);
        Array.Fill(withdrawPose, Quat.Identity);
        holdPose[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, -1.0f);
        holdPose[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -0.6f);
        withdrawPose[(int)Muscle.ShoulderR] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
        withdrawPose[(int)Muscle.ElbowR] = Quat.FromAxisAngle(0f, 0f, 1f, -2.0f);

        _rng = new SplitMix64(seed);
        var gain = 1.0 + 0.2 * _rng.NextSigned();
        var drain = 1.0 + 0.2 * _rng.NextSigned();
        Actor = new Actor(Dt, HumanoidRagdoll.RegionCount, new NerveParameters { Gain = gain }, new WithdrawalParameters(),
            new DriveParameters { DrainTrait = drain }, hold ?? new HoldParameters(), holdPose, withdrawPose);
    }

    public Box3DSoaView View => World.View;

    /// <summary>Right hand point now.</summary>
    public (float X, float Y, float Z) Hand
    {
        get { Ragdoll.ReadRegions(X, Y, Z); return (X[(int)Region.HandR], Y[(int)Region.HandR], Z[(int)Region.HandR]); }
    }

    public bool HandInBox => Sources[0].Contains(Hand.X, Hand.Y, Hand.Z);

    /// <summary>One tick: the game writes the box intensity, the body reports its regions, the Actor decides, the body is driven, the world steps.</summary>
    public TickEvents Tick()
    {
        var tick = Actor.TickCount;
        var ramp = Math.Min(1.0, tick * Dt / 2.0);
        Sources[0].Intensity = Math.Max(0.0, 0.8 * ramp * (1.0 + 0.1 * _rng.NextSigned()));
        Ragdoll.ReadRegions(X, Y, Z);
        var events = Actor.Tick(X, Y, Z, Sources, Strength, Target);
        Ragdoll.Drive(Strength, Target);
        World.Step((float)Dt, 4);
        return events;
    }

    public void Dispose() => World.Dispose();
}

public sealed class ConnectedPainBoxTests
{
    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    public void ReadRegions_puts_the_hands_at_the_lower_arm_tips_at_the_creation_pose()
    {
        using var s = new RagdollScenario(seed: 1);
        var x = new float[2]; var y = new float[2]; var z = new float[2];
        s.Ragdoll.ReadRegions(x, y, z);
        Assert.Equal(0.21f, x[(int)Region.HandL], 3);
        Assert.Equal(-0.21f, x[(int)Region.HandR], 3);
        Assert.Equal(0.96f, y[(int)Region.HandR], 3);
        Assert.Equal(0f, z[(int)Region.HandR], 3);
        Assert.Throws<ArgumentException>(() => s.Ragdoll.ReadRegions(new float[1], y, z));
    }

    [Fact]
    public void ReadRegions_allocates_nothing()
    {
        using var s = new RagdollScenario(seed: 1);
        var x = new float[2]; var y = new float[2]; var z = new float[2];
        s.Ragdoll.ReadRegions(x, y, z);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) s.Ragdoll.ReadRegions(x, y, z);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed record Timeline(List<long> Flinches, long Broke, long Withdrew, List<(long Tick, (float X, float Y, float Z) Hand, bool InBox)> Hand, ulong Hash);

    private static Timeline Run(ulong seed, HoldParameters? hold = null, double maxSeconds = 25.0)
    {
        using var s = new ConnectedPainBox(seed, hold);
        var t = new Timeline(new List<long>(), -1, -1, new List<(long, (float, float, float), bool)>(), 0);
        long broke = -1, withdrew = -1;
        var max = (long)(maxSeconds / ConnectedPainBox.Dt);
        for (long i = 0; i < max && withdrew < 0; i++)
        {
            var e = s.Tick();
            if ((e & TickEvents.Flinch) != 0) t.Flinches.Add(i);
            if ((e & TickEvents.WillBroke) != 0) broke = i;
            if ((e & TickEvents.Withdrew) != 0) withdrew = i;
            t.Hand.Add((i, s.Hand, s.HandInBox));
        }
        // Keep running a little so the broken will has time to pull the arm out.
        for (var k = 0; k < 60; k++)
        {
            s.Tick();
            t.Hand.Add((t.Hand.Count, s.Hand, s.HandInBox));
        }
        return t with { Broke = broke, Withdrew = withdrew, Hash = RagdollScenario.HashOf(s.World) };
    }

    private static readonly (float X, float Y, float Z) Centre = ConnectedPainBox.Box;
    private const int Hz = 120;

    [Fact]
    public void The_reflex_leaks_through_real_muscle_strength_and_the_broken_will_pulls_the_arm_out()
    {
        var t = Run(seed: 1);
        Assert.True(t.Broke > 0, "the will never broke");
        Assert.True(t.Flinches.Count > 0);

        // The hand stays in the box until the will breaks (from 2 s on: before that the arm is still swinging up into it).
        for (var i = 2 * Hz; i < t.Broke; i++) Assert.True(t.Hand[i].InBox, $"hand left the box at tick {i}, will broke at {t.Broke}");

        // A flinch moves it: within 200 ms of some Flinch the hand is over 2.3 cm from its hold point (the box centre).
        var moved = t.Flinches.Any(f => t.Hand.Skip((int)f).Take(Hz / 5 + 1).Any(h => Dist(h.Hand, Centre) > 0.023f));
        Assert.True(moved, "no flinch moved the hand over 2.3 cm from the hold point");

        // The broken will pulls it out within 300 ms.
        Assert.Contains(t.Hand.Skip((int)t.Broke).Take(Hz * 3 / 10 + 1), h => !h.InBox);
    }

    [Fact]
    public void The_movement_is_the_leak_not_the_body_without_it_the_hand_holds_still_and_with_it_the_hand_is_pulled_away_by_the_last_flinch()
    {
        var plain = Run(seed: 1, new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 }, maxSeconds: 16.0);
        var full = Run(seed: 1);
        Assert.Equal(full.Flinches, plain.Flinches); // the nervous state does not depend on the arm while the hand is in the box

        // No leak, no shake: after settling, the hand stays within 2.3 cm of the hold point until the will breaks.
        for (var i = 3 * Hz; i < plain.Broke; i++) Assert.True(Dist(plain.Hand[i].Hand, Centre) <= 0.023f, $"drift {Dist(plain.Hand[i].Hand, Centre)} at tick {i}");

        // With the leak, at the last flinch the hand is over 2.3 cm from where the leak-free arm is at the same tick.
        var last = (int)full.Flinches[^1];
        Assert.True(Dist(full.Hand[last].Hand, plain.Hand[last].Hand) > 0.023f);
    }

    private static float VerticalPeakToPeak(Timeline t, int fromSecond)
    {
        var seg = t.Hand.Skip(fromSecond * Hz).Take(Hz).Select(h => h.Hand.Y).ToList();
        return seg.Max() - seg.Min();
    }

    [Fact]
    public void Strain_shows_as_a_visible_shake_of_the_hand_which_the_shipped_amplitude_makes_and_zero_does_not()
    {
        // Seconds 8 to 13 of seed 1: strain is about 3, the will has not broken (it breaks at 14.75 s). Measured at the
        // shipped StrainShake 0.03 / ShakeHz 3.5: 3.9-4.7 cm peak to peak in hand height per second; at 0, 0.3-0.7 cm.
        var shaking = Run(seed: 1, new HoldParameters(), maxSeconds: 16.0);
        var still = Run(seed: 1, new HoldParameters { StrainShake = 0.0 }, maxSeconds: 16.0);
        for (var s = 8; s <= 13; s++)
        {
            Assert.True(VerticalPeakToPeak(shaking, s) > 0.03f, $"shipped shake only {VerticalPeakToPeak(shaking, s)} m peak to peak in second {s}");
            Assert.True(VerticalPeakToPeak(still, s) < 0.015f, $"no shake still moved {VerticalPeakToPeak(still, s)} m in second {s}");
        }
    }

    [Fact]
    public void The_same_seed_twice_gives_the_same_events_and_the_same_world_state_hash()
    {
        var a = Run(seed: 1);
        var b = Run(seed: 1);
        Assert.Equal(a.Flinches, b.Flinches);
        Assert.Equal(a.Broke, b.Broke);
        Assert.Equal(a.Withdrew, b.Withdrew);
        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, Run(seed: 2).Hash);
    }
}
