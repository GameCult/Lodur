using System;

namespace Lodur
{
    /// <summary>What a character is trying to do. Today: hold against the reflex, or withdraw.</summary>
    public enum Goal
    {
        Hold,
        Withdraw,
    }

    /// <summary>The layer-4 stand-in's tunables.</summary>
    public sealed record HoldParameters
    {
        /// <summary>Muscle strength written for every muscle, in [0, 1] (above 1 an explicit PD arm sags and drifts).</summary>
        public float HoldStrength { get; init; } = 1f;

        /// <summary>How far the reflex leak pulls the pose from hold toward withdraw, per unit of leak.</summary>
        public double FlinchGain { get; init; } = 0.3;

        /// <summary>Strain shake amplitude (blend fraction) per unit of strain. Zero strain, no shake.</summary>
        public double StrainShake { get; init; } = 0.03;

        /// <summary>Shake frequency. A few Hz: the arm's PD filters anything faster to nothing.</summary>
        public double ShakeHz { get; init; } = 3.5;
    }

    /// <summary>
    /// Layer 4 stand-in: hold a pose against the withdrawal reflex. The target is the hold pose blended toward
    /// the withdraw pose by the reflex leak and a strain-scaled shake; once the goal is Withdraw it is the
    /// withdraw pose. It writes per-muscle strength and target rotation into the caller's spans and nothing else
    /// (invariant body-one-interface). The poses are the caller's data, one rotation per muscle.
    /// </summary>
    public sealed class HoldAgainstReflex
    {
        private readonly HoldParameters _p;
        private readonly Quat[] _hold;
        private readonly Quat[] _withdraw;

        public HoldAgainstReflex(HoldParameters parameters, ReadOnlySpan<Quat> holdPose, ReadOnlySpan<Quat> withdrawPose)
        {
            if (holdPose.Length != withdrawPose.Length)
            {
                throw new ArgumentException("The hold and withdraw poses need one rotation per muscle each.");
            }

            _p = parameters;
            _hold = holdPose.ToArray();
            _withdraw = withdrawPose.ToArray();
        }

        public int MuscleCount => _hold.Length;

        /// <summary>The blend toward the withdraw pose: 1 once withdrawing, else the clamped leak and shake.</summary>
        public double Blend(Goal goal, double leak, double strain, double seconds)
        {
            if (goal == Goal.Withdraw)
            {
                return 1.0;
            }

            var w = leak * _p.FlinchGain + _p.StrainShake * strain * Math.Sin(2.0 * Math.PI * _p.ShakeHz * seconds);
            return Math.Min(Math.Max(w, 0.0), 1.0);
        }

        public void Write(Goal goal, double leak, double strain, double seconds, Span<float> strengths, Span<Quat> targets)
        {
            if (strengths.Length != _hold.Length || targets.Length != _hold.Length)
            {
                throw new ArgumentException($"strengths and targets need {_hold.Length} entries, one per muscle.");
            }

            var w = (float)Blend(goal, leak, strain, seconds);
            for (var m = 0; m < _hold.Length; m++)
            {
                strengths[m] = _p.HoldStrength;
                targets[m] = Quat.Nlerp(_hold[m], _withdraw[m], w);
            }
        }
    }
}
