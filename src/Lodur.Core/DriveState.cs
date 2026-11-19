using System;

namespace Lodur
{
    /// <summary>The drive's tunables: how pain and strain accumulate, and how they drain the will.</summary>
    public sealed record DriveParameters
    {
        public double PainRise { get; init; } = 1.0;
        public double PainDecay { get; init; } = 0.05;
        public double StrainRecover { get; init; } = 0.1;
        public double ResolveDrainPain { get; init; } = 0.012;
        public double ResolveDrainStrain { get; init; } = 0.05;
        public double ResolveRecovery { get; init; } = 0.02;
        public double ResolveRecoversBelowPain { get; init; } = 0.1;

        /// <summary>How hard the objective pulls, 0..1. It scales resolve drain by (1 - pull); it is not a second resolve bar.</summary>
        public double ObjectivePull { get; init; } = 0.5;

        /// <summary>Per-character multiplier on resolve drain (the consumer draws its trait spread).</summary>
        public double DrainTrait { get; init; } = 1.0;
    }

    /// <summary>
    /// Layer 3: per-character state that evolves every tick. Pain rises with afferent input, strain with the
    /// reflex held down, resolve drains under both and recovers when pain is low. Resolve reaching zero breaks
    /// the will, and a broken will never mends. Only the owning <see cref="Actor"/> steps it.
    /// </summary>
    public struct DriveState
    {
        public double Pain { get; private set; }
        public double Strain { get; private set; }
        public double Resolve { get; private set; }
        public bool WillBroken { get; private set; }

        internal static DriveState Fresh => new DriveState { Resolve = 1.0 };

        /// <summary>Steps one tick; returns true on the tick the will breaks.</summary>
        internal bool Step(DriveParameters p, double dt, double afferent, double strainLoad)
        {
            Pain = Math.Min(Math.Max(Pain + dt * (p.PainRise * afferent * (1.0 - Pain) - p.PainDecay * Pain), 0.0), 1.0);
            Strain = Math.Max(0.0, Strain + dt * (strainLoad - p.StrainRecover * Strain));
            if (WillBroken)
            {
                return false;
            }

            var drain = (p.ResolveDrainPain * Pain + p.ResolveDrainStrain * Strain) * (1.0 - p.ObjectivePull) * p.DrainTrait;
            var recovery = Pain < p.ResolveRecoversBelowPain ? p.ResolveRecovery * (1.0 - Resolve) : 0.0;
            Resolve = Math.Min(Math.Max(Resolve + dt * (recovery - drain), 0.0), 1.0);
            if (Resolve <= 0.0)
            {
                WillBroken = true;
                return true;
            }

            return false;
        }
    }
}
