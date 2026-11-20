using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

public sealed class HandTests
{
    private static readonly Limb[] HandBodies = { Limb.PalmL, Limb.PalmR, Limb.ThumbL, Limb.ThumbR, Limb.FingersL, Limb.FingersR };

    /// <summary>FNV-1a 64 over the creation lanes of the 13 base bodies (world lanes 1..13) and the 12 base joints.</summary>
    private static ulong BaseCreationHash(Box3DSoaWorld world)
    {
        var v = world.View;
        var h = 14695981039346656037ul;
        void U(ulong x) { for (var k = 0; k < 8; k++) { h ^= (byte)(x >> (8 * k)); h *= 1099511628211ul; } }
        void F(float f) => U((uint)BitConverter.SingleToInt32Bits(f));
        for (var i = 1; i <= 13; i++)
        {
            U(v.StableId[i]); U(v.Kind[i]); U((ulong)v.Parent[i]); U((ulong)v.Limb[i]);
            F(v.Size0[i]); F(v.Size1[i]); F(v.Size2[i]);
            F(v.PositionX[i]); F(v.PositionY[i]); F(v.PositionZ[i]);
            F(v.RotationX[i]); F(v.RotationY[i]); F(v.RotationZ[i]); F(v.RotationW[i]);
        }
        for (var j = 0; j < 12; j++)
        {
            U(v.JointKind[j]); U((ulong)v.JointBodyA[j]); U((ulong)v.JointBodyB[j]);
            F(v.JointFrameX[j]); F(v.JointFrameY[j]); F(v.JointFrameZ[j]); F(v.JointFrameW[j]);
            F(v.JointOriginX[j]); F(v.JointOriginY[j]); F(v.JointOriginZ[j]);
        }
        return h;
    }

    /// <summary>Recorded at Lodur dedf391 (no hands); equal there and with the hands appended.</summary>
    private const ulong BaseHash = 0xccf5cedb0df1b2e8ul;

    [Fact]
    public void Hands_are_appended_after_the_base_bodies_and_joints_and_leave_them_as_they_were()
    {
        using var s = new RagdollScenario(seed: 1);
        var v = s.World.View;
        var r = s.Ragdoll;
        Assert.Equal(1 + 19, v.Count);
        Assert.Equal(18, v.JointCount);
        Assert.Equal(BaseHash, BaseCreationHash(s.World));
        Assert.Equal(new ulong[] { 113, 114, 115, 116, 117, 118 }, Enumerable.Range(14, 6).Select(i => v.StableId[i]).ToArray());
        Assert.All(HandBodies, l => Assert.InRange(r.BodyLane(l), 14, 19));
        foreach (var m in new[] { Muscle.WristL, Muscle.WristR, Muscle.ThumbL, Muscle.ThumbR, Muscle.FingersL, Muscle.FingersR }) Assert.InRange(r.JointLane(m), 12, 17);
        foreach (var side in new[] { 0, 1 })
        {
            var palm = r.BodyLane(Limb.PalmL + side);
            var thumb = r.BodyLane(Limb.ThumbL + side);
            var fingers = r.BodyLane(Limb.FingersL + side);
            Assert.Equal(r.BodyLane(Limb.LowerArmL + side), v.Parent[palm]);
            Assert.Equal(palm, v.Parent[thumb]);
            Assert.Equal(palm, v.Parent[fingers]);
            Assert.Equal(((uint)Box3DSoaKind.Box, (uint)Box3DSoaKind.Capsule, (uint)Box3DSoaKind.Box), (v.Kind[palm], v.Kind[thumb], v.Kind[fingers]));
            Assert.Equal((0.014f, 0.045f, 0.040f), (v.Size0[palm], v.Size1[palm], v.Size2[palm]));
            Assert.Equal((0.011f, 0.03f), (v.Size0[thumb], v.Size1[thumb]));
            Assert.Equal((0.011f, 0.0425f, 0.038f), (v.Size0[fingers], v.Size1[fingers], v.Size2[fingers]));
            Assert.Equal((r.BodyLane(Limb.LowerArmL + side), palm), (v.JointBodyA[r.JointLane(Muscle.WristL + side)], v.JointBodyB[r.JointLane(Muscle.WristL + side)]));
            Assert.Equal((palm, thumb), (v.JointBodyA[r.JointLane(Muscle.ThumbL + side)], v.JointBodyB[r.JointLane(Muscle.ThumbL + side)]));
            Assert.Equal((palm, fingers), (v.JointBodyA[r.JointLane(Muscle.FingersL + side)], v.JointBodyB[r.JointLane(Muscle.FingersL + side)]));
        }
    }

    [Fact]
    public void The_hand_region_is_the_palm_body_wherever_the_palm_goes()
    {
        using var s = new RagdollScenario(seed: 2);
        Array.Clear(s.Strength);
        s.Run(1.0f); // falling: the palms are nowhere near the creation pose
        var x = new float[HumanoidRagdoll.RegionCount]; var y = new float[HumanoidRagdoll.RegionCount]; var z = new float[HumanoidRagdoll.RegionCount];
        s.Ragdoll.ReadRegions(x, y, z);
        var v = s.World.View;
        foreach (var (region, limb) in new[] { (Region.HandL, Limb.PalmL), (Region.HandR, Limb.PalmR) })
        {
            var i = s.Ragdoll.BodyLane(limb);
            Assert.Equal((v.PositionX[i], v.PositionY[i], v.PositionZ[i]), (x[(int)region], y[(int)region], z[(int)region]));
        }
    }

    /// <summary>Runs <paramref name="runSeconds"/>, then one more second step by step; the fastest the limbs turn and move in that second.</summary>
    private static (float Angular, float Linear) LastSecond(RagdollScenario s, float runSeconds, IEnumerable<Limb> limbs)
    {
        s.Run(runSeconds);
        float ang = 0f, lin = 0f;
        var watched = limbs.ToArray();
        for (var step = 0; step < 120; step++)
        {
            s.Run(RagdollScenario.Dt);
            foreach (var l in watched)
            {
                var sp = s.Speeds(l);
                ang = MathF.Max(ang, sp.Angular);
                lin = MathF.Max(lin, sp.Linear);
            }
        }
        return (ang, lin);
    }

    [Theory]
    [InlineData(1ul)]
    [InlineData(2ul)]
    [InlineData(3ul)]
    public void Hands_rest_standing_palm_thumb_and_fingers_alike(ulong seed)
    {
        using var s = new RagdollScenario(seed);
        var (ang, _) = LastSecond(s, 5f, HandBodies);
        Assert.True(ang < 0.05f, $"seed {seed}: a hand body (thumbs included: a free thumb twist spins) still turns at {ang} rad/s in the last second of 6");
    }

    [Fact]
    public void Limp_hands_settle_instead_of_ringing()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            using var s = new RagdollScenario(seed);
            Array.Clear(s.Strength);
            var (ang, lin) = LastSecond(s, 7f, HandBodies);
            Assert.True(ang < 1f && lin < 0.1f, $"seed {seed}: a limp hand still moves at {lin} m/s, {ang} rad/s in the last second of 8");
        }
    }

    /// <summary>Distance between the two bodies' copies of a joint's anchor, each carried by its own body from creation.</summary>
    private sealed class JointSeparation
    {
        private readonly (int A, int B, (float X, float Y, float Z) LocalA, (float X, float Y, float Z) LocalB)[] _joints;

        public JointSeparation(Box3DSoaWorld world, IEnumerable<int> jointLanes)
        {
            var v = world.View;
            _joints = jointLanes.Select(j =>
            {
                int a = v.JointBodyA[j], b = v.JointBodyB[j];
                return (a, b, Local(v, a, v.JointOriginX[j], v.JointOriginY[j], v.JointOriginZ[j]), Local(v, b, v.JointOriginX[j], v.JointOriginY[j], v.JointOriginZ[j]));
            }).ToArray();
        }

        private static (float, float, float) Local(Box3DSoaView v, int i, float x, float y, float z)
        {
            new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]).Conjugate()
                .Rotate(x - v.PositionX[i], y - v.PositionY[i], z - v.PositionZ[i], out var lx, out var ly, out var lz);
            return (lx, ly, lz);
        }

        private static (float X, float Y, float Z) World(Box3DSoaView v, int i, (float X, float Y, float Z) p)
        {
            new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]).Rotate(p.X, p.Y, p.Z, out var x, out var y, out var z);
            return (x + v.PositionX[i], y + v.PositionY[i], z + v.PositionZ[i]);
        }

        /// <summary>The worst separation among the joints from index <paramref name="firstHand"/> on, and among those before it.</summary>
        public (float AtOrAfter, float Before) MaxBy(Box3DSoaView v, int firstHand)
        {
            float after = 0f, before = 0f;
            for (var k = 0; k < _joints.Length; k++)
            {
                var (a, b, la, lb) = _joints[k];
                var p = World(v, a, la); var q = World(v, b, lb);
                var d = MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y) + (p.Z - q.Z) * (p.Z - q.Z));
                if (k >= firstHand) after = MathF.Max(after, d); else before = MathF.Max(before, d);
            }
            return (after, before);
        }
    }

    /// <summary>
    /// The slam flail's worst joint-anchor separations over seeds 1..20 and strike/wind-up half-periods of 15, 30 and 60 ticks,
    /// 6 s each: [0] the hand joints (wrist, thumb, fingers), [1] the body joints. Runs are independent worlds, taken in parallel.
    /// </summary>
    private static (float Hand, float Body) FlailWorst(out int runs)
    {
        var cases = new List<(ulong Seed, int Half)>();
        foreach (var half in new[] { 15, 30, 60 }) for (ulong seed = 1; seed <= 20; seed++) cases.Add((seed, half));
        runs = cases.Count;
        var results = new (float Hand, float Body)[cases.Count];
        Parallel.For(0, cases.Count, k =>
        {
            var (seed, half) = cases[k];
            using var s = new RagdollScenario(seed);
            var all = Enumerable.Range(0, HumanoidRagdoll.MuscleCount).ToArray();
            var sep = new JointSeparation(s.World, all.Select(m => s.Ragdoll.JointLane((Muscle)m)));
            float hand = 0f, body = 0f;
            for (var i = 0; i < 6 * 120; i++)
            {
                var strike = (i / half) % 2 == 0;
                foreach (var (sh, el) in new[] { (Muscle.ShoulderL, Muscle.ElbowL), (Muscle.ShoulderR, Muscle.ElbowR) })
                {
                    s.Target[(int)sh] = Quat.FromAxisAngle(1f, 0f, 0f, strike ? -1.5f : -0.5f);
                    s.Target[(int)el] = Quat.FromAxisAngle(0f, 0f, 1f, strike ? 0f : -2.2f);
                }
                s.Target[(int)Muscle.FingersL] = s.Target[(int)Muscle.FingersR] = Quat.FromAxisAngle(0f, 0f, 1f, strike ? -1.5f : 0f);
                s.Target[(int)Muscle.WristL] = s.Target[(int)Muscle.WristR] = Quat.FromAxisAngle(1f, 0f, 0f, strike ? 0.8f : -0.8f);
                s.Run(RagdollScenario.Dt);
                var v = s.World.View;
                for (var b = 0; b < v.Count; b++)
                {
                    Assert.True(float.IsFinite(v.PositionX[b] + v.PositionY[b] + v.PositionZ[b] + v.RotationW[b] + v.VelocityX[b] + v.AngularVelocityX[b]), $"seed {seed} half {half}: body {b} is finite at step {i}");
                }
                var (h, bo) = sep.MaxBy(v, (int)Muscle.WristL);
                hand = MathF.Max(hand, h);
                body = MathF.Max(body, bo);
            }
            results[k] = (hand, body);
        });
        return (results.Max(r => r.Hand), results.Max(r => r.Body));
    }

    [Fact]
    public void Joints_hold_together_under_a_slam_flail_across_seeds_and_cadences_within_their_measured_bars()
    {
        // The bars are MEASURED, not targets (ruling flail-bar-honest-multiseed). Over these 60 runs the worst hand joint stretched
        // 57.4 mm (thumb) and the worst body joint 37.4 mm (elbow); the bars sit just above, so a worse hand or body fails.
        // A soft joint stretches by the slam's acceleration over its stiffness, whatever the gains; the real tightening is
        // follow-up ymir-joint-softness-and-filter (joint softness and collision filtering in Ymir's SoA world).
        var (hand, body) = FlailWorst(out _);
        Assert.True(hand <= 0.060f, $"a hand joint came apart by {hand * 1000f} mm (bar 60)");
        Assert.True(body <= 0.045f, $"a body joint came apart by {body * 1000f} mm (bar 45)");
    }

    /// <summary>Swing of a joint's rotation off its axis, radians (the twist about the joint z removed).</summary>
    private static float Swing(RagdollScenario s, Muscle m)
    {
        var v = s.World.View;
        var j = s.Ragdoll.JointLane(m);
        var along = MathF.Sqrt(v.JointRotationW[j] * v.JointRotationW[j] + v.JointRotationZ[j] * v.JointRotationZ[j]);
        return 2f * MathF.Acos(MathF.Min(1f, along));
    }

    /// <summary>How far the fingers are curled from straight (palm-frame), radians; curl is positive for both hands.</summary>
    private static float Curl(RagdollScenario s, int side)
    {
        var v = s.World.View;
        var palm = s.Ragdoll.BodyLane(Limb.PalmL + side);
        var fingers = s.Ragdoll.BodyLane(Limb.FingersL + side);
        var relative = new Quat(v.RotationX[palm], v.RotationY[palm], v.RotationZ[palm], v.RotationW[palm]).Conjugate()
            * new Quat(v.RotationX[fingers], v.RotationY[fingers], v.RotationZ[fingers], v.RotationW[fingers]);
        var sx = side == 0 ? 1f : -1f;
        var angleAboutZ = 2f * MathF.Atan2(relative.Z, relative.W);
        return -sx * angleAboutZ;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_fingers_rest_relaxed_at_creation_and_stay_there_while_nothing_grips(int side)
    {
        using var s = new RagdollScenario(seed: 1);
        Assert.Equal(0.35f, Curl(s, side), 0.01f); // ruling hand-rest-curl-relaxed
        s.Run(3f);
        Assert.Equal(0.35f, Curl(s, side), 0.05f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_knuckle_curls_to_1_6_rad_from_straight_and_extends_0_35_rad_past_it_and_no_further(int side)
    {
        float Reach(float target)
        {
            using var s = new RagdollScenario(seed: 1);
            s.Target[(int)Muscle.FingersL + side] = Quat.FromAxisAngle(0f, 0f, 1f, target);
            s.Run(2f);
            return Curl(s, side);
        }

        Assert.InRange(Reach(-3f), 1.5f, 1.72f);   // the limit is 1.6 from straight (a soft stop gives a little), whatever the creation curl
        Assert.InRange(Reach(3f), -0.45f, -0.30f); // 0.35 past straight, a soft stop gives a little
    }

    [Theory]
    [InlineData(Muscle.WristL, 1.0f)]
    [InlineData(Muscle.WristR, 1.0f)]
    [InlineData(Muscle.ThumbL, 0.7f)]
    [InlineData(Muscle.ThumbR, 0.7f)]
    public void A_hand_joint_swings_to_its_cone_and_no_further_whichever_way_it_is_told(Muscle joint, float cone)
    {
        var reached = 0f;
        foreach (var (ax, ay) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
        {
            using var s = new RagdollScenario(seed: 1);
            s.Target[(int)joint] = Quat.FromAxisAngle(ax, ay, 0f, 2.5f);
            s.Run(2f);
            var swing = Swing(s, joint);
            Assert.True(swing < cone + 0.06f, $"{joint} swung {swing} past its cone {cone} toward ({ax}, {ay})");
            reached = MathF.Max(reached, swing);
        }
        Assert.True(reached > cone - 0.15f, $"{joint} only swung {reached} of its cone {cone} in any direction");
    }
}
