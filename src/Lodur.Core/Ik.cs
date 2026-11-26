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
    /// </summary>
    public sealed class ArmChain
    {
        private readonly Quat _parentRestInverse, _frame, _frameInverse;
        private readonly IkVec _parentCentre, _shoulderAnchor, _upper, _lower, _elbowAxis, _twistAxis;
        private readonly float _flexSign, _flexLo, _flexHi, _cone, _twistLo, _twistHi, _coneSin2, _coneSinHalf, _coneCosHalf;
        private readonly float _maxSwivelStep;

        // The previous solve's shoulder target before clamping, whose swivel the next solve starts from; none after construction or Reset.
        private Quat _previous;
        private bool _hasPrevious;

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

            _maxSwivelStep = (float)(SwivelRate * tickSeconds);
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
        }

        /// <summary>Finds the shoulder, elbow and wrist of <paramref name="side"/> by label; <see cref="ArgumentException"/> if the plan has no such arm.</summary>
        /// <remarks>One chain is one arm's solver across ticks of <paramref name="tickSeconds"/>, which sets how far the swivel turns per solve.</remarks>
        public static ArmChain Of(BodyPlan plan, Side side, double tickSeconds) => new ArmChain(plan, side, tickSeconds);

        /// <summary>Forgets the previous swivel: the next solve uses the stateless rule.</summary>
        public void Reset() => _hasPrevious = false;

        /// <summary>The last solve's shoulder target before clamping, whose swivel the next solve holds (identity before any solve); tests read it.</summary>
        internal Quat Held => _hasPrevious ? _previous : Quat.Identity;

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
        /// Returns false when the point was out of reach or a limit clamped; the targets are then the limits-clamped pose of the swivel that
        /// violates the limits least, continuous with the reached targets at the edge of the feasible set, or, while the swivel turns between
        /// basins, the clamped pose of the swivel on its way. One call is one tick of the arm's history.
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
            _previous = local;
            _hasPrevious = true;
            if (clamp)
            {
                // Infeasible: swing and twist clamped to the limits.
                local = ClampToLimits(local);
                reached = false;
            }

            shoulder = local;
            elbow = Quat.FromAxisAngle(0f, 0f, 1f, flex);
            return reached;
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
