using Lodur;
using static Lodur.Core.Tests.PhysioFixtures;

namespace Lodur.Core.Tests;

/// <summary>The Physiology's rules: resolution against a body, integrity from injuries, capacities, the clocks, bleeding, death, the bounded table.</summary>
public sealed class PhysiologyTests
{
    private static void Afflict(Physiology p, string label, int part, int layer, double severity) =>
        Assert.True(p.Afflict(Af(label), part, layer, severity));

    // Resolution against a body.

    private static BodyPlan Tiny()
    {
        PartDef P(string label) => new(label, Side.Center, PartShape.Box, (0.1f, 0.1f, 0.1f), (0f, 0f, 0f), Quat.Identity, 1000f, 0.8f, 0f);
        var joint = new JointDef("J", Side.Center, JointKind.Spherical, 0, 1, (0f, 0f, 0f), (0f, 1f, 0f), 0f, 0f, 0f, new MuscleGains(1f, 1f, 1f, 1f));
        return new BodyPlan(new[] { P("Root"), P("Kid") }, new[] { joint }, Array.Empty<SensePointDef>());
    }

    private static PartTissuesDef Stack(string part, params TissueDef[] tissues) => new(part, Side.Center, tissues);

    private static PhysiologyPlan Over(params PartTissuesDef[] parts) =>
        new(parts, Array.Empty<AfflictionDef>(), Array.Empty<Effect>(), DefaultBlood, 4);

    private static readonly TissueDef BrainT = new("brain", 10, 1, Consciousness: 1);
    private static readonly TissueDef MuscleT = new("muscle", 10, 1, Motor: 1);

    [Fact]
    public void A_physiology_resolves_a_plan_whose_stacks_cover_the_body_parts_exactly()
    {
        var ok = new Physiology(Tiny(), Over(Stack("Root", BrainT), Stack("Kid", MuscleT)), 20);
        Assert.Equal(1, ok.JointCount);
        Assert.Equal(1.0, ok.Consciousness);
        Assert.Equal(1.0, ok.MotorCeiling(0));
    }

    [Fact]
    public void A_physiology_refuses_a_body_part_with_no_stack_a_stack_naming_no_part_and_a_part_with_two_stacks()
    {
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", BrainT)), 20));
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", BrainT), Stack("Kid", MuscleT), Stack("Ghost", MuscleT)), 20));
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", BrainT), Stack("Kid", MuscleT), Stack("Kid", MuscleT)), 20));
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(),
            Over(Stack("Root", BrainT), Stack("Kid", MuscleT) with { Side = Side.Left }), 20));   // the side is part of the name
    }

    [Fact]
    public void A_physiology_refuses_a_body_with_no_consciousness_tissue_or_a_joint_child_with_no_motor_tissue()
    {
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", MuscleT), Stack("Kid", MuscleT)), 20));
        Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", BrainT), Stack("Kid", BrainT)), 20));
    }

    [Fact]
    public void A_physiology_refuses_a_slow_scale_that_is_not_finite_and_above_zero()
    {
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => new Physiology(Tiny(), Over(Stack("Root", BrainT), Stack("Kid", MuscleT)), bad));
        }
    }

    // Integrity.

    [Fact]
    public void Integrity_is_derived_from_the_injuries_bound_to_a_tissue()
    {
        var p = Make();
        Afflict(p, "Lesion", 1, 0, 3);
        Afflict(p, "Lesion", 1, 0, 2);          // a second instance, never merged
        Assert.Equal(2, Live(p));
        Assert.Equal(1.0, p.Integrity(1, 0));   // nothing has stepped: integrity is a step's output
        p.Step(0.01);
        Assert.Equal(0.5, p.Integrity(1, 0));
        Assert.Equal(1.0, p.Integrity(2, 0));
        Assert.Equal(1.0, p.Integrity(0, p.TissueIndex(0, "brain")));
    }

    [Fact]
    public void An_affliction_that_does_not_injure_leaves_integrity_whole()
    {
        var p = Make();
        Afflict(p, "Mark", 1, 0, 9);
        p.Step(0.01);
        Assert.Equal(1.0, p.Integrity(1, 0));
    }

    [Fact]
    public void Healing_to_zero_severity_frees_the_slot_and_restores_integrity()
    {
        var p = Make();
        Afflict(p, "Heal", 1, 0, 1.0);          // rate -1 per second
        p.Step(0.5);
        Assert.Equal(0.95, p.Integrity(1, 0), 12);
        Assert.Equal(1, Live(p));
        p.Step(0.5);                            // severity reaches 0: freed
        Assert.Equal(0, Live(p));
        Assert.Equal(1.0, p.Integrity(1, 0));
    }

    [Fact]
    public void A_wound_past_the_tissues_hp_leaves_integrity_at_zero_not_below()
    {
        var p = Make();
        Afflict(p, "Lesion", 1, 0, 25);
        p.Step(0.01);
        Assert.Equal(0.0, p.Integrity(1, 0));
        Assert.Equal(0.0, p.MotorCeiling(0));
    }

    [Fact]
    public void Afflict_refuses_a_severity_that_is_not_finite_and_above_zero_and_names_that_are_not_there()
    {
        var p = Make();
        foreach (var bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => p.Afflict(Af("Lesion"), 1, 0, bad));
        }

        Assert.Throws<ArgumentException>(() => p.Afflict(-1, 1, 0, 1));
        Assert.Throws<ArgumentException>(() => p.Afflict(Defs.Length, 1, 0, 1));
        Assert.Throws<ArgumentException>(() => p.Afflict(0, 3, 0, 1));
        Assert.Throws<ArgumentException>(() => p.Afflict(0, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => p.Afflict(0, 1, -1, 1));
        Assert.Throws<ArgumentException>(() => p.TissueIndex(1, "brain"));
        Assert.Equal(0, Live(p));
        Assert.Equal(0, p.Refused);
    }

    // Capacities.

    [Fact]
    public void Capacities_fold_offsets_then_factors()
    {
        var p = Make();
        var brain = p.TissueIndex(0, "brain");
        Afflict(p, "Lesion", 0, brain, 6);
        p.Step(0.01);
        Assert.Equal(0.4, p.Consciousness, 12);

        Afflict(p, "Dim", 0, brain, 1);         // Offset -0.1
        Afflict(p, "Halve", 0, brain, 1);       // Factor 0.5
        p.Step(0.01);
        Assert.Equal((0.4 - 0.1) * 0.5, p.Consciousness, 12);   // not 0.4 x 0.5 - 0.1
    }

    [Fact]
    public void A_capacity_is_the_weighted_mean_integrity_of_the_tissues_that_carry_it()
    {
        var root = new[]
        {
            new TissueDef("left", 10, 1, Consciousness: 1),
            new TissueDef("right", 10, 1, Consciousness: 3),
            new TissueDef("skin", 10, 1, Motor: 1),
            new TissueDef("heart", 10, 4, BloodPumping: 1),
        };
        var p = Make(root: root);
        Afflict(p, "Lesion", 0, 0, 5);          // left at 0.5, right whole
        p.Step(0.01);
        Assert.Equal((0.5 * 1 + 1.0 * 3) / 4, p.Consciousness, 12);
        Assert.Equal(1.0, p.BloodPumping);
        Afflict(p, "Lesion", 0, 2, 10);          // a tissue with no consciousness weight does not touch it
        p.Step(0.01);
        Assert.Equal((0.5 * 1 + 1.0 * 3) / 4, p.Consciousness, 12);
    }

    [Fact]
    public void Blood_pumping_is_one_when_no_tissue_pumps()
    {
        var p = Make(root: new[] { new TissueDef("brain", 10, 1, Consciousness: 1), new TissueDef("muscle", 10, 1, Motor: 1) });
        Afflict(p, "Bleeder", 0, 1, 1);
        p.Step(0.01);
        Assert.Equal(1.0, p.BloodPumping);
        Assert.Equal(1e-3, p.Bleed(0), 15);
    }

    [Fact]
    public void An_organism_effect_reads_the_blood_loss_as_the_step_began()
    {
        var organism = new[] { new Effect(EffectTarget.Consciousness, EffectOp.Offset, EffectDriver.BloodLoss, new[] { (0.0, 0.0), (0.2, 0.0), (0.2, -0.3), (1.0, -0.3) }) };
        var p = Make(organism: organism);
        Afflict(p, "Bleeder", 2, 0, 1);
        while (p.BloodLoss < 0.2) p.Step(0.01);
        Assert.True(p.Consciousness == 1.0, "the step that crosses the stage has not read it yet");
        p.Step(0.01);
        Assert.Equal(0.7, p.Consciousness, 12);
    }

    // Motor ceiling.

    [Fact]
    public void The_motor_ceiling_is_the_child_parts_integrity_and_its_effects_times_consciousness()
    {
        var p = Make();
        Afflict(p, "Lesion", 1, 0, 4);          // A's muscle at 0.6
        Afflict(p, "MotorHalf", 2, 0, 1);       // Factor 0.5 on B
        p.Step(0.01);
        Assert.Equal(0.6, p.MotorCeiling(0), 12);
        Assert.Equal(0.5, p.MotorCeiling(1), 12);

        Afflict(p, "Lesion", 0, BrainLayer, 4);      // consciousness 0.6 scales every ceiling
        p.Step(0.01);
        Assert.Equal(0.6, p.Consciousness, 12);
        Assert.Equal(0.6 * 0.6, p.MotorCeiling(0), 12);
        Assert.Equal(0.5 * 0.6, p.MotorCeiling(1), 12);
    }

    [Fact]
    public void A_motor_effect_binds_to_the_part_it_was_afflicted_on_not_to_the_body()
    {
        var p = Make();
        Afflict(p, "MotorHalf", 1, 0, 1);
        p.Step(0.01);
        Assert.Equal(0.5, p.MotorCeiling(0));
        Assert.Equal(1.0, p.MotorCeiling(1));
        Assert.Equal(1.0, p.Consciousness);
    }

    [Fact]
    public void A_motor_ceiling_is_the_motor_weighted_mean_then_offsets_then_factors()
    {
        var a = new[] { new TissueDef("light", 10, 1, Motor: 1), new TissueDef("heavy", 10, 1, Motor: 3) };
        var p = Make(limbA: a);
        Afflict(p, "Lesion", 1, 0, 10);            // the light layer is gone: (1 x 0 + 3 x 1) / 4
        p.Step(0.01);
        Assert.Equal(0.75, p.MotorCeiling(0), 12);

        Afflict(p, "MotorDim", 1, 0, 1);           // offset -0.2
        Afflict(p, "MotorHalf", 1, 0, 1);          // factor 0.5
        p.Step(0.01);
        Assert.Equal((0.75 - 0.2) * 0.5, p.MotorCeiling(0), 12);   // offsets before factors
        Assert.Equal(1.0, p.MotorCeiling(1));
    }

    // Clocks.

    [Fact]
    public void A_slow_affliction_moves_on_the_scaled_clock_and_an_acute_one_on_the_callers()
    {
        var p = Make(slowScale: 20);
        Afflict(p, "SlowHeal", 1, 0, 5);
        Afflict(p, "Heal", 2, 0, 5);
        for (var i = 0; i < 10; i++) p.Step(0.01);
        var slow = 0;
        var acute = 0;
        for (var s = 0; s < p.SlotCount; s++)
        {
            if (p.SlotDef(s) == Af("SlowHeal")) slow = s;
            if (p.SlotDef(s) == Af("Heal")) acute = s;
        }

        Assert.Equal(5 - 10 * 20 * 1 * 0.01, p.SlotSeverity(slow), 12);
        Assert.Equal(5 - 10 * 1 * 0.01, p.SlotSeverity(acute), 12);
        Assert.Equal(10 * 20 * 0.01, p.SlotAge(slow), 12);   // age counts the clock's seconds
        Assert.Equal(10 * 0.01, p.SlotAge(acute), 12);
    }

    [Fact]
    public void Bleeding_is_real_time_whatever_the_slow_scale()
    {
        double Remaining(double scale)
        {
            var p = Make(slowScale: scale);
            Afflict(p, "Bleeder", 1, 0, 1);
            for (var i = 0; i < 200; i++) p.Step(0.01);
            return p.BloodVolume;
        }

        Assert.Equal(Remaining(1), Remaining(20));
        Assert.Equal(0.005 - 1e-3 * 2.0, Remaining(1), 12);
    }

    [Fact]
    public void Blood_regrows_only_while_nothing_bleeds_and_on_the_slow_clock()
    {
        double Regrown(double scale)
        {
            var p = Make(slowScale: scale, blood: DefaultBlood with { RegenPerSecond = 1e-5 });
            Afflict(p, "Fading", 1, 0, 1);       // bleeds, shrinking by 0.1 severity per second, until it is gone
            var last = p.BloodVolume;
            while (Live(p) > 0)
            {
                p.Step(0.01);
                if (Live(p) > 0) Assert.True(p.BloodVolume <= last, "blood grew while something bled");
                last = p.BloodVolume;
            }

            var settled = p.BloodVolume;
            for (var i = 0; i < 100; i++) p.Step(0.01);
            Assert.True(p.BloodVolume < 0.005);
            return (p.BloodVolume - settled) / scale;
        }

        Assert.Equal(1e-5 * 0.005 * 100 * 0.01, Regrown(1), 12);
        Assert.Equal(Regrown(1), Regrown(20), 12);
    }

    [Fact]
    public void Regrowth_is_capped_at_the_nominal_volume()
    {
        var p = Make(blood: DefaultBlood with { RegenPerSecond = 10 });
        Afflict(p, "Fading", 1, 0, 1);
        for (var i = 0; i < 2000; i++) p.Step(0.01);
        Assert.Equal(0.005, p.BloodVolume);
    }

    // Bleeding.

    [Fact]
    public void Volume_falls_by_the_sum_of_the_parts_bleed_times_dt()
    {
        var p = Make();
        Afflict(p, "Bleeder", 1, 0, 1);
        Afflict(p, "Bleeder", 2, 0, 0.5);
        Afflict(p, "Bleeder", 0, RootMuscle, 0.25);
        for (var i = 0; i < 20; i++)
        {
            var before = p.BloodVolume;
            p.Step(0.05);
            var sum = p.Bleed(0) + p.Bleed(1) + p.Bleed(2);
            Assert.Equal(before - sum * 0.05, p.BloodVolume, 15);
        }

        Assert.Equal(1e-3, p.Bleed(1), 15);                       // severity 1 x vascularity 1 x pumping 1
        Assert.Equal(1e-3 * 0.5, p.Bleed(2), 15);
        Assert.Equal(1e-3 * 0.25, p.Bleed(0), 15);
    }

    [Fact]
    public void Bleeding_goes_through_the_tissues_vascularity()
    {
        var p = Make();
        Afflict(p, "Bleeder", 0, Heart, 1);        // the heart's vascularity is 4
        Afflict(p, "Bleeder", 0, RootMuscle, 1);   // the muscle's is 1
        p.Step(0.01);
        Assert.Equal(1e-3 * 4 + 1e-3, p.Bleed(0), 15);
    }

    [Fact]
    public void An_affliction_never_bleeds_negatively_nor_cancels_another_afflictions_bleed()
    {
        var p = Make();
        Afflict(p, "Staunch", 1, 0, 1);
        p.Step(0.01);
        Assert.Equal(0.0, p.Bleed(1));
        Assert.Equal(0.005, p.BloodVolume);

        Afflict(p, "Bleeder", 1, 0, 1);
        p.Step(0.01);
        Assert.Equal(1e-3, p.Bleed(1), 15);        // the staunch slot clamps at 0 on its own; it does not subtract from the bleeder
    }

    [Fact]
    public void A_dead_body_reads_no_bleed_whatever_it_was_bleeding()
    {
        var p = Make();
        Afflict(p, "Bleeder", 1, 0, 1);
        Afflict(p, "Lesion", 0, BrainLayer, 10);   // consciousness 0: dead on this step, the heart still pumping
        p.Step(0.01);
        Assert.True(p.Dead);
        Assert.Equal(DeathCause.Consciousness, p.Cause);
        Assert.Equal(0.0, p.Bleed(1));
    }

    [Fact]
    public void A_reused_slot_starts_at_age_zero()
    {
        var p = Make();
        Afflict(p, "Heal", 1, 0, 0.5);             // slot 0, healed away in one second
        p.Step(1.0);
        Assert.Equal(0, Live(p));
        Afflict(p, "Clot", 1, 0, 1);               // reuses slot 0
        Assert.Equal(Af("Clot"), p.SlotDef(0));
        Assert.Equal(0.0, p.SlotAge(0));
        p.Step(0.01);
        Assert.Equal(0.01, p.SlotAge(0), 15);
    }

    [Fact]
    public void Bleeding_reads_the_pumping_so_a_stopped_non_vital_heart_bleeds_nothing()
    {
        var p = Make(heartVital: false);
        Afflict(p, "Bleeder", 1, 0, 1);
        p.Step(0.01);
        Assert.Equal(1e-3, p.Bleed(1), 15);
        Afflict(p, "Lesion", 0, Heart, 10);
        p.Step(0.01);
        Assert.False(p.Dead);
        Assert.Equal(0.0, p.BloodPumping);
        Assert.Equal(0.0, p.Bleed(1));
        var v = p.BloodVolume;
        p.Step(0.01);
        Assert.Equal(v, p.BloodVolume);
    }

    [Fact]
    public void An_age_factor_halves_a_bleed_at_its_age()
    {
        var p = Make(blood: new BloodDef(10.0, 0.5, 0.0));
        Afflict(p, "Clot", 1, 0, 1);
        for (var i = 0; i < 60; i++) p.Step(1.0);
        Assert.Equal(0.5e-3, p.Bleed(1), 15);
        for (var i = 0; i < 240; i++) p.Step(1.0);
        Assert.Equal(0.1e-3, p.Bleed(1), 15);
    }

    // Death.

    [Fact]
    public void A_vital_tissue_at_zero_integrity_kills_on_that_step_and_one_short_is_alive()
    {
        var p = Make();
        Afflict(p, "Lesion", 0, Heart, 9.5);
        p.Step(0.01);
        Assert.False(p.Dead);
        Assert.Equal(DeathCause.None, p.Cause);
        Assert.Equal(-1, p.DiedAtStep);
        Afflict(p, "Lesion", 0, Heart, 0.5);
        p.Step(0.01);
        Assert.True(p.Dead);
        Assert.Equal(DeathCause.VitalTissue, p.Cause);
        Assert.Equal(1, p.DiedAtStep);
    }

    [Fact]
    public void Blood_loss_to_the_lethal_fraction_kills_with_that_cause()
    {
        var p = Make();
        Afflict(p, "Bleeder", 1, 0, 1);
        for (var i = 0; i < 100 && !p.Dead; i++)
        {
            Assert.True(p.BloodLoss < 1.0);
            p.Step(0.1);
        }

        Assert.True(p.Dead);
        Assert.Equal(DeathCause.BloodLoss, p.Cause);
        Assert.True(p.BloodLoss >= 1.0 && p.BloodLoss < 1.1);
    }

    [Fact]
    public void A_non_vital_brain_at_zero_integrity_kills_by_consciousness_and_one_short_is_alive()
    {
        var p = Make();
        Afflict(p, "Lesion", 0, BrainLayer, 9.5);
        p.Step(0.01);
        Assert.False(p.Dead);
        Assert.Equal(0.05, p.Consciousness, 12);
        Afflict(p, "Lesion", 0, BrainLayer, 0.5);
        p.Step(0.01);
        Assert.True(p.Dead);
        Assert.Equal(DeathCause.Consciousness, p.Cause);
    }

    [Fact]
    public void Consciousness_driven_to_zero_by_effects_kills_too_and_the_first_rule_names_the_cause()
    {
        var p = Make(slots: 16);
        for (var i = 0; i < 10; i++) Afflict(p, "Dim", 0, BrainLayer, 1);
        p.Step(0.01);
        Assert.False(p.Dead);                     // ten offsets of -0.1 leave a hair above zero in doubles
        Afflict(p, "Dim", 0, BrainLayer, 1);
        p.Step(0.01);
        Assert.True(p.Dead);
        Assert.Equal(DeathCause.Consciousness, p.Cause);

        var both = Make();
        Afflict(both, "Lesion", 0, BrainLayer, 10);
        Afflict(both, "Lesion", 0, Heart, 10);
        both.Step(0.01);
        Assert.Equal(DeathCause.VitalTissue, both.Cause);   // the vital rule comes first
    }

    [Fact]
    public void When_several_rules_hold_in_one_step_the_first_in_the_ruled_order_names_the_cause()
    {
        // One 3 s step: a 1 mL/s bleed loses 3 mL of a 5 mL body (past the lethal half) while the brain is wiped out.
        var vitalBrain = new[] { new TissueDef("brain", 10, 1, Vital: true, Consciousness: 1), new TissueDef("heart", 10, 4, BloodPumping: 1), new TissueDef("muscle", 10, 1, Motor: 1) };
        var vital = Make(root: vitalBrain);
        Afflict(vital, "Bleeder", 1, 0, 1);
        Afflict(vital, "Lesion", 0, 0, 10);
        vital.Step(3.0);
        Assert.True(vital.BloodLoss >= 1.0);
        Assert.Equal(DeathCause.VitalTissue, vital.Cause);       // vital before blood

        var plain = Make();
        Afflict(plain, "Bleeder", 1, 0, 1);
        Afflict(plain, "Lesion", 0, BrainLayer, 10);
        plain.Step(3.0);
        Assert.True(plain.BloodLoss >= 1.0 && plain.Consciousness == 0.0);
        Assert.Equal(DeathCause.BloodLoss, plain.Cause);         // blood before consciousness
    }

    [Fact]
    public void Death_is_permanent_and_limp_and_nothing_changes_after_it_but_the_table_still_takes_wounds()
    {
        var p = Make();
        Afflict(p, "Bleeder", 1, 0, 1);
        Afflict(p, "Clot", 2, 0, 1);
        Afflict(p, "Lesion", 0, Heart, 10);
        p.Step(0.01);
        Assert.True(p.Dead);
        Assert.Equal(0, p.DiedAtStep);
        Assert.Equal(0.0, p.Consciousness);
        Assert.Equal(0.0, p.BloodPumping);
        Assert.Equal(0.0, p.Bleed(1));
        Assert.Equal(0.0, p.MotorCeiling(0));
        Assert.Equal(0.0, p.MotorCeiling(1));

        var volume = p.BloodVolume;
        var severity = new double[p.SlotCount];
        var age = new double[p.SlotCount];
        for (var s = 0; s < p.SlotCount; s++) { severity[s] = p.SlotSeverity(s); age[s] = p.SlotAge(s); }
        var integrity = p.Integrity(0, Heart);
        for (var i = 0; i < 100; i++) p.Step(1.0);
        Assert.Equal(volume, p.BloodVolume);
        Assert.Equal(0, p.DiedAtStep);
        Assert.Equal(DeathCause.VitalTissue, p.Cause);
        for (var s = 0; s < p.SlotCount; s++) { Assert.Equal(severity[s], p.SlotSeverity(s)); Assert.Equal(age[s], p.SlotAge(s)); }
        Assert.Equal(integrity, p.Integrity(0, Heart));

        Assert.True(p.Afflict(Af("Lesion"), 1, 0, 1));          // the wound is recorded, nothing steps
        Assert.Equal(4, Live(p));
        p.Step(1.0);
        Assert.Equal(1.0, p.Integrity(1, 0));
    }

    // The table.

    [Fact]
    public void A_full_table_refuses_the_next_wound_and_overwrites_nothing()
    {
        var p = Make(slots: 4);
        for (var i = 0; i < 4; i++) Assert.True(p.Afflict(Af("Lesion"), 1, 0, 1 + i));
        Assert.False(p.Afflict(Af("Lesion"), 1, 0, 50));
        Assert.Equal(1, p.Refused);
        for (var s = 0; s < 4; s++) Assert.Equal(1.0 + s, p.SlotSeverity(s));
        Assert.False(p.Afflict(Af("Lesion"), 2, 0, 50));
        Assert.Equal(2, p.Refused);
        p.Step(0.01);
        Assert.Equal(0.0, p.Integrity(1, 0));   // 1+2+3+4 = 10 of 10
        Assert.Equal(1.0, p.Integrity(2, 0));
    }

    [Fact]
    public void A_freed_slot_takes_the_next_wound()
    {
        var p = Make(slots: 1);
        Assert.True(p.Afflict(Af("Heal"), 1, 0, 0.5));
        Assert.False(p.Afflict(Af("Lesion"), 1, 0, 1));
        p.Step(1.0);
        Assert.Equal(0, Live(p));
        Assert.True(p.Afflict(Af("Lesion"), 1, 0, 1));
    }

    // Determinism.

    private static (long Volume, long Consciousness, long Motor) Bits(Physiology p) =>
        (BitConverter.DoubleToInt64Bits(p.BloodVolume), BitConverter.DoubleToInt64Bits(p.Consciousness), BitConverter.DoubleToInt64Bits(p.MotorCeiling(0)));

    [Fact]
    public void Two_identical_runs_agree_bit_for_bit_every_step()
    {
        Physiology Run()
        {
            var p = Make(slowScale: 20, blood: new BloodDef(1.0, 0.5, 1e-4));
            Afflict(p, "Bleeder", 1, 0, 0.7);
            Afflict(p, "Clot", 2, 0, 1);
            Afflict(p, "SlowHeal", 0, BrainLayer, 3);
            Afflict(p, "Fading", 0, RootMuscle, 1);
            return p;
        }

        var a = Run();
        var b = Run();
        for (var i = 0; i < 3000; i++)
        {
            a.Step(0.01);
            b.Step(0.01);
            Assert.Equal(Bits(a), Bits(b));
        }

        Assert.True(a.BloodVolume < 1.0 && !a.Dead);
        Assert.True(a.Consciousness > 0.9);
    }
}
