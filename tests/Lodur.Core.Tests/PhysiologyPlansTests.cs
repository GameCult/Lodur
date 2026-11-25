using Lodur;

namespace Lodur.Core.Tests;

/// <summary>The humanoid's physiology content resolves against the humanoid body and behaves as the content says.</summary>
public sealed class PhysiologyPlansTests
{
    private static readonly BodyPlan Body = BodyPlans.Humanoid();

    private static Physiology Spawn() => new(Body, PhysiologyPlans.Humanoid(), 20);

    private static int Cut => PhysiologyPlans.Humanoid().AfflictionIndex("Cut");
    private static int Bruise => PhysiologyPlans.Humanoid().AfflictionIndex("Bruise");

    [Fact]
    public void The_humanoid_resolves_and_spawns_whole()
    {
        var p = Spawn();
        Assert.Equal(Body.Joints.Count, p.JointCount);
        Assert.Equal(1.0, p.Consciousness);
        Assert.Equal(1.0, p.BloodPumping);
        Assert.Equal(0.005, p.BloodVolume);
        for (var j = 0; j < p.JointCount; j++) Assert.Equal(1.0, p.MotorCeiling(j));
        Assert.False(p.Dead);
    }

    [Fact]
    public void The_brain_on_the_head_and_the_heart_on_the_torso_are_vital_and_a_skull_is_not()
    {
        var head = Body.PartIndex("Head");
        var torso = Body.PartIndex("Torso");

        var brain = Spawn();
        Assert.True(brain.Afflict(Cut, head, brain.TissueIndex(head, "Brain"), 11));
        brain.Step(0.01);
        Assert.Equal(DeathCause.VitalTissue, brain.Cause);

        var heart = Spawn();
        Assert.True(heart.Afflict(Cut, torso, heart.TissueIndex(torso, "Heart"), 16));
        heart.Step(0.01);
        Assert.Equal(DeathCause.VitalTissue, heart.Cause);

        var skull = Spawn();
        Assert.True(skull.Afflict(Bruise, head, skull.TissueIndex(head, "Skull"), 26));
        skull.Step(0.01);
        Assert.False(skull.Dead);
        Assert.Equal(0.0, skull.Integrity(head, skull.TissueIndex(head, "Skull")));

        var lung = Spawn();
        Assert.True(lung.Afflict(Bruise, torso, lung.TissueIndex(torso, "LungLeft"), 16));
        lung.Step(0.01);
        Assert.False(lung.Dead);   // an organ with no vital flag yet; the airway cut gives lungs their function
    }

    [Fact]
    public void Every_side_of_every_limb_has_its_own_stack()
    {
        var p = Spawn();
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var shin = Body.PartIndex("Shin", side);
            Assert.True(p.Afflict(Bruise, shin, p.TissueIndex(shin, "Tibia"), 26));
        }

        p.Step(0.01);
        Assert.Equal(0.0, p.Integrity(Body.PartIndex("Shin", Side.Left), p.TissueIndex(Body.PartIndex("Shin", Side.Left), "Tibia")));
        Assert.Equal(0.0, p.Integrity(Body.PartIndex("Shin", Side.Right), p.TissueIndex(Body.PartIndex("Shin", Side.Right), "Tibia")));
        Assert.Equal(1.0, p.Integrity(Body.PartIndex("Thigh", Side.Left), 0));
    }

    [Fact]
    public void Blood_loss_stages_drop_consciousness_by_the_ruled_steps()
    {
        var p = Spawn();
        var torso = Body.PartIndex("Torso");
        var muscle = p.TissueIndex(torso, "Muscle");
        for (var i = 0; i < 3; i++) Assert.True(p.Afflict(Cut, torso, muscle, 100));   // 3 x 100 HP of cut: a wound far past the muscle's 40

        void ReachAndRead(double loss, double expected)
        {
            for (var i = 0; i < 100000 && p.BloodLoss < loss; i++) p.Step(0.01);
            Assert.False(p.Dead);
            Assert.True(p.BloodLoss >= loss && p.BloodLoss < loss + 0.05, $"loss {p.BloodLoss}");
            p.Step(0.01);                      // the next step reads the blood the last one left
            Assert.Equal(expected, p.Consciousness, 12);
        }

        // 0.5 x 0.16 of the nominal volume lost reads the 0.15 stage; 0.31 reads the 0.30 stage.
        ReachAndRead(0.16, 0.9);
        ReachAndRead(0.31, 0.8);
    }

    [Fact]
    public void A_cut_bleeds_and_clots_and_a_bruise_does_not_bleed()
    {
        var p = Spawn();
        var torso = Body.PartIndex("Torso");
        var muscle = p.TissueIndex(torso, "Muscle");
        Assert.True(p.Afflict(Bruise, torso, muscle, 40));
        p.Step(0.01);
        Assert.Equal(0.0, p.Bleed(torso));

        Assert.True(p.Afflict(Cut, torso, muscle, 20));
        p.Step(0.01);
        var fresh = p.Bleed(torso);
        Assert.InRange(fresh, 1.9e-5, 2e-5);   // 1 mL/s per HP of severity (2e-5 m^3/s at 20), x vascularity 1 x pumping 1, a hair clotted
        for (var i = 0; i < 600; i++) p.Step(0.1);               // 60 real seconds: the clot factor has fallen to a half
        Assert.False(p.Dead);
        Assert.True(p.Bleed(torso) > 0.0 && p.Bleed(torso) < fresh * 0.55);
    }

    [Fact]
    public void A_cut_bleeds_and_clots_in_real_seconds_whatever_the_slow_scale()
    {
        (double Bleed3, double Lost60) Run(double scale)
        {
            var p = new Physiology(Body, PhysiologyPlans.Humanoid(), scale);
            var torso = Body.PartIndex("Torso");
            Assert.True(p.Afflict(Cut, torso, p.TissueIndex(torso, "Muscle"), 10));
            var at3 = 0.0;
            for (var i = 1; i <= 1200; i++)
            {
                p.Step(0.05);
                if (i == 60) at3 = p.Bleed(torso);
            }

            Assert.False(p.Dead);
            return (at3, 0.005 - p.BloodVolume);
        }

        var one = Run(1);
        var twenty = Run(20);
        Assert.InRange(one.Bleed3, 9.5e-6, 9.9e-6);          // about 9.7 mL/s at 3 s: 10 HP x 1 mL/s, a hair clotted
        Assert.InRange(one.Lost60, 4.4e-4, 4.6e-4);          // about 450 mL in 60 s
        Assert.Equal(one.Bleed3, twenty.Bleed3);
        Assert.Equal(one.Lost60, twenty.Lost60);
    }
}
