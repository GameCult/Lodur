using System;
using Ymir.Box3D;

namespace Lodur.Body.Ymir
{
    /// <summary>The ragdoll's joints, in strength-array and target-array order.</summary>
    internal enum Muscle
    {
        Spine, Neck, ShoulderL, ShoulderR, ElbowL, ElbowR, HipL, HipR, KneeL, KneeR, AnkleL, AnkleR
    }

    /// <summary>The ragdoll's bodies, also the limb label written into the world's limb lane.</summary>
    internal enum Limb
    {
        Pelvis, Torso, Head, UpperArmL, UpperArmR, LowerArmL, LowerArmR, ThighL, ThighR, ShinL, ShinR, FootL, FootR
    }

    /// <summary>
    /// A 13-body humanoid built in a caller-owned Ymir SoA world (invariant one-physics-world): capsule limbs,
    /// box pelvis, torso and feet, a sphere head, spherical joints at spine, neck, shoulders and hips, hinges at
    /// elbows, knees and ankles. Facing +z, left is +x, standing on y = 0 at the creation pose, which is also
    /// every joint's zero. <see cref="Drive"/> is the muscle layer: it turns caller-owned strengths and target
    /// rotations into torques on the world's torque lanes and does nothing else to the physics.
    /// </summary>
    internal sealed class HumanoidRagdoll
    {
        public const int MuscleCount = 12;
        public const int LimbCount = 13;

        private const float Density = 1000f;
        private const float Friction = 0.8f;
        private const float HeadRolling = 0.1f; // a bare sphere on the ground rolls on, so the fallen head never settles

        private readonly Box3DSoaWorld _world;
        private readonly int[] _bodyLane = new int[LimbCount];
        private readonly int[] _jointLane = new int[MuscleCount];
        private readonly MuscleGains[] _gains = new MuscleGains[MuscleCount];

        private HumanoidRagdoll(Box3DSoaWorld world) => _world = world;

        /// <summary>World lane index of a limb's body.</summary>
        public int BodyLane(Limb limb) => _bodyLane[(int)limb];

        /// <summary>World lane index of a muscle's joint.</summary>
        public int JointLane(Muscle muscle) => _jointLane[(int)muscle];

        /// <summary>Body and joint capacity a world needs to hold one ragdoll (plus whatever else the caller adds).</summary>
        public static (int Bodies, int Joints) Needs => (LimbCount, MuscleCount);

        /// <summary>Builds the ragdoll standing with its feet on y = 0 at <paramref name="origin"/> (x, z used). Stable ids are firstStableId.. + 12.</summary>
        public static HumanoidRagdoll Build(Box3DSoaWorld world, (float X, float Z) origin, ulong firstStableId)
        {
            var r = new HumanoidRagdoll(world);
            var ox = origin.X;
            var oz = origin.Z;
            ulong id = firstStableId;

            int Box(Limb limb, int parent, float x, float y, float z, float hx, float hy, float hz) =>
                r._bodyLane[(int)limb] = world.AddBox(id++, (ox + x, y, oz + z), (hx, hy, hz), Density,
                    friction: Friction, parent: parent, limb: (int)limb);
            int Capsule(Limb limb, int parent, float x, float y, float radius, float half) =>
                r._bodyLane[(int)limb] = world.AddCapsule(id++, (ox + x, y, oz), radius, half, Density,
                    friction: Friction, parent: parent, limb: (int)limb);

            var pelvis = Box(Limb.Pelvis, -1, 0f, 1.11f, 0f, 0.15f, 0.08f, 0.09f);
            var torso = Box(Limb.Torso, pelvis, 0f, 1.41f, 0f, 0.15f, 0.22f, 0.09f);
            var head = r._bodyLane[(int)Limb.Head] = world.AddSphere(id++, (ox, 1.74f, oz), 0.11f, Density,
                friction: Friction, parent: torso, limb: (int)Limb.Head, rollingResistance: HeadRolling);
            foreach (var (side, sx) in new[] { (0, 1f), (1, -1f) })
            {
                var upperArm = Capsule(Limb.UpperArmL + side, torso, sx * 0.21f, 1.395f, 0.045f, 0.11f);
                var lowerArm = Capsule(Limb.LowerArmL + side, upperArm, sx * 0.21f, 1.10f, 0.04f, 0.10f);
                var thigh = Capsule(Limb.ThighL + side, pelvis, sx * 0.09f, 0.79f, 0.07f, 0.18f);
                var shin = Capsule(Limb.ShinL + side, thigh, sx * 0.09f, 0.31f, 0.06f, 0.17f);
                Box(Limb.FootL + side, shin, sx * 0.09f, 0.04f, 0.05f, 0.05f, 0.04f, 0.12f);
            }

            void Joint(Muscle m, Box3DSoaJointKind kind, Limb parent, Limb child, float x, float y, float z,
                (float, float, float) axis, MuscleGains gains, float lower = 0f, float upper = 0f, float cone = 0f)
            {
                r._jointLane[(int)m] = world.AddJoint(kind, r._bodyLane[(int)parent], r._bodyLane[(int)child],
                    (ox + x, y, oz + z), axis, limit: true, lower: lower, upper: upper, coneAngle: cone);
                r._gains[(int)m] = gains;
            }

            var up = (0f, 1f, 0f);
            var across = (1f, 0f, 0f);
            // Gains are tuned against measured stand/fall runs at 120 Hz (see the tests), not derived: explicit PD on
            // light limbs is stable only while damping * dt stays under the inertia it acts on.
            Joint(Muscle.Spine, Box3DSoaJointKind.Spherical, Limb.Pelvis, Limb.Torso, 0f, 1.19f, 0f, up, new MuscleGains(500f, 4f, 200f, 8f), -0.4f, 0.4f, 0.5f);
            Joint(Muscle.Neck, Box3DSoaJointKind.Spherical, Limb.Torso, Limb.Head, 0f, 1.63f, 0f, up, new MuscleGains(30f, 0.8f, 20f, 0.6f), -0.6f, 0.6f, 0.6f);
            for (var side = 0; side < 2; side++)
            {
                var sx = side == 0 ? 1f : -1f;
                Joint(Muscle.ShoulderL + side, Box3DSoaJointKind.Spherical, Limb.Torso, Limb.UpperArmL + side, sx * 0.21f, 1.55f, 0f, up, new MuscleGains(20f, 1f, 1f, 0.02f), -1f, 1f, 1.5f);
                Joint(Muscle.ElbowL + side, Box3DSoaJointKind.Revolute, Limb.UpperArmL + side, Limb.LowerArmL + side, sx * 0.21f, 1.24f, 0f, across, new MuscleGains(0f, 0f, 12f, 0.6f), -2.4f, 0f);
                Joint(Muscle.HipL + side, Box3DSoaJointKind.Spherical, Limb.Pelvis, Limb.ThighL + side, sx * 0.09f, 1.04f, 0f, up, new MuscleGains(600f, 3f, 10f, 0.15f), -0.5f, 0.5f, 1.2f);
                Joint(Muscle.KneeL + side, Box3DSoaJointKind.Revolute, Limb.ThighL + side, Limb.ShinL + side, sx * 0.09f, 0.54f, 0f, across, new MuscleGains(0f, 0f, 300f, 8f), 0f, 2.4f);
                Joint(Muscle.AnkleL + side, Box3DSoaJointKind.Revolute, Limb.ShinL + side, Limb.FootL + side, sx * 0.09f, 0.08f, 0f, across, new MuscleGains(0f, 0f, 100f, 0.5f), -0.6f, 0.6f);
            }
            return r;
        }

        /// <summary>
        /// Writes this tick's muscle torques onto the world's torque lanes: for each joint a PD torque toward
        /// <paramref name="targets"/>[joint] (the child's rotation relative to the parent in the joint frame, whose z axis is a hinge's axis; a hinge has no swing gains, so it takes torque about that axis only) scaled by <paramref name="strengths"/>[joint], applied to the child
        /// and, equal and opposite, to the parent. Both arrays are the caller's, one entry per <see cref="Muscle"/>;
        /// nothing is copied or retained. Allocates nothing. Call once per step, before <c>world.Step</c>; the
        /// world clears the lanes when it applies them.
        /// </summary>
        public void Drive(ReadOnlySpan<float> strengths, ReadOnlySpan<Quat> targets)
        {
            if (strengths.Length != MuscleCount || targets.Length != MuscleCount)
            {
                throw new ArgumentException($"strengths and targets need {MuscleCount} entries, one per muscle.");
            }

            var v = _world.View;
            for (var m = 0; m < MuscleCount; m++)
            {
                if (!(strengths[m] > 0f))
                {
                    continue; // limp (or NaN): no torque
                }

                var j = _jointLane[m];
                var rotation = new Quat(v.JointRotationX[j], v.JointRotationY[j], v.JointRotationZ[j], v.JointRotationW[j]);
                PdMuscle.Torque(rotation, v.JointAngularVelocityX[j], v.JointAngularVelocityY[j], v.JointAngularVelocityZ[j],
                    targets[m], _gains[m], strengths[m], out var tx, out var ty, out var tz);
                new Quat(v.JointFrameX[j], v.JointFrameY[j], v.JointFrameZ[j], v.JointFrameW[j])
                    .Rotate(tx, ty, tz, out var wx, out var wy, out var wz);
                var child = v.JointBodyB[j];
                var parent = v.JointBodyA[j];
                v.TorqueX[child] += wx; v.TorqueY[child] += wy; v.TorqueZ[child] += wz;
                v.TorqueX[parent] -= wx; v.TorqueY[parent] -= wy; v.TorqueZ[parent] -= wz;
            }
        }
    }
}
