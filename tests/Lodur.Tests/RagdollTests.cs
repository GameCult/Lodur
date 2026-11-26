using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

public sealed class RagdollTests
{
    private const float StandingHead = 1.74f;

    [Theory]
    [InlineData(1ul)]
    [InlineData(2ul)]
    [InlineData(3ul)]
    public void A_ragdoll_with_full_strength_stands_through_a_shove(ulong seed)
    {
        using var s = new RagdollScenario(seed);
        s.Run(1.5f);
        Assert.InRange(s.HeadHeight, StandingHead - 0.1f, StandingHead + 0.05f);
        Assert.True(s.PelvisHeight > 1.05f);
        var (linear, angular) = s.MaxSpeeds();
        Assert.True(linear < 0.6f && angular < 1.0f, $"settled upright: {linear} m/s, {angular} rad/s");
    }

    /// <summary>The trunk is at rest; extremities may still rock a little (a head resting on an arm chatters at a few rad/s for seconds).</summary>
    private static void AssertResting(RagdollScenario s, string label)
    {
        foreach (var (limb, name) in new[] { (s.Plan.PartIndex("Pelvis"), "Pelvis"), (s.Plan.PartIndex("Torso"), "Torso") })
        {
            var (linear, angular) = s.Speeds(limb);
            Assert.True(linear < 0.05f && angular < 0.2f, $"{label}: {name} at {linear} m/s, {angular} rad/s");
        }
        var (maxLinear, maxAngular) = s.MaxSpeeds();
        Assert.True(maxLinear < 0.6f && maxAngular < 6f, $"{label}: an extremity still moves at {maxLinear} m/s, {maxAngular} rad/s");
        Assert.True(s.HeadHeight < 0.4f && s.PelvisHeight < 0.3f, $"{label}: lying down");
        var v = s.World.View;
        for (var i = 1; i < v.Count; i++)
        {
            Assert.True(v.PositionY[i] > 0f && float.IsFinite(v.PositionY[i]), $"{label}: body {i} above the ground");
        }
    }

    [Theory]
    [InlineData(1ul)]
    [InlineData(2ul)]
    [InlineData(3ul)]
    public void Without_strength_the_same_ragdoll_falls_and_comes_to_rest(ulong seed)
    {
        using var s = new RagdollScenario(seed);
        Array.Clear(s.Strength);
        s.Run(1.0f);
        Assert.True(s.HeadHeight < 0.6f, $"limp joints cannot hold it up: head at {s.HeadHeight}");
        s.Run(5.0f);
        AssertResting(s, $"seed {seed}");
    }

    [Fact]
    public void Stand_then_go_limp_falls_from_height_and_rests()
    {
        using var s = new RagdollScenario(seed: 7);
        s.Run(1.5f);
        Assert.True(s.HeadHeight > 1.6f);
        Array.Clear(s.Strength); // will gives out: the caller's array is the only thing that changed
        s.Run(1.0f);
        Assert.True(s.HeadHeight < 1.0f, $"falling after strength drops: head at {s.HeadHeight}");
        s.Run(5.0f);
        AssertResting(s, "after standing");
    }

    [Fact]
    public void Strength_is_per_muscle_zeroing_one_knee_changes_that_knee_and_not_the_other_arrays_entries()
    {
        static (float Left, float Right) Knees(float leftStrength)
        {
            using var s = new RagdollScenario(seed: 1);
            // Both knees are told to flex 0.5 rad about their hinge (the joint frame's z axis).
            s.Target[s.Plan.JointIndex("Knee", Side.Left)] = Quat.FromAxisAngle(0f, 0f, 1f, 0.5f);
            s.Target[s.Plan.JointIndex("Knee", Side.Right)] = Quat.FromAxisAngle(0f, 0f, 1f, 0.5f);
            s.Strength[s.Plan.JointIndex("Knee", Side.Left)] = leftStrength; // a caller-owned element, written in place
            s.Run(0.5f);
            var v = s.World.View;
            return (v.JointAngle[s.Ragdoll.JointLane(s.Plan.JointIndex("Knee", Side.Left))], v.JointAngle[s.Ragdoll.JointLane(s.Plan.JointIndex("Knee", Side.Right))]);
        }

        var (strongL, strongR) = Knees(1f);
        var (limpL, limpR) = Knees(0f);
        Assert.InRange(strongL, 0.35f, 1.2f);               // both follow the target, plus the sag of the body's weight
        Assert.True(MathF.Abs(strongL - strongR) < 0.15f, $"symmetric when both are strong: {strongL} {strongR}");
        Assert.True(MathF.Abs(limpL - limpR) > 0.3f, $"the limp knee no longer tracks: left {limpL}, right {limpR}");
    }

    [Fact]
    public void Same_seed_gives_the_same_state_hash_at_every_checkpoint_and_other_seeds_differ()
    {
        static List<ulong> Hashes(ulong seed, int strengthElementToFlip = -1)
        {
            using var s = new RagdollScenario(seed);
            if (strengthElementToFlip >= 0) s.Strength[strengthElementToFlip] = 0.5f;
            var hashes = new List<ulong>();
            for (var i = 0; i < 6; i++) { s.Run(0.5f); hashes.Add(s.StateHash()); }
            Array.Clear(s.Strength); // stand, then fall
            for (var i = 0; i < 8; i++) { s.Run(0.5f); hashes.Add(s.StateHash()); }
            return hashes;
        }

        var a = Hashes(42);
        Assert.Equal(a, Hashes(42));
        Assert.Equal(a.Count, a.Distinct().Count()); // the hash moves every checkpoint: the ragdoll is not frozen
        Assert.NotEqual(a[0], Hashes(43)[0]);        // the seed matters
        Assert.NotEqual(a[0], Hashes(42, BodyPlans.Humanoid().JointIndex("Hip", Side.Left))[0]); // and so does one strength element
    }

    [Fact]
    public void Muscle_torques_are_equal_and_opposite_on_parent_and_child()
    {
        using var s = new RagdollScenario(seed: 1);
        var v = s.World.View;
        v.TorqueX.Clear(); v.TorqueY.Clear(); v.TorqueZ.Clear(); // drop the shove
        Array.Clear(s.Strength);
        s.Strength[s.Plan.JointIndex("Elbow", Side.Left)] = 1f;
        s.Strength[s.Plan.JointIndex("Hip", Side.Right)] = 1f;
        s.Target[s.Plan.JointIndex("Elbow", Side.Left)] = Quat.FromAxisAngle(0f, 0f, 1f, -0.5f);
        s.Target[s.Plan.JointIndex("Hip", Side.Right)] = Quat.FromAxisAngle(1f, 0f, 0f, 0.4f);
        s.Ragdoll.Drive(s.Strength, s.Target);

        float sx = 0f, sy = 0f, sz = 0f;
        for (var i = 0; i < v.Count; i++) { sx += v.TorqueX[i]; sy += v.TorqueY[i]; sz += v.TorqueZ[i]; }
        Assert.Equal(0f, sx, 1e-3f); Assert.Equal(0f, sy, 1e-3f); Assert.Equal(0f, sz, 1e-3f);

        // Only the four bodies of those two joints were touched, and the elbow child got the hinge torque.
        var touched = Enumerable.Range(0, v.Count).Where(i => v.TorqueX[i] != 0f || v.TorqueY[i] != 0f || v.TorqueZ[i] != 0f).ToHashSet();
        Assert.Equal(new HashSet<int>
        {
            s.Ragdoll.BodyLane(s.Plan.PartIndex("UpperArm", Side.Left)), s.Ragdoll.BodyLane(s.Plan.PartIndex("LowerArm", Side.Left)),
            s.Ragdoll.BodyLane(s.Plan.PartIndex("Pelvis")), s.Ragdoll.BodyLane(s.Plan.PartIndex("Thigh", Side.Right)),
        }, touched);
        Assert.NotEqual(0f, v.TorqueX[s.Ragdoll.BodyLane(s.Plan.PartIndex("LowerArm", Side.Left))]);
    }

    private static float TorqueNorm(RagdollScenario s, int part)
    {
        var v = s.World.View;
        var i = s.Ragdoll.BodyLane(part);
        return MathF.Sqrt(v.TorqueX[i] * v.TorqueX[i] + v.TorqueY[i] * v.TorqueY[i] + v.TorqueZ[i] * v.TorqueZ[i]);
    }

    private static void ClearTorques(RagdollScenario s)
    {
        var v = s.World.View;
        v.TorqueX.Clear(); v.TorqueY.Clear(); v.TorqueZ.Clear();
    }

    [Fact]
    public void A_fractional_strength_scales_the_written_torque_linearly()
    {
        using var s = new RagdollScenario(seed: 1);
        Array.Clear(s.Strength);
        s.Target[s.Plan.JointIndex("Hip", Side.Left)] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
        ClearTorques(s);
        s.Strength[s.Plan.JointIndex("Hip", Side.Left)] = 1f;
        s.Ragdoll.Drive(s.Strength, s.Target);
        var full = TorqueNorm(s, s.Plan.PartIndex("Thigh", Side.Left));
        ClearTorques(s);
        s.Strength[s.Plan.JointIndex("Hip", Side.Left)] = 0.25f;
        s.Ragdoll.Drive(s.Strength, s.Target);
        Assert.True(full > 1f);
        Assert.Equal(full * 0.25f, TorqueNorm(s, s.Plan.PartIndex("Thigh", Side.Left)), full * 1e-3f);
    }

    [Fact]
    public void A_hinge_takes_torque_about_its_own_axis_only_and_a_ball_joint_takes_any()
    {
        // The gravity feedforward is the same under either target, so what differs is the PD term alone: the hinge turns what it can (its target's off-axis part changes nothing,
        // bit for bit) and the ball joint takes the swing it is told.
        using var s = new RagdollScenario(seed: 1);
        Array.Clear(s.Strength);
        var elbow = s.Plan.JointIndex("Elbow", Side.Left);
        var hip = s.Plan.JointIndex("Hip", Side.Left);
        var forearm = s.Plan.PartIndex("LowerArm", Side.Left);
        var thigh = s.Plan.PartIndex("Thigh", Side.Left);
        s.Strength[elbow] = 1f;
        s.Strength[hip] = 1f;
        (float X, float Y, float Z) Torque(int part) { var v = s.World.View; var i = s.Ragdoll.BodyLane(part); return (v.TorqueX[i], v.TorqueY[i], v.TorqueZ[i]); }

        s.Target[elbow] = Quat.Identity;
        s.Target[hip] = Quat.Identity;
        ClearTorques(s);
        s.Ragdoll.Drive(s.Strength, s.Target);
        var straightForearm = Torque(forearm);
        var straightThigh = Torque(thigh);

        var offAxis = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f); // about the joint frame's x: not the hinge axis (z)
        s.Target[elbow] = offAxis;
        s.Target[hip] = offAxis;
        ClearTorques(s);
        s.Ragdoll.Drive(s.Strength, s.Target);
        Assert.Equal(straightForearm, Torque(forearm)); // the hinge ignores what it cannot turn
        var t = Torque(thigh);
        var change = MathF.Sqrt((t.X - straightThigh.X) * (t.X - straightThigh.X) + (t.Y - straightThigh.Y) * (t.Y - straightThigh.Y) + (t.Z - straightThigh.Z) * (t.Z - straightThigh.Z));
        Assert.True(change > 1f, $"the ball joint's thigh torque moved {change} N m"); // the ball joint does not
    }

    [Fact]
    public void The_gravity_feedforward_on_a_hinge_acts_about_its_axis_only()
    {
        // A forearm held out of the vertical (shoulder raised, elbow bent, everything at full strength for 1 s), then only the elbow strong: the load's torque about the elbow
        // is not zero there, and what the forearm gets is along the elbow's axis, though the load's own torque is not (the hinge has twist gains only).
        using var s = new RagdollScenario(seed: 1);
        var shoulder = s.Plan.JointIndex("Shoulder", Side.Left);
        var elbow = s.Plan.JointIndex("Elbow", Side.Left);
        s.Target[shoulder] = Quat.FromAxisAngle(1f, 0f, 0f, -1.2f);
        s.Target[elbow] = Quat.FromAxisAngle(0f, 0f, 1f, 0.8f);
        s.Run(1f);
        var forearm = s.Plan.PartIndex("LowerArm", Side.Left);
        Array.Clear(s.Strength);
        ClearTorques(s);
        s.Ragdoll.Drive(s.Strength, s.Target);
        var v = s.World.View;
        var i = s.Ragdoll.BodyLane(forearm);
        var (x0, y0, z0) = (v.TorqueX[i], v.TorqueY[i], v.TorqueZ[i]); // the wrist's and the elbow's tissue damping alone, which the difference below removes
        s.Strength[elbow] = 1f;
        ClearTorques(s);
        s.Ragdoll.Drive(s.Strength, s.Target);
        var (dx, dy, dz) = (v.TorqueX[i] - x0, v.TorqueY[i] - y0, v.TorqueZ[i] - z0);
        var j = s.Ragdoll.JointLane(elbow);
        new Quat(v.JointFrameX[j], v.JointFrameY[j], v.JointFrameZ[j], v.JointFrameW[j]).Rotate(0f, 0f, 1f, out var ax, out var ay, out var az);
        var total = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        var along = dx * ax + dy * ay + dz * az;
        Assert.True(total > 0.5f, $"the held-out forearm gets {total} N m");
        Assert.True(MathF.Abs(along) > total * 0.99999f, $"{total} N m on the forearm, {along} of it along the elbow axis");
    }

    /// <summary>
    /// Zero gravity: Drive writes the PD torque and nothing else (the feedforward is skipped), so the 2 s state hash of a free ragdoll, seeds 1-3, is the one recorded at
    /// origin/main e47e3d3, before the gravity feedforward. Goldens move only where a ruling says; this one pins that the feedforward is zero where gravity is.
    /// </summary>
    [Fact]
    public void A_zero_gravity_world_replays_the_hash_recorded_before_the_gravity_feedforward()
    {
        var recorded = new ulong[] { 4596821165163159072ul, 2187121869401780341ul, 2995211072229544614ul };
        var got = new ulong[3];
        for (var k = 0; k < 3; k++)
        {
            using var s = new RagdollScenario((ulong)(k + 1), gravityY: 0f);
            s.Run(2f);
            got[k] = s.StateHash();
        }

        Assert.True(recorded.SequenceEqual(got), string.Join(", ", got));
    }

    [Fact]
    public void Driving_a_tick_allocates_nothing()
    {
        using var s = new RagdollScenario(seed: 1);
        s.Ragdoll.Drive(s.Strength, s.Target); // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) s.Ragdoll.Drive(s.Strength, s.Target);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void The_world_carries_the_limb_tree_and_the_arrays_must_have_one_entry_per_muscle()
    {
        using var s = new RagdollScenario(seed: 1);
        var v = s.World.View;
        var r = s.Ragdoll;
        Assert.Equal(-1, v.Parent[r.BodyLane(s.Plan.PartIndex("Pelvis"))]);
        Assert.Equal(r.BodyLane(s.Plan.PartIndex("Pelvis")), v.Parent[r.BodyLane(s.Plan.PartIndex("Torso"))]);
        Assert.Equal(r.BodyLane(s.Plan.PartIndex("Thigh", Side.Left)), v.Parent[r.BodyLane(s.Plan.PartIndex("Shin", Side.Left))]);
        Assert.Equal(r.BodyLane(s.Plan.PartIndex("Shin", Side.Right)), v.Parent[r.BodyLane(s.Plan.PartIndex("Foot", Side.Right))]);
        Assert.Equal(s.Plan.PartIndex("LowerArm", Side.Right), v.Limb[r.BodyLane(s.Plan.PartIndex("LowerArm", Side.Right))]);
        Assert.Equal((uint)Box3DSoaKind.Capsule, v.Kind[r.BodyLane(s.Plan.PartIndex("Thigh", Side.Left))]);
        Assert.Equal((uint)Box3DSoaKind.Box, v.Kind[r.BodyLane(s.Plan.PartIndex("Torso"))]);
        Assert.Equal((uint)Box3DSoaKind.Sphere, v.Kind[r.BodyLane(s.Plan.PartIndex("Head"))]);
        Assert.Equal(s.Plan.Joints.Count, v.JointCount);
        Assert.Throws<ArgumentException>(() => r.Drive(new float[3], s.Target));
        Assert.Throws<ArgumentException>(() => r.Drive(s.Strength, new Quat[3]));
    }
}
