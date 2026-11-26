using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Why a body died; the first rule that held at the end of the step that killed it (ruling bds-death).</summary>
    public enum DeathCause { None, VitalTissue, BloodLoss, Consciousness }

    /// <summary>
    /// The liquid at a body's airway openings this step, as the caller measures it: the live liquid and the pooled liquid, each a non-negative amount
    /// summed over the openings, and Drop in m, at least 0: how far the lowest opening's rim lies below the deepest point liquid pools at inside the airway, 0 when none lies below.
    /// The default is nothing at the airway.
    /// </summary>
    public readonly record struct AirwaySample(double Live, double Pooled, double Drop)
    {
        /// <summary>One entry per opening: Live and Pooled summed, Drop the largest of the openings' signed drops, floored at 0 (0 with no opening).</summary>
        public static AirwaySample Of(ReadOnlySpan<float> live, ReadOnlySpan<float> pooled, ReadOnlySpan<float> drop)
        {
            double l = 0.0, p = 0.0, f = 0.0;
            for (var i = 0; i < live.Length; i++) l += live[i];
            for (var i = 0; i < pooled.Length; i++) p += pooled[i];
            for (var i = 0; i < drop.Length; i++) f = Math.Max(f, drop[i]);
            return new AirwaySample(l, p, f);
        }
    }

    /// <summary>
    /// What the stepping Actor reports each step. Arousal is the sensory load carried over time and Effort the mean strength the muscles actually applied last tick,
    /// after each joint's motor ceiling (the Actor reports it so, not what the behaviour asked), each in [0, 1]. BreathHeld is whether the breath is held this step
    /// (decided by the Actor on the tick before); Airway is what the caller finds at the airway. The default is rest, breathing freely, nothing at the airway.
    /// </summary>
    public readonly record struct PhysiologyInput(double Arousal, double Effort, bool BreathHeld = false, AirwaySample Airway = default);

    /// <summary>
    /// The body's physiology: tissue integrity, bound afflictions, blood and its oxygen, the capacities they fold into, the motor ceiling and death.
    /// Harm enters only through <see cref="Afflict"/>; time through <see cref="Step"/>. Integrity is derived from the severities of the
    /// Injures afflictions bound to a tissue (part HP is never stored), and capacities, bleed and the motor ceiling are recomputed each step;
    /// nobody writes them; the gases are integrated by Step and nobody writes those either. Content owns every label and number (<see cref="PhysiologyPlan"/>). Everything is allocated in the constructor.
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
        private readonly double[] _capacityWeight;     // per capacity, indexed by EffectTarget (Consciousness, BloodPumping, Breathing)
        private readonly RespirationDef _respiration;
        private readonly AirwayDef _airway;
        private readonly double[] _drivers;             // the organism's driver values as the step began, indexed by EffectDriver
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
        private double _breathing = 1.0;
        private double _bloodOxygen = 1.0;
        private double _brainOxygen = 1.0;
        private double _airwayLiquid;
        private double _occlusion;
        private double _carbonDioxide;
        private double _urge;
        private double _coughed;
        private double _drained;
        private bool _atAirway;
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
            _respiration = plan.Respiration;
            _airway = plan.Airway;
            _volume = _nominal;
            _capacityWeight = new double[CapacityCount];
            _drivers = new double[Enum.GetValues(typeof(EffectDriver)).Length];

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
                    _capacityWeight[(int)EffectTarget.Consciousness] += t.Consciousness;
                    _capacityWeight[(int)EffectTarget.BloodPumping] += t.BloodPumping;
                    _capacityWeight[(int)EffectTarget.Breathing] += t.Breathing;
                    _motorWeight[p] += t.Motor;
                }
            }

            if (!(_capacityWeight[(int)EffectTarget.Consciousness] > 0.0)) throw new ArgumentException("no tissue carries consciousness.");
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

        /// <summary>The lungs' capacity to ventilate, 0..1; 0 once dead. An empty Breathing weight reads 1, as BloodPumping does.</summary>
        public double Breathing => Dead ? 0.0 : _breathing;

        /// <summary>The blood's oxygen store, 0..1, starting full; integrated by <see cref="Step"/>.</summary>
        public double BloodOxygen => _bloodOxygen;

        /// <summary>The brain's oxygen, 0..1, starting full: it relaxes toward what the pumping delivers from the blood store. Content turns it into consciousness.</summary>
        public double BrainOxygen => _brainOxygen;

        /// <summary>The liquid in the airway in m^3, 0 to the airway's capacity; integrated by <see cref="Step"/>.</summary>
        public double AirwayLiquid => _airwayLiquid;

        /// <summary>The fraction of the airway the liquid fills, 0..1; it takes that share of the ventilation. Derived from the liquid each step.</summary>
        public double Occlusion => _occlusion;

        /// <summary>Carbon dioxide, 0 at rest and about 1 at the break point; it rises with the load that uses oxygen and is blown off by ventilation.</summary>
        public double CarbonDioxide => _carbonDioxide;

        /// <summary>Air hunger, 0..1: the larger of the carbon dioxide urge and the ventilation deficit urge as the last step computed them; 0 once dead. The Actor carries it to the drive.</summary>
        public double BreathUrge => Dead ? 0.0 : _urge;

        /// <summary>True when the liquid at the airway the last step was sampled was at least the airway's HoldAt: the Actor holds the breath while it is.</summary>
        public bool LiquidAtAirway => _atAirway;

        /// <summary>The liquid in m^3 the last step coughed out of the airway.</summary>
        public double Coughed => _coughed;

        /// <summary>The liquid in m^3 the last step drained out of the airway (by gravity through the mouth).</summary>
        public double Drained => _drained;

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
        /// capacities, bleeding and regrowth follow, then the gases on the Acute clock, the motor ceilings, and death is judged once.
        /// A dead physiology does not change. A non-finite input, or one outside [0, 1], is refused before anything changes.
        /// </summary>
        public void Step(double dt, in PhysiologyInput input = default)
        {
            if (!(input.Arousal >= 0.0 && input.Arousal <= 1.0 && input.Effort >= 0.0 && input.Effort <= 1.0))
            {
                throw new ArgumentException("the input's arousal and effort must be finite and in [0, 1].");
            }

            var air = input.Airway;
            if (!(air.Live >= 0.0 && air.Live < double.PositiveInfinity && air.Pooled >= 0.0 && air.Pooled < double.PositiveInfinity && air.Drop >= 0.0 && air.Drop < double.PositiveInfinity))
            {
                throw new ArgumentException("the airway sample's liquid must be finite and not negative and its drop finite and not negative.");
            }

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

            // 3. Capacities: the weighted mean integrity, then every effect's offsets, then its factors. The blood loss and brain oxygen they read are the values the step began with.
            _drivers[(int)EffectDriver.BloodLoss] = BloodLoss;
            _drivers[(int)EffectDriver.BrainOxygen] = _brainOxygen;
            _consciousness = Capacity(EffectTarget.Consciousness);
            _pumping = Capacity(EffectTarget.BloodPumping);
            _breathing = Capacity(EffectTarget.Breathing);

            // 3b. The airway, a quantity (not particles): liquid at the openings is drawn in by a free breath, never by a held one; a cough clears it by this step's
            // consciousness, and it drains by gravity through the mouth. What a step removes is reported as Coughed and Drained; occlusion takes its share of the ventilation below.
            var held = input.BreathHeld;
            var aw = _airway;
            var at = aw.LiveWeight * air.Live + aw.PooledWeight * air.Pooled;
            _atAirway = at > 0.0 && at >= aw.HoldAt;
            var inflow = held ? 0.0 : aw.DrawPerSecond * _breathing * at;
            var cough = aw.CoughPerSecond * Effect.EvalCurve(aw.CoughByConsciousness, _consciousness);
            var drain = aw.DrainPerSecond * Clamp01(air.Drop / aw.DrainDrop);
            var total = cough + drain;
            var avail = _airwayLiquid + dt * inflow;
            var want = dt * total * _airwayLiquid;
            var removed = want < avail ? want : avail;
            _coughed = total > 0.0 ? removed * cough / total : 0.0;
            _drained = removed - _coughed;
            _airwayLiquid = Math.Min(aw.Capacity, avail - removed);
            _occlusion = Clamp01(_airwayLiquid / aw.Capacity);

            // 4. Bleeding: each affliction's bleed effects, through its tissue's vascularity and the pumping.
            Array.Clear(_bleed, 0, _bleed.Length);
            var totalBleed = 0.0;
            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] < 0) continue;
                double offset = 0.0, factor = 1.0;
                Fold(_defs[_slotDef[s]].Effects, EffectTarget.Bleed, _slotSeverity[s], _slotAge[s], ref offset, ref factor);
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

            // 6. The gases, on the Acute clock (ruling bds-asphyxia-real-time): the blood store refills by ventilation and is used by exertion;
            // the brain relaxes toward what the pumping delivers. Placeholder constants live in content (RespirationDef).
            var r = _respiration;
            var load = 1.0 + r.ArousalUse * input.Arousal + r.EffortUse * input.Effort;
            var use = r.BasalUsePerSecond * load;
            var ventilation = held ? 0.0 : _breathing * (1.0 - _occlusion);
            _bloodOxygen = Clamp01(_bloodOxygen + dt * (r.UptakePerSecond * ventilation * (1.0 - _bloodOxygen) - use));
            _carbonDioxide = Math.Max(0.0, _carbonDioxide + dt * (r.CarbonDioxideRise * load - r.CarbonDioxideClear * ventilation * _carbonDioxide));
            // Air hunger: carbon dioxide, or a breath tried and not got (neuroventilatory dissociation, felt at once); a held breath is not tried, so its urge is carbon dioxide alone.
            var deficit = held ? 0.0 : 1.0 - _breathing * (1.0 - _occlusion);
            _urge = Math.Max(Effect.EvalCurve(r.UrgeByCarbonDioxide, _carbonDioxide), Effect.EvalCurve(r.UrgeByVentilationDeficit, deficit));
            _brainOxygen = Clamp01(_brainOxygen + (_pumping * _bloodOxygen - _brainOxygen) * (1.0 - Math.Exp(-dt / r.BrainSeconds)));

            // 7. Motor ceilings: the child part's motor-weighted integrity, folded with its live afflictions' motor effects, times consciousness.
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
                        Fold(_defs[_slotDef[s]].Effects, EffectTarget.Motor, _slotSeverity[s], _slotAge[s], ref offset, ref factor);
                    }
                }

                _motor[j] = Clamp01((mean / _motorWeight[part] + offset) * factor) * _consciousness;
            }

            // 8. Death, once, for every cause; the first rule that holds names it.
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

        private const int CapacityCount = 3;

        private static double Weight(in TissueDef t, EffectTarget target) => target switch
        {
            EffectTarget.Consciousness => t.Consciousness,
            EffectTarget.BloodPumping => t.BloodPumping,
            _ => t.Breathing,
        };

        private double Capacity(EffectTarget target)
        {
            var weightSum = _capacityWeight[(int)target];
            var mean = 1.0;
            if (weightSum > 0.0)
            {
                var acc = 0.0;
                for (var i = 0; i < _tissues.Length; i++)
                {
                    var w = Weight(_tissues[i], target);
                    if (w > 0.0) acc += w * _integrity[i];
                }

                mean = acc / weightSum;
            }

            double offset = 0.0, factor = 1.0;
            for (var i = 0; i < _organism.Length; i++)
            {
                Fold(_organism[i], target, 0.0, 0.0, ref offset, ref factor);
            }

            for (var s = 0; s < _slotDef.Length; s++)
            {
                if (_slotDef[s] >= 0) Fold(_defs[_slotDef[s]].Effects, target, _slotSeverity[s], _slotAge[s], ref offset, ref factor);
            }

            return Clamp01((mean + offset) * factor);
        }

        private void Fold(IReadOnlyList<Effect> effects, EffectTarget target, double severity, double age, ref double offset, ref double factor)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                Fold(effects[i], target, severity, age, ref offset, ref factor);
            }
        }

        private void Fold(Effect e, EffectTarget target, double severity, double age, ref double offset, ref double factor)
        {
            if (e.Target != target) return;
            var x = e.Driver switch
            {
                EffectDriver.Severity => severity,
                EffectDriver.Age => age,
                _ => _drivers[(int)e.Driver],
            };
            var v = e.Eval(x);
            if (e.Op == EffectOp.Offset) offset += v;
            else factor *= v;
        }

        private static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
    }
}
