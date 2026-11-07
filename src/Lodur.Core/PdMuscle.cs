using System;

namespace Lodur
{
    /// <summary>A unit quaternion, x y z w.</summary>
    public readonly struct Quat
    {
        public readonly float X, Y, Z, W;

        public Quat(float x, float y, float z, float w)
        {
            X = x; Y = y; Z = z; W = w;
        }

        public static Quat Identity => new Quat(0f, 0f, 0f, 1f);

        public Quat Conjugate() => new Quat(-X, -Y, -Z, W);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>Rotation about <paramref name="x"/>,<paramref name="y"/>,<paramref name="z"/> (a unit axis) by <paramref name="radians"/>.</summary>
        public static Quat FromAxisAngle(float x, float y, float z, float radians)
        {
            var half = 0.5f * radians;
            var s = MathF.Sin(half);
            return new Quat(x * s, y * s, z * s, MathF.Cos(half));
        }

        /// <summary>Rotates the vector v by this quaternion.</summary>
        public void Rotate(float vx, float vy, float vz, out float rx, out float ry, out float rz)
        {
            // v' = v + 2w(q x v) + 2 q x (q x v)
            var tx = 2f * (Y * vz - Z * vy);
            var ty = 2f * (Z * vx - X * vz);
            var tz = 2f * (X * vy - Y * vx);
            rx = vx + W * tx + (Y * tz - Z * ty);
            ry = vy + W * ty + (Z * tx - X * tz);
            rz = vz + W * tz + (X * ty - Y * tx);
        }
    }

    /// <summary>
    /// Gains of one muscle. Swing acts on the joint frame's x and y axes, twist on its z axis (the hinge axis
    /// of a hinge, the limb's own axis for a ball joint). They differ because a limb turns about its own axis
    /// with a fraction of the inertia it has swinging about its end, and an explicit controller is unstable
    /// when damping times the step exceeds that inertia.
    /// </summary>
    public readonly struct MuscleGains
    {
        public readonly float SwingStiffness, SwingDamping, TwistStiffness, TwistDamping;

        public MuscleGains(float swingStiffness, float swingDamping, float twistStiffness, float twistDamping)
        {
            SwingStiffness = swingStiffness; SwingDamping = swingDamping;
            TwistStiffness = twistStiffness; TwistDamping = twistDamping;
        }
    }

    /// <summary>
    /// The body layer's muscle: a proportional-derivative law on one joint. It sees only the joint's
    /// relative rotation and angular velocity, a target rotation and a strength, and returns a torque;
    /// it knows nothing of the physics engine that produced those numbers (invariant body-one-interface).
    /// </summary>
    public static class PdMuscle
    {
        /// <summary>
        /// Torque on the child, in the parent joint frame, that turns <paramref name="rotation"/> (child frame
        /// relative to parent frame) toward <paramref name="target"/> and damps <paramref name="angularVelocity"/>
        /// (child minus parent, in the parent frame). Strength scales every gain together, so the
        /// damping ratio does not change with it; strength 0 is a limp joint and returns exactly zero.
        /// The rotation error takes the shorter way round.
        /// </summary>
        public static void Torque(
            in Quat rotation, float wx, float wy, float wz, in Quat target, in MuscleGains gains, float strength,
            out float tx, out float ty, out float tz)
        {
            if (strength <= 0f)
            {
                tx = ty = tz = 0f;
                return;
            }

            // Error rotation in the parent frame that carries the current rotation onto the target.
            var e = target * rotation.Conjugate();
            if (e.W < 0f)
            {
                e = new Quat(-e.X, -e.Y, -e.Z, -e.W);
            }

            var sin = MathF.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
            // Rotation vector of the error: angle * axis, with the small-angle limit of 2 * vector part.
            var scale = sin < 1e-6f ? 2f : 2f * MathF.Atan2(sin, e.W) / sin;
            tx = strength * (gains.SwingStiffness * scale * e.X - gains.SwingDamping * wx);
            ty = strength * (gains.SwingStiffness * scale * e.Y - gains.SwingDamping * wy);
            tz = strength * (gains.TwistStiffness * scale * e.Z - gains.TwistDamping * wz);
        }
    }
}
