using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>The builder assumes no species: a six-part quadruped builds and steps beside the humanoid in one caller world.</summary>
public sealed class QuadrupedPlanTests
{
    private static BodyPlan Quadruped()
    {
        PartDef Part(string label, Side side, PartShape shape, (float, float, float) size, (float, float, float) at) =>
            new(label, side, shape, size, at, Quat.Identity, 1000f, 0.8f, 0f);
        JointDef Joint(string label, Side side, JointKind kind, int parent, int child, (float, float, float) at, (float, float, float) axis, float lower, float upper, float cone) =>
            new(label, side, kind, parent, child, at, axis, lower, upper, cone, new MuscleGains(10f, 1f, 5f, 0.5f));

        var parts = new[]
        {
            Part("Trunk", Side.Center, PartShape.Box, (0.4f, 0.15f, 0.12f), (0f, 0.6f, 0f)),
            Part("Head", Side.Center, PartShape.Sphere, (0.12f, 0f, 0f), (0.55f, 0.8f, 0f)),
            Part("Foreleg", Side.Left, PartShape.Capsule, (0.04f, 0.2f, 0f), (0.3f, 0.25f, 0.1f)),
            Part("Foreleg", Side.Right, PartShape.Capsule, (0.04f, 0.2f, 0f), (0.3f, 0.25f, -0.1f)),
            Part("Hindleg", Side.Left, PartShape.Capsule, (0.04f, 0.2f, 0f), (-0.3f, 0.25f, 0.1f)),
            Part("Hindleg", Side.Right, PartShape.Capsule, (0.04f, 0.2f, 0f), (-0.3f, 0.25f, -0.1f)),
        };
        var joints = new[]
        {
            Joint("Neck", Side.Center, JointKind.Spherical, 0, 1, (0.45f, 0.7f, 0f), (0f, 1f, 0f), -0.5f, 0.5f, 0.5f),
            Joint("Shoulder", Side.Left, JointKind.Revolute, 0, 2, (0.3f, 0.5f, 0.1f), (0f, 0f, 1f), -1f, 1f, 0f),
            Joint("Shoulder", Side.Right, JointKind.Revolute, 0, 3, (0.3f, 0.5f, -0.1f), (0f, 0f, 1f), -1f, 1f, 0f),
            Joint("Hip", Side.Left, JointKind.Revolute, 0, 4, (-0.3f, 0.5f, 0.1f), (0f, 0f, 1f), -1f, 1f, 0f),
            Joint("Hip", Side.Right, JointKind.Revolute, 0, 5, (-0.3f, 0.5f, -0.1f), (0f, 0f, 1f), -1f, 1f, 0f),
        };
        return new BodyPlan(parts, joints, new[] { new SensePointDef("Muzzle", Side.Center, 1, (0.12f, 0f, 0f)) });
    }

    [Fact]
    public void A_quadruped_builds_after_the_humanoid_in_one_world_in_its_own_plan_order_and_steps_finite()
    {
        var humanoidPlan = BodyPlans.Humanoid();
        var quadPlan = Quadruped();
        var (hb, hj, hs) = Ragdoll.Needs(humanoidPlan);
        var (qb, qj, qs) = Ragdoll.Needs(quadPlan);
        Assert.Equal((6, 5, 0), (qb, qj, qs));
        Assert.Equal(2, hs);
        using var world = new Box3DSoaWorld(capacity: hb + qb + 1, jointCapacity: hj + qj);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var human = Ragdoll.Build(world, humanoidPlan, (0f, 0f), firstStableId: 100);
        var quad = Ragdoll.Build(world, quadPlan, (2f, 0f), firstStableId: 200);

        var v = world.View;
        Assert.Equal(1 + hb + qb, v.Count);
        Assert.Equal(hj + qj, v.JointCount);
        for (var i = 0; i < qb; i++)
        {
            var lane = quad.BodyLane(i);
            Assert.Equal(1 + hb + i, lane);                                  // contiguous, right after the humanoid
            Assert.Equal(200ul + (ulong)i, v.StableId[lane]);                // stable id = first + plan ordinal
            Assert.Equal(i, v.Limb[lane]);                                   // limb lane = plan ordinal
            Assert.Equal(quadPlan.ParentOf(i) < 0 ? -1 : quad.BodyLane(quadPlan.ParentOf(i)), v.Parent[lane]);
        }

        for (var j = 0; j < qj; j++)
        {
            var lane = quad.JointLane(j);
            Assert.Equal(hj + j, lane);
            Assert.Equal((quad.BodyLane(quadPlan.Joints[j].Parent), quad.BodyLane(quadPlan.Joints[j].Child)), (v.JointBodyA[lane], v.JointBodyB[lane]));
        }

        Assert.Equal((uint)Box3DSoaKind.Box, v.Kind[quad.BodyLane(quadPlan.PartIndex("Trunk"))]);
        Assert.Equal((uint)Box3DSoaKind.Sphere, v.Kind[quad.BodyLane(quadPlan.PartIndex("Head"))]);
        Assert.Equal((uint)Box3DSoaKind.Capsule, v.Kind[quad.BodyLane(quadPlan.PartIndex("Hindleg", Side.Right))]);
        Assert.Equal(2.3f, v.PositionX[quad.BodyLane(quadPlan.PartIndex("Foreleg", Side.Left))], 4); // origin x 2 + plan x 0.3

        var humanStrength = new float[humanoidPlan.Joints.Count]; var humanTarget = new Quat[humanoidPlan.Joints.Count];
        var quadStrength = new float[qj]; var quadTarget = new Quat[qj];
        Array.Fill(humanTarget, Quat.Identity); Array.Fill(quadTarget, Quat.Identity);
        for (var tick = 0; tick < 60; tick++)
        {
            human.Drive(humanStrength, humanTarget);
            quad.Drive(quadStrength, quadTarget);
            world.Step(1f / 120f, 4);
        }

        for (var i = 0; i < v.Count; i++)
        {
            Assert.True(float.IsFinite(v.PositionX[i] + v.PositionY[i] + v.PositionZ[i] + v.RotationW[i]), $"body {i} finite");
        }

        var x = new float[1]; var y = new float[1]; var z = new float[1];
        quad.ReadSensePoints(x, y, z);
        var head = quad.BodyLane(quadPlan.PartIndex("Head"));
        Assert.True(MathF.Abs(x[0] - v.PositionX[head]) < 0.2f); // the muzzle sits within its offset of the head
        Assert.Throws<ArgumentException>(() => quad.ReadSensePoints(new float[3], new float[3], new float[3]));
        Assert.Throws<ArgumentException>(() => quad.Drive(new float[3], quadTarget));
    }

    private const float SenseTolerance = 3.5e-4f; // geometric mean of the shipped worst (one float ulp at 3, 2.4e-7) and the weakest mutant (0.5)

    [Fact]
    public void A_sense_point_reads_its_parts_position_plus_the_parts_rotation_applied_to_its_offset()
    {
        // Root turned 90 degrees about z: the offset (0.5, 0.2, 0.1) lands at (-0.2, 0.5, 0.1) from the part, not at (0.5, 0.2, 0.1) and not at 0.
        var turned = Quat.FromAxisAngle(0f, 0f, 1f, MathF.PI / 2f);
        var parts = new[]
        {
            new PartDef("Root", Side.Center, PartShape.Box, (0.2f, 0.1f, 0.1f), (0f, 1f, 0f), turned, 1000f, 0.8f, 0f),
            new PartDef("Tip", Side.Center, PartShape.Sphere, (0.05f, 0f, 0f), (0f, 1.5f, 0f), Quat.Identity, 1000f, 0.8f, 0f),
        };
        var joints = new[] { new JointDef("J", Side.Center, JointKind.Spherical, 0, 1, (0f, 1.2f, 0f), (0f, 1f, 0f), -1f, 1f, 0.5f, new MuscleGains(1f, 1f, 1f, 1f)) };
        var plan = new BodyPlan(parts, joints, new[] { new SensePointDef("Probe", Side.Center, 0, (0.5f, 0.2f, 0.1f)) });
        var (bodies, jointCount, _) = Ragdoll.Needs(plan);
        using var world = new Box3DSoaWorld(capacity: bodies, jointCapacity: jointCount);
        var ragdoll = Ragdoll.Build(world, plan, (3f, 0f), firstStableId: 1);

        var x = new float[1]; var y = new float[1]; var z = new float[1];
        ragdoll.ReadSensePoints(x, y, z);
        var error = MathF.Max(MathF.Abs(x[0] - 2.8f), MathF.Max(MathF.Abs(y[0] - 1.5f), MathF.Abs(z[0] - 0.1f)));
        Assert.True(error < SenseTolerance, $"sense point ({x[0]}, {y[0]}, {z[0]}) is {error} from (2.8, 1.5, 0.1)");
    }
}
