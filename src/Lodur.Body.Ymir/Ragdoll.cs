using System;
using Ymir.Box3D;

namespace Lodur.Body.Ymir
{
    /// <summary>
    /// A <see cref="BodyPlan"/> built in a caller-owned Ymir SoA world (invariant one-physics-world): parts in plan order, then joints
    /// in plan order, the stable id firstStableId + part ordinal, the limb lane the part ordinal, the parent lane the lane of the part's
    /// parent in the plan. The game owns the physics world: Build adds bodies to the caller's world and Lodur never creates or steps one.
    /// <see cref="Drive"/> is the muscle layer: it turns caller-owned strengths and target rotations into torques on the world's
    /// torque lanes and does nothing else to the physics. Each muscle's torque is a PD term toward its target plus the gravity load of the
    /// muscle's unsupported subtree times its strength, projected on the axes it has gains for (ruling gravity-feedforward-all-muscles), so
    /// sag comes only from weakness. Each body's weight is carried from the lowest common ancestor of it and every body it touches, and by
    /// no joint when it touches anything outside this ragdoll: only a touch outside a joint's subtree supports that joint's load (ruling
    /// gravity-support-subtree-external, superseding gravity-feedforward-count-gate). The load reads Ymir's Mass, Facts.Touching, JointOrigin and
    /// Gravity. The binding holds no body numbers; the plan owns them.
    /// In a world with a particle system Build also places one liquid sensor per plan airway opening, and <see cref="ReadAirway"/> reads them.
    /// </summary>
    public sealed class Ragdoll
    {
        private readonly Box3DSoaWorld _world;
        private readonly int[] _bodyLane;
        private readonly int[] _jointLane;
        // The airway sensors, one per plan opening, when Build created them (a world with no particle system has none); Ragdoll is their one owner. Ask and taken are TakeDrawn's scratch.
        private readonly Box3DSoaConstraintHandle[] _sensors;
        private readonly float[] _ask, _taken;
        private bool _hasSensors;
        // LowestOpening's last answer (-1 before the first): the opening it keeps until another's rim lies LowestHysteresis lower.
        private int _lowest = -1;
        private readonly MuscleGains[] _gains;
        // Subtree mass and mass-weighted position per part: scratch rebuilt every Drive, never read across ticks.
        private readonly float[] _mass, _momentX, _momentY, _momentZ;
        // Depth in the plan's tree, fixed at construction, and the part each part's weight is deposited at (-1: carried from outside): the deposit is scratch rebuilt every Drive.
        private readonly int[] _depth, _deposit;
        private ulong _firstStableId;

        private Ragdoll(Box3DSoaWorld world, BodyPlan plan)
        {
            _world = world;
            Plan = plan;
            _bodyLane = new int[plan.Parts.Count];
            _jointLane = new int[plan.Joints.Count];
            _sensors = new Box3DSoaConstraintHandle[plan.AirwayOpenings.Count];
            _ask = new float[plan.AirwayOpenings.Count];
            _taken = new float[plan.AirwayOpenings.Count];
            _gains = new MuscleGains[plan.Joints.Count];
            _mass = new float[plan.Parts.Count];
            _momentX = new float[plan.Parts.Count];
            _momentY = new float[plan.Parts.Count];
            _momentZ = new float[plan.Parts.Count];
            _depth = new int[plan.Parts.Count];
            _deposit = new int[plan.Parts.Count];
            for (var i = 1; i < _depth.Length; i++) _depth[i] = plan.ParentOf(i) < 0 ? 0 : _depth[plan.ParentOf(i)] + 1; // parent before child
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
            r._firstStableId = firstStableId;
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

                world.CreateSensors(sensors, r._sensors);
                r._hasSensors = true;
            }

            return r;
        }

        /// <summary>
        /// Writes this tick's muscle torques onto the world's torque lanes: for each joint a PD torque toward
        /// <paramref name="targets"/>[joint] (the child's rotation relative to the parent in the joint frame, whose z axis is a hinge's axis; a hinge has no swing gains, so it takes torque about that axis only) scaled by <paramref name="strengths"/>[joint], applied to the child
        /// and, equal and opposite, to the parent, plus the gravity feedforward: the torque about the joint origin that holds up the weight of the child's
        /// subtree, except each body in it that touches something outside the subtree (the floor, an object, another body: ruling
        /// gravity-support-subtree-external), times the strength and kept to the joint frame axes the muscle has gains for. A world with no gravity skips it.
        /// Both arrays are the caller's, one entry per plan joint; nothing is copied or retained. Allocates nothing. Throws when the world dropped
        /// touching pairs for want of fact capacity: a load from a partial list is wrong without a trace. Call once per step, before <c>world.Step</c>; the
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

            var (gx, gy, gz) = _world.Gravity;
            if (gx == 0f && gy == 0f && gz == 0f) return;
            var n = _mass.Length;
            var facts = _world.Facts;
            if (facts.Dropped.Touching > 0)
            {
                throw new InvalidOperationException("The world dropped touching pairs past its fact capacity; the gravity load needs every pair. Raise the world's fact capacity.");
            }

            for (var p = 0; p < n; p++) _deposit[p] = p;
            foreach (var t in facts.Touching)
            {
                var a = PartOf(t.LaneA, t.StableIdA);
                var b = PartOf(t.LaneB, t.StableIdB);
                if (a >= 0 && b >= 0)
                {
                    if (_deposit[a] >= 0) _deposit[a] = CommonAncestor(_deposit[a], b);
                    if (_deposit[b] >= 0) _deposit[b] = CommonAncestor(_deposit[b], a);
                }
                else if (a >= 0) _deposit[a] = -1;
                else if (b >= 0) _deposit[b] = -1;
            }

            Array.Clear(_mass);
            Array.Clear(_momentX);
            Array.Clear(_momentY);
            Array.Clear(_momentZ);
            for (var p = 0; p < n; p++)
            {
                var d = _deposit[p];
                if (d < 0) continue;
                var b = _bodyLane[p];
                var load = v.Mass[b];
                _mass[d] += load;
                _momentX[d] += load * v.PositionX[b];
                _momentY[d] += load * v.PositionY[b];
                _momentZ[d] += load * v.PositionZ[b];
            }

            for (var p = n - 1; p > 0; p--) // BodyPlan puts every parent before its child
            {
                var q = Plan.ParentOf(p);
                if (q < 0) continue;
                _mass[q] += _mass[p];
                _momentX[q] += _momentX[p];
                _momentY[q] += _momentY[p];
                _momentZ[q] += _momentZ[p];
            }

            for (var m = 0; m < _gains.Length; m++)
            {
                var j = _jointLane[m];
                var c = Plan.Joints[m].Child;
                var rx = _momentX[c] - _mass[c] * v.JointOriginX[j];
                var ry = _momentY[c] - _mass[c] * v.JointOriginY[j];
                var rz = _momentZ[c] - _mass[c] * v.JointOriginZ[j];
                var k = MathF.Max(strengths[m], 0f);
                // The muscle holds the load up: minus the gravity torque of the subtree's weight about the joint origin.
                var tx = -k * (ry * gz - rz * gy);
                var ty = -k * (rz * gx - rx * gz);
                var tz = -k * (rx * gy - ry * gx);
                var frame = new Quat(v.JointFrameX[j], v.JointFrameY[j], v.JointFrameZ[j], v.JointFrameW[j]);
                frame.Conjugate().Rotate(tx, ty, tz, out var lx, out var ly, out var lz);
                if (_gains[m].SwingStiffness <= 0f) { lx = 0f; ly = 0f; }
                if (_gains[m].TwistStiffness <= 0f) lz = 0f;
                frame.Rotate(lx, ly, lz, out var wx, out var wy, out var wz);
                var child = v.JointBodyB[j];
                var parent = v.JointBodyA[j];
                v.TorqueX[child] += wx; v.TorqueY[child] += wy; v.TorqueZ[child] += wz;
                v.TorqueX[parent] -= wx; v.TorqueY[parent] -= wy; v.TorqueZ[parent] -= wz;
            }
        }

        /// <summary>The plan part a touch end is, or -1 when the body is not this ragdoll's (its stable id is outside the range, or its lane is not the part's).</summary>
        private int PartOf(int lane, ulong stableId)
        {
            var part = stableId - _firstStableId;
            return stableId >= _firstStableId && part < (ulong)_bodyLane.Length && _bodyLane[(int)part] == lane ? (int)part : -1;
        }

        private int CommonAncestor(int a, int b)
        {
            while (_depth[a] > _depth[b]) a = Plan.ParentOf(a);
            while (_depth[b] > _depth[a]) b = Plan.ParentOf(b);
            while (a != b) { a = Plan.ParentOf(a); b = Plan.ParentOf(b); }
            return a;
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
        /// (zeros in a world with no particle system), and the opening's drop in m: with d the normalised <paramref name="down"/>, how far the opening's lowest rim point
        /// lies below the deepest plan airway sump along d (positive below, negative above). The rim is the opening's circle of Radius in the plane across its outward normal
        /// (its part's rotation applied to the normalised offset), so a mouth turned to the side still has its half-width of drop. Drop comes from the body's pose and is written in
        /// every world. Refuses spans not one per opening and a zero <paramref name="down"/>. Allocates nothing.
        /// </summary>
        public void ReadAirway(Span<float> live, Span<float> pooled, Span<float> drop, (float X, float Y, float Z) down)
        {
            var n = _sensors.Length;
            if (live.Length != n || pooled.Length != n || drop.Length != n) throw new ArgumentException($"live, pooled and drop need {n} entries, one per airway opening.");
            var (dx, dy, dz) = Unit(down);
            var deepest = DeepestSump(dx, dy, dz);
            var v = _world.View;
            for (var k = 0; k < n; k++)
            {
                live[k] = _hasSensors ? v.SensorLiquidVolume[_sensors[k].Lane] : 0f;
                pooled[k] = _hasSensors ? v.SensorWetVolume[_sensors[k].Lane] : 0f;
                drop[k] = OpeningDrop(k, dx, dy, dz, deepest);
            }
        }

        /// <summary>
        /// Takes what a breath drew from the pool: <paramref name="drawnPooled"/> (Physiology's DrawnPooled, world m^3) split over the openings by each one's pooled reading at the last step's end
        /// (exact, because Physiology's weighted sum is linear in the openings), and removed from the wet patches each opening reads. Returns the summed volume that left the world's patches, m^3:
        /// 0 for a <paramref name="drawnPooled"/> of 0 or less, a world with no sensors or no pooled reading; float rounding can leave it below the ask. Readings change at the next step's end. Allocates nothing.
        /// </summary>
        public double TakeDrawn(double drawnPooled)
        {
            if (!_hasSensors || !(drawnPooled > 0d)) return 0d;
            var v = _world.View;
            var sum = 0d;
            for (var k = 0; k < _sensors.Length; k++) sum += v.SensorWetVolume[_sensors[k].Lane];
            if (!(sum > 0d)) return 0d;
            for (var k = 0; k < _sensors.Length; k++) _ask[k] = (float)(drawnPooled * v.SensorWetVolume[_sensors[k].Lane] / sum);
            _world.TakeWet(_sensors, _ask, _taken);
            var taken = 0d;
            for (var k = 0; k < _taken.Length; k++) taken += _taken[k];
            return taken;
        }

        /// <summary>The margin (m) by which another opening's rim must lie below the current lowest one's before <see cref="LowestOpening"/> changes its answer: face down the two rims are 0.2 mm apart and a few degrees of head pitch would swap them.</summary>
        public const float LowestHysteresis = 0.005f;

        /// <summary>
        /// The plan airway opening whose rim lies lowest along <paramref name="down"/> (the largest drop of <see cref="ReadAirway"/>, the first on a tie): where liquid leaves
        /// (ruling bds-drain-head-decides, the head's pose alone decides). The answer keeps the previous call's opening until another's drop exceeds that opening's by <see cref="LowestHysteresis"/>, so a head lying face down
        /// does not swap nose and mouth with every degree of pitch; the first call (or a ragdoll with no previous answer) takes the largest drop. Refuses a zero <paramref name="down"/>. Allocates nothing.
        /// </summary>
        public int LowestOpening((float X, float Y, float Z) down)
        {
            var (dx, dy, dz) = Unit(down);
            var deepest = DeepestSump(dx, dy, dz);
            var best = 0;
            var bestDrop = float.NegativeInfinity;
            var keptDrop = float.NegativeInfinity;
            for (var k = 0; k < _sensors.Length; k++)
            {
                var drop = OpeningDrop(k, dx, dy, dz, deepest);
                if (k == _lowest) keptDrop = drop;
                if (drop > bestDrop)
                {
                    bestDrop = drop;
                    best = k;
                }
            }

            if (_lowest >= 0 && bestDrop <= keptDrop + LowestHysteresis) best = _lowest;
            _lowest = best;
            return best;
        }

        private static (float X, float Y, float Z) Unit((float X, float Y, float Z) down)
        {
            var dl = MathF.Sqrt(down.X * down.X + down.Y * down.Y + down.Z * down.Z);
            if (!(dl > 0f) || !float.IsFinite(dl)) throw new ArgumentException("down needs a nonzero finite direction.", nameof(down));
            return (down.X / dl, down.Y / dl, down.Z / dl);
        }

        /// <summary>The deepest plan airway sump's position along the unit direction d.</summary>
        private float DeepestSump(float dx, float dy, float dz)
        {
            var v = _world.View;
            var deepest = float.NegativeInfinity;
            for (var m = 0; m < Plan.AirwaySumps.Count; m++)
            {
                var s = Plan.AirwaySumps[m];
                var i = _bodyLane[s.Part];
                new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]).Rotate(s.Offset.X, s.Offset.Y, s.Offset.Z, out var rx, out var ry, out var rz);
                deepest = MathF.Max(deepest, (v.PositionX[i] + rx) * dx + (v.PositionY[i] + ry) * dy + (v.PositionZ[i] + rz) * dz);
            }

            return deepest;
        }

        /// <summary>Opening k's lowest rim point's drop below <paramref name="deepest"/> along the unit direction d.</summary>
        private float OpeningDrop(int k, float dx, float dy, float dz, float deepest)
        {
            var v = _world.View;
            var a = Plan.AirwayOpenings[k];
            var i = _bodyLane[a.Part];
            var o = a.Offset;
            var q = new Quat(v.RotationX[i], v.RotationY[i], v.RotationZ[i], v.RotationW[i]);
            q.Rotate(o.X, o.Y, o.Z, out var cx, out var cy, out var cz);
            var centre = (v.PositionX[i] + cx) * dx + (v.PositionY[i] + cy) * dy + (v.PositionZ[i] + cz) * dz;
            var ol = MathF.Sqrt(o.X * o.X + o.Y * o.Y + o.Z * o.Z);
            var rim = a.Radius;
            if (ol > 0f)
            {
                q.Rotate(o.X / ol, o.Y / ol, o.Z / ol, out var nx, out var ny, out var nz);
                var along = dx * nx + dy * ny + dz * nz;
                var px = dx - along * nx;
                var py = dy - along * ny;
                var pz = dz - along * nz;
                rim *= MathF.Sqrt(px * px + py * py + pz * pz);
            }

            return centre + rim - deepest;
        }
    }
}
