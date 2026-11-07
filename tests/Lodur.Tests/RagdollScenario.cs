using Lodur;
using Lodur.Body.Ymir;
using Ymir.Box3D;

namespace Lodur.Tests;

/// <summary>One ragdoll on a ground slab, stepped at 120 Hz, with caller-owned strength and target arrays.</summary>
internal sealed class RagdollScenario : IDisposable
{
    public const float Dt = 1f / 120f;
    public readonly Box3DSoaWorld World;
    public readonly HumanoidRagdoll Ragdoll;
    public readonly float[] Strength = new float[HumanoidRagdoll.MuscleCount];
    public readonly Quat[] Target = new Quat[HumanoidRagdoll.MuscleCount];
    private int _steps;

    public RagdollScenario(ulong seed)
    {
        var (bodies, joints) = HumanoidRagdoll.Needs;
        World = new Box3DSoaWorld(capacity: bodies + 1, jointCapacity: joints);
        World.AddStaticBox(1, (0f, -0.5f, 0f), (20f, 0.5f, 20f), friction: 0.8f);
        Ragdoll = HumanoidRagdoll.Build(World, (0f, 0f), firstStableId: 100);
        Array.Fill(Target, Quat.Identity);
        Array.Fill(Strength, 1f);
        Shove(seed);
    }

    /// <summary>The seed's only effect: one step of random torque on pelvis and torso, up to 30 N m per axis.</summary>
    private void Shove(ulong seed)
    {
        var rng = new SplitMix64(seed);
        var v = World.View;
        foreach (var limb in new[] { Limb.Pelvis, Limb.Torso })
        {
            var i = Ragdoll.BodyLane(limb);
            v.TorqueX[i] = 30f * rng.NextSigned();
            v.TorqueY[i] = 30f * rng.NextSigned();
            v.TorqueZ[i] = 30f * rng.NextSigned();
        }
    }

    public void Run(float seconds)
    {
        var n = (int)MathF.Round(seconds / Dt);
        for (var i = 0; i < n; i++)
        {
            Ragdoll.Drive(Strength, Target);
            World.Step(Dt, 4);
            _steps++;
        }
    }

    public float HeadHeight => World.View.PositionY[Ragdoll.BodyLane(Limb.Head)];
    public float PelvisHeight => World.View.PositionY[Ragdoll.BodyLane(Limb.Pelvis)];

    /// <summary>Linear and angular speed of one limb's body.</summary>
    public (float Linear, float Angular) Speeds(Limb limb)
    {
        var v = World.View;
        var i = Ragdoll.BodyLane(limb);
        return (MathF.Sqrt(v.VelocityX[i] * v.VelocityX[i] + v.VelocityY[i] * v.VelocityY[i] + v.VelocityZ[i] * v.VelocityZ[i]),
                MathF.Sqrt(v.AngularVelocityX[i] * v.AngularVelocityX[i] + v.AngularVelocityY[i] * v.AngularVelocityY[i] + v.AngularVelocityZ[i] * v.AngularVelocityZ[i]));
    }

    /// <summary>The fastest linear and angular speed of any ragdoll body.</summary>
    public (float Linear, float Angular) MaxSpeeds()
    {
        var v = World.View;
        float lin = 0f, ang = 0f;
        for (var i = 1; i < v.Count; i++)
        {
            lin = MathF.Max(lin, MathF.Sqrt(v.VelocityX[i] * v.VelocityX[i] + v.VelocityY[i] * v.VelocityY[i] + v.VelocityZ[i] * v.VelocityZ[i]));
            ang = MathF.Max(ang, MathF.Sqrt(v.AngularVelocityX[i] * v.AngularVelocityX[i] + v.AngularVelocityY[i] * v.AngularVelocityY[i] + v.AngularVelocityZ[i] * v.AngularVelocityZ[i]));
        }
        return (lin, ang);
    }

    /// <summary>FNV-1a 64 over every body and joint state lane, byte for byte, in lane order.</summary>
    public ulong StateHash()
    {
        var v = World.View;
        var h = 14695981039346656037ul;
        void Bytes(ReadOnlySpan<byte> b)
        {
            foreach (var x in b) { h ^= x; h *= 1099511628211ul; }
        }
        void F(ReadOnlySpan<float> lane) => Bytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(lane));
        Bytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(BitConverter.GetBytes(World.StepCount).AsSpan()));
        F(v.PositionX); F(v.PositionY); F(v.PositionZ);
        F(v.RotationX); F(v.RotationY); F(v.RotationZ); F(v.RotationW);
        F(v.VelocityX); F(v.VelocityY); F(v.VelocityZ);
        F(v.AngularVelocityX); F(v.AngularVelocityY); F(v.AngularVelocityZ);
        F(v.JointAngle);
        F(v.JointRotationX); F(v.JointRotationY); F(v.JointRotationZ); F(v.JointRotationW);
        F(v.JointAngularVelocityX); F(v.JointAngularVelocityY); F(v.JointAngularVelocityZ);
        return h;
    }

    public void Dispose() => World.Dispose();
}

/// <summary>SplitMix64: a seedable generator whose sequence is the same on every platform.</summary>
internal struct SplitMix64
{
    private ulong _s;
    public SplitMix64(ulong seed) => _s = seed;

    public ulong Next()
    {
        var z = _s += 0x9E3779B97F4A7C15ul;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [-1, 1).</summary>
    public float NextSigned() => (Next() >> 40) / (float)(1 << 23) - 1f;
}
