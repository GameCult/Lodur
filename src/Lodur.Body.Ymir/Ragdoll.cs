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
    /// In a world with a particle system Build also places one liquid sensor per plan airway opening, and <see cref="ReadAirway"/> reads them.
    /// </summary>
    public sealed class Ragdoll
    {
        private readonly Box3DSoaWorld _world;
        private readonly int[] _bodyLane;
        private readonly int[] _jointLane;
        private readonly int[] _sensorLane;
        private readonly MuscleGains[] _gains;

        private Ragdoll(Box3DSoaWorld world, BodyPlan plan)
        {
            _world = world;
            Plan = plan;
            _bodyLane = new int[plan.Parts.Count];
            _jointLane = new int[plan.Joints.Count];
            _sensorLane = new int[plan.AirwayOpenings.Count];
            Array.Fill(_sensorLane, -1);
            _gains = new MuscleGains[plan.Joints.Count];
        }

        /// <summary>The plan this ragdoll was built from.</summary>
        public BodyPlan Plan { get; }

        /// <summary>World lane index of a part's body, by plan part ordinal.</summary>
        public int BodyLane(int part) => _bodyLane[part];

        /// <summary>World lane index of a joint, by plan joint ordinal.</summary>
        public int JointLane(int joint) => _jointLane[joint];

        /// <summary>
        /// Body and joint capacity a world needs to hold one ragdoll of <paramref name="plan"/> (plus whatever else the caller adds), and the sensor
        /// capacity: a world with a particle system must hold Sensors sensor lanes (a world without one needs none).
        /// </summary>
        public static (int Bodies, int Joints, int Sensors) Needs(BodyPlan plan) => (plan.Parts.Count, plan.Joints.Count, plan.AirwayOpenings.Count);

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

            if (plan.AirwayOpenings.Count > 0 && world.View.ParticleCapacity > 0)
            {
                // Ymir refuses the whole batch when it lacks lanes: a particle world that cannot hold the airway fails here, never silently.
                var sensors = new (int Body, (float X, float Y, float Z) Point, float Radius)[plan.AirwayOpenings.Count];
                for (var k = 0; k < sensors.Length; k++)
                {
                    var a = plan.AirwayOpenings[k];
                    sensors[k] = (r._bodyLane[a.Part], a.Offset, a.Radius);
                }

                var handles = new Box3DSoaConstraintHandle[sensors.Length];
                world.CreateSensors(sensors, handles);
                for (var k = 0; k < handles.Length; k++) r._sensorLane[k] = handles[k].Lane;
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

        /// <summary>Writes each plan part's world rotation and centre into the caller's span, one entry per part, for the Actor's orient layers. Allocates nothing.</summary>
        public void ReadPartPoses(Span<PartPose> poses)
        {
            var count = Plan.Parts.Count;
            if (poses.Length != count)
            {
                throw new ArgumentException($"poses needs {count} entries, one per part.");
            }

            var v = _world.View;
            for (var p = 0; p < count; p++)
            {
                var i = _bodyLane[p];
                poses[p] = new PartPose(new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]), v.PositionX[i], v.PositionY[i], v.PositionZ[i]);
            }
        }

        /// <summary>
        /// Writes, per plan airway opening, the end-of-step volume (m^3) of live fluid and of settled (wet patch) liquid inside its sphere
        /// (zeros in a world with no particle system), and the opening's facing: the cosine between its part's rotation applied to the
        /// opening's normalised offset and the normalised <paramref name="down"/> (1 pointing straight down, 0 on the side, -1 up).
        /// Facing comes from the body's pose and is written in every world. Refuses spans not one per opening and a zero <paramref name="down"/>. Allocates nothing.
        /// </summary>
        public void ReadAirway(Span<float> live, Span<float> pooled, Span<float> facing, (float X, float Y, float Z) down)
        {
            var n = _sensorLane.Length;
            if (live.Length != n || pooled.Length != n || facing.Length != n) throw new ArgumentException($"live, pooled and facing need {n} entries, one per airway opening.");
            var dl = MathF.Sqrt(down.X * down.X + down.Y * down.Y + down.Z * down.Z);
            if (!(dl > 0f) || !float.IsFinite(dl)) throw new ArgumentException("down needs a nonzero finite direction.", nameof(down));

            var v = _world.View;
            for (var k = 0; k < n; k++)
            {
                var lane = _sensorLane[k];
                live[k] = lane < 0 ? 0f : v.SensorLiquidVolume[lane];
                pooled[k] = lane < 0 ? 0f : v.SensorWetVolume[lane];

                var a = Plan.AirwayOpenings[k];
                var i = _bodyLane[a.Part];
                var o = a.Offset;
                var ol = MathF.Sqrt(o.X * o.X + o.Y * o.Y + o.Z * o.Z);
                if (ol <= 0f) { facing[k] = 0f; continue; }
                new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]).Rotate(o.X / ol, o.Y / ol, o.Z / ol, out var rx, out var ry, out var rz);
                facing[k] = (rx * down.X + ry * down.Y + rz * down.Z) / dl;
            }
        }
    }
}
