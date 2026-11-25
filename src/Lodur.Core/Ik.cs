using System;

namespace Lodur
{
    /// <summary>A plain 3-vector for the solvers; a value type, so solving allocates nothing.</summary>
    internal readonly struct IkVec
    {
        public readonly float X, Y, Z;

        public IkVec(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        public IkVec((float X, float Y, float Z) v)
        {
            X = v.X; Y = v.Y; Z = v.Z;
        }

        public static IkVec operator +(IkVec a, IkVec b) => new IkVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static IkVec operator -(IkVec a, IkVec b) => new IkVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static IkVec operator *(IkVec a, float s) => new IkVec(a.X * s, a.Y * s, a.Z * s);

        public float Dot(IkVec b) => X * b.X + Y * b.Y + Z * b.Z;
        public IkVec Cross(IkVec b) => new IkVec(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public float Length => MathF.Sqrt(Dot(this));

        public IkVec Unit()
        {
            var l = Length;
            return l > 0f ? this * (1f / l) : this;
        }

        public IkVec Rotated(in Quat q)
        {
            q.Rotate(X, Y, Z, out var x, out var y, out var z);
            return new IkVec(x, y, z);
        }
    }

    /// <summary>
    /// Inverse kinematics as a producer of joint target rotations: a world point in, target rotations out, in the convention of the
    /// body interface. It decides nothing about when to reach or how hard, and it reads the plan, never writes it.
    /// <para>
    /// Convention: a joint's frame F is the shortest-arc rotation taking +z to the joint's axis (the plan's creation-pose axis). A target q
    /// means the child's rotation relative to the parent, from the creation pose, is F q F^-1 in the parent's creation-pose frame. Twist is
    /// about the frame's z, swing about its x and y. A spherical joint's Lower and Upper bound twist and Cone bounds swing; a hinge's Lower
    /// and Upper bound its angle about frame z.
    /// </para>
    /// </summary>
    public static class Ik
    {
        /// <summary>The joint frame F of <paramref name="joint"/>: the shortest arc from +z to the unit joint axis.</summary>
        public static Quat JointFrame(in JointDef joint) => Arc(new IkVec(0f, 0f, 1f), new IkVec(joint.Axis).Unit());

        /// <summary>The shortest-arc rotation taking unit vector a onto unit vector b.</summary>
        internal static Quat Arc(IkVec a, IkVec b)
        {
            var dot = a.Dot(b);
            if (dot < -1f + 1e-6f)
            {
                // Opposite: a half turn about any axis perpendicular to a.
                var p = a.Cross(MathF.Abs(a.X) < 0.9f ? new IkVec(1f, 0f, 0f) : new IkVec(0f, 1f, 0f)).Unit();
                return new Quat(p.X, p.Y, p.Z, 0f);
            }

            var c = a.Cross(b);
            var w = 1f + dot;
            var inv = 1f / MathF.Sqrt(c.Dot(c) + w * w);
            return new Quat(c.X * inv, c.Y * inv, c.Z * inv, w * inv);
        }

        internal static Quat About(IkVec axis, float radians) => Quat.FromAxisAngle(axis.X, axis.Y, axis.Z, radians);

        internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>The target in the parent part's creation-pose frame: where it sits if the parent were still at its creation pose.</summary>
        internal static IkVec ToCreationFrame(in Quat parentRotation, in Quat parentRestInverse, IkVec parentCentre,
            float px, float py, float pz, float tx, float ty, float tz)
        {
            var rel = parentRotation * parentRestInverse;
            return parentCentre + new IkVec(tx - px, ty - py, tz - pz).Rotated(rel.Conjugate());
        }
    }

    /// <summary>
    /// A two-bone arm: shoulder (spherical), elbow (revolute) and wrist anchors of one side, found by label in the plan. Solves a world
    /// point for the wrist anchor with zero shoulder twist: the elbow flex is fixed by the distance, and the swivel about the shoulder-to-wrist
    /// line is the one that leaves the shoulder target no twist.
    /// </summary>
    public sealed class ArmChain
    {
        private readonly Quat _parentRestInverse, _frame, _frameInverse;
        private readonly IkVec _parentCentre, _shoulderAnchor, _upper, _lower, _elbowAxis, _twistAxis;
        private readonly float _flexSign, _flexLo, _flexHi, _cone;

        private ArmChain(BodyPlan plan, Side side)
        {
            var si = plan.JointIndex("Shoulder", side);
            var ei = plan.JointIndex("Elbow", side);
            var wi = plan.JointIndex("Wrist", side);
            var s = plan.Joints[si];
            var e = plan.Joints[ei];
            var w = plan.Joints[wi];
            if (s.Kind != JointKind.Spherical || e.Kind != JointKind.Revolute || w.Parent != e.Child || e.Parent != s.Child)
            {
                throw new ArgumentException($"the {side} arm is not a spherical shoulder, a revolute elbow and a wrist on the forearm.");
            }

            ShoulderJoint = si;
            ElbowJoint = ei;
            ParentPart = s.Parent;
            var shoulder = new IkVec(s.Anchor);
            var elbow = new IkVec(e.Anchor);
            var wrist = new IkVec(w.Anchor);
            _upper = elbow - shoulder;
            _lower = wrist - elbow;
            L1 = _upper.Length;
            L2 = _lower.Length;
            _elbowAxis = new IkVec(e.Axis).Unit();
            _twistAxis = new IkVec(s.Axis).Unit();
            // The closed form needs the creation pose straight and the hinge across both bones.
            if (L1 < 1e-4f || L2 < 1e-4f || _upper.Cross(_lower).Length > 1e-3f * L1 * L2 || MathF.Abs(_elbowAxis.Dot(_lower)) > 1e-3f * L2)
            {
                throw new ArgumentException($"the {side} arm is not straight at its creation pose with the elbow hinge across it.");
            }

            // The hinge bends to one side of zero; a range straddling zero would make the flex ambiguous.
            if (e.Lower < 0f && e.Upper <= 0f) _flexSign = -1f;
            else if (e.Lower >= 0f && e.Upper > 0f) _flexSign = 1f;
            else throw new ArgumentException($"the {side} elbow range [{e.Lower}, {e.Upper}] does not lie on one side of zero.");
            _flexLo = e.Lower;
            _flexHi = e.Upper;
            _cone = s.Cone;
            _shoulderAnchor = shoulder;
            _frame = Ik.JointFrame(s);
            _frameInverse = _frame.Conjugate();
            var parent = plan.Parts[ParentPart];
            _parentCentre = new IkVec(parent.Position);
            _parentRestInverse = parent.Rotation.Conjugate();
            ShoulderAnchor = s.Anchor;
            ElbowAnchor = e.Anchor;
            WristAnchor = w.Anchor;
        }

        /// <summary>Finds the shoulder, elbow and wrist of <paramref name="side"/> by label; <see cref="ArgumentException"/> if the plan has no such arm.</summary>
        public static ArmChain Of(BodyPlan plan, Side side) => new ArmChain(plan, side);

        /// <summary>The plan's joint ordinals, where the caller writes the two targets.</summary>
        public int ShoulderJoint { get; }
        public int ElbowJoint { get; }

        /// <summary>The part the shoulder hangs from; Solve takes its world rotation and position.</summary>
        public int ParentPart { get; }

        /// <summary>Creation-pose anchors. The effector is <see cref="WristAnchor"/>.</summary>
        public (float X, float Y, float Z) ShoulderAnchor { get; }
        public (float X, float Y, float Z) ElbowAnchor { get; }
        public (float X, float Y, float Z) WristAnchor { get; }

        /// <summary>Shoulder to elbow anchor, and elbow to wrist anchor, in metres.</summary>
        public float L1 { get; }
        public float L2 { get; }

        /// <summary>
        /// Targets that put the wrist anchor on the world point (tx, ty, tz), given the parent part's world rotation and position.
        /// Returns false when the point was out of reach or a limit clamped; the targets are then the nearest the chain can go.
        /// </summary>
        public bool Solve(in Quat parentRotation, float px, float py, float pz, float tx, float ty, float tz, out Quat shoulder, out Quat elbow)
        {
            var reached = true;
            var v = Ik.ToCreationFrame(parentRotation, _parentRestInverse, _parentCentre, px, py, pz, tx, ty, tz) - _shoulderAnchor;
            var d = v.Length;
            var dMin = MathF.Abs(L1 - L2) + 1e-3f;
            var dMax = L1 + L2 - 1e-3f;
            var dc = Ik.Clamp(d, dMin, dMax);
            if (dc != d) reached = false;
            var vHat = d > 1e-6f ? v * (1f / d) : _upper.Unit();

            var cosFlex = Ik.Clamp((dc * dc - L1 * L1 - L2 * L2) / (2f * L1 * L2), -1f, 1f);
            var flexWant = _flexSign * MathF.Acos(cosFlex);
            var flex = Ik.Clamp(flexWant, _flexLo, _flexHi);
            if (flex != flexWant) reached = false;

            // The hand offset at this flex, in the upper arm's creation frame, and the swing taking it onto the target direction.
            var p = _upper + _lower.Rotated(Ik.About(_elbowAxis, flex));
            var arc = Ik.Arc(p.Unit(), vHat);
            var q = new IkVec(arc.X, arc.Y, arc.Z);
            var num = -q.Dot(_twistAxis);
            var den = arc.W * vHat.Dot(_twistAxis) + vHat.Cross(q).Dot(_twistAxis);
            var swing = Ik.About(vHat, 2f * MathF.Atan2(num, den)) * arc;

            var local = _frameInverse * swing * _frame;
            if (local.W < 0f) local = new Quat(-local.X, -local.Y, -local.Z, -local.W);
            var vecLen = MathF.Sqrt(local.X * local.X + local.Y * local.Y + local.Z * local.Z);
            var angle = 2f * MathF.Atan2(vecLen, local.W);
            if (angle > _cone)
            {
                local = Quat.FromAxisAngle(local.X / vecLen, local.Y / vecLen, local.Z / vecLen, _cone);
                reached = false;
            }

            shoulder = local;
            elbow = Quat.FromAxisAngle(0f, 0f, 1f, flex);
            return reached;
        }
    }

    /// <summary>
    /// A head that looks: one spherical joint (the neck by default) turned so the child part's creation-pose forward, +z, points at a world
    /// point. Yaw about the joint axis is the joint's twist and is clamped to [Lower, Upper]; pitch about the creation-pose left axis is swing and
    /// is clamped to Cone.
    /// </summary>
    public sealed class LookChain
    {
        private readonly Quat _parentRestInverse, _frame, _frameInverse;
        private readonly IkVec _parentCentre, _childCentre, _up, _forward, _left;
        private readonly float _lower, _upper, _cone;

        private LookChain(BodyPlan plan, string joint)
        {
            var ji = plan.JointIndex(joint);
            var j = plan.Joints[ji];
            if (j.Kind != JointKind.Spherical) throw new ArgumentException($"joint '{joint}' is not spherical.");
            Joint = ji;
            ParentPart = j.Parent;
            ChildPart = j.Child;
            _up = new IkVec(j.Axis).Unit();
            var z = new IkVec(0f, 0f, 1f);
            var forward = z - _up * z.Dot(_up);
            if (forward.Length < 1e-3f) throw new ArgumentException($"joint '{joint}' has an axis along forward, so it has no yaw.");
            _forward = forward.Unit();
            _left = _up.Cross(_forward);
            _lower = j.Lower;
            _upper = j.Upper;
            _cone = j.Cone;
            _frame = Ik.JointFrame(j);
            _frameInverse = _frame.Conjugate();
            var parent = plan.Parts[j.Parent];
            _parentCentre = new IkVec(parent.Position);
            _parentRestInverse = parent.Rotation.Conjugate();
            _childCentre = new IkVec(plan.Parts[j.Child].Position);
        }

        public static LookChain Of(BodyPlan plan, string joint = "Neck") => new LookChain(plan, joint);

        /// <summary>The plan's joint ordinal, where the caller writes the target.</summary>
        public int Joint { get; }
        public int ParentPart { get; }
        public int ChildPart { get; }

        /// <summary>
        /// The target that points the child part at the world point: the direction runs from the child's creation-pose centre to the point,
        /// in the parent's frame. Returns false when yaw or pitch was clamped.
        /// </summary>
        public bool Solve(in Quat parentRotation, float px, float py, float pz, float tx, float ty, float tz, out Quat neck)
        {
            var reached = true;
            var dir = Ik.ToCreationFrame(parentRotation, _parentRestInverse, _parentCentre, px, py, pz, tx, ty, tz) - _childCentre;
            var len = dir.Length;
            var d = len > 1e-6f ? dir * (1f / len) : _forward;
            var yawWant = MathF.Atan2(d.Dot(_left), d.Dot(_forward));
            var pitchWant = -MathF.Asin(Ik.Clamp(d.Dot(_up), -1f, 1f));
            var yaw = Ik.Clamp(yawWant, _lower, _upper);
            var pitch = Ik.Clamp(pitchWant, -_cone, _cone);
            if (yaw != yawWant || pitch != pitchWant) reached = false;
            neck = _frameInverse * (Ik.About(_up, yaw) * Ik.About(_left, pitch)) * _frame;
            return reached;
        }
    }
}
