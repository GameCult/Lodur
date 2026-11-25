using System;
using System.Collections.Generic;

namespace Lodur
{
    /// <summary>Whom a fact is about, in a verb: the actor, the verb's target, or the verb's second thing.</summary>
    public enum FactSubject
    {
        Self,
        This,
        Tool,
    }

    /// <summary>One literal: bit <see cref="Bit"/> (0..63) of the subject's facts is <see cref="Value"/>. Lodur never interprets a bit except <see cref="Perception.InReachFact"/>.</summary>
    public readonly record struct Fact(FactSubject Subject, int Bit, bool Value);

    /// <summary>
    /// A thing the game lets the puppet do, as the planner sees it: what must hold before and what holds after.
    /// Content, authored by the consumer; the planner never reads <see cref="Id"/>.
    /// </summary>
    public sealed record Verb
    {
        private IReadOnlyList<Fact> _needsView = Array.AsReadOnly(Array.Empty<Fact>());
        private IReadOnlyList<Fact> _givesView = Array.AsReadOnly(Array.Empty<Fact>());

        /// <summary>The consumer's name for the verb; never read.</summary>
        public string Id { get; init; } = "";

        /// <summary>The kinds the verb's target may be; null accepts any kind.</summary>
        public TargetFilter? This { get; init; }

        /// <summary>The kinds the verb's second thing may be; null means the verb takes no second thing.</summary>
        public TargetFilter? Tool { get; init; }

        /// <summary>What must hold before the verb. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Fact> Needs
        {
            get => _needsView;
            init => _needsView = Array.AsReadOnly(Planner.Copy(value));
        }

        /// <summary>What holds after the verb. Copied when set; read back as a read-only view.</summary>
        public IReadOnlyList<Fact> Gives
        {
            get => _givesView;
            init => _givesView = Array.AsReadOnly(Planner.Copy(value));
        }
    }

    /// <summary>One step of a plan: the verb's index in the planner's list and the percept ids it is grounded on (Tool 0: none). Ids, not slots, because slots are reused.</summary>
    public readonly record struct PlanStep(int Verb, ulong This, ulong Tool);

    /// <summary>
    /// A bounded regressive planner over the percepts' facts. Given a want it finds the shallowest sequence of at most
    /// <c>depth</c> verbs that would make it hold, working back from the want through what each verb gives and needs.
    /// It reads percepts and keeps nothing: the plan is the caller's, derived per call. Everything it allocates is allocated here.
    /// Not thread-safe: the search works in the planner's own buffers.
    /// </summary>
    public sealed class Planner
    {
        private const int MaxNeeds = 8;

        /// <summary>
        /// The most groundings one <see cref="Plan"/> call may regress, counted across all its depths. A counter, not a clock, so the cut-off is the same on every machine.
        /// A call that spends it returns -1 as if no plan existed.
        /// </summary>
        public const int NodeBudget = 4096;

        private readonly Verb[] _verbs;
        private readonly Fact[][] _needs;
        private readonly Fact[][] _gives;
        private readonly uint[]?[] _thisKinds;  // the kinds a verb's target may be; null accepts any
        private readonly uint[]?[] _toolKinds;  // the kinds a verb's second thing may be; null when it takes none
        private readonly ulong[] _givenBits;    // per verb, bit i set when one of its gives is on bit i of any subject
        private readonly int _words;
        private readonly int _capacity;
        private readonly double _reach;
        private readonly int _stride;
        private readonly ulong[] _facts;        // thing 0 is self, thing 1 + s is percept slot s
        private readonly ulong[] _slotId;
        private readonly uint[] _slotKind;
        private readonly int[] _set;            // the needed literals with r steps left, at r * _stride
        private readonly int[] _setCount;
        private readonly int[] _stepVerb;       // the step chosen with r steps left, at r - 1
        private readonly int[] _stepThis;
        private readonly int[] _stepTool;
        private readonly ulong[] _meetsThis;    // per level, the slots whose literal a This give of the verb in hand meets, _words per level
        private readonly ulong[] _meetsTool;
        private int _nodes;

        public Planner(IReadOnlyList<Verb> verbs, int capacity, int maxDepth, double reachMeters)
        {
            if (verbs is null)
            {
                throw new ArgumentNullException(nameof(verbs));
            }

            if (maxDepth < 0 || maxDepth > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDepth), "The planner's depth must be in 0..8.");
            }

            if (!(reachMeters > 0.0 && reachMeters < double.PositiveInfinity))
            {
                throw new ArgumentException("The reach must be finite and above zero.", nameof(reachMeters));
            }

            if (capacity < 1 || capacity > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "The planner's capacity must be in 1..256.");
            }

            _verbs = new Verb[verbs.Count];
            _needs = new Fact[verbs.Count][];
            _gives = new Fact[verbs.Count][];
            _thisKinds = new uint[verbs.Count][];
            _toolKinds = new uint[verbs.Count][];
            _givenBits = new ulong[verbs.Count];
            for (var v = 0; v < _verbs.Length; v++)
            {
                var verb = verbs[v] ?? throw new ArgumentException($"Verb {v} is null.", nameof(verbs));
                Validate(v, verb);
                _verbs[v] = verb;
                _needs[v] = Copy(verb.Needs);
                _gives[v] = Copy(verb.Gives);
                _thisKinds[v] = Kinds(verb.This);
                _toolKinds[v] = Kinds(verb.Tool);
                foreach (var g in _gives[v])
                {
                    _givenBits[v] |= 1UL << g.Bit;
                }
            }

            MaxDepth = maxDepth;
            _capacity = capacity;
            _reach = reachMeters;
            _stride = 1 + maxDepth * MaxNeeds;
            _facts = new ulong[capacity + 1];
            _slotId = new ulong[capacity];
            _slotKind = new uint[capacity];
            _set = new int[(maxDepth + 1) * _stride];
            _setCount = new int[maxDepth + 1];
            _stepVerb = new int[Math.Max(1, maxDepth)];
            _stepThis = new int[Math.Max(1, maxDepth)];
            _stepTool = new int[Math.Max(1, maxDepth)];
            _words = (capacity + 63) >> 6;
            _meetsThis = new ulong[(maxDepth + 1) * _words];
            _meetsTool = new ulong[(maxDepth + 1) * _words];
        }

        /// <summary>The deepest plan this planner can be asked for.</summary>
        public int MaxDepth { get; }

        /// <summary>The groundings the last <see cref="Plan"/> call regressed; above <see cref="NodeBudget"/> means it ran out and returned -1.</summary>
        public int LastNodes => _nodes;

        /// <summary>
        /// 0 when the want already holds, else the length of the shallowest plan of at most <paramref name="depth"/> steps, written into
        /// <paramref name="plan"/> in execution order, else -1. The want is about self or about the percept in <paramref name="targetSlot"/>.
        /// A thing's facts are those it was last sensed with, bit 63 replaced by whether it is within reach of the point.
        /// </summary>
        public int Plan(Perception perception, ulong selfFacts, float ox, float oy, float oz, int targetSlot, Fact want, int depth, Span<PlanStep> plan)
        {
            Check(perception, targetSlot, want, depth, plan.Length);
            _nodes = 0;

            _facts[0] = selfFacts & ~Perception.InReachFact;
            var ids = perception.PerceptIds;
            var kinds = perception.PerceptKinds;
            var facts = perception.PerceptFacts;
            var xs = perception.PerceptX;
            var ys = perception.PerceptY;
            var zs = perception.PerceptZ;
            var reachSquared = _reach * _reach;
            ids.CopyTo(_slotId);
            kinds.CopyTo(_slotKind);
            for (var s = 0; s < _capacity; s++)
            {
                double dx = xs[s] - ox, dy = ys[s] - oy, dz = zs[s] - oz;
                _facts[s + 1] = (facts[s] & ~Perception.InReachFact) | (dx * dx + dy * dy + dz * dz <= reachSquared ? Perception.InReachFact : 0UL);
            }

            var wantLit = Lit(want.Subject == FactSubject.Self ? 0 : targetSlot + 1, want.Bit, want.Value);
            if (Holds(wantLit))
            {
                return 0;
            }

            for (var d = 1; d <= depth; d++)
            {
                _set[d * _stride] = wantLit;
                _setCount[d] = 1;
                if (Search(d))
                {
                    for (var i = 0; i < d; i++)
                    {
                        plan[i] = Step(_stepVerb[i], _slotId[_stepThis[i]], _stepTool[i] < 0 ? 0UL : _slotId[_stepTool[i]]);
                    }

                    return d;
                }

                if (_nodes > NodeBudget)
                {
                    break;
                }
            }

            return -1;
        }

        // Can the set at r be reached in exactly r steps from the state? The last step is chosen here, in ascending (verb, This slot, Tool slot).
        private bool Search(int r)
        {
            var cur = r * _stride;
            var n = _setCount[r];
            if (r == 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (!Holds(_set[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            var open = 0UL;
            for (var i = 0; i < n; i++)
            {
                open |= 1UL << ((_set[cur + i] >> 1) & 63);
            }

            for (var v = 0; v < _verbs.Length; v++)
            {
                // Relevance, before any grounding: a step is only worth taking if it gives a literal still open.
                if ((_givenBits[v] & open) == 0UL || !Meets(v, r, cur, n, out var meetsSelf))
                {
                    continue;
                }

                var thisKinds = _thisKinds[v];
                var toolKinds = _toolKinds[v];
                var m = r * _words;
                for (var a = 0; a < _capacity; a++)
                {
                    if (_slotId[a] == 0 || (thisKinds is not null && !Has(thisKinds, _slotKind[a])))
                    {
                        continue;
                    }

                    var meetsA = meetsSelf || ((_meetsThis[m + (a >> 6)] >> (a & 63)) & 1UL) != 0UL;
                    if (toolKinds is null)
                    {
                        if (meetsA && Try(r, v, a, -1, cur, n))
                        {
                            return true;
                        }

                        if (_nodes > NodeBudget)
                        {
                            return false;
                        }

                        continue;
                    }

                    for (var b = 0; b < _capacity; b++)
                    {
                        if (b == a || _slotId[b] == 0 || !Has(toolKinds, _slotKind[b]) || !(meetsA || ((_meetsTool[m + (b >> 6)] >> (b & 63)) & 1UL) != 0UL))
                        {
                            continue;
                        }

                        if (Try(r, v, a, b, cur, n))
                        {
                            return true;
                        }

                        if (_nodes > NodeBudget)
                        {
                            return false;
                        }
                    }
                }
            }

            return false;
        }

        // Takes the step v on (a, b) as the last of r: regress through it and search the rest.
        private bool Try(int r, int v, int a, int b, int cur, int n)
        {
            _nodes++;
            if (_nodes > NodeBudget || !Regress(v, a, b, cur, n, (r - 1) * _stride, out var count))
            {
                return false;
            }

            _setCount[r - 1] = count;
            _stepVerb[r - 1] = v;
            _stepThis[r - 1] = a;
            _stepTool[r - 1] = b;
            return Search(r - 1);
        }

        // Which groundings of the verb give a literal the set at r still needs: a Self give meets one whatever the grounding (meetsSelf),
        // a This give meets the one on the slot it is grounded on, a Tool give likewise; those slots are left in the level's masks.
        // False when no grounding of the verb meets anything.
        private bool Meets(int v, int r, int cur, int n, out bool meetsSelf)
        {
            meetsSelf = false;
            var m = r * _words;
            for (var w = 0; w < _words; w++)
            {
                _meetsThis[m + w] = 0UL;
                _meetsTool[m + w] = 0UL;
            }

            var any = false;
            var gives = _gives[v];
            for (var g = 0; g < gives.Length; g++)
            {
                var gf = gives[g];
                for (var i = 0; i < n; i++)
                {
                    var l = _set[cur + i];
                    if (((l >> 1) & 63) != gf.Bit || (l & 1) != (gf.Value ? 1 : 0))
                    {
                        continue;
                    }

                    var thing = l >> 7;
                    if (gf.Subject == FactSubject.Self)
                    {
                        if (thing == 0)
                        {
                            meetsSelf = true;
                            any = true;
                        }
                    }
                    else if (thing > 0)
                    {
                        var mask = gf.Subject == FactSubject.This ? _meetsThis : _meetsTool;
                        mask[m + ((thing - 1) >> 6)] |= 1UL << ((thing - 1) & 63);
                        any = true;
                    }
                }
            }

            return any;
        }

        private static bool Has(uint[] kinds, uint kind)
        {
            for (var i = 0; i < kinds.Length; i++)
            {
                if (kinds[i] == kind)
                {
                    return true;
                }
            }

            return false;
        }

        private static uint[]? Kinds(TargetFilter? filter)
        {
            if (filter is null)
            {
                return null;
            }

            var kinds = new uint[filter.Kinds.Count];
            for (var i = 0; i < kinds.Length; i++)
            {
                kinds[i] = filter.Kinds[i];
            }

            return kinds;
        }

        // The set before the step: what the step gives that is needed is met, what it needs is added. False when the
        // step undoes something needed or leaves a literal and its negation. That it meets something is Search's relevance check.
        private bool Regress(int v, int a, int b, int cur, int n, int next, out int count)
        {
            count = 0;
            var gives = _gives[v];
            for (var g = 0; g < gives.Length; g++)
            {
                var lit = Ground(gives[g], a, b);
                for (var i = 0; i < n; i++)
                {
                    var l = _set[cur + i];
                    if ((l >> 1) != (lit >> 1))
                    {
                        continue;
                    }

                    if (l != lit)
                    {
                        return false;
                    }
                }
            }

            for (var i = 0; i < n; i++)
            {
                var l = _set[cur + i];
                var given = false;
                for (var g = 0; g < gives.Length && !given; g++)
                {
                    given = Ground(gives[g], a, b) == l;
                }

                if (!given)
                {
                    _set[next + count++] = l;
                }
            }

            var needs = _needs[v];
            for (var k = 0; k < needs.Length; k++)
            {
                var lit = Ground(needs[k], a, b);
                var present = false;
                for (var i = 0; i < count; i++)
                {
                    var l = _set[next + i];
                    if (l == lit)
                    {
                        present = true;
                    }
                    else if (l == (lit ^ 1))
                    {
                        return false;
                    }
                }

                if (!present)
                {
                    _set[next + count++] = lit;
                }
            }

            return true;
        }

        private bool Holds(int lit) => (int)((_facts[lit >> 7] >> ((lit >> 1) & 63)) & 1UL) == (lit & 1);

        private static int Lit(int thing, int bit, bool value) => (thing << 7) | (bit << 1) | (value ? 1 : 0);

        private static int Ground(Fact f, int thisSlot, int toolSlot) =>
            Lit(f.Subject == FactSubject.Self ? 0 : (f.Subject == FactSubject.This ? thisSlot : toolSlot) + 1, f.Bit, f.Value);

        private static PlanStep Step(int verb, ulong thisId, ulong toolId) => new PlanStep(verb, thisId, toolId);

        private void Check(Perception perception, int targetSlot, Fact want, int depth, int planLength)
        {
            if (perception is null)
            {
                throw new ArgumentNullException(nameof(perception));
            }

            if (perception.PerceptCount != _capacity)
            {
                throw new ArgumentException($"The planner holds {_capacity} percept slots; the perception has {perception.PerceptCount}.", nameof(perception));
            }

            if (depth < 0 || depth > MaxDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(depth), $"The depth must be in 0..{MaxDepth}.");
            }

            if (planLength < depth)
            {
                throw new ArgumentException("The plan span is shorter than the depth.", "plan");
            }

            if (want.Subject == FactSubject.Tool || (uint)want.Subject > 2u || (uint)want.Bit > 63u)
            {
                throw new ArgumentException("The want is about self or the target, on a bit in 0..63.", nameof(want));
            }

            if (want.Subject == FactSubject.Self && want.Bit == 63)
            {
                throw new ArgumentException("Bit 63 is Lodur's reach; self has none.", nameof(want));
            }

            if (want.Subject == FactSubject.This && ((uint)targetSlot >= (uint)_capacity || perception.Percept(targetSlot).Id == 0))
            {
                throw new ArgumentException("A want on the target needs an occupied target slot.", nameof(targetSlot));
            }
        }

        internal static Fact[] Copy(IReadOnlyList<Fact> facts)
        {
            var copy = new Fact[facts.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = facts[i];
            }

            return copy;
        }

        private static void Validate(int index, Verb verb)
        {
            var name = $"Verb {index} ({verb.Id})";
            if ((verb.This is not null && verb.This.Kinds.Count == 0) || (verb.Tool is not null && verb.Tool.Kinds.Count == 0))
            {
                throw new ArgumentException($"{name} has a filter with no kinds.");
            }

            if (verb.Gives.Count == 0)
            {
                throw new ArgumentException($"{name} gives nothing.");
            }

            if (verb.Needs.Count > MaxNeeds || verb.Gives.Count > MaxNeeds)
            {
                throw new ArgumentException($"{name} has more than {MaxNeeds} needs or gives.");
            }

            ValidateFacts(name, verb, verb.Needs);
            ValidateFacts(name, verb, verb.Gives);
        }

        private static void ValidateFacts(string name, Verb verb, IReadOnlyList<Fact> facts)
        {
            for (var i = 0; i < facts.Count; i++)
            {
                var f = facts[i];
                if ((uint)f.Bit > 63u || (uint)f.Subject > 2u)
                {
                    throw new ArgumentException($"{name} has a fact on bit {f.Bit} of subject {f.Subject}; bits are 0..63.");
                }

                if (f.Subject == FactSubject.Tool && verb.Tool is null)
                {
                    throw new ArgumentException($"{name} has a Tool fact but takes no second thing.");
                }

                if (f.Subject == FactSubject.Self && f.Bit == 63)
                {
                    throw new ArgumentException($"{name} has a Self fact on bit 63, which is Lodur's reach.");
                }

                for (var j = 0; j < i; j++)
                {
                    if (facts[j].Subject == f.Subject && facts[j].Bit == f.Bit && facts[j].Value != f.Value)
                    {
                        throw new ArgumentException($"{name} lists {f.Subject} bit {f.Bit} both ways.");
                    }
                }
            }
        }
    }
}
