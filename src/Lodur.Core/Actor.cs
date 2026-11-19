using System;

namespace Lodur
{
    /// <summary>What happened on one tick.</summary>
    [Flags]
    public enum TickEvents
    {
        None = 0,
        /// <summary>A reflex flinched while the will still held.</summary>
        Flinch = 1,
        /// <summary>Resolve reached zero this tick.</summary>
        WillBroke = 2,
        /// <summary>Deliberate behaviour took over, the withdrawal latency after the will broke.</summary>
        Withdrew = 4,
    }

    /// <summary>
    /// One character's nervous state and the per-tick order: perception, reflex, drive, behaviour. The caller
    /// supplies one point per body region, the game's stimulus sources, and the spans the behaviour writes its
    /// strengths and targets into; the Actor writes nothing else of the caller's and never touches physics.
    /// Everything is allocated in the constructor, and the same inputs give the same outputs.
    /// </summary>
    public sealed class Actor
    {
        private readonly double _dt;
        private readonly DriveParameters _driveP;
        private readonly long _withdrawLatency;
        private readonly Perception _perception;
        private readonly WithdrawalReflex[] _reflexes;
        private readonly HoldAgainstReflex _behaviour;
        private DriveState _drive = DriveState.Fresh;
        private long _tick;
        private long _withdrawTick = -1;
        private bool _withdrew;

        public Actor(
            double tickSeconds, int regionCount,
            NerveParameters nerve, WithdrawalParameters withdrawal, DriveParameters drive, HoldParameters hold,
            ReadOnlySpan<Quat> holdPose, ReadOnlySpan<Quat> withdrawPose)
        {
            _dt = tickSeconds;
            _driveP = drive;
            _withdrawLatency = Math.Max(1, (long)Math.Round(withdrawal.LatencySeconds / tickSeconds));
            _perception = new Perception(nerve, tickSeconds, regionCount);
            _reflexes = new WithdrawalReflex[regionCount];
            for (var r = 0; r < regionCount; r++)
            {
                _reflexes[r] = new WithdrawalReflex(withdrawal, tickSeconds);
            }

            _behaviour = new HoldAgainstReflex(hold, holdPose, withdrawPose);
        }

        public int RegionCount => _reflexes.Length;

        /// <summary>Ticks run so far.</summary>
        public long TickCount => _tick;

        /// <summary>A snapshot of the drive; nothing but <see cref="Tick"/> writes it.</summary>
        public DriveState Drive => _drive;

        /// <summary>Hold until the will breaks, Withdraw from then on.</summary>
        public Goal Goal => _drive.WillBroken ? Goal.Withdraw : Goal.Hold;

        public double Stimulus(int region) => _perception.Stimulus(region);

        public double Afferent(int region) => _perception.Afferent(region);

        /// <summary>
        /// Runs one physics step's worth of nervous system. The point spans hold one entry per region; the strengths
        /// and targets spans one entry per muscle, written for the body to follow.
        /// </summary>
        public TickEvents Tick(
            ReadOnlySpan<float> regionX, ReadOnlySpan<float> regionY, ReadOnlySpan<float> regionZ,
            ReadOnlySpan<BoxStimulus> sources, Span<float> strengths, Span<Quat> targets)
        {
            var events = TickEvents.None;
            var holding = !_drive.WillBroken;

            _perception.Sense(regionX, regionY, regionZ, sources);

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

            if (_drive.Step(_driveP, _dt, afferent, strainLoad))
            {
                events |= TickEvents.WillBroke;
                _withdrawTick = _tick + _withdrawLatency;
            }

            if (_tick == _withdrawTick)
            {
                events |= TickEvents.Withdrew;
                _withdrew = true;
            }

            _behaviour.Write(_withdrew ? Goal.Withdraw : Goal.Hold, leak, _drive.Strain, _tick * _dt, strengths, targets);
            _tick++;
            return events;
        }
    }
}
