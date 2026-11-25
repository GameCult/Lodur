using Lodur;
using static Lodur.Core.Tests.PhysioFixtures;

namespace Lodur.Core.Tests;

/// <summary>
/// Air hunger, the struggle it drives, the breath held on will and ended by a gasp, cough and drainage (rulings bds-airway-cough-hold-drain, bds-asphyxia-real-time).
/// The timed tests run on the real Actor over a Body3 with a lung and the humanoid's respiration and airway, at 120 Hz and slow scale 1 and 20 (the gases are on the Acute clock, so the
/// readings are identical). Every bound is a deterministic one (ruling deterministic-bound-margin): the geometric midpoint of the shipped worst and the least claimed mutant, or the
/// ruled literature window where that is tighter; a zero reading counts as half a unit for the shipped side and a whole unit for a mutant (ruling deterministic-bound-zero-reading).
/// </summary>
public sealed class AirwayPhysiologyTests
{
    private const double Dt = 1.0 / 120.0;
    private static readonly float[] NoPoint = { 0f };

    /// <summary>The humanoid's HoldAt: the liquid at the openings at which the breath is held.</summary>
    private static readonly double HoldAt = PhysiologyPlans.Humanoid().Airway.HoldAt;

    private static readonly double Capacity = PhysiologyPlans.Humanoid().Airway.Capacity;

    /// <summary>The humanoid's DrawPerSecond, a placeholder: the tests that read what a free breath draws read it from here.</summary>
    private static readonly double Draw = PhysiologyPlans.Humanoid().Airway.DrawPerSecond;

    private static Brain RestOnly() => new()
    {
        Goals = new[]
        {
            new GoalSpec
            {
                Id = "rest", Priority = 0.0,
                Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = new HoldParameters { HoldStrength = 0.7f } },
            },
        },
    };

    private static Actor Real(Physiology p, Brain brain, WithdrawalParameters? breath = null) =>
        new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain, physiology: p, breath: breath);

    private static Physiology Smothered(double slowScale)
    {
        var p = Breather(slowScale);
        Assert.True(p.Afflict(Af("Choke"), 0, 0, 1e9));
        return p;
    }

    private readonly record struct Smother(double Struggle, double Out, double Dead, double Effort, double Arousal30, DeathCause Cause);

    /// <summary>Smothers (Breathing 0 for good) a real Actor with no stimulus: when the struggle goal took over, when consciousness first fell to 0.1, when it died, the mean strength written while awake, and the arousal at 30 s.</summary>
    private static Smother Suffocate(double slowScale, Brain brain)
    {
        var p = Smothered(slowScale);
        var a = Real(p, brain);
        var s = new float[2];
        var q = new Quat[2];
        double struggle = -1, outAt = -1, deadAt = -1, arousal = -1, effort = 0;
        long awake = 0;
        for (long i = 0; i < (long)(500.0 / Dt) && !p.Dead; i++)
        {
            var e = a.Tick(NoPoint, NoPoint, NoPoint, default, s, q);
            var t = (i + 1) * Dt;
            if (struggle < 0 && a.GoalIndex == 1) struggle = t;
            if (outAt < 0 && p.Consciousness <= 0.1) outAt = t;
            if (outAt < 0)
            {
                effort += (s[0] + s[1]) / 2.0;
                awake++;
            }

            if (arousal < 0 && t >= 30.0) arousal = a.Drive.Arousal;
            if ((e & TickEvents.Died) != 0) deadAt = t;
        }

        return new Smother(struggle, outAt, deadAt, effort / awake, arousal, p.Cause);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(20.0)]
    public void Smothered_the_real_Actor_struggles_and_is_out_in_the_ruled_window(double slowScale)
    {
        // Shipped (placeholder content, probe and real Actor agree): struggle at 0.11 s, out 81.6 s, dead 154.3 s, mean awake effort 0.72. The fading-effort finding: the same Actor with only the rest goal
        // (the will broken by arousal, the goal limp) is out at 219.8 s and dead at 308.4 s; with no deficit term the smothering is out at 100.0 and dead at 188.5 (2.0 and 1.22 times shipped: claimed).
        // Bounds: out 60 (ruling) to 90 (the ruled 90 s, tighter than the midpoint 90.3), dead 134 (ruling) to 170.6 (midpoint of 154.3 and 188.5), effort 0.6 (shipped 0.72 is 1.2 above it; the rest-only reading 0.04 is 17 below).
        var r = Suffocate(slowScale, StruggleBrain());
        Assert.InRange(r.Struggle, 0.0, 2.0);
        Assert.InRange(r.Out, 60.0, 90.0);
        Assert.InRange(r.Dead, 134.0, 170.6);
        Assert.Equal(DeathCause.Consciousness, r.Cause);
        Assert.True(r.Effort > 0.6, $"mean awake effort {r.Effort}");
    }

    [Fact]
    public void Without_a_struggle_goal_the_broken_will_goes_limp_and_the_window_is_missed()
    {
        // The finding, kept as a claim of the content: the rest goal alone is the will-weighted goal that the oxygen tests assumed fades only with consciousness.
        var r = Suffocate(1.0, RestOnly());
        Assert.True(r.Out > 150.0, $"out {r.Out}");
        Assert.True(r.Effort < 0.2, $"effort {r.Effort}");
    }

    [Fact]
    public void Air_hunger_raises_arousal_with_no_sensory_load()
    {
        // Shipped 0.75 at 30 s; with hunger kept out of the drive 0.0, which counts as one unit (0.01): the bound is sqrt(0.75 x 0.01) = 0.0866 (ruling deterministic-bound-zero-reading).
        var r = Suffocate(1.0, RestOnly());
        Assert.True(r.Arousal30 > 0.0866, $"arousal at 30 s {r.Arousal30}");
    }

    private readonly record struct Hold(double FirstLiquid, double MaxBeforeGasp, double Gasp, double WillAtGasp, double After, double Window);

    /// <summary>A real Actor, calm and awake, with liquid at the airway openings from the first tick on, facing down.</summary>
    private static Hold HoldUnderLiquid(double slowScale, WithdrawalParameters? breath = null, double attention = 1.0)
    {
        var p = Breather(slowScale);
        var a = Real(p, StruggleBrain(), breath);
        a.SetAttention(attention);
        var s = new float[2];
        var q = new Quat[2];
        var liquid = new AirwaySample(2.0 * HoldAt, 0.0, -1.0);
        double first = -1, max = 0, gasp = -1, will = -1, after = -1, window = -1;
        for (long i = 0; i < (long)(200.0 / Dt); i++)
        {
            var e = a.Tick(NoPoint, NoPoint, NoPoint, default, s, q, default, liquid);
            var t = (i + 1) * Dt;
            if (i == 0) first = p.AirwayLiquid;
            if (gasp < 0)
            {
                if ((e & TickEvents.Gasp) != 0)
                {
                    gasp = t;
                    will = a.Drive.Will;
                }
                else
                {
                    max = Math.Max(max, p.AirwayLiquid);
                }
            }
            else
            {
                if (window < 0 && t >= gasp + 0.25) window = p.AirwayLiquid;
                if (t >= gasp + 1.0)
                {
                    after = p.AirwayLiquid;
                    break;
                }
            }
        }

        return new Hold(first, max, gasp, will, after, window);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(20.0)]
    public void A_calm_awake_Fella_holds_his_breath_under_liquid_and_gasps_in_45_to_90_s(double slowScale)
    {
        // Shipped gasp 60.06 s (the will gives out first, at urge 0.30); inside the ruled 45-90 s with 1.34 and 1.5 to spare. The first tick draws one tick's inflow (the Actor decides the hold from what
        // the step found), 2.6e-7 m^3; held, nothing more enters and the cough clears it, so the airway never holds more than that until the gasp. With inflow while held it climbs to the cough's balance, 1.6e-5 (60 times).
        var h = HoldUnderLiquid(slowScale);
        Assert.InRange(h.Gasp, 45.0, 90.0);
        // Draw is a placeholder and decides this reading (the first tick's inflow is Draw x Breathing x liquid x dt): it is read from the content, not written here.
        Assert.Equal(Draw * 1.0 * 2.0 * HoldAt * Dt, h.FirstLiquid, 12);
        Assert.True(h.MaxBeforeGasp <= h.FirstLiquid * 1.0000001, $"airway liquid {h.MaxBeforeGasp} while held");
        Assert.Equal(0.0, h.WillAtGasp);
        // Placeholders deciding this test: Draw (the first-tick inflow, and the 1.35e-5 after the gasp); Cough (the airway never holds more than a tick's inflow while held, since it clears at once); HoldAt is
        // content the test reads. The gasp draws the liquid in: a second later the airway holds 1.35e-5 (the held mutant 2.6e-7); the bound is their geometric mean.
        Assert.True(h.After > 1.8e-6, $"airway liquid a second after the gasp {h.After}");
    }

    [Fact]
    public void The_breath_gasps_when_the_hunger_leaks_past_a_will_that_still_holds()
    {
        // Suppression 0 and a low leak threshold: the urge itself crosses the threshold (carbon dioxide 0.54) long before the will gives out.
        var h = HoldUnderLiquid(1.0, new WithdrawalParameters { MaxSuppression = 0.0, FlinchLeakThreshold = 0.05 });
        var shipped = HoldUnderLiquid(1.0);
        Assert.True(h.Gasp > 0.0 && h.Gasp < shipped.Gasp / 1.2, $"leak gasp {h.Gasp}, will gasp {shipped.Gasp}");
        Assert.True(h.WillAtGasp > 0.0, "the will had not given out");
    }

    [Fact]
    public void With_no_will_there_is_no_hold()
    {
        // The will is 0 (attention is the seam): the breath is never held, liquid is drawn at once and no gasp is ever reported. Shipped the airway fills to the cough's balance (1.56e-5) within 5 s;
        // a hold not gated by will stays at one tick's inflow (2.6e-7): the bound is the geometric midpoint, sqrt(1.56e-5 x 2.6e-7) = 2.0e-6.
        var p = Breather(20.0);
        var a = Real(p, StruggleBrain());
        a.SetAttention(0.0);
        var s = new float[2];
        var q = new Quat[2];
        var liquid = new AirwaySample(2.0 * HoldAt, 0.0, -1.0);
        for (long i = 0; i < (long)(5.0 / Dt); i++)
        {
            Assert.Equal(TickEvents.None, a.Tick(NoPoint, NoPoint, NoPoint, default, s, q, default, liquid) & TickEvents.Gasp);
        }

        Assert.True(p.AirwayLiquid > 2.0e-6, $"airway liquid {p.AirwayLiquid}");
    }

    /// <summary>
    /// Fills the airway with a flood (awake, the cough cannot keep up) until it is full, then smothers calmly with no liquid left at it until consciousness first falls to 0.1; returns that physiology,
    /// the airway full. Calm and unventilated the blood store falls by its basal use alone (0.002 per second), so the fall to 0.1 takes 334.5 s; a Fella left face up dies 125 s later.
    /// </summary>
    private static Physiology Drowned()
    {
        var p = Breather(20.0);
        var flood = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(100.0 * HoldAt, 0.0, -1.0));
        for (long i = 0; i < (long)(600.0 / Dt) && p.Consciousness > 0.1; i++) p.Step(Dt, flood);
        Assert.True(p.Consciousness <= 0.1 && !p.Dead, $"consciousness {p.Consciousness}");
        Assert.Equal(Capacity, p.AirwayLiquid);
        return p;
    }

    /// <summary>The unconscious Fella, liquid no longer at the airway, with his face this way; steps for the given seconds.</summary>
    private static Physiology Rolled(double facing, double seconds)
    {
        var p = Drowned();
        var rolled = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(0.0, 0.0, facing));
        for (long i = 0; i < (long)(seconds / Dt) && !p.Dead; i++) p.Step(Dt, rolled);
        return p;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void Face_down_or_on_his_side_the_airway_drains_and_he_lives(double facing)
    {
        // Placeholders: Drain decides nothing here but whether it is above zero (a tiny drain restarts ventilation and the awake cough then clears the airway: probed at 0.1, 0.01, 0.001); Cough decides the recovery once he wakes.
        // Out at 0.1 with the airway full, then drained at 0.1 (facing 1) or 0.05 (facing 0.5) per second: back above 0.5 in 7.0 and 8.5 s (the bound is the 200 s simulated; a drain that waits on consciousness never comes). Face up it never drains and he dies (below).
        var p = Rolled(facing, 200.0);
        Assert.False(p.Dead);
        Assert.True(p.Consciousness > 0.5, $"consciousness {p.Consciousness}");
        Assert.True(p.AirwayLiquid < 1e-6, $"airway liquid {p.AirwayLiquid}");
    }

    [Fact]
    public void Face_up_the_airway_stays_full_and_he_dies_through_consciousness()
    {
        // Decided by the Cough curve (zero below 0.3 consciousness) and by facing; Drain and Draw decide nothing here (nothing drains face up, nothing is drawn, none at the openings).
        var p = Rolled(-1.0, 600.0);
        Assert.True(p.Dead);
        Assert.Equal(DeathCause.Consciousness, p.Cause);
        Assert.Equal(Capacity, p.AirwayLiquid);
        Assert.Equal(0.0, p.Drained);
    }

    private static Physiology Flooded(int dims)
    {
        var p = Breather(20.0);
        for (var d = 0; d < dims; d++) Assert.True(p.Afflict(Af("Dim"), 0, BrainLayer, 1.0));
        var flood = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(100.0 * HoldAt, 0.0, -1.0));
        for (long i = 0; i < (long)(2.0 / Dt); i++) p.Step(Dt, flood);
        Assert.Equal(Capacity, p.AirwayLiquid);
        return p;
    }

    [Fact]
    public void An_awake_Fella_coughs_his_airway_clear()
    {
        // Placeholders deciding this: Cough (2.2 s to clear half, and every bound below); Draw only fills the half airway first and decides no outcome; Drain is 0 facing up and decides nothing.
        // Half the airway, awake, face up, nothing at the openings: gone (below 1e-6, a 75-fold fall) in 10 s; shipped it takes 2.2 s. Without the cough (or with one gated by something else) it never falls.
        var p = Breather(20.0);
        var flood = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(100.0 * HoldAt, 0.0, -1.0));
        while (p.AirwayLiquid < 0.5 * Capacity) p.Step(Dt, flood);
        var clear = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(0.0, 0.0, -1.0));
        double coughed = 0.0;
        for (long i = 0; i < (long)(10.0 / Dt); i++)
        {
            p.Step(Dt, clear);
            coughed += p.Coughed;
        }

        Assert.True(p.AirwayLiquid < 1e-6, $"airway liquid {p.AirwayLiquid}");
        Assert.True(coughed > 0.4 * Capacity, $"coughed {coughed}");
        Assert.Equal(0.0, p.Drained);
    }

    [Fact]
    public void A_stuporous_Fella_cannot_cough()
    {
        // Consciousness held at 0.2 by eight Dim afflictions, below the cough's 0.3: the Cough curve's knee (a placeholder) decides this; Drain decides nothing (facing up).
        var p = Flooded(8);
        Assert.Equal(0.2, p.Consciousness, 9);
        var clear = new PhysiologyInput(0.0, 0.0, false, new AirwaySample(0.0, 0.0, -1.0));
        for (long i = 0; i < (long)(10.0 / Dt); i++)
        {
            p.Step(Dt, clear);
            Assert.Equal(0.0, p.Coughed);
        }

        Assert.Equal(Capacity, p.AirwayLiquid);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(20.0)]
    public void Healthy_and_one_lung_bodies_feel_no_air_hunger_at_rest(double slowScale)
    {
        foreach (var lungLost in new[] { false, true })
        {
            var p = Breather(slowScale);
            if (lungLost) Assert.True(p.Afflict(Af("Lesion"), 0, 2, 5.0));     // half the lung layer: Breathing 0.5
            var a = Real(p, StruggleBrain());
            var s = new float[2];
            var q = new Quat[2];
            for (long i = 0; i < (long)(600.0 / Dt); i++)
            {
                a.Tick(NoPoint, NoPoint, NoPoint, default, s, q);
                Assert.Equal(0.0, p.BreathUrge);
                Assert.Equal(0.0, a.Drive.AirHunger);
            }

            Assert.Equal(lungLost ? 0.5 : 1.0, p.Breathing);
            Assert.True(p.CarbonDioxide < 0.5, $"carbon dioxide {p.CarbonDioxide}");
        }
    }

    [Fact]
    public void After_a_gasp_the_breath_is_free_for_its_window_before_it_is_held_again()
    {
        // The leak gasp (the will still holds): a quarter second later the breath is still free and the airway holds 6.2e-6 (Draw and Cough, placeholders, decide it); with no window (_held = willing) the
        // breath is held the very next tick and the airway holds a tick's inflow, 2.6e-7 or less: the bound is their geometric mean, 1.3e-6.
        var h = HoldUnderLiquid(1.0, new WithdrawalParameters { MaxSuppression = 0.0, FlinchLeakThreshold = 0.05 });
        Assert.True(h.WillAtGasp > 0.0, "the will had not given out");
        Assert.True(h.Window > 1.3e-6, $"airway liquid a quarter second after the gasp {h.Window}");
    }

    [Fact]
    public void Liquid_below_the_hold_threshold_is_breathed_in_and_not_held()
    {
        // Liquid at the openings under HoldAt: the breath is not held, so the airway draws it in and never gasps. With the hold at any liquid the breath is held from the second tick and the airway keeps a tick's draw (6.5e-8). Shipped it holds 3.9e-6 after 5 s (Draw and Cough, placeholders); the bound is the geometric mean, 5.0e-7.
        var p = Breather(20.0);
        var a = Real(p, StruggleBrain());
        var s = new float[2];
        var q = new Quat[2];
        var low = new AirwaySample(0.5 * HoldAt, 0.0, -1.0);
        for (long i = 0; i < (long)(5.0 / Dt); i++)
        {
            Assert.Equal(TickEvents.None, a.Tick(NoPoint, NoPoint, NoPoint, default, s, q, default, low) & TickEvents.Gasp);
        }

        Assert.True(p.AirwayLiquid > 5.0e-7, $"airway liquid {p.AirwayLiquid}");
    }

    [Fact]
    public void A_breath_draws_in_what_the_lung_ventilates()
    {
        // One step from empty (nothing to cough or drain yet): the inflow is Draw x Breathing x liquid x dt, and a lung half lost draws half. An inflow that ignores Breathing draws the same in both.
        double Drawn(bool lungLost)
        {
            var p = Breather(20.0);
            if (lungLost) Assert.True(p.Afflict(Af("Lesion"), 0, 2, 5.0));
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, new AirwaySample(0.5 * HoldAt, 0.0, -1.0)));
            Assert.Equal(lungLost ? 0.5 : 1.0, p.Breathing);
            return p.AirwayLiquid;
        }

        Assert.Equal(Draw * 1.0 * 0.5 * HoldAt * Dt, Drawn(false), 14);
        Assert.Equal(Draw * 0.5 * 0.5 * HoldAt * Dt, Drawn(true), 14);
    }

    [Fact]
    public void A_dead_body_feels_no_urge()
    {
        var p = Smothered(20.0);
        double last = 0.0;
        for (long i = 0; i < (long)(500.0 / Dt) && !p.Dead; i++)
        {
            p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, default));
            if (!p.Dead) last = p.BreathUrge;
        }

        Assert.True(p.Dead);
        Assert.True(last > 0.5, $"urge the step before he died {last}");
        Assert.Equal(0.0, p.BreathUrge);
    }

    [Fact]
    public void An_airway_sample_is_the_sum_of_what_is_live_and_pooled_and_the_greatest_facing()
    {
        var s = AirwaySample.Of(new[] { 1f, 2f }, new[] { 0.5f }, new[] { -0.5f, 0.25f, -1f });
        Assert.Equal(new AirwaySample(3.0, 0.5, 0.25), s);
        Assert.Equal(new AirwaySample(0.0, 0.0, -1.0), AirwaySample.Of(default, default, default));
    }

    [Fact]
    public void A_sample_outside_its_range_is_refused_before_anything_changes()
    {
        var p = Breather(20.0);
        foreach (var bad in new[] { new AirwaySample(-1e-9, 0, 0), new AirwaySample(0, double.NaN, 0), new AirwaySample(double.PositiveInfinity, 0, 0), new AirwaySample(0, 0, 1.01), new AirwaySample(0, 0, -1.01), new AirwaySample(0, 0, double.NaN) })
        {
            Assert.Throws<ArgumentException>(() => p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, bad)));
        }

        Assert.Equal(1.0, p.BloodOxygen);
        Assert.Equal(0.0, p.AirwayLiquid);
        p.Step(Dt, new PhysiologyInput(0.0, 0.0, false, new AirwaySample(1.0, 1.0, 1.0)));
    }

    [Fact]
    public void An_airway_or_urge_curve_the_physiology_cannot_run_on_is_refused()
    {
        var h = PhysiologyPlans.Humanoid();
        PhysiologyPlan With(RespirationDef? r = null, AirwayDef? w = null) =>
            new(h.Parts, h.Afflictions, h.OrganismEffects, h.Blood, r ?? h.Respiration, w ?? h.Airway, h.AfflictionSlots);

        With();
        var ok = h.Airway;
        foreach (var bad in new[]
        {
            ok with { Capacity = 0.0 }, ok with { Capacity = double.NaN }, ok with { DrawPerSecond = -1.0 }, ok with { HoldAt = double.PositiveInfinity },
            ok with { CoughPerSecond = -0.1 }, ok with { DrainPerSecond = double.NaN }, ok with { DrainFacingStart = 1.0 }, ok with { DrainFacingStart = -1.5 },
            ok with { CoughByConsciousness = Array.Empty<(double X, double Y)>() }, ok with { CoughByConsciousness = new[] { (1.0, 0.0), (0.0, 1.0) } },
            ok with { LiveWeight = -1.0 }, ok with { PooledWeight = double.NaN },
        })
        {
            Assert.Throws<ArgumentException>(() => With(w: bad));
        }

        var r = h.Respiration;
        foreach (var bad in new[]
        {
            r with { CarbonDioxideRise = -1.0 }, r with { CarbonDioxideClear = double.NaN },
            r with { UrgeByCarbonDioxide = Array.Empty<(double X, double Y)>() }, r with { UrgeByVentilationDeficit = new[] { (1.0, 0.0), (0.5, 1.0) } },
        })
        {
            Assert.Throws<ArgumentException>(() => With(r: bad));
        }

        // The plan owns its curves: a caller editing the array it passed changes nothing.
        var mine = new[] { (0.0, 0.0), (1.0, 1.0) };
        var plan = With(r: r with { UrgeByCarbonDioxide = mine });
        mine[1] = (1.0, 5.0);
        Assert.Equal(1.0, plan.Respiration.UrgeByCarbonDioxide[1].Y);
    }
}
