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

    /// <summary>A cough-free, drain-free airway with a breath that draws the liquid at the openings and holds at 1e-3 of it.</summary>
    private static AirwayDef Quiet => new(150e-6, 1.0, 1.0, 1.0, 1e-3, 0.0, new[] { (0.0, 0.0), (1.0, 0.0) }, 0.0, 0.05);

    private static void Near(double expected, double actual) => Assert.InRange(Math.Abs(expected - actual), 0.0, 1e-12 * Math.Abs(expected));

    private static Physiology Bleeding(double share, double gain, AirwayDef? airway = null, BloodDef? blood = null)
    {
        var root = new[]
        {
            new TissueDef("brain", 10, 1, Consciousness: 1),
            new TissueDef("heart", 10, 4, true, BloodPumping: 1),
            new TissueDef("muscle", 10, 1, Motor: 1) { AirwayShare = share },
        };
        var p = new Physiology(Body3(), Plan3(root: root, airway: airway ?? Quiet, blood: blood), 20.0, gain);
        Assert.True(p.Afflict(Af("Bleeder"), 0, RootMuscle, 1));
        return p;
    }

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
        }

        Assert.Equal(0.0, p.AirwayBleed);
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
