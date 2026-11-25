using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>The clock a process runs on: Acute on the caller's seconds, Slow on those times the session's slow scale (ruling bds-physiology-clock).</summary>
    public enum PhysiologyClock { Acute, Slow }

    /// <summary>What an effect shapes. One mechanism reads each: capacities (Consciousness, BloodPumping), an affliction's bleed rate, a part's motor ceiling.</summary>
    public enum EffectTarget { Consciousness, BloodPumping, Bleed, Motor }

    /// <summary>How an effect's value joins its target: summed into the offsets, or multiplied into the factors.</summary>
    public enum EffectOp { Offset, Factor }

    /// <summary>What an effect's curve is read at: its affliction's severity or age (in its clock's seconds), or the organism's blood loss.</summary>
    public enum EffectDriver { Severity, Age, BloodLoss }

    /// <summary>
    /// The material of a layer simulated as a particle sheet. <paramref name="Spacing"/> is the particle spacing in m. The compliances are
    /// XPBD compliances in m/N: along the sheet's edges (also its joint stitches) and to the bone. <paramref name="TensileStrength"/> is
    /// ultimate stress in Pa; the sheet builder derives an edge's break force as TensileStrength x Thickness x Spacing.
    /// </summary>
    public readonly record struct SheetDef(double Spacing, double EdgeCompliance, double AttachCompliance, double TensileStrength);

    /// <summary>
    /// One tissue layer of a part. <paramref name="Consciousness"/>, <paramref name="BloodPumping"/> and <paramref name="Motor"/> are
    /// function weights, a closed set with one reader each: a capacity is the weighted mean integrity of the tissues that carry its weight.
    /// A Vital tissue at integrity 0 kills. A stack is shells (<see cref="Thickness"/> above 0, in m) outside-in, then cores (Thickness 0:
    /// bone, organs, a muscle with no sheet); the cores fill what the shells leave of the part's shape. <see cref="Sheet"/> is the
    /// material of a shell simulated as a particle sheet; null means it is not.
    /// </summary>
    public readonly record struct TissueDef(
        string Label, double Hp, double Vascularity, bool Vital = false, double Consciousness = 0.0, double BloodPumping = 0.0, double Motor = 0.0)
    {
        public double Thickness { get; init; }
        public SheetDef? Sheet { get; init; }
    }

    /// <summary>The one tissue stack of a part, outside-in, owned by <see cref="PhysiologyPlan.Parts"/> and joined to a <see cref="BodyPlan"/> only through <see cref="PhysiologyPlan.Resolve"/>.</summary>
    public readonly record struct PartTissuesDef(string PartLabel, Side Side, IReadOnlyList<TissueDef> Tissues);

    /// <summary>The blood: nominal volume in m^3, the lost fraction that kills, and the regrowth per slow second as a fraction of nominal.</summary>
    public readonly record struct BloodDef(double NominalVolume, double LethalLossFraction, double RegenPerSecond);

    /// <summary>
    /// A piecewise-linear curve in its driver, clamped at the ends. An equal-x pair is a step: at that x the later value holds
    /// (a stage begins at its threshold). Effects only shape values a mechanism reads; they never write state.
    /// </summary>
    public readonly record struct Effect(EffectTarget Target, EffectOp Op, EffectDriver Driver, IReadOnlyList<(double X, double Y)> Curve)
    {
        public double Eval(double x)
        {
            var c = Curve;
            if (x < c[0].X)
            {
                return c[0].Y;
            }

            for (var i = 1; i < c.Count; i++)
            {
                if (x < c[i].X)
                {
                    var a = c[i - 1];
                    var b = c[i];
                    return a.Y + (b.Y - a.Y) * ((x - a.X) / (b.X - a.X));
                }
            }

            return c[c.Count - 1].Y;
        }
    }

    /// <summary>
    /// A kind of harm or condition. <paramref name="SeverityRate"/> is per second of its clock (negative heals); an Injures
    /// affliction's severity is HP lost from its tissue; a live affliction's effects apply while it lives.
    /// </summary>
    public readonly record struct AfflictionDef(string Label, PhysiologyClock Clock, double SeverityRate, bool Injures, IReadOnlyList<Effect> Effects);

    /// <summary>
    /// Physiology as content: tissue stacks per body part, the afflictions that can be bound, the organism-wide effects of blood
    /// loss, the blood and the size of the affliction table. Validated at construction; resolved against a <see cref="BodyPlan"/> by <see cref="Resolve"/>, the one (label, side) join, which <see cref="Physiology"/> and the sheet builder both call.
    /// </summary>
    public sealed class PhysiologyPlan
    {
        private readonly Dictionary<string, int> _afflictionIndex = new Dictionary<string, int>();

        public PhysiologyPlan(
            IReadOnlyList<PartTissuesDef> parts, IReadOnlyList<AfflictionDef> afflictions, IReadOnlyList<Effect> organismEffects,
            BloodDef blood, int afflictionSlots)
        {
            var ownedParts = new List<PartTissuesDef>(parts.Count);
            foreach (var p in parts)
            {
                ownedParts.Add(p with { Tissues = new List<TissueDef>(p.Tissues).AsReadOnly() });
            }

            var ownedAfflictions = new List<AfflictionDef>(afflictions.Count);
            foreach (var a in afflictions)
            {
                ownedAfflictions.Add(a with { Effects = Own(a.Effects) });
            }

            Parts = ownedParts.AsReadOnly();
            Afflictions = ownedAfflictions.AsReadOnly();
            OrganismEffects = Own(organismEffects);
            Blood = blood;
            AfflictionSlots = afflictionSlots;
            Validate();
        }

        public IReadOnlyList<PartTissuesDef> Parts { get; }
        public IReadOnlyList<AfflictionDef> Afflictions { get; }
        public IReadOnlyList<Effect> OrganismEffects { get; }
        public BloodDef Blood { get; }
        public int AfflictionSlots { get; }

        public int AfflictionIndex(string label) =>
            _afflictionIndex.TryGetValue(label, out var i) ? i : throw new ArgumentException($"no affliction labelled '{label}'.");

        /// <summary>
        /// The one (label, side) join of stacks to a body: element p is the index in <see cref="Parts"/> of body part p's stack. Refuses a
        /// stack naming no part, a part with two stacks, a part with no stack, and shells that do not fit (a stack's summed Thickness must be
        /// below the part's inscribed half extent, compared at the shape's float precision). Physiology and the sheet builder call it; it allocates, at construction only.
        /// </summary>
        public int[] Resolve(BodyPlan body)
        {
            var parts = body.Parts.Count;
            var stackOf = new int[parts];
            for (var p = 0; p < parts; p++) stackOf[p] = -1;
            for (var s = 0; s < Parts.Count; s++)
            {
                var def = Parts[s];
                var part = -1;
                for (var p = 0; p < parts; p++)
                {
                    if (body.Parts[p].Label == def.PartLabel && body.Parts[p].Side == def.Side) part = p;
                }

                if (part < 0) throw new ArgumentException($"a tissue stack names no body part: '{def.PartLabel}' {def.Side}.");
                if (stackOf[part] >= 0) throw new ArgumentException($"body part '{def.PartLabel}' {def.Side} has two tissue stacks.");
                stackOf[part] = s;
            }

            for (var p = 0; p < parts; p++)
            {
                var d = body.Parts[p];
                if (stackOf[p] < 0) throw new ArgumentException($"body part '{d.Label}' {d.Side} has no tissue stack.");
                var shells = 0.0;
                foreach (var t in Parts[stackOf[p]].Tissues) shells += t.Thickness;
                var half = d.Shape == PartShape.Box ? Math.Min(d.Size.X, Math.Min(d.Size.Y, d.Size.Z)) : d.Size.X;
                if (!((float)shells < half)) throw new ArgumentException($"body part '{d.Label}' {d.Side}: the shells ({shells} m) do not fit inside its inscribed half extent ({half} m).");
            }

            return stackOf;
        }

        private static IReadOnlyList<Effect> Own(IReadOnlyList<Effect> effects)
        {
            var list = new List<Effect>(effects.Count);
            foreach (var e in effects)
            {
                list.Add(e with { Curve = new List<(double X, double Y)>(e.Curve).AsReadOnly() });
            }

            return list.AsReadOnly();
        }

        private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

        private void Validate()
        {
            foreach (var p in Parts)
            {
                var seen = new HashSet<string>();
                var core = false;
                foreach (var t in p.Tissues)
                {
                    var where = $"tissue '{t.Label}' of part '{p.PartLabel}' {p.Side}";
                    if (string.IsNullOrEmpty(t.Label)) throw new ArgumentException($"a tissue of part '{p.PartLabel}' {p.Side} has an empty label.");
                    if (!seen.Add(t.Label)) throw new ArgumentException($"{where}: the label is not unique within the part.");
                    if (!(IsFinite(t.Hp) && t.Hp > 0.0)) throw new ArgumentException($"{where}: Hp is not finite and above 0.");
                    if (!(IsFinite(t.Vascularity) && t.Vascularity >= 0.0)) throw new ArgumentException($"{where}: vascularity is not finite and at least 0.");
                    if (!(IsFinite(t.Consciousness) && t.Consciousness >= 0.0 && IsFinite(t.BloodPumping) && t.BloodPumping >= 0.0 && IsFinite(t.Motor) && t.Motor >= 0.0))
                    {
                        throw new ArgumentException($"{where}: a function weight is not finite and at least 0.");
                    }

                    if (!(IsFinite(t.Thickness) && t.Thickness >= 0.0)) throw new ArgumentException($"{where}: thickness is not finite and at least 0.");
                    if (t.Thickness == 0.0) core = true;
                    else if (core) throw new ArgumentException($"{where}: a shell follows a core.");
                    if (t.Sheet is SheetDef sh)
                    {
                        if (!(t.Thickness > 0.0)) throw new ArgumentException($"{where}: a sheet needs a thickness above 0.");
                        if (!(IsFinite(sh.Spacing) && sh.Spacing > 0.0)) throw new ArgumentException($"{where}: sheet spacing is not finite and above 0.");
                        if (!(IsFinite(sh.EdgeCompliance) && sh.EdgeCompliance >= 0.0 && IsFinite(sh.AttachCompliance) && sh.AttachCompliance >= 0.0))
                        {
                            throw new ArgumentException($"{where}: a sheet compliance is not finite and at least 0.");
                        }

                        if (!(IsFinite(sh.TensileStrength) && sh.TensileStrength > 0.0)) throw new ArgumentException($"{where}: sheet tensile strength is not finite and above 0.");
                    }
                }
            }

            var labels = new HashSet<string>();
            for (var i = 0; i < Afflictions.Count; i++)
            {
                var a = Afflictions[i];
                if (string.IsNullOrEmpty(a.Label)) throw new ArgumentException("an affliction has an empty label.");
                if (!labels.Add(a.Label)) throw new ArgumentException($"affliction '{a.Label}': the label is not unique.");
                _afflictionIndex.Add(a.Label, i);
                if (!Enum.IsDefined(typeof(PhysiologyClock), a.Clock)) throw new ArgumentException($"affliction '{a.Label}': the clock is not defined.");
                if (!IsFinite(a.SeverityRate)) throw new ArgumentException($"affliction '{a.Label}': the severity rate is not finite.");
                foreach (var e in a.Effects)
                {
                    ValidateEffect(e, $"affliction '{a.Label}'");
                }
            }

            foreach (var e in OrganismEffects)
            {
                ValidateEffect(e, "an organism effect");
                if (e.Target != EffectTarget.Consciousness && e.Target != EffectTarget.BloodPumping)
                {
                    throw new ArgumentException("an organism effect targets only Consciousness or BloodPumping.");
                }

                if (e.Driver != EffectDriver.BloodLoss)
                {
                    throw new ArgumentException("an organism effect is driven only by BloodLoss.");
                }
            }

            if (!(IsFinite(Blood.NominalVolume) && Blood.NominalVolume > 0.0)) throw new ArgumentException("blood: the nominal volume is not finite and above 0.");
            if (!(Blood.LethalLossFraction > 0.0 && Blood.LethalLossFraction <= 1.0)) throw new ArgumentException("blood: the lethal loss fraction is not in (0, 1].");
            if (!(IsFinite(Blood.RegenPerSecond) && Blood.RegenPerSecond >= 0.0)) throw new ArgumentException("blood: the regrowth is not finite and at least 0.");
            if (AfflictionSlots < 1) throw new ArgumentException("the affliction table needs at least one slot.");
        }

        private static void ValidateEffect(Effect e, string where)
        {
            if (!Enum.IsDefined(typeof(EffectTarget), e.Target) || !Enum.IsDefined(typeof(EffectOp), e.Op) || !Enum.IsDefined(typeof(EffectDriver), e.Driver))
            {
                throw new ArgumentException($"{where}: an effect has an undefined target, op or driver.");
            }

            if (e.Curve.Count == 0) throw new ArgumentException($"{where}: an effect has an empty curve.");
            for (var i = 0; i < e.Curve.Count; i++)
            {
                var (x, y) = e.Curve[i];
                if (!IsFinite(x) || !IsFinite(y)) throw new ArgumentException($"{where}: an effect curve has a non-finite point.");
                if (i > 0 && x < e.Curve[i - 1].X) throw new ArgumentException($"{where}: an effect curve's x decreases.");
            }
        }
    }
}
