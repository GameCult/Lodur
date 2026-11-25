using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Which side of the body a part, joint or sense point is on; Center for the midline.</summary>
    public enum Side { Center, Left, Right }

    /// <summary>A part's collision shape.</summary>
    public enum PartShape { Box, Capsule, Sphere }

    /// <summary>A joint's degrees of freedom: a ball (swing cone and twist) or a hinge about its axis.</summary>
    public enum JointKind { Spherical, Revolute }

    /// <summary>
    /// One rigid part. <see cref="Size"/> is (x, y, z): half extents for a box; radius, half length, 0 for a capsule;
    /// radius, 0, 0 for a sphere. <see cref="Position"/> is the creation pose relative to the build origin.
    /// The part tree is not a field here: it is the joint list.
    /// </summary>
    public readonly record struct PartDef(
        string Label, Side Side, PartShape Shape, (float X, float Y, float Z) Size, (float X, float Y, float Z) Position,
        Quat Rotation, float Density, float Friction, float RollingResistance);

    /// <summary>A joint from <see cref="Parent"/> to <see cref="Child"/> (part ordinals); the child's only joint is the one that attaches it.</summary>
    public readonly record struct JointDef(
        string Label, Side Side, JointKind Kind, int Parent, int Child, (float X, float Y, float Z) Anchor,
        (float X, float Y, float Z) Axis, float Lower, float Upper, float Cone, MuscleGains Gains);

    /// <summary>A point on a part that perception senses at: the part's position plus its rotation applied to <see cref="Offset"/>.</summary>
    public readonly record struct SensePointDef(string Label, Side Side, int Part, (float X, float Y, float Z) Offset);

    /// <summary>
    /// Where the airway opens to the outside: a sphere of <see cref="Radius"/> m centred at <see cref="Offset"/> in the part's frame; liquid inside it is liquid
    /// at the airway. The sphere must reach outside the part's shape, since liquid never enters a body.
    /// </summary>
    public readonly record struct AirwayOpeningDef(string Label, Side Side, int Part, (float X, float Y, float Z) Offset, float Radius);

    /// <summary>
    /// A body as data: parts, the joints that are also its tree, sense points and airway openings, each named by label and side.
    /// Engine-free; a binding (Lodur.Body.Ymir) builds it into a physics world. Validated at construction.
    /// </summary>
    public sealed class BodyPlan
    {
        private readonly int[] _parentOf;
        private readonly Dictionary<(string, Side), int> _parts = new Dictionary<(string, Side), int>();
        private readonly Dictionary<(string, Side), int> _joints = new Dictionary<(string, Side), int>();
        private readonly Dictionary<(string, Side), int> _senses = new Dictionary<(string, Side), int>();
        private readonly Dictionary<(string, Side), int> _airway = new Dictionary<(string, Side), int>();

        public BodyPlan(IReadOnlyList<PartDef> parts, IReadOnlyList<JointDef> joints, IReadOnlyList<SensePointDef> sensePoints,
            IReadOnlyList<AirwayOpeningDef>? airwayOpenings = null)
        {
            Parts = new List<PartDef>(parts).AsReadOnly();
            Joints = new List<JointDef>(joints).AsReadOnly();
            SensePoints = new List<SensePointDef>(sensePoints).AsReadOnly();
            AirwayOpenings = new List<AirwayOpeningDef>(airwayOpenings ?? Array.Empty<AirwayOpeningDef>()).AsReadOnly();
            _parentOf = Validate();
        }

        public IReadOnlyList<PartDef> Parts { get; }
        public IReadOnlyList<JointDef> Joints { get; }
        public IReadOnlyList<SensePointDef> SensePoints { get; }
        public IReadOnlyList<AirwayOpeningDef> AirwayOpenings { get; }

        /// <summary>The part ordinal a part hangs from (the parent of the one joint that attaches it), -1 for the root.</summary>
        public int ParentOf(int part) => _parentOf[part];

        public int PartIndex(string label, Side side = Side.Center) => Find(_parts, "part", label, side);
        public int JointIndex(string label, Side side = Side.Center) => Find(_joints, "joint", label, side);
        public int SenseIndex(string label, Side side = Side.Center) => Find(_senses, "sense point", label, side);
        public int AirwayIndex(string label, Side side = Side.Center) => Find(_airway, "airway opening", label, side);

        private static int Find(Dictionary<(string, Side), int> index, string what, string label, Side side) =>
            index.TryGetValue((label, side), out var i) ? i : throw new ArgumentException($"no {what} labelled '{label}' on side {side}.");

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite((float X, float Y, float Z) v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

        private int[] Validate()
        {
            // 1. (Label, Side) unique per list, labels non-empty.
            Unique(Parts.Count, i => (Parts[i].Label, Parts[i].Side), _parts, "part");
            Unique(Joints.Count, i => (Joints[i].Label, Joints[i].Side), _joints, "joint");
            Unique(SensePoints.Count, i => (SensePoints[i].Label, SensePoints[i].Side), _senses, "sense point");
            Unique(AirwayOpenings.Count, i => (AirwayOpenings[i].Label, AirwayOpenings[i].Side), _airway, "airway opening");

            // 2. Joint ends in range and ordered, so the tree is acyclic and parts are created parent first.
            foreach (var d in Joints)
            {
                if (d.Parent < 0 || d.Parent >= Parts.Count || d.Child < 0 || d.Child >= Parts.Count)
                {
                    throw new ArgumentException($"joint '{d.Label}' {d.Side}: parent {d.Parent} or child {d.Child} is not a part.");
                }

                if (d.Parent >= d.Child)
                {
                    throw new ArgumentException($"joint '{d.Label}' {d.Side}: parent {d.Parent} must precede child {d.Child}.");
                }
            }

            // 3. Part 0 is the root (rule 2 already leaves it no joint to be the child of); every other part is the child of exactly one joint.
            if (Parts.Count == 0) throw new ArgumentException("a body plan needs a root part.");
            var parentOf = new int[Parts.Count];
            var attached = new int[Parts.Count];
            for (var p = 0; p < parentOf.Length; p++) parentOf[p] = -1;
            foreach (var d in Joints)
            {
                parentOf[d.Child] = d.Parent;
                attached[d.Child]++;
            }

            for (var p = 1; p < attached.Length; p++)
            {
                if (attached[p] != 1)
                {
                    throw new ArgumentException($"part '{Parts[p].Label}' {Parts[p].Side} is the child of {attached[p]} joints, needs 1.");
                }
            }

            // 4. Sense points sit on parts.
            foreach (var s in SensePoints)
            {
                if (s.Part < 0 || s.Part >= Parts.Count) throw new ArgumentException($"sense point '{s.Label}' {s.Side}: part {s.Part} is not a part.");
            }

            foreach (var a in AirwayOpenings)
            {
                if (a.Part < 0 || a.Part >= Parts.Count) throw new ArgumentException($"airway opening '{a.Label}' {a.Side}: part {a.Part} is not a part.");
            }

            // 5. Numbers a physics world can take.
            foreach (var p in Parts)
            {
                var r = p.Rotation;
                var finite = Finite(p.Size) && Finite(p.Position) && IsFinite(r.X) && IsFinite(r.Y) && IsFinite(r.Z) && IsFinite(r.W)
                    && IsFinite(p.Density) && IsFinite(p.Friction) && IsFinite(p.RollingResistance);
                if (!finite) throw new ArgumentException($"part '{p.Label}' {p.Side} has a non-finite number.");
                var sz = p.Size;
                var sized = p.Shape == PartShape.Box ? sz.X > 0f && sz.Y > 0f && sz.Z > 0f
                    : p.Shape == PartShape.Capsule ? sz.X > 0f && sz.Y > 0f : sz.X > 0f;
                if (!sized) throw new ArgumentException($"part '{p.Label}' {p.Side}: a size its shape uses is not positive.");

                // A binding has no destination for what the shape cannot carry (sphere rotation, box rolling resistance, size components
                // beyond the shape's own), so the plan may not say it.
                var spare = p.Shape == PartShape.Box ? 0f : p.Shape == PartShape.Capsule ? sz.Z : Math.Abs(sz.Y) + Math.Abs(sz.Z);
                if (spare != 0f) throw new ArgumentException($"part '{p.Label}' {p.Side}: a size its shape does not use is not zero.");
                if (p.Shape == PartShape.Sphere && (r.X != 0f || r.Y != 0f || r.Z != 0f || r.W != 1f))
                {
                    throw new ArgumentException($"part '{p.Label}' {p.Side}: a sphere carries no rotation.");
                }

                if (p.Shape == PartShape.Box && p.RollingResistance != 0f) throw new ArgumentException($"part '{p.Label}' {p.Side}: a box carries no rolling resistance.");
                if (!(p.Density > 0f)) throw new ArgumentException($"part '{p.Label}' {p.Side}: density is not positive.");
            }

            foreach (var d in Joints)
            {
                var g = d.Gains;
                var finite = Finite(d.Anchor) && Finite(d.Axis) && IsFinite(d.Lower) && IsFinite(d.Upper) && IsFinite(d.Cone)
                    && IsFinite(g.SwingStiffness) && IsFinite(g.SwingDamping) && IsFinite(g.TwistStiffness) && IsFinite(g.TwistDamping) && IsFinite(g.Passive);
                if (!finite) throw new ArgumentException($"joint '{d.Label}' {d.Side} has a non-finite number.");
                if (d.Lower > d.Upper) throw new ArgumentException($"joint '{d.Label}' {d.Side}: lower limit exceeds upper.");
                if (d.Cone < 0f) throw new ArgumentException($"joint '{d.Label}' {d.Side}: cone is negative.");
            }

            foreach (var s in SensePoints)
            {
                if (!Finite(s.Offset)) throw new ArgumentException($"sense point '{s.Label}' {s.Side} has a non-finite number.");
            }

            foreach (var a in AirwayOpenings)
            {
                if (!Finite(a.Offset) || !IsFinite(a.Radius)) throw new ArgumentException($"airway opening '{a.Label}' {a.Side} has a non-finite number.");
                if (!(a.Radius > 0f)) throw new ArgumentException($"airway opening '{a.Label}' {a.Side}: radius is not positive.");
            }

            // 6. An opening's sphere reaches outside its part: liquid never enters a body, so a sphere wholly inside reads nothing.
            foreach (var a in AirwayOpenings)
            {
                if (SignedDistance(Parts[a.Part], a.Offset) <= -a.Radius)
                {
                    throw new ArgumentException($"airway opening '{a.Label}' {a.Side} lies inside its part.");
                }
            }

            return parentOf;
        }

        /// <summary>Signed distance from a point in the part's frame to the part's shape: negative inside.</summary>
        private static float SignedDistance(PartDef p, (float X, float Y, float Z) o)
        {
            var sz = p.Size;
            switch (p.Shape)
            {
                case PartShape.Sphere:
                    return MathF.Sqrt(o.X * o.X + o.Y * o.Y + o.Z * o.Z) - sz.X;
                case PartShape.Capsule:
                {
                    var dy = o.Y - Math.Clamp(o.Y, -sz.Y, sz.Y);
                    return MathF.Sqrt(o.X * o.X + dy * dy + o.Z * o.Z) - sz.X;
                }
                default:
                {
                    var qx = Math.Abs(o.X) - sz.X;
                    var qy = Math.Abs(o.Y) - sz.Y;
                    var qz = Math.Abs(o.Z) - sz.Z;
                    var ox = Math.Max(qx, 0f);
                    var oy = Math.Max(qy, 0f);
                    var oz = Math.Max(qz, 0f);
                    return MathF.Sqrt(ox * ox + oy * oy + oz * oz) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0f);
                }
            }
        }

        private static void Unique(int count, Func<int, (string, Side)> key, Dictionary<(string, Side), int> index, string what)
        {
            for (var i = 0; i < count; i++)
            {
                var (label, side) = key(i);
                if (string.IsNullOrEmpty(label)) throw new ArgumentException($"a {what} has an empty label.");
                if (!index.TryAdd((label, side), i)) throw new ArgumentException($"two {what}s are labelled '{label}' on side {side}.");
            }
        }
    }
}
