using Lodur;

namespace Lodur.Core.Tests;

/// <summary>The physiology content is validated at construction, and resolved against a body plan by the Physiology: each rule refuses.</summary>
public sealed class PhysiologyPlanTests
{
    private static readonly (double X, double Y)[] Ramp = { (0, 0), (1, 1) };

    private static Effect Fx(EffectTarget t = EffectTarget.Consciousness, EffectDriver d = EffectDriver.BloodLoss, params (double X, double Y)[] curve) =>
        new(t, EffectOp.Offset, d, curve.Length == 0 ? Ramp : curve);

    private static BloodDef GoodBlood => new(0.005, 0.5, 0.001);

    private static PhysiologyPlan Plan(
        IReadOnlyList<TissueDef>? tissues = null, IReadOnlyList<AfflictionDef>? afflictions = null,
        IReadOnlyList<Effect>? organism = null, BloodDef? blood = null, int slots = 4) =>
        new(new[] { new PartTissuesDef("A", Side.Center, tissues ?? new[] { new TissueDef("brain", 10, 1, Consciousness: 1, Motor: 1) }) },
            afflictions ?? new[] { new AfflictionDef("a", PhysiologyClock.Acute, 0, true, Array.Empty<Effect>()) },
            organism ?? Array.Empty<Effect>(), blood ?? GoodBlood, slots);

    private static void Refuses(Action build) => Assert.Throws<ArgumentException>(build);

    [Fact]
    public void A_well_formed_plan_constructs_and_finds_afflictions_by_label()
    {
        var plan = Plan(organism: new[] { Fx() });
        Assert.Equal(0, plan.AfflictionIndex("a"));
        Assert.Throws<ArgumentException>(() => plan.AfflictionIndex("b"));
    }

    [Fact]
    public void A_curve_is_piecewise_linear_clamped_at_the_ends_and_an_equal_x_pair_is_a_step_that_holds_its_later_value_from_x()
    {
        var ramp = Fx(curve: new[] { (0.0, 0.0), (2.0, 1.0) });
        Assert.Equal(0.0, ramp.Eval(-5));
        Assert.Equal(0.25, ramp.Eval(0.5));
        Assert.Equal(1.0, ramp.Eval(2));
        Assert.Equal(1.0, ramp.Eval(9));

        var step = Fx(curve: new[] { (0.0, 0.0), (0.15, 0.0), (0.15, -0.1), (0.30, -0.1) });
        Assert.Equal(0.0, step.Eval(0.1499));
        Assert.Equal(-0.1, step.Eval(0.15));
        Assert.Equal(-0.1, step.Eval(1.0));
    }

    [Fact]
    public void Each_validation_rule_refuses()
    {
        var t = new TissueDef("t", 10, 1, Consciousness: 1, Motor: 1);
        Refuses(() => Plan(new[] { t, t }));                                                   // duplicate tissue label
        Refuses(() => Plan(new[] { t with { Label = "" } }));                                  // empty tissue label
        Refuses(() => Plan(new[] { t with { Hp = 0 } }));
        Refuses(() => Plan(new[] { t with { Hp = double.NaN } }));
        Refuses(() => Plan(new[] { t with { Hp = double.PositiveInfinity } }));
        Refuses(() => Plan(new[] { t with { Vascularity = -1 } }));
        Refuses(() => Plan(new[] { t with { Vascularity = double.NaN } }));
        Refuses(() => Plan(new[] { t with { Consciousness = -1 } }));
        Refuses(() => Plan(new[] { t with { BloodPumping = double.NaN } }));
        Refuses(() => Plan(new[] { t with { Motor = double.PositiveInfinity } }));

        var sheet = new SheetDef(0.1, 1e-4, 1e-3, 1e6);
        var shell = t with { Label = "shell", Thickness = 0.01, Sheet = sheet };
        Refuses(() => Plan(new[] { t with { Thickness = -1 } }));
        Refuses(() => Plan(new[] { t with { Thickness = double.NaN } }));
        Refuses(() => Plan(new[] { t with { Sheet = sheet } }));                               // a sheet on Thickness 0
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { Spacing = 0 } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { Spacing = double.NaN } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { EdgeCompliance = -1 } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { EdgeCompliance = double.NaN } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { AttachCompliance = -1 } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { TensileStrength = 0 } } }));
        Refuses(() => Plan(new[] { shell with { Sheet = sheet with { TensileStrength = double.NaN } } }));
        Refuses(() => Plan(new[] { t, shell }));                                               // a shell after a core

        var a = new AfflictionDef("a", PhysiologyClock.Acute, 0, true, Array.Empty<Effect>());
        Refuses(() => Plan(afflictions: new[] { a, a }));                                      // duplicate affliction label
        Refuses(() => Plan(afflictions: new[] { a with { Label = "" } }));
        Refuses(() => Plan(afflictions: new[] { a with { SeverityRate = double.NaN } }));
        Refuses(() => Plan(afflictions: new[] { a with { Effects = new[] { new Effect(EffectTarget.Consciousness, EffectOp.Offset, EffectDriver.Severity, Array.Empty<(double X, double Y)>()) } } }));
        Refuses(() => Plan(afflictions: new[] { a with { Effects = new[] { Fx(curve: new[] { (1.0, 0.0), (0.5, 0.0) }) } } }));   // x decreases
        Refuses(() => Plan(afflictions: new[] { a with { Effects = new[] { Fx(curve: new[] { (0.0, double.NaN) }) } } }));
        Refuses(() => Plan(afflictions: new[] { a with { Effects = new[] { Fx(curve: new[] { (double.PositiveInfinity, 0.0) }) } } }));

        Refuses(() => Plan(organism: new[] { Fx(d: EffectDriver.Severity) }));                 // a driver only an affliction has
        Refuses(() => Plan(organism: new[] { Fx(d: EffectDriver.Age) }));
        Refuses(() => Plan(organism: new[] { Fx(t: EffectTarget.Bleed) }));                    // a target only an affliction has
        Refuses(() => Plan(organism: new[] { Fx(t: EffectTarget.Motor) }));

        Refuses(() => Plan(blood: GoodBlood with { NominalVolume = 0 }));
        Refuses(() => Plan(blood: GoodBlood with { NominalVolume = double.NaN }));
        Refuses(() => Plan(blood: GoodBlood with { LethalLossFraction = 0 }));
        Refuses(() => Plan(blood: GoodBlood with { LethalLossFraction = 1.5 }));
        Refuses(() => Plan(blood: GoodBlood with { RegenPerSecond = -1 }));
        Refuses(() => Plan(slots: 0));
    }

    [Fact]
    public void A_positional_tissue_has_no_thickness_and_no_sheet_and_zero_compliance_is_accepted()
    {
        var t = new TissueDef("t", 10, 1, Consciousness: 1, Motor: 1);
        Assert.Equal(0.0, t.Thickness);
        Assert.Null(t.Sheet);
        Plan(new[] { t });
        Plan(new[] { t with { Label = "s", Thickness = 0.01, Sheet = new SheetDef(0.1, 0, 0, 1) }, t });
    }

    [Fact]
    public void An_affliction_may_use_every_driver_and_the_boundary_values_of_the_rules_are_accepted()
    {
        var effects = new[] { Fx(EffectTarget.Bleed, EffectDriver.Severity), Fx(EffectTarget.Motor, EffectDriver.Age), Fx(EffectTarget.Consciousness, EffectDriver.BloodLoss), Fx(curve: new[] { (1.0, 0.0), (1.0, 1.0) }) };
        Plan(afflictions: new[] { new AfflictionDef("a", PhysiologyClock.Slow, -1, false, effects) }, blood: GoodBlood with { LethalLossFraction = 1, RegenPerSecond = 0 }, slots: 1);
    }

    [Fact]
    public void The_plan_owns_a_copy_of_what_it_was_given()
    {
        var curve = new List<(double X, double Y)> { (0, 0), (1, 1) };
        var tissues = new List<TissueDef> { new("t", 10, 1, Consciousness: 1, Motor: 1) };
        var plan = Plan(tissues, organism: new[] { new Effect(EffectTarget.Consciousness, EffectOp.Offset, EffectDriver.BloodLoss, curve) });
        curve[1] = (1, double.NaN);
        tissues.Clear();
        Assert.Equal(1.0, plan.OrganismEffects[0].Eval(1));
        Assert.Single(plan.Parts[0].Tissues);
    }
}
