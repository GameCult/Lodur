using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>A part's world rotation and centre, as the caller reads them off the body each tick (one per plan part).</summary>
    public readonly record struct PartPose(Quat Rotation, float X, float Y, float Z);

    /// <summary>What an arm layer does with the wrists.</summary>
    public enum ArmMode
    {
        /// <summary>The wrist goes to the target.</summary>
        Reach,
        /// <summary>The wrists go between the head and the target, a hand's breadth either side of the line.</summary>
        WardOff,
        /// <summary>The wrists go to a fixed point in the arm parent's frame; the target is ignored.</summary>
        Present,
    }

    /// <summary>Which arm or arms a layer drives.</summary>
    public enum ArmSide
    {
        Left,
        Right,
        Both,
        /// <summary>The arm whose shoulder was nearer the target when the (goal, target) pair committed; held until the next commit.</summary>
        Nearer,
    }

    /// <summary>Turns the head toward the committed target: the neck's joint target is blended toward the look solution by <see cref="Weight"/>. Joint targets only, never strength.</summary>
    public sealed record LookLayer
    {
        /// <summary>The blend from the base neck pose to the look solution, in [0, 1].</summary>
        public double Weight { get; init; } = 1.0;

        internal void Validate(string goal)
        {
            if (!(double.IsFinite(Weight) && Weight >= 0.0 && Weight <= 1.0))
            {
                throw new ArgumentException($"Goal {goal}: a Look layer's Weight must be finite and in [0, 1].");
            }
        }
    }

    /// <summary>Drives the shoulder and elbow of one or both arms from the arm solver: the base pose is blended toward the solution by <see cref="Weight"/>. Joint targets only, never strength.</summary>
    public sealed record ArmLayer
    {
        public ArmMode Mode { get; init; }

        public ArmSide Side { get; init; }

        /// <summary>The blend from the base shoulder and elbow poses to the solution, in [0, 1].</summary>
        public double Weight { get; init; } = 1.0;

        /// <summary>WardOff: metres from the head centre toward the target, where the wrists meet the line. Positive.</summary>
        public float Distance { get; init; }

        /// <summary>WardOff: metres each wrist sits either side of that line, across the torso (the left wrist toward the actor's left). Not negative.</summary>
        public float Spread { get; init; }

        /// <summary>Present: the wrists' goal in the arm parent's creation frame, given for the right arm; the left arm takes it mirrored in x.</summary>
        public (float X, float Y, float Z) Point { get; init; }

        internal void Validate(string goal)
        {
            if (!(double.IsFinite(Weight) && Weight >= 0.0 && Weight <= 1.0))
            {
                throw new ArgumentException($"Goal {goal}: an Arms layer's Weight must be finite and in [0, 1].");
            }

            if (Mode == ArmMode.WardOff && !(float.IsFinite(Distance) && Distance > 0f && float.IsFinite(Spread) && Spread >= 0f))
            {
                throw new ArgumentException($"Goal {goal}: a WardOff layer needs a finite Distance above 0 and a finite Spread of at least 0.");
            }

            if (Mode == ArmMode.Present)
            {
                if (Side == ArmSide.Nearer)
                {
                    throw new ArgumentException($"Goal {goal}: a Present layer ignores the target, so it cannot pick the Nearer arm.");
                }

                if (!(float.IsFinite(Point.X) && float.IsFinite(Point.Y) && float.IsFinite(Point.Z)))
                {
                    throw new ArgumentException($"Goal {goal}: a Present layer's Point must be finite.");
                }
            }

            if (!Enum.IsDefined(typeof(ArmMode), Mode) || !Enum.IsDefined(typeof(ArmSide), Side))
            {
                throw new ArgumentException($"Goal {goal}: an Arms layer has a Mode or Side that is not defined.");
            }
        }
    }

    /// <summary>
    /// Shrinks from a threat: the base pose is blended toward <see cref="AwayLeft"/> or <see cref="AwayRight"/> by the target's bearing and
    /// <see cref="Proximity"/> (a Distance consideration, head centre to target, whose response is the blend depth). A frontal threat
    /// blends half of each. Joint targets only, never strength.
    /// </summary>
    public sealed record RecoilLayer
    {
        private Quat[] _left = Array.Empty<Quat>();
        private Quat[] _right = Array.Empty<Quat>();
        private IReadOnlyList<Quat> _leftView = Array.AsReadOnly(Array.Empty<Quat>());
        private IReadOnlyList<Quat> _rightView = Array.AsReadOnly(Array.Empty<Quat>());

        /// <summary>The pose that shrinks from a threat on the actor's left, one rotation per muscle. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Quat> AwayLeft
        {
            get => _leftView;
            init
            {
                _left = Copy(value);
                _leftView = Array.AsReadOnly(_left);
            }
        }

        /// <summary>The pose that shrinks from a threat on the actor's right. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Quat> AwayRight
        {
            get => _rightView;
            init
            {
                _right = Copy(value);
                _rightView = Array.AsReadOnly(_right);
            }
        }

        /// <summary>Input Distance (the head centre to the target); the response is the blend depth, 0 meaning the base pose.</summary>
        public Consideration Proximity { get; init; }

        internal Quat[] Left => _left;

        internal Quat[] Right => _right;

        private static Quat[] Copy(IReadOnlyList<Quat> source)
        {
            var copy = new Quat[source.Count];
            for (var m = 0; m < copy.Length; m++)
            {
                copy[m] = source[m];
            }

            return copy;
        }

        internal void Validate(string goal, int muscles)
        {
            if (_left.Length != muscles || _right.Length != muscles)
            {
                throw new ArgumentException($"Goal {goal}: a Recoil layer needs AwayLeft and AwayRight poses of one rotation per muscle.");
            }

            var k = Proximity;
            if (k.Input != ConsiderationInput.Distance)
            {
                throw new ArgumentException($"Goal {goal}: a Recoil layer's Proximity must read Distance.");
            }

            if (!double.IsFinite(k.X0) || !double.IsFinite(k.X1) || k.X1 == k.X0
                || !(k.Y0 >= 0.0 && k.Y0 <= 1.0) || !(k.Y1 >= 0.0 && k.Y1 <= 1.0)
                || !(double.IsFinite(k.Exponent) && k.Exponent > 0.0))
            {
                throw new ArgumentException($"Goal {goal}: a Recoil layer's Proximity needs finite X0 != X1, Y0 and Y1 in [0, 1], and Exponent > 0.");
            }
        }
    }

    /// <summary>One tick's inputs to the layers: the rig, the caller's part poses, the committed target (Id 0: none) and the arm the Nearer mode holds.</summary>
    internal readonly ref struct OrientInput
    {
        public readonly OrientRig? Rig;
        public readonly ReadOnlySpan<PartPose> Parts;
        public readonly Percept Target;
        public readonly Side Arm;

        public OrientInput(OrientRig rig, ReadOnlySpan<PartPose> parts, Percept target, Side arm)
        {
            Rig = rig;
            Parts = parts;
            Target = target;
            Arm = arm;
        }
    }

    /// <summary>
    /// The solvers an Actor's brain needs, resolved once from the body plan: the look chain when any goal looks, wards off or recoils (they read
    /// the head), an arm chain for each side an Arms layer can use. A chain the plan cannot give refuses at construction, naming the goal, so a
    /// plan the closed form cannot solve fails at spawn and never mid-tick. The arm chains are the Actor's arms across ticks and hold each
    /// arm's swivel and returned targets from tick to tick (an arm not driven on a tick keeps its last ones, and slews from them when next driven, until <see cref="ResetArms"/>); the rig holds nothing else, and solving allocates nothing.
    /// </summary>
    internal sealed class OrientRig
    {
        private readonly BodyPlan _plan;
        private readonly LookChain? _look;
        private readonly ArmChain? _left;
        private readonly ArmChain? _right;

        public OrientRig(BodyPlan plan, Brain brain, double tickSeconds)
        {
            _plan = plan;
            foreach (var goal in brain.Goals)
            {
                var b = goal.Behaviour;
                if (!b.HasLayers)
                {
                    continue;
                }

                if (_look == null && (b.Look != null || b.Recoil != null || b.Arms?.Mode == ArmMode.WardOff))
                {
                    try
                    {
                        _look = LookChain.Of(plan);
                    }
                    catch (ArgumentException e)
                    {
                        throw new ArgumentException($"Goal {goal.Id}: the body has no head that can look: {e.Message}", e);
                    }
                }

                if (b.Arms is { } arms)
                {
                    if (_left == null && arms.Side != ArmSide.Right)
                    {
                        _left = Arm(plan, goal.Id, Side.Left, tickSeconds);
                    }

                    if (_right == null && arms.Side != ArmSide.Left)
                    {
                        _right = Arm(plan, goal.Id, Side.Right, tickSeconds);
                    }
                }
            }
        }

        private static ArmChain Arm(BodyPlan plan, string goal, Side side, double tickSeconds)
        {
            try
            {
                return ArmChain.Of(plan, side, tickSeconds);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException($"Goal {goal}: the {side} arm cannot take an Arms layer: {e.Message}", e);
            }
        }

        /// <summary>Forgets both arms' held swivels and returned targets: each arm's next solve is the stateless rule, unslewed.</summary>
        public void ResetArms()
        {
            _left?.Reset();
            _right?.Reset();
        }

        /// <summary>The arm whose shoulder is nearer the target; ties go to the right.</summary>
        public Side NearerArm(ReadOnlySpan<PartPose> parts, in Percept target)
        {
            if (_left == null || _right == null)
            {
                return Side.Right;
            }

            var l = ShoulderDistance(_left, parts, target);
            var r = ShoulderDistance(_right, parts, target);
            return l < r ? Side.Left : Side.Right;
        }

        private float ShoulderDistance(ArmChain arm, ReadOnlySpan<PartPose> parts, in Percept target)
        {
            var s = World(arm.ParentPart, parts, new IkVec(arm.ShoulderAnchor));
            return (new IkVec(target.X, target.Y, target.Z) - s).Length;
        }

        /// <summary>A point given in a part's creation frame, where it is now: the part's centre plus its rotation from creation applied to the offset.</summary>
        private IkVec World(int part, ReadOnlySpan<PartPose> parts, IkVec creationPoint)
        {
            var def = _plan.Parts[part];
            var pose = parts[part];
            var rel = pose.Rotation * def.Rotation.Conjugate();
            return new IkVec(pose.X, pose.Y, pose.Z) + (creationPoint - new IkVec(def.Position)).Rotated(rel);
        }

        /// <summary>Composes the layers onto the base pose already in <paramref name="targets"/>: recoil, then look, then the arms.</summary>
        public void Compose(PoseBehaviour b, in OrientInput input, Span<Quat> targets)
        {
            var parts = input.Parts;
            var hasTarget = input.Target.Id != 0UL;
            var goal = new IkVec(input.Target.X, input.Target.Y, input.Target.Z);

            // The head centre, the torso's left axis and the unit direction to the target: read only when a layer needs the target.
            var head = default(IkVec);
            var left = default(IkVec);
            var toward = default(IkVec);
            var distance = 0f;
            if (hasTarget && _look != null)
            {
                var h = parts[_look.ChildPart];
                head = new IkVec(h.X, h.Y, h.Z);
                var torso = _look.ParentPart;
                left = new IkVec(1f, 0f, 0f).Rotated(parts[torso].Rotation * _plan.Parts[torso].Rotation.Conjugate());
                var v = goal - head;
                distance = v.Length;
                toward = distance > 1e-6f ? v * (1f / distance) : new IkVec(0f, 0f, 1f);
            }

            if (hasTarget && b.Recoil is { } recoil && _look != null)
            {
                var s = Ik.Clamp(toward.Dot(left), -1f, 1f);
                var depth = (float)recoil.Proximity.Response(distance);
                var wl = depth * (1f + s) * 0.5f;
                var wr = depth * (1f - s) * 0.5f;
                var awayL = recoil.Left;
                var awayR = recoil.Right;
                for (var m = 0; m < targets.Length; m++)
                {
                    targets[m] = Quat.Nlerp(Quat.Nlerp(targets[m], awayL[m], wl), awayR[m], wr);
                }
            }

            if (hasTarget && b.Look is { } look && _look != null)
            {
                var t = parts[_look.ParentPart];
                _look.Solve(t.Rotation, t.X, t.Y, t.Z, goal.X, goal.Y, goal.Z, out var neck);
                targets[_look.Joint] = Toward(targets[_look.Joint], neck, look.Weight);
            }

            if (b.Arms is { } arms && (hasTarget || arms.Mode == ArmMode.Present))
            {
                var leftArm = arms.Side == ArmSide.Left || arms.Side == ArmSide.Both || (arms.Side == ArmSide.Nearer && input.Arm == Side.Left);
                var rightArm = arms.Side == ArmSide.Right || arms.Side == ArmSide.Both || (arms.Side == ArmSide.Nearer && input.Arm != Side.Left);
                if (leftArm && _left != null)
                {
                    Drive(_left, 1f, arms, parts, goal, head, toward, left, targets);
                }

                if (rightArm && _right != null)
                {
                    Drive(_right, -1f, arms, parts, goal, head, toward, left, targets);
                }
            }
        }

        // The solution itself at weight 1, a blend toward it below that.
        private static Quat Toward(in Quat from, in Quat solution, double weight) => weight >= 1.0 ? solution : Quat.Nlerp(from, solution, (float)weight);

        // across: +1 for the left arm (its wrist sits toward the torso's left), -1 for the right.
        private void Drive(ArmChain arm, float across, ArmLayer arms, ReadOnlySpan<PartPose> parts, IkVec target, IkVec head, IkVec toward, IkVec left, Span<Quat> targets)
        {
            IkVec wrist;
            switch (arms.Mode)
            {
                case ArmMode.Reach:
                    wrist = target;
                    break;
                case ArmMode.WardOff:
                    wrist = head + toward * arms.Distance + left * (across * arms.Spread);
                    break;
                default:
                    // Given for the right arm, whose side is -x (forward is +z, left is +x); the left arm mirrors it.
                    wrist = World(arm.ParentPart, parts, new IkVec(across * -arms.Point.X, arms.Point.Y, arms.Point.Z));
                    break;
            }

            var p = parts[arm.ParentPart];
            arm.Solve(p.Rotation, p.X, p.Y, p.Z, wrist.X, wrist.Y, wrist.Z, out var shoulder, out var elbow);
            targets[arm.ShoulderJoint] = Toward(targets[arm.ShoulderJoint], shoulder, arms.Weight);
            targets[arm.ElbowJoint] = Toward(targets[arm.ElbowJoint], elbow, arms.Weight);
        }
    }
}
