using System;
using Lodur;
using Xunit.Abstractions;

namespace Lodur.Core.Tests;

/// <summary>
/// An arm's motion across ticks, owned by <see cref="ArmChain"/>: the minimum-jerk hand path through a via point ahead of the trunk, taken only
/// when the trunk is in the way (rulings reach-clears-body-by-via-point, reach-path-blocked-only), and the hand-off into and out of the arm layer,
/// slewed at the output rate both ways (ruling ik-output-slew-limit, follow-up orient-s4-lows).
/// </summary>
public sealed class ArmPathTests
{
    private const double Dt = 0.01;
    private const uint Thing = 7;
    private const float Step = (float)(ArmChain.MaxAngularRate * Dt);
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();
    private static readonly int Joints = Plan.Joints.Count;
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();
    private static readonly float[] Origin = { 0f };
    private readonly ITestOutputHelper _output;

    public ArmPathTests(ITestOutputHelper output) => _output = output;

    private static (float X, float Y, float Z) Mirror((float X, float Y, float Z) p, Side side) => side == Side.Right ? p : (-p.X, p.Y, p.Z);

    private static float Gap((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static float Turn(Quat a, Quat b) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W)));

    /// <summary>Drives the arm with the body at its creation pose and the wrist where it hangs, or at <paramref name="wrist"/>.</summary>
    private static bool Drive(ArmChain arm, (float X, float Y, float Z) goal, Quat baseShoulder, Quat baseElbow, double weight, out Quat shoulder, out Quat elbow,
        (float X, float Y, float Z)? wrist = null)
    {
        var parent = Plan.Parts[arm.ParentPart];
        var w = wrist ?? arm.WristAnchor;
        return arm.Drive(parent.Rotation, parent.Position.X, parent.Position.Y, parent.Position.Z, goal.X, goal.Y, goal.Z, w.X, w.Y, w.Z,
            baseShoulder, baseElbow, weight, out shoulder, out elbow);
    }

    [Theory]
    [InlineData(Side.Right)]
    [InlineData(Side.Left)]
    public void Blocked_is_decided_by_the_trunk_once_per_path_start(Side side)
    {
        // The hanging wrist to a goal across the chest and level with the torso: the straight segment touches the trunk. To a goal out to the
        // arm's own side it stays clear of the trunk by more than the forearm radius.
        var arm = ArmChain.Of(Plan, side, Dt);
        var across = Mirror((-0.04f, 1.60f, 0.24f), side);
        var aside = Mirror((-0.30f, 1.74f, 0.26f), side);

        Drive(arm, across, Quat.Identity, Quat.Identity, 1.0, out _, out _);
        Assert.True(arm.PathBlocked);
        Assert.True(Gap(arm.Aim, arm.WristAnchor) < 1e-4f, $"the path starts at the wrist, not {arm.Aim}");

        // Decided once: the goal moving clear of the trunk does not unblock the path.
        Drive(arm, aside, Quat.Identity, Quat.Identity, 1.0, out _, out _);
        Assert.True(arm.PathBlocked);

        arm.Restart();
        Drive(arm, aside, Quat.Identity, Quat.Identity, 1.0, out _, out _);
        Assert.False(arm.PathBlocked);
        Assert.True(Gap(arm.Aim, aside) < 1e-4f, $"an unblocked reach solves the goal itself, not {arm.Aim}");
    }

    [Fact]
    public void The_hand_path_is_minimum_jerk_through_the_via_point_and_meets_a_moving_goal()
    {
        var start = (0f, 1f, 0f);
        var via = (0.1f, 1.2f, 0.3f);
        var seg = ArmChain.PathSegmentSeconds;
        var g0 = (0.5f, 1.5f, 0.4f);
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g0, 0f), start) < 1e-4f);
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g0, seg), via) < 1e-4f);
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g0, 2f * seg), g0) < 1e-4f);
        var mid = ((start.Item1 + via.Item1) / 2f, (start.Item2 + via.Item2) / 2f, (start.Item3 + via.Item3) / 2f);
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g0, 0.5f * seg), mid) < 1e-4f, "midway in the first segment is the midpoint");
        // Zero velocity at both ends of a segment: a tick in is far less than a linear tick in.
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g0, 0.01f * seg), start) < 1e-3f * Gap(start, via));

        // A goal that has moved on during the second segment is met at its end, and the via point's offset shrinks to nothing on the way.
        var g1 = (0.7f, 1.4f, 0.5f);
        Assert.True(Gap(ArmChain.PathPoint(start, via, g0, g1, 2f * seg), g1) < 1e-4f);
        var half = ArmChain.PathPoint(start, via, g0, g1, 1.5f * seg);
        var want = (g1.Item1 + 0.5f * (via.Item1 - g0.Item1), g1.Item2 + 0.5f * (via.Item2 - g0.Item2), g1.Item3 + 0.5f * (via.Item3 - g0.Item3));
        Assert.True(Gap(half, want) < 1e-4f);

        // The chain follows it from the measured wrist: the via point is (wrist x, mid y, trunk front + clearance), the trunk front 0.09 m on the humanoid.
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var wrist = arm.WristAnchor;
        (float X, float Y, float Z) goal = (-0.04f, 1.60f, 0.24f);
        (float X, float Y, float Z) viaWant = (wrist.X, 0.5f * (wrist.Y + goal.Y), 0.09f + ArmChain.ViaClearance);
        var ticks = (int)Math.Round(2.0 * ArmChain.PathSegmentSeconds / Dt);
        for (var i = 0; i <= ticks + 5; i++)
        {
            // The goal drifts 0.1 m forward over the path.
            (float X, float Y, float Z) g = (goal.X, goal.Y, goal.Z + 0.1f * Math.Min(1f, i / (float)ticks));
            Drive(arm, g, Quat.Identity, Quat.Identity, 1.0, out _, out _);
            var t = i * (float)Dt;
            if (i == 0) Assert.True(Gap(arm.Aim, wrist) < 1e-4f);
            if (i == (int)Math.Round(0.5 * ArmChain.PathSegmentSeconds / Dt))
            {
                var half1 = ((wrist.X + viaWant.X) / 2f, (wrist.Y + viaWant.Y) / 2f, (wrist.Z + viaWant.Z) / 2f);
                Assert.True(Gap(arm.Aim, half1) < 2e-3f, $"midway to the via point at {t} s: {arm.Aim}");
            }

            if (i == (int)Math.Round(ArmChain.PathSegmentSeconds / Dt))
            {
                Assert.True(Gap(arm.Aim, viaWant) < 2e-3f, $"at the via point at {t} s: {arm.Aim}");
            }

            if (i >= ticks)
            {
                Assert.True(Gap(arm.Aim, g) < 1e-4f, $"after the path the goal itself is solved for, not {arm.Aim}");
            }
        }
    }

    [Fact]
    public void Slewed_by_the_actor_the_hand_off_moves_the_written_arm_targets_at_most_the_rate_both_ways_and_ends_in_the_base()
    {
        // A pose goal and a ward-off goal alternate as a sound comes and goes. Every tick, the shoulder and elbow targets of both arms move at most
        // the rate times the tick, across engaging and disengaging; and once the arms have eased back they write exactly what the pose goal does.
        var key = new Quat[Joints];
        for (var m = 0; m < Joints; m++) key[m] = Quat.FromAxisAngle(0f, 0f, 1f, 0.02f * (m + 1));
        var idle = Pose(key, null);
        var ward = Pose(key, new ArmLayer { Mode = ArmMode.WardOff, Side = ArmSide.Both, Distance = 0.3f, Spread = 0.15f });
        var brain = new Brain
        {
            Goals = new[]
            {
                new GoalSpec { Id = "idle", Priority = 0.0, Behaviour = idle },
                new GoalSpec
                {
                    Id = "ward", Priority = 1.0, Target = new TargetFilter { Kinds = new[] { Thing } },
                    Considerations = new[] { new Consideration(ConsiderationInput.Distance, 0.0, 60.0, 1.0, 0.0) },
                    Behaviour = ward,
                },
            },
        };
        var actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = 4, MemorySeconds = 0.2 }, body: Plan);
        var parts = Rest();
        var targets = new Quat[Joints];
        var previous = new Quat[Joints];
        var strengths = new float[Joints];
        var armJoints = new[] { Plan.JointIndex("Shoulder", Side.Left), Plan.JointIndex("Elbow", Side.Left), Plan.JointIndex("Shoulder", Side.Right), Plan.JointIndex("Elbow", Side.Right) };
        var sound = new[] { new Emitter { Channel = Channel.Audio, X = 0.3f, Y = 1.5f, Z = 0.7f, Radius = 40f, Intensity = 1.0, Id = 1, Kind = Thing } };
        var reference = new Quat[Joints];
        idle.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[Joints], reference);

        var (worst, engaged, released, restored) = (0f, 0, 0, 0);
        var wasWard = false;
        for (var i = 0; i < 1800; i++)
        {
            var on = (i / 150) % 2 == 0;
            actor.Tick(Origin, Origin, Origin, NoBoxes, strengths, targets, on ? sound : Array.Empty<Emitter>(), parts: parts);
            var ward1 = actor.GoalIndex == 1;
            if (ward1 && !wasWard) engaged++;
            if (!ward1 && wasWard) released++;
            wasWard = ward1;
            if (i > 0)
            {
                foreach (var m in armJoints) worst = MathF.Max(worst, Turn(previous[m], targets[m]));
            }

            if (!ward1 && i % 150 == 149 && !on)
            {
                // The arms have had the rest of the off-phase to ease back.
                var same = true;
                for (var m = 0; m < Joints; m++) same &= targets[m].Equals(reference[m]);
                if (same) restored++;
            }

            Array.Copy(targets, previous, Joints);
        }

        _output.WriteLine($"worst per-tick change {worst:F5} rad against {Step}; engaged {engaged}, released {released}, written the base bit for bit at {restored} phase ends");
        Assert.True(engaged >= 5 && released >= 5, $"the goals alternated too little to test the hand-off: {engaged}, {released}");
        Assert.True(worst <= Step + 1e-5f, $"an arm target moved {worst} rad in one tick, the bound is {Step}");
        Assert.True(worst > 0.5f * Step, $"the arms moved at most {worst} rad a tick, so the hand-off was not slewed at the rate");
        Assert.True(restored >= 5, $"the arms wrote the base pose bit for bit at only {restored} of the off-phases");

        static PoseBehaviour Pose(Quat[] key, ArmLayer? arms) => new()
        {
            Keys = new[] { key }, FlinchPose = Array.ConvertAll(key, _ => Quat.Identity), Arms = arms,
        };
    }

    [Fact]
    public void A_released_arm_stays_the_base_and_reset_and_restart_forget_the_path_and_the_hand_off()
    {
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var baseShoulder = Quat.FromAxisAngle(0f, 0f, 1f, 0.2f);
        var baseElbow = Quat.FromAxisAngle(0f, 0f, 1f, -0.3f);
        var goal = (-0.04f, 1.60f, 0.24f);

        // An arm never driven writes the base untouched.
        arm.Release(baseShoulder, baseElbow, out var s0, out var e0);
        Assert.Equal(baseShoulder, s0);
        Assert.Equal(baseElbow, e0);

        for (var i = 0; i < 100; i++) Drive(arm, goal, baseShoulder, baseElbow, 1.0, out _, out _);
        var held = Quat.Identity;
        var converged = -1;
        for (var i = 0; i < 100; i++)
        {
            arm.Release(baseShoulder, baseElbow, out var s, out var e);
            if (i > 0) Assert.True(Turn(held, s) <= Step + 1e-5f, "the release moved the shoulder faster than the rate");
            held = s;
            if (converged < 0 && s.Equals(baseShoulder) && e.Equals(baseElbow)) converged = i;
            if (converged >= 0) Assert.True(s.Equals(baseShoulder) && e.Equals(baseElbow), "a released arm left the base after arriving");
        }

        Assert.True(converged > 3, $"the arm was back on the base after {converged} ticks: it was not slewed");

        // Reset forgets the hand-off: the arm is idle, so the base passes through at once.
        for (var i = 0; i < 20; i++) Drive(arm, goal, baseShoulder, baseElbow, 1.0, out _, out _);
        arm.Reset();
        arm.Release(baseShoulder, baseElbow, out var s1, out var e1);
        Assert.Equal(baseShoulder, s1);
        Assert.Equal(baseElbow, e1);

        // At half weight the written targets are the base blended toward the output, and the hand-off is continuous in them, not in the output.
        var half = ArmChain.Of(Plan, Side.Right, Dt);
        var writtenHalf = Quat.Identity;
        for (var i = 0; i < 100; i++) Drive(half, goal, baseShoulder, baseElbow, 0.5, out writtenHalf, out _);
        var released = 0f;
        for (var i = 0; i < 20; i++)
        {
            half.Release(baseShoulder, baseElbow, out var sh, out _);
            released = MathF.Max(released, Turn(writtenHalf, sh));
            writtenHalf = sh;
        }

        Assert.True(released <= Step + 1e-5f, $"releasing at half weight moved the written shoulder {released} rad in a tick");

        // Restart starts the path again at the measured wrist, keeping the output: the aim goes back to the wrist.
        for (var i = 0; i < 100; i++) Drive(arm, goal, baseShoulder, baseElbow, 1.0, out _, out _);
        Assert.True(Gap(arm.Aim, goal) < 1e-4f);
        arm.Restart();
        Drive(arm, goal, baseShoulder, baseElbow, 1.0, out _, out _);
        Assert.True(Gap(arm.Aim, arm.WristAnchor) < 1e-4f, $"a restarted path begins at the measured wrist, not {arm.Aim}");
    }

    [Fact]
    public void A_commit_restarts_both_arms_paths_from_the_measured_wrist()
    {
        // Reach across the body with the right arm, settled; the parts do not follow, so the measured wrist stays hanging. A tick after
        // the rig is told of a commit, the arm is on a new path from that wrist and its output turns back toward it by a step; without
        // the commit the settled output does not move.
        var key = new Quat[Joints];
        Array.Fill(key, Quat.Identity);
        var b = new PoseBehaviour { Keys = new[] { key }, FlinchPose = key, Arms = new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Right } };
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "g", Behaviour = b } } };
        var parts = Rest();
        var target = new Percept(1, Thing, 0.3f, 1.45f, 0.5f, 1.0, 0.0);
        var shoulder = Plan.JointIndex("Shoulder", Side.Right);
        foreach (var commit in new[] { false, true })
        {
            var rig = new OrientRig(Plan, brain, Dt);
            var targets = new Quat[Joints];
            for (var i = 0; i < 100; i++) b.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[Joints], targets, new OrientInput(rig, parts, target, Side.Right));
            var settled = targets[shoulder];
            if (commit) rig.Commit();
            b.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[Joints], targets, new OrientInput(rig, parts, target, Side.Right));
            var moved = Turn(settled, targets[shoulder]);
            if (commit) Assert.True(moved > 0.5f * Step, $"after a commit the arm moved only {moved} rad");
            else Assert.True(moved < 1e-4f, $"a settled arm moved {moved} rad without a commit");
        }
    }

    [Fact]
    public void The_actor_restarts_the_hand_path_when_it_commits_another_target()
    {
        // The right arm reaches across the body to a sound, settled. The parts do not follow, so the measured wrist stays hanging. A second,
        // sound, a millimetre nearer, is committed a latency later and the arm starts a new path from that wrist, turning back by steps; with no second sound the
        // settled arm does not move.
        var key = new Quat[Joints];
        Array.Fill(key, Quat.Identity);
        PoseBehaviour Pose(ArmLayer? arms) => new() { Keys = new[] { key }, FlinchPose = key, Arms = arms };
        var brain = new Brain
        {
            Goals = new[]
            {
                new GoalSpec { Id = "idle", Priority = 0.0, Behaviour = Pose(null) },
                new GoalSpec
                {
                    Id = "reach", Priority = 1.0, Target = new TargetFilter { Kinds = new[] { Thing } },
                    Considerations = new[] { new Consideration(ConsiderationInput.Distance, 0.0, 60.0, 1.0, 0.0) },
                    Behaviour = Pose(new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Right }),
                },
            },
        };
        Emitter Sound(ulong id, float x, float y, float z) => new() { Channel = Channel.Audio, X = x, Y = y, Z = z, Radius = 40f, Intensity = 1.0, Id = id, Kind = Thing };
        var shoulder = Plan.JointIndex("Shoulder", Side.Right);
        foreach (var retarget in new[] { false, true })
        {
            var actor = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
                new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
                new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 }, body: Plan);
            var parts = Rest();
            var targets = new Quat[Joints];
            var first = new[] { Sound(1, 0.3f, 1.45f, 0.5f) };
            var both = new[] { Sound(1, 0.3f, 1.45f, 0.5f), Sound(2, 0.3f, 1.45f, 0.499f) };
            for (var i = 0; i < 150; i++) actor.Tick(Origin, Origin, Origin, NoBoxes, new float[Joints], targets, first, parts: parts);
            Assert.Equal(1UL, actor.TargetId);
            var last = targets[shoulder];
            var worst = 0f;
            for (var i = 0; i < 100; i++)
            {
                actor.Tick(Origin, Origin, Origin, NoBoxes, new float[Joints], targets, retarget ? both : first, parts: parts);
                worst = MathF.Max(worst, Turn(last, targets[shoulder]));
                last = targets[shoulder];
            }

            if (retarget)
            {
                Assert.Equal(2UL, actor.TargetId);
                Assert.True(worst > 0.5f * Step, $"a committed retarget moved the arm at most {worst} rad in a tick");
            }
            else
            {
                Assert.True(worst < 1e-3f, $"a settled arm moved {worst} rad in a tick with nothing new");
            }
        }
    }

    [Fact]
    public void An_engaging_arm_takes_the_short_way_from_a_base_given_with_the_opposite_sign()
    {
        // The same rotation as q and as -q: engaging from -q must not turn the long way round to the solution q.
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var fresh = ArmChain.Of(Plan, Side.Right, Dt);
        var goal = (-0.2f, 1.4f, 0.35f);
        var parent = Plan.Parts[arm.ParentPart];
        fresh.Solve(parent.Rotation, parent.Position.X, parent.Position.Y, parent.Position.Z, goal.Item1, goal.Item2, goal.Item3, out var solution, out _);
        // A base a few steps short of the solution, negated: engaging turns the step toward it, not away.
        var toward = Quat.Nlerp(solution, Quat.Identity, 0.05f);
        var negated = new Quat(-toward.X, -toward.Y, -toward.Z, -toward.W);
        Drive(arm, goal, negated, Quat.Identity, 1.0, out var written, out _);
        Assert.True(Turn(negated, written) <= Step + 1e-5f, $"engaging turned {Turn(negated, written)} rad from the base in one tick");
        Assert.True(Turn(written, solution) < Turn(negated, solution) + 1e-4f, "engaging turned away from the solution");
        Assert.True(Turn(negated, solution) > 0f);
    }

    [Theory]
    [InlineData(Side.Right)]
    [InlineData(Side.Left)]
    public void A_goal_outrunning_the_rate_keeps_the_shoulder_target_a_unit_rotation(Side side)
    {
        // The goal circles the shoulder so fast that the target is always behind it: ten thousand slewed steps compose without drift.
        var arm = ArmChain.Of(Plan, side, Dt);
        var s = arm.ShoulderAnchor;
        var worst = 0f;
        var chasing = 0;
        var last = Quat.Identity;
        for (var i = 0; i < 10000; i++)
        {
            var th = 0.9f * i;
            arm.Solve(Plan.Parts[arm.ParentPart].Rotation, Plan.Parts[arm.ParentPart].Position.X, Plan.Parts[arm.ParentPart].Position.Y, Plan.Parts[arm.ParentPart].Position.Z,
                s.X + 0.4f * MathF.Sin(th), s.Y - 0.1f, s.Z + 0.4f * MathF.Cos(th), out var q, out _);
            worst = MathF.Max(worst, MathF.Abs(MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W) - 1f));
            if (i > 0 && Turn(last, q) > 0.99f * Step) chasing++;
            last = q;
        }

        _output.WriteLine($"{side}: norm error {worst:E2}, {chasing} of 10000 ticks at the rate");
        Assert.True(chasing > 5000, $"the target was at the rate on only {chasing} ticks, so it was not outrun");
        Assert.True(worst < 1e-5f, $"the shoulder target left the unit sphere by {worst}");
    }

    // A pose with the arms at rest, to look at the written targets of.
    private static PartPose[] Rest()
    {
        var poses = new PartPose[Plan.Parts.Count];
        for (var p = 0; p < poses.Length; p++)
        {
            var d = Plan.Parts[p];
            poses[p] = new PartPose(d.Rotation, d.Position.X, d.Position.Y, d.Position.Z);
        }

        return poses;
    }
}
