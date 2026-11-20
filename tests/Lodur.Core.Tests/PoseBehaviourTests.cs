using Lodur;

namespace Lodur.Core.Tests;

public sealed class PoseBehaviourTests
{
    private static readonly Quat[] Hold = { Quat.Identity, Quat.FromAxisAngle(1f, 0f, 0f, -1f) };
    private static readonly Quat[] Withdraw = { Quat.Identity, Quat.FromAxisAngle(1f, 0f, 0f, 0.3f) };

    private static PoseBehaviour Held(HoldParameters? p = null) =>
        new() { Keys = new[] { Hold }, FlinchPose = Withdraw, Hold = p ?? new HoldParameters() };

    private static (float[] S, Quat[] T) Write(PoseBehaviour b, double leak, double strain, double t, double inGoal = 0.0, double weight = 1.0)
    {
        var s = new float[2];
        var q = new Quat[2];
        b.Write(weight, leak, strain, t, inGoal, s, q);
        return (s, q);
    }

    private static float AngleAboutX(Quat q) => 2f * MathF.Atan2(q.X, q.W);

    private static void SameBits(Quat want, Quat got)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(want.X), BitConverter.SingleToInt32Bits(got.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(want.Y), BitConverter.SingleToInt32Bits(got.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(want.Z), BitConverter.SingleToInt32Bits(got.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(want.W), BitConverter.SingleToInt32Bits(got.W));
    }

    [Fact]
    public void One_key_is_held_and_the_leak_and_shake_blend_it_toward_the_flinch_pose_exactly_as_the_old_reflex_did()
    {
        var b = Held();
        var (s, q) = Write(b, 0.0, 0.0, 0.123);
        Assert.Equal(-1f, AngleAboutX(q[1]), 4);
        Assert.All(s, x => Assert.Equal(1f, x));

        foreach (var (leak, strain, t) in new[] { (0.4, 0.0, 0.0), (0.1, 2.5, 0.07), (0.0, 3.0, 1.234), (50.0, 50.0, 0.1) })
        {
            var w = Math.Min(Math.Max(leak * 0.3 + 0.03 * strain * Math.Sin(2.0 * Math.PI * 3.5 * t), 0.0), 1.0);
            (s, q) = Write(b, leak, strain, t, inGoal: 9.9);
            for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(Hold[m], Withdraw[m], (float)w), q[m]);
            Assert.All(s, x => Assert.InRange(x, 0f, 1f));
        }
    }

    [Fact]
    public void The_release_form_a_key_equal_to_the_flinch_pose_with_gains_zero_writes_the_old_withdraw_pose_bit_for_bit()
    {
        var release = new PoseBehaviour
        {
            Keys = new[] { PainBoxBrain.Withdraw }, FlinchPose = PainBoxBrain.Withdraw,
            Hold = new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 },
        };
        foreach (var (leak, strain, t) in new[] { (0.0, 0.0, 0.0), (0.9, 4.0, 3.3), (50.0, 50.0, 7.7) })
        {
            var (s, q) = Write(release, leak, strain, t, inGoal: 1.0);
            for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(PainBoxBrain.Hold[m], PainBoxBrain.Withdraw[m], 1f), q[m]);
            Assert.All(s, x => Assert.Equal(1f, x));
        }
    }

    [Fact]
    public void A_shipped_hold_strength_is_what_every_muscle_is_written_at()
    {
        var (s, _) = Write(Held(new HoldParameters { HoldStrength = 0.6f }), 0.0, 0.0, 0.0);
        Assert.All(s, x => Assert.Equal(0.6f, x));
    }

    [Theory]
    [InlineData(1.0, 0.0, 1.0)]
    [InlineData(0.5, 0.0, 0.5)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.2, 0.1, 0.5)]
    [InlineData(0.9, 0.2, 1.0)]    // capped at 1, never past it
    public void A_voluntary_strength_is_the_hold_strength_scaled_by_the_weight_plus_the_leak_capped_at_one(double weight, double leak, double expectedScale)
    {
        var hold = new HoldParameters { HoldStrength = 0.8f, FlinchGain = 3.0, StrainShake = 0.0 };
        var (s, _) = Write(Held(hold), leak, 0.0, 0.0, weight: weight);
        // weight + leak x FlinchGain: 0.2 + 0.1 x 3 = 0.5; 0.9 + 0.2 x 3 caps at 1.
        Assert.All(s, x => Assert.Equal(0.8f * (float)expectedScale, x));
    }

    [Fact]
    public void The_weight_scales_strength_only_the_targets_are_the_same_at_every_weight()
    {
        var b = Held(new HoldParameters { StrainShake = 0.4 });
        var (_, full) = Write(b, 0.07, 0.6, 0.31, weight: 1.0);
        var (_, faint) = Write(b, 0.07, 0.6, 0.31, weight: 0.1);
        for (var m = 0; m < full.Length; m++) SameBits(full[m], faint[m]);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_weight_outside_zero_to_one_is_refused(double weight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Held().Write(weight, 0.0, 0.0, 0.0, 0.0, new float[2], new Quat[2]));

    [Fact]
    public void The_shake_is_zero_at_zero_strain_and_its_amplitude_grows_with_strain()
    {
        var b = Held();
        double Peak(double strain)
        {
            var peak = 0.0;
            for (var i = 0; i < 400; i++) peak = Math.Max(peak, b.Blend(0.0, strain, i * 0.005));
            return peak;
        }
        Assert.Equal(0.0, Peak(0.0));
        Assert.True(Peak(1.0) > 0.0);
        Assert.True(Peak(2.0) > Peak(1.0) * 1.9);
        Assert.True(Peak(4.0) > Peak(2.0) * 1.9);
    }

    [Fact]
    public void Keys_are_walked_in_a_loop_key_zero_exactly_at_phase_zero_the_nlerp_midpoint_half_way_and_back_to_key_zero_after_k()
    {
        var keys = new[]
        {
            new[] { Quat.FromAxisAngle(0f, 0f, 1f, 0.0f), Quat.FromAxisAngle(0f, 1f, 0f, 0.1f) },
            new[] { Quat.FromAxisAngle(0f, 0f, 1f, 0.5f), Quat.FromAxisAngle(0f, 1f, 0f, 0.7f) },
            new[] { Quat.FromAxisAngle(0f, 0f, 1f, 1.0f), Quat.FromAxisAngle(0f, 1f, 0f, 0.2f) },
            new[] { Quat.FromAxisAngle(0f, 0f, 1f, 1.5f), Quat.FromAxisAngle(0f, 1f, 0f, -0.4f) },
        };
        var b = new PoseBehaviour
        {
            Keys = keys, SecondsPerKey = 0.5, FlinchPose = keys[0], // flinch = key 0 and w = 0: only the base shows
            Hold = new HoldParameters { FlinchGain = 0.0, StrainShake = 0.0 },
        };

        var (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 0.0);
        for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(keys[0][m], keys[0][m], 0f), q[m]);

        (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 0.25); // half way from key 0 to key 1
        for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(Quat.Nlerp(keys[0][m], keys[1][m], 0.5f), keys[0][m], 0f), q[m]);

        (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 1.0); // key 2 exactly
        for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(keys[2][m], keys[0][m], 0f), q[m]);

        (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 1.75); // half way from the last key back to key 0
        for (var m = 0; m < 2; m++) SameBits(Quat.Nlerp(Quat.Nlerp(keys[3][m], keys[0][m], 0.5f), keys[0][m], 0f), q[m]);

        (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 2.0); // after k keys, key 0 again
        var (_, start) = Write(b, 0.0, 0.0, 0.0, inGoal: 0.0);
        for (var m = 0; m < 2; m++) SameBits(start[m], q[m]);
        (_, q) = Write(b, 0.0, 0.0, 0.0, inGoal: 4.25); // and the second lap is the first
        var (_, first) = Write(b, 0.0, 0.0, 0.0, inGoal: 0.25);
        for (var m = 0; m < 2; m++) SameBits(first[m], q[m]);
    }

    [Fact]
    public void A_behaviour_is_checked_against_its_own_shape_and_writes_into_spans_of_its_muscle_count_only()
    {
        var b = Held();
        Assert.Equal(2, b.MuscleCount);
        Assert.Throws<ArgumentException>(() => b.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[1], new Quat[2]));
        Assert.Throws<ArgumentException>(() => b.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[2], new Quat[3]));

        Brain Of(PoseBehaviour p) => new() { Goals = new[] { new GoalSpec { Id = "g", Behaviour = p } } };
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Of(new PoseBehaviour())));
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            Of(b with { FlinchPose = new Quat[1] })));
        Assert.Throws<ArgumentException>(() => new Actor(0.01, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(),
            Of(b with { Keys = new[] { Hold, new Quat[1] } })));
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
