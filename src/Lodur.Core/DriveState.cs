using System;

namespace Lodur
{
    /// <summary>The drive's tunables: how pain, strain and arousal accumulate, and how they drain the will.</summary>
    public sealed record DriveParameters
    {
        public double PainRise { get; init; } = 1.0;
        public double PainDecay { get; init; } = 0.05;
        public double StrainRecover { get; init; } = 0.1;
        public double ResolveDrainPain { get; init; } = 0.012;
        public double ResolveDrainStrain { get; init; } = 0.05;
        public double ResolveRecovery { get; init; } = 0.02;
        public double ResolveRecoversBelowPain { get; init; } = 0.1;

        /// <summary>Per-character multiplier on resolve drain (the consumer draws its trait spread).</summary>
        public double DrainTrait { get; init; } = 1.0;

        /// <summary>How fast arousal rises toward 1 under full sensory load.</summary>
        public double ArousalRise { get; init; } = 0.5;

        /// <summary>How fast arousal falls back once the load stops.</summary>
        public double ArousalDecay { get; init; } = 0.1;

        /// <summary>Resolve drained per second at full arousal, before the active goal's pull and the drain trait scale it.</summary>
        public double ResolveDrainArousal { get; init; } = 0.1;

        /// <summary>Resolve recovers only while arousal is below this, as well as pain being low.</summary>
        public double ResolveRecoversBelowArousal { get; init; } = 0.1;

        /// <summary>Once the will has broken it mends when resolve climbs back to this, in (0, 1]; it can then break again.</summary>
        public double WillMendsAt { get; init; } = 0.25;
    }

    /// <summary>
    /// Layer 3: per-character state that evolves every tick. Pain rises with afferent input, strain with the
    /// reflex held down, arousal with sensory load; resolve drains under all three and recovers when pain and
    /// arousal are both low. Resolve reaching zero breaks the will (counted in <see cref="Breaks"/>); the broken will
    /// mends when resolve climbs back to <see cref="DriveParameters.WillMendsAt"/> and can break again. Resolve keeps
    /// evolving through a break. Only the owning <see cref="Actor"/> steps it.
    /// </summary>
    public struct DriveState
    {
        public double Pain { get; private set; }
        public double Strain { get; private set; }

        /// <summary>Sensory load carried over time, 0..1; it rises under load and decays when quiet.</summary>
        public double Arousal { get; private set; }
        public double Resolve { get; private set; }

        /// <summary>True from a break until the will mends: broken now, not "has ever broken" (see <see cref="Breaks"/>).</summary>
        public bool WillBroken { get; private set; }

        /// <summary>How many times the will has broken.</summary>
        public int Breaks { get; private set; }

        /// <summary>The fraction of resolve that attention lets through, 0..1. A slot: nothing lowers it yet (lodur-startle-attention fills it).</summary>
        public double Attention => 1.0;

        /// <summary>The ceiling on the will that wakefulness sets, 0..1. A slot: nothing lowers it yet (lodur-physiology fills it).</summary>
        public double Consciousness => 1.0;

        /// <summary>The will that is available to hold reflexes down and to power voluntary behaviour: resolve through attention, capped by consciousness.</summary>
        public double Will => Math.Min(Resolve * Attention, Consciousness);

        internal static DriveState Fresh => new DriveState { Resolve = 1.0 };

        /// <summary>A goal's entry effect: sets resolve, then the mend check runs (a break the entry lifts past <see cref="DriveParameters.WillMendsAt"/> is mended). It is not a break and counts none.</summary>
        internal void Enter(DriveParameters p, double resolve)
        {
            Resolve = resolve;
            if (WillBroken && Resolve >= p.WillMendsAt)
            {
                WillBroken = false;
            }
        }

        /// <summary>Steps one tick; returns true on the tick the will breaks (each break, not only the first). <paramref name="pull"/> is the active goal's: it scales resolve drain by (1 - pull). <paramref name="effort"/> is the active goal's resolve drained per second to sustain it (0 while it is involuntary); it joins the drain sum, so pull scales it too.</summary>
        internal bool Step(DriveParameters p, double dt, double afferent, double strainLoad, double load, double pull, double effort)
        {
            Arousal = Math.Min(Math.Max(Arousal + dt * (p.ArousalRise * load * (1.0 - Arousal) - p.ArousalDecay * Arousal), 0.0), 1.0);
            Pain = Math.Min(Math.Max(Pain + dt * (p.PainRise * afferent * (1.0 - Pain) - p.PainDecay * Pain), 0.0), 1.0);
            Strain = Math.Max(0.0, Strain + dt * (strainLoad - p.StrainRecover * Strain));
            var drain = (p.ResolveDrainPain * Pain + p.ResolveDrainStrain * Strain + p.ResolveDrainArousal * Arousal + effort) * (1.0 - pull) * p.DrainTrait;
            var recovery = Pain < p.ResolveRecoversBelowPain && Arousal < p.ResolveRecoversBelowArousal ? p.ResolveRecovery * (1.0 - Resolve) : 0.0;
            Resolve = Math.Min(Math.Max(Resolve + dt * (recovery - drain), 0.0), 1.0);
            if (WillBroken)
            {
                if (Resolve >= p.WillMendsAt)
                {
                    WillBroken = false;
                }

                return false;
            }

            if (Resolve <= 0.0)
            {
                WillBroken = true;
                Breaks++;
                return true;
            }

            return false;
        }
    }
}
