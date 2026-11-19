using Lodur;

namespace Lodur.Core.Tests;

public sealed class ActorTests
{
    [Fact]
    public void Parity_seed_1_breaks_at_14730_ms_withdraws_at_14830_ms_with_16_flinches()
    {
        var (broke, withdrew, flinches) = new PainBoxReplay(1).Run(120.0);
        Assert.Equal(1473, broke);
        Assert.Equal(1483, withdrew);
        Assert.Equal(16, flinches);
    }

    [Fact]
    public void Reflex_gain_is_set_ahead_of_time_suppression_uses_the_previous_resolve()
    {
        // Drain hard so resolve moves a lot on every tick; then the previous and the current resolve differ visibly.
        var drive = new DriveParameters { ResolveDrainPain = 8.0, ResolveDrainStrain = 8.0 };
        var w = new WithdrawalParameters();
        var r = new PainBoxReplay(1, drive: drive);
        var checkedTicks = 0;
        while (r.Actor.TickCount < 3000 && !r.Actor.Drive.WillBroken)
        {
            var before = r.Actor.Drive;
            r.Tick();
            var after = r.Actor.Drive;
            var afferent = r.Actor.Afferent(0);
            if (before.Resolve < 0.9 && afferent > 0.2 && before.Resolve - after.Resolve > 1e-4 && !after.WillBroken)
            {
                var load = afferent * (w.MaxSuppression * before.Resolve);
                var viaPrevious = before.Strain + PainBoxReplay.Dt * (load - 0.1 * before.Strain);
                var viaCurrent = before.Strain + PainBoxReplay.Dt * (afferent * (w.MaxSuppression * after.Resolve) - 0.1 * before.Strain);
                Assert.Equal(viaPrevious, after.Strain, 12);
                Assert.True(Math.Abs(viaCurrent - after.Strain) > 1e-7);
                checkedTicks++;
            }
        }
        Assert.True(checkedTicks > 10);
    }

    [Fact]
    public void Suppression_is_never_free_pain_is_identical_and_only_the_suppressed_run_accumulates_strain()
    {
        var held = new PainBoxReplay(2);
        var free = new PainBoxReplay(2, withdrawal: new WithdrawalParameters { MaxSuppression = 0.0 });
        for (var i = 0; i < 1000; i++)
        {
            held.Tick();
            free.Tick();
            Assert.Equal(free.Actor.Drive.Pain, held.Actor.Drive.Pain);
            Assert.Equal(0.0, free.Actor.Drive.Strain);
        }
        Assert.True(held.Actor.Drive.Strain > 0.5);
        Assert.True(held.Actor.Drive.Pain > 0.1);
    }

    [Fact]
    public void A_broken_will_releases_suppression_goal_withdraws_for_good_and_deliberate_withdrawal_follows_its_latency()
    {
        var r = new PainBoxReplay(1);
        long broke = -1, withdrew = -1;
        var strain = 0.0;
        for (long i = 0; i < 1600; i++)
        {
            var e = r.Tick();
            Assert.Equal(broke < 0 && (e & TickEvents.WillBroke) == 0 ? Goal.Hold : Goal.Withdraw, r.Actor.Goal);
            if (broke >= 0)
            {
                // Nothing is held down any more: strain only decays, and a broken will stays broken.
                Assert.True(r.Actor.Drive.WillBroken);
                Assert.True(r.Actor.Drive.Strain < strain);
            }
            if ((e & TickEvents.WillBroke) != 0) broke = i;
            if ((e & TickEvents.Withdrew) != 0) withdrew = i;
            strain = r.Actor.Drive.Strain;
            if (withdrew >= 0) break;
        }
        Assert.Equal(broke + 10, withdrew); // round(0.1 s / 10 ms)
        // From Withdrew the behaviour is the withdraw pose, whatever the leak.
        Assert.Equal(PainBoxReplay.Withdraw[0].X, r.Targets[0].X, 5);
        Assert.Equal(PainBoxReplay.Withdraw[1].Z, r.Targets[1].Z, 5);
        Assert.Equal(PainBoxReplay.Withdraw[0].W, r.Targets[0].W, 5);
    }

    private static long BreakTick(double intensity, double pull)
    {
        var (broke, _, _) = new PainBoxReplay(1, intensity, pull).Run(3000.0);
        return broke;
    }

    [Fact]
    public void Break_time_is_ordered_by_intensity_and_by_pull_and_no_stimulus_never_breaks()
    {
        long a = BreakTick(1.0, 0.5), b = BreakTick(0.8, 0.5), c = BreakTick(0.6, 0.5);
        Assert.True(a > 0 && a < b && b < c, $"intensity: {a} {b} {c}");

        long p0 = BreakTick(0.8, 0.0), p5 = BreakTick(0.8, 0.5), p8 = BreakTick(0.8, 0.8);
        Assert.True(p0 > 0 && p0 < p5 && p5 < p8, $"pull: {p0} {p5} {p8}");

        var quiet = new PainBoxReplay(1, intensity: 0.0);
        for (var i = 0; i < 60000; i++) Assert.Equal(TickEvents.None, quiet.Tick());
        Assert.Equal(0.0, quiet.Actor.Drive.Pain);
        Assert.Equal(0.0, quiet.Actor.Drive.Strain);
        Assert.Equal(1.0, quiet.Actor.Drive.Resolve);
        Assert.False(quiet.Actor.Drive.WillBroken);
    }

    [Fact]
    public void Tick_allocates_nothing_in_steady_state()
    {
        var r = new PainBoxReplay(1);
        r.Tick(); // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) r.Tick();
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Two_actors_ticked_interleaved_equal_the_same_actors_ticked_alone()
    {
        const int n = 2500;
        var a1 = new PainBoxReplay(1);
        var b1 = new PainBoxReplay(7, intensity: 0.6, pull: 0.2);
        var a2 = new PainBoxReplay(1);
        var b2 = new PainBoxReplay(7, intensity: 0.6, pull: 0.2);
        var ea = new TickEvents[n];
        var eb = new TickEvents[n];
        for (var i = 0; i < n; i++) { ea[i] = a1.Tick(); eb[i] = b1.Tick(); }
        for (var i = 0; i < n; i++) Assert.Equal(ea[i], a2.Tick());
        for (var i = 0; i < n; i++) Assert.Equal(eb[i], b2.Tick());

        static void Same(PainBoxReplay x, PainBoxReplay y)
        {
            Assert.Equal(x.Actor.Drive.Pain, y.Actor.Drive.Pain);
            Assert.Equal(x.Actor.Drive.Strain, y.Actor.Drive.Strain);
            Assert.Equal(x.Actor.Drive.Resolve, y.Actor.Drive.Resolve);
            Assert.Equal(x.Actor.Drive.WillBroken, y.Actor.Drive.WillBroken);
            Assert.Equal(x.Actor.Afferent(0), y.Actor.Afferent(0));
            Assert.Equal(x.Actor.Stimulus(0), y.Actor.Stimulus(0));
            Assert.Equal(x.Actor.Goal, y.Actor.Goal);
            Assert.Equal(x.Actor.TickCount, y.Actor.TickCount);
            Assert.Equal(x.Strengths, y.Strengths);
            Assert.Equal(x.Targets, y.Targets);
        }
        Same(a1, a2);
        Same(b1, b2);
    }
}
