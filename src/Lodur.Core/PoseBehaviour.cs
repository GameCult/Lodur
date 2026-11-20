using System;

namespace Lodur
{
    /// <summary>The pose behaviour's tunables.</summary>
    public sealed record HoldParameters
    {
        /// <summary>Muscle strength written for every muscle, in [0, 1] (above 1 an explicit PD arm sags and drifts).</summary>
        public float HoldStrength { get; init; } = 1f;

        /// <summary>How far the reflex leak pulls the pose from its base toward the flinch pose, per unit of leak.</summary>
        public double FlinchGain { get; init; } = 0.3;

        /// <summary>Strain shake amplitude (blend fraction) per unit of strain. Zero strain, no shake.</summary>
        public double StrainShake { get; init; } = 0.03;

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
        /// <summary>k keys of one rotation per muscle each.</summary>
        public Quat[][] Keys { get; init; } = Array.Empty<Quat[]>();

        /// <summary>Seconds from one key to the next when there is more than one key.</summary>
        public double SecondsPerKey { get; init; } = 1.0;

        /// <summary>The pose the leak and the shake pull toward, one rotation per muscle.</summary>
        public Quat[] FlinchPose { get; init; } = Array.Empty<Quat>();

        public HoldParameters Hold { get; init; } = new HoldParameters();

        public int MuscleCount => Keys.Length == 0 ? 0 : Keys[0].Length;

        internal void Validate(string goal)
        {
            if (Keys.Length == 0 || Keys[0].Length == 0)
            {
                throw new ArgumentException($"Goal {goal}: a pose behaviour needs at least one key of at least one muscle.");
            }

            foreach (var key in Keys)
            {
                if (key.Length != Keys[0].Length)
                {
                    throw new ArgumentException($"Goal {goal}: every key needs one rotation per muscle.");
                }
            }

            if (FlinchPose.Length != Keys[0].Length)
            {
                throw new ArgumentException($"Goal {goal}: the flinch pose needs one rotation per muscle.");
            }

            if (Keys.Length > 1 && !(SecondsPerKey > 0.0 && double.IsFinite(SecondsPerKey)))
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

        public void Write(double leak, double strain, double seconds, double secondsInGoal, Span<float> strengths, Span<Quat> targets)
        {
            var count = MuscleCount;
            if (strengths.Length != count || targets.Length != count)
            {
                throw new ArgumentException($"strengths and targets need {count} entries, one per muscle.");
            }

            var w = (float)Blend(leak, strain, seconds);
            var k = Keys.Length;
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
                var a = Keys[from][m];
                if (phase > 0f)
                {
                    a = Quat.Nlerp(Keys[from][m], Keys[to][m], phase);
                }

                strengths[m] = Hold.HoldStrength;
                targets[m] = Quat.Nlerp(a, FlinchPose[m], w);
            }
        }
    }
}
