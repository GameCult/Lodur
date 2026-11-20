using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>The pose behaviour's tunables.</summary>
    public sealed record HoldParameters
    {
        /// <summary>Muscle strength written for every muscle, in [0, 1] (above 1 an explicit PD arm sags and drifts).</summary>
        public float HoldStrength { get; init; } = 1f;

        /// <summary>How far the reflex leak pulls the pose from its base toward the flinch pose, per unit of leak.</summary>
        public double FlinchGain { get; init; } = 0.3;

        /// <summary>Strain shake amplitude (blend fraction) per unit of strain. Zero strain, no shake. Measured at the palm of the ragdoll with hands (hand mass damps the arm's response), where it makes the 3-7 cm shake in hand height the strain rule describes.</summary>
        public double StrainShake { get; init; } = 0.08;

        /// <summary>Shake frequency. A few Hz: the arm's PD filters anything faster to nothing.</summary>
        public double ShakeHz { get; init; } = 3.5;
    }

    /// <summary>
    /// The one behaviour a goal can have: a pose held, or a loop through pose keys, with the reflex leak and a
    /// strain-scaled shake blending the base pose toward the flinch pose. One key is held as it is; k keys are
    /// walked in a loop, <see cref="SecondsPerKey"/> from each to the next and the last back to the first.
    /// It writes per-muscle strength and target rotation into the caller's spans and nothing else (invariant
    /// body-one-interface). The poses are the consumer's data, one rotation per muscle. A release is the same
    /// behaviour with a key and a flinch pose equal and the gains zero.
    /// </summary>
    public sealed record PoseBehaviour
    {
        private Quat[][] _keys = Array.Empty<Quat[]>();
        private Quat[] _flinch = Array.Empty<Quat>();
        private IReadOnlyList<IReadOnlyList<Quat>> _keysView = Array.AsReadOnly(Array.Empty<IReadOnlyList<Quat>>());
        private IReadOnlyList<Quat> _flinchView = Array.AsReadOnly(Array.Empty<Quat>());

        /// <summary>k keys of one rotation per muscle each. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<IReadOnlyList<Quat>> Keys
        {
            get => _keysView;
            init
            {
                var copy = new Quat[value.Count][];
                var views = new IReadOnlyList<Quat>[copy.Length];
                for (var k = 0; k < copy.Length; k++)
                {
                    copy[k] = new Quat[value[k].Count];
                    for (var m = 0; m < copy[k].Length; m++)
                    {
                        copy[k][m] = value[k][m];
                    }

                    views[k] = Array.AsReadOnly(copy[k]);
                }

                _keys = copy;
                _keysView = Array.AsReadOnly(views);
            }
        }

        /// <summary>Seconds from one key to the next when there is more than one key.</summary>
        public double SecondsPerKey { get; init; } = 1.0;

        /// <summary>The pose the leak and the shake pull toward, one rotation per muscle. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Quat> FlinchPose
        {
            get => _flinchView;
            init
            {
                _flinch = new Quat[value.Count];
                for (var m = 0; m < _flinch.Length; m++)
                {
                    _flinch[m] = value[m];
                }

                _flinchView = Array.AsReadOnly(_flinch);
            }
        }

        public HoldParameters Hold { get; init; } = new HoldParameters();

        public int MuscleCount => _keys.Length == 0 ? 0 : _keys[0].Length;

        internal void Validate(string goal)
        {
            if (_keys.Length == 0 || _keys[0].Length == 0)
            {
                throw new ArgumentException($"Goal {goal}: a pose behaviour needs at least one key of at least one muscle.");
            }

            foreach (var key in _keys)
            {
                if (key.Length != _keys[0].Length)
                {
                    throw new ArgumentException($"Goal {goal}: every key needs one rotation per muscle.");
                }
            }

            if (_flinch.Length != _keys[0].Length)
            {
                throw new ArgumentException($"Goal {goal}: the flinch pose needs one rotation per muscle.");
            }

            if (_keys.Length > 1 && !(SecondsPerKey > 0.0 && double.IsFinite(SecondsPerKey)))
            {
                throw new ArgumentException($"Goal {goal}: SecondsPerKey must be positive and finite.");
            }
        }

        /// <summary>The blend toward the flinch pose: the leak and the shake, clamped to [0, 1]. <paramref name="seconds"/> is the Actor's absolute time.</summary>
        public double Blend(double leak, double strain, double seconds)
        {
            var w = leak * Hold.FlinchGain + Hold.StrainShake * strain * Math.Sin(2.0 * Math.PI * Hold.ShakeHz * seconds);
            return Math.Min(Math.Max(w, 0.0), 1.0);
        }

        /// <summary>
        /// Writes one tick's strengths and targets. The strength is <see cref="HoldParameters.HoldStrength"/> scaled by the
        /// <paramref name="weight"/> in [0, 1] (the will that powers the layer, or 1 while it is involuntary) plus the leaked
        /// reflex, capped at 1: a reflex that gets past a failing will still moves the muscle. The target rule is
        /// <see cref="Blend"/>, unchanged.
        /// </summary>
        public void Write(double weight, double leak, double strain, double seconds, double secondsInGoal, Span<float> strengths, Span<Quat> targets)
        {
            var count = MuscleCount;
            if (strengths.Length != count || targets.Length != count)
            {
                throw new ArgumentException($"strengths and targets need {count} entries, one per muscle.");
            }

            if (!(weight >= 0.0 && weight <= 1.0))
            {
                throw new ArgumentOutOfRangeException(nameof(weight), "The weight must be in [0, 1].");
            }

            var w = (float)Blend(leak, strain, seconds);
            var strength = Hold.HoldStrength * (float)Math.Min(1.0, weight + leak * Hold.FlinchGain);
            var k = _keys.Length;
            var from = 0;
            var to = 0;
            var phase = 0f;
            if (k > 1)
            {
                var p = secondsInGoal / SecondsPerKey;
                var whole = Math.Floor(p);
                from = (int)(whole % k);
                to = (from + 1) % k;
                phase = (float)(p - whole);
            }

            for (var m = 0; m < count; m++)
            {
                var a = _keys[from][m];
                if (phase > 0f)
                {
                    a = Quat.Nlerp(_keys[from][m], _keys[to][m], phase);
                }

                strengths[m] = strength;
                targets[m] = Quat.Nlerp(a, _flinch[m], w);
            }
        }
    }
}
