using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Physiology content in code, until it lives in a store; the only place tissue, organ and affliction labels are spelled.</summary>
    public static class PhysiologyPlans
    {
        private const double Day = 86400.0;

        // Vascularity is the blood a wound in the tissue loses relative to muscle (play-tuned placeholders): skin and bone are thin, organs rich.
        private const double SkinV = 0.2, MuscleV = 1.0, BoneV = 0.3, BrainV = 1.0, HeartV = 4.0, LungV = 2.0, LiverV = 3.0, KidneyV = 2.0, GutV = 1.0;

        private static TissueDef Skin(double hp) => new TissueDef("Skin", hp, SkinV);
        private static TissueDef Muscle(double hp) => new TissueDef("Muscle", hp, MuscleV, Motor: 1.0);
        private static TissueDef Bone(string label, double hp) => new TissueDef(label, hp, BoneV);

        private static Effect Curve(EffectTarget target, EffectOp op, EffectDriver driver, params (double X, double Y)[] curve) => new Effect(target, op, driver, curve);

        /// <summary>
        /// The 19-part humanoid of <see cref="BodyPlans.Humanoid"/>: tissue stacks outside-in with RimWorld's human part HP, the
        /// blood, the stages of blood loss, and the Cut and Bruise afflictions. Numbers not cited are play-tuned placeholders.
        /// </summary>
        public static PhysiologyPlan Humanoid()
        {
            var parts = new List<PartTissuesDef>();
            void Stack(string part, Side side, params TissueDef[] tissues) => parts.Add(new PartTissuesDef(part, side, tissues));

            Stack("Pelvis", Side.Center, Skin(10), Muscle(30), Bone("Pelvis", 25), new TissueDef("Gut", 20, GutV));
            Stack("Torso", Side.Center,
                Skin(10), Muscle(40), Bone("Ribcage", 25),
                new TissueDef("Heart", 15, HeartV, Vital: true, BloodPumping: 1.0),
                new TissueDef("LungLeft", 15, LungV), new TissueDef("LungRight", 15, LungV),
                new TissueDef("Liver", 20, LiverV), new TissueDef("KidneyLeft", 15, KidneyV), new TissueDef("KidneyRight", 15, KidneyV));
            // The neck's muscle carries the Neck joint's motor weight: the head has to be a joint child with a muscle (spec discrepancy, report deviation).
            Stack("Head", Side.Center, Skin(5), Muscle(25), Bone("Skull", 25), new TissueDef("Brain", 10, BrainV, Vital: true, Consciousness: 1.0));
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                Stack("UpperArm", side, Skin(8), Muscle(30), Bone("Humerus", 25));
                Stack("LowerArm", side, Skin(8), Muscle(25), Bone("Radius", 20));
                Stack("Thigh", side, Skin(10), Muscle(30), Bone("Femur", 25));
                Stack("Shin", side, Skin(8), Muscle(25), Bone("Tibia", 25));
                Stack("Foot", side, Skin(5), Muscle(20), Bone("Bones", 25));
                Stack("Palm", side, Skin(5), Muscle(20), Bone("Metacarpals", 15));
                Stack("Thumb", side, Skin(3), Muscle(8), Bone("Bone", 8));
                Stack("Fingers", side, Skin(3), Muscle(8), Bone("Bone", 8));
            }

            // RimWorld's blood-loss stages (research rw-blood-bleeding) as step offsets on consciousness, by fraction of the lethal loss.
            var bloodLoss = Curve(EffectTarget.Consciousness, EffectOp.Offset, EffectDriver.BloodLoss,
                (0.0, 0.0), (0.15, 0.0), (0.15, -0.1), (0.30, -0.1), (0.30, -0.2), (0.45, -0.2), (0.45, -0.4), (0.60, -0.4), (0.60, -0.9), (1.0, -0.9));

            var afflictions = new[]
            {
                // Acute: a wound bleeds, clots and heals in real seconds whatever the slow scale (ruling bds-physiology-clock). 1 mL/s per HP of severity
                // (play-tuned), clotting with age (play-tuned); heals 10 HP per day of real seconds (RimWorld's rate).
                new AfflictionDef("Cut", PhysiologyClock.Acute, -10.0 / Day, true, new[]
                {
                    Curve(EffectTarget.Bleed, EffectOp.Offset, EffectDriver.Severity, (0.0, 0.0), (100.0, 1e-4)),
                    Curve(EffectTarget.Bleed, EffectOp.Factor, EffectDriver.Age, (0.0, 1.0), (60.0, 0.5), (300.0, 0.1)),
                }),
                new AfflictionDef("Bruise", PhysiologyClock.Slow, -20.0 / Day, true, Array.Empty<Effect>()),
            };

            // ATLS class IV past 40% loss (research df-and-atls) puts the lethal fraction at 0.5 (play-tuned); regrowth is RimWorld's 0.333 per slow day.
            return new PhysiologyPlan(parts, afflictions, new[] { bloodLoss }, new BloodDef(0.005, 0.5, 0.333 / Day), 64);
        }
    }
}
