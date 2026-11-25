using Lodur;
using Lodur.Body.Ymir;
using Xunit.Abstractions;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>
/// The orient layers on the real body: a ragdoll seated on a stool welded to the pelvis, gravity on, a sound at one side. An Actor whose goal looks
/// at it and wards it off drives the ragdoll through strengths and targets only; the game (this scenario) reads the part poses, owns the world and steps it.
/// </summary>
public sealed class SeatedOrientTests
{
    private const float Dt = 1f / 120f;
    private const uint Thing = 7;
    private const float WardDistance = 0.3f;
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();

    /// <summary>Where the sound sits for each case: well to one side and a little ahead, at head height and beyond the arms reach.</summary>
    private static readonly (float X, float Y, float Z)[] Sounds = { (0.4f, 1.74f, 0.7f), (-0.4f, 1.74f, 0.7f) };

    /// <summary>
    /// Head forward error bound after 2 s, degrees, at the geometric midpoint (deterministic-bound-margin) of the shipped worst, 0.040 deg over the two
    /// sounds, and the least claimed mutant, 15.157 deg (the look blended at half weight): separation 379. Also killed: look not applied, 30.6 deg;
    /// the look aimed at the target mirrored in x, 60.2 deg. Layers off reads 29.9 deg.
    /// </summary>
    private const float LookBoundDegrees = 0.78f;

    /// <summary>
    /// Bound on the mean wrist distance to the head-to-sound line after 2 s, metres, at the geometric midpoint of the shipped worst, 51.67 cm, and the least
    /// claimed mutant, 78.67 cm (the arms layer not applied, which reads the spawn value): separation 1.52. The ward-off wrists sit short of the line under gravity
    /// (follow-up lodur-reach-tracking), so the bound proves they rose toward it, not that they met it. Unclaimed: the wrists sent sideways of the line by the
    /// ward distance instead of along it reads 62.44 and 57.76 cm, separation 1.12 against the shipped worst, which this metric does not separate.
    /// </summary>
    private const float WardBound = 0.6376f;

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

    private static Brain Watching(bool layered) => new()
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
                    Look = layered ? new LookLayer() : null,
                    Arms = layered ? new ArmLayer { Mode = ArmMode.WardOff, Side = ArmSide.Both, Distance = WardDistance, Spread = 0.15f } : null,
                },
            },
        },
    };

    /// <summary>Seats the body, sounds the emitter for two seconds and returns the head forward's error to the sound bearing (radians) and the mean wrist distance to the head-to-sound line, at the end and at the start.</summary>
    private static (float Look, float WardEnd, float WardStart, ulong Target) Run((float X, float Y, float Z) sound, bool layered)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies + 2, jointCapacity: joints + 1);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        var ragdoll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.AddJoint(Box3DSoaJointKind.Weld, stool, ragdoll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));

        var senseHead = Plan.SenseIndex("Head");
        var actor = new Actor(Dt, Plan.SensePoints.Count, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Watching(layered),
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, senseHead,
            new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 }, body: Plan);
        var x = new float[Plan.SensePoints.Count]; var y = new float[x.Length]; var z = new float[x.Length];
        var strength = new float[Plan.Joints.Count];
        var target = new Quat[Plan.Joints.Count];
        var parts = new PartPose[Plan.Parts.Count];
        var emitters = new[] { new Emitter { Channel = Channel.Audio, X = sound.X, Y = sound.Y, Z = sound.Z, Radius = 40f, Intensity = 1.0, Id = 1, Kind = Thing } };
        var head = Plan.PartIndex("Head");
        var wrists = new[] { Plan.JointIndex("Wrist", Side.Left), Plan.JointIndex("Wrist", Side.Right) };

        float WristError()
        {
            var h = parts[head];
            var u = Unit((sound.X - h.X, sound.Y - h.Y, sound.Z - h.Z));
            var sum = 0f;
            foreach (var w in wrists)
            {
                var j = Plan.Joints[w];
                var fore = parts[j.Parent];
                var c = Plan.Parts[j.Parent].Position;
                fore.Rotation.Rotate(j.Anchor.X - c.X, j.Anchor.Y - c.Y, j.Anchor.Z - c.Z, out var ox, out var oy, out var oz);
                var q = (X: fore.X + ox - h.X, Y: fore.Y + oy - h.Y, Z: fore.Z + oz - h.Z);
                var along = q.X * u.X + q.Y * u.Y + q.Z * u.Z;
                sum += MathF.Sqrt(MathF.Max(0f, q.X * q.X + q.Y * q.Y + q.Z * q.Z - along * along));
            }

            return sum / wrists.Length;
        }

        ragdoll.ReadPartPoses(parts);
        var start = WristError();
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
        return (MathF.Acos(cos), WristError(), start, actor.TargetId);
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
    public void A_seated_body_looks_at_the_sound_and_wards_it_off()
    {
        for (var s = 0; s < Sounds.Length; s++)
        {
            var (look, wardEnd, wardStart, target) = Run(Sounds[s], layered: true);
            var (lookOff, wardOff, _, _) = Run(Sounds[s], layered: false);
            _out.WriteLine($"sound {s}: head error {look * 57.2958f:F3} deg (layers off {lookOff * 57.2958f:F3}), wrists to the head-sound line {wardEnd * 100f:F2} cm (start {wardStart * 100f:F2}, layers off {wardOff * 100f:F2}), target {target}");
            Assert.Equal(1UL, target);
            Assert.True(look * 57.2958f < LookBoundDegrees, $"head forward is {look * 57.2958f:F2} deg off the sound, bound {LookBoundDegrees} deg");
            Assert.True(wardEnd < WardBound && wardEnd < wardStart, $"wrists are {wardEnd * 100f:F1} cm from the head-sound line (start {wardStart * 100f:F1}), bound {WardBound * 100f:F1} cm");
        }
    }
}
