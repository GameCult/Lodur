using Lodur;

namespace Lodur.Tests;

public sealed class PdMuscleTests
{
    private static (float X, float Y, float Z) Torque(Quat rotation, (float X, float Y, float Z) w, Quat target, float kp, float kd, float strength, float? twistKp = null, float? twistKd = null)
    {
        PdMuscle.Torque(rotation, w.X, w.Y, w.Z, target, new MuscleGains(kp, kd, twistKp ?? kp, twistKd ?? kd), strength, out var x, out var y, out var z);
        return (x, y, z);
    }

    [Fact]
    public void At_the_target_and_at_rest_there_is_no_torque()
    {
        var q = Quat.FromAxisAngle(0f, 1f, 0f, 0.7f);
        var t = Torque(q, (0f, 0f, 0f), q, 100f, 10f, 1f);
        Assert.Equal((0f, 0f, 0f), (MathF.Abs(t.X) < 1e-4f ? 0f : t.X, MathF.Abs(t.Y) < 1e-4f ? 0f : t.Y, MathF.Abs(t.Z) < 1e-4f ? 0f : t.Z));
    }

    [Fact]
    public void Torque_turns_the_child_toward_the_target_in_proportion_to_the_error()
    {
        var near = Torque(Quat.Identity, default, Quat.FromAxisAngle(0f, 0f, 1f, 0.1f), 100f, 0f, 1f);
        var far = Torque(Quat.Identity, default, Quat.FromAxisAngle(0f, 0f, 1f, 0.2f), 100f, 0f, 1f);
        Assert.Equal(10f, near.Z, 1e-2f);   // kp * angle about +z
        Assert.Equal(20f, far.Z, 1e-2f);
        Assert.Equal(0f, near.X, 1e-4f);
        Assert.Equal(0f, near.Y, 1e-4f);
        var back = Torque(Quat.FromAxisAngle(0f, 0f, 1f, 0.3f), default, Quat.Identity, 100f, 0f, 1f);
        Assert.Equal(-30f, back.Z, 1e-2f);  // the error is a rotation, so it flips sign with direction
    }

    [Fact]
    public void Damping_opposes_the_relative_angular_velocity()
    {
        var t = Torque(Quat.Identity, (1f, -2f, 3f), Quat.Identity, 100f, 5f, 1f);
        Assert.Equal((-5f, 10f, -15f), (t.X, t.Y, t.Z));
    }

    [Fact]
    public void Strength_scales_the_whole_law_and_zero_is_limp()
    {
        var target = Quat.FromAxisAngle(1f, 0f, 0f, 0.4f);
        var full = Torque(Quat.Identity, (0.5f, 0f, 0f), target, 100f, 10f, 1f);
        var half = Torque(Quat.Identity, (0.5f, 0f, 0f), target, 100f, 10f, 0.5f);
        Assert.Equal(full.X * 0.5f, half.X, 1e-4f);
        var limp = Torque(Quat.Identity, (0.5f, 0f, 0f), target, 100f, 10f, 0f);
        Assert.Equal((0f, 0f, 0f), limp);
        Assert.Equal((0f, 0f, 0f), Torque(Quat.Identity, (0.5f, 0f, 0f), target, 100f, 10f, -1f));
    }

    [Fact]
    public void The_error_takes_the_shorter_way_round_even_when_the_quaternions_are_opposite_signs()
    {
        // 350 degrees about z is 10 degrees the other way.
        var rotation = Quat.FromAxisAngle(0f, 0f, 1f, 350f * MathF.PI / 180f);
        var t = Torque(rotation, default, Quat.Identity, 100f, 0f, 1f);
        Assert.Equal(100f * 10f * MathF.PI / 180f, t.Z, 1e-2f);
        // The same rotation written with every sign flipped gives the same torque.
        var flipped = new Quat(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
        var t2 = Torque(flipped, default, Quat.Identity, 100f, 0f, 1f);
        Assert.Equal(t.Z, t2.Z, 1e-3f);
    }

    [Fact]
    public void Twist_gains_act_on_z_only_and_swing_gains_on_x_and_y()
    {
        var w = (1f, 1f, 1f);
        var t = Torque(Quat.Identity, w, Quat.Identity, 100f, 10f, 1f, twistKp: 100f, twistKd: 1f);
        Assert.Equal((-10f, -10f, -1f), (t.X, t.Y, t.Z));
        var e = Torque(Quat.Identity, default, Quat.FromAxisAngle(1f, 0f, 0f, 0.1f), 100f, 0f, 1f, twistKp: 5f, twistKd: 0f);
        Assert.Equal(10f, e.X, 1e-2f);
        var z = Torque(Quat.Identity, default, Quat.FromAxisAngle(0f, 0f, 1f, 0.1f), 100f, 0f, 1f, twistKp: 5f, twistKd: 0f);
        Assert.Equal(0.5f, z.Z, 1e-3f);
    }

    [Fact]
    public void Quaternion_rotate_matches_the_axis_angle()
    {
        Quat.FromAxisAngle(0f, 0f, 1f, MathF.PI / 2).Rotate(1f, 0f, 0f, out var x, out var y, out var z);
        Assert.Equal(0f, x, 1e-5f);
        Assert.Equal(1f, y, 1e-5f);
        Assert.Equal(0f, z, 1e-5f);
    }
}
