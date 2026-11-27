using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The humanoid's vessel bands on a real Actor: a knife cut at the BDS knife's severity and the BDS blood gain (ruling bds-blood-gain-simulated; both fixtures here, named, as
/// the BDS client maps them) kills in about 30 s of blood loss when it opens a carotid or the femoral, and drowns him first when it opens the front of the throat
/// (rulings bds-bleed-by-vessels, bds-drown-in-own-blood-first). The Actor's brain clutches on air hunger, as a body that struggles for breath does.
/// </summary>
public sealed class VesselTests
{
    private const double Dt = 0.01;
    private const double KnifeSeverity = 20.0;
    private const double BloodGain = 10.0;

    private static readonly BodyPlan Body = BodyPlans.Humanoid();
    private static readonly PhysiologyPlan Plan = PhysiologyPlans.Humanoid();
    private static readonly float[] NoPoint = { 0f };

    private readonly record struct Band(DeathCause Cause, double DeadAt, double OutAt, double BrainOxygenOut, double BloodLossOut, double MaxOcclusion, double OcclusionFullAt, string[] Layers);

    /// <summary>The identity pose of every joint of the humanoid, the pose the clutch goal holds.</summary>
    private static Quat[] Identity() => Enumerable.Repeat(Quat.Identity, Body.Joints.Count).ToArray();

    private static Brain Clutch() => new()
    {
        Goals = new[]
        {
            new GoalSpec
            {
                Id = "rest", Priority = 0.0,
                Behaviour = new PoseBehaviour { Keys = new[] { Identity() }, FlinchPose = Identity(), Hold = new HoldParameters { HoldStrength = 0.7f } },
            },
            new GoalSpec
            {
                Id = "clutch", Priority = 1.0, InvoluntarySeconds = double.PositiveInfinity,
                When = new[] { new Condition(DriveQuantity.AirHunger, ConditionOp.AtLeast, 0.3) },
                Behaviour = new PoseBehaviour { Keys = new[] { Identity() }, FlinchPose = Identity(), Hold = new HoldParameters { HoldStrength = 1f, FlinchGain = 0.0, StrainShake = 0.0 } },
            },
        },
    };

    /// <summary>One knife wound of the fixture's severity at <paramref name="point"/> (the part's frame) and <paramref name="depth"/>, then the real Actor ticked until it dies.</summary>
    private static Band Cut(string part, Side side, (float X, float Y, float Z) point, double depth)
    {
        var p = new Physiology(Body, Plan, 20.0, BloodGain);
        var a = new Actor(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), Clutch(), physiology: p);
        var pi = Body.PartIndex(part, side);
        var stack = Plan.Parts.First(s => s.PartLabel == part && s.Side == side);
        Span<int> layers = stackalloc int[stack.Tissues.Count];
        var n = stack.Reach(point, depth, layers);
        Assert.True(p.Afflict(Plan.AfflictionIndex("Cut"), pi, layers[..n], KnifeSeverity));
        var names = layers[..n].ToArray().Select(i => stack.Tissues[i].Label).ToArray();

        var s = new float[p.JointCount];
        var q = new Quat[p.JointCount];
        double deadAt = -1, outAt = -1, oxygen = -1, loss = -1, maxOcc = 0, fullAt = -1;
        for (long i = 0; i < (long)(600.0 / Dt) && !p.Dead; i++)
        {
            a.Tick(NoPoint, NoPoint, NoPoint, default, s, q);
            var t = (i + 1) * Dt;
            if (p.Dead) deadAt = t;
            maxOcc = Math.Max(maxOcc, p.Occlusion);
            if (fullAt < 0 && p.Occlusion >= 0.99) fullAt = t;
            if (outAt < 0 && p.Consciousness <= 0.1)
            {
                outAt = t;
                oxygen = p.BrainOxygen;
                loss = p.BloodLoss;
            }
        }

        return new Band(p.Cause, deadAt, outAt, oxygen, loss, maxOcc, fullAt, names);
    }

    // Shipped readings (deterministic, Release): either carotid and either femoral die of BloodLoss at 29.73 s, out at 16.82 s with brain oxygen 0.995 and no occlusion; the front of the throat
    // fills the airway at 1.29 s, is out at 83.46 s with brain oxygen 0.371 and blood loss 0.450 and dies of Consciousness at 167.3 s. Mutants measured the same way: VesselV 1.0 dies at 79.35 s;
    // a carotid with no site (reached at the front of the throat too) at 16.15 s; cough 2.0 fills the airway at 129.95 s, is out at 127.7 s with brain oxygen 0.994 and blood loss 0.6000 and dies of BloodLoss at 380.9 s;
    // larynx V 1.0 is out at 86.61 s with blood loss 0.6001 (brain oxygen 0.376: the hypoxia bound does not claim it).

    [Theory]
    [InlineData("Head", Side.Center, 0.0778f, -0.0778f, 0f, "CarotidLeft")]
    [InlineData("Head", Side.Center, -0.0778f, -0.0778f, 0f, "CarotidRight")]
    [InlineData("Thigh", Side.Left, -0.0495f, 0.12f, 0.0495f, "Femoral")]
    [InlineData("Thigh", Side.Right, 0.0495f, 0.12f, 0.0495f, "Femoral")]
    public void A_vessel_kills_in_about_30_seconds_of_blood_loss_and_does_not_drown(string part, Side side, float x, float y, float z, string vessel)
    {
        // Death in [20, 40] s: shipped 29.73 (ruled about 30). VesselV 1.0 reads 79.35 (2.0 x the upper bound), an unsited carotid 16.15 (1.24 x under the lower bound); the shipped reading is 1.35 and 1.49 inside them.
        // The geometric midpoints (48.6 and 21.9 s) are looser; the ruled band is kept.
        var r = Cut(part, side, (x, y, z), 0.02);
        Assert.Contains(vessel, r.Layers);
        Assert.Equal(DeathCause.BloodLoss, r.Cause);
        Assert.InRange(r.DeadAt, 20.0, 40.0);
        Assert.Equal(0.0, r.MaxOcclusion);   // no airway tissue reached: exactly zero
    }

    [Fact]
    public void The_front_of_the_throat_drowns_him_before_it_bleeds_him_out()
    {
        var r = Cut("Head", Side.Center, (0f, -0.0778f, 0.0778f), 0.02);
        Assert.Equal(new[] { "Skin", "Fat", "Larynx" }, r.Layers);   // no carotid at the front
        // Occlusion full within 5 s: shipped 1.29 s (3.9 x inside), cough 2.0 reads 129.95 s (26 x outside).
        Assert.InRange(r.OcclusionFullAt, 0.0, 5.0);
        Assert.True(r.OutAt > 0.0, "he never loses consciousness");
        // Brain oxygen at consciousness 0.1: shipped 0.371, cough 2.0 0.994; bound sqrt(0.371 x 0.994) = 0.607 (separation 2.7).
        Assert.True(r.BrainOxygenOut < 0.607, $"brain oxygen {r.BrainOxygenOut}");
        // Blood loss then: shipped 0.4501, cough 2.0 and larynx V 1.0 0.6000; bound sqrt(0.4501 x 0.6000) = 0.5197 (separation 1.33, ruling deterministic-bound-margin).
        Assert.True(r.BloodLossOut < 0.5197, $"blood loss {r.BloodLossOut}");
        Assert.Equal(DeathCause.Consciousness, r.Cause);
    }
}
