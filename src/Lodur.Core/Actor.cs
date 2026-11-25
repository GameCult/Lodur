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
        /// <summary>The physiology died this tick (reported once, on the tick it died).</summary>
        Died = 8,
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
        private readonly long[] _onset;
        private readonly long[] _entryTicks;
        private readonly Perception _perception;
        private readonly WithdrawalReflex[] _reflexes;
        private readonly Physiology? _physiology;
        private bool _diedReported;
        // The mean of the strengths written on the last tick, after the ceiling, 0..1: the exertion the physiology is told about.
        private double _effort;
        private DriveState _drive = DriveState.Fresh;
        private long _tick;
        private int _goal;
        private int _pending = -1;
        private ulong _target;
        private ulong _pendingTarget;
        private long _switchTick;
        private long _goalSince;
        // The entry ramp of the goal in force: its length in ticks (-1: none, as for the Start goal) and the gain and arousal it started from.
        private long _rampTicks = -1;
        private double _rampGain0;
        private double _rampArousal0;

        public Actor(
            double tickSeconds, int regionCount,
            NerveParameters nerve, WithdrawalParameters withdrawal, DriveParameters drive, Brain brain,
            NerveParameters? senses = null, int senseRegion = 0, PerceptParameters? percepts = null, Physiology? physiology = null)
        {
            brain.Validate();
            drive.Validate();
            if (physiology != null && !physiology.TryBind())
            {
                throw new ArgumentException("this physiology is already bound to an actor; one actor steps one physiology.");
            }

            _physiology = physiology;

            _dt = tickSeconds;
            _driveP = drive;
            _brain = brain;
            _goal = brain.Start;
            _drive.SensoryGain = brain.Goals[brain.Start].SensoryGain;
            if (brain.Goals[brain.Start].EntryArousal is double startArousal)
            {
                _drive.Arousal = startArousal;
            }
            // The Start goal takes over at construction: its entry effect applies like any other goal's.
            if (brain.Goals[brain.Start].EntryResolve is double startEntry)
            {
                _drive.Enter(drive, startEntry);
            }
            _latency = Math.Max(1, (long)Math.Round(brain.DeliberateLatencySeconds / tickSeconds));
            _dwell = new long[brain.Goals.Count];
            _onset = new long[brain.Goals.Count];
            _entryTicks = new long[brain.Goals.Count];
            for (var g = 0; g < _dwell.Length; g++)
            {
                _dwell[g] = (long)Math.Round(brain.Goals[g].MinDwellSeconds / tickSeconds);
                // Rounded to whole ticks once, like the dwell, so the onset is the same number of ticks whatever the tick length does to a product.
                // A quotient past the long range (+infinity included) is involuntary throughout: the cast of an out-of-range double is not defined across runtimes.
                var onsetTicks = brain.Goals[g].InvoluntarySeconds / tickSeconds;
                _onset[g] = onsetTicks >= long.MaxValue ? long.MaxValue : (long)Math.Round(onsetTicks);
                // The entry ramp is rounded once as well; EntrySeconds is finite, but its quotient can still pass the long range.
                var entryTicks = brain.Goals[g].EntrySeconds / tickSeconds;
                _entryTicks[g] = entryTicks >= long.MaxValue ? long.MaxValue : (long)Math.Round(entryTicks);
            }

            _perception = new Perception(nerve, tickSeconds, regionCount, senses, senseRegion, percepts);
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

        /// <summary>The physiology this actor steps and reads, null for an actor with none.</summary>
        public Physiology? Physiology => _physiology;

        /// <summary>Sets the drive's Attention slot; a seam for the tests until lodur-startle-attention writes it.</summary>
        internal void SetAttention(double attention)
        {
            _drive.Attention = attention;
        }

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

        /// <summary>The number of percept slots (0 with no senses); a free slot reads as Id 0.</summary>
        public int PerceptCount => _perception.PerceptCount;

        public Percept Percept(int slot) => _perception.Percept(slot);

        /// <summary>The id of the percept the committed goal targets; 0 for an untargeted goal. Only the Brain's choice, committed after the latency, writes it.</summary>
        public ulong TargetId => _target;

        /// <summary>The slot whose Id equals <see cref="TargetId"/>; default (Id 0) when the target is 0 or forgotten.</summary>
        public Percept Target
        {
            get
            {
                if (_target != 0UL)
                {
                    for (var s = 0; s < _perception.PerceptCount; s++)
                    {
                        var p = _perception.Percept(s);
                        if (p.Id == _target)
                        {
                            return p;
                        }
                    }
                }

                return default;
            }
        }

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
            if (_physiology != null)
            {
                if (strengths.Length != _physiology.JointCount)
                {
                    throw new ArgumentException("strengths must have one entry per joint of the physiology's body.");
                }

                // The physiology steps first, on the drive as the last tick left it, and its consciousness is the slot's only writer.
                if (!_physiology.Dead)
                {
                    _physiology.Step(_dt, new PhysiologyInput(_drive.Arousal, _effort));
                    _drive.Consciousness = _physiology.Consciousness;
                }

                if (_physiology.Dead)
                {
                    // Limp and permanent: the drive, goal and perception never change again; targets are left as they were.
                    strengths.Clear();
                    _tick++;
                    if (_diedReported) return events;
                    _diedReported = true;
                    return TickEvents.Died;
                }
            }

            // A flinch is the leak of a suppressed reflex; with no will nothing is suppressed, so none is reported.
            var holding = _drive.Will > 0.0;

            _perception.Sense(regionX, regionY, regionZ, sources, emitters, _drive.SensoryGain);

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
            var effort = _tick - _goalSince >= _onset[_goal] ? inForce.Effort : 0.0;
            if (_drive.Step(_driveP, _dt, afferent, strainLoad, _perception.Load, inForce.Pull, effort))
            {
                events |= TickEvents.WillBroke;
            }

            // A decision in flight commits at its tick even if the condition that chose it has lapsed; none is made while one is
            // pending. Within the goal in force's minimum dwell only a strictly higher-priority goal is decided on.
            if (_pending < 0)
            {
                var sr = _perception.SenseRegion;
                var wanted = _brain.Select(_drive, _perception, regionX[sr], regionY[sr], regionZ[sr], new Choice(_goal, _target));
                if (wanted != new Choice(_goal, _target)
                    && (wanted.Goal == _goal || _tick - _goalSince >= _dwell[_goal] || _brain.Goals[wanted.Goal].Priority > _brain.Goals[_goal].Priority))
                {
                    _pending = wanted.Goal;
                    _pendingTarget = wanted.Target;
                    _switchTick = _tick + _latency;
                }
            }

            if (_pending >= 0 && _tick == _switchTick)
            {
                var changed = _pending != _goal;
                _goal = _pending;
                _target = _pendingTarget;
                _pending = -1;
                if (changed)
                {
                    _goalSince = _tick;
                    events |= TickEvents.GoalChanged;
                    _rampTicks = _entryTicks[_goal];
                    _rampGain0 = _drive.SensoryGain;
                    _rampArousal0 = _drive.Arousal;
                    if (_brain.Goals[_goal].EntryResolve is double entry)
                    {
                        _drive.Enter(_driveP, entry);
                    }
                }
            }

            // For EntrySeconds after a takeover the Actor, not the drive, takes the gain (and the arousal, if the goal declares one) from their values at
            // takeover to the goal's targets, linearly; its last tick assigns the targets themselves (a lerp need not land on a nonzero target), and after it the drive
            // owns arousal again. Sense reads the gain at the start of the next tick, so a change of gain reaches Load one tick after the Actor sets it.
            var sinceGoal = _tick - _goalSince;
            if (sinceGoal <= _rampTicks)
            {
                var goal = _brain.Goals[_goal];
                var last = sinceGoal == _rampTicks;   // also the takeover tick of a zero-length ramp
                var k = last ? 1.0 : (double)sinceGoal / _rampTicks;
                _drive.SensoryGain = last ? goal.SensoryGain : _rampGain0 + (goal.SensoryGain - _rampGain0) * k;
                if (goal.EntryArousal is double target)
                {
                    _drive.Arousal = last ? target : _rampArousal0 + (target - _rampArousal0) * k;
                }
            }

            // Every voluntary layer is held by will, in contest with the reflex; an involuntary one (the onset of a goal) is not.
            var secondsInGoal = (_tick - _goalSince) * _dt;
            var weight = _tick - _goalSince >= _onset[_goal] ? Math.Pow(_drive.Will, _driveP.WillCurve) : 1.0;
            _brain.Goals[_goal].Behaviour.Write(weight, leak, _drive.Strain, _tick * _dt, secondsInGoal, strengths, targets);
            if (_physiology != null)
            {
                // The only scaling of strength: each muscle (plan joint m) is held to its ceiling.
                var sum = 0.0;
                for (var m = 0; m < strengths.Length; m++)
                {
                    strengths[m] *= (float)_physiology.MotorCeiling(m);
                    sum += strengths[m];
                }

                var mean = strengths.Length > 0 ? sum / strengths.Length : 0.0;
                _effort = mean < 0.0 ? 0.0 : mean > 1.0 ? 1.0 : mean;
            }

            _tick++;
            return events;
        }
    }
}
