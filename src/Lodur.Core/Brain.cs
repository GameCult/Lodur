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
        /// <summary>Air hunger, 0..1: the physiology's urge to breathe, carried by the Actor.</summary>
        AirHunger,
    }

    public enum ConditionOp
    {
        AtLeast,
        Below,
    }

    internal static class DriveReads
    {
        internal static double Read(in DriveState drive, DriveQuantity quantity) => quantity switch
        {
            DriveQuantity.Pain => drive.Pain,
            DriveQuantity.Strain => drive.Strain,
            DriveQuantity.Arousal => drive.Arousal,
            DriveQuantity.Resolve => drive.Resolve,
            DriveQuantity.Breaks => (double)drive.Breaks,
            DriveQuantity.AirHunger => drive.AirHunger,
            _ => drive.WillBroken ? 1.0 : 0.0,
        };
    }

    /// <summary>
    /// What a consideration reads: a percept's distance from the observer, its salience, the seconds since it was sensed, a drive quantity, or the length of
    /// the plan to a want goal's pair.
    /// </summary>
    public enum ConsiderationInput
    {
        Distance,
        Salience,
        SecondsSinceSensed,
        Drive,
        /// <summary>
        /// Only on a goal with a <see cref="GoalSpec.Want"/>: x is the pair's plan length (1..<see cref="Brain.PlanDepth"/>) for a planned pair and
        /// <see cref="Brain.PlanDepth"/> + 1 for a direct try. Read by the gate in Brain.Select, never by the raw score.
        /// </summary>
        PlanSteps,
    }

    /// <summary>
    /// One response curve over an input, in [0, 1]: t = clamp01((x - X0) / (X1 - X0)), y = Y0 + (Y1 - Y0) * t^Exponent, clamped. A goal's pair
    /// score is its Priority times every consideration's response. Drive reads <see cref="Quantity"/> off the drive state.
    /// </summary>
    public readonly record struct Consideration(ConsiderationInput Input, double X0, double X1, double Y0, double Y1, double Exponent = 1.0, DriveQuantity Quantity = default)
    {
        public double Response(double x)
        {
            var t = Math.Clamp((x - X0) / (X1 - X0), 0.0, 1.0);
            return Math.Clamp(Y0 + (Y1 - Y0) * Math.Pow(t, Exponent), 0.0, 1.0);
        }
    }

    /// <summary>The game's kinds a targeted goal accepts; Lodur never interprets them.</summary>
    public sealed record TargetFilter
    {
        private IReadOnlyList<uint> _kinds = Array.AsReadOnly(Array.Empty<uint>());

        /// <summary>Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<uint> Kinds
        {
            get => _kinds;
            init
            {
                var copy = new uint[value.Count];
                for (var i = 0; i < copy.Length; i++)
                {
                    copy[i] = value[i];
                }

                _kinds = Array.AsReadOnly(copy);
            }
        }

        internal bool Accepts(uint kind)
        {
            for (var i = 0; i < _kinds.Count; i++)
            {
                if (_kinds[i] == kind)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The Brain's choice: a goal index and the id of the percept it targets (0: untargeted).</summary>
    public readonly record struct Choice(int Goal, ulong Target);

    /// <summary>One test of the drive state: the quantity against a threshold.</summary>
    public readonly record struct Condition(DriveQuantity Quantity, ConditionOp Op, double Threshold)
    {
        public bool Holds(in DriveState drive)
        {
            var v = DriveReads.Read(drive, Quantity);
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

        /// <summary>The kind of thing this goal targets; null is an untargeted goal. Whether a target exists is decided in <see cref="Brain.Select"/>, not by Eligible.</summary>
        public TargetFilter? Target { get; init; }

        /// <summary>
        /// A fact the goal wants to make true, about himself (<see cref="FactSubject.Self"/>, never bit 63) or about the goal's target (<see cref="FactSubject.This"/>, which needs a
        /// <see cref="Target"/>); null is no want. A want pair whose want already holds is not offered; one that does not is planned to <see cref="Brain.PlanDepth"/> steps and scored
        /// by the plan's length through <see cref="ConsiderationInput.PlanSteps"/>, or tried direct at <see cref="Brain.DirectDiscount"/> when no plan is found. A want goal's Priority
        /// must not be negative. Only an Actor built with verbs selects a Brain that has wants.
        /// </summary>
        public Fact? Want { get; init; }

        private Consideration[] _considerations = Array.Empty<Consideration>();
        private IReadOnlyList<Consideration> _considerationsView = Array.AsReadOnly(Array.Empty<Consideration>());

        /// <summary>Multiplied with Priority to score each (goal, percept) pair. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Consideration> Considerations
        {
            get => _considerationsView;
            init
            {
                _considerations = new Consideration[value.Count];
                for (var c = 0; c < _considerations.Length; c++)
                {
                    _considerations[c] = value[c];
                }

                _considerationsView = Array.AsReadOnly(_considerations);
            }
        }

        /// <summary>Priority times every consideration but <see cref="ConsiderationInput.PlanSteps"/>: the raw score, before the plan's length is known.</summary>
        internal double Score(in DriveState drive, in Percept percept, float ox, float oy, float oz)
        {
            var score = Priority;
            for (var c = 0; c < _considerations.Length; c++)
            {
                var k = _considerations[c];
                double x;
                switch (k.Input)
                {
                    case ConsiderationInput.PlanSteps:
                        continue;
                    case ConsiderationInput.Drive:
                        x = DriveReads.Read(drive, k.Quantity);
                        break;
                    case ConsiderationInput.Distance:
                    {
                        double dx = percept.X - ox, dy = percept.Y - oy, dz = percept.Z - oz;
                        x = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        break;
                    }
                    case ConsiderationInput.Salience:
                        x = percept.Salience;
                        break;
                    default:
                        x = percept.SecondsSinceSensed;
                        break;
                }

                score *= k.Response(x);
            }

            return score;
        }

        /// <summary>The product of the <see cref="ConsiderationInput.PlanSteps"/> responses at plan length <paramref name="x"/>; 1 with none.</summary>
        internal double PlanFactor(int x)
        {
            var factor = 1.0;
            for (var c = 0; c < _considerations.Length; c++)
            {
                if (_considerations[c].Input == ConsiderationInput.PlanSteps)
                {
                    factor *= _considerations[c].Response(x);
                }
            }

            return factor;
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

        /// <summary>How much the current (goal, target) pair's score is multiplied by, finite and at least 1; 1 is no commitment.</summary>
        public double TargetCommitment { get; init; } = 1.0;

        /// <summary>The planner's depth, the intelligence stat's base: the most steps a want goal's plan may have, 0..8, default 0 (no plans; every want pair is tried direct).</summary>
        public int PlanDepth { get; init; }

        /// <summary>The factor on a pair's score when its goal's want has no plan within <see cref="PlanDepth"/> (or the think budget ran out): he goes at the target directly, in [0, 1], default 0.5 (a placeholder).</summary>
        public double DirectDiscount { get; init; } = 0.5;

        /// <summary>The most groundings one Select may regress across all its plans, at least 1, default 4096: one budget per decision, spent by deepening across the want pairs, short plans first (lodur:question:bds-think-budget-per-decision).</summary>
        public int ThinkBudget { get; init; } = Planner.NodeBudget;

        /// <summary>How close a thing must be to be in reach, in meters, finite and above 0, default 0.6 (an arm's length, 0.575 m, as a placeholder).</summary>
        public double ReachMeters { get; init; } = 0.6;

        /// <summary>True when any goal has a want: such a Brain is selected through an Actor's Deliberation, not the public Select.</summary>
        internal bool HasWants
        {
            get
            {
                for (var i = 0; i < Goals.Count; i++)
                {
                    if (Goals[i].Want is not null)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// The pair wanted now. Each eligible untargeted goal is one pair (goal, 0) scored Priority times its Drive considerations; each eligible
        /// targeted goal is one pair per percept slot with a non-zero Id and a Kind it accepts, scored Priority times every consideration. The pair
        /// equal to <paramref name="current"/> is multiplied by <see cref="TargetCommitment"/>. The highest score wins; ties go to the current pair,
        /// then the lowest goal index, then the lowest slot. With no pair at all the goal in force stays and lets go of its target: (current.Goal, 0),
        /// so a forgotten target is not held (the Actor commits it a latency later and the orient layers rest). A percept Id is only compared, never decoded.
        /// Throws for a Brain with a want: the planner's gate needs an Actor's verbs, so the Actor selects it.
        /// </summary>
        public Choice Select(in DriveState drive, Perception perception, float observerX, float observerY, float observerZ, Choice current)
        {
            if (HasWants)
            {
                throw new InvalidOperationException("A Brain with a want is selected by its Actor, which holds the verbs the planner needs.");
            }

            return Select(drive, perception, observerX, observerY, observerZ, current, null);
        }

        /// <summary>
        /// <see cref="Select(in DriveState, Perception, float, float, float, Choice)"/> with the want gate. Pass 1 scores every pair raw (commitment applied) and offers
        /// the untargeted-by-want pairs; a want pair whose want holds is not offered, the rest wait in the Deliberation. Pass 2 deepens across the waiting pairs: round k takes them by
        /// bound (raw times the best factor any plan longer than k - 1 steps, or going direct, could earn), asks each for a plan of exactly k steps with what is left of <see cref="ThinkBudget"/>,
        /// and offers raw times the plan-length factor for one found; a pair with none of k steps waits for round k + 1, and one with no plan within PlanDepth (or the budget gone) is offered
        /// at raw times the factor at PlanDepth + 1 times <see cref="DirectDiscount"/>. A round ends when no remaining bound can beat or tie-win the best, so short plans are found
        /// first and one pair's dead end cannot starve the rest.
        /// </summary>
        internal Choice Select(in DriveState drive, Perception perception, float ox, float oy, float oz, Choice current, Deliberation? d)
        {
            var best = default(Pick);
            var slots = perception.PerceptCount;
            d?.Begin(perception, ox, oy, oz);
            for (var i = 0; i < Goals.Count; i++)
            {
                var goal = Goals[i];
                if (!goal.Eligible(drive))
                {
                    continue;
                }

                if (goal.Target is null)
                {
                    Consider(i, goal, -1, 0UL, goal.Score(drive, default, ox, oy, oz), current, d, ref best);
                    continue;
                }

                for (var s = 0; s < slots; s++)
                {
                    var percept = perception.Percept(s);
                    if (percept.Id == 0UL || !goal.Target.Accepts(percept.Kind))
                    {
                        continue;
                    }

                    Consider(i, goal, s, percept.Id, goal.Score(drive, percept, ox, oy, oz), current, d, ref best);
                }
            }

            if (d is not null)
            {
                for (var k = PlanDepth == 0 ? 0 : 1; ; k++)
                {
                    while (d.TryPop(out var pair))
                    {
                        if (best.Found && pair.Bound < best.Score)
                        {
                            d.ClearRound();
                            break;
                        }

                        var goal = Goals[pair.Goal];
                        var length = d.PlanAt(pair.Goal, goal.Want!.Value, pair.Slot, k);
                        if (length == 0)
                        {
                            pair.Bound = pair.Raw * d.BoundFactor(pair.Goal, k);
                            d.Defer(pair);
                            continue;
                        }

                        var factor = length > 0 ? goal.PlanFactor(length) : goal.PlanFactor(PlanDepth + 1) * DirectDiscount;
                        Offer(pair.Goal, pair.Slot, pair.Target, pair.Raw * factor, pair.Current, ref best);
                    }

                    if (!d.NextRound())
                    {
                        break;
                    }
                }
            }

            return best.Found ? Pair(best.Goal, best.Target) : Pair(current.Goal, 0UL);
        }

        private static Choice Pair(int goal, ulong target) => new Choice(goal, target);

        // One scored pair: offered now, or (a want pair) held back for the planner's gate. The commitment multiplies the raw score here, so pass 2 orders by the committed raw.
        private void Consider(int goal, GoalSpec spec, int slot, ulong target, double raw, Choice current, Deliberation? d, ref Pick best)
        {
            var isCurrent = goal == current.Goal && target == current.Target;
            if (isCurrent)
            {
                raw *= TargetCommitment;
            }

            if (spec.Want is not { } want)
            {
                Offer(goal, slot, target, raw, isCurrent, ref best);
            }
            else if (!d!.Holds(goal, want, slot))
            {
                d.Add(goal, slot, target, raw, isCurrent);
            }
        }

        // Ties resolve by key (current, then lowest goal, then lowest slot), not by call order: pass 2 offers out of index order.
        private static void Offer(int goal, int slot, ulong target, double score, bool isCurrent, ref Pick best)
        {
            if (!best.Found || score > best.Score
                || (score == best.Score && (isCurrent != best.IsCurrent ? isCurrent : goal < best.Goal || (goal == best.Goal && slot < best.Slot))))
            {
                best = new Pick { Goal = goal, Slot = slot, Target = target, Score = score, IsCurrent = isCurrent, Found = true };
            }
        }

        private struct Pick
        {
            public int Goal;
            public int Slot;
            public ulong Target;
            public double Score;
            public bool IsCurrent;
            public bool Found;
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

            if (!(double.IsFinite(TargetCommitment) && TargetCommitment >= 1.0))
            {
                throw new ArgumentException("TargetCommitment must be finite and at least 1.");
            }

            if (PlanDepth < 0 || PlanDepth > 8)
            {
                throw new ArgumentException("PlanDepth must be in 0..8.");
            }

            if (!(DirectDiscount >= 0.0 && DirectDiscount <= 1.0))
            {
                throw new ArgumentException("DirectDiscount must be in [0, 1].");
            }

            if (ThinkBudget < 1)
            {
                throw new ArgumentException("ThinkBudget must be at least 1.");
            }

            if (!(double.IsFinite(ReachMeters) && ReachMeters > 0.0))
            {
                throw new ArgumentException("ReachMeters must be finite and above 0.");
            }

            var muscles = -1;
            foreach (var goal in Goals)
            {
                if (goal.Target is { } filter && filter.Kinds.Count == 0)
                {
                    throw new ArgumentException($"Goal {goal.Id}: a Target needs at least one kind.");
                }

                if ((goal.Target != null || goal.Considerations.Count > 0) && !(goal.Priority >= 0.0))
                {
                    throw new ArgumentException($"Goal {goal.Id}: Priority must not be negative on a goal with a Target or considerations.");
                }

                if (goal.Want is { } want)
                {
                    if (want.Subject != FactSubject.Self && want.Subject != FactSubject.This)
                    {
                        throw new ArgumentException($"Goal {goal.Id}: a Want is about himself or the target.");
                    }

                    if ((uint)want.Bit > 63u || (want.Subject == FactSubject.Self && want.Bit == 63))
                    {
                        throw new ArgumentException($"Goal {goal.Id}: a Want is on a bit in 0..63, and bit 63 is Lodur's reach, which the actor has none of.");
                    }

                    if (want.Subject == FactSubject.This && goal.Target is null)
                    {
                        throw new ArgumentException($"Goal {goal.Id}: a Want about the target needs a Target.");
                    }

                    if (!(goal.Priority >= 0.0))
                    {
                        throw new ArgumentException($"Goal {goal.Id}: Priority must not be negative on a goal with a Want.");
                    }
                }

                foreach (var k in goal.Considerations)
                {
                    if (!double.IsFinite(k.X0) || !double.IsFinite(k.X1) || k.X1 == k.X0
                        || !(k.Y0 >= 0.0 && k.Y0 <= 1.0) || !(k.Y1 >= 0.0 && k.Y1 <= 1.0)
                        || !(double.IsFinite(k.Exponent) && k.Exponent > 0.0))
                    {
                        throw new ArgumentException($"Goal {goal.Id}: a consideration needs finite X0 != X1, Y0 and Y1 in [0, 1], and Exponent > 0.");
                    }

                    if (k.Input == ConsiderationInput.PlanSteps)
                    {
                        if (goal.Want is null)
                        {
                            throw new ArgumentException($"Goal {goal.Id}: a PlanSteps consideration needs a goal with a Want.");
                        }
                    }
                    else if (k.Input != ConsiderationInput.Drive && goal.Target is null)
                    {
                        throw new ArgumentException($"Goal {goal.Id}: Distance, Salience and SecondsSinceSensed considerations need a targeted goal.");
                    }
                }

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
