using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>A want pair held back for the planner's gate: its goal, percept slot (-1 for an untargeted goal), the percept's id, its raw score (commitment applied) and whether it is the current pair.</summary>
    internal struct WantPair
    {
        public int Goal;
        public int Slot;
        public ulong Target;
        public double Raw;
        public bool Current;
    }

    /// <summary>
    /// What one Actor thinks with while its Brain selects: the planner, the want pairs waiting for the gate (a max-heap by raw score, ties current, lowest goal, lowest slot),
    /// the think budget spent so far in this Select, and the plan of the last call. Owned by one Actor; allocates only in its constructor. Not thread-safe.
    /// </summary>
    internal sealed class Deliberation
    {
        private const int Unplanned = int.MinValue;

        private readonly Planner _planner;
        private readonly Brain _brain;
        private readonly PlanStep[] _plan;
        private readonly WantPair[] _heap;
        private readonly sbyte[] _selfHeld;     // per goal this Select: -1 not asked, 0 the Self want does not hold, 1 it holds
        private readonly int[] _selfLength;     // per goal this Select: the Self want's plan length, -1 direct, Unplanned not asked
        private Perception _perception;
        private float _ox;
        private float _oy;
        private float _oz;
        private int _count;
        private int _spent;

        internal Deliberation(Brain brain, IReadOnlyList<Verb> verbs, int capacity)
        {
            _brain = brain;
            _planner = new Planner(verbs, capacity, brain.PlanDepth, brain.ReachMeters);
            _plan = new PlanStep[Math.Max(1, brain.PlanDepth)];
            _heap = new WantPair[brain.Goals.Count * capacity];
            _selfHeld = new sbyte[brain.Goals.Count];
            _selfLength = new int[brain.Goals.Count];
            _perception = null!;
        }

        /// <summary>The actor's own facts this tick, supplied by the game (bit 63 never set).</summary>
        internal ulong SelfFacts { get; set; }

        /// <summary>The groundings the current Select has paid out of <see cref="Brain.ThinkBudget"/>.</summary>
        internal int Spent => _spent;

        /// <summary>The plan of the last derivation, in execution order: step i for i below the length <see cref="Derive"/> returned.</summary>
        internal PlanStep Step(int i) => _plan[i];

        /// <summary>Starts a Select: nothing waits, nothing is spent, nothing is known of the Self wants.</summary>
        internal void Begin(Perception perception, float ox, float oy, float oz)
        {
            _perception = perception;
            _ox = ox;
            _oy = oy;
            _oz = oz;
            _count = 0;
            _spent = 0;
            for (var g = 0; g < _selfHeld.Length; g++)
            {
                _selfHeld[g] = -1;
                _selfLength[g] = Unplanned;
            }
        }

        /// <summary>Whether the want already holds for the pair on <paramref name="slot"/> (-1 for an untargeted goal): a depth-0 plan, which spends no budget. A Self want is asked once per goal.</summary>
        internal bool Holds(int goal, Fact want, int slot)
        {
            if (want.Subject != FactSubject.Self)
            {
                return AskHolds(want, slot);
            }

            if (_selfHeld[goal] < 0)
            {
                _selfHeld[goal] = (sbyte)(AskHolds(want, 0) ? 1 : 0);
            }

            return _selfHeld[goal] == 1;
        }

        private bool AskHolds(Fact want, int slot) =>
            _planner.Plan(_perception, SelfFacts, _ox, _oy, _oz, slot, want, 0, _plan, 1) == 0;

        /// <summary>
        /// The plan length of a want pair that does not hold: n steps, or -1 to go at it direct (no plan within the depth, the call spent the rest of the budget, or none was left).
        /// A Self want is planned once per goal and shared by its pairs. Spends from the Select's one budget.
        /// </summary>
        internal int Plan(int goal, Fact want, int slot)
        {
            var self = want.Subject == FactSubject.Self;
            if (self && _selfLength[goal] != Unplanned)
            {
                return _selfLength[goal];
            }

            var length = -1;
            var left = _brain.ThinkBudget - _spent;
            if (_brain.PlanDepth > 0 && left >= 1)
            {
                var n = _planner.Plan(_perception, SelfFacts, _ox, _oy, _oz, self ? 0 : slot, want, _brain.PlanDepth, _plan, left);
                _spent = Math.Min(_brain.ThinkBudget, _spent + _planner.LastNodes);
                length = n > 0 ? n : -1;
            }

            if (self)
            {
                _selfLength[goal] = length;
            }

            return length;
        }

        /// <summary>
        /// The committed pair's plan, derived with the full budget: 0 when the want holds or its target is not in memory; n for an n-step plan, readable through
        /// <see cref="Step"/>; -1 for a direct try.
        /// </summary>
        internal int Derive(Perception perception, float ox, float oy, float oz, Fact want, ulong target)
        {
            var slot = 0;
            if (want.Subject == FactSubject.This)
            {
                slot = -1;
                var ids = perception.PerceptIds;
                for (var s = 0; s < ids.Length; s++)
                {
                    if (target != 0UL && ids[s] == target)
                    {
                        slot = s;
                        break;
                    }
                }

                if (slot < 0)
                {
                    return 0;
                }
            }

            return _planner.Plan(perception, SelfFacts, ox, oy, oz, slot, want, _brain.PlanDepth, _plan, _brain.ThinkBudget);
        }

        internal void Add(int goal, int slot, ulong target, double raw, bool current)
        {
            var i = _count++;
            _heap[i] = new WantPair { Goal = goal, Slot = slot, Target = target, Raw = raw, Current = current };
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (!Before(_heap[i], _heap[parent]))
                {
                    break;
                }

                (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
                i = parent;
            }
        }

        /// <summary>The waiting want pair with the highest raw score (ties current, lowest goal, lowest slot).</summary>
        internal bool TryPop(out WantPair pair)
        {
            if (_count == 0)
            {
                pair = default;
                return false;
            }

            pair = _heap[0];
            _heap[0] = _heap[--_count];
            var i = 0;
            while (true)
            {
                var l = 2 * i + 1;
                if (l >= _count)
                {
                    break;
                }

                var c = l + 1 < _count && Before(_heap[l + 1], _heap[l]) ? l + 1 : l;
                if (!Before(_heap[c], _heap[i]))
                {
                    break;
                }

                (_heap[i], _heap[c]) = (_heap[c], _heap[i]);
                i = c;
            }

            return true;
        }

        private static bool Before(in WantPair a, in WantPair b) =>
            a.Raw != b.Raw ? a.Raw > b.Raw
            : a.Current != b.Current ? a.Current
            : a.Goal != b.Goal ? a.Goal < b.Goal
            : a.Slot < b.Slot;
    }
}
