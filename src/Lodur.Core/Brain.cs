using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>A number the drive carries, as a condition reads it.</summary>
    public enum DriveQuantity
    {
        Pain,
        Strain,
        Arousal,
        Resolve,
        /// <summary>1 while the will is broken now (it mends), else 0.</summary>
        WillBroken,
        /// <summary>How many times the will has broken: the one-way latch, for a goal that must hold after a mend.</summary>
        Breaks,
    }

    public enum ConditionOp
    {
        AtLeast,
        Below,
    }

    /// <summary>One test of the drive state: the quantity against a threshold.</summary>
    public readonly record struct Condition(DriveQuantity Quantity, ConditionOp Op, double Threshold)
    {
        public bool Holds(in DriveState drive)
        {
            var v = Quantity switch
            {
                DriveQuantity.Pain => drive.Pain,
                DriveQuantity.Strain => drive.Strain,
                DriveQuantity.Arousal => drive.Arousal,
                DriveQuantity.Resolve => drive.Resolve,
                DriveQuantity.Breaks => (double)drive.Breaks,
                _ => drive.WillBroken ? 1.0 : 0.0,
            };
            return Op == ConditionOp.AtLeast ? v >= Threshold : v < Threshold;
        }
    }

    /// <summary>One thing the consumer wants a character to be doing, and when it is eligible to be.</summary>
    public sealed record GoalSpec
    {
        /// <summary>The consumer's name for it; Lodur never reads it.</summary>
        public string Id { get; init; } = "";

        /// <summary>The eligible goal of the highest priority is wanted.</summary>
        public double Priority { get; init; }

        /// <summary>How hard the goal pulls, in [0, 1], default 0 (no pull): resolve drain is scaled by (1 - pull) while it is active. It is not a second resolve bar.</summary>
        public double Pull { get; init; }

        /// <summary>
        /// Commitment, soft: until this goal has been active this long (rounded to whole ticks like the latency, counted
        /// from the tick it took over), a goal that does not strictly outrank it cannot be decided on; a strictly
        /// higher-priority goal preempts at once, after the deliberate latency. 0 (the default) never holds a goal. It keeps
        /// a condition hovering at its threshold from making the Brain chatter: the return trip from an entered goal is
        /// gated by that goal's own dwell. The dwell holds the decision to leave; a decision already in flight commits.
        /// </summary>
        public double MinDwellSeconds { get; init; }

        /// <summary>
        /// The resolve the character has the tick this goal takes over (for the Start goal, at construction), in [0, 1]; null leaves resolve as it is. Rage is what
        /// the will breaking leads to, and it resets the will (lodur:ruling:rage-involuntary-onset-then-voluntary).
        /// </summary>
        public double? EntryResolve { get; init; }

        /// <summary>Resolve drained per second to sustain this goal, finite and not negative, default 0; it is not paid during <see cref="InvoluntarySeconds"/> and is scaled by (1 - Pull) like the rest of the drain.</summary>
        public double Effort { get; init; }

        /// <summary>
        /// The gain on the sense channels (not the nociceptive region afferents) while this goal is in force, in [0, 1], default 1; 0 is deaf and blind to the
        /// world's stimuli, which keep sounding (lodur:ruling:stress-bar-shows-arousal-calm-silences). Applied like <see cref="EntryResolve"/>: at the tick the goal
        /// takes over, and at construction for the Start goal. The sense reads the gain at the start of each tick, so a change reaches
        /// <see cref="Perception.Load"/> one tick after the Actor sets it.
        /// </summary>
        public double SensoryGain { get; init; } = 1.0;

        /// <summary>
        /// The arousal this goal brings, in [0, 1]; null leaves arousal to the drive. Calm drops it so the stress bar drains
        /// (lodur:ruling:stress-bar-shows-arousal-calm-silences). Reached over <see cref="EntrySeconds"/>, then the drive owns arousal again.
        /// </summary>
        public double? EntryArousal { get; init; }

        /// <summary>
        /// How long after it takes over the goal's <see cref="EntryArousal"/> and <see cref="SensoryGain"/> are reached, finite and not negative, default 0 (at the
        /// takeover tick): a linear ramp from their values at takeover, rounded to whole ticks once like the onset. The aftershock tail
        /// of lodur:ruling:flinch-tail-after-stimulus. It does not ramp <see cref="EntryResolve"/>, which stays a snap. For the Start goal the targets apply at construction.
        /// </summary>
        public double EntrySeconds { get; init; }

        /// <summary>
        /// How long after it takes over the goal runs without will (rounded to whole ticks once, like the dwell), not negative, default 0 (voluntary from the first tick);
        /// +infinity is involuntary throughout. While involuntary the behaviour writes at full weight and pays no Effort;
        /// after it the goal is voluntary: sustained by <see cref="DriveState.Will"/> like every other voluntary layer.
        /// </summary>
        public double InvoluntarySeconds { get; init; }

        private Condition[] _when = Array.Empty<Condition>();
        private IReadOnlyList<Condition> _whenView = Array.AsReadOnly(Array.Empty<Condition>());

        /// <summary>All must hold for the goal to be eligible; none means always eligible. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Condition> When
        {
            get => _whenView;
            init
            {
                _when = new Condition[value.Count];
                for (var c = 0; c < _when.Length; c++)
                {
                    _when[c] = value[c];
                }

                _whenView = Array.AsReadOnly(_when);
            }
        }

        internal bool Eligible(in DriveState drive)
        {
            for (var c = 0; c < _when.Length; c++)
            {
                if (!_when[c].Holds(drive))
                {
                    return false;
                }
            }

            return true;
        }

        public PoseBehaviour Behaviour { get; init; } = new PoseBehaviour();
    }

    /// <summary>
    /// Layer 4: the consumer's goals, selected by conditions over the drive state. Lodur has no flip rule of its
    /// own; every rule is this data. Data only: the Actor commits the choice, after the deliberate latency.
    /// </summary>
    public sealed record Brain
    {
        private IReadOnlyList<GoalSpec> _goals = Array.Empty<GoalSpec>();

        /// <summary>The goals, copied when set: later changes to the caller's list or arrays do not reach a Brain, and the getter is a read-only view.</summary>
        public IReadOnlyList<GoalSpec> Goals
        {
            get => _goals;
            init
            {
                var copy = new GoalSpec[value.Count];
                for (var i = 0; i < copy.Length; i++)
                {
                    copy[i] = value[i];
                }

                _goals = Array.AsReadOnly(copy);
            }
        }

        /// <summary>The index of the goal a character begins in.</summary>
        public int Start { get; init; }

        /// <summary>How long a chosen switch takes to commit; a decision in flight commits even if its condition lapses.</summary>
        public double DeliberateLatencySeconds { get; init; } = 0.1;

        /// <summary>The goal wanted now: the eligible goal of highest priority, ties to the current goal then the lowest index; the current goal if none is eligible.</summary>
        public int Select(in DriveState drive, int current)
        {
            var best = -1;
            var bestPriority = 0.0;
            for (var i = 0; i < Goals.Count; i++)
            {
                var goal = Goals[i];
                if (!goal.Eligible(drive))
                {
                    continue;
                }

                if (best < 0 || goal.Priority > bestPriority || (goal.Priority == bestPriority && i == current))
                {
                    best = i;
                    bestPriority = goal.Priority;
                }
            }

            return best < 0 ? current : best;
        }

        internal void Validate()
        {
            if (Goals.Count == 0)
            {
                throw new ArgumentException("A brain needs at least one goal.");
            }

            if (Start < 0 || Start >= Goals.Count)
            {
                throw new ArgumentException("Start is not a goal index.");
            }

            if (!(DeliberateLatencySeconds >= 0.0 && double.IsFinite(DeliberateLatencySeconds)))
            {
                throw new ArgumentException("DeliberateLatencySeconds must be finite and not negative.");
            }

            var muscles = -1;
            foreach (var goal in Goals)
            {
                if (!double.IsFinite(goal.Priority) || !(goal.Pull >= 0.0 && goal.Pull <= 1.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: priority must be finite and pull in [0, 1].");
                }

                if (!(goal.MinDwellSeconds >= 0.0 && double.IsFinite(goal.MinDwellSeconds)))
                {
                    throw new ArgumentException($"Goal {goal.Id}: MinDwellSeconds must be finite (not infinite) and not negative.");
                }

                if (goal.EntryResolve is double entry && !(entry >= 0.0 && entry <= 1.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: EntryResolve must be in [0, 1].");
                }

                if (!(goal.SensoryGain >= 0.0 && goal.SensoryGain <= 1.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: SensoryGain must be in [0, 1].");
                }

                if (goal.EntryArousal is double entryArousal && !(entryArousal >= 0.0 && entryArousal <= 1.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: EntryArousal must be in [0, 1].");
                }

                if (!(goal.EntrySeconds >= 0.0 && double.IsFinite(goal.EntrySeconds)))
                {
                    throw new ArgumentException($"Goal {goal.Id}: EntrySeconds must be finite and not negative.");
                }

                if (!(goal.Effort >= 0.0 && double.IsFinite(goal.Effort)))
                {
                    throw new ArgumentException($"Goal {goal.Id}: Effort must be finite and not negative.");
                }

                if (!(goal.InvoluntarySeconds >= 0.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: InvoluntarySeconds must not be negative or NaN (infinity is allowed).");
                }

                foreach (var c in goal.When)
                {
                    if (!double.IsFinite(c.Threshold))
                    {
                        throw new ArgumentException($"Goal {goal.Id}: a condition threshold is not finite.");
                    }
                }

                goal.Behaviour.Validate(goal.Id);
                if (muscles >= 0 && goal.Behaviour.MuscleCount != muscles)
                {
                    throw new ArgumentException($"Goal {goal.Id}: every behaviour needs the same muscle count.");
                }

                muscles = goal.Behaviour.MuscleCount;
            }
        }
    }
}
