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
        foreach (var limb in new[] { Limb.Pelvis, Limb.Torso })
        {
            var (linear, angular) = s.Speeds(limb);
            Assert.True(linear < 0.05f && angular < 0.2f, $"{label}: {limb} at {linear} m/s, {angular} rad/s");
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
            s.Target[(int)Muscle.KneeL] = Quat.FromAxisAngle(0f, 0f, 1f, 0.5f);
            s.Target[(int)Muscle.KneeR] = Quat.FromAxisAngle(0f, 0f, 1f, 0.5f);
            s.Strength[(int)Muscle.KneeL] = leftStrength; // a caller-owned element, written in place
            s.Run(0.5f);
            var v = s.World.View;
            return (v.JointAngle[s.Ragdoll.JointLane(Muscle.KneeL)], v.JointAngle[s.Ragdoll.JointLane(Muscle.KneeR)]);
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
        Assert.NotEqual(a[0], Hashes(42, (int)Muscle.HipL)[0]); // and so does one strength element
    }

    [Fact]
    public void Muscle_torques_are_equal_and_opposite_on_parent_and_child()
    {
        using var s = new RagdollScenario(seed: 1);
        var v = s.World.View;
        v.TorqueX.Clear(); v.TorqueY.Clear(); v.TorqueZ.Clear(); // drop the shove
        Array.Clear(s.Strength);
        s.Strength[(int)Muscle.ElbowL] = 1f;
        s.Strength[(int)Muscle.HipR] = 1f;
        s.Target[(int)Muscle.ElbowL] = Quat.FromAxisAngle(0f, 0f, 1f, -0.5f);
        s.Target[(int)Muscle.HipR] = Quat.FromAxisAngle(1f, 0f, 0f, 0.4f);
        s.Ragdoll.Drive(s.Strength, s.Target);

        float sx = 0f, sy = 0f, sz = 0f;
        for (var i = 0; i < v.Count; i++) { sx += v.TorqueX[i]; sy += v.TorqueY[i]; sz += v.TorqueZ[i]; }
        Assert.Equal(0f, sx, 1e-3f); Assert.Equal(0f, sy, 1e-3f); Assert.Equal(0f, sz, 1e-3f);

        // Only the four bodies of those two joints were touched, and the elbow child got the hinge torque.
        var touched = Enumerable.Range(0, v.Count).Where(i => v.TorqueX[i] != 0f || v.TorqueY[i] != 0f || v.TorqueZ[i] != 0f).ToHashSet();
        Assert.Equal(new HashSet<int>
        {
            s.Ragdoll.BodyLane(Limb.UpperArmL), s.Ragdoll.BodyLane(Limb.LowerArmL),
            s.Ragdoll.BodyLane(Limb.Pelvis), s.Ragdoll.BodyLane(Limb.ThighR),
        }, touched);
        Assert.NotEqual(0f, v.TorqueX[s.Ragdoll.BodyLane(Limb.LowerArmL)]);
    }

    private static float TorqueNorm(RagdollScenario s, Limb limb)
    {
        var v = s.World.View;
        var i = s.Ragdoll.BodyLane(limb);
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
        s.Target[(int)Muscle.HipL] = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f);
        ClearTorques(s);
        s.Strength[(int)Muscle.HipL] = 1f;
        s.Ragdoll.Drive(s.Strength, s.Target);
        var full = TorqueNorm(s, Limb.ThighL);
        ClearTorques(s);
        s.Strength[(int)Muscle.HipL] = 0.25f;
        s.Ragdoll.Drive(s.Strength, s.Target);
        Assert.True(full > 1f);
        Assert.Equal(full * 0.25f, TorqueNorm(s, Limb.ThighL), full * 1e-3f);
    }

    [Fact]
    public void A_hinge_takes_torque_about_its_own_axis_only_and_a_ball_joint_takes_any()
    {
        using var s = new RagdollScenario(seed: 1);
        Array.Clear(s.Strength);
        var offAxis = Quat.FromAxisAngle(1f, 0f, 0f, 0.3f); // about the joint frame's x: not the hinge axis (z)
        s.Target[(int)Muscle.ElbowL] = offAxis;
        s.Target[(int)Muscle.HipL] = offAxis;
        s.Strength[(int)Muscle.ElbowL] = 1f;
        s.Strength[(int)Muscle.HipL] = 1f;
        ClearTorques(s);
        s.Ragdoll.Drive(s.Strength, s.Target);
        Assert.Equal(0f, TorqueNorm(s, Limb.LowerArmL));  // the hinge ignores what it cannot turn
        Assert.True(TorqueNorm(s, Limb.ThighL) > 1f);     // the ball joint does not
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
        Assert.Equal(-1, v.Parent[r.BodyLane(Limb.Pelvis)]);
        Assert.Equal(r.BodyLane(Limb.Pelvis), v.Parent[r.BodyLane(Limb.Torso)]);
        Assert.Equal(r.BodyLane(Limb.ThighL), v.Parent[r.BodyLane(Limb.ShinL)]);
        Assert.Equal(r.BodyLane(Limb.ShinR), v.Parent[r.BodyLane(Limb.FootR)]);
        Assert.Equal((int)Limb.LowerArmR, v.Limb[r.BodyLane(Limb.LowerArmR)]);
        Assert.Equal((uint)Box3DSoaKind.Capsule, v.Kind[r.BodyLane(Limb.ThighL)]);
        Assert.Equal((uint)Box3DSoaKind.Box, v.Kind[r.BodyLane(Limb.Torso)]);
        Assert.Equal((uint)Box3DSoaKind.Sphere, v.Kind[r.BodyLane(Limb.Head)]);
        Assert.Equal(HumanoidRagdoll.MuscleCount, v.JointCount);
        Assert.Throws<ArgumentException>(() => r.Drive(new float[3], s.Target));
        Assert.Throws<ArgumentException>(() => r.Drive(s.Strength, new Quat[3]));
    }
}
