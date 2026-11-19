using System;

namespace Lodur
{
    /// <summary>The withdrawal reflex's tunables, and the latency of the deliberate withdrawal that takes over from it.</summary>
    public sealed record WithdrawalParameters
    {
        /// <summary>How much of the reflex a fully resolved will can hold down (0..1).</summary>
        public double MaxSuppression { get; init; } = 0.95;
        public double FlinchLeakThreshold { get; init; } = 0.3;
        public double FlinchRefractorySeconds { get; init; } = 0.5;

        /// <summary>How long after the will breaks deliberate behaviour withdraws.</summary>
        public double LatencySeconds { get; init; } = 0.1;
    }

    /// <summary>
    /// Layer 2 for one body region. Its gain is set ahead of time by the drive: it reads the drive state the
    /// previous tick left, before the drive steps (invariant reflex-gain-from-drive). What the suppression holds
    /// down is the strain load; what leaks past is the reflex command and, over the threshold, a flinch.
    /// </summary>
    public sealed class WithdrawalReflex
    {
        private readonly WithdrawalParameters _p;
        private readonly long _refractory;
        private long _tick;
        private long _lastFlinch = long.MinValue / 2;

        public WithdrawalReflex(WithdrawalParameters parameters, double tickSeconds)
        {
            _p = parameters;
            _refractory = (long)Math.Round(parameters.FlinchRefractorySeconds / tickSeconds);
        }

        /// <summary>The part of the afferent signal that gets past the will this tick.</summary>
        public double Leak { get; private set; }

        /// <summary>The part the will holds down this tick: the strain the drive accumulates.</summary>
        public double StrainLoad { get; private set; }

        /// <summary>Steps once; returns true on a flinch (leak over the threshold, outside the refractory window).</summary>
        public bool Step(double afferent, in DriveState drive)
        {
            var suppression = drive.WillBroken ? 0.0 : _p.MaxSuppression * drive.Resolve;
            Leak = afferent * (1.0 - suppression);
            StrainLoad = afferent * suppression;
            var flinch = Leak > _p.FlinchLeakThreshold && _tick - _lastFlinch >= _refractory;
            if (flinch)
            {
                _lastFlinch = _tick;
            }

            _tick++;
            return flinch;
        }
    }
}
