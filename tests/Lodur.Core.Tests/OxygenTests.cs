using Lodur;
using static Lodur.Core.Tests.PhysioFixtures;

namespace Lodur.Core.Tests;

/// <summary>
/// Blood and brain oxygen: the stores, the Breathing capacity and the content that turns brain oxygen into consciousness. The windows are the
/// literature the content constants are fitted to (placeholders, rulings bds-asphyxia-real-time and bds-heart-stop-stagger).
/// </summary>
public sealed class OxygenTests
{
    private const double Dt = 1.0 / 120.0;

    private static readonly BodyPlan Body = BodyPlans.Humanoid();

    private static Physiology Spawn() => new(Body, PhysiologyPlans.Humanoid(), 20);

    /// <summary>The humanoid's content with a smothering that never lifts added to its afflictions.</summary>
    private static Physiology Smothered()
    {
        var h = PhysiologyPlans.Humanoid();
        var choke = new AfflictionDef("Choke", PhysiologyClock.Acute, -1.0 / 40.0, false,
            new[] { new Effect(EffectTarget.Breathing, EffectOp.Factor, EffectDriver.Severity, new[] { (0.0, 0.0), (1.0, 0.0) }) });
        var defs = new List<AfflictionDef>(h.Afflictions) { choke };
        var plan = new PhysiologyPlan(h.Parts, defs, h.OrganismEffects, h.Blood, h.Respiration, h.Airway, h.AfflictionSlots);
        var p = new Physiology(Body, plan, 20);
        Assert.True(p.Afflict(plan.AfflictionIndex("Choke"), Body.PartIndex("Torso"), 0, 1e9));
        return p;
    }

    /// <summary>Steps with the given arousal; effort is the consciousness the last step left (a struggle that fades with the motor ceiling) unless fixed. Returns the second consciousness first fell to 0.1 or below and the second of death (-1 for neither).</summary>
    private static (double Out, double Dead) Run(Physiology p, double arousal, double? effort, double seconds)
    {
        double outAt = -1.0, deadAt = -1.0, c = 1.0;
        var steps = (long)(seconds / Dt);
        for (long i = 0; i < steps && !p.Dead; i++)
        {
            p.Step(Dt, new PhysiologyInput(arousal, effort ?? c));
            c = p.Consciousness;
            var t = (i + 1) * Dt;
            if (outAt < 0.0 && c <= 0.1) outAt = t;
            if (p.Dead) deadAt = t;
        }

        return (outAt, deadAt);
    }

    [Fact]
    public void A_whole_humanoid_at_rest_keeps_its_oxygen_and_full_consciousness()
    {
        var p = Spawn();
        for (var i = 0; i < (int)(600.0 / Dt); i++)
        {
            p.Step(Dt);
            Assert.Equal(1.0, p.Consciousness);
        }

        Assert.True(p.BloodOxygen >= 0.99, $"blood oxygen {p.BloodOxygen}");
        Assert.True(p.BrainOxygen >= 0.99, $"brain oxygen {p.BrainOxygen}");
        Assert.Equal(1.0, p.Breathing);
    }

    [Fact]
    public void A_struggling_smothered_humanoid_is_out_in_60_to_90_s_and_dead_in_134_to_360_s()
    {
        var p = Smothered();
        var (outAt, deadAt) = Run(p, 1.0, null, 400.0);
        Assert.InRange(outAt, 60.0, 90.0);
        Assert.InRange(deadAt, 134.0, 360.0);
        Assert.Equal(DeathCause.Consciousness, p.Cause);
    }

    [Fact]
    public void Calm_is_slower_than_struggling()
    {
        var struggling = Run(Smothered(), 1.0, null, 400.0);
        var calm = Run(Smothered(), 0.0, 0.0, 1200.0);
        Assert.True(calm.Out > struggling.Out, $"calm {calm.Out} s, struggling {struggling.Out} s");
    }

    [Fact]
    public void A_destroyed_heart_staggers_for_seconds_and_kills_through_consciousness()
    {
        var p = Spawn();
        var torso = Body.PartIndex("Torso");
        Assert.True(p.Afflict(PhysiologyPlans.Humanoid().AfflictionIndex("Cut"), torso, p.TissueIndex(torso, "Heart"), 16));
        p.Step(Dt);
        Assert.False(p.Dead);
        Assert.Equal(DeathCause.None, p.Cause);
        Assert.Equal(0.0, p.BloodPumping);

        var (outAt, deadAt) = Run(p, 0.0, 0.0, 60.0);
        Assert.InRange(outAt + Dt, 6.0, 12.0);
        Assert.True(deadAt > outAt, $"out {outAt} s, dead {deadAt} s");
        Assert.Equal(DeathCause.Consciousness, p.Cause);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(20.0)]
    public void One_lung_lost_still_breathes(double slowScale)
    {
        var p = new Physiology(Body, PhysiologyPlans.Humanoid(), slowScale);
        var torso = Body.PartIndex("Torso");
        Assert.True(p.Afflict(PhysiologyPlans.Humanoid().AfflictionIndex("Bruise"), torso, p.TissueIndex(torso, "LungLeft"), 16));
        double c = 1.0;
        for (var i = 0; i < (int)(300.0 / Dt); i++)
        {
            p.Step(Dt, new PhysiologyInput(1.0, c));
            c = p.Consciousness;
            Assert.True(c >= 0.9, $"consciousness {c} at step {i}");
        }

        Assert.Equal(0.5, p.Breathing);   // the bruise is a wound: real seconds, so the slow scale cannot heal it
        // Half the ventilation settles the blood store at 1 - use / (uptake x breathing) = 0.978 here: uptake 1.0 gives that, 0.5 gives 0.956, 2.0 gives 0.989;
        // the bounds are the geometric midpoints of the deficit (ruling deterministic-bound-margin).
        Assert.InRange(p.BloodOxygen, 0.969, 0.9844);
        Assert.False(p.Dead);
    }

    [Fact]
    public void A_bruise_heals_identically_at_every_slow_scale()
    {
        double Left(double scale)
        {
            var p = new Physiology(Body, PhysiologyPlans.Humanoid(), scale);
            var torso = Body.PartIndex("Torso");
            Assert.True(p.Afflict(PhysiologyPlans.Humanoid().AfflictionIndex("Bruise"), torso, p.TissueIndex(torso, "LungLeft"), 16));
            for (var i = 0; i < (int)(300.0 / Dt); i++) p.Step(Dt);
            Assert.Equal(1, Live(p));
            return p.SlotSeverity(0);
        }

        var real = Left(1.0);
        Assert.Equal(real, Left(20.0));
        Assert.Equal(16.0 - 20.0 * 300.0 / 86400.0, real, 6);   // 20 HP per day of real seconds
    }

    [Fact]
    public void A_lifted_smothering_refills()
    {
        var p = Make(organism: new[] { Hypoxia });
        Assert.True(p.Afflict(Af("Choke"), 0, 0, 1.0));      // lifts at 40 s
        double c = 1.0, lowest = 1.0;
        for (var i = 0; i < (int)(39.0 / Dt); i++)
        {
            p.Step(Dt, new PhysiologyInput(1.0, c));
            c = p.Consciousness;
            lowest = Math.Min(lowest, c);
        }

        Assert.True(lowest < 1.0, "the smothering never dimmed the brain");
        Assert.Equal(0.0, p.Breathing);
        for (var i = 0; i < (int)(31.0 / Dt); i++)
        {
            p.Step(Dt, new PhysiologyInput(1.0, c));
            c = p.Consciousness;
        }

        Assert.Equal(1.0, c);
        Assert.False(p.Dead);
    }

    [Fact]
    public void An_input_outside_zero_to_one_or_not_finite_is_refused_before_anything_changes()
    {
        var p = Spawn();
        foreach (var bad in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => p.Step(Dt, new PhysiologyInput(bad, 0.0)));
            Assert.Throws<ArgumentException>(() => p.Step(Dt, new PhysiologyInput(0.0, bad)));
        }

        Assert.Equal(1.0, p.BloodOxygen);
        p.Step(Dt, new PhysiologyInput(1.0, 1.0));
        Assert.True(p.BloodOxygen < 1.0);
    }
}
