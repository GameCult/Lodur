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
    /// point for the wrist anchor: the elbow flex is fixed by the distance, and the swivel about the shoulder-to-wrist line is the one that
    /// leaves the shoulder target no twist, the shoulder's twist muscle being weak. When that swivel puts the swing past the cone, the swivel
    /// is the one nearest it whose swing is inside the cone and whose twist is inside the twist limits (the joint-limit-feasible swivel range of
    /// Tolani, Goswami and Badler 2000). When no swivel is feasible, the swivel is the one whose limit violation (swing past the cone plus
    /// twist past its limits) is least, and its swing and twist clamp to the limits; as a goal leaves the feasible set that swivel is the
    /// last feasible one, so the targets move continuously across the edge.
    /// <para>
    /// That rule alone is the stateless rule, and mirrored options tie under it: where two swivels are equally good it must jump from one to
    /// the other somewhere on the tie set. So a chain is one arm's solver across ticks and carries the arm's previous swivel (ruling
    /// lodur:ruling:ik-swivel-temporal-coherence): each solve stays in the basin of the previous swivel and moves to the stateless choice only
    /// when that is better by <see cref="BasinMargin"/>, never turning the swivel faster than <see cref="SwivelRate"/>. Better means less limit
    /// violation, or, both swivels being feasible, less twist. The first solve after construction or <see cref="Reset"/> is the stateless rule.
    /// Identical goals can therefore give different targets depending on history; the same history gives the same targets.
    /// </para>
    /// <para>
    /// The solved targets can still jump where the solution is discontinuous in the goal (the clamp's swing-twist split at a half-turn swing,
    /// a basin change, a goal that leaps). So the targets the chain returns move toward the solved ones no faster than
    /// <see cref="MaxAngularRate"/>: per solve the shoulder target turns, and the elbow target flexes, by at most MaxAngularRate times the
    /// tick (ruling lodur:ruling:ik-output-slew-limit). While the goal holds still they converge on the solution. A goal that leaps is
    /// followed at that rate, not at once. The first solve after construction or <see cref="Reset"/> returns the solution itself.
    /// </para>
    /// </summary>
    public sealed class ArmChain
    {
        private readonly Quat _parentRestInverse, _frame, _frameInverse;
        private readonly IkVec _parentCentre, _shoulderAnchor, _upper, _lower, _elbowAxis, _twistAxis;
        private readonly float _flexSign, _flexLo, _flexHi, _cone, _twistLo, _twistHi, _coneSin2, _coneSinHalf, _coneCosHalf;
        private readonly float _maxSwivelStep, _maxStep, _cosHalfMaxStep, _sinHalfMaxStep;

        // The previous solve's shoulder target before clamping, whose swivel the next solve starts from; none after construction or Reset.
        // The targets the previous solve returned, which the next one slews from, share the flag.
        private Quat _previous, _shoulderOut;
        private float _flexOut;
        private bool _hasPrevious, _hasOutput;

        private enum Hand : byte { Idle, Driving, Releasing }

        // The hand-off and the hand path. The path is in the arm parent's creation frame; its time counts the ticks driven since it started.
        private Hand _hand;
        private double _weight;
        private bool _newPath, _pathBlocked, _hasSegGoal;
        private IkVec _pathStart, _pathVia, _segGoal, _aim;
        private int _pathTicks;
        private readonly float _dt;
        private readonly IkVec[] _trunkCentre, _trunkHalf;
        private readonly float _trunkFront, _forearmRadius;

        /// <summary>
        /// Radians of limit violation, or of twist when both swivels are feasible, by which the stateless choice must beat the held swivel's
        /// basin before the arm leaves it. Placeholder: no measured pose cost sets it. It must sit far above the solver's resolution (about
        /// 1e-5 rad), so noise cannot flip a tie, and well below the 1.0-1.57 rad between the mirrored basins Soul measured, so a clearly
        /// better basin is taken.
        /// </summary>
        public const double BasinMargin = 0.05;

        /// <summary>
        /// The fastest the swivel turns, in radians per second. Placeholder: of the order of a deliberate, unhurried shoulder rotation, not a
        /// measured one.
        /// </summary>
        public const float SwivelRate = 3f;

        /// <summary>
        /// The fastest the returned targets move, in radians per second: the shoulder target's turn and the elbow target's flex per solve are at
        /// most this times the tick. Placeholder: near human peak arm angular speed, not measured for this body.
        /// </summary>
        public const float MaxAngularRate = 10f;

        /// <summary>
        /// Metres the via point sits ahead of the trunk's front. Placeholder: the probe (goal-ai-map fact 25) measured 0.15 to 0.3 m clearing the
        /// seated far arm; no pose cost sets it.
        /// </summary>
        public const float ViaClearance = 0.2f;

        /// <summary>Seconds of each of the path's two segments, wrist to via point and via point to goal. Placeholder: a deliberate reach, not measured (fact 25).</summary>
        public const float PathSegmentSeconds = 0.3f;

        // Swivel search: steps of Pi / SwivelSteps outward from the zero-twist swivel, then bisection to the feasible boundary. When none is
        // feasible, the least-violating of LeastSteps steps round the circle, refined by golden section.
        private const int SwivelSteps = 90;
        private const int SwivelBisections = 16;
        private const int LeastSteps = 36;
        private const int LeastRefinements = 28;

        // Among swivels that violate the limits equally (the arm along the twist axis, where the swivel only twists) the least twist wins.
        // Small against the violation's slope of about one, so it barely moves the least-violating swivel.
        private const double TwistPreference = 1e-5;

        private ArmChain(BodyPlan plan, Side side, double tickSeconds)
        {
            if (!(double.IsFinite(tickSeconds) && tickSeconds > 0.0))
            {
                throw new ArgumentException($"the tick length {tickSeconds} s is not finite and positive.");
            }

            _dt = (float)tickSeconds;
            _maxSwivelStep = (float)(SwivelRate * tickSeconds);
            _maxStep = (float)(MaxAngularRate * tickSeconds);
            _cosHalfMaxStep = MathF.Cos(0.5f * _maxStep);
            _sinHalfMaxStep = MathF.Sin(0.5f * _maxStep);
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
            _twistLo = s.Lower;
            _twistHi = s.Upper;
            _coneSinHalf = MathF.Sin(0.5f * _cone);
            _coneCosHalf = MathF.Cos(0.5f * _cone);
            _coneSin2 = _coneSinHalf * _coneSinHalf;
            _shoulderAnchor = shoulder;
            _frame = Ik.JointFrame(s);
            _frameInverse = _frame.Conjugate();
            var parent = plan.Parts[ParentPart];
            _parentCentre = new IkVec(parent.Position);
            _parentRestInverse = parent.Rotation.Conjugate();
            ShoulderAnchor = s.Anchor;
            ElbowAnchor = e.Anchor;
            WristAnchor = w.Anchor;
            ForearmPart = e.Child;

            // The trunk: the shoulder's parent part and its ancestors, each as its creation-pose axis-aligned bound.
            var trunk = 0;
            for (var part = s.Parent; part >= 0; part = plan.ParentOf(part)) trunk++;
            _trunkCentre = new IkVec[trunk];
            _trunkHalf = new IkVec[trunk];
            _trunkFront = float.NegativeInfinity;
            trunk = 0;
            for (var part = s.Parent; part >= 0; part = plan.ParentOf(part))
            {
                var def = plan.Parts[part];
                _trunkCentre[trunk] = new IkVec(def.Position);
                _trunkHalf[trunk] = Half(def);
                _trunkFront = MathF.Max(_trunkFront, def.Position.Z + _trunkHalf[trunk].Z);
                trunk++;
            }

            var fore = plan.Parts[e.Child];
            _forearmRadius = fore.Shape == PartShape.Box ? MathF.Min(fore.Size.X, MathF.Min(fore.Size.Y, fore.Size.Z)) : fore.Size.X;
            _newPath = true;
        }

        // The half extents of a part's creation-pose axis-aligned bound.
        private static IkVec Half(in PartDef part)
        {
            var q = part.Rotation;
            var (sx, sy, sz) = part.Size;
            q.Rotate(1f, 0f, 0f, out var xx, out var xy, out var xz);
            q.Rotate(0f, 1f, 0f, out var yx, out var yy, out var yz);
            q.Rotate(0f, 0f, 1f, out var zx, out var zy, out var zz);
            switch (part.Shape)
            {
                case PartShape.Box:
                    return new IkVec(MathF.Abs(xx) * sx + MathF.Abs(yx) * sy + MathF.Abs(zx) * sz,
                        MathF.Abs(xy) * sx + MathF.Abs(yy) * sy + MathF.Abs(zy) * sz,
                        MathF.Abs(xz) * sx + MathF.Abs(yz) * sy + MathF.Abs(zz) * sz);
                case PartShape.Capsule:
                    // Radius sx, half length sy along the part's y.
                    return new IkVec(MathF.Abs(yx) * sy + sx, MathF.Abs(yy) * sy + sx, MathF.Abs(yz) * sy + sx);
                default:
                    return new IkVec(sx, sx, sx);
            }
        }

        /// <summary>Finds the shoulder, elbow and wrist of <paramref name="side"/> by label; <see cref="ArgumentException"/> if the plan has no such arm.</summary>
        /// <remarks>One chain is one arm's solver across ticks of <paramref name="tickSeconds"/>, which sets how far the swivel turns per solve.</remarks>
        public static ArmChain Of(BodyPlan plan, Side side, double tickSeconds) => new ArmChain(plan, side, tickSeconds);

        /// <summary>
        /// Forgets the previous swivel, the returned targets, the hand path and the hand-off: the next <see cref="Solve"/> uses the stateless rule
        /// and returns its solution unslewed, and the arm is idle, so <see cref="Release"/> passes the base through and <see cref="Drive"/> engages
        /// from it.
        /// </summary>
        public void Reset()
        {
            _hasPrevious = false;
            _hasOutput = false;
            _hand = Hand.Idle;
            _newPath = true;
        }

        /// <summary>The next <see cref="Drive"/> starts a new hand path from the measured wrist, keeping the swivel and the returned targets.</summary>
        public void Restart() => _newPath = true;

        /// <summary>The last solve's shoulder target before clamping, whose swivel the next solve holds (identity before any solve); tests read it.</summary>
        internal Quat Held => _hasPrevious ? _previous : Quat.Identity;

        /// <summary>The plan's joint ordinals, where the caller writes the two targets.</summary>
        public int ShoulderJoint { get; }
        public int ElbowJoint { get; }

        /// <summary>The part the shoulder hangs from; Solve takes its world rotation and position.</summary>
        public int ParentPart { get; }

        /// <summary>The forearm part, which carries the wrist anchor; Drive takes the measured wrist from its pose.</summary>
        public int ForearmPart { get; }

        /// <summary>Creation-pose anchors. The effector is <see cref="WristAnchor"/>.</summary>
        public (float X, float Y, float Z) ShoulderAnchor { get; }
        public (float X, float Y, float Z) ElbowAnchor { get; }
        public (float X, float Y, float Z) WristAnchor { get; }

        /// <summary>Shoulder to elbow anchor, and elbow to wrist anchor, in metres.</summary>
        public float L1 { get; }
        public float L2 { get; }

        /// <summary>
        /// Targets that put the wrist anchor on the world point (tx, ty, tz), given the parent part's world rotation and position.
        /// Returns false when the point was out of reach or a limit clamped; the targets are then the limits-clamped pose of the swivel that
        /// violates the limits least, continuous with the reached targets at the edge of the feasible set, or, while the swivel turns between
        /// basins, the clamped pose of the swivel on its way. The returned targets are slewed toward that solution at
        /// <see cref="MaxAngularRate"/>; the return value describes the solution. One call is one tick of the arm's history.
        /// </summary>
        public bool Solve(in Quat parentRotation, float px, float py, float pz, float tx, float ty, float tz, out Quat shoulder, out Quat elbow) =>
            SolveCreation(Ik.ToCreationFrame(parentRotation, _parentRestInverse, _parentCentre, px, py, pz, tx, ty, tz), out shoulder, out elbow);

        // Solve for a wrist point already in the parent's creation frame.
        private bool SolveCreation(IkVec target, out Quat shoulder, out Quat elbow)
        {
            var reached = true;
            var v = target - _shoulderAnchor;
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
            var zeroTwist = 2f * MathF.Atan2(num, den);
            // The target is linear in the half-angle of the swivel: Local(phi) = cos(phi / 2) A + sin(phi / 2) B.
            var a = _frameInverse * arc * _frame;
            var b = _frameInverse * (new Quat(vHat.X, vHat.Y, vHat.Z, 0f) * arc) * _frame;
            var phi = Stateless(a, b, zeroTwist, out var clamp);
            if (_hasPrevious)
            {
                // The previous target's swivel on this goal's swivel circle: a and b are orthonormal, so the nearest point is a projection.
                var held = 2f * MathF.Atan2(Dot(_previous, b), Dot(_previous, a));
                var basin = BasinOptimum(a, b, zeroTwist, held);
                var want = ClearlyBetter(a, b, phi, basin) ? phi : basin;
                phi = held + Ik.Clamp(Wrap(want - held), -_maxSwivelStep, _maxSwivelStep);
                clamp = !Feasible(Local(a, b, phi));
            }

            var local = Local(a, b, phi);
            var slew = _hasOutput;
            _previous = local;
            _hasPrevious = true;
            _hasOutput = true;
            if (clamp)
            {
                // Infeasible: swing and twist clamped to the limits.
                local = ClampToLimits(local);
                reached = false;
            }

            if (slew)
            {
                _shoulderOut = Toward(_shoulderOut, local);
                _flexOut += Ik.Clamp(flex - _flexOut, -_maxStep, _maxStep);
            }
            else
            {
                (_shoulderOut, _flexOut) = (local, flex);
            }

            shoulder = _shoulderOut;
            elbow = Quat.FromAxisAngle(0f, 0f, 1f, _flexOut);
            return reached;
        }

        /// <summary>
        /// One tick the arm's layer drives: the targets to write for the wrist goal (tx, ty, tz) given the arm parent's world pose, the wrist
        /// measured now (mx, my, mz) and the base shoulder and elbow targets, blended toward the solution by <paramref name="weight"/> in [0, 1].
        /// An arm that was idle starts slewing from the base targets; one that was releasing, from where its output was. The tick that engages the
        /// arm, and every tick after <see cref="Restart"/>, starts a hand path from the measured wrist. Returns whether the goal was reached,
        /// as <see cref="Solve"/> does for the point it was given.
        /// </summary>
        public bool Drive(in Quat parentRotation, float px, float py, float pz, float tx, float ty, float tz, float mx, float my, float mz,
            in Quat baseShoulder, in Quat baseElbow, double weight, out Quat shoulder, out Quat elbow)
        {
            if (_hand != Hand.Driving)
            {
                if (_hand == Hand.Idle)
                {
                    _shoulderOut = baseShoulder;
                    _flexOut = Flex(baseElbow);
                    _hasOutput = true;
                }

                _hand = Hand.Driving;
                _newPath = true;
            }

            _weight = weight;
            var goal = Ik.ToCreationFrame(parentRotation, _parentRestInverse, _parentCentre, px, py, pz, tx, ty, tz);
            if (_newPath)
            {
                _newPath = false;
                var wrist = Ik.ToCreationFrame(parentRotation, _parentRestInverse, _parentCentre, px, py, pz, mx, my, mz);
                _pathStart = wrist;
                _pathBlocked = Blocked(wrist, goal);
                _pathVia = new IkVec(wrist.X, 0.5f * (wrist.Y + goal.Y), _trunkFront + ViaClearance);
                _pathTicks = 0;
                _hasSegGoal = false;
            }

            var aim = goal;
            if (_pathBlocked && _pathTicks * _dt < 2f * PathSegmentSeconds)
            {
                var t = _pathTicks * _dt;
                if (!_hasSegGoal && t >= PathSegmentSeconds)
                {
                    _segGoal = goal;
                    _hasSegGoal = true;
                }

                aim = PathPoint(_pathStart, _pathVia, _segGoal, goal, t);
                _pathTicks++;
            }

            _aim = aim;
            var reached = SolveCreation(aim, out var s, out var e);
            shoulder = Blend(baseShoulder, s, weight);
            elbow = Blend(baseElbow, e, weight);
            return reached;
        }

        /// <summary>
        /// One tick the arm's layer does not drive: while the arm still holds a driven output, it slews to the base targets at
        /// <see cref="MaxAngularRate"/>, written blended toward the base by the weight it was last driven at; when it has arrived, and for an arm
        /// that never was driven, the written targets are the base ones, untouched.
        /// </summary>
        public void Release(in Quat baseShoulder, in Quat baseElbow, out Quat shoulder, out Quat elbow)
        {
            if (_hand == Hand.Idle)
            {
                shoulder = baseShoulder;
                elbow = baseElbow;
                return;
            }

            _hand = Hand.Releasing;
            _shoulderOut = Toward(_shoulderOut, baseShoulder);
            var flex = Flex(baseElbow);
            var gap = flex - _flexOut;
            _flexOut = MathF.Abs(gap) <= _maxStep ? flex : _flexOut + (gap > 0f ? _maxStep : -_maxStep);
            if (_flexOut == flex && _shoulderOut.X == baseShoulder.X && _shoulderOut.Y == baseShoulder.Y && _shoulderOut.Z == baseShoulder.Z
                && _shoulderOut.W == baseShoulder.W)
            {
                _hand = Hand.Idle;
                shoulder = baseShoulder;
                elbow = baseElbow;
                return;
            }

            shoulder = Blend(baseShoulder, _shoulderOut, _weight);
            elbow = Blend(baseElbow, Quat.FromAxisAngle(0f, 0f, 1f, _flexOut), _weight);
        }

        // The solution itself at weight 1, a blend toward it from the base below that.
        private static Quat Blend(in Quat baseTarget, in Quat solution, double weight) =>
            weight >= 1.0 ? solution : Quat.Nlerp(baseTarget, solution, (float)weight);

        // The flex of an elbow target, a rotation about the hinge's frame z, on the shortest turn.
        private static float Flex(in Quat elbow) => 2f * MathF.Atan2(elbow.W < 0f ? -elbow.Z : elbow.Z, MathF.Abs(elbow.W));

        // Minimum jerk (Flash and Hogan 1985): 0 to 1 with zero velocity and acceleration at both ends.
        internal static float MinimumJerk(float t)
        {
            t = Ik.Clamp(t, 0f, 1f);
            return t * t * t * (10f - 15f * t + 6f * t * t);
        }

        /// <summary>
        /// The hand path in the arm parent's creation frame at <paramref name="t"/> seconds: the start to the via point over
        /// <see cref="PathSegmentSeconds"/>, then the goal plus the via point's offset from the goal as it was when the segment began, shrinking
        /// to nothing, so a goal that moves is met at the end. <paramref name="segmentGoal"/> is the goal at that moment.
        /// </summary>
        internal static (float X, float Y, float Z) PathPoint((float X, float Y, float Z) start, (float X, float Y, float Z) via,
            (float X, float Y, float Z) segmentGoal, (float X, float Y, float Z) goal, float t)
        {
            var p = PathPoint(new IkVec(start), new IkVec(via), new IkVec(segmentGoal), new IkVec(goal), t);
            return (p.X, p.Y, p.Z);
        }

        private static IkVec PathPoint(IkVec start, IkVec via, IkVec segmentGoal, IkVec goal, float t)
        {
            if (t < PathSegmentSeconds) return start + (via - start) * MinimumJerk(t / PathSegmentSeconds);
            return goal + (via - segmentGoal) * (1f - MinimumJerk((t - PathSegmentSeconds) / PathSegmentSeconds));
        }

        /// <summary>Whether the last path start found the trunk in the way (tests read it).</summary>
        internal bool PathBlocked => _pathBlocked;

        /// <summary>The creation-frame point the last <see cref="Drive"/> solved for: the path point, or the goal (tests read it).</summary>
        internal (float X, float Y, float Z) Aim => (_aim.X, _aim.Y, _aim.Z);

        // Whether the straight segment from the wrist to the goal passes within the forearm radius of a trunk bound.
        private bool Blocked(IkVec wrist, IkVec goal)
        {
            for (var b = 0; b < _trunkCentre.Length; b++)
            {
                // The distance to a box is convex along a line, so a ternary search finds its least over the segment.
                float lo = 0f, hi = 1f;
                for (var i = 0; i < 40; i++)
                {
                    var m1 = lo + (hi - lo) / 3f;
                    var m2 = hi - (hi - lo) / 3f;
                    if (BoxDistance(wrist + (goal - wrist) * m1, b) < BoxDistance(wrist + (goal - wrist) * m2, b)) hi = m2;
                    else lo = m1;
                }

                if (BoxDistance(wrist + (goal - wrist) * (0.5f * (lo + hi)), b) < _forearmRadius) return true;
            }

            return false;
        }

        private float BoxDistance(IkVec p, int box)
        {
            var d = p - _trunkCentre[box];
            var h = _trunkHalf[box];
            var x = MathF.Max(0f, MathF.Abs(d.X) - h.X);
            var y = MathF.Max(0f, MathF.Abs(d.Y) - h.Y);
            var z = MathF.Max(0f, MathF.Abs(d.Z) - h.Z);
            return MathF.Sqrt(x * x + y * y + z * z);
        }

        // The rotation from current toward target, turned by at most the per-solve step: the target itself when it is within the step.
        private Quat Toward(in Quat current, in Quat target)
        {
            var d = current.Conjugate() * target;
            var s = d.W < 0f ? -1f : 1f;
            var (x, y, z, w) = (s * d.X, s * d.Y, s * d.Z, s * d.W);
            var sinHalf = MathF.Sqrt(x * x + y * y + z * z);
            if (2f * MathF.Atan2(sinHalf, w) <= _maxStep) return target;
            var k = _sinHalfMaxStep / sinHalf;
            // Renormalised, so a goal outrunning the rate for many ticks does not drift the target off the unit sphere.
            var r = current * new Quat(x * k, y * k, z * k, _cosHalfMaxStep);
            var n = 1f / MathF.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W);
            return new Quat(r.X * n, r.Y * n, r.Z * n, r.W * n);
        }

        // The stateless rule: zero twist inside the cone, else the nearest feasible swivel, else the least-violating one (clamp set).
        private float Stateless(in Quat a, in Quat b, float zeroTwist, out bool clamp)
        {
            clamp = false;
            if (InsideCone(Local(a, b, zeroTwist))) return zeroTwist;
            if (SomeSwivelInsideCone(a, b) && NearestFeasible(a, b, zeroTwist, out var feasible)) return feasible;
            clamp = true;
            return LeastViolating(a, b, zeroTwist);
        }

        // The best swivel of the held swivel's basin. When some swivel is feasible: from the held swivel, or the feasible one nearest it,
        // toward zero twist while feasible. When none is: the least violation downhill from the held swivel.
        private float BasinOptimum(in Quat a, in Quat b, float zeroTwist, float held)
        {
            if (SomeSwivelInsideCone(a, b))
            {
                if (Feasible(Local(a, b, held))) return TowardZeroTwist(a, b, zeroTwist, held);
                if (NearestFeasible(a, b, held, out var start)) return TowardZeroTwist(a, b, zeroTwist, start);
            }

            return LocalLeastViolating(a, b, held);
        }

        // From the feasible swivel start toward the zero-twist swivel, the way twist shrinks: the zero-twist swivel if every step there is
        // feasible, else the feasible boundary on the way.
        private float TowardZeroTwist(in Quat a, in Quat b, float zeroTwist, float start)
        {
            var d = Wrap(start - zeroTwist);
            if (d == 0f) return zeroTwist;
            var dir = d > 0f ? -1f : 1f;
            var dist = MathF.Abs(d);
            const float step = MathF.PI / SwivelSteps;
            var inside = start;
            for (var k = 1; k <= SwivelSteps; k++)
            {
                var t = MathF.Min(k * step, dist);
                var phi = start + dir * t;
                if (!Feasible(Local(a, b, phi))) return Boundary(a, b, inside, phi);
                if (t >= dist) return zeroTwist;
                inside = phi;
            }

            return inside;
        }

        // Whether swivel x beats swivel y by the margin: less violation, or, both feasible, less twist.
        private bool ClearlyBetter(in Quat a, in Quat b, float x, float y)
        {
            if (Violation(a, b, x) < Violation(a, b, y) - BasinMargin) return true;
            var lx = Local(a, b, x);
            var ly = Local(a, b, y);
            return Feasible(lx) && Feasible(ly) && MathF.Abs(Twist(lx)) < MathF.Abs(Twist(ly)) - (float)BasinMargin;
        }

        private static float Twist(in Quat local) => 2f * MathF.Atan2(local.Z, local.W);

        private static float Dot(in Quat p, in Quat q) => p.X * q.X + p.Y * q.Y + p.Z * q.Z + p.W * q.W;

        // An angle difference into [-pi, pi]: the swivel's period is a full turn.
        private static float Wrap(float x) => x - 2f * MathF.PI * MathF.Round(x / (2f * MathF.PI));

        // The shoulder target in the joint frame for the swivel phi, with w >= 0.
        private static Quat Local(in Quat a, in Quat b, float phi)
        {
            var c = MathF.Cos(0.5f * phi);
            var s = MathF.Sin(0.5f * phi);
            var local = new Quat(c * a.X + s * b.X, c * a.Y + s * b.Y, c * a.Z + s * b.Z, c * a.W + s * b.W);
            return local.W < 0f ? new Quat(-local.X, -local.Y, -local.Z, -local.W) : local;
        }

        // Swing is the angle between the frame z and its image: inside the cone when sin^2(swing / 2) = x^2 + y^2 <= sin^2(cone / 2).
        private bool InsideCone(in Quat local) => local.X * local.X + local.Y * local.Y <= _coneSin2;

        // x^2 + y^2 is a quadratic form in (cos(phi / 2), sin(phi / 2)); its least value over the swivel is the form's smaller eigenvalue.
        private bool SomeSwivelInsideCone(in Quat a, in Quat b)
        {
            var maa = a.X * a.X + a.Y * a.Y;
            var mbb = b.X * b.X + b.Y * b.Y;
            var mab = a.X * b.X + a.Y * b.Y;
            var half = 0.5f * (maa - mbb);
            return 0.5f * (maa + mbb) - MathF.Sqrt(half * half + mab * mab) <= _coneSin2;
        }

        private bool Feasible(in Quat local)
        {
            if (!InsideCone(local)) return false;
            var twist = Twist(local);
            return twist >= _twistLo && twist <= _twistHi;
        }

        // Bisection between a feasible and an infeasible swivel to the boundary; the feasible end.
        private float Boundary(in Quat a, in Quat b, float inside, float outside)
        {
            for (var i = 0; i < SwivelBisections; i++)
            {
                var mid = 0.5f * (inside + outside);
                if (Feasible(Local(a, b, mid))) inside = mid;
                else outside = mid;
            }

            return inside;
        }

        // The feasible swivel nearest phi0: step outward both ways, and at the first step that lands inside, bisect back to the boundary;
        // of the two sides, the one with the smaller twist. False when no swivel is feasible.
        private bool NearestFeasible(in Quat a, in Quat b, float phi0, out float swivel)
        {
            swivel = 0f;
            const float step = MathF.PI / SwivelSteps;
            for (var k = 1; k <= SwivelSteps; k++)
            {
                var found = false;
                var bestTwist = float.MaxValue;
                for (var sign = -1; sign <= 1; sign += 2)
                {
                    var phi = phi0 + sign * k * step;
                    if (!Feasible(Local(a, b, phi))) continue;
                    var inside = Boundary(a, b, phi, phi0 + sign * (k - 1) * step);
                    var twist = MathF.Abs(Twist(Local(a, b, inside)));
                    if (twist < bestTwist)
                    {
                        bestTwist = twist;
                        swivel = inside;
                        found = true;
                    }
                }

                if (found) return true;
            }

            return false;
        }

        // Radians past the limits at the swivel phi, swing past the cone plus twist past its range, with the small twist preference. In
        // double: the least point of a smooth minimum is found only to the square root of the arithmetic's precision.
        private double Violation(in Quat a, in Quat b, double phi)
        {
            var c = Math.Cos(0.5 * phi);
            var s = Math.Sin(0.5 * phi);
            var x = c * a.X + s * b.X;
            var y = c * a.Y + s * b.Y;
            var z = c * a.Z + s * b.Z;
            var w = c * a.W + s * b.W;
            if (w < 0.0)
            {
                z = -z;
                w = -w;
            }

            var swing = 2.0 * Math.Atan2(Math.Sqrt(x * x + y * y), Math.Sqrt(z * z + w * w));
            var twist = 2.0 * Math.Atan2(z, w);
            var over = twist < _twistLo ? _twistLo - twist : (twist > _twistHi ? twist - _twistHi : 0.0);
            return Math.Max(0.0, swing - _cone) + over + TwistPreference * Math.Abs(twist);
        }

        // The least-violating swivel: the least of LeastSteps steps round the circle from phi0, refined by golden section within a step
        // either side.
        private float LeastViolating(in Quat a, in Quat b, float phi0)
        {
            const double step = 2.0 * Math.PI / LeastSteps;
            var best = (double)phi0;
            var least = Violation(a, b, best);
            for (var k = 1; k < LeastSteps; k++)
            {
                var phi = phi0 + k * step;
                var v = Violation(a, b, phi);
                if (v < least)
                {
                    least = v;
                    best = phi;
                }
            }

            return Golden(a, b, best - step, best + step);
        }

        // The least-violating swivel of the held swivel's basin: downhill in steps of a LeastSteps-th of the circle while the violation
        // falls, then golden section within a step either side.
        private float LocalLeastViolating(in Quat a, in Quat b, float held)
        {
            const double step = 2.0 * Math.PI / LeastSteps;
            var best = (double)held;
            var least = Violation(a, b, best);
            var up = Violation(a, b, best + step);
            var down = Violation(a, b, best - step);
            if (up < least || down < least)
            {
                var dir = up < down ? 1.0 : -1.0;
                for (var k = 0; k < LeastSteps; k++)
                {
                    var next = best + dir * step;
                    var v = Violation(a, b, next);
                    if (v >= least) break;
                    best = next;
                    least = v;
                }
            }

            return Golden(a, b, best - step, best + step);
        }

        // Golden-section search for the least violation on [lo, hi].
        private float Golden(in Quat a, in Quat b, double lo, double hi)
        {
            const double g = 0.6180339887498949;
            var p = hi - g * (hi - lo);
            var q = lo + g * (hi - lo);
            var fp = Violation(a, b, p);
            var fq = Violation(a, b, q);
            for (var i = 0; i < LeastRefinements; i++)
            {
                if (fp <= fq)
                {
                    hi = q;
                    q = p;
                    fq = fp;
                    p = hi - g * (hi - lo);
                    fp = Violation(a, b, p);
                }
                else
                {
                    lo = p;
                    p = q;
                    fp = fq;
                    q = lo + g * (hi - lo);
                    fq = Violation(a, b, q);
                }
            }

            return (float)(fp <= fq ? p : q);
        }

        // Twist clamped to its range and swing to the cone, the swing direction kept. The target is swing * twist, the twist about the frame
        // z and the swing about an axis in its xy plane.
        private Quat ClampToLimits(in Quat local)
        {
            // At a half-turn swing the twist is undefined: read it as none.
            var tl = MathF.Sqrt(local.Z * local.Z + local.W * local.W);
            var tz = tl > 1e-6f ? local.Z / tl : 0f;
            var tw = tl > 1e-6f ? local.W / tl : 1f;
            var sx = tw * local.X - tz * local.Y;
            var sy = tz * local.X + tw * local.Y;
            var sw = tl;
            var s2 = sx * sx + sy * sy;
            if (s2 > _coneSin2)
            {
                var k = _coneSinHalf / MathF.Sqrt(s2);
                sx *= k;
                sy *= k;
                sw = _coneCosHalf;
            }

            var half = 0.5f * Ik.Clamp(2f * MathF.Atan2(tz, tw), _twistLo, _twistHi);
            tz = MathF.Sin(half);
            tw = MathF.Cos(half);
            return new Quat(tw * sx + tz * sy, tw * sy - tz * sx, sw * tz, sw * tw);
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
