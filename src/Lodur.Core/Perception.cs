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
    /// at the radius. With an <see cref="Id"/> it is also a thing the puppet can come to know, remembered as a percept.
    /// </summary>
    public struct Emitter
    {
        public Channel Channel;
        public float X, Y, Z;
        public float Radius;
        public double Intensity;

        /// <summary>The game's stable id for the thing this emitter belongs to. 0 is anonymous: it loads the nerve and is never a percept. The top bit is reserved (<see cref="Perception.PhantomIdBit"/>); <see cref="Perception.Sense"/> refuses it.</summary>
        public ulong Id;

        /// <summary>The game's tag for the thing (what kind it is). Lodur carries it and never interprets it; emitters sharing an id must share a kind.</summary>
        public uint Kind;

        /// <summary>The thing's facts as the game sees them, bit i is fact i. The game owns every bit but <see cref="Perception.InReachFact"/> (63), which <see cref="Perception.Sense"/> refuses; emitters sharing an id must share facts.</summary>
        public ulong Facts;

        public Emitter(Channel channel, float x, float y, float z, float radius, double intensity = 0.0, ulong id = 0, uint kind = 0, ulong facts = 0)
        {
            Channel = channel;
            X = x; Y = y; Z = z;
            Radius = radius;
            Intensity = intensity;
            Id = id;
            Kind = kind;
            Facts = facts;
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

    /// <summary>The percept memory's tunables.</summary>
    public sealed record PerceptParameters
    {
        /// <summary>How many things can be remembered at once, 1..256.</summary>
        public int Capacity { get; init; } = 16;

        /// <summary>How long a thing stays remembered after it was last sensed; finite and not negative.</summary>
        public double MemorySeconds { get; init; } = 5.0;

        public void Validate()
        {
            if (Capacity < 1 || Capacity > 256)
            {
                throw new ArgumentException("The percept capacity must be in 1..256.", nameof(Capacity));
            }

            if (!(MemorySeconds >= 0.0 && MemorySeconds < double.PositiveInfinity))
            {
                throw new ArgumentException("The percept memory time must be finite and not negative.", nameof(MemorySeconds));
            }
        }
    }

    /// <summary>One remembered thing. Id 0 is a free slot. Nothing here says whether the thing is real.</summary>
    public readonly struct Percept
    {
        public ulong Id { get; }
        public uint Kind { get; }

        /// <summary>The thing's facts as last sensed; a thing changed out of sight keeps its old facts.</summary>
        public ulong Facts { get; }

        /// <summary>Where the emitter that reached the puppet hardest was, the last tick the thing was sensed.</summary>
        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        /// <summary>The largest intensity received among the thing's emitters times the sensory gain; 0 on a tick it is not sensed.</summary>
        public double Salience { get; }

        /// <summary>0 on a tick the thing is sensed.</summary>
        public double SecondsSinceSensed { get; }

        public Percept(ulong id, uint kind, float x, float y, float z, double salience, double secondsSinceSensed, ulong facts = 0)
        {
            Id = id; Kind = kind; Facts = facts; X = x; Y = y; Z = z; Salience = salience; SecondsSinceSensed = secondsSinceSensed;
        }
    }

    /// <summary>
    /// Layer 1 for one character, a fixed number of body regions. Each tick it takes one point per region and the
    /// game's stimulus sources and produces, per region, the stimulus there and the afferent signal after
    /// threshold, sensitisation and conduction delay. With a sensory nerve it also senses point emitters at one
    /// region on three channels and combines the channels into one load and remembers the identified things it senses in a bounded slot table. Everything is allocated in the constructor.
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
        private readonly double _memorySeconds;
        private readonly ulong[] _pId;        // percept slots; id 0 is free
        private readonly uint[] _pKind;
        private readonly ulong[] _pFacts;
        private readonly float[] _pX, _pY, _pZ;
        private readonly double[] _pSalience;
        private readonly double[] _pSince;
        private readonly bool[] _pSensed;     // sensed this tick
        private long _tick;

        private const int ChannelCount = 3;

        /// <summary>The top bit of an emitter id, reserved to Lodur's phantoms; <see cref="Sense"/> refuses a caller emitter that sets it.</summary>
        public const ulong PhantomIdBit = 1UL << 63;

        /// <summary>The top fact bit, Lodur's own: whether the thing is within reach. The planner is its only writer, deriving it per call from distance; <see cref="Sense"/> refuses an emitter that sets it.</summary>
        public const ulong InReachFact = 1UL << 63;

        /// <param name="senses">The nerve of the sensory channels; null for a body with no senses (the load stays zero).</param>
        /// <param name="senseRegion">The region whose point the emitters are sensed at.</param>
        public Perception(NerveParameters parameters, double tickSeconds, int regionCount, NerveParameters? senses = null, int senseRegion = 0, PerceptParameters? percepts = null)
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
                var pp = percepts ?? new PerceptParameters();
                pp.Validate();
                _memorySeconds = pp.MemorySeconds;
                _pId = new ulong[pp.Capacity];
                _pKind = new uint[pp.Capacity];
                _pFacts = new ulong[pp.Capacity];
                _pX = new float[pp.Capacity];
                _pY = new float[pp.Capacity];
                _pZ = new float[pp.Capacity];
                _pSalience = new double[pp.Capacity];
                _pSince = new double[pp.Capacity];
                _pSensed = new bool[pp.Capacity];
            }
            else
            {
                _senseRing = Array.Empty<double>();
                _pId = Array.Empty<ulong>();
                _pKind = Array.Empty<uint>();
                _pFacts = Array.Empty<ulong>();
                _pX = _pY = _pZ = Array.Empty<float>();
                _pSalience = _pSince = Array.Empty<double>();
                _pSensed = Array.Empty<bool>();
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

        /// <summary>The region whose point the senses are sensed at.</summary>
        public int SenseRegion => _senseRegion;

        /// <summary>The number of percept slots: the capacity, or 0 with no senses. A slot is free when its percept's id is 0.</summary>
        public int PerceptCount => _pId.Length;

        /// <summary>The percept in the slot. A thing keeps its slot while it is remembered; a free slot reads as Id 0.</summary>
        public Percept Percept(int slot) =>
            new Percept(_pId[slot], _pKind[slot], _pX[slot], _pY[slot], _pZ[slot], _pSalience[slot], _pSince[slot], _pFacts[slot]);

        /// <summary>
        /// Senses once per physics step. The spans are the caller's, one point per region. The sensory gain (default 1, the identity) multiplies the sense
        /// channels' afferents as they leave the conduction delay, so a change of gain reaches <see cref="Load"/> on the next call it is passed to; the nociceptive region
        /// afferents are never gated. A gain outside [0, 1] (NaN and infinity included) is refused,
        /// as is an emitter that sets <see cref="PhantomIdBit"/> or <see cref="InReachFact"/> and two emitters of one id with different kinds or facts.
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

                    var id = emitters[i].Id;
                    if ((id & PhantomIdBit) != 0)
                    {
                        throw new ArgumentException($"Emitter {i} sets the phantom id bit, which is reserved to Lodur.", nameof(emitters));
                    }

                    if ((emitters[i].Facts & InReachFact) != 0)
                    {
                        throw new ArgumentException($"Emitter {i} sets the in-reach fact bit, which is reserved to Lodur.", nameof(emitters));
                    }

                    if (id != 0)
                    {
                        for (var j = 0; j < i; j++)
                        {
                            if (emitters[j].Id == id && emitters[j].Kind != emitters[i].Kind)
                            {
                                throw new ArgumentException($"Emitters {j} and {i} share id {id} with different kinds.", nameof(emitters));
                            }

                            if (emitters[j].Id == id && emitters[j].Facts != emitters[i].Facts)
                            {
                                throw new ArgumentException($"Emitters {j} and {i} share id {id} with different facts.", nameof(emitters));
                            }
                        }
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
            SensePercepts(x, y, z, emitters, gain);
        }

        // A thing is sensed when it has an id, the gain is above zero and the sense point is strictly inside the emitter's radius.
        private void SensePercepts(float x, float y, float z, ReadOnlySpan<Emitter> emitters, double gain)
        {
            for (var s = 0; s < _pId.Length; s++)
            {
                if (_pId[s] == 0)
                {
                    continue;
                }

                _pSensed[s] = false;
                _pSalience[s] = 0.0;
                _pSince[s] += _dt;
            }

            if (gain > 0.0)
            {
                // Things already remembered first, so a newcomer can never evict one that is sensed this tick.
                for (var i = 0; i < emitters.Length; i++)
                {
                    var slot = Reaches(emitters[i], x, y, z) ? SlotOf(emitters[i].Id) : -1;
                    if (slot >= 0)
                    {
                        Touch(slot, emitters[i], x, y, z, gain);
                    }
                }
            }

            for (var s = 0; s < _pId.Length; s++)
            {
                if (_pId[s] != 0 && !_pSensed[s] && _pSince[s] > _memorySeconds)
                {
                    _pId[s] = 0;
                    _pKind[s] = 0;
                    _pFacts[s] = 0;
                    _pX[s] = 0f; _pY[s] = 0f; _pZ[s] = 0f;
                    _pSalience[s] = 0.0;
                    _pSince[s] = 0.0;
                }
            }

            if (gain > 0.0)
            {
                for (var i = 0; i < emitters.Length; i++)
                {
                    if (!Reaches(emitters[i], x, y, z))
                    {
                        continue;
                    }

                    var slot = SlotOf(emitters[i].Id);
                    if (slot < 0)
                    {
                        slot = Admit(emitters[i].Id, emitters[i].Kind);
                    }

                    if (slot >= 0)
                    {
                        Touch(slot, emitters[i], x, y, z, gain);
                    }
                }
            }
        }

        private static bool Reaches(in Emitter e, float x, float y, float z)
        {
            if (e.Id == 0 || !(e.Radius > 0f))
            {
                return false;
            }

            double dx = x - e.X, dy = y - e.Y, dz = z - e.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) < e.Radius;
        }

        private int SlotOf(ulong id)
        {
            for (var s = 0; s < _pId.Length; s++)
            {
                if (_pId[s] == id)
                {
                    return s;
                }
            }

            return -1;
        }

        // The lowest free slot, else the unsensed slot longest unsensed (ties lowest slot); -1 when every slot was sensed this tick.
        private int Admit(ulong id, uint kind)
        {
            var slot = -1;
            for (var s = 0; s < _pId.Length; s++)
            {
                if (_pId[s] == 0)
                {
                    slot = s;
                    break;
                }
            }

            if (slot < 0)
            {
                for (var s = 0; s < _pId.Length; s++)
                {
                    if (!_pSensed[s] && (slot < 0 || _pSince[s] > _pSince[slot]))
                    {
                        slot = s;
                    }
                }
            }

            if (slot >= 0)
            {
                _pId[slot] = id;
                _pKind[slot] = kind;
                _pFacts[slot] = 0;
                _pSensed[slot] = false;
                _pSalience[slot] = 0.0;
                _pSince[slot] = 0.0;
            }

            return slot;
        }

        // The first emitter of the tick fills the slot; a later one replaces it only by reaching him strictly harder.
        private void Touch(int slot, in Emitter e, float x, float y, float z, double gain)
        {
            var salience = e.Received(x, y, z) * gain;
            if (!_pSensed[slot] || salience > _pSalience[slot])
            {
                _pX[slot] = e.X; _pY[slot] = e.Y; _pZ[slot] = e.Z;
                _pSalience[slot] = salience;
            }

            _pKind[slot] = e.Kind;
            _pFacts[slot] = e.Facts;
            _pSensed[slot] = true;
            _pSince[slot] = 0.0;
        }
    }
}
