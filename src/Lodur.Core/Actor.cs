using System;

namespace Lodur
{
    /// <summary>What happened on one tick.</summary>
    [Flags]
    public enum TickEvents
    {
        None = 0,
        /// <summary>A reflex leaked past the suppression the will still held. With no will nothing is suppressed, so there is no flinch.</summary>
        Flinch = 1,
        /// <summary>Resolve reached zero this tick: the will broke (again, if it had mended).</summary>
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
        private readonly long[] _dwell;
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
            if (!(drive.WillMendsAt > 0.0 && drive.WillMendsAt <= 1.0))
            {
                throw new ArgumentException("WillMendsAt must be in (0, 1].");
            }

            if (!(drive.WillCurve > 0.0 && double.IsFinite(drive.WillCurve)))
            {
                throw new ArgumentException("WillCurve must be finite and above 0.");
            }

            _dt = tickSeconds;
            _driveP = drive;
            _brain = brain;
            _goal = brain.Start;
            _latency = Math.Max(1, (long)Math.Round(brain.DeliberateLatencySeconds / tickSeconds));
            _dwell = new long[brain.Goals.Count];
            for (var g = 0; g < _dwell.Length; g++)
            {
                _dwell[g] = (long)Math.Round(brain.Goals[g].MinDwellSeconds / tickSeconds);
            }

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
            // A flinch is the leak of a suppressed reflex; with no will nothing is suppressed, so none is reported.
            var holding = _drive.Will > 0.0;

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

            // Effort is paid only once the goal is voluntary: the onset (InvoluntarySeconds) runs without will, and without cost.
            var inForce = _brain.Goals[_goal];
            var effort = (_tick - _goalSince) * _dt >= inForce.InvoluntarySeconds ? inForce.Effort : 0.0;
            if (_drive.Step(_driveP, _dt, afferent, strainLoad, _perception.Load, inForce.Pull, effort))
            {
                events |= TickEvents.WillBroke;
            }

            // A decision in flight commits at its tick even if the condition that chose it has lapsed; none is made while one is
            // pending. Within the goal in force's minimum dwell only a strictly higher-priority goal is decided on.
            if (_pending < 0)
            {
                var wanted = _brain.Select(_drive, _goal);
                if (wanted != _goal && (_tick - _goalSince >= _dwell[_goal] || _brain.Goals[wanted].Priority > _brain.Goals[_goal].Priority))
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
                if (_brain.Goals[_goal].EntryResolve is double entry)
                {
                    _drive.Enter(_driveP, entry);
                }
            }

            // Every voluntary layer is held by will, in contest with the reflex; an involuntary one (the onset of a goal) is not.
            var secondsInGoal = (_tick - _goalSince) * _dt;
            var weight = secondsInGoal >= _brain.Goals[_goal].InvoluntarySeconds ? Math.Pow(_drive.Will, _driveP.WillCurve) : 1.0;
            _brain.Goals[_goal].Behaviour.Write(weight, leak, _drive.Strain, _tick * _dt, secondsInGoal, strengths, targets);
            _tick++;
            return events;
        }
    }
}
