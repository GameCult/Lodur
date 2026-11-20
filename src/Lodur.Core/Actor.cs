using System;

namespace Lodur
{
    /// <summary>What happened on one tick.</summary>
    [Flags]
    public enum TickEvents
    {
        None = 0,
        /// <summary>A reflex leaked past the suppression the will still held. After the will breaks nothing is suppressed, so there is no flinch.</summary>
        Flinch = 1,
        /// <summary>Resolve reached zero this tick.</summary>
        WillBroke = 2,
        /// <summary>The Brain's chosen goal took over, its deliberate latency after the choice.</summary>
        GoalChanged = 4,
    }

    /// <summary>
    /// One character's nervous state and the per-tick order: perception, reflex, drive, the Brain's goal, behaviour. The caller
    /// supplies one point per body region, the game's stimulus sources, and the spans the behaviour writes its
    /// strengths and targets into; the Actor writes nothing else of the caller's and never touches physics.
    /// Everything is allocated in the constructor, and the same inputs give the same outputs.
    /// </summary>
    public sealed class Actor
    {
        private readonly double _dt;
        private readonly DriveParameters _driveP;
        private readonly Brain _brain;
        private readonly long _latency;
        private readonly Perception _perception;
        private readonly WithdrawalReflex[] _reflexes;
        private DriveState _drive = DriveState.Fresh;
        private long _tick;
        private int _goal;
        private int _pending = -1;
        private long _switchTick;
        private long _goalSince;

        public Actor(
            double tickSeconds, int regionCount,
            NerveParameters nerve, WithdrawalParameters withdrawal, DriveParameters drive, Brain brain,
            NerveParameters? senses = null, int senseRegion = 0)
        {
            brain.Validate();
            _dt = tickSeconds;
            _driveP = drive;
            _brain = brain;
            _goal = brain.Start;
            _latency = Math.Max(1, (long)Math.Round(brain.DeliberateLatencySeconds / tickSeconds));
            _perception = new Perception(nerve, tickSeconds, regionCount, senses, senseRegion);
            _reflexes = new WithdrawalReflex[regionCount];
            for (var r = 0; r < regionCount; r++)
            {
                _reflexes[r] = new WithdrawalReflex(withdrawal, tickSeconds);
            }
        }

        public int RegionCount => _reflexes.Length;

        /// <summary>Ticks run so far.</summary>
        public long TickCount => _tick;

        /// <summary>A snapshot of the drive; nothing but <see cref="Tick"/> writes it.</summary>
        public DriveState Drive => _drive;

        public Brain Brain => _brain;

        /// <summary>The index in <see cref="Brain"/>'s goals of the goal in force; it changes only on the tick that reports <see cref="TickEvents.GoalChanged"/>.</summary>
        public int GoalIndex => _goal;

        /// <summary>How long the goal in force will have been in force at the next tick; 0 on the tick it took over.</summary>
        public double SecondsInGoal => (_tick - _goalSince) * _dt;

        public double Stimulus(int region) => _perception.Stimulus(region);

        public double Afferent(int region) => _perception.Afferent(region);

        /// <summary>The sensory load this tick: the channels' afferents combined, 0..1.</summary>
        public double Load => _perception.Load;

        public double ChannelStimulus(Channel channel) => _perception.ChannelStimulus(channel);

        public double ChannelAfferent(Channel channel) => _perception.ChannelAfferent(channel);

        /// <summary>
        /// Runs one physics step's worth of nervous system. The point spans hold one entry per region; the strengths
        /// and targets spans one entry per muscle, written for the body to follow. The emitters are the game's point
        /// sources, sensed at the sense region's point.
        /// </summary>
        public TickEvents Tick(
            ReadOnlySpan<float> regionX, ReadOnlySpan<float> regionY, ReadOnlySpan<float> regionZ,
            ReadOnlySpan<BoxStimulus> sources, Span<float> strengths, Span<Quat> targets,
            ReadOnlySpan<Emitter> emitters = default)
        {
            var events = TickEvents.None;
            // A flinch is the leak of a suppressed reflex; once the will has broken nothing is suppressed, so none is reported.
            var holding = !_drive.WillBroken;

            _perception.Sense(regionX, regionY, regionZ, sources, emitters);

            double afferent = 0.0, strainLoad = 0.0, leak = 0.0;
            for (var r = 0; r < _reflexes.Length; r++)
            {
                var a = _perception.Afferent(r);
                var flinch = _reflexes[r].Step(a, _drive);
                afferent += a;
                strainLoad += _reflexes[r].StrainLoad;
                leak += _reflexes[r].Leak;
                if (flinch && holding)
                {
                    events |= TickEvents.Flinch;
                }
            }

            if (_drive.Step(_driveP, _dt, afferent, strainLoad, _perception.Load, _brain.Goals[_goal].Pull))
            {
                events |= TickEvents.WillBroke;
            }

            // A decision in flight commits at its tick even if the condition that chose it has lapsed; none is made while one is pending.
            if (_pending < 0)
            {
                var wanted = _brain.Select(_drive, _goal);
                if (wanted != _goal)
                {
                    _pending = wanted;
                    _switchTick = _tick + _latency;
                }
            }

            if (_pending >= 0 && _tick == _switchTick)
            {
                _goal = _pending;
                _pending = -1;
                _goalSince = _tick;
                events |= TickEvents.GoalChanged;
            }

            _brain.Goals[_goal].Behaviour.Write(leak, _drive.Strain, _tick * _dt, (_tick - _goalSince) * _dt, strengths, targets);
            _tick++;
            return events;
        }
    }
}
