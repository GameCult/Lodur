using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Why a body died; the first rule that held at the end of the step that killed it (ruling bds-death).</summary>
    public enum DeathCause { None, VitalTissue, BloodLoss, Consciousness }

    /// <summary>
    /// The body's physiology: tissue integrity, bound afflictions, blood, the capacities they fold into, the motor ceiling and death.
    /// Harm enters only through <see cref="Afflict"/>; time through <see cref="Step"/>. Integrity is derived from the severities of the
    /// Injures afflictions bound to a tissue (part HP is never stored), and capacities, bleed and the motor ceiling are recomputed each step;
    /// nobody writes them. Content owns every label and number (<see cref="PhysiologyPlan"/>). Everything is allocated in the constructor.
    /// </summary>
    public sealed class Physiology
    {
        private readonly BodyPlan _body;
        private readonly double _slowScale;
        private readonly double _nominal;
        private readonly double _lethalLoss;
        private readonly double _regen;
        private readonly AfflictionDef[] _defs;
        private readonly Effect[] _organism;
        private readonly int[] _partStart;              // first tissue of each part in the flattened tissues; one past the last at [parts]
        private readonly TissueDef[] _tissues;
        private readonly double[] _injury;
        private readonly double[] _integrity;
        private readonly double _consciousnessWeight;
        private readonly double _pumpingWeight;
        private readonly double[] _motorWeight;         // per part
        private readonly int[] _slotDef;                // -1: free
        private readonly int[] _slotPart;
        private readonly int[] _slotTissue;             // flattened
        private readonly double[] _slotSeverity;
        private readonly double[] _slotAge;
        private readonly double[] _bleed;               // per part, m^3/s
        private readonly double[] _motor;               // per joint
        private double _volume;
        private double _consciousness;
        private double _pumping = 1.0;
        private long _steps;
        private bool _bound;

        public Physiology(BodyPlan body, PhysiologyPlan plan, double slowScale)
        {
            if (!(slowScale > 0.0 && !double.IsInfinity(slowScale))) throw new ArgumentException("the slow scale must be finite and above 0.");
            _body = body;
            _slowScale = slowScale;
            _nominal = plan.Blood.NominalVolume;
            _lethalLoss = plan.Blood.LethalLossFraction;
            _regen = plan.Blood.RegenPerSecond;
            _volume = _nominal;

            _defs = new AfflictionDef[plan.Afflictions.Count];
            for (var i = 0; i < _defs.Length; i++) _defs[i] = plan.Afflictions[i];
            _organism = new Effect[plan.OrganismEffects.Count];
            for (var i = 0; i < _organism.Length; i++) _organism[i] = plan.OrganismEffects[i];

            var parts = body.Parts.Count;
            var stackOf = plan.Resolve(body);

            _partStart = new int[parts + 1];
            var total = 0;
            for (var p = 0; p < parts; p++)
            {
                _partStart[p] = total;
                total += plan.Parts[stackOf[p]].Tissues.Count;
            }

            _partStart[parts] = total;
            _tissues = new TissueDef[total];
            _injury = new double[total];
            _integrity = new double[total];
            _motorWeight = new double[parts];
            for (var p = 0; p < parts; p++)
            {
                var stack = plan.Parts[stackOf[p]].Tissues;
                for (var l = 0; l < stack.Count; l++)
                {
                    var t = stack[l];
                    _tissues[_partStart[p] + l] = t;
                    _integrity[_partStart[p] + l] = 1.0;
                    _consciousnessWeight += t.Consciousness;
                    _pumpingWeight += t.BloodPumping;
                    _motorWeight[p] += t.Motor;
                }
            }

            if (!(_consciousnessWeight > 0.0)) throw new ArgumentException("no tissue carries consciousness.");
            _motor = new double[body.Joints.Count];
            for (var j = 0; j < _motor.Length; j++)
            {
                if (!(_motorWeight[body.Joints[j].Child] > 0.0))
                {
                    throw new ArgumentException($"the part below joint '{body.Joints[j].Label}' {body.Joints[j].Side} has no motor tissue.");
                }

                _motor[j] = 1.0;
            }

            _consciousness = 1.0;
            _bleed = new double[parts];
            var slots = plan.AfflictionSlots;
            _slotDef = new int[slots];
            _slotPart = new int[slots];
            _slotTissue = new int[slots];
            _slotSeverity = new double[slots];
            _slotAge = new double[slots];
            for (var s = 0; s < slots; s++) _slotDef[s] = -1;
        }

        public int JointCount => _motor.Length;

        /// <summary>Blood volume in m^3; starts at the nominal volume.</summary>
        public double BloodVolume => _volume;

        /// <summary>The fraction of the lethal loss lost so far: 0 at nominal volume, 1 where the blood kills. Derived from the volume.</summary>
        public double BloodLoss => (1.0 - _volume / _nominal) / _lethalLoss;

        public bool Dead { get; private set; }
        public DeathCause Cause { get; private set; }

        /// <summary>The ordinal (from 0) of the <see cref="Step"/> that killed, -1 while alive.</summary>
        public long DiedAtStep { get; private set; } = -1;

        /// <summary>Afflictions refused because the table was full.</summary>
        public int Refused { get; private set; }

        /// <summary>Wakefulness, 0..1; 0 once dead.</summary>
        public double Consciousness => Dead ? 0.0 : _consciousness;

        /// <summary>The heart's capacity to drive what bleeds, 0..1; 0 once dead.</summary>
        public double BloodPumping => Dead ? 0.0 : _pumping;

        /// <summary>The tissue's remaining fraction, 0..1, as of the last step.</summary>
        public double Integrity(int part, int layer) => _integrity[Flat(part, layer)];

        /// <summary>The part's blood loss in m^3/s as of the last step; 0 once dead.</summary>
        public double Bleed(int part) => Dead ? 0.0 : _bleed[part];

        /// <summary>The ceiling on a joint's muscle strength, 0..1: its capacity times consciousness; 0 once dead.</summary>
        public double MotorCeiling(int joint) => Dead ? 0.0 : _motor[joint];

        /// <summary>The layer of the part's stack with this label (the layer index <see cref="Afflict"/> and <see cref="Integrity"/> take).</summary>
        public int TissueIndex(int part, string label)
        {
            for (var i = _partStart[part]; i < _partStart[part + 1]; i++)
            {
                if (_tissues[i].Label == label) return i - _partStart[part];
            }

            throw new ArgumentException($"part {part} has no tissue labelled '{label}'.");
        }

        internal int SlotCount => _slotDef.Length;
        internal int SlotDef(int slot) => _slotDef[slot];
        internal double SlotSeverity(int slot) => _slotSeverity[slot];
        internal double SlotAge(int slot) => _slotAge[slot];

        /// <summary>Set by the Actor that steps this physiology; one Actor steps one physiology until lodur-grafting moves stepping to the organism.</summary>
        internal bool TryBind()
        {
            if (_bound) return false;
            _bound = true;
            return true;
        }

        private int Flat(int part, int layer)
        {
            if ((uint)part >= (uint)_bleed.Length) throw new ArgumentException("not a part.");
            var i = _partStart[part] + layer;
            if (layer < 0 || i >= _partStart[part + 1]) throw new ArgumentException("not a layer of the part.");
            return i;
        }

        /// <summary>
        /// Binds a new instance of the affliction to a tissue, never merged with another. Returns false, counting a refusal and
        /// overwriting nothing, when the table is full. Allowed while dead: it records the wound and nothing steps.
        /// </summary>
        public bool Afflict(int affliction, int part, int layer, double severity)
        {
            if ((uint)affliction >= (uint)_defs.Length) throw new ArgumentException("not an affliction.");
            var tissue = Flat(part, layer);
            if (!(severity > 0.0 && !double.IsInfinity(severity))) throw new ArgumentException("severity must be finite and above 0.");
            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] >= 0) continue;
                _slotDef[s] = affliction;
                _slotPart[s] = part;
                _slotTissue[s] = tissue;
                _slotSeverity[s] = severity;
                _slotAge[s] = 0.0;
                return true;
            }

            Refused++;
            return false;
        }

        /// <summary>
        /// Advances by dt seconds of the caller's clock: afflictions age and change severity (healed ones free their slot), integrity,
        /// capacities, bleeding and regrowth follow, then death is judged once. A dead physiology does not change.
        /// </summary>
        public void Step(double dt)
        {
            if (Dead) return;
            var slowDt = dt * _slowScale;

            // 1. Each live affliction on its own clock.
            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] < 0) continue;
                var def = _defs[_slotDef[s]];
                var c = def.Clock == PhysiologyClock.Acute ? dt : slowDt;
                _slotAge[s] += c;
                _slotSeverity[s] += def.SeverityRate * c;
                if (_slotSeverity[s] <= 0.0) _slotDef[s] = -1;
            }

            // 2. Integrity from the severities of the Injures afflictions.
            Array.Clear(_injury, 0, _injury.Length);
            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] >= 0 && _defs[_slotDef[s]].Injures) _injury[_slotTissue[s]] += _slotSeverity[s];
            }

            for (var i = 0; i < _integrity.Length; i++) _integrity[i] = Clamp01(1.0 - _injury[i] / _tissues[i].Hp);

            // 3. Capacities: the weighted mean integrity, then every effect's offsets, then its factors. The blood loss they read is the volume the step began with.
            var bloodLoss = BloodLoss;
            _consciousness = Capacity(EffectTarget.Consciousness, bloodLoss);
            _pumping = Capacity(EffectTarget.BloodPumping, bloodLoss);

            // 4. Bleeding: each affliction's bleed effects, through its tissue's vascularity and the pumping.
            Array.Clear(_bleed, 0, _bleed.Length);
            var totalBleed = 0.0;
            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] < 0) continue;
                double offset = 0.0, factor = 1.0;
                Fold(_defs[_slotDef[s]].Effects, EffectTarget.Bleed, _slotSeverity[s], _slotAge[s], bloodLoss, ref offset, ref factor);
                var rate = Math.Max(0.0, offset * factor) * _tissues[_slotTissue[s]].Vascularity * _pumping;
                _bleed[_slotPart[s]] += rate;
                totalBleed += rate;
            }

            _volume -= totalBleed * dt;

            // 5. Regrowth, on the slow clock, only while nothing bleeds.
            if (totalBleed == 0.0)
            {
                _volume = Math.Min(_nominal, _volume + _regen * _nominal * slowDt);
            }

            // 6. Motor ceilings: the child part's motor-weighted integrity, folded with its live afflictions' motor effects, times consciousness.
            for (var j = 0; j < _motor.Length; j++)
            {
                var part = _body.Joints[j].Child;
                var mean = 0.0;
                for (var i = _partStart[part]; i < _partStart[part + 1]; i++)
                {
                    var w = _tissues[i].Motor;
                    if (w > 0.0) mean += w * _integrity[i];
                }

                double offset = 0.0, factor = 1.0;
                for (var s = 0; s < _slotDef.Length; s++)
                {
                    if (_slotDef[s] >= 0 && _slotPart[s] == part)
                    {
                        Fold(_defs[_slotDef[s]].Effects, EffectTarget.Motor, _slotSeverity[s], _slotAge[s], bloodLoss, ref offset, ref factor);
                    }
                }

                _motor[j] = Clamp01((mean / _motorWeight[part] + offset) * factor) * _consciousness;
            }

            // 7. Death, once, for every cause; the first rule that holds names it.
            var cause = DeathCause.None;
            for (var i = 0; i < _tissues.Length && cause == DeathCause.None; i++)
            {
                if (_tissues[i].Vital && _integrity[i] <= 0.0) cause = DeathCause.VitalTissue;
            }

            if (cause == DeathCause.None && BloodLoss >= 1.0) cause = DeathCause.BloodLoss;
            if (cause == DeathCause.None && _consciousness <= 0.0) cause = DeathCause.Consciousness;
            if (cause != DeathCause.None)
            {
                Dead = true;
                Cause = cause;
                DiedAtStep = _steps;
            }

            _steps++;
        }

        private double Capacity(EffectTarget target, double bloodLoss)
        {
            var weightSum = target == EffectTarget.Consciousness ? _consciousnessWeight : _pumpingWeight;
            var mean = 1.0;
            if (weightSum > 0.0)
            {
                var acc = 0.0;
                for (var i = 0; i < _tissues.Length; i++)
                {
                    var w = target == EffectTarget.Consciousness ? _tissues[i].Consciousness : _tissues[i].BloodPumping;
                    if (w > 0.0) acc += w * _integrity[i];
                }

                mean = acc / weightSum;
            }

            double offset = 0.0, factor = 1.0;
            for (var i = 0; i < _organism.Length; i++)
            {
                Fold(_organism[i], target, 0.0, 0.0, bloodLoss, ref offset, ref factor);
            }

            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] >= 0) Fold(_defs[_slotDef[s]].Effects, target, _slotSeverity[s], _slotAge[s], bloodLoss, ref offset, ref factor);
            }

            return Clamp01((mean + offset) * factor);
        }

        private static void Fold(IReadOnlyList<Effect> effects, EffectTarget target, double severity, double age, double bloodLoss, ref double offset, ref double factor)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                Fold(effects[i], target, severity, age, bloodLoss, ref offset, ref factor);
            }
        }

        private static void Fold(Effect e, EffectTarget target, double severity, double age, double bloodLoss, ref double offset, ref double factor)
        {
            if (e.Target != target) return;
            var x = e.Driver == EffectDriver.Severity ? severity : e.Driver == EffectDriver.Age ? age : bloodLoss;
            var v = e.Eval(x);
            if (e.Op == EffectOp.Offset) offset += v;
            else factor *= v;
        }

        private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
    }
}
