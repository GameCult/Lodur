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
    /// <summary>
    /// The stimulus box's half extent, the smallest in 0.005 m steps that holds the shipped run's hand (seed 1) in the box from 2 s until the break. At e47e3d3 that was 0.125
    /// (0.120 let the palm out 4 ticks early). With the gravity feedforward the will's failing sag, which the break no longer outruns, leaves the box earlier:
    /// 0.125 lets it out 18 ticks early, 0.140 eight, 0.145 none. Limit 0.150: past it the hold no longer holds by intent.
    /// </summary>
    public const float BoxHalf = 0.145f;
    /// <summary>The hold point: where the hold pose is solved to put the strength-1 right palm (the shoulder-right and elbow-right angles below; settled seed 1, 3-4 s, no leak), no longer where the controller sags it to.</summary>
    public static readonly (float X, float Y, float Z) Box = (-0.210f, 1.116f, 0.441f);

    public readonly Box3DSoaWorld World;
    public readonly BodyPlan Plan = BodyPlans.Humanoid();
    public readonly Ragdoll Ragdoll;
    public readonly Actor Actor;
    public readonly BoxStimulus[] Sources = { new BoxStimulus(Box.X, Box.Y, Box.Z, BoxHalf, BoxHalf, BoxHalf) };
    public readonly float[] X, Y, Z;
    public readonly float[] Strength;
    public readonly Quat[] Target;
    private readonly int _handR;
    private SplitMix64 _rng;

    public ConnectedPainBox(ulong seed, HoldParameters? hold = null, DriveParameters? driveTunables = null)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        X = new float[Plan.SensePoints.Count]; Y = new float[Plan.SensePoints.Count]; Z = new float[Plan.SensePoints.Count];
        Strength = new float[Plan.Joints.Count];
        Target = new Quat[Plan.Joints.Count];
        _handR = Plan.SenseIndex("Hand", Side.Right);
        World = new Box3DSoaWorld(capacity: bodies + 2, jointCapacity: joints + 1);
        World.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = World.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        Ragdoll = Ragdoll.Build(World, Plan, (0f, 0f), firstStableId: 100);
        World.AddJoint(Box3DSoaJointKind.Weld, stool, Ragdoll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

        var holdPose = new Quat[Plan.Joints.Count];
        var withdrawPose = new Quat[Plan.Joints.Count];
        Array.Fill(holdPose, Quat.Identity);
        Array.Fill(withdrawPose, Quat.Identity);
        // The hold pose is solved by intent: the shoulder and elbow angles that put the settled strength-1 palm on Box (grid then refine over both, seed 1, 3-4 s,
        // box silent): -0.70 and -0.36, 0.34 cm off. At e47e3d3 the same fit was -1.0 and -0.6, a pose that gravity sagged onto Box; with the feedforward the
        // controller no longer sags, and -1.0, -0.6 would hold the palm well above it.
        holdPose[Plan.JointIndex("Shoulder", Side.Right)] = Quat.FromAxisAngle(1f, 0f, 0f, -0.70f);
        holdPose[Plan.JointIndex("Elbow", Side.Right)] = Quat.FromAxisAngle(0f, 0f, 1f, -0.36f);
        withdrawPose[Plan.JointIndex("Shoulder", Side.Right)] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
        withdrawPose[Plan.JointIndex("Elbow", Side.Right)] = Quat.FromAxisAngle(0f, 0f, 1f, -2.0f);

        _rng = new SplitMix64(seed);
        var gain = 1.0 + 0.2 * _rng.NextSigned();
        var drain = 1.0 + 0.2 * _rng.NextSigned();
        // The pain box as a Brain: hold against the reflex until the will breaks, then release into the withdraw pose.
        var tunables = hold ?? new HoldParameters();
        var brain = new Brain
        {
            Goals = new[]
            {
                new GoalSpec
                {
                    Id = "objective", Priority = 0.0, Pull = 0.5,
                    Behaviour = new PoseBehaviour { Keys = new[] { holdPose }, FlinchPose = withdrawPose, Hold = tunables },
                },
                new GoalSpec
                {
                    Id = "release", Priority = 1.0, Pull = 0.5, InvoluntarySeconds = double.PositiveInfinity,
                    When = new[] { new Condition(DriveQuantity.Breaks, ConditionOp.AtLeast, 1.0) },
                    Behaviour = new PoseBehaviour
                    {
                        Keys = new[] { withdrawPose }, FlinchPose = withdrawPose,
                        Hold = tunables with { FlinchGain = 0.0, StrainShake = 0.0 },
                    },
                },
            },
        };
        Actor = new Actor(Dt, Plan.SensePoints.Count, new NerveParameters { Gain = gain }, new WithdrawalParameters(),
            (driveTunables ?? new DriveParameters()) with { DrainTrait = drain }, brain);
    }

    public Box3DSoaView View => World.View;

    /// <summary>Right hand point now.</summary>
    public (float X, float Y, float Z) Hand
    {
        get { Ragdoll.ReadSensePoints(X, Y, Z); return (X[_handR], Y[_handR], Z[_handR]); }
    }

    public bool HandInBox => Sources[0].Contains(Hand.X, Hand.Y, Hand.Z);

    /// <summary>One tick: the game writes the box intensity, the body reports its regions, the Actor decides, the body is driven, the world steps.</summary>
    public TickEvents Tick()
    {
        var tick = Actor.TickCount;
        var ramp = Math.Min(1.0, tick * Dt / 2.0);
        Sources[0].Intensity = Math.Max(0.0, 0.8 * ramp * (1.0 + 0.1 * _rng.NextSigned()));
        Ragdoll.ReadSensePoints(X, Y, Z);
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
    public void ReadSensePoints_puts_the_hands_at_the_palms_at_the_creation_pose()
    {
        using var s = new RagdollScenario(seed: 1);
        var x = new float[3]; var y = new float[3]; var z = new float[3];
        s.Ragdoll.ReadSensePoints(x, y, z);
        Assert.Equal(0.21f, x[s.Plan.SenseIndex("Hand", Side.Left)], 3);
        Assert.Equal(-0.21f, x[s.Plan.SenseIndex("Hand", Side.Right)], 3);
        Assert.Equal(0.93f, y[s.Plan.SenseIndex("Hand", Side.Right)], 3);
        Assert.Equal(0f, z[s.Plan.SenseIndex("Hand", Side.Right)], 3);
        Assert.Equal(0f, x[s.Plan.SenseIndex("Head")], 3);   // the head body's centre, within a millimetre
        Assert.Equal(1.74f, y[s.Plan.SenseIndex("Head")], 3);
        Assert.Equal(0f, z[s.Plan.SenseIndex("Head")], 3);
        Assert.Throws<ArgumentException>(() => s.Ragdoll.ReadSensePoints(new float[1], y, z));
    }

    [Fact]
    public void ReadSensePoints_allocates_nothing()
    {
        using var s = new RagdollScenario(seed: 1);
        var x = new float[3]; var y = new float[3]; var z = new float[3];
        s.Ragdoll.ReadSensePoints(x, y, z);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) s.Ragdoll.ReadSensePoints(x, y, z);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed record Timeline(List<long> Flinches, long Broke, long Withdrew, List<(long Tick, (float X, float Y, float Z) Hand, bool InBox)> Hand, ulong Hash);

    private static Timeline Run(ulong seed, HoldParameters? hold = null, double maxSeconds = 25.0, DriveParameters? drive = null)
    {
        using var s = new ConnectedPainBox(seed, hold, drive);
        var t = new Timeline(new List<long>(), -1, -1, new List<(long, (float, float, float), bool)>(), 0);
        long broke = -1, withdrew = -1;
        var max = (long)(maxSeconds / ConnectedPainBox.Dt);
        for (long i = 0; i < max && withdrew < 0; i++)
        {
            var e = s.Tick();
            if ((e & TickEvents.Flinch) != 0) t.Flinches.Add(i);
            if ((e & TickEvents.WillBroke) != 0) broke = i;
            if ((e & TickEvents.GoalChanged) != 0) withdrew = i;
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
        // Both runs hold at full strength until the will is nearly gone (a WillCurve this small weights by ~1): the will's own
        // sag of the hold, which would move even a leak-free arm, is pinned in the next test, so this isolates the leak.
        var flat = new DriveParameters { WillCurve = 0.001 };
        var plain = Run(seed: 1, new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 }, maxSeconds: 16.0, flat);
        var full = Run(seed: 1, drive: flat);
        Assert.Equal(full.Flinches, plain.Flinches); // the nervous state does not depend on the arm while the hand is in the box

        // No leak, no shake: after settling, the hand stays within 2.3 cm of the hold point until the will breaks.
        for (var i = 3 * Hz; i < plain.Broke; i++) Assert.True(Dist(plain.Hand[i].Hand, Centre) <= 0.023f, $"drift {Dist(plain.Hand[i].Hand, Centre)} at tick {i}");

        // With the leak, at the last flinch the hand is over 2.3 cm from where the leak-free arm is at the same tick.
        var last = (int)full.Flinches[^1];
        Assert.True(Dist(full.Hand[last].Hand, plain.Hand[last].Hand) > 0.023f);
    }

    [Fact]
    public void A_failing_will_sags_the_hold_even_with_no_leak_and_no_shake_while_a_strong_will_holds_still()
    {
        var plain = Run(seed: 1, new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 }, maxSeconds: 16.0);
        // Early, resolve is high and the weight near 1: the hand stays within 2.3 cm of the hold point (seed 1 measured 0.1-1.7 cm over 3-6 s with the gravity feedforward; 0.2-2.0 cm to 8 s at e47e3d3).
        for (var i = 3 * Hz; i < 6 * Hz; i++) Assert.True(Dist(plain.Hand[i].Hand, Centre) <= 0.023f, $"drift {Dist(plain.Hand[i].Hand, Centre)} at tick {i}");
        // Late, the will is nearly gone: the voluntary hold has gone slack and the arm sags well out of the box before the break (measured 0.199 m at 14.3 s with the gravity feedforward, the will's weakness the only sag left; 0.155 m at e47e3d3).
        Assert.True(Dist(plain.Hand[(int)plain.Broke - 60].Hand, Centre) > 0.1f);
    }

    [Fact]
    public void The_hold_pose_puts_the_settled_palm_on_the_box_centre_at_full_strength()
    {
        // The pose is solved for this (0.34 cm at the fit): with a flat will and no leak the palm's mean from 3 to 4 s is within 0.5 cm of the hold point (0.24 cm measured).
        var t = Run(seed: 1, new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 }, maxSeconds: 5.0, new DriveParameters { WillCurve = 0.001 });
        var settled = t.Hand.Skip(3 * Hz).Take(Hz).ToList();
        var mean = (X: settled.Average(h => h.Hand.X), Y: settled.Average(h => h.Hand.Y), Z: settled.Average(h => h.Hand.Z));
        Assert.True(Dist(mean, Centre) <= 0.005f, $"settled palm {Dist(mean, Centre) * 100f:F2} cm from the hold point");
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
        // palm with the shipped StrainShake 0.08 / ShakeHz 3.5: 3.3-4.6 cm peak to peak in hand height per second; at 0, 0.2-1.4 cm (3.2-5.5 and 0.6-1.2 cm at e47e3d3).
        // (The shake was 0.03 on the forearm-tip hand, 3.9-4.7 cm there; the hands' mass damps the arm and the default rose to keep the rule.)
        var shaking = Run(seed: 1, new HoldParameters(), maxSeconds: 16.0);
        var still = Run(seed: 1, new HoldParameters { StrainShake = 0.0 }, maxSeconds: 16.0);
        for (var s = 8; s <= 13; s++)
        {
            Assert.True(VerticalPeakToPeak(shaking, s) > 0.03f, $"shipped shake only {VerticalPeakToPeak(shaking, s)} m peak to peak in second {s}");
            Assert.True(VerticalPeakToPeak(shaking, s) < 0.07f, $"shipped shake is {VerticalPeakToPeak(shaking, s)} m peak to peak in second {s}, more than a strain tremor");
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

    private static ulong HandTrackHash(Timeline t)
    {
        var h = 14695981039346656037ul;
        foreach (var (tick, hand, inBox) in t.Hand)
        {
            foreach (var f in new[] { hand.X, hand.Y, hand.Z })
            {
                h ^= (uint)BitConverter.SingleToInt32Bits(f);
                h *= 1099511628211ul;
            }
            h ^= inBox ? 1ul : 0ul;
            h *= 1099511628211ul;
        }
        return h;
    }

    [Fact]
    public void Seed_1_to_16_seconds_replays_the_world_the_break_and_the_deliberate_switch_recorded_at_the_will_mix()
    {
        // Recorded with the gravity feedforward and the re-solved hold (pose -0.70, -0.36, box half 0.145); the line before was recorded at e47e3d3, the ragdoll hands
        // (palm region, hand-mass damping, StrainShake 0.08, box half 0.125, pose -1.0, -0.6): broke=1772 withdrew=1784 flinches=16 hand=17416bc7c2d12853 world=9f710f8186aff900.
        // The break moved one tick, the flinch count did not; the hand and the world moved because the muscles now carry the arm's weight.
        var t = Run(seed: 1, maxSeconds: 16.0);
        Assert.Equal("broke=1773 withdrew=1785 flinches=16 hand=6db1f8d175e3e635 world=348765d9a09a27cf",
            $"broke={t.Broke} withdrew={t.Withdrew} flinches={t.Flinches.Count} hand={HandTrackHash(t):x16} world={t.Hash:x16}");
    }
}
