using Lodur;
using Xunit.Abstractions;

namespace Lodur.Core.Tests;

public sealed class IkTests
{
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();

    // The tick the chains are built for; it sets the swivel step, SwivelRate * Dt.
    private const double Dt = 0.01;
    private readonly ITestOutputHelper _output;

    public IkTests(ITestOutputHelper output) => _output = output;

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
        var arm = ArmChain.Of(Plan, side, Dt);
        var reachedCount = 0;
        foreach (var (rot, pos, target) in Grid(arm, 7 + (int)side, 600))
        {
            // Independent goals, not a path: each is the stateless rule.
            arm.Reset();
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
    public void Arm_solve_twists_the_shoulder_only_where_zero_twist_would_leave_the_cone(Side side)
    {
        var arm = ArmChain.Of(Plan, side, Dt);
        var joint = Plan.Joints[arm.ShoulderJoint];
        var (n, reached, twisted) = (0, 0, 0);
        foreach (var (rot, pos, target) in Grid(arm, 21 + (int)side, 600))
        {
            arm.Reset();
            var ok = arm.Solve(rot, pos.X, pos.Y, pos.Z, target.X, target.Y, target.Z, out var sq, out _);
            var swing = 2f * MathF.Asin(MathF.Min(1f, MathF.Sqrt(sq.X * sq.X + sq.Y * sq.Y)));
            var twist = 2f * MathF.Atan2(sq.Z, sq.W);
            Assert.True(swing <= joint.Cone + 1e-4f, $"swing {swing} past the cone {joint.Cone} for {target}");
            Assert.True(twist >= joint.Lower - 1e-4f && twist <= joint.Upper + 1e-4f, $"twist {twist} outside [{joint.Lower}, {joint.Upper}] for {target}");
            if (MathF.Abs(twist) > 1e-5f)
            {
                // A twist is taken only to keep the swing inside the cone, so the swivel sits on the boundary of the feasible range; an
                // unreached goal's least-violating swivel, clamped, sits on the cone.
                if (ok) twisted++;
                var onLimit = MathF.Abs(swing - joint.Cone) < 1e-3f || MathF.Abs(twist - joint.Lower) < 1e-3f || MathF.Abs(twist - joint.Upper) < 1e-3f;
                Assert.True(onLimit, $"shoulder target twists by {twist} rad at swing {swing} for {target}, off every limit");
            }

            n++;
            if (ok) reached++;
        }

        Assert.Equal(600, n);
        Assert.InRange(twisted, 1, reached / 2);
    }

    [Fact]
    public void A_wrist_goal_the_zero_twist_swivel_cannot_reach_is_reached_by_twisting_inside_the_limits()
    {
        // The left ward-off wrist goal for a sound at head height, ahead and to the left: zero twist saturates the cone 20 cm short.
        var arm = ArmChain.Of(Plan, Side.Left, Dt);
        var joint = Plan.Joints[arm.ShoulderJoint];
        var centre = Plan.Parts[arm.ParentPart].Position;
        var goal = (0.2988f, 1.74f, 0.2605f);
        Assert.True(arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, goal.Item1, goal.Item2, goal.Item3, out var sq, out var eq));
        var wrist = Wrist(arm, Quat.Identity, centre, sq, eq);
        Assert.True(Len(Sub(wrist, goal)) < 1e-3f, $"wrist {wrist} missed {goal} by {Len(Sub(wrist, goal))} m");
        var twist = 2f * MathF.Atan2(sq.Z, sq.W);
        Assert.True(MathF.Abs(twist) > 0.05f && twist >= joint.Lower && twist <= joint.Upper, $"twist {twist}");
    }

    [Theory]
    [InlineData(Side.Left)]
    [InlineData(Side.Right)]
    public void A_goal_crossing_out_of_the_feasible_set_moves_the_targets_continuously(Side side)
    {
        // Two stretches of a circle about the shoulder where a goal leaves the feasible set: past the edge no swivel keeps both swing and
        // twist inside the limits. Each stretch is 2000 steps of about 0.02 mm.
        var arm = ArmChain.Of(Plan, side, Dt);
        var centre = Plan.Parts[arm.ParentPart].Position;
        var s = arm.ShoulderAnchor;
        var (worstTurn, worstMove) = (0f, 0f);
        foreach (var (from, to) in new[] { (0.25f, 0.33f), (2.65f, 2.74f) })
        {
            const int steps = 2000;
            var crossings = 0;
            var (lastReached, lastShoulder, lastWrist) = (false, Quat.Identity, (0f, 0f, 0f));
            // Each stretch is its own path.
            arm.Reset();
            for (var i = 0; i <= steps; i++)
            {
                var th = from + (to - from) * i / steps;
                var goal = (s.X + 0.45f * MathF.Cos(th), s.Y + 0.45f * MathF.Sin(th), s.Z + 0.25f + 0.1f * MathF.Sin(2f * th));
                var reached = arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, goal.Item1, goal.Item2, goal.Item3, out var sq, out var eq);
                var wrist = Wrist(arm, Quat.Identity, centre, sq, eq);
                if (i > 0)
                {
                    if (reached != lastReached) crossings++;
                    var dot = MathF.Abs(sq.X * lastShoulder.X + sq.Y * lastShoulder.Y + sq.Z * lastShoulder.Z + sq.W * lastShoulder.W);
                    worstTurn = MathF.Max(worstTurn, 2f * MathF.Acos(MathF.Min(1f, dot)));
                    worstMove = MathF.Max(worstMove, Len(Sub(wrist, lastWrist)));
                }

                (lastReached, lastShoulder, lastWrist) = (reached, sq, wrist);
            }

            // Tripwire: the stretch still crosses the edge, so the bounds below still test a crossing.
            Assert.Equal(1, crossings);
        }

        _output.WriteLine($"{side}: largest step of the shoulder target {worstTurn:F4} rad, of the wrist {worstMove * 1000f:F4} mm");
        // deterministic-bound-margin: each bound is the geometric mean of the shipped worst and the least mutant. Shipped worst, both
        // arms: 0.0117 rad and 0.0216 mm. Mutant, the zero-twist swing clamped to the cone past the edge (the fallback before this test):
        // 1.3752 rad and 269.72 mm. Bounds sqrt(0.0117 * 1.3752) = 0.127 rad and sqrt(0.0216 * 269.72) = 2.41 mm.
        Assert.True(worstTurn < 0.127f, $"the shoulder target jumped {worstTurn} rad in one step");
        Assert.True(worstMove < 0.00241f, $"the wrist jumped {worstMove} m in one step");
    }

    [Fact]
    public void Out_of_reach_points_the_straight_arm_at_the_target_and_reports_not_reached()
    {
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var s = arm.ShoulderAnchor;
        var centre = Plan.Parts[arm.ParentPart].Position;
        var dirs = new[] { (0f, -0.5f, 0.87f), (-0.4f, -0.6f, 0.7f) };
        foreach (var raw in dirs)
        {
            var l = Len(raw);
            var dir = (raw.Item1 / l, raw.Item2 / l, raw.Item3 / l);
            var target = (s.X + dir.Item1 * 2f, s.Y + dir.Item2 * 2f, s.Z + dir.Item3 * 2f);
            arm.Reset();
            var reached = arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, target.Item1, target.Item2, target.Item3, out var sq, out var eq);
            Assert.False(reached);
            var wrist = Wrist(arm, Quat.Identity, centre, sq, eq);
            var reach = arm.L1 + arm.L2 - 1e-3f;
            var want = (s.X + dir.Item1 * reach, s.Y + dir.Item2 * reach, s.Z + dir.Item3 * reach);
            Assert.True(Len(Sub(wrist, want)) < 1e-4f, $"wrist {wrist} is not on the line to the target at full stretch {want}");
            Assert.True(MathF.Abs(2f * MathF.Atan2(eq.Z, eq.W)) < 0.15f, "the elbow is not nearly straight");
        }

        // The same direction well inside reach is reached.
        arm.Reset();
        Assert.True(arm.Solve(Quat.Identity, centre.X, centre.Y, centre.Z, s.X, s.Y, s.Z + 0.4f, out _, out _));
    }

    [Fact]
    public void Limits_clamp_swing_flex_and_look()
    {
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var s = arm.ShoulderAnchor;
        var c = Plan.Parts[arm.ParentPart].Position;
        var cone = Plan.Joints[arm.ShoulderJoint].Cone;
        var (lo, hi) = (Plan.Joints[arm.ElbowJoint].Lower, Plan.Joints[arm.ElbowJoint].Upper);

        // Straight up is half a turn from the hanging arm: past the cone.
        Assert.False(arm.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X, s.Y + 0.45f, s.Z, out var up, out _));
        Assert.Equal(cone, 2f * MathF.Atan2(MathF.Sqrt(up.X * up.X + up.Y * up.Y + up.Z * up.Z), up.W), 1e-4f);
        Assert.True(ZTwist(up) < 1e-5f);

        // Near the shoulder the hinge would have to fold past its range.
        arm.Reset();
        Assert.False(arm.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X, s.Y - 0.1f, s.Z + 0.1f, out _, out var folded));
        Assert.Equal(lo, 2f * MathF.Atan2(folded.Z, folded.W), 1e-4f);

        // A bend inside the range is not a clamp.
        arm.Reset();
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
        var left = ArmChain.Of(Plan, Side.Left, Dt);
        var right = ArmChain.Of(Plan, Side.Right, Dt);
        var c = Plan.Parts[right.ParentPart].Position;
        var n = 0;
        foreach (var (_, _, target) in Grid(right, 5, 300))
        {
            right.Reset();
            left.Reset();
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

    private static float Turn(Quat a, Quat b) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W)));

    private static float Swing(Quat q) => 2f * MathF.Atan2(MathF.Sqrt(q.X * q.X + q.Y * q.Y), MathF.Sqrt(q.Z * q.Z + q.W * q.W));

    // Tie lines at the shoulder's sagittal plane (goal relative to the shoulder (dx, y, z)), where two mirrored swivels tie under the
    // stateless rule (Soul s3 on cut-lodur-orient-goals). The first four are unreached throughout; (-0.224, -0.2) is reached beyond 4 mm
    // either side; (-0.25, -0.25) is reached throughout, two feasible intervals tying.
    private static readonly (float Y, float Z)[] TieLines = { (0.12f, -0.4f), (0.2f, -0.2f), (0.25f, 0f), (0.3f, -0.1f), (-0.224f, -0.2f), (-0.25f, -0.25f) };

    /// <summary>
    /// Walks one chain along goals, one solve per tick, from a fresh state: the largest per-tick turn of the shoulder target and the largest
    /// swing of the held (unclamped) target. With <paramref name="stateless"/> the chain is reset before every solve: the stateless argmin,
    /// the claimed mutant.
    /// </summary>
    private static (float Turn, float HeldSwing) Walk(ArmChain arm, IEnumerable<(float X, float Y, float Z)> goals, bool stateless, Quat[]? record = null)
    {
        var c = Plan.Parts[arm.ParentPart].Position;
        var (worst, swing, last, i) = (0f, 0f, Quat.Identity, 0);
        arm.Reset();
        foreach (var g in goals)
        {
            if (stateless) arm.Reset();
            arm.Solve(Quat.Identity, c.X, c.Y, c.Z, g.X, g.Y, g.Z, out var sq, out _);
            if (i > 0) worst = MathF.Max(worst, Turn(sq, last));
            swing = MathF.Max(swing, Swing(arm.Held));
            if (record != null) record[i] = sq;
            last = sq;
            i++;
        }

        return (worst, swing);
    }

    private static IEnumerable<(float X, float Y, float Z)> Line((float X, float Y, float Z) s, float y, float z, float sign, int from, int to, float step)
    {
        for (var i = from; i <= to; i++) yield return (s.X + sign * i * step, s.Y + y, s.Z + z);
    }

    // deterministic-bound-margin for the held swivel's per-tick turn at Dt = 0.01 s (a 0.03 rad swivel step): the geometric mean of the
    // shipped worst and the least claimed mutant, the stateless argmin (the chain reset every tick). Shipped worst over
    // the tie lines, the random jumps and the basin switch: 0.0333 rad. Least mutant: 0.5039 rad (a random jump; the tie lines least is 0.9995).
    // Bound sqrt(0.0333 * 0.5039) = 0.129.
    private const float HeldTurnBound = 0.129f;

    [Fact]
    public void Across_a_tie_line_the_arm_keeps_its_swivel_and_the_arms_mirror()
    {
        var left = ArmChain.Of(Plan, Side.Left, Dt);
        var right = ArmChain.Of(Plan, Side.Right, Dt);
        var (worst, mutant) = (0f, float.MaxValue);
        var (lq, rq) = (new Quat[101], new Quat[101]);
        foreach (var (y, z) in TieLines)
        {
            // dx from -10 to +10 mm in 0.2 mm ticks; the left arm walks the mirrored path.
            var held = Walk(right, Line(right.ShoulderAnchor, y, z, 1f, -50, 50, 0.0002f), false, rq).Turn;
            Walk(left, Line(left.ShoulderAnchor, y, z, -1f, -50, 50, 0.0002f), false, lq);
            var jump = Walk(right, Line(right.ShoulderAnchor, y, z, 1f, -50, 50, 0.0002f), true).Turn;
            _output.WriteLine($"tie line ({y}, {z}): held worst {held:F4} rad per tick, stateless {jump:F4}");
            (worst, mutant) = (MathF.Max(worst, held), MathF.Min(mutant, jump));
            for (var i = 0; i < 101; i++)
            {
                Assert.Equal(rq[i].X, lq[i].X, 1e-5f);
                Assert.Equal(-rq[i].Y, lq[i].Y, 1e-5f);
                Assert.Equal(-rq[i].Z, lq[i].Z, 1e-5f);
                Assert.Equal(rq[i].W, lq[i].W, 1e-5f);
            }
        }

        _output.WriteLine($"worst held {worst:F4}, least stateless worst {mutant:F4}");
        // Tripwire: every line still ties under the stateless rule, so the bound still tests a tie.
        Assert.True(mutant > 0.9f, $"a tie line no longer ties: the stateless rule turns only {mutant} rad");
        Assert.True(worst < HeldTurnBound, $"the shoulder target turned {worst} rad in one tick across a tie");
    }

    [Theory]
    [InlineData(Side.Left)]
    [InlineData(Side.Right)]
    public void The_stateless_jumps_of_random_segments_are_held(Side side)
    {
        // Soul's probe: random segments about the shoulder walked in 0.4 mm steps; each step the stateless rule turns over 0.5 rad is
        // bisected to a gap under 2 microns, where it persists. The held chain walks 2 mm up to the near side, across the gap in one
        // tick, and 2 mm on.
        // Excluded and counted: jumps where the held target swings past HalfTurnSwing. Near a half-turn swing the swing-twist split is
        // singular, so the clamp to the limits turns the target by up to 3 rad for a swivel step of 0.03 rad: the held swivel is
        // continuous there and the clamped target is not. That is the clamp's defect, outside the temporal-coherence ruling.
        const float HalfTurnSwing = 3.0f;
        var arm = ArmChain.Of(Plan, side, Dt);
        var s = arm.ShoulderAnchor;
        var c = Plan.Parts[arm.ParentPart].Position;
        var rng = new Random(side == Side.Left ? 5 : 6);
        (float X, float Y, float Z) Pt()
        {
            double dx, dy, dz, l;
            do { dx = rng.NextDouble() * 2 - 1; dy = rng.NextDouble() * 2 - 1; dz = rng.NextDouble() * 2 - 1; l = Math.Sqrt(dx * dx + dy * dy + dz * dz); }
            while (l is < 0.05 or > 1.0);
            var r = (float)(0.05 + rng.NextDouble() * 0.7);
            return (s.X + (float)(dx / l) * r, s.Y + (float)(dy / l) * r, s.Z + (float)(dz / l) * r);
        }

        Quat Fresh((float X, float Y, float Z) g)
        {
            arm.Reset();
            arm.Solve(Quat.Identity, c.X, c.Y, c.Z, g.X, g.Y, g.Z, out var q, out _);
            return q;
        }

        static (float X, float Y, float Z) Lerp((float X, float Y, float Z) a, (float X, float Y, float Z) b, float u) => (a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u);
        var (found, held, singular, worst, mutant) = (0, 0, 0, 0f, float.MaxValue);
        var path = new (float X, float Y, float Z)[12];
        for (var k = 0; k < 400 && found < 40; k++)
        {
            var a = Pt();
            var b = Pt();
            var steps = (int)(Len(Sub(b, a)) / 0.0004f) + 1;
            var prev = Fresh(a);
            for (var i = 1; i <= steps; i++)
            {
                var q1 = Fresh(Lerp(a, b, (float)i / steps));
                if (Turn(q1, prev) > 0.5f)
                {
                    found++;
                    var (lo, hi, qlo, qhi) = (Lerp(a, b, (float)(i - 1) / steps), Lerp(a, b, (float)i / steps), prev, q1);
                    for (var it = 0; it < 40 && Len(Sub(hi, lo)) > 2e-6f; it++)
                    {
                        var mid = Lerp(lo, hi, 0.5f);
                        var qm = Fresh(mid);
                        if (Turn(qm, qlo) >= Turn(qm, qhi)) (hi, qhi) = (mid, qm);
                        else (lo, qlo) = (mid, qm);
                    }

                    Assert.True(Turn(qlo, qhi) > 0.5f, $"a stateless jump vanished on bisection: {Turn(qlo, qhi)} rad");
                    var u = Sub(b, a);
                    var step = 0.0004f / Len(u);
                    for (var j = 0; j < 6; j++) path[j] = Lerp(lo, Add(lo, u), (j - 5) * step);
                    for (var j = 0; j < 6; j++) path[6 + j] = Lerp(hi, Add(hi, u), j * step);
                    var walk = Walk(arm, path, false);
                    if (walk.HeldSwing > HalfTurnSwing)
                    {
                        singular++;
                    }
                    else
                    {
                        held++;
                        worst = MathF.Max(worst, walk.Turn);
                        mutant = MathF.Min(mutant, Walk(arm, path, true).Turn);
                    }
                }

                prev = q1;
            }
        }

        _output.WriteLine($"{side}: {found} stateless jumps, {held} held (worst {worst:F4} rad per tick, least stateless worst {mutant:F4}), {singular} at the half-turn swing");
        Assert.True(found >= 40, $"only {found} stateless jumps found; the probe no longer finds the ties");
        Assert.True(held >= 25, $"only {held} jumps away from the half-turn swing; the bound no longer tests many");
        Assert.True(worst < HeldTurnBound, $"the shoulder target turned {worst} rad in one tick at a stateless jump");
    }

    [Fact]
    public void A_clearly_better_basin_is_taken_at_the_swivel_rate_and_reset_restores_the_stateless_rule()
    {
        // The feasible tie: goal (dx, -0.25, -0.25) from the shoulder is reached throughout, by two mirrored feasible swivels that tie at
        // dx 0. From dx -10 mm in 0.2 mm ticks the arm holds its basin past the tie; once the stateless swivel's twist is smaller by the
        // margin it turns there, no faster than the swivel step.
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var fresh = ArmChain.Of(Plan, Side.Right, Dt);
        var s = arm.ShoulderAnchor;
        var c = Plan.Parts[arm.ParentPart].Position;
        Quat Solve(ArmChain chain, float dx, out bool reached)
        {
            reached = chain.Solve(Quat.Identity, c.X, c.Y, c.Z, s.X + dx, s.Y - 0.25f, s.Z - 0.25f, out var q, out _);
            return q;
        }

        var (worst, last, heldAt, switchedAt) = (0f, Quat.Identity, float.NaN, float.NaN);
        for (var i = -50; i <= 1000; i++)
        {
            var dx = i * 0.0002f;
            var q = Solve(arm, dx, out _);
            fresh.Reset();
            var stateless = Solve(fresh, dx, out _);
            if (i > -50) worst = MathF.Max(worst, Turn(q, last));
            if (i == 10)
            {
                // 2 mm past the tie the arm is still in its basin, the other side of the stateless choice.
                heldAt = Turn(q, stateless);
            }

            if (float.IsNaN(switchedAt) && i > 10 && Turn(q, stateless) < 0.01f) switchedAt = dx;
            last = q;
        }

        _output.WriteLine($"2 mm past the tie: {heldAt:F3} rad from the stateless target; switched by dx {switchedAt * 1000f:F1} mm; worst {worst:F4} rad per tick");
        Assert.True(heldAt > 1f, $"2 mm past the tie the arm left its basin ({heldAt} rad from the stateless target)");
        Assert.False(float.IsNaN(switchedAt), "the arm never took the clearly better basin");
        Assert.True(worst < HeldTurnBound, $"the shoulder target turned {worst} rad in one tick");

        // Reset forgets the swivel: the next solve is the stateless rule, bit for bit, where the held arm differs.
        arm.Reset();
        Solve(arm, -0.01f, out _);
        var held = Solve(arm, 0.002f, out _);
        arm.Reset();
        var reset = Solve(arm, 0.002f, out var r1);
        fresh.Reset();
        var expected = Solve(fresh, 0.002f, out var r2);
        Assert.Equal(expected, reset);
        Assert.Equal(r2, r1);
        Assert.True(Turn(held, reset) > 1f, "the held arm already matched the stateless target, so the reset proves nothing");
    }

    [Fact]
    public void Solving_allocates_nothing()
    {
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var look = LookChain.Of(Plan);
        var s = arm.ShoulderAnchor;
        var rot = Quat.FromAxisAngle(0f, 1f, 0f, 0.2f);
        var up = Add((0f, 1.4f, 0f), Rot(rot, Sub(s, Plan.Parts[arm.ParentPart].Position)));
        Assert.False(arm.Solve(rot, 0f, 1.4f, 0f, up.X, up.Y + 0.4f, up.Z, out _, out _));
        void Work(int n)
        {
            for (var i = 0; i < n; i++)
            {
                var t = 0.3f + i % 7 * 0.03f;
                arm.Solve(rot, 0f, 1.4f, 0f, s.X, s.Y - t, s.Z + t, out _, out _);
                // Straight up from the shoulder, in reach: no swivel is feasible, so the least-violating search runs.
                arm.Solve(rot, 0f, 1.4f, 0f, up.X, up.Y + t, up.Z, out _, out _);
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
        Assert.Throws<ArgumentException>(() => ArmChain.Of(bare, Side.Left, Dt));
        Assert.Throws<ArgumentException>(() => LookChain.Of(bare));
    }
}
