using Lodur;

namespace Lodur.Core.Tests;

/// <summary>A three-part, two-joint body and a plain physiology over it, for the tests that are about a rule rather than about the humanoid's content.</summary>
internal static class PhysioFixtures
{
    public static BodyPlan Body3()
    {
        PartDef P(string label) => new(label, Side.Center, PartShape.Box, (0.1f, 0.1f, 0.1f), (0f, 0f, 0f), Quat.Identity, 1000f, 0.8f, 0f);
        JointDef J(string label, int child) => new(label, Side.Center, JointKind.Spherical, 0, child, (0f, 0f, 0f), (0f, 1f, 0f), 0f, 0f, 0f, new MuscleGains(1f, 1f, 1f, 1f));
        return new BodyPlan(new[] { P("Root"), P("A"), P("B") }, new[] { J("J0", 1), J("J1", 2) }, Array.Empty<SensePointDef>());
    }

    public const int BrainLayer = 0, Heart = 1, RootMuscle = 2;

    private static Effect Const(EffectTarget t, EffectOp op, double v) => new(t, op, EffectDriver.Severity, new[] { (0.0, v), (1.0, v) });

    private static AfflictionDef Def(string label, double rate, bool injures, PhysiologyClock clock, params Effect[] effects) => new(label, clock, rate, injures, effects);

    public static readonly AfflictionDef[] Defs =
    {
        Def("Lesion", 0, true, PhysiologyClock.Acute),
        Def("Mark", 0, false, PhysiologyClock.Acute),
        Def("Heal", -1, true, PhysiologyClock.Acute),
        Def("SlowHeal", -1, true, PhysiologyClock.Slow),
        Def("Bleeder", 0, false, PhysiologyClock.Acute, new Effect(EffectTarget.Bleed, EffectOp.Offset, EffectDriver.Severity, new[] { (0.0, 0.0), (1.0, 1e-3) })),
        Def("Dim", 0, false, PhysiologyClock.Acute, Const(EffectTarget.Consciousness, EffectOp.Offset, -0.1)),
        Def("Halve", 0, false, PhysiologyClock.Acute, Const(EffectTarget.Consciousness, EffectOp.Factor, 0.5)),
        Def("MotorHalf", 0, false, PhysiologyClock.Acute, Const(EffectTarget.Motor, EffectOp.Factor, 0.5)),
        Def("Clot", 0, false, PhysiologyClock.Acute,
            Const(EffectTarget.Bleed, EffectOp.Offset, 1e-3),
            new Effect(EffectTarget.Bleed, EffectOp.Factor, EffectDriver.Age, new[] { (0.0, 1.0), (60.0, 0.5), (300.0, 0.1) })),
        Def("MotorDim", 0, false, PhysiologyClock.Acute, Const(EffectTarget.Motor, EffectOp.Offset, -0.2)),
        Def("Staunch", 0, false, PhysiologyClock.Acute, Const(EffectTarget.Bleed, EffectOp.Offset, -1e-3)),
        Def("Fading", -0.1, false, PhysiologyClock.Acute, new Effect(EffectTarget.Bleed, EffectOp.Offset, EffectDriver.Severity, new[] { (0.0, 0.0), (1.0, 1e-5) })),
        Def("SlowBleeder", 0, false, PhysiologyClock.Slow, new Effect(EffectTarget.Bleed, EffectOp.Offset, EffectDriver.Severity, new[] { (0.0, 0.0), (1.0, 1e-3) })),
        // A test smothering: ventilation is zero until it lifts, 40 s at severity 1.
        Def("Choke", -1.0 / 40.0, false, PhysiologyClock.Acute, Const(EffectTarget.Breathing, EffectOp.Factor, 0.0)),
    };

    public static int Af(string label)
    {
        for (var i = 0; i < Defs.Length; i++) if (Defs[i].Label == label) return i;
        throw new ArgumentException(label);
    }

    public static BloodDef DefaultBlood => new(0.005, 0.5, 0.0);

    /// <summary>The same literature-shaped placeholders as the humanoid's content.</summary>
    public static RespirationDef DefaultRespiration => PhysiologyPlans.Humanoid().Respiration;

    /// <summary>No liquid ever counts as at the airway: the airway stays empty and the hold is never wanted.</summary>
    public static AirwayDef InertAirway => new(150e-6, 1.0, 1.0, 1.0, 1e9, 2.0, new[] { (0.3, 0.0), (0.6, 1.0) }, 0.1, 0.05);

    /// <summary>The humanoid's hypoxia curve: brain oxygen to consciousness, flat 1 above 0.70.</summary>
    public static Effect Hypoxia => new(EffectTarget.Consciousness, EffectOp.Factor, EffectDriver.BrainOxygen, new[] { (0.10, 0.0), (0.35, 0.1), (0.70, 1.0) });

    /// <summary>Root: brain (weight 1), heart (pumping 1, vital unless asked otherwise), a muscle; A and B: a muscle each. Every tissue has Hp 10.</summary>
    public static PhysiologyPlan Plan3(bool heartVital = true, BloodDef? blood = null, int slots = 8, IReadOnlyList<Effect>? organism = null, TissueDef[]? root = null, TissueDef[]? limbA = null, AirwayDef? airway = null)
    {
        var stack = root ?? new[]
        {
            new TissueDef("brain", 10, 1, Consciousness: 1),
            new TissueDef("heart", 10, 4, heartVital, BloodPumping: 1),
            new TissueDef("muscle", 10, 1, Motor: 1),
        };
        var limb = new[] { new TissueDef("muscle", 10, 1, Motor: 1) };
        return new PhysiologyPlan(
            new[] { new PartTissuesDef("Root", Side.Center, stack), new PartTissuesDef("A", Side.Center, limbA ?? limb), new PartTissuesDef("B", Side.Center, limb) },
            Defs, organism ?? Array.Empty<Effect>(), blood ?? DefaultBlood, DefaultRespiration, airway ?? InertAirway, slots);
    }

    public static Physiology Make(double slowScale = 20.0, bool heartVital = true, BloodDef? blood = null, int slots = 8, IReadOnlyList<Effect>? organism = null, TissueDef[]? root = null, TissueDef[]? limbA = null) =>
        new(Body3(), Plan3(heartVital, blood, slots, organism, root, limbA), slowScale);

    /// <summary>
    /// Body3 with a lung on the root (Breathing 1), the hypoxia curve, the humanoid's respiration and airway, and the test afflictions (Choke lifts at 40 s at severity 1;
    /// the airway tests bind it at 1e9 to keep it). A real Actor over it needs a brain with a goal that reads air hunger: <see cref="StruggleBrain"/>.
    /// </summary>
    public static Physiology Breather(double slowScale = 20.0)
    {
        var h = PhysiologyPlans.Humanoid();
        var root = new[]
        {
            new TissueDef("brain", 10, 1, Consciousness: 1),
            new TissueDef("heart", 10, 4, true, BloodPumping: 1),
            new TissueDef("lung", 10, 2, Breathing: 1),
            new TissueDef("muscle", 10, 1, Motor: 1),
        };
        var limb = new[] { new TissueDef("muscle", 10, 1, Motor: 1) };
        var plan = new PhysiologyPlan(
            new[] { new PartTissuesDef("Root", Side.Center, root), new PartTissuesDef("A", Side.Center, limb), new PartTissuesDef("B", Side.Center, limb) },
            Defs, new[] { Hypoxia }, DefaultBlood, h.Respiration, h.Airway, 8);
        return new Physiology(Body3(), plan, slowScale);
    }

    /// <summary>The goal that hunger drives: rest holds the pose at 0.7; the struggle, eligible at air hunger 0.3 and above, outranks it, runs involuntary throughout (nothing to break) and loops the two poses at full strength.</summary>
    public static Brain StruggleBrain() => new()
    {
        Goals = new[]
        {
            new GoalSpec
            {
                Id = "rest", Priority = 0.0,
                Behaviour = new PoseBehaviour { Keys = new[] { PainBoxBrain.Hold }, FlinchPose = PainBoxBrain.Withdraw, Hold = new HoldParameters { HoldStrength = 0.7f } },
            },
            new GoalSpec
            {
                Id = "struggle", Priority = 1.0, InvoluntarySeconds = double.PositiveInfinity,
                When = new[] { new Condition(DriveQuantity.AirHunger, ConditionOp.AtLeast, 0.3) },
                Behaviour = new PoseBehaviour
                {
                    Keys = new[] { PainBoxBrain.Hold, PainBoxBrain.Withdraw }, FlinchPose = PainBoxBrain.Withdraw, SecondsPerKey = 0.5,
                    Hold = new HoldParameters { HoldStrength = 1f, FlinchGain = 0.0, StrainShake = 0.0 },
                },
            },
        },
    };

    public static int Live(Physiology p)
    {
        var n = 0;
        for (var s = 0; s < p.SlotCount; s++) if (p.SlotDef(s) >= 0) n++;
        return n;
    }
}
