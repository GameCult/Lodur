using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Body plans in code, until they live in a store (follow-up lodur-body-plan-cultcache).</summary>
    public static class BodyPlans
    {
        private const float Density = 1000f;
        private const float Friction = 0.8f;
        /// <summary>The fingers' creation curl at the knuckle, radians: the relaxed resting hand (question hand-rest-curl).</summary>
        private const float RestCurl = 0.35f;
        private const float Thumb30 = 30f * MathF.PI / 180f; // the thumb's metacarpal, out of the palm plane
        private const float HeadRolling = 0.1f; // a bare sphere on the ground rolls on, so the fallen head never settles

        /// <summary>
        /// A 19-part humanoid: capsule limbs, box pelvis, torso, feet and palms, a sphere head, a thumb capsule and a fingers box per hand,
        /// 18 joints: spherical at spine, neck, shoulders, hips, wrists and thumbs, hinges at elbows, knees, ankles and knuckles.
        /// Facing +z, left is +x, standing on y = 0 at the creation pose, which is also every joint's zero. Parts and joints are in
        /// build order: the trunk, then per side (left, right) the limbs, then per side the hand.
        /// </summary>
        public static BodyPlan Humanoid()
        {
            var parts = new List<PartDef>();
            var joints = new List<JointDef>();

            int Box(string label, Side side, float x, float y, float z, float hx, float hy, float hz) =>
                Add(parts, new PartDef(label, side, PartShape.Box, (hx, hy, hz), (x, y, z), Quat.Identity, Density, Friction, 0f));
            int Capsule(string label, Side side, float x, float y, float radius, float half) =>
                Add(parts, new PartDef(label, side, PartShape.Capsule, (radius, half, 0f), (x, y, 0f), Quat.Identity, Density, Friction, 0f));

            var pelvis = Box("Pelvis", Side.Center, 0f, 1.11f, 0f, 0.15f, 0.08f, 0.09f);
            var torso = Box("Torso", Side.Center, 0f, 1.41f, 0f, 0.15f, 0.22f, 0.09f);
            var head = Add(parts, new PartDef("Head", Side.Center, PartShape.Sphere, (0.11f, 0f, 0f), (0f, 1.74f, 0f), Quat.Identity, Density, Friction, HeadRolling));
            var sides = new[] { (Side.Left, 1f), (Side.Right, -1f) };
            var upperArm = new int[2]; var lowerArm = new int[2]; var thigh = new int[2]; var shin = new int[2]; var foot = new int[2];
            for (var k = 0; k < 2; k++)
            {
                var (side, sx) = sides[k];
                upperArm[k] = Capsule("UpperArm", side, sx * 0.21f, 1.395f, 0.045f, 0.11f);
                lowerArm[k] = Capsule("LowerArm", side, sx * 0.21f, 1.10f, 0.04f, 0.10f);
                thigh[k] = Capsule("Thigh", side, sx * 0.09f, 0.79f, 0.07f, 0.18f);
                shin[k] = Capsule("Shin", side, sx * 0.09f, 0.31f, 0.06f, 0.17f);
                foot[k] = Box("Foot", side, sx * 0.09f, 0.04f, 0.05f, 0.05f, 0.04f, 0.12f);
            }

            // Hands, appended after the base parts. The palm faces -sx with the thumb forward (+z); the thumb capsule
            // lies along its metacarpal axis, 30 degrees out of the palm plane; the fingers are created at the rest curl.
            var cmcAxis = (0f, -MathF.Cos(Thumb30), MathF.Sin(Thumb30));
            var palm = new int[2]; var thumb = new int[2]; var fingers = new int[2];
            for (var k = 0; k < 2; k++)
            {
                var (side, sx) = sides[k];
                palm[k] = Box("Palm", side, sx * 0.21f, 0.930f, 0f, 0.014f, 0.045f, 0.040f);
                var thumbSpin = Quat.FromAxisAngle(1f, 0f, 0f, -Thumb30);
                thumb[k] = Add(parts, new PartDef("Thumb", side, PartShape.Capsule, (0.011f, 0.03f, 0f),
                    (sx * 0.206f + 0.05f * cmcAxis.Item1, 0.955f + 0.05f * cmcAxis.Item2, 0.03f + 0.05f * cmcAxis.Item3),
                    thumbSpin, Density, Friction, 0f));
                var curl = Quat.FromAxisAngle(0f, 0f, sx, -RestCurl);
                fingers[k] = Add(parts, new PartDef("Fingers", side, PartShape.Box, (0.011f, 0.0425f, 0.038f),
                    (sx * 0.21f - sx * MathF.Sin(RestCurl) * 0.0425f, 0.885f - MathF.Cos(RestCurl) * 0.0425f, 0f),
                    curl, Density, Friction, 0f));
            }

            void Joint(string label, Side side, JointKind kind, int parent, int child, float x, float y, float z,
                (float, float, float) axis, MuscleGains gains, float lower = 0f, float upper = 0f, float cone = 0f) =>
                joints.Add(new JointDef(label, side, kind, parent, child, (x, y, z), axis, lower, upper, cone, gains));

            var up = (0f, 1f, 0f);
            var across = (1f, 0f, 0f);
            // Gains are tuned against measured stand/fall runs at 120 Hz (see the tests), not derived: explicit PD on
            // light limbs is stable only while damping * dt stays under the inertia it acts on.
            Joint("Spine", Side.Center, JointKind.Spherical, pelvis, torso, 0f, 1.19f, 0f, up, new MuscleGains(500f, 4f, 200f, 8f), -0.4f, 0.4f, 0.5f);
            Joint("Neck", Side.Center, JointKind.Spherical, torso, head, 0f, 1.63f, 0f, up, new MuscleGains(30f, 0.8f, 20f, 0.6f), -0.6f, 0.6f, 0.6f);
            for (var k = 0; k < 2; k++)
            {
                var (side, sx) = sides[k];
                Joint("Shoulder", side, JointKind.Spherical, torso, upperArm[k], sx * 0.21f, 1.55f, 0f, up, new MuscleGains(20f, 1.5f, 1f, 0.02f), -1f, 1f, 1.5f); // damping carries the hand's mass (hands-gains: 1 and 0.6 before)
                Joint("Elbow", side, JointKind.Revolute, upperArm[k], lowerArm[k], sx * 0.21f, 1.24f, 0f, across, new MuscleGains(0f, 0f, 12f, 0.9f), -2.4f, 0f);
                Joint("Hip", side, JointKind.Spherical, pelvis, thigh[k], sx * 0.09f, 1.04f, 0f, up, new MuscleGains(600f, 3f, 10f, 0.15f), -0.5f, 0.5f, 1.2f);
                Joint("Knee", side, JointKind.Revolute, thigh[k], shin[k], sx * 0.09f, 0.54f, 0f, across, new MuscleGains(0f, 0f, 300f, 8f), 0f, 2.4f);
                Joint("Ankle", side, JointKind.Revolute, shin[k], foot[k], sx * 0.09f, 0.08f, 0f, across, new MuscleGains(0f, 0f, 100f, 0.5f), -0.6f, 0.6f);
            }

            // Hand gains are bounded by each body's own inertia, not the arm subtree's (explicit PD is stable only while
            // damping * dt stays under the inertia it acts on; a wrist kd sized for the arm limit-cycles at once), and
            // every hand joint keeps passive damping when limp so loose hands settle instead of ringing.
            for (var k = 0; k < 2; k++)
            {
                var (side, sx) = sides[k];
                Joint("Wrist", side, JointKind.Spherical, lowerArm[k], palm[k], sx * 0.21f, 0.975f, 0f, up, new MuscleGains(1f, 0.012f, 0.3f, 0.005f, 1f), -1.2f, 1.2f, 1.0f);
                Joint("Thumb", side, JointKind.Spherical, palm[k], thumb[k], sx * 0.206f, 0.955f, 0.03f, cmcAxis, new MuscleGains(0.06f, 0.001f, 0f, 0f, 1f), 0f, 0f, 0.7f); // twist locked: a free thumb spins
                Joint("Fingers", side, JointKind.Revolute, palm[k], fingers[k], sx * 0.21f, 0.885f, 0f, (0f, 0f, sx), new MuscleGains(0f, 0f, 0.3f, 0.004f, 1f), -1.6f + RestCurl, 0.35f + RestCurl);
            }

            var senses = new[]
            {
                new SensePointDef("Hand", Side.Left, palm[0], (0f, 0f, 0f)),
                new SensePointDef("Hand", Side.Right, palm[1], (0f, 0f, 0f)),
                new SensePointDef("Head", Side.Center, head, (0f, 0f, 0f)),
            };
            return new BodyPlan(parts, joints, senses);
        }

        private static int Add(List<PartDef> parts, PartDef part)
        {
            parts.Add(part);
            return parts.Count - 1;
        }
    }
}
