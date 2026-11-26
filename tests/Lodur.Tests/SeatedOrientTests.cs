using Lodur;
using Lodur.Body.Ymir;
using Xunit.Abstractions;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>
/// The orient layers on the real body: a ragdoll seated on a stool welded to the pelvis, without and with gravity, a sound at one side. An Actor whose goal looks
/// at it and wards it off drives the ragdoll through strengths and targets only; the game (this scenario) reads the part poses, owns the world and steps it.
/// </summary>
public sealed class SeatedOrientTests
{
    private const float Dt = 1f / 120f;
    private const uint Thing = 7;
    private const float WardDistance = 0.3f;
    private const float Spread = 0.15f;
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();

    /// <summary>Where the sound sits for each case: well to one side and a little ahead, at head height and beyond the arms reach.</summary>
    private static readonly (float X, float Y, float Z)[] Sounds = { (0.4f, 1.74f, 0.7f), (-0.4f, 1.74f, 0.7f) };

    /// <summary>
    /// A lower sound to the left, whose ward goals the near (left) arm reaches with zero shoulder twist. The far arm's goal crosses the midline and the
    /// straight wrist path to it touches the trunk, so the far arm follows the hand path through the clearance via point (ruling
    /// lodur:ruling:reach-clears-body-by-via-point): forward first, then across.
    /// </summary>
    private static readonly (float X, float Y, float Z) LowSound = (0.4f, 1.2f, 0.9f);

    /// <summary>
    /// Without gravity, bound on the far (right) wrist's distance to its ward goal at the low sound after 2 s, metres: the geometric midpoint of the shipped
    /// 6.97 cm and the least claimed mutant, 34.92 cm (the via point at the trunk front, clearance 0). Also killed: the via point's x at the midpoint of wrist
    /// and goal, 39.31 cm; every reach solved directly (blocked forced false), 44.90 cm; the wedge before the path, 44.6 cm.
    /// </summary>
    private const float FarArmBound = 0.1560f;

    /// <summary>
    /// Head forward error bound after 2 s, degrees, set at the geometric midpoint (deterministic-bound-margin) of the shipped worst then, 1.030 deg, and the
    /// least claimed mutant, 15.233 deg (the look blended at half weight), and not loosened since: the shipped worst is now 1.946 deg over the two sounds under gravity (1.080 without it; the
    /// feedforward lifts the head's look to 1.946 from 0.814), the half-weight mutant 15.729 deg. Layers off reads 29.7 deg.
    /// </summary>
    private const float LookBoundDegrees = 3.961f;

    /// <summary>
    /// Without gravity, bound on each wrist's distance to its own ward goal after 2 s at the head-height sounds, metres: the geometric midpoint of the shipped
    /// worst, 8.11 cm (the arm that the other sound's side blocks, on its via path), and the least claimed mutant, 11.45 cm (the via point's x at the midpoint).
    /// Also killed: no spread, 13.25 cm; the wrists sent sideways of the head by the ward distance, 35.02 cm; the arms blended at half weight, 46.09 cm. Layers off reads the start,
    /// 81.3 and 83.5 cm. Not re-measured: spread halved and offset, and the clamped zero-twist swivel (64.94 cm before the hand path), each far above the bound.
    /// </summary>
    private const float WardBound = 0.0964f;

    /// <summary>
    /// Without gravity, bound on the near (left) wrist's distance to its ward goal at the low sound, the geometric midpoint of the shipped worst, 4.34 cm,
    /// and the least claimed mutant, 7.65 cm (the via point's x at the midpoint). Also killed: the arms at half weight, 36.48 cm; sideways, 25.90 cm; Not killed: no spread reads
    /// 2.95 cm, the look at half weight 5.01 cm, every reach solved directly 5.19 cm.
    /// </summary>
    private const float NearWardBound = 0.0576f;

    /// <summary>
    /// Under gravity, the head-height bound on each wrist: the geometric midpoint of the shipped worst, 7.03 cm, and the least claimed mutant, 9.81 cm (the arm's path never
    /// blocked by the trunk: 9.81 and 3.76 cm): 8.30 cm, margin 1.18 each side, separation 1.40. Also killed, re-measured under the subtree-external gate (ruling
    /// gravity-support-subtree-external): the gravity feedforward off, 18.68 cm; no spread, 21.21 cm; the via point's x at the midpoint, 26.77 cm; the wrists sent sideways of
    /// the head, 34.25 cm; the via point at the trunk front with clearance 0, 34.30 cm (15.52 at 63444f0, where the count gate left the arm lighter); the arms at half weight, 52.17 cm.
    /// Not killed: every reach solved directly, 7.20 cm at 63444f0, not re-measured. The ward is not limited by gravity or by the gate: zero gravity reads 8.11 cm, the count gate
    /// 6.00, no gate 7.59, the subtree-external gate 7.03, so the reading rests on the planner's own residual. At 63444f0 the bound was 9.65 cm (shipped worst 6.00, least claimed
    /// mutant 15.52); the shipped reading rose to 7.03 and the path-never-blocked mutant, 9.81, is separated from it by 1.40, so it is claimed and the bound fell. The bound at e47e3d3, before the feedforward, was 21.78 cm (shipped worst 18.68, least claimed mutant
    /// 25.41). Not re-measured: spread halved and offset, and the clamped zero-twist swivel.
    /// </summary>
    private const float GravityWardBound = 0.0830f;

    private readonly ITestOutputHelper _out;

    public SeatedOrientTests(ITestOutputHelper output) => _out = output;

    private static float Dist((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static (float X, float Y, float Z) Unit((float X, float Y, float Z) v)
    {
        var l = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return (v.X / l, v.Y / l, v.Z / l);
    }

    private static Quat[] Held()
    {
        var pose = new Quat[Plan.Joints.Count];
        Array.Fill(pose, Quat.Identity);
        return pose;
    }

    private static Brain Watching() => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "sit", Priority = 0.0, Behaviour = new PoseBehaviour { Keys = new[] { Held() }, FlinchPose = Held() } },
            new GoalSpec
            {
                Id = "watch", Priority = 1.0, Target = new TargetFilter { Kinds = new[] { Thing } },
                Considerations = new[] { new Consideration(ConsiderationInput.Distance, 0.0, 60.0, 1.0, 0.0) },
                Behaviour = new PoseBehaviour
                {
                    Keys = new[] { Held() }, FlinchPose = Held(),
                    Look = new LookLayer(),
                    Arms = new ArmLayer { Mode = ArmMode.WardOff, Side = ArmSide.Both, Distance = WardDistance, Spread = Spread },
                },
            },
        },
    };

    /// <summary>Seats the body, sounds the emitter for two seconds and returns the head forward's error to the sound bearing (radians) and each wrist's distance to its ward goal at the end and at the start.</summary>
    private static (float Look, float Left, float Right, float LeftStart, float RightStart, ulong Target) Run((float X, float Y, float Z) sound, float gravity)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies + 2, gravityY: gravity, jointCapacity: joints + 1);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.AddJoint(Box3DSoaJointKind.Weld, stool, ragdoll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

        var senseHead = Plan.SenseIndex("Head");
        var actor = new Actor(Dt, Plan.SensePoints.Count, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Watching(),
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, senseHead,
            new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 }, body: Plan);
        var x = new float[Plan.SensePoints.Count]; var y = new float[x.Length]; var z = new float[x.Length];
        var strength = new float[Plan.Joints.Count];
        var target = new Quat[Plan.Joints.Count];
        var parts = new PartPose[Plan.Parts.Count];
        var emitters = new[] { new Emitter { Channel = Channel.Audio, X = sound.X, Y = sound.Y, Z = sound.Z, Radius = 40f, Intensity = 1.0, Id = 1, Kind = Thing } };
        var head = Plan.PartIndex("Head");
        var wrists = new[] { Plan.JointIndex("Wrist", Side.Left), Plan.JointIndex("Wrist", Side.Right) };

        var torso = Plan.PartIndex("Torso");

        // Each wrist's distance to its own ward-off goal: the head centre plus Distance toward the sound plus Spread along the torso's left
        // axis, the left wrist to the left. The goal is rebuilt from the measured head and torso, as the layer builds it.
        float WristError(int k)
        {
            var h = parts[head];
            var u = Unit((sound.X - h.X, sound.Y - h.Y, sound.Z - h.Z));
            parts[torso].Rotation.Rotate(1f, 0f, 0f, out var lx, out var ly, out var lz);
            var across = k == 0 ? Spread : -Spread;
            var goal = (h.X + u.X * WardDistance + lx * across, h.Y + u.Y * WardDistance + ly * across, h.Z + u.Z * WardDistance + lz * across);
            var j = Plan.Joints[wrists[k]];
            var fore = parts[j.Parent];
            var c = Plan.Parts[j.Parent].Position;
            fore.Rotation.Rotate(j.Anchor.X - c.X, j.Anchor.Y - c.Y, j.Anchor.Z - c.Z, out var ox, out var oy, out var oz);
            return Dist((fore.X + ox, fore.Y + oy, fore.Z + oz), goal);
        }

        ragdoll.ReadPartPoses(parts);
        var (leftStart, rightStart) = (WristError(0), WristError(1));
        for (var i = 0; i < (int)MathF.Round(2f / Dt); i++)
        {
            ragdoll.ReadSensePoints(x, y, z);
            ragdoll.ReadPartPoses(parts);
            actor.Tick(x, y, z, Array.Empty<BoxStimulus>(), strength, target, emitters, parts: parts);
            ragdoll.Drive(strength, target);
            world.Step(Dt, 4);
        }

        ragdoll.ReadPartPoses(parts);
        var hp = parts[head];
        hp.Rotation.Rotate(0f, 0f, 1f, out var fx, out var fy, out var fz);
        var bearing = Unit((sound.X - hp.X, sound.Y - hp.Y, sound.Z - hp.Z));
        var cos = Math.Clamp(fx * bearing.X + fy * bearing.Y + fz * bearing.Z, -1f, 1f);
        return (MathF.Acos(cos), WristError(0), WristError(1), leftStart, rightStart, actor.TargetId);
    }

    [Fact]
    public void ReadPartPoses_reports_each_part_at_its_creation_pose_and_allocates_nothing()
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies, jointCapacity: joints);
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var poses = new PartPose[Plan.Parts.Count];
        ragdoll.ReadPartPoses(poses);
        for (var p = 0; p < poses.Length; p++)
        {
            var d = Plan.Parts[p];
            Assert.Equal(d.Position.X, poses[p].X, 3);
            Assert.Equal(d.Position.Y, poses[p].Y, 3);
            Assert.Equal(d.Position.Z, poses[p].Z, 3);
            var dot = MathF.Abs(d.Rotation.X * poses[p].Rotation.X + d.Rotation.Y * poses[p].Rotation.Y + d.Rotation.Z * poses[p].Rotation.Z + d.Rotation.W * poses[p].Rotation.W);
            Assert.True(dot > 1f - 1e-5f, $"part {d.Label} {d.Side} rotation {poses[p].Rotation} against {d.Rotation}");
        }

        Assert.Throws<ArgumentException>(() => ragdoll.ReadPartPoses(new PartPose[poses.Length - 1]));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) ragdoll.ReadPartPoses(poses);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Without_gravity_a_seated_body_looks_at_the_sound_and_its_wrists_meet_the_ward_goals()
    {
        for (var s = 0; s < Sounds.Length; s++)
        {
            var r = Run(Sounds[s], gravity: 0f);
            _out.WriteLine($"sound {s}: head error {r.Look * 57.2958f:F3} deg, wrists to their goals {r.Left * 100f:F2} and {r.Right * 100f:F2} cm (start {r.LeftStart * 100f:F2} and {r.RightStart * 100f:F2}), target {r.Target}");
            Assert.Equal(1UL, r.Target);
            Assert.True(r.Look * 57.2958f < LookBoundDegrees, $"head forward is {r.Look * 57.2958f:F2} deg off the sound, bound {LookBoundDegrees} deg");
            Assert.True(r.Left < WardBound && r.Right < WardBound, $"wrists are {r.Left * 100f:F1} and {r.Right * 100f:F1} cm from their goals, bound {WardBound * 100f:F1} cm");
        }

        var low = Run(LowSound, gravity: 0f);
        _out.WriteLine($"low sound: wrists to their goals {low.Left * 100f:F2} and {low.Right * 100f:F2} cm");
        Assert.True(low.Left < NearWardBound, $"the near wrist is {low.Left * 100f:F1} cm from its goal, bound {NearWardBound * 100f:F1} cm");
        Assert.True(low.Right < FarArmBound, $"the far wrist is {low.Right * 100f:F1} cm from its goal, bound {FarArmBound * 100f:F1} cm");
    }

    [Fact]
    public void Under_gravity_a_seated_body_looks_at_the_sound_and_its_wrists_rise_toward_the_ward_goals()
    {
        for (var s = 0; s < Sounds.Length; s++)
        {
            var r = Run(Sounds[s], gravity: -10f);
            _out.WriteLine($"sound {s}: head error {r.Look * 57.2958f:F3} deg, wrists to their goals {r.Left * 100f:F2} and {r.Right * 100f:F2} cm (start {r.LeftStart * 100f:F2} and {r.RightStart * 100f:F2}), target {r.Target}");
            Assert.Equal(1UL, r.Target);
            Assert.True(r.Look * 57.2958f < LookBoundDegrees, $"head forward is {r.Look * 57.2958f:F2} deg off the sound, bound {LookBoundDegrees} deg");
            Assert.True(r.Left < GravityWardBound && r.Right < GravityWardBound, $"wrists are {r.Left * 100f:F1} and {r.Right * 100f:F1} cm from their goals, bound {GravityWardBound * 100f:F1} cm");
        }
    }
}
