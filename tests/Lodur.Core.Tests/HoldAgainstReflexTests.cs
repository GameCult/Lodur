using Lodur;

namespace Lodur.Core.Tests;

public sealed class HoldAgainstReflexTests
{
    private static readonly Quat[] Hold = { Quat.Identity, Quat.FromAxisAngle(1f, 0f, 0f, -1f) };
    private static readonly Quat[] Withdraw = { Quat.Identity, Quat.FromAxisAngle(1f, 0f, 0f, 0.3f) };

    private static (float[] S, Quat[] T) Write(HoldAgainstReflex b, Goal g, double leak, double strain, double t)
    {
        var s = new float[2];
        var q = new Quat[2];
        b.Write(g, leak, strain, t, s, q);
        return (s, q);
    }

    private static float AngleAboutX(Quat q) => 2f * MathF.Atan2(q.X, q.W);

    [Fact]
    public void No_leak_and_no_strain_write_the_hold_pose_withdraw_writes_the_withdraw_pose_strengths_stay_in_unit_range()
    {
        var b = new HoldAgainstReflex(new HoldParameters(), Hold, Withdraw);
        var (s, q) = Write(b, Goal.Hold, 0.0, 0.0, 0.123);
        Assert.Equal(-1f, AngleAboutX(q[1]), 4);
        Assert.All(s, x => Assert.InRange(x, 0f, 1f));

        (s, q) = Write(b, Goal.Withdraw, 0.0, 0.0, 0.123);
        Assert.Equal(0.3f, AngleAboutX(q[1]), 4);
        Assert.All(s, x => Assert.InRange(x, 0f, 1f));

        (s, q) = Write(b, Goal.Hold, 50.0, 50.0, 0.1); // huge input: the blend clamps, strength does not leave [0, 1]
        Assert.All(s, x => Assert.InRange(x, 0f, 1f));
        Assert.InRange(AngleAboutX(q[1]), -1.0001f, 0.3001f);
    }

    [Fact]
    public void The_shake_is_zero_at_zero_strain_and_its_amplitude_grows_with_strain()
    {
        var b = new HoldAgainstReflex(new HoldParameters(), Hold, Withdraw);
        double Peak(double strain)
        {
            var peak = 0.0;
            for (var i = 0; i < 400; i++) peak = Math.Max(peak, b.Blend(Goal.Hold, 0.0, strain, i * 0.005));
            return peak;
        }
        Assert.Equal(0.0, Peak(0.0));
        Assert.True(Peak(1.0) > 0.0);
        Assert.True(Peak(2.0) > Peak(1.0) * 1.9);
        Assert.True(Peak(4.0) > Peak(2.0) * 1.9);
    }

    [Fact]
    public void Nlerp_takes_the_shorter_arc_and_returns_a_unit_quaternion()
    {
        var a = Quat.FromAxisAngle(0f, 0f, 1f, 0.2f);
        var b = Quat.FromAxisAngle(0f, 0f, 1f, 0.6f);
        var negB = new Quat(-b.X, -b.Y, -b.Z, -b.W); // same rotation, opposite sign
        var m = Quat.Nlerp(a, negB, 0.5f);
        Assert.Equal(0.4f, 2f * MathF.Atan2(m.Z, m.W), 3);
        Assert.Equal(1f, m.X * m.X + m.Y * m.Y + m.Z * m.Z + m.W * m.W, 5);
        Assert.Equal(a.Z, Quat.Nlerp(a, b, 0f).Z, 6);
        Assert.Equal(b.Z, Quat.Nlerp(a, b, 1f).Z, 6);
    }
}
