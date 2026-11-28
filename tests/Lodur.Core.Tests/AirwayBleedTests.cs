using Lodur;
using static Lodur.Core.Tests.PhysioFixtures;

namespace Lodur.Core.Tests;

/// <summary>
/// A wound in an airway tissue bleeds into the airway: the bleed splits at the tissue by its AirwayShare, the airway share enters at the caller's blood gain, and a full airway spills
/// through the mouth (idea bds-slice-map.gain-at-circulation-boundary; rulings bds-drown-in-own-blood-first, bds-blood-gain-simulated, bds-airway-cough-hold-drain).
/// The rule tests run on Body3 / Plan3 with a root muscle given an AirwayShare and a Bleeder (1e-3 m^3/s at severity 1, vascularity 1) on it.
/// </summary>
public sealed class AirwayBleedTests
{
    private const double Dt = 0.01;
    private const double Rate = 1e-3;

    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0.0, 1e-12 * Math.Abs(expected));

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void A_tissues_bleed_splits_by_its_airway_share_and_the_blood_falls_by_both(double share)
    {
        var p = Bleeding(share, 1.0);
        p.Step(Dt);
        Assert.Equal(Rate * (1.0 - share), p.Bleed(0), 15);
        Assert.Equal(Rate * share, p.AirwayBleed, 15);
        Assert.Equal(0.0, p.Bleed(1));
        Assert.Equal(0.0, p.Bleed(2));
        var drop = 0.005 - p.BloodVolume;
        Assert.Equal(Dt * (p.Bleed(0) + p.Bleed(1) + p.Bleed(2) + p.AirwayBleed), drop, 15);
        Assert.Equal(Dt * Rate, drop, 15);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(10.0, false)]
    [InlineData(1.0, true)]
    [InlineData(10.0, true)]
    public void Blood_enters_the_airway_at_the_gain_held_breath_or_not(double gain, bool held)
    {
        var p = Bleeding(1.0, gain);
        p.Step(Dt, new PhysiologyInput(0.0, 0.0, held));
        Near(Dt * gain * p.AirwayBleed, p.AirwayLiquid);
        Near(Dt * gain * Rate, p.AirwayLiquid);
        Assert.Equal(0.0, p.Coughed);
        Assert.Equal(0.0, p.Drained);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(10.0)]
    [InlineData(0.1)]
    public void The_gain_is_applied_once_at_airway_entry_and_not_to_the_circulation_or_the_readouts(double gain)
    {
        const double share = 0.25;
        var p = Bleeding(share, gain);
        p.Step(Dt);
        // Real m^3/s and real blood volume are ungained at any gain; only the airway's intake is.
        Assert.Equal(Rate * (1.0 - share), p.Bleed(0), 15);
        Assert.Equal(Rate * share, p.AirwayBleed, 15);
        Near(Dt * Rate, 0.005 - p.BloodVolume);
        Near(gain * Dt * Rate * share, p.AirwayLiquid);
    }

    [Fact]
    public void A_breath_into_a_full_airway_spills_exactly_nothing_whatever_the_draw()
    {
        // The spill is only what bled in: a draw into a full airway must leave Drained exactly 0, not a rounding residue (after - Capacity - drawRaw leaves up to ~1e-20).
        for (var k = 0; k < 400; k++)
        {
            var p = new Physiology(Body3(), Plan3(airway: Quiet), 20.0);
            var wet = new AirwaySample(1e-4 * (1.0 + 0.0371 * k), 0.0, 0.0);
            for (var i = 0; i < 200; i++)
            {
                p.Step(Dt * (1.0 + 0.013 * k), new PhysiologyInput(0.0, 0.0, false, wet));
                Assert.Equal(0.0, p.Drained);
            }

            Assert.Equal(Quiet.Capacity, p.AirwayLiquid);
        }
    }

    [Fact]
    public void A_full_airway_spills_what_bleeds_into_it_through_the_mouth()
    {
        // Share 1, gain 10, a drain the sample opens (Drop at DrainDrop): fill, then 600 more steps.
        var air = Quiet with { DrainPerSecond = 0.1 };
        var p = Bleeding(1.0, 10.0, air, new BloodDef(1.0, 0.5, 0.0));
        var drop = new AirwaySample(0.0, 0.0, air.DrainDrop);
        double bledIn = 0.0, out_ = 0.0;
        var steps = 0;
        for (; p.AirwayLiquid < air.Capacity && steps < 100000; steps++) Step();
        Assert.Equal(air.Capacity, p.AirwayLiquid);
        for (var i = 0; i < 600; i++) Step();
        Assert.Equal(air.Capacity, p.AirwayLiquid);
        Assert.True(out_ > 0.0);
        Near(10.0 * bledIn, out_ + p.AirwayLiquid);

        void Step()
        {
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, drop));
            bledIn += p.AirwayBleed * Dt;
            out_ += p.Coughed + p.Drained;
        }
    }

    [Fact]
    public void Every_airway_flow_is_at_least_zero_at_any_cough_rate()
    {
        // Drained was removed - Coughed + spill, with Coughed = removed x cough / total: for a cough that is not a power of two the product can exceed removed by an ulp and Drained read -5e-23 (BDS refuses a negative drain).
        // Fill the airway, then clear it with drain on and off, and read every flow every step.
        var humanoid = PhysiologyPlans.Humanoid().Airway;
        var rates = new List<double> { 0.3, 0.36, 0.24, 2.0, humanoid.CoughPerSecond };
        for (var k = 1; k <= 300; k++) rates.Add(0.01 * k);
        foreach (var rate in rates)
        {
            foreach (var drop in new[] { 0.0, 0.5 * humanoid.DrainDrop, humanoid.DrainDrop })
            {
                var air = humanoid with { CoughPerSecond = rate };
                var p = new Physiology(Body3(), Plan3(airway: air), 20.0);
                var flood = new AirwaySample(100.0 * air.HoldAt, 0.0, drop);
                var clear = new AirwaySample(0.0, 0.0, drop);
                for (var i = 0; i < 1500; i++)
                {
                    p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, i < 100 ? flood : clear));
                    var at = $"cough {rate} drop {drop} step {i}";
                    Assert.True(p.Coughed >= 0.0, $"{at}: Coughed {p.Coughed:R}");
                    Assert.True(p.Drained >= 0.0, $"{at}: Drained {p.Drained:R}");
                    Assert.True(p.AirwayBleed >= 0.0 && p.DrawnLive >= 0.0 && p.DrawnPooled >= 0.0, $"{at}: a flow is negative");
                    Assert.True(p.AirwayLiquid >= 0.0, $"{at}: liquid {p.AirwayLiquid:R}");
                }
            }
        }
    }

    [Fact]
    public void A_breath_draws_only_into_the_room_left()
    {
        var p = Make();   // no wound: nothing bleeds
        var pp = new Physiology(Body3(), Plan3(airway: Quiet), 20.0);
        var wet = new AirwaySample(1e-3, 0.0, 0.0);
        for (var i = 0; i < 100 && pp.AirwayLiquid < Quiet.Capacity; i++) pp.Step(Dt, new PhysiologyInput(0.0, 0.0, false, wet));
        Assert.Equal(Quiet.Capacity, pp.AirwayLiquid);
        for (var i = 0; i < 100; i++)
        {
            pp.Step(Dt, new PhysiologyInput(0.0, 0.0, false, wet));
            Assert.Equal(Quiet.Capacity, pp.AirwayLiquid);
            Assert.Equal(0.0, pp.Drained);
            Assert.Equal(0.0, pp.Coughed);
            Assert.Equal(0.0, pp.DrawnLive);
            Assert.Equal(0.0, pp.DrawnPooled);
        }

        Assert.Equal(0.0, p.AirwayBleed);
    }

    [Fact]
    public void A_breath_reports_what_it_drew_split_by_the_airways_weights()
    {
        var p = new Physiology(Body3(), Plan3(airway: Quiet), 20.0);
        p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, new AirwaySample(2e-5, 6e-5, 0.0)));
        Near(Dt * 2e-5, p.DrawnLive);
        Near(Dt * 6e-5, p.DrawnPooled);

        var half = new Physiology(Body3(), Plan3(airway: Quiet with { PooledWeight = 0.5 }), 20.0);
        half.Step(Dt, new PhysiologyInput(0.0, 0.0, false, new AirwaySample(2e-5, 6e-5, 0.0)));
        Near(Dt * 2e-5, half.DrawnLive);
        Near(Dt * 3e-5, half.DrawnPooled);
    }

    [Fact]
    public void A_nearly_full_airway_draws_only_the_room_left()
    {
        var p = new Physiology(Body3(), Plan3(airway: Quiet), 20.0);
        var wet = new AirwaySample(1e-3, 3e-3, 0.0);
        for (var i = 0; i < 100 && p.AirwayLiquid < Quiet.Capacity; i++)
        {
            var room = Quiet.Capacity - p.AirwayLiquid;
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, wet));
            if (Dt * 4e-3 > room)
            {
                Near(room, p.DrawnLive + p.DrawnPooled);
                Near(0.25, p.DrawnLive / (p.DrawnLive + p.DrawnPooled));
                Assert.Equal(Quiet.Capacity, p.AirwayLiquid);
            }
        }

        Assert.Equal(Quiet.Capacity, p.AirwayLiquid);
    }

    [Fact]
    public void A_held_breath_draws_nothing_while_the_airway_bleeds()
    {
        var p = Bleeding(1.0, 10.0);
        p.Step(Dt, new PhysiologyInput(0.0, 0.0, true, new AirwaySample(1e-4, 1e-4, 0.0)));
        Assert.Equal(0.0, p.DrawnLive);
        Assert.Equal(0.0, p.DrawnPooled);
        Near(Dt * 10.0 * Rate, p.AirwayLiquid);
    }

    [Fact]
    public void The_airways_liquid_is_conserved_with_the_draw()
    {
        var p = Bleeding(0.5, 10.0, InertAirway, new BloodDef(1.0, 0.5, 0.0));
        var rng = new Random(7);
        var drew = 0.0;
        for (var i = 0; i < 2000; i++)
        {
            var before = p.AirwayLiquid;
            var sample = new AirwaySample(rng.NextDouble() * 1e-4, rng.NextDouble() * 1e-4, rng.NextDouble() * InertAirway.DrainDrop);
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, rng.NextDouble() < 0.2, sample));
            Assert.True(p.DrawnLive >= 0.0 && p.DrawnPooled >= 0.0);
            var expected = Dt * 10.0 * p.AirwayBleed + p.DrawnLive + p.DrawnPooled - p.Coughed - p.Drained;
            Assert.InRange(Math.Abs(p.AirwayLiquid - before - expected), 0.0, 1e-18);
            drew += p.DrawnLive + p.DrawnPooled;
        }

        Assert.True(drew > 0.0);
    }

    [Fact]
    public void A_dry_sample_draws_exactly_zero_not_nan()
    {
        var p = new Physiology(Body3(), Plan3(airway: Quiet), 20.0);
        for (var i = 0; i < 10; i++)
        {
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, new AirwaySample(0.0, 0.0, 0.0)));
            Assert.Equal(0.0, p.DrawnLive);
            Assert.Equal(0.0, p.DrawnPooled);
        }

        var b = Bleeding(1.0, 10.0);
        b.Step(Dt, new PhysiologyInput(0.0, 0.0, false));
        Assert.Equal(0.0, b.DrawnLive);
        Assert.Equal(0.0, b.DrawnPooled);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(10.0)]
    public void The_dying_step_into_a_full_airway_draws_nothing(double gain)
    {
        var p = Bleeding(0.5, gain, Quiet, new BloodDef(0.005, 0.5, 0.0));
        var wet = new AirwaySample(1e-5, 1e-5, 0.0);
        for (var i = 0; i < 100000 && !p.Dead; i++) p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, wet));
        Assert.True(p.Dead);
        Assert.Equal(Quiet.Capacity, p.AirwayLiquid);
        Assert.Equal(0.0, p.DrawnLive);
        Assert.Equal(0.0, p.DrawnPooled);
    }

    [Fact]
    public void The_dying_step_reports_its_draw_and_the_next_step_none()
    {
        var p = Bleeding(0.5, 0.1, InertAirway);
        var pooled = new AirwaySample(1e-6, 1e-6, InertAirway.DrainDrop);
        for (var i = 0; i < 100000 && !p.Dead; i++) p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, pooled));
        Assert.True(p.Dead);
        Assert.True(p.DrawnPooled > 0.0);
        Assert.True(p.DrawnLive > 0.0);

        p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, pooled));
        Assert.Equal(0.0, p.DrawnLive);
        Assert.Equal(0.0, p.DrawnPooled);
    }

    [Fact]
    public void The_dying_step_reports_its_flows_and_the_next_step_none()
    {
        var p = Bleeding(0.5, 1.0, InertAirway);   // cough 2/s and a drain, so Coughed and Drained read above 0 while it bleeds into the airway
        var drop = new AirwaySample(0.0, 0.0, InertAirway.DrainDrop);
        double lost = 0.0;
        for (var i = 0; i < 100000 && !p.Dead; i++)
        {
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, drop));
            lost += Dt * (p.Bleed(0) + p.Bleed(1) + p.Bleed(2) + p.AirwayBleed);
        }

        Assert.True(p.Dead);
        Assert.Equal(DeathCause.BloodLoss, p.Cause);
        Assert.True(p.Bleed(0) > 0.0 && p.AirwayBleed > 0.0);
        Near(0.005 - p.BloodVolume, lost);
        Assert.True(p.Coughed > 0.0 && p.Drained > 0.0);

        p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, drop));
        Assert.Equal(0.0, p.Bleed(0));
        Assert.Equal(0.0, p.AirwayBleed);
        Assert.Equal(0.0, p.Coughed);
        Assert.Equal(0.0, p.Drained);
    }

    [Fact]
    public void A_cut_larynx_fills_the_humanoids_airway_at_the_gain()
    {
        var body = BodyPlans.Humanoid();
        var plan = PhysiologyPlans.Humanoid();
        var head = body.PartIndex("Head");
        var cut = plan.AfflictionIndex("Cut");

        double Seconds(double gain)
        {
            var p = new Physiology(body, plan, 20.0, gain);
            Assert.True(p.Afflict(cut, head, p.TissueIndex(head, "Larynx"), 60));
            var input = new PhysiologyInput(0.5, 0.5);
            for (var i = 0; i < 3.05 * 120; i++)
            {
                p.Step(1.0 / 120.0, input);
                if (p.Occlusion >= 0.99) return (i + 1) / 120.0;
            }

            return double.PositiveInfinity;
        }

        Assert.True(Seconds(10.0) <= 3.05);
        Assert.True(double.IsPositiveInfinity(Seconds(1.0)));   // at gain 1 it takes 31 s (probe): a dropped gain misses the bound

        // Larynx and Trachea are cores with share 1, the lungs 0.5, every other tissue 0.
        foreach (var part in plan.Parts)
        {
            foreach (var t in part.Tissues)
            {
                if (t.Label is "Larynx" or "Trachea") { Assert.Equal(0.0, t.Thickness); Assert.Equal(1.0, t.AirwayShare); }
                else if (t.Label is "LungLeft" or "LungRight") Assert.Equal(0.5, t.AirwayShare);
                else Assert.Equal(0.0, t.AirwayShare);
            }
        }
    }

    [Fact]
    public void Shares_and_gains_the_physiology_cannot_run_on_are_refused()
    {
        foreach (var bad in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
        {
            var root = new[] { new TissueDef("brain", 10, 1, Consciousness: 1) { AirwayShare = bad } };
            Assert.Throws<ArgumentException>(() => Plan3(root: root));
        }

        foreach (var bad in new[] { 0.0, -1.0, double.PositiveInfinity, double.NaN })
        {
            Assert.Throws<ArgumentException>(() => new Physiology(Body3(), Plan3(), 20.0, bad));
        }
    }
}
