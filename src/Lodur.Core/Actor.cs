using System;
using System.Collections.Generic;

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
        /// <summary>An involuntary breath ended a held breath: the breath reflex leaked past the will, or the will gave out while the breath was wanted held.</summary>
        Gasp = 16,
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
        // The breath-hold's contest with air hunger (null without a physiology), the gasp's length in ticks, the tick of the last gasp, and whether the breath is held for the next step.
        private readonly WithdrawalReflex? _breath;
        private readonly long _gaspTicks;
        private long _lastGasp = long.MinValue / 2;
        private bool _held;
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
        // The orient layers' solvers (null when no goal has a layer) and the arm the Nearer mode holds, set when a (goal, target) pair commits.
        private readonly OrientRig? _rig;
        private readonly bool _layered;
        private Side _arm = Side.Right;
        // The want gate's thinking space (null when no goal has a want) and the plan length of the committed pair, derived each tick.
        private readonly Deliberation? _deliberation;
        private int _planLength;

        public Actor(
            double tickSeconds, int regionCount,
            NerveParameters nerve, WithdrawalParameters withdrawal, DriveParameters drive, Brain brain,
            NerveParameters? senses = null, int senseRegion = 0, PerceptParameters? percepts = null, Physiology? physiology = null,
            WithdrawalParameters? breath = null, BodyPlan? body = null, IReadOnlyList<Verb>? verbs = null)
        {
            brain.Validate();
            drive.Validate();
            Body = body;
            foreach (var goal in brain.Goals)
            {
                if (!goal.Behaviour.HasLayers)
                {
                    continue;
                }

                if (body == null)
                {
                    throw new ArgumentException($"Goal {goal.Id}: a behaviour with orient layers needs the Actor's BodyPlan.");
                }

                if (goal.Behaviour.MuscleCount != body.Joints.Count)
                {
                    throw new ArgumentException($"Goal {goal.Id}: a behaviour with orient layers needs one muscle per joint of the body ({body.Joints.Count}), not {goal.Behaviour.MuscleCount}.");
                }

                _layered = true;
            }

            if (_layered)
            {
                _rig = new OrientRig(body!, brain, tickSeconds);
            }

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

            if (physiology != null)
            {
                var breathP = breath ?? new WithdrawalParameters();
                _breath = new WithdrawalReflex(breathP, tickSeconds);
                _gaspTicks = (long)Math.Round(breathP.FlinchRefractorySeconds / tickSeconds);
            }

            _perception = new Perception(nerve, tickSeconds, regionCount, senses, senseRegion, percepts);
            if (brain.HasWants)
            {
                if (_perception.PerceptCount == 0)
                {
                    throw new ArgumentException("A Brain with a want needs percepts: the planner reads the percepts' facts.");
                }

                _deliberation = new Deliberation(brain, verbs ?? Array.Empty<Verb>(), _perception.PerceptCount);
            }

            _reflexes = new WithdrawalReflex[regionCount];
            for (var r = 0; r < regionCount; r++)
            {
                _reflexes[r] = new WithdrawalReflex(withdrawal, tickSeconds);
            }
        }

        public int RegionCount => _reflexes.Length;

        /// <summary>The body the orient layers solve against; null when the actor was built without one.</summary>
        public BodyPlan? Body { get; }

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

        /// <summary>
        /// Forgets the arms' history: each arm's next solve is the stateless rule, as on the first tick. For a caller that moves the body
        /// discontinuously (a teleport, a respawn) so the arms neither carry a swivel from the old place nor slew from its targets. Nothing for an actor without arms.
        /// </summary>
        public void ResetArms() => _rig?.ResetArms();

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

        /// <summary>
        /// The length of the plan to the committed pair's want, derived each tick from the percepts (never kept across ticks): n steps, -1 when he goes at it direct (no plan within
        /// <see cref="Brain.PlanDepth"/>), 0 when the goal has no want, the want holds, or its target is no longer in memory.
        /// </summary>
        public int PlanLength => _planLength;

        /// <summary>Step <paramref name="i"/> of the committed pair's plan, for i below <see cref="PlanLength"/>: a verb of the Actor's list and the percept ids it is grounded on.</summary>
        public PlanStep PlanStep(int i)
        {
            if ((uint)i >= (uint)Math.Max(_planLength, 0))
            {
                throw new ArgumentOutOfRangeException(nameof(i), "There is no such step in the plan.");
            }

            return _deliberation!.Step(i);
        }

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
        /// sources, sensed at the sense region's point. The airway is what the caller finds at the body's airway openings; nothing by default.
        /// The parts are the body's part poses (one per plan part), read when the goal in force has orient layers and ignored otherwise.
        /// <paramref name="selfFacts"/> are the actor's own facts for the planner (the game's; bit 63 is refused), read only by a Brain with wants.
        /// </summary>
        public TickEvents Tick(
            ReadOnlySpan<float> regionX, ReadOnlySpan<float> regionY, ReadOnlySpan<float> regionZ,
            ReadOnlySpan<BoxStimulus> sources, Span<float> strengths, Span<Quat> targets,
            ReadOnlySpan<Emitter> emitters = default, in AirwaySample airway = default, ReadOnlySpan<PartPose> parts = default, ulong selfFacts = 0UL)
        {
            if ((selfFacts & Perception.InReachFact) != 0UL)
            {
                throw new ArgumentException("Bit 63 is Lodur's reach; the actor's own facts have none.", nameof(selfFacts));
            }

            // The goal that will write this tick: a decision due now commits before the behaviour runs, and one made now cannot (the latency is a tick at least).
            if (_layered && _brain.Goals[_pending >= 0 && _tick == _switchTick ? _pending : _goal].Behaviour.HasLayers && parts.Length != Body!.Parts.Count)
            {
                throw new ArgumentException($"parts needs one pose per part of the body ({Body.Parts.Count}).");
            }

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
                    _physiology.Step(_dt, new PhysiologyInput(_drive.Arousal, _effort, _held, airway));
                    _drive.Consciousness = _physiology.Consciousness;
                    _drive.AirHunger = _physiology.BreathUrge;
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

            // The breath is held on will while liquid is at the airway, in contest with air hunger as a reflex is with pain: it ends in a gasp when the hunger leaks past the will, or
            // when the will gives out. The breath's leak is not the limbs' leak; its strain joins the drive's.
            if (_physiology != null)
            {
                var wanted = _physiology.LiquidAtAirway;
                var willing = wanted && _drive.Will > 0.0;
                if (willing)
                {
                    if (_breath!.Step(_drive.AirHunger, _drive))
                    {
                        events |= TickEvents.Gasp;
                        _lastGasp = _tick;
                    }

                    strainLoad += _breath.StrainLoad;
                }
                else if (wanted && _held)
                {
                    events |= TickEvents.Gasp;
                    _lastGasp = _tick;
                }

                _held = willing && _tick - _lastGasp >= _gaspTicks;
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
                var now = new Choice(_goal, _target);
                if (_deliberation != null)
                {
                    _deliberation.SelfFacts = selfFacts;
                }

                var wanted = _brain.Select(_drive, _perception, regionX[sr], regionY[sr], regionZ[sr], now, _deliberation);
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
                _rig?.Commit();
                if (_layered && _brain.Goals[_goal].Behaviour.Arms is { Side: ArmSide.Nearer } && _target != 0UL)
                {
                    var committed = Target;
                    if (committed.Id != 0UL)
                    {
                        _arm = _rig!.NearerArm(parts, committed);
                    }
                }

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

            // The committed pair's plan, derived afresh from the percepts of this tick with the whole think budget.
            _planLength = 0;
            if (_deliberation != null && _brain.Goals[_goal].Want is { } committedWant)
            {
                var sr = _perception.SenseRegion;
                _deliberation.SelfFacts = selfFacts;
                _planLength = _deliberation.Derive(_perception, regionX[sr], regionY[sr], regionZ[sr], committedWant, _target);
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
            var behaviour = _brain.Goals[_goal].Behaviour;
            if (_rig != null)
            {
                behaviour.Write(weight, leak, _drive.Strain, _tick * _dt, secondsInGoal, strengths, targets, new OrientInput(_rig, parts, Target, _arm));
            }
            else
            {
                behaviour.Write(weight, leak, _drive.Strain, _tick * _dt, secondsInGoal, strengths, targets);
            }

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
