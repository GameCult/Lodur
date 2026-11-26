using Lodur;
using Lodur.Body.Ymir;
using Xunit.Abstractions;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>The IK solve against the physical ragdoll: Ymir's joint frame is the one Ik.JointFrame mirrors, and a solved arm lands the wrist.</summary>
public sealed class IkReachTests
{
    private const float Dt = 1f / 120f;
    private const float Seconds = 4f;
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();

    // Shoulder plus these offsets (body fact 9).
    private static readonly (float X, float Y, float Z)[] Targets = { (0f, -0.25f, 0.35f), (-0.15f, -0.3f, 0.25f), (0f, 0.05f, 0.45f) };

    private readonly ITestOutputHelper _out;

    public IkReachTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Wrist error bound at the geometric midpoint (deterministic-bound-margin) of the shipped worst, 2.98 cm over the three free-floating targets
    /// (probe 3.0, 2.3, 1.0 cm), and the least claimed mutant, 4.78 cm (the parent rotation conjugate dropped in the creation-frame transform): margin 1.27 each side.
    /// Also killed: swing order flipped, 5.29 cm (5.22 before the arm held its swivel across ticks); the joint frame applied the wrong way round,
    /// 5.58 cm. A flipped elbow flex reads 33 cm.
    /// Twist is not this metric's mutant (the arm tracks twist weakly); the Core twist test claims it.
    /// </summary>
    private const float FreeBound = 0.0377f;

    /// <summary>Runs the right arm toward shoulder + offset for four seconds and returns the wrist error, the final state hash and the torso drift.</summary>
    private static (float Error, ulong Hash) Reach((float X, float Y, float Z) offset, float gravity, bool weldPelvis, float strengthValue = 1f)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies + (weldPelvis ? 1 : 0), gravityY: gravity, jointCapacity: joints + (weldPelvis ? 1 : 0));
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        if (weldPelvis)
        {
            var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
            world.AddJoint(Box3DSoaJointKind.Weld, stool, ragdoll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));
        }

        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var strength = new float[Plan.Joints.Count];
        var target = new Quat[Plan.Joints.Count];
        Array.Fill(strength, strengthValue);
        Array.Fill(target, Quat.Identity);
        var goal = (X: arm.ShoulderAnchor.X + offset.X, Y: arm.ShoulderAnchor.Y + offset.Y, Z: arm.ShoulderAnchor.Z + offset.Z);
        var torso = ragdoll.BodyLane(arm.ParentPart);
        var v = world.View;
        for (var i = 0; i < (int)MathF.Round(Seconds / Dt); i++)
        {
            var rotation = new Quat(v.RotationX[torso], v.RotationY[torso], v.RotationZ[torso], v.RotationW[torso]);
            arm.Solve(rotation, v.PositionX[torso], v.PositionY[torso], v.PositionZ[torso], goal.X, goal.Y, goal.Z, out target[arm.ShoulderJoint], out target[arm.ElbowJoint]);
            ragdoll.Drive(strength, target);
            world.Step(Dt, 4);
        }

        // The wrist anchor: the forearm body's position plus its rotation applied to the anchor's creation-pose offset.
        var wristJoint = Plan.Joints[Plan.JointIndex("Wrist", Side.Right)];
        var fore = ragdoll.BodyLane(wristJoint.Parent);
        var centre = Plan.Parts[wristJoint.Parent].Position;
        new Quat(v.RotationX[fore], v.RotationY[fore], v.RotationZ[fore], v.RotationW[fore])
            .Rotate(wristJoint.Anchor.X - centre.X, wristJoint.Anchor.Y - centre.Y, wristJoint.Anchor.Z - centre.Z, out var ox, out var oy, out var oz);
        var dx = v.PositionX[fore] + ox - goal.X;
        var dy = v.PositionY[fore] + oy - goal.Y;
        var dz = v.PositionZ[fore] + oz - goal.Z;
        return (MathF.Sqrt(dx * dx + dy * dy + dz * dz), RagdollScenario.HashOf(world));
    }

    [Fact]
    public void Core_joint_frames_match_Ymir()
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies, jointCapacity: joints);
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var v = world.View;
        Assert.Equal(18, Plan.Joints.Count);
        for (var j = 0; j < Plan.Joints.Count; j++)
        {
            var lane = ragdoll.JointLane(j);
            var ymir = new Quat(v.JointFrameX[lane], v.JointFrameY[lane], v.JointFrameZ[lane], v.JointFrameW[lane]);
            var core = Ik.JointFrame(Plan.Joints[j]);
            var dot = MathF.Abs(core.X * ymir.X + core.Y * ymir.Y + core.Z * ymir.Z + core.W * ymir.W);
            Assert.True(dot > 1f - 1e-5f, $"joint {Plan.Joints[j].Label} {Plan.Joints[j].Side}: Core frame {core} against Ymir {ymir}");
        }
    }

    [Fact]
    public void Zero_gravity_reach_lands_the_wrist()
    {
        // State hashes recorded at origin/main e47e3d3, before the gravity feedforward: in zero gravity Drive writes the PD torque only, so they never move.
        var recorded = new ulong[] { 2780975657960733482ul, 1878696821643337211ul, 486659158273771449ul };
        var worst = 0f;
        for (var k = 0; k < Targets.Length; k++)
        {
            var offset = Targets[k];
            var (error, hash) = Reach(offset, gravity: 0f, weldPelvis: false);
            _out.WriteLine($"zero gravity, offset {offset}: wrist error {error * 100f:F2} cm, hash {hash}");
            Assert.Equal(recorded[k], hash);
            worst = MathF.Max(worst, error);
            Assert.True(error < FreeBound, $"wrist error {error * 100f:F2} cm at {offset} exceeds {FreeBound * 100f:F1} cm");
        }

        _out.WriteLine($"shipped worst {worst * 100f:F2} cm, bound {FreeBound * 100f:F1} cm");
    }

    /// <summary>
    /// Gravity reach bound at the geometric midpoint (deterministic-bound-margin) of the shipped worst, 0.77 cm over the three targets with the pelvis welded (0.77, 0.16, 0.72),
    /// and the least claimed mutant, 3.97 cm (the count gate, any touch carries the body's weight at no joint: 3.01, 3.16, 3.97): 1.75 cm, margin 2.27 each side, separation 5.2.
    /// Also killed: a deposit at the touched body instead of the common ancestor, 5.84 cm; the feedforward off, 24.01 cm (13.69, 10.16, 24.01). Not claimed here: no gate at all reads
    /// 0.89 cm and a touch outside the ragdoll ignored 0.77 cm, 1.16 and 1.00 times the shipped worst; the hand-rest tests in HandTests kill both. The controller no longer sags: zero
    /// gravity reads 0.89 cm. Only a touch outside a joint's subtree supports it (ruling gravity-support-subtree-external), so the right hand's thumb on its fingers no longer lifts the arm's load.
    /// At 63444f0 this bound was 9.76 cm, pinned to the count gate's own self-contact cost.
    /// </summary>
    private const float GravityBound = 0.0175f;

    [Fact]
    public void Gravity_reach_lands_the_wrist_at_full_strength()
    {
        var worst = 0f;
        foreach (var offset in Targets)
        {
            var (error, _) = Reach(offset, gravity: -10f, weldPelvis: true);
            _out.WriteLine($"gravity on, pelvis welded, offset {offset}: wrist error {error * 100f:F2} cm");
            worst = MathF.Max(worst, error);
            Assert.True(error < GravityBound, $"wrist error {error * 100f:F2} cm at {offset} exceeds {GravityBound * 100f:F2} cm");
        }

        _out.WriteLine($"shipped worst {worst * 100f:F2} cm, bound {GravityBound * 100f:F2} cm");
    }

    /// <summary>
    /// The least a half-strength arm sags at any target, at the geometric midpoint of the shipped least (13.85 cm; 19.00, 13.85, 22.79) and the least claimed mutant (0.88 cm:
    /// the feedforward without its strength factor, which holds the weight up at half strength; 0.88, 0.89, 12.78): 3.49 cm, margin 3.97 each side. A weak will sags (strength 0.5 is
    /// weakness, not the controller), and the feedforward off reads 22.45, 17.18, 38.77 there. At 63444f0 the bound was 4.43 cm (shipped least 13.38, mutant 1.47).
    /// </summary>
    private const float WeakSag = 0.0349f;

    [Fact]
    public void A_half_strength_arm_still_sags_under_gravity_at_every_target()
    {
        foreach (var offset in Targets)
        {
            var (error, _) = Reach(offset, gravity: -10f, weldPelvis: true, strengthValue: 0.5f);
            _out.WriteLine($"gravity on, pelvis welded, half strength, offset {offset}: wrist error {error * 100f:F2} cm");
            Assert.True(error > WeakSag, $"half strength landed within {error * 100f:F2} cm at {offset}, under {WeakSag * 100f:F2} cm: weakness did not sag");
        }
    }

    [Fact]
    public void Reach_is_deterministic()
    {
        var a = Reach(Targets[0], gravity: 0f, weldPelvis: false);
        var b = Reach(Targets[0], gravity: 0f, weldPelvis: false);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.Error, b.Error);
    }
}
