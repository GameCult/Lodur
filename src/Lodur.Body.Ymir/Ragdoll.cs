using System;
using Ymir.Box3D;

namespace Lodur.Body.Ymir
{
    /// <summary>
    /// A <see cref="BodyPlan"/> built in a caller-owned Ymir SoA world (invariant one-physics-world): parts in plan order, then joints
    /// in plan order, the stable id firstStableId + part ordinal, the limb lane the part ordinal, the parent lane the lane of the part's
    /// parent in the plan. The game owns the physics world: Build adds bodies to the caller's world and Lodur never creates or steps one.
    /// <see cref="Drive"/> is the muscle layer: it turns caller-owned strengths and target rotations into torques on the world's
    /// torque lanes and does nothing else to the physics. The binding holds no body numbers; the plan owns them.
    /// </summary>
    public sealed class Ragdoll
    {
        private readonly Box3DSoaWorld _world;
        private readonly int[] _bodyLane;
        private readonly int[] _jointLane;
        private readonly MuscleGains[] _gains;

        private Ragdoll(Box3DSoaWorld world, BodyPlan plan)
        {
            _world = world;
            Plan = plan;
            _bodyLane = new int[plan.Parts.Count];
            _jointLane = new int[plan.Joints.Count];
            _gains = new MuscleGains[plan.Joints.Count];
        }

        /// <summary>The plan this ragdoll was built from.</summary>
        public BodyPlan Plan { get; }

        /// <summary>World lane index of a part's body, by plan part ordinal.</summary>
        public int BodyLane(int part) => _bodyLane[part];

        /// <summary>World lane index of a joint, by plan joint ordinal.</summary>
        public int JointLane(int joint) => _jointLane[joint];

        /// <summary>Body and joint capacity a world needs to hold one ragdoll of <paramref name="plan"/> (plus whatever else the caller adds).</summary>
        public static (int Bodies, int Joints) Needs(BodyPlan plan) => (plan.Parts.Count, plan.Joints.Count);

        /// <summary>Builds <paramref name="plan"/> at <paramref name="origin"/> (x, z used). Stable ids are firstStableId + part ordinal.</summary>
        public static Ragdoll Build(Box3DSoaWorld world, BodyPlan plan, (float X, float Z) origin, ulong firstStableId)
        {
            var r = new Ragdoll(world, plan);
            var ox = origin.X;
            var oz = origin.Z;
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                var p = plan.Parts[i];
                var id = firstStableId + (ulong)i;
                var position = (ox + p.Position.X, p.Position.Y, oz + p.Position.Z);
                var parent = plan.ParentOf(i) < 0 ? -1 : r._bodyLane[plan.ParentOf(i)];
                switch (p.Shape)
                {
                    case PartShape.Box:
                        r._bodyLane[i] = world.AddBox(id, position, p.Size, p.Density, (p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W),
                            friction: p.Friction, parent: parent, limb: i);
                        break;
                    case PartShape.Capsule:
                        r._bodyLane[i] = world.AddCapsule(id, position, p.Size.X, p.Size.Y, p.Density, (p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W),
                            friction: p.Friction, parent: parent, limb: i, rollingResistance: p.RollingResistance);
                        break;
                    default:
                        r._bodyLane[i] = world.AddSphere(id, position, p.Size.X, p.Density,
                            friction: p.Friction, parent: parent, limb: i, rollingResistance: p.RollingResistance);
                        break;
                }
            }

            for (var j = 0; j < plan.Joints.Count; j++)
            {
                var d = plan.Joints[j];
                var kind = d.Kind == JointKind.Spherical ? Box3DSoaJointKind.Spherical : Box3DSoaJointKind.Revolute;
                r._jointLane[j] = world.AddJoint(kind, r._bodyLane[d.Parent], r._bodyLane[d.Child],
                    (ox + d.Anchor.X, d.Anchor.Y, oz + d.Anchor.Z), d.Axis, limit: true, lower: d.Lower, upper: d.Upper, coneAngle: d.Cone);
                r._gains[j] = d.Gains;
            }

            return r;
        }

        /// <summary>
        /// Writes this tick's muscle torques onto the world's torque lanes: for each joint a PD torque toward
        /// <paramref name="targets"/>[joint] (the child's rotation relative to the parent in the joint frame, whose z axis is a hinge's axis; a hinge has no swing gains, so it takes torque about that axis only) scaled by <paramref name="strengths"/>[joint], applied to the child
        /// and, equal and opposite, to the parent. Both arrays are the caller's, one entry per plan joint;
        /// nothing is copied or retained. Allocates nothing. Call once per step, before <c>world.Step</c>; the
        /// world clears the lanes when it applies them.
        /// </summary>
        public void Drive(ReadOnlySpan<float> strengths, ReadOnlySpan<Quat> targets)
        {
            if (strengths.Length != _gains.Length || targets.Length != _gains.Length)
            {
                throw new ArgumentException($"strengths and targets need {_gains.Length} entries, one per plan joint.");
            }

            var v = _world.View;
            for (var m = 0; m < _gains.Length; m++)
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
        /// Writes each plan sense point's world position into the caller's arrays, one entry per sense point: its part's position plus
        /// the part's rotation applied to the point's offset. Allocates nothing.
        /// </summary>
        public void ReadSensePoints(Span<float> x, Span<float> y, Span<float> z)
        {
            var points = Plan.SensePoints;
            if (x.Length != points.Count || y.Length != points.Count || z.Length != points.Count)
            {
                throw new ArgumentException($"x, y and z need {points.Count} entries, one per sense point.");
            }

            var v = _world.View;
            for (var s = 0; s < points.Count; s++)
            {
                var i = _bodyLane[points[s].Part];
                var o = points[s].Offset;
                new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]).Rotate(o.X, o.Y, o.Z, out var rx, out var ry, out var rz);
                x[s] = v.PositionX[i] + rx;
                y[s] = v.PositionY[i] + ry;
                z[s] = v.PositionZ[i] + rz;
            }
        }
    }
}
