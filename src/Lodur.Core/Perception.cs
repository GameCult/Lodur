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

    /// <summary>The sensory channels a point emitter can speak on. Each has its own stimulus, sensitisation and nerve.</summary>
    public enum Channel
    {
        Audio = 0,
        Visual = 1,
        Interrupt = 2,
    }

    /// <summary>
    /// A game stimulus at a point: sound, flicker, a notification. The game writes its position and intensity every
    /// tick; Lodur decides what reaches the sense region. What arrives is the intensity falling off linearly to zero
    /// at the radius.
    /// </summary>
    public struct Emitter
    {
        public Channel Channel;
        public float X, Y, Z;
        public float Radius;
        public double Intensity;

        public Emitter(Channel channel, float x, float y, float z, float radius, double intensity = 0.0)
        {
            Channel = channel;
            X = x; Y = y; Z = z;
            Radius = radius;
            Intensity = intensity;
        }

        /// <summary>Intensity * max(0, 1 - distance / Radius) at the point; zero at and beyond the radius.</summary>
        public double Received(float x, float y, float z)
        {
            double dx = x - X, dy = y - Y, dz = z - Z;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return Radius > 0f && distance < Radius ? Intensity * (1.0 - distance / Radius) : 0.0;
        }
    }

    /// <summary>The nerve's tunables, the transduction of any channel, noxious or sensory: threshold, sensitisation under sustained stimulus, conduction delay, and a per-character gain.</summary>
    public sealed record NerveParameters
    {
        public double Threshold { get; init; } = 0.1;
        public double SensitisationGrowth { get; init; } = 0.04;
        public double SensitisationRecovery { get; init; } = 0.02;
        public double SensitisationCap { get; init; } = 2.0;
        public double ConductionDelaySeconds { get; init; } = 0.06;

        /// <summary>Per-character gain (the consumer draws its trait spread).</summary>
        public double Gain { get; init; } = 1.0;
    }

    /// <summary>
    /// Layer 1 for one character, a fixed number of body regions. Each tick it takes one point per region and the
    /// game's stimulus sources and produces, per region, the stimulus there and the afferent signal after
    /// threshold, sensitisation and conduction delay. With a sensory nerve it also senses point emitters at one
    /// region on three channels and combines the channels into one load. Everything is allocated in the constructor.
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
        private readonly NerveParameters? _senses;
        private readonly int _senseRegion;
        private readonly int _senseSlots;
        private readonly double[] _senseRing;  // afferent in flight per channel, channel-major
        private readonly double[] _senseSens = new double[ChannelCount];
        private readonly double[] _senseStimulus = new double[ChannelCount];
        private readonly double[] _senseAfferent = new double[ChannelCount];
        private long _tick;

        private const int ChannelCount = 3;

        /// <param name="senses">The nerve of the sensory channels; null for a body with no senses (the load stays zero).</param>
        /// <param name="senseRegion">The region whose point the emitters are sensed at.</param>
        public Perception(NerveParameters parameters, double tickSeconds, int regionCount, NerveParameters? senses = null, int senseRegion = 0)
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
            _senses = senses;
            _senseRegion = senseRegion;
            if (senses != null)
            {
                if ((uint)senseRegion >= (uint)regionCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(senseRegion), $"The sense region must be one of {regionCount} regions.");
                }

                _senseSlots = Math.Max(1, (int)Math.Round(senses.ConductionDelaySeconds / tickSeconds));
                _senseRing = new double[ChannelCount * _senseSlots];
                Array.Fill(_senseSens, 1.0);
            }
            else
            {
                _senseRing = Array.Empty<double>();
            }
        }

        public int RegionCount => _regions;

        /// <summary>The stimulus at the region's point this tick: the largest intensity among the boxes containing it.</summary>
        public double Stimulus(int region) => _stimulus[region];

        /// <summary>The afferent signal arriving this tick: what the nerve took in a conduction delay ago.</summary>
        public double Afferent(int region) => _afferent[region];

        /// <summary>The sensory signals combined: 1 - the product over channels of (1 - afferent, capped at 1), the afferents being the gated ones (see <see cref="ChannelAfferent"/>). Zero with no senses or no emitters.</summary>
        public double Load { get; private set; }

        /// <summary>The largest intensity of the channel's emitters at the sense region's point this tick.</summary>
        public double ChannelStimulus(Channel channel) => _senseStimulus[(int)channel];

        /// <summary>
        /// The channel's afferent arriving this tick, after threshold, sensitisation and conduction delay, times the sensory gain
        /// <see cref="Sense"/> was given (lodur:ruling:stress-bar-shows-arousal-calm-silences). <see cref="ChannelStimulus"/> stays the world's.
        /// </summary>
        public double ChannelAfferent(Channel channel) => _senseAfferent[(int)channel];

        /// <summary>
        /// Senses once per physics step. The spans are the caller's, one point per region. The sensory gain (default 1, the identity) multiplies the sense
        /// channels' afferents as they leave the conduction delay, so a change of gain reaches <see cref="Load"/> on the next call it is passed to; the nociceptive region
        /// afferents are never gated. A gain outside [0, 1] (NaN and infinity included) is refused.
        /// </summary>
        public void Sense(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, ReadOnlySpan<BoxStimulus> sources,
            ReadOnlySpan<Emitter> emitters = default, double sensoryGain = 1.0)
        {
            if (x.Length != _regions || y.Length != _regions || z.Length != _regions)
            {
                throw new ArgumentException($"x, y and z need {_regions} entries, one per region.");
            }

            if (!(sensoryGain >= 0.0 && sensoryGain <= 1.0))
            {
                throw new ArgumentException("The sensory gain must be in [0, 1].", nameof(sensoryGain));
            }

            if (_senses != null)
            {
                // Refused before any state steps, so a bad emitter leaves the tick unrun.
                for (var i = 0; i < emitters.Length; i++)
                {
                    if ((uint)emitters[i].Channel >= ChannelCount)
                    {
                        throw new ArgumentException($"Emitter {i} has channel {(int)emitters[i].Channel}; the channels are Audio, Visual and Interrupt.", nameof(emitters));
                    }
                }
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
                var above = Math.Max(0.0, stimulus - _p.Threshold);
                var sens = _sens[r] + _dt * (_p.SensitisationGrowth * above - _p.SensitisationRecovery * (_sens[r] - 1.0));
                sens = Math.Min(Math.Max(sens, 1.0), _p.SensitisationCap);
                _sens[r] = sens;
                var ring = r * _slots + slot;
                _afferent[r] = _ring[ring]; // written one conduction delay ago
                _ring[ring] = above * sens * _p.Gain;
                _stimulus[r] = stimulus;
            }

            if (_senses != null)
            {
                SenseEmitters(x[_senseRegion], y[_senseRegion], z[_senseRegion], emitters, sensoryGain);
            }

            _tick++;
        }

        private void SenseEmitters(float x, float y, float z, ReadOnlySpan<Emitter> emitters, double gain)
        {
            var senses = _senses!;
            var slot = (int)(_tick % _senseSlots);
            Array.Clear(_senseStimulus, 0, ChannelCount);
            for (var i = 0; i < emitters.Length; i++)
            {
                var c = (int)emitters[i].Channel;
                var received = emitters[i].Received(x, y, z);
                if (received > _senseStimulus[c])
                {
                    _senseStimulus[c] = received;
                }
            }

            var quiet = 1.0;
            for (var c = 0; c < ChannelCount; c++)
            {
                var above = Math.Max(0.0, _senseStimulus[c] - senses.Threshold);
                var sens = _senseSens[c] + _dt * (senses.SensitisationGrowth * above - senses.SensitisationRecovery * (_senseSens[c] - 1.0));
                sens = Math.Min(Math.Max(sens, 1.0), senses.SensitisationCap);
                _senseSens[c] = sens;
                var ring = c * _senseSlots + slot;
                // Written one conduction delay ago; the gate sits after the ring, so sensitisation still follows the world.
                _senseAfferent[c] = _senseRing[ring] * gain;
                _senseRing[ring] = above * sens * senses.Gain;
                quiet *= 1.0 - Math.Min(1.0, _senseAfferent[c]);
            }

            Load = 1.0 - quiet;
        }
    }
}
