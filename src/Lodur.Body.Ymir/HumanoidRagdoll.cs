using System;
using Ymir.Box3D;

namespace Lodur.Body.Ymir
{
    /// <summary>The ragdoll's joints, in strength-array and target-array order.</summary>
    public enum Muscle
    {
        Spine, Neck, ShoulderL, ShoulderR, ElbowL, ElbowR, HipL, HipR, KneeL, KneeR, AnkleL, AnkleR,
        WristL, WristR, ThumbL, ThumbR, FingersL, FingersR
    }

    /// <summary>The body regions perception senses at, in the order of the point arrays <see cref="HumanoidRagdoll.ReadRegions"/> fills.</summary>
    public enum Region
    {
        HandL, HandR, Head
    }

    /// <summary>The ragdoll's bodies, also the limb label written into the world's limb lane.</summary>
    public enum Limb
    {
        Pelvis, Torso, Head, UpperArmL, UpperArmR, LowerArmL, LowerArmR, ThighL, ThighR, ShinL, ShinR, FootL, FootR,
        PalmL, PalmR, ThumbL, ThumbR, FingersL, FingersR
    }

    /// <summary>
    /// A 19-body humanoid built in a caller-owned Ymir SoA world (invariant one-physics-world): capsule limbs,
    /// box pelvis, torso, feet and palms, a sphere head, a thumb capsule and a fingers box per hand, 18 joints: spherical at
    /// spine, neck, shoulders, hips, wrists and thumbs, hinges at elbows, knees, ankles and knuckles. Facing +z, left is +x, standing on y = 0 at the creation pose, which is also
    /// every joint's zero. The game owns the physics world: Build adds bodies to the caller's world and Lodur never creates or steps one. <see cref="Drive"/> is the muscle layer: it turns caller-owned strengths and target
    /// rotations into torques on the world's torque lanes and does nothing else to the physics.
    /// </summary>
    public sealed class HumanoidRagdoll
    {
        public const int MuscleCount = 18;
        public const int LimbCount = 19;
        public const int RegionCount = 3;

        private const float Density = 1000f;
        private const float Friction = 0.8f;
        /// <summary>The fingers' creation curl at the knuckle, radians: the relaxed resting hand (question hand-rest-curl).</summary>
        private const float RestCurl = 0.35f;
        private const float Thumb30 = 30f * MathF.PI / 180f; // the thumb's metacarpal, out of the palm plane
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

        /// <summary>Builds the ragdoll standing with its feet on y = 0 at <paramref name="origin"/> (x, z used). Stable ids are firstStableId.. + 18.</summary>
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

            // Hands, appended after the base bodies. The palm faces -sx with the thumb forward (+z); the thumb capsule
            // lies along its metacarpal axis, 30 degrees out of the palm plane; the fingers are created at the rest curl.
            var cmcAxis = (0f, -MathF.Cos(Thumb30), MathF.Sin(Thumb30));
            foreach (var (side, sx) in new[] { (0, 1f), (1, -1f) })
            {
                var lowerArm = r._bodyLane[(int)Limb.LowerArmL + side];
                var palm = Box(Limb.PalmL + side, lowerArm, sx * 0.21f, 0.930f, 0f, 0.014f, 0.045f, 0.040f);
                var thumbSpin = Quat.FromAxisAngle(1f, 0f, 0f, -Thumb30);
                r._bodyLane[(int)Limb.ThumbL + side] = world.AddCapsule(id++,
                    (ox + sx * 0.206f + 0.05f * cmcAxis.Item1, 0.955f + 0.05f * cmcAxis.Item2, oz + 0.03f + 0.05f * cmcAxis.Item3),
                    0.011f, 0.03f, Density, (thumbSpin.X, thumbSpin.Y, thumbSpin.Z, thumbSpin.W),
                    friction: Friction, parent: palm, limb: (int)Limb.ThumbL + side);
                var curl = Quat.FromAxisAngle(0f, 0f, sx, -RestCurl);
                r._bodyLane[(int)Limb.FingersL + side] = world.AddBox(id++,
                    (ox + sx * 0.21f - sx * MathF.Sin(RestCurl) * 0.0425f, 0.885f - MathF.Cos(RestCurl) * 0.0425f, oz),
                    (0.011f, 0.0425f, 0.038f), Density, (curl.X, curl.Y, curl.Z, curl.W),
                    friction: Friction, parent: palm, limb: (int)Limb.FingersL + side);
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
                Joint(Muscle.ShoulderL + side, Box3DSoaJointKind.Spherical, Limb.Torso, Limb.UpperArmL + side, sx * 0.21f, 1.55f, 0f, up, new MuscleGains(20f, 1.5f, 1f, 0.02f), -1f, 1f, 1.5f); // damping carries the hand's mass (hands-gains: 1 and 0.6 before)
                Joint(Muscle.ElbowL + side, Box3DSoaJointKind.Revolute, Limb.UpperArmL + side, Limb.LowerArmL + side, sx * 0.21f, 1.24f, 0f, across, new MuscleGains(0f, 0f, 12f, 0.9f), -2.4f, 0f);
                Joint(Muscle.HipL + side, Box3DSoaJointKind.Spherical, Limb.Pelvis, Limb.ThighL + side, sx * 0.09f, 1.04f, 0f, up, new MuscleGains(600f, 3f, 10f, 0.15f), -0.5f, 0.5f, 1.2f);
                Joint(Muscle.KneeL + side, Box3DSoaJointKind.Revolute, Limb.ThighL + side, Limb.ShinL + side, sx * 0.09f, 0.54f, 0f, across, new MuscleGains(0f, 0f, 300f, 8f), 0f, 2.4f);
                Joint(Muscle.AnkleL + side, Box3DSoaJointKind.Revolute, Limb.ShinL + side, Limb.FootL + side, sx * 0.09f, 0.08f, 0f, across, new MuscleGains(0f, 0f, 100f, 0.5f), -0.6f, 0.6f);
            }

            // Hand gains are bounded by each body's own inertia, not the arm subtree's (explicit PD is stable only while
            // damping * dt stays under the inertia it acts on; a wrist kd sized for the arm limit-cycles at once), and
            // every hand joint keeps passive damping when limp so loose hands settle instead of ringing.
            for (var side = 0; side < 2; side++)
            {
                var sx = side == 0 ? 1f : -1f;
                Joint(Muscle.WristL + side, Box3DSoaJointKind.Spherical, Limb.LowerArmL + side, Limb.PalmL + side, sx * 0.21f, 0.975f, 0f, up, new MuscleGains(1f, 0.012f, 0.3f, 0.005f, 1f), -1.2f, 1.2f, 1.0f);
                Joint(Muscle.ThumbL + side, Box3DSoaJointKind.Spherical, Limb.PalmL + side, Limb.ThumbL + side, sx * 0.206f, 0.955f, 0.03f, cmcAxis, new MuscleGains(0.06f, 0.001f, 0f, 0f, 1f), 0f, 0f, 0.7f); // twist locked: a free thumb spins
                Joint(Muscle.FingersL + side, Box3DSoaJointKind.Revolute, Limb.PalmL + side, Limb.FingersL + side, sx * 0.21f, 0.885f, 0f, (0f, 0f, sx), new MuscleGains(0f, 0f, 0.3f, 0.004f, 1f), -1.6f + RestCurl, 0.35f + RestCurl);
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

        /// <summary>
        /// Writes each <see cref="Region"/>'s world point into the caller's arrays, one entry per region: a hand is its
        /// palm body's centre, the head the head body's centre. Allocates nothing.
        /// </summary>
        public void ReadRegions(Span<float> x, Span<float> y, Span<float> z)
        {
            if (x.Length != RegionCount || y.Length != RegionCount || z.Length != RegionCount)
            {
                throw new ArgumentException($"x, y and z need {RegionCount} entries, one per region.");
            }

            var v = _world.View;
            for (var r = 0; r < (int)Region.Head; r++)
            {
                var i = _bodyLane[(int)(r == (int)Region.HandL ? Limb.PalmL : Limb.PalmR)];
                x[r] = v.PositionX[i];
                y[r] = v.PositionY[i];
                z[r] = v.PositionZ[i];
            }

            var head = _bodyLane[(int)Limb.Head];
            x[(int)Region.Head] = v.PositionX[head];
            y[(int)Region.Head] = v.PositionY[head];
            z[(int)Region.Head] = v.PositionZ[head];
        }
    }
}
