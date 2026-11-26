using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>The airway openings the plan carries become liquid sensors in a particle world, and Ragdoll.ReadAirway reads live and pooled volume per opening.</summary>
public sealed class AirwayTests
{
    private const float Dt = 1f / 120f;
    private const float R = 0.0125f;
    private const float DropVolume = (2f * R) * (2f * R) * (2f * R);
    private static readonly uint FluidPhase = Box3DSoaPhase.Of(0, fluid: true);

    // Bounds, from deterministic-bound-margin (readings are identical run to run), read as max(signed drop, 0) as AirwaySample.Of floors them. Side-lying falls (12 seeds): shipped worst drop 0.01323 m,
    // least claimed mutant (the opening's offset rotated about z only) 0.00914 m, separation 1.45: bound sqrt(0.01323 x 0.00914) = 0.0110 m. An opening read from lane 0, a sump read from lane 0, the rim subtracted, the sump offset unrotated or its lane's rotation ignored, the pharynx sump moved and the rim read as a cosine
    // (0.00117) are further off; most read 0 on some seed. The zero-reading convention was not needed: the least claimed mutant reads above 0.
    private const float SideDropBound = 0.0110f;

    // Upright (creation pose -0.04671, welded stool at full strength -0.04615 m): clearance below the sump, shipped worst 0.04615 m, least claimed mutant (the glottis sump 1 cm shallower) 0.03615 m, separation 1.28:
    // bound sqrt(0.04615 x 0.03615) = 0.0408 m, so a reading below -0.0408. The 2 cm shallower sump (0.02615) and the mutants reading at or above 0 are further off; the zero-reading convention was not needed.
    private const float UprightBound = -0.0408f;

    // Limp seated falls (40 seeds): shipped 31 drain, the pelvis-lane mutant 0 (counted as 1): bound sqrt(31 x 1) = 5.6, so at least 6 drain.
    private const int SeatedDraining = 6;

    private static readonly BodyPlan Plan = BodyPlans.Humanoid();
    private static readonly int Mouth = Plan.AirwayIndex("Mouth");
    private static readonly int Nose = Plan.AirwayIndex("Nose");

    private static Box3DSoaWorld ParticleWorld(int sensors, float gravityZ = 0f)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        return new Box3DSoaWorld(bodies, gravityY: 0f, gravityZ: gravityZ, jointCapacity: joints,
            particles: new Box3DSoaParticleCapacities(16, 16, 16, 16, 64, 2, 16, 8, sensors));
    }

    /// <summary>World position 1.3 cm beyond an opening along its offset direction, from the head's creation pose (no step has run).</summary>
    private static (float X, float Y, float Z) Outside(Box3DSoaWorld world, Ragdoll doll, int opening, float beyond)
    {
        var a = Plan.AirwayOpenings[opening];
        var lane = doll.BodyLane(a.Part);
        var v = world.View;
        var o = a.Offset;
        var len = MathF.Sqrt(o.X * o.X + o.Y * o.Y + o.Z * o.Z);
        var k = (len + beyond) / len;
        return (v.PositionX[lane] + o.X * k, v.PositionY[lane] + o.Y * k, v.PositionZ[lane] + o.Z * k);
    }

    private static void Drop(Box3DSoaWorld world, (float X, float Y, float Z) at) =>
        world.CreateParticles([new Box3DSoaParticleDesc(at, 1000f * DropVolume, R, FluidPhase)]);

    private static (float[] Live, float[] Pooled) Read(Ragdoll doll)
    {
        var live = new float[Plan.AirwayOpenings.Count];
        var pooled = new float[Plan.AirwayOpenings.Count];
        doll.ReadAirway(live, pooled, new float[Plan.AirwayOpenings.Count], (0f, -1f, 0f));
        return (live, pooled);
    }

    private static float[] DropOf(Ragdoll doll, (float X, float Y, float Z) down)
    {
        var drop = new float[Plan.AirwayOpenings.Count];
        doll.ReadAirway(new float[drop.Length], new float[drop.Length], drop, down);
        return drop;
    }

    [Fact]
    public void A_drop_held_at_the_mouth_reads_at_the_mouth_only()
    {
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        Drop(world, Outside(world, doll, Mouth, 0.013f));
        world.Step(Dt, 4);
        var (live, pooled) = Read(doll);
        Assert.Equal(DropVolume, live[Mouth], 9);
        Assert.Equal(0f, live[Nose]);
        Assert.Equal(0f, pooled[Mouth]);
        Assert.Equal(0f, pooled[Nose]);
    }

    [Fact]
    public void The_same_drop_at_the_nose_reads_at_the_nose_only_and_behind_the_head_reads_nothing()
    {
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        Drop(world, Outside(world, doll, Nose, 0.013f));
        world.Step(Dt, 4);
        var (live, pooled) = Read(doll);
        Assert.Equal(DropVolume, live[Nose], 9);
        Assert.Equal(0f, live[Mouth]);
        Assert.Equal(0f, pooled[Nose] + pooled[Mouth]);

        using var behindWorld = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var behind = Ragdoll.Build(behindWorld, Plan, (0f, 0f), firstStableId: 100);
        var head = behind.BodyLane(Plan.PartIndex("Head"));
        var h = behindWorld.View;
        Drop(behindWorld, (h.PositionX[head], h.PositionY[head], h.PositionZ[head] - 0.16f)); // 5 cm behind the back of the head
        behindWorld.Step(Dt, 4);
        var (l2, p2) = Read(behind);
        Assert.Equal(0f, l2[Mouth] + l2[Nose] + p2[Mouth] + p2[Nose]);
    }

    [Fact]
    public void A_drop_settled_on_the_face_at_the_mouth_reads_pooled_at_the_mouth()
    {
        // Gravity pulls toward the face (-z), so the drop presses on the head; the whole body falls with it.
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors, gravityZ: -1f);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var at = Outside(world, doll, Mouth, R);
        world.CreateParticles([new Box3DSoaParticleDesc(at, 1000f * DropVolume, R, FluidPhase)]);
        var head = doll.BodyLane(Plan.PartIndex("Head"));
        var supported = false;
        for (var i = 0; i < 60 && !supported; i++)
        {
            world.Step(Dt, 4);
            supported = world.View.SupportBody[0] == head;
        }

        Assert.True(supported, "the drop never rested against the head");
        world.Settle([new Box3DSoaParticleHandle(0, world.View.ParticleGeneration[0])]);
        world.Step(Dt, 4);
        var (live, pooled) = Read(doll);
        Assert.Equal(DropVolume, pooled[Mouth], 9);
        Assert.Equal(0f, live[Mouth]);
        Assert.Equal(0f, pooled[Nose]);
    }

    [Fact]
    public void A_world_with_no_particle_system_builds_the_humanoid_and_reads_zeros()
    {
        var (bodies, joints, sensors) = Ragdoll.Needs(Plan);
        Assert.Equal(2, sensors);
        using var world = new Box3DSoaWorld(bodies, jointCapacity: joints);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.Step(Dt, 4);
        var live = new[] { 7f, 7f };
        var pooled = new[] { 7f, 7f };
        var drop = new[] { 7f, 7f };
        doll.ReadAirway(live, pooled, drop, (0f, 0f, 1f));
        Assert.Equal(new[] { 0f, 0f }, live);
        Assert.Equal(new[] { 0f, 0f }, pooled);
        Assert.Equal(0.0716f, drop[Mouth], 0.002f);
        Assert.Equal(0.0718f, drop[Nose], 0.002f);
    }

    /// <summary>The drop at the creation pose for each down, metres: Mouth, Nose.</summary>
    [Theory]
    [InlineData(0f, -1f, 0f, -0.0467f, -0.0951f)] // upright: the glottis is the deepest sump, both rims above it
    [InlineData(0f, 0f, -1f, -0.0734f, -0.0872f)] // on his back: the oropharynx is the deepest, the lips above it
    [InlineData(0f, 0f, 1f, 0.0716f, 0.0718f)]    // face down
    [InlineData(1f, 0f, 0f, 0.0250f, 0.0250f)]    // on his side: the rim, the half-width, lies below the sumps on the midline
    [InlineData(-1f, 0f, 0f, 0.0250f, 0.0250f)]
    [InlineData(0f, 1f, 0f, 0.0433f, 0.0949f)]    // inverted
    public void Drop_at_the_creation_pose_follows_down(float x, float y, float z, float mouth, float nose)
    {
        // Each within 0.002 m. Kills: the offset ignored (nose and mouth swap), the rim ignored (the side poses read 0), the rim taken as a sphere (face down reads 0.094),
        // the shallowest sump (upright reads +0.005), and a drop cosine restored (the side poses read 0).
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var drop = DropOf(doll, (x, y, z));
        Assert.Equal(mouth, drop[Mouth], 0.002f);
        Assert.Equal(nose, drop[Nose], 0.002f);
    }

    [Fact]
    public void The_length_of_down_does_not_matter()
    {
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var unit = DropOf(doll, (0f, -1f, 0f));
        var weight = DropOf(doll, (0f, -9.81f, 0f));
        var side = DropOf(doll, (1f, 0f, 0f));
        var sideLong = DropOf(doll, (3f, 0f, 0f));
        for (var k = 0; k < unit.Length; k++)
        {
            Assert.Equal(unit[k], weight[k], 1e-6f);
            Assert.Equal(side[k], sideLong[k], 1e-6f);
        }
    }

    /// <summary>The humanoid on a ground slab, limp, shoved by a seeded torque on pelvis and torso for six steps, then six seconds: the largest drop over its openings and the torso's face direction y.</summary>
    private static (float Drop, float TorsoFaceY) Fall(ulong seed)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies + 1, jointCapacity: joints);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var strength = new float[Plan.Joints.Count];
        var target = new Quat[Plan.Joints.Count];
        Array.Fill(target, Quat.Identity);
        var v = world.View;
        var rng = new SplitMix64(seed);
        for (var s = 0; s < 120 * 6; s++)
        {
            if (s < 6)
            {
                foreach (var part in new[] { "Pelvis", "Torso" })
                {
                    var i = doll.BodyLane(Plan.PartIndex(part));
                    v.TorqueX[i] = 200f * rng.NextSigned();
                    v.TorqueY[i] = 200f * rng.NextSigned();
                    v.TorqueZ[i] = 200f * rng.NextSigned();
                }
            }

            doll.Drive(strength, target);
            world.Step(Dt, 4);
        }

        var t = doll.BodyLane(Plan.PartIndex("Torso"));
        new Quat(v.RotationX[t], v.RotationY[t], v.RotationZ[t], v.RotationW[t]).Rotate(0f, 0f, 1f, out _, out var faceY, out _);
        var drop = DropOf(doll, (0f, -1f, 0f));
        return (MathF.Max(drop[Mouth], drop[Nose]), faceY);
    }

    /// <summary>The humanoid welded to a stool (the SeatedOrientTests rig): held at the given strength for three seconds, or limp (strength 0) after a seeded torso shove for six: the largest drop over its openings.</summary>
    private static float Seated(float strength, ulong seed)
    {
        var (bodies, joints, _) = Ragdoll.Needs(Plan);
        using var world = new Box3DSoaWorld(capacity: bodies + 2, jointCapacity: joints + 1);
        world.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        var stool = world.AddStaticBox(2, (0f, 1.11f, -0.115f), (0.05f, 0.05f, 0.025f), friction: 0.8f);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.AddJoint(Box3DSoaJointKind.Weld, stool, doll.BodyLane(Plan.PartIndex("Pelvis")), (0f, 1.11f, -0.09f), (0f, 1f, 0f));
        var held = new float[Plan.Joints.Count];
        Array.Fill(held, strength);
        var target = new Quat[Plan.Joints.Count];
        Array.Fill(target, Quat.Identity);
        var v = world.View;
        var rng = new SplitMix64(seed);
        var steps = 120 * (strength > 0f ? 3 : 6);
        for (var s = 0; s < steps; s++)
        {
            if (strength == 0f && s < 6)
            {
                var i = doll.BodyLane(Plan.PartIndex("Torso"));
                v.TorqueX[i] = 60f * rng.NextSigned();
                v.TorqueY[i] = 60f * rng.NextSigned();
                v.TorqueZ[i] = 60f * rng.NextSigned();
            }

            doll.Drive(held, target);
            world.Step(Dt, 4);
        }

        var drop = DropOf(doll, (0f, -1f, 0f));
        return MathF.Max(drop[Mouth], drop[Nose]);
    }

    [Fact]
    public void Drop_follows_the_heads_rotation()
    {
        // One box head created turned 90 degrees about x (its +z face along -y), a sump at its centre: the opening's centre and normal come from the lane's rotation, not the plan's offsets.
        // Down (0,-1,0): the mouth's centre lies 0.1025 m below the sump and its rim adds 0.0091 (0.0233 x the sine of the angle
        // between down and its normal), the nose 0.1095 and 0.0023; unrotated the mouth would read 0.04 + 0.0233 less the sump. Down (0,0,1): the mouth's centre 0.04 above the sump and its rim 0.0233
        // below it, -0.0167. Kills the rotation ignored (the part's identity pose).
        var head = new PartDef("Head", Side.Center, PartShape.Box, (0.1f, 0.1f, 0.1f), (0f, 1f, 0f), Quat.FromAxisAngle(1f, 0f, 0f, MathF.PI / 2f), 1000f, 0.5f, 0f);
        var turned = new BodyPlan([head], [], [], [.. Plan.AirwayOpenings.Select(o => o with { Part = 0 })], [new AirwaySumpDef("Sump", 0, (0f, 0f, 0f))]);
        var (bodies, joints, _) = Ragdoll.Needs(turned);
        using var world = new Box3DSoaWorld(bodies, gravityY: 0f, jointCapacity: joints);
        var doll = Ragdoll.Build(world, turned, (0f, 0f), firstStableId: 100);
        var drop = new float[2];
        doll.ReadAirway(new float[2], new float[2], drop, (0f, -1f, 0f));
        Assert.Equal(0.1116f, drop[Mouth], 0.002f);
        Assert.Equal(0.1118f, drop[Nose], 0.002f);
        doll.ReadAirway(new float[2], new float[2], drop, (0f, 0f, 1f));
        Assert.Equal(-0.0167f, drop[Mouth], 0.002f);
    }

    [Fact]
    public void A_side_lying_limp_humanoid_drains()
    {
        // The twelve falls of the 240-seed sweep that end on the side (torso face y within 0.3 of level) each read a drop above the bound. Kills the opening's offset rotated about z only (least claimed, 0.00914) and the
        // mutants further off (a lane read from the pelvis reads 0 on 5 of the 12: the pelvis stays upright).
        foreach (var seed in new ulong[] { 6, 10, 11, 18, 39, 66, 92, 120, 133, 151, 171, 208 })
        {
            var (drop, faceY) = Fall(seed);
            Assert.InRange(faceY, -0.3f, 0.3f);
            Assert.True(drop > SideDropBound, $"seed {seed}: drop {drop} (face y {faceY})");
        }
    }

    [Fact]
    public void Upright_drains_nothing()
    {
        // The creation pose and the humanoid welded to a stool at full strength: both rims stay above the glottis (readings -0.047 and -0.046).
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        var create = DropOf(doll, (0f, -1f, 0f));
        var seated = Seated(1f, 1);
        Assert.True(MathF.Max(create[Mouth], create[Nose]) < UprightBound, $"creation {create[Mouth]} {create[Nose]}");
        Assert.True(seated < UprightBound, $"seated {seated}");
    }

    [Fact]
    public void A_limp_seated_humanoid_whose_head_hangs_drains()
    {
        var draining = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            if (Seated(0f, seed) > 0f) draining++;
        }

        Assert.True(draining >= SeatedDraining, $"{draining} of 40 drain");
    }

    [Fact]
    public void A_particle_world_with_one_sensor_lane_refuses_the_humanoid_at_build()
    {
        using var world = ParticleWorld(sensors: 1);
        Assert.ThrowsAny<Exception>(() => Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100));
    }

    [Fact]
    public void ReadAirway_allocates_nothing_and_refuses_spans_not_one_per_opening()
    {
        using var world = ParticleWorld(Ragdoll.Needs(Plan).Sensors);
        var doll = Ragdoll.Build(world, Plan, (0f, 0f), firstStableId: 100);
        world.Step(Dt, 4);
        var live = new float[2];
        var pooled = new float[2];
        var drop = new float[2];
        var down = (0f, -1f, 0f);
        doll.ReadAirway(live, pooled, drop, down);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) doll.ReadAirway(live, pooled, drop, down);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(new float[1], pooled, drop, down));
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(live, new float[3], drop, down));
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(live, pooled, new float[1], down));
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(live, pooled, drop, (0f, 0f, 0f)));
    }
}
