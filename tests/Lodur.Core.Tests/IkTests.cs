using Lodur;

namespace Lodur.Core.Tests;

public sealed class IkTests
{
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();

    private static (float X, float Y, float Z) Add((float X, float Y, float Z) a, (float X, float Y, float Z) b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static (float X, float Y, float Z) Sub((float X, float Y, float Z) a, (float X, float Y, float Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Len((float X, float Y, float Z) a) => MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

    private static (float X, float Y, float Z) Rot(Quat q, (float X, float Y, float Z) v)
    {
        q.Rotate(v.X, v.Y, v.Z, out var x, out var y, out var z);
        return (x, y, z);
    }

    /// <summary>The convention applied by hand: where the wrist anchor lands for these two targets, in world coordinates.</summary>
    private static (float X, float Y, float Z) Wrist(ArmChain arm, Quat parentRotation, (float X, float Y, float Z) parentPosition, Quat shoulder, Quat elbow)
    {
        var fs = Ik.JointFrame(Plan.Joints[arm.ShoulderJoint]);
        var fe = Ik.JointFrame(Plan.Joints[arm.ElbowJoint]);
        var rs = fs * shoulder * fs.Conjugate();
        var re = fe * elbow * fe.Conjugate();
        var a = Sub(arm.ElbowAnchor, arm.ShoulderAnchor);
        var b = Sub(arm.WristAnchor, arm.ElbowAnchor);
        var local = Add(arm.ShoulderAnchor, Rot(rs, Add(a, Rot(re, b))));
        var centre = Plan.Parts[arm.ParentPart].Position;
        return Add(parentPosition, Rot(parentRotation, Sub(local, centre)));
    }

    private static Quat RandomRotation(Random rng, float maxAngle)
    {
        var x = (float)rng.NextDouble() * 2f - 1f;
        var y = (float)rng.NextDouble() * 2f - 1f;
        var z = (float)rng.NextDouble() * 2f - 1f;
        var l = MathF.Sqrt(x * x + y * y + z * z);
        return l < 1e-3f ? Quat.Identity : Quat.FromAxisAngle(x / l, y / l, z / l, (float)rng.NextDouble() * maxAngle);
    }

    /// <summary>Seeded targets around one arm: a random direction and a distance the flex range can reach, under a random parent pose.</summary>
    private static IEnumerable<(Quat Rotation, (float X, float Y, float Z) Position, (float X, float Y, float Z) Target)> Grid(ArmChain arm, int seed, int count)
    {
        var rng = new Random(seed);
        var centre = Plan.Parts[arm.ParentPart].Position;
        for (var i = 0; i < count; i++)
        {
            var rot = i % 2 == 0 ? Quat.Identity : RandomRotation(rng, 0.8f);
            var pos = i % 2 == 0 ? centre : (centre.X + (float)rng.NextDouble() - 0.5f, centre.Y + (float)rng.NextDouble() - 0.5f, centre.Z + (float)rng.NextDouble() - 0.5f);
            double dx, dy, dz, l;
            do
            {
                dx = rng.NextDouble() * 2 - 1; dy = rng.NextDouble() * 2 - 1; dz = rng.NextDouble() * 2 - 1;
                l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            while (l is < 0.1 or > 1.0);
            var r = (float)(0.23 + rng.NextDouble() * 0.32);
            var dir = ((float)(dx / l), (float)(dy / l), (float)(dz / l));
            // The shoulder in the world under this parent pose, then the target out along dir.
            var shoulder = Add(pos, Rot(rot, Sub(arm.ShoulderAnchor, centre)));
            yield return (rot, pos, (shoulder.X + dir.Item1 * r, shoulder.Y + dir.Item2 * r, shoulder.Z + dir.Item3 * r));
        }
    }

    private static float ZTwist(Quat q) => 2f * MathF.Atan2(MathF.Abs(q.Z), MathF.Abs(q.W));

    [Theory]
    [InlineData(Side.Left)]
    [InlineData(Side.Right)]
    public void Arm_solve_lands_the_wrist_by_forward_kinematics(Side side)
    {
        var arm = ArmChain.Of(Plan, side);
        var reachedCount = 0;
        foreach (var (rot, pos, target) in Grid(arm, 7 + (int)side, 600))
        {
            if (!arm.Solve(rot, pos.X, pos.Y, pos.Z, target.X, target.Y, target.Z, out var sq, out var eq)) continue;
            reachedCount++;
            var wrist = Wrist(arm, rot, pos, sq, eq);
            Assert.True(Len(Sub(wrist, target)) < 1e-4f, $"wrist {wrist} missed target {target} by {Len(Sub(wrist, target))} m");
        }

        Assert.True(reachedCount > 100, $"only {reachedCount} grid targets were reachable; the grid no longer exercises the solve");
    }

    [Theory]
    [InlineData(Side.Left)]
    [InlineData(Side.Right)]
    public void Arm_solve_never_twists_the_shoulder(Side side)
    {
        var arm = ArmChain.Of(Plan, side);
        var n = 0;
        foreach (var (rot, pos, target) in Grid(arm, 21 + (int)side, 600))
        {
            arm.Solve(rot, pos.X, pos.Y, pos.Z, target.X, target.Y, target.Z, out var sq, out _);
            Assert.True(ZTwist(sq) < 1e-5f, $"shoulder target twists by {ZTwist(sq)} rad for {target}");
            n++;
        }

        Assert.Equal(600, n);
    }

    [Fact]
    public void Out_of_reach_points_the_straight_arm_at_the_target_and_reports_not_reached()
    {
        var arm = ArmChain.Of(Plan, Side.Right);
        var s = arm.ShoulderAnchor;
        var centre = Plan.Parts[arm.ParentPart].Position;
        var dirs = new[] { (0f, -0.5f, 0.87f), (-0.4f, -0.6f, 0.7f) };
        foreach (var raw in dirs)
        {
            var l = Len(raw);
            var dir = (raw.Item1 / l, raw.Item2 / l, raw.Item3 / l);
            var target = (s.X + dir.Item1 * 2f, s.Y + dir.Item2 * 2f, s.Z + dir.Item3 * 2f);
            var reached = arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, target.Item1, target.Item2, target.Item3, out var sq, out var eq);
            Assert.False(reached);
            var wrist = Wrist(arm, Quat.Identity, centre, sq, eq);
            var reach = arm.L1 + arm.L2 - 1e-3f;
            var want = (s.X + dir.Item1 * reach, s.Y + dir.Item2 * reach, s.Z + dir.Item3 * reach);
            Assert.True(Len(Sub(wrist, want)) < 1e-4f, $"wrist {wrist} is not on the line to the target at full stretch {want}");
            Assert.True(MathF.Abs(2f * MathF.Atan2(eq.Z, eq.W)) < 0.15f, "the elbow is not nearly straight");
        }

        // The same direction well inside reach is reached.
        Assert.True(arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, s.X, s.Y, s.Z + 0.4f, out _, out _));
    }

    [Fact]
    public void Limits_clamp_swing_flex_and_look()
    {
        var arm = ArmChain.Of(Plan, Side.Right);
        var s = arm.ShoulderAnchor;
        var c = Plan.Parts[arm.ParentPart].Position;
        var cone = Plan.Joints[arm.ShoulderJoint].Cone;
        var (lo, hi) = (Plan.Joints[arm.ElbowJoint].Lower, Plan.Joints[arm.ElbowJoint].Upper);

        // Straight up is half a turn from the hanging arm: past the cone.
        Assert.False(arm.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X, s.Y + 0.45f, s.Z, out var up, out _));
        Assert.Equal(cone, 2f * MathF.Atan2(MathF.Sqrt(up.X * up.X + up.Y * up.Y + up.Z * up.Z), up.W), 1e-4f);
        Assert.True(ZTwist(up) < 1e-5f);

        // Near the shoulder the hinge would have to fold past its range.
        Assert.False(arm.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X, s.Y - 0.1f, s.Z + 0.1f, out _, out var folded));
        Assert.Equal(lo, 2f * MathF.Atan2(folded.Z, folded.W), 1e-4f);

        // A bend inside the range is not a clamp.
        Assert.True(arm.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X, s.Y - 0.25f, s.Z + 0.25f, out _, out var bent));
        var flex = 2f * MathF.Atan2(bent.Z, bent.W);
        Assert.InRange(flex, lo + 0.05f, hi - 0.05f);

        var look = LookChain.Of(Plan);
        var neck = Plan.Joints[look.Joint];
        var head = Plan.Parts[look.ChildPart].Position;
        var parent = Plan.Parts[look.ParentPart].Position;
        (float Yaw, float Pitch) Aim(float dx, float dy, float dz, out bool reached)
        {
            reached = look.Solve(Quat.Identity, parent.X, parent.Y, parent.Z, head.X + dx, head.Y + dy, head.Z + dz, out var q);
            var f = Ik.JointFrame(neck);
            var d = Rot(f * q * f.Conjugate(), (0f, 0f, 1f));
            return (MathF.Atan2(d.X, d.Z), -MathF.Asin(d.Y));
        }

        var (yaw, _) = Aim(2f, 0f, 0.5f, out var r1);
        Assert.False(r1);
        Assert.Equal(neck.Upper, yaw, 1e-4f);
        (yaw, _) = Aim(-2f, 0f, 0.5f, out r1);
        Assert.False(r1);
        Assert.Equal(neck.Lower, yaw, 1e-4f);
        var (_, pitch) = Aim(0f, 2f, 0.5f, out r1);
        Assert.False(r1);
        Assert.Equal(-neck.Cone, pitch, 1e-4f);
        (_, pitch) = Aim(0f, -2f, 0.5f, out r1);
        Assert.False(r1);
        Assert.Equal(neck.Cone, pitch, 1e-4f);
        Aim(0.3f, 0.2f, 1f, out r1);
        Assert.True(r1);
    }

    [Fact]
    public void Left_and_right_mirror()
    {
        var left = ArmChain.Of(Plan, Side.Left);
        var right = ArmChain.Of(Plan, Side.Right);
        var c = Plan.Parts[right.ParentPart].Position;
        var n = 0;
        foreach (var (_, _, target) in Grid(right, 5, 300))
        {
            var rReached = right.Solve(Quat.Identity, c.X, c.Y, c.Z, target.X, target.Y, target.Z, out var rs, out var re);
            var lReached = left.Solve(Quat.Identity, c.X, c.Y, c.Z, -target.X, target.Y, target.Z, out var ls, out var le);
            Assert.Equal(rReached, lReached);
            // A reflection across x leaves a rotation about x alone and turns the other two components over.
            Assert.Equal(rs.X, ls.X, 1e-5f);
            Assert.Equal(-rs.Y, ls.Y, 1e-5f);
            Assert.Equal(-rs.Z, ls.Z, 1e-5f);
            Assert.Equal(rs.W, ls.W, 1e-5f);
            Assert.Equal(re.Z, le.Z, 1e-5f);
            Assert.Equal(re.W, le.W, 1e-5f);
            n++;
        }

        Assert.Equal(300, n);
    }

    [Fact]
    public void Look_aims_the_head_forward_at_the_target()
    {
        var look = LookChain.Of(Plan);
        var neck = Plan.Joints[look.Joint];
        var f = Ik.JointFrame(neck);
        var head = Plan.Parts[look.ChildPart].Position;
        var parentCentre = Plan.Parts[look.ParentPart].Position;
        var rng = new Random(11);
        var aimed = 0;
        for (var i = 0; i < 400; i++)
        {
            var rot = i % 2 == 0 ? Quat.Identity : RandomRotation(rng, 0.8f);
            (float X, float Y, float Z) pos = i % 2 == 0 ? parentCentre : (parentCentre.X + 0.3f, parentCentre.Y - 0.2f, parentCentre.Z + 0.1f);
            // A direction inside the yaw and pitch limits, at 0.5 to 3 m, from the head's creation-pose centre.
            var yaw = (float)(rng.NextDouble() * 2 - 1) * 0.55f;
            var pitch = (float)(rng.NextDouble() * 2 - 1) * 0.55f;
            var d = (MathF.Sin(yaw) * MathF.Cos(pitch), -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
            var r = 0.5f + (float)rng.NextDouble() * 2.5f;
            var local = Add(head, (d.Item1 * r, d.Item2 * r, d.Item3 * r));
            var world = Add(pos, Rot(rot, Sub(local, parentCentre)));
            Assert.True(look.Solve(rot, pos.X, pos.Y, pos.Z, world.X, world.Y, world.Z, out var q));
            var got = Rot(f * q * f.Conjugate(), (0f, 0f, 1f));
            var cross = (got.Y * d.Item3 - got.Z * d.Item2, got.Z * d.Item1 - got.X * d.Item3, got.X * d.Item2 - got.Y * d.Item1);
            Assert.True(Len(cross) < 1e-4f && got.X * d.Item1 + got.Y * d.Item2 + got.Z * d.Item3 > 0f, $"head forward {got} is off the direction {d}");
            aimed++;
        }

        Assert.Equal(400, aimed);
    }

    [Fact]
    public void Solving_allocates_nothing()
    {
        var arm = ArmChain.Of(Plan, Side.Right);
        var look = LookChain.Of(Plan);
        var s = arm.ShoulderAnchor;
        var rot = Quat.FromAxisAngle(0f, 1f, 0f, 0.2f);
        void Work(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var t = 0.3f + i % 7 * 0.03f;
                arm.Solve(rot, 0f, 1.4f, 0f, s.X, s.Y - t, s.Z + t, out _, out _);
                look.Solve(rot, 0f, 1.4f, 0f, t, 1.7f, 2f, out _);
            }
        }

        Work(2000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Work(2000);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_plan_without_the_arm_is_refused()
    {
        var parts = new[] { new PartDef("A", Side.Center, PartShape.Sphere, (0.1f, 0f, 0f), (0f, 0f, 0f), Quat.Identity, 1f, 1f, 0f) };
        var bare = new BodyPlan(parts, Array.Empty<JointDef>(), Array.Empty<SensePointDef>());
        Assert.Throws<ArgumentException>(() => ArmChain.Of(bare, Side.Left));
        Assert.Throws<ArgumentException>(() => LookChain.Of(bare));
    }
}
