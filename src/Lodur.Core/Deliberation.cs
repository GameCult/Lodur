using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>A want pair held back for the planner's gate: its goal, percept slot (-1 for an untargeted goal), the percept's id, its raw score (commitment applied), the most it can still score (raw times the bound factor of the depth searched so far) and whether it is the current pair.</summary>
    internal struct WantPair
    {
        public int Goal;
        public int Slot;
        public ulong Target;
        public double Raw;
        public double Bound;
        public bool Current;
    }

    /// <summary>
    /// What one Actor thinks with while its Brain selects: the planner, the want pairs waiting for the gate (a max-heap by bound, ties current, lowest goal, lowest slot),
    /// the pairs deferred to the next round of deepening, the bound table, and the think budget spent so far in this Select. Pass 2 deepens across pairs (Korf's iterative
    /// deepening, anytime): round k asks every live pair for a plan of exactly k steps, so short plans are found first and no pair starves the rest. Owned by one Actor;
    /// allocates only in its constructor. Not thread-safe.
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
        private readonly int _depth;
        private readonly double[] _bound;       // per goal g and searched depth k in 0..PlanDepth: the most a pair with no plan of k or fewer steps can score, as a factor on raw
        private readonly WantPair[] _next;      // the pairs with no plan of this round's k steps, waiting for round k + 1
        private int _nextCount;
        private readonly int[] _selfDepth;      // per goal this Select: the depth a Self want has been searched to
        private bool _gone;                     // this Select's budget is gone: every later pair goes direct

        internal Deliberation(Brain brain, IReadOnlyList<Verb> verbs, int capacity)
        {
            _brain = brain;
            _planner = new Planner(verbs, capacity, brain.PlanDepth, brain.ReachMeters);
            _plan = new PlanStep[Math.Max(1, brain.PlanDepth)];
            _heap = new WantPair[brain.Goals.Count * capacity];
            _selfHeld = new sbyte[brain.Goals.Count];
            _selfLength = new int[brain.Goals.Count];
            _perception = null!;
            _depth = brain.PlanDepth;
            _next = new WantPair[_heap.Length];
            _selfDepth = new int[brain.Goals.Count];
            _bound = new double[brain.Goals.Count * (_depth + 1)];
            for (var g = 0; g < brain.Goals.Count; g++)
            {
                var direct = brain.Goals[g].PlanFactor(_depth + 1) * brain.DirectDiscount;
                for (var k = 0; k <= _depth; k++)
                {
                    var b = direct;
                    for (var x = k + 1; x <= _depth; x++)
                    {
                        b = Math.Max(b, brain.Goals[g].PlanFactor(x));
                    }

                    _bound[g * (_depth + 1) + k] = b;
                }
            }
        }

        /// <summary>The most a pair of <paramref name="goal"/> with no plan of <paramref name="k"/> or fewer steps can score, as a factor on its raw score. No monotonicity of the plan-length factor is assumed.</summary>
        internal double BoundFactor(int goal, int k) => _bound[goal * (_depth + 1) + k];

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
            _nextCount = 0;
            _gone = false;
            for (var g = 0; g < _selfHeld.Length; g++)
            {
                _selfHeld[g] = -1;
                _selfLength[g] = Unplanned;
                _selfDepth[g] = 0;
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
        /// Round <paramref name="k"/> of the deepening for a want pair that does not hold: n = k when the pair has a plan of exactly k steps (none shorter was found in an earlier round),
        /// 0 when it has none of k steps and k is below PlanDepth (defer to round k + 1), -1 to go at it direct (no plan within PlanDepth, PlanDepth is 0, or the budget is gone, and then
        /// it stays gone for the rest of the Select). A Self want is searched once per goal and depth and shared by its pairs. Spends from the Select's one budget.
        /// </summary>
        internal int PlanAt(int goal, Fact want, int slot, int k)
        {
            if (_depth == 0 || _gone)
            {
                return -1;
            }

            var self = want.Subject == FactSubject.Self;
            if (self)
            {
                if (_selfLength[goal] != Unplanned)
                {
                    return _selfLength[goal];
                }

                if (_selfDepth[goal] >= k)
                {
                    return k == _depth ? -1 : 0;
                }
            }

            var left = _brain.ThinkBudget - _spent;
            if (left < 1)
            {
                _gone = true;
                return -1;
            }

            var n = _planner.Plan(_perception, SelfFacts, _ox, _oy, _oz, self ? 0 : slot, want, k, _plan, left, k);
            _spent = Math.Min(_brain.ThinkBudget, _spent + _planner.LastNodes);
            int result;
            if (n > 0)
            {
                result = n;
            }
            else if (_planner.LastExhausted)
            {
                _gone = true;
                result = -1;
            }
            else
            {
                result = k == _depth ? -1 : 0;
            }

            if (self)
            {
                _selfDepth[goal] = k;
                if (result != 0)
                {
                    _selfLength[goal] = result;
                }
            }

            return result;
        }

        /// <summary>Holds a pair with no plan of this round's steps for the next round.</summary>
        internal void Defer(in WantPair pair) => _next[_nextCount++] = pair;

        /// <summary>Drops every pair still waiting in this round: none of them can beat the best found.</summary>
        internal void ClearRound() => _count = 0;

        /// <summary>Starts the next round with the deferred pairs; false when nothing was deferred.</summary>
        internal bool NextRound()
        {
            if (_nextCount == 0)
            {
                return false;
            }

            _count = 0;
            for (var i = 0; i < _nextCount; i++)
            {
                Push(_next[i]);
            }

            _nextCount = 0;
            return true;
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
            Push(new WantPair { Goal = goal, Slot = slot, Target = target, Raw = raw, Bound = raw * BoundFactor(goal, 0), Current = current });
        }

        private void Push(in WantPair pair)
        {
            var i = _count++;
            _heap[i] = pair;
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

        /// <summary>The waiting want pair with the highest bound (ties current, lowest goal, lowest slot).</summary>
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
            a.Bound != b.Bound ? a.Bound > b.Bound
            : a.Current != b.Current ? a.Current
            : a.Goal != b.Goal ? a.Goal < b.Goal
            : a.Slot < b.Slot;
    }
}
