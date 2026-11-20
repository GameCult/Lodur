using Lodur;

namespace Lodur.Core.Tests;

/// <summary>The will breaks when resolve reaches zero, mends when it climbs back to WillMendsAt, and can break again.</summary>
public sealed class WillTests
{
    private static Brain Gate(DriveQuantity q, double threshold) => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "objective", Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw } },
            new GoalSpec
            {
                Id = "release", Priority = 1.0,
                When = new[] { new Condition(q, ConditionOp.AtLeast, threshold) },
                Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Withdraw }, FlinchPose = PainBoxBrain.Withdraw },
            },
        },
    };

    /// <summary>Ticks the box on (or off) until the predicate holds or the cap runs out; counts WillBroke events on the way.</summary>
    private static long Until(PainBoxReplay r, bool boxOn, Func<DriveState, bool> done, ref int breaks, long cap = 40000)
    {
        for (long i = 0; i < cap; i++)
        {
            if ((r.Tick(boxOn) & TickEvents.WillBroke) != 0) breaks++;
            if (done(r.Actor.Drive)) return i;
        }

        Assert.Fail("the predicate never held");
        return -1;
    }

    [Fact]
    public void A_will_that_mends_can_break_again_and_each_break_is_reported_and_counted_but_resolve_parked_at_zero_breaks_once()
    {
        var r = new PainBoxReplay(1, brain: Gate(DriveQuantity.Breaks, 99.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        Assert.Equal(1, breaks);
        Assert.Equal(1, r.Actor.Drive.Breaks);

        for (var i = 0; i < 3000; i++) r.Tick(true);   // held under the box, resolve parked at zero
        Assert.Equal(0.0, r.Actor.Drive.Resolve);
        Assert.True(r.Actor.Drive.WillBroken);

        for (var i = 0; i < 3000; i++) if ((r.Tick(true) & TickEvents.WillBroke) != 0) breaks++;
        Assert.Equal(1, breaks);

        Until(r, false, d => !d.WillBroken, ref breaks);
        Assert.True(r.Actor.Drive.Resolve >= new DriveParameters().WillMendsAt);
        Assert.Equal(1, r.Actor.Drive.Breaks);

        Until(r, true, d => d.WillBroken, ref breaks);
        Assert.Equal(2, breaks);
        Assert.Equal(2, r.Actor.Drive.Breaks);
    }

    [Fact]
    public void A_mend_waits_for_resolve_to_reach_WillMendsAt_not_merely_to_leave_zero()
    {
        var mends = new DriveParameters { WillMendsAt = 0.6 };
        var r = new PainBoxReplay(1, drive: mends, brain: Gate(DriveQuantity.Breaks, 99.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        Until(r, false, d => d.Resolve > 0.0, ref breaks);
        var mended = Until(r, false, d => !d.WillBroken, ref breaks);
        Assert.True(mended > 0, "the will mended the moment resolve left zero");
        Assert.True(r.Actor.Drive.Resolve >= 0.6);
    }

    [Theory]
    [InlineData(DriveQuantity.Breaks, true)]
    [InlineData(DriveQuantity.WillBroken, false)]
    public void A_release_gated_on_Breaks_holds_after_the_will_mends_and_one_gated_on_WillBroken_returns_to_the_objective(DriveQuantity q, bool holds)
    {
        var r = new PainBoxReplay(1, brain: Gate(q, 1.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        for (var i = 0; i < 50; i++) r.Tick(true);
        Assert.Equal(1, r.Actor.GoalIndex);

        Until(r, false, d => !d.WillBroken, ref breaks);
        for (var i = 0; i < 100; i++) r.Tick(false);
        Assert.Equal(holds ? 1 : 0, r.Actor.GoalIndex);
    }

    [Fact]
    public void A_condition_on_Breaks_at_two_follows_the_second_break_by_the_latency_not_the_first()
    {
        var r = new PainBoxReplay(1, brain: Gate(DriveQuantity.Breaks, 2.0));
        var breaks = 0;
        Until(r, true, d => d.WillBroken, ref breaks);
        for (var i = 0; i < 100; i++) r.Tick(true);
        Assert.Equal(0, r.Actor.GoalIndex);

        Until(r, false, d => !d.WillBroken, ref breaks);
        Until(r, true, d => d.Breaks == 2, ref breaks);
        var second = r.Actor.TickCount;
        long changed = -1;
        for (var i = 0; i < 30 && changed < 0; i++)
        {
            if ((r.Tick(true) & TickEvents.GoalChanged) != 0) changed = r.Actor.TickCount;
        }

        Assert.Equal(second + 10, changed);
    }

    [Fact]
    public void Will_is_resolve_bit_for_bit_while_attention_and_consciousness_are_slots_that_read_one()
    {
        var r = new PainBoxReplay(3);
        for (var i = 0; i < 2500; i++)
        {
            r.Tick(true);
            var d = r.Actor.Drive;
            Assert.Equal(1.0, d.Attention);
            Assert.Equal(1.0, d.Consciousness);
            Assert.Equal(BitConverter.DoubleToInt64Bits(d.Resolve), BitConverter.DoubleToInt64Bits(d.Will));
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void A_WillMendsAt_outside_zero_exclusive_to_one_is_refused(double mendsAt) =>
        Assert.Throws<ArgumentException>(() => new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = mendsAt }));

    [Fact]
    public void A_WillMendsAt_of_one_is_allowed() => _ = new PainBoxReplay(1, drive: new DriveParameters { WillMendsAt = 1.0 });
}
