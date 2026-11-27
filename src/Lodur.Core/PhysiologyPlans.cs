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

        // Airway tissues: mucosa as perfused as muscle (placeholders). AirwayShare is the fraction of a wound's bleed that enters the airway: all of the larynx's and trachea's, half a lung's (half coughed up, half stays in the chest).
        private const double LarynxV = 0.7, TracheaV = 1.0, MucosaShare = 1.0, LungShare = 0.5;

        // Major vessels: a sited tissue placed among the shells at its depth (ruling bds-bleed-by-vessels), so a knife at the side of the neck or the inner thigh reaches one and the front of the throat does not.
        // VesselV is play-tuned (probe: at the BDS knife's severity 20 and blood gain 10 either dies of blood loss at about 30 s; 3.0 gave 33.9 s, 4.0 gave 26.5 s). A vessel is no airway tissue (AirwayShare 0).
        // LarynxV 1.0 -> 0.7: at 1.0 the front of the throat bled him out before it drowned him (grid in the cut report); 0.7 with the cough at 0.3 drowns him first (ruling bds-drown-in-own-blood-first).
        private const double VesselV = 3.5;

        // Fat is thickness in the stack, not particles; its vascularity is a placeholder.
        private const double FatV = 0.1;

        // Shell thickness in m. Skin: mean dermis thickness of excised back skin (research tissue-tensile-strength.ts-skin-annaidh-2012). Fat and muscle sheet: (placeholder).
        private const double SkinT = 0.00256, FatT = 0.005, MuscleSheetT = 0.015;

        // Sheet material: spacing m, edge and attach compliance m/N, tensile strength Pa. Skin strength is the same study's ultimate stress (21.6 MPa);
        // spacing and compliances are (placeholder) values the fella.c and sieve.c probes ran with (flesh-map P1, P2, P6). Muscle strength is (placeholder): no human failure value.
        private static readonly SheetDef SkinSheet = new SheetDef(0.10, 2e-4, 1e-3, 21.6e6);
        private static readonly SheetDef MuscleSheetMaterial = new SheetDef(0.14, 1e-4, 1e-4, 0.5e6);

        private static TissueDef Skin(double hp) => new TissueDef("Skin", hp, SkinV) { Thickness = SkinT, Sheet = SkinSheet };
        private static TissueDef Fat(double hp) => new TissueDef("Fat", hp, FatV) { Thickness = FatT };
        private static TissueDef Muscle(double hp) => new TissueDef("Muscle", hp, MuscleV, Motor: 1.0);
        private static TissueDef MuscleSheet(double hp) => Muscle(hp) with { Thickness = MuscleSheetT, Sheet = MuscleSheetMaterial };
        private static TissueDef Bone(string label, double hp) => new TissueDef(label, hp, BoneV);

        private static Effect Curve(EffectTarget target, EffectOp op, EffectDriver driver, params (double X, double Y)[] curve) => new Effect(target, op, driver, curve);

        /// <summary>
        /// The 19-part humanoid of <see cref="BodyPlans.Humanoid"/>: tissue stacks outside-in with RimWorld's human part HP (fat and bone are stack thickness and core, not particles), the
        /// blood, the stages of blood loss, the hypoxia curve and the respiration, and the Cut and Bruise afflictions. Numbers not cited are play-tuned placeholders; the respiration constants (uptake, basal and exertion use, brain lag) are literature-shaped placeholders.
        /// </summary>
        public static PhysiologyPlan Humanoid()
        {
            var parts = new List<PartTissuesDef>();
            void Stack(string part, Side side, params TissueDef[] tissues) => parts.Add(new PartTissuesDef(part, side, tissues));

            Stack("Pelvis", Side.Center, Skin(10), Fat(10), MuscleSheet(30), Bone("Pelvis", 25), new TissueDef("Gut", 20, GutV));
            Stack("Torso", Side.Center,
                Skin(10), Fat(10), MuscleSheet(40), Bone("Ribcage", 25),
                // Organ sites (placeholders in the part's frame, left is +x, probed against PartTissuesDef.Reach); the ribcage stays unsited and fills the torso.
                new TissueDef("Heart", 15, HeartV, BloodPumping: 1.0) { Site = new TissueSite((0.02f, 0.05f, 0.03f), 0.05f) },
                new TissueDef("LungLeft", 15, LungV, Breathing: 1.0) { AirwayShare = LungShare, Site = new TissueSite((0.08f, 0.07f, 0f), 0.06f) },
                new TissueDef("LungRight", 15, LungV, Breathing: 1.0) { AirwayShare = LungShare, Site = new TissueSite((-0.08f, 0.07f, 0f), 0.06f) },
                new TissueDef("Liver", 20, LiverV) { Site = new TissueSite((-0.06f, -0.1f, 0.01f), 0.06f) },
                new TissueDef("KidneyLeft", 15, KidneyV) { Site = new TissueSite((0.06f, -0.15f, -0.05f), 0.03f) },
                new TissueDef("KidneyRight", 15, KidneyV) { Site = new TissueSite((-0.06f, -0.15f, -0.05f), 0.03f) },
                new TissueDef("Trachea", 10, TracheaV) { AirwayShare = MucosaShare, Site = new TissueSite((0f, 0.2f, 0.06f), 0.015f) });
            // The neck's muscle carries the Neck joint's motor weight: the head has to be a joint child with a muscle (spec discrepancy, report deviation).
            // Head sites are placeholders in the part's frame (+z face, -y chin), probed against PartTissuesDef.Reach: the neck muscle low at the chin, skull and brain about the crown, the larynx at the throat.
            // The carotids lie at the side of the neck, 0.055 m either side of the midline (placeholders), after the fat and before the muscle: a depth-2 cm wound at the side reaches one, the front of the throat does not.
            Stack("Head", Side.Center, Skin(5), Fat(5),
                new TissueDef("CarotidLeft", 10, VesselV) { Site = new TissueSite((0.055f, -0.07f, 0.01f), 0.012f) },
                new TissueDef("CarotidRight", 10, VesselV) { Site = new TissueSite((-0.055f, -0.07f, 0.01f), 0.012f) },
                Muscle(25) with { Site = new TissueSite((0f, -0.085f, -0.01f), 0.06f) }, Bone("Skull", 25) with { Site = new TissueSite((0f, 0.02f, -0.005f), 0.09f) },
                new TissueDef("Brain", 10, BrainV, Vital: true, Consciousness: 1.0) { Site = new TissueSite((0f, 0.02f, -0.005f), 0.07f) },
                new TissueDef("Larynx", 10, LarynxV) { AirwayShare = MucosaShare, Site = new TissueSite((0f, -0.075f, 0.06f), 0.015f) });
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                Stack("UpperArm", side, Skin(8), Fat(8), MuscleSheet(30), Bone("Humerus", 25));
                Stack("LowerArm", side, Skin(8), Fat(8), MuscleSheet(25), Bone("Radius", 20));
                // The femoral vessel runs at the inner thigh (sx +1 Left, -1 Right: the inner side is toward the midline), between the fat and the muscle sheet (placeholder site in the part's frame).
                var sx = side == Side.Left ? 1f : -1f;
                Stack("Thigh", side, Skin(10), Fat(10), new TissueDef("Femoral", 10, VesselV) { Site = new TissueSite((-sx * 0.032f, 0.12f, 0.032f), 0.012f) }, MuscleSheet(30), Bone("Femur", 25));
                Stack("Shin", side, Skin(8), Fat(8), MuscleSheet(25), Bone("Tibia", 25));
                Stack("Foot", side, Skin(5), Muscle(20), Bone("Bones", 25));
                Stack("Palm", side, Skin(5), Muscle(20), Bone("Metacarpals", 15));
                Stack("Thumb", side, Skin(3), Muscle(8), Bone("Bone", 8));
                Stack("Fingers", side, Skin(3), Muscle(8), Bone("Bone", 8));
            }

            // RimWorld's blood-loss stages (research rw-blood-bleeding) as step offsets on consciousness, by fraction of the lethal loss.
            var bloodLoss = Curve(EffectTarget.Consciousness, EffectOp.Offset, EffectDriver.BloodLoss,
                (0.0, 0.0), (0.15, 0.0), (0.15, -0.1), (0.30, -0.1), (0.30, -0.2), (0.45, -0.2), (0.45, -0.4), (0.60, -0.4), (0.60, -0.9), (1.0, -0.9));

            // Brain oxygen to consciousness: flat 1 above 0.70 so a healthy body reads exactly 1, falling to 0 at 0.10. A stopped heart or a smothering
            // reaches it through the stores. (placeholder, fitted to ~75 s unconscious while struggling and death at 134-360 s, PMC8928428 and Szpilman NEJM 2012;
            // 6-10 s after cerebral circulation stops, Rossen, Kabat and Anderson 1943; rulings bds-asphyxia-real-time, bds-heart-stop-stagger)
            var hypoxia = Curve(EffectTarget.Consciousness, EffectOp.Factor, EffectDriver.BrainOxygen, (0.10, 0.0), (0.35, 0.1), (0.70, 1.0));

            var afflictions = new[]
            {
                // Acute: a wound (Cut, Bruise) bleeds, clots and heals in real seconds whatever the slow scale (ruling bds-physiology-clock); Slow is for toxins, radiation and osmosis only. 1 mL/s per HP of severity
                // (play-tuned), clotting with age (play-tuned); heals 10 HP per day of real seconds (RimWorld's rate).
                new AfflictionDef("Cut", PhysiologyClock.Acute, -10.0 / Day, true, new[]
                {
                    Curve(EffectTarget.Bleed, EffectOp.Offset, EffectDriver.Severity, (0.0, 0.0), (100.0, 1e-4)),
                    Curve(EffectTarget.Bleed, EffectOp.Factor, EffectDriver.Age, (0.0, 1.0), (60.0, 0.5), (300.0, 0.1)),
                }),
                new AfflictionDef("Bruise", PhysiologyClock.Acute, -20.0 / Day, true, Array.Empty<Effect>()),
            };

            // ATLS class IV past 40% loss (research df-and-atls) puts the lethal fraction at 0.5 (play-tuned); regrowth is RimWorld's 0.333 per slow day.
            return new PhysiologyPlan(parts, afflictions, new[] { bloodLoss, hypoxia }, new BloodDef(0.005, 0.5, 0.333 / Day),
                new RespirationDef(
                    1.0, 0.002, 0.5, 4.0, 9.5,
                    // CO2 (placeholders, the readings are the real Actor smothered: struggle takes over at 0.1 s, out 81.6 s, dead 154.3 s; calm breath-hold gasps at 60.1 s). Rise 1/300 per second of
                    // basal load: carbon dioxide reaches its urge threshold in tens of seconds of exertion with no ventilation (breath-hold breaking point, PMC8511405; Frontiers Physiol 2021, 10.3389/fphys.2021.731633).
                    // Clear 0.5 per ventilated second keeps a healthy body at 0.01-0.07, below the curves' floor of 0.5.
                    1.0 / 300.0, 0.5,
                    // Urge by CO2 (placeholder): nothing up to 0.5, full at 1.3. Urge by ventilation deficit (placeholder): nothing up to a deficit of 0.5 (a lung lost), 0.6 at no breath at all;
                    // a breath tried and not got is felt at once (Banzett, Lansing and Schwartzstein, Air hunger, Respir Physiol Neurobiol 2008).
                    new[] { (0.5, 0.0), (1.3, 1.0) }, new[] { (0.5, 0.0), (1.0, 0.6) }),
                // The airway (placeholders). Capacity 150 mL is 2 mL/kg at 75 kg (an upper-airway and bronchial volume, the lung's own is far larger: the airway fills before the lung); it holds the breath from 15.6 mL
                // of liquid at the openings (a mouthful, a tenth of the capacity). A free breath draws the liquid at the airway in at 1 per second, an awake cough clears 0.3 per second of what is there (a continuous airway bleed at the knife's severity must beat the cough, or the front of the throat bleeds him out instead of drowning him) and fails below 0.3
                // consciousness, and the airway drains at 0.1 per second once the lowest rim of an opening lies 5 cm below the deepest sump (a true side pose, drop 0.025 m the mouth's half-width, halves it; side-lying falls measure a median 0.043 m, upright -0.047 m, so he drains nothing standing). Liquid and pooled liquid count alike.
                new AirwayDef(150e-6, 1.0, 1.0, 1.0, 15.6e-6, 0.3, new[] { (0.3, 0.0), (0.6, 1.0) }, 0.1, 0.05), 64);
        }
    }
}
