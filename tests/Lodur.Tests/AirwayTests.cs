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
        doll.ReadAirway(live, pooled);
        return (live, pooled);
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
        doll.ReadAirway(live, pooled);
        Assert.Equal(new[] { 0f, 0f }, live);
        Assert.Equal(new[] { 0f, 0f }, pooled);
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
        doll.ReadAirway(live, pooled);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) doll.ReadAirway(live, pooled);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(new float[1], pooled));
        Assert.Throws<ArgumentException>(() => doll.ReadAirway(live, new float[3]));
    }
}
