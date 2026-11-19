using System;

namespace Lodur
{
    /// <summary>
    /// A game stimulus: an axis-aligned box volume with an intensity the game writes every tick. Lodur reads
    /// it and decides containment and transduction; the box is never a physics body.
    /// </summary>
    public struct BoxStimulus
    {
        public float CenterX, CenterY, CenterZ;
        public float HalfX, HalfY, HalfZ;

        /// <summary>Noxious intensity inside the box, written by the game each tick (0..1 in the slice's scale).</summary>
        public double Intensity;

        public BoxStimulus(float centerX, float centerY, float centerZ, float halfX, float halfY, float halfZ, double intensity = 0.0)
        {
            CenterX = centerX; CenterY = centerY; CenterZ = centerZ;
            HalfX = halfX; HalfY = halfY; HalfZ = halfZ;
            Intensity = intensity;
        }

        /// <summary>True when the point is inside the box or on one of its faces.</summary>
        public bool Contains(float x, float y, float z) =>
            MathF.Abs(x - CenterX) <= HalfX && MathF.Abs(y - CenterY) <= HalfY && MathF.Abs(z - CenterZ) <= HalfZ;
    }

    /// <summary>The nerve's tunables: threshold, sensitisation under sustained stimulus, conduction delay, and a per-character gain.</summary>
    public sealed record NerveParameters
    {
        public double NociceptorThreshold { get; init; } = 0.1;
        public double SensitisationGrowth { get; init; } = 0.04;
        public double SensitisationRecovery { get; init; } = 0.02;
        public double SensitisationCap { get; init; } = 2.0;
        public double ConductionDelaySeconds { get; init; } = 0.06;

        /// <summary>Per-character nociceptor gain (the consumer draws its trait spread).</summary>
        public double Gain { get; init; } = 1.0;
    }

    /// <summary>
    /// Layer 1 for one character, a fixed number of body regions. Each tick it takes one point per region and the
    /// game's stimulus sources and produces, per region, the stimulus there and the afferent signal after
    /// threshold, sensitisation and conduction delay. Everything is allocated in the constructor.
    /// </summary>
    public sealed class Perception
    {
        private readonly NerveParameters _p;
        private readonly double _dt;
        private readonly int _regions;
        private readonly int _slots;
        private readonly double[] _ring;       // afferent signal in flight along the nerve, region-major
        private readonly double[] _sens;
        private readonly double[] _stimulus;
        private readonly double[] _afferent;
        private long _tick;

        public Perception(NerveParameters parameters, double tickSeconds, int regionCount)
        {
            _p = parameters;
            _dt = tickSeconds;
            _regions = regionCount;
            var delayTicks = (int)Math.Round(parameters.ConductionDelaySeconds / tickSeconds);
            _slots = Math.Max(1, delayTicks);
            _ring = new double[regionCount * _slots];
            _sens = new double[regionCount];
            Array.Fill(_sens, 1.0);
            _stimulus = new double[regionCount];
            _afferent = new double[regionCount];
        }

        public int RegionCount => _regions;

        /// <summary>The stimulus at the region's point this tick: the largest intensity among the boxes containing it.</summary>
        public double Stimulus(int region) => _stimulus[region];

        /// <summary>The afferent signal arriving this tick: what the nerve took in a conduction delay ago.</summary>
        public double Afferent(int region) => _afferent[region];

        /// <summary>Senses once per physics step. The spans are the caller's, one point per region.</summary>
        public void Sense(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, ReadOnlySpan<BoxStimulus> sources)
        {
            if (x.Length != _regions || y.Length != _regions || z.Length != _regions)
            {
                throw new ArgumentException($"x, y and z need {_regions} entries, one per region.");
            }

            var slot = (int)(_tick % _slots);
            for (var r = 0; r < _regions; r++)
            {
                var stimulus = 0.0;
                for (var i = 0; i < sources.Length; i++)
                {
                    if (sources[i].Intensity > stimulus && sources[i].Contains(x[r], y[r], z[r]))
                    {
                        stimulus = sources[i].Intensity;
                    }
                }

                // Nociceptor: threshold, then sensitisation, then conduction delay.
                var above = Math.Max(0.0, stimulus - _p.NociceptorThreshold);
                var sens = _sens[r] + _dt * (_p.SensitisationGrowth * above - _p.SensitisationRecovery * (_sens[r] - 1.0));
                sens = Math.Min(Math.Max(sens, 1.0), _p.SensitisationCap);
                _sens[r] = sens;
                var ring = r * _slots + slot;
                _afferent[r] = _ring[ring]; // written one conduction delay ago
                _ring[ring] = above * sens * _p.Gain;
                _stimulus[r] = stimulus;
            }

            _tick++;
        }
    }
}
