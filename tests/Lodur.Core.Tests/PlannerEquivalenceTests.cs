using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The planner against a forward breadth-first reference that shares none of its machinery: random small worlds, the same
/// depth, the same answer. The reference also picks, among all plans of the shallowest length, the one the spec orders first.
/// </summary>
public sealed class PlannerEquivalenceTests
{
    private const int Slots = 4;
    private const int Depth = 4;
    private const int Reach = 63;
    private static readonly float[] Zero = { 0f };

    private readonly record struct Step(int Verb, int This, int Tool);

    private sealed class World
    {
        public Perception Perception = null!;
        public Verb[] Verbs = null!;
        public ulong SelfFacts;
        public ulong[] Start = null!;      // thing 0 is self, thing 1 + s is slot s, the reach bit derived
        public uint[] Kind = null!;
        public int WantThing;
        public int WantBit;
        public bool WantValue;
    }

    private static Fact Fact(FactSubject s, int bit, bool v) => new(s, bit, v);

    private static Fact[] RandomFacts(Random rng, int count, bool hasTool, bool selfAllowed, int reachOneIn)
    {
        var chosen = new List<Fact>();
        for (var i = 0; i < count; i++)
        {
            var subject = (FactSubject)rng.Next(hasTool ? 3 : 2);
            if (subject == FactSubject.Self && !selfAllowed)
            {
                subject = FactSubject.This;
            }

            var bit = subject != FactSubject.Self && rng.Next(reachOneIn) == 0 ? Reach : rng.Next(4);
            if (chosen.Any(f => f.Subject == subject && f.Bit == bit))
            {
                continue;
            }

            chosen.Add(Fact(subject, bit, rng.Next(2) == 0));
        }

        return chosen.ToArray();
    }

    private static TargetFilter? RandomFilter(Random rng, int chance)
    {
        if (rng.Next(100) >= chance)
        {
            return null;
        }

        var kinds = new List<uint> { (uint)(1 + rng.Next(3)) };
        if (rng.Next(2) == 0)
        {
            kinds.Add((uint)(1 + rng.Next(3)));
        }

        return new TargetFilter { Kinds = kinds.ToArray() };
    }

    private static World Generate(Random rng)
    {
        var w = new World { Kind = new uint[Slots] };
        var things = 3 + rng.Next(2);
        var emitters = new List<Emitter>();
        w.Start = new ulong[Slots + 1];
        for (var i = 0; i < things; i++)
        {
            var kind = (uint)(1 + rng.Next(3));
            var facts = (ulong)rng.Next(16);
            var x = rng.Next(3) switch { 0 => 0.5f, 1 => 1.2f, _ => 5f };
            emitters.Add(new Emitter(Channel.Visual, x, 0f, 0f, 100f, 1.0, (ulong)(10 + i), kind, facts));
        }

        w.Perception = new Perception(new NerveParameters(), 0.01, 1,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = Slots, MemorySeconds = 5.0 });
        w.Perception.Sense(Zero, Zero, Zero, Array.Empty<BoxStimulus>(), emitters.ToArray());

        w.SelfFacts = (ulong)rng.Next(16);
        w.Start[0] = w.SelfFacts;
        for (var s = 0; s < Slots; s++)
        {
            var p = w.Perception.Percept(s);
            w.Kind[s] = p.Kind;
            w.Start[s + 1] = p.Id == 0 ? 0UL : (p.Facts & ~Perception.InReachFact) | (p.X * p.X <= 1.0 ? Perception.InReachFact : 0UL);
        }

        var verbs = new List<Verb>();
        for (var v = 0; v < 2 + rng.Next(5); v++)
        {
            var tool = RandomFilter(rng, 30);
            verbs.Add(new Verb
            {
                Id = "v" + v,
                This = RandomFilter(rng, 60),
                Tool = tool,
                Needs = RandomFacts(rng, rng.Next(4), tool is not null, true, 3),
                Gives = RandomFacts(rng, 1 + rng.Next(3), tool is not null, false, 4).DefaultIfEmpty(Fact(FactSubject.This, 0, true)).ToArray(),
            });
        }

        w.Verbs = verbs.ToArray();

        var occupied = Enumerable.Range(0, Slots).Where(s => w.Perception.Percept(s).Id != 0).ToArray();
        if (rng.Next(2) == 0)
        {
            w.WantThing = 0;
            w.WantBit = rng.Next(4);
        }
        else
        {
            w.WantThing = 1 + occupied[rng.Next(occupied.Length)];
            w.WantBit = rng.Next(2) == 0 ? Reach : rng.Next(4);
        }

        w.WantValue = rng.Next(2) == 0;
        return w;
    }

    private static bool Holds(ulong[] state, int thing, int bit, bool value) => (((state[thing] >> bit) & 1UL) != 0UL) == value;

    private static IEnumerable<Step> Groundings(World w)
    {
        for (var v = 0; v < w.Verbs.Length; v++)
        {
            var verb = w.Verbs[v];
            for (var a = 0; a < Slots; a++)
            {
                if (w.Perception.Percept(a).Id == 0 || (verb.This is not null && !verb.This.Kinds.Contains(w.Kind[a])))
                {
                    continue;
                }

                if (verb.Tool is null)
                {
                    yield return new Step(v, a, -1);
                    continue;
                }

                for (var b = 0; b < Slots; b++)
                {
                    if (b != a && w.Perception.Percept(b).Id != 0 && verb.Tool.Kinds.Contains(w.Kind[b]))
                    {
                        yield return new Step(v, a, b);
                    }
                }
            }
        }
    }

    private static int ThingOf(Fact f, Step s) => f.Subject == FactSubject.Self ? 0 : 1 + (f.Subject == FactSubject.This ? s.This : s.Tool);

    // The reach test of the start state and of a move alike: squared distance of the remembered positions against reach squared.
    private static bool Near(World w, int a, int b)
    {
        Percept pa = w.Perception.Percept(a), pb = w.Perception.Percept(b);
        double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
        return dx * dx + dy * dy + dz * dz <= 1.0;
    }

    // moves: a verb with a This give of reach true takes every other thing out of reach unless it is near the verb's This.
    private static ulong[]? Apply(World w, ulong[] state, Step s, bool moves = true)
    {
        var verb = w.Verbs[s.Verb];
        foreach (var n in verb.Needs)
        {
            if (!Holds(state, ThingOf(n, s), n.Bit, n.Value))
            {
                return null;
            }
        }

        var next = (ulong[])state.Clone();
        // The move first, then the verb's own gives: a give of reach on a thing is a literal and stands.
        if (moves && verb.Gives.Any(g => g.Subject == FactSubject.This && g.Bit == Reach && g.Value))
        {
            for (var o = 0; o < Slots; o++)
            {
                if (o != s.This && w.Perception.Percept(o).Id != 0)
                {
                    next[o + 1] = Near(w, o, s.This) ? next[o + 1] | Perception.InReachFact : next[o + 1] & ~Perception.InReachFact;
                }
            }
        }

        foreach (var g in verb.Gives)
        {
            var thing = ThingOf(g, s);
            next[thing] = g.Value ? next[thing] | (1UL << g.Bit) : next[thing] & ~(1UL << g.Bit);
        }

        return next;
    }

    private static string Key(ulong[] state) => string.Join(",", state);

    // The shallowest plan length by breadth-first search over states, or -1 past the depth.
    private static int Shallowest(World w, List<Step> all, bool moves = true)
    {
        var frontier = new List<ulong[]> { w.Start };
        var seen = new HashSet<string> { Key(w.Start) };
        for (var d = 0; d <= Depth; d++)
        {
            if (frontier.Any(s => Holds(s, w.WantThing, w.WantBit, w.WantValue)))
            {
                return d;
            }

            var next = new List<ulong[]>();
            foreach (var state in frontier)
            {
                foreach (var step in all)
                {
                    var n = Apply(w, state, step, moves);
                    if (n is not null && seen.Add(Key(n)))
                    {
                        next.Add(n);
                    }
                }
            }

            frontier = next;
        }

        return -1;
    }

    // The first plan of exactly n steps in the spec's order: the last step's (verb, This, Tool) ascending first, then the step before it.
    private static Step[]? First(World w, List<Step> all, int n, bool moves = true)
    {
        var chosen = new Step[n];
        return Choose(n) ? chosen : null;

        // Fills chosen from the last step backwards; once every step is chosen, replays them forward.
        bool Choose(int left)
        {
            if (left == 0)
            {
                var state = w.Start;
                foreach (var step in chosen)
                {
                    var next = Apply(w, state, step, moves);
                    if (next is null)
                    {
                        return false;
                    }

                    state = next;
                }

                return Holds(state, w.WantThing, w.WantBit, w.WantValue);
            }

            foreach (var step in all)
            {
                chosen[left - 1] = step;
                if (Choose(left - 1))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static string Dump(World w, PlanStep[] plan, int n) =>
        Environment.NewLine + string.Join(Environment.NewLine, w.Verbs.Select(v => $"{v.Id} this={(v.This is null ? "-" : string.Join(",", v.This.Kinds))} tool={(v.Tool is null ? "-" : string.Join(",", v.Tool.Kinds))} needs={string.Join(",", v.Needs.Select(f => $"{f.Subject}{f.Bit}{f.Value}"))} gives={string.Join(",", v.Gives.Select(f => $"{f.Subject}{f.Bit}{f.Value}"))}"))
        + $"{Environment.NewLine}start={Key(w.Start)} kinds={string.Join(",", w.Kind)} want={w.WantThing}/{w.WantBit}/{w.WantValue} plan={string.Join(" ", plan.Take(Math.Max(n, 0)))} xs={string.Join(",", Enumerable.Range(0, Slots).Select(q => w.Perception.Percept(q).X))}";

    [Fact]
    public void The_plan_is_sound_complete_and_shallowest_against_a_forward_search_on_4000_random_worlds()
    {
        var rng = new Random(20261125);
        int solved = 0, none = 0, ordered = 0, changed = 0;
        var plan = new PlanStep[Depth];
        for (var i = 0; i < 4000; i++)
        {
            var w = Generate(rng);
            var all = Groundings(w).ToList();
            var expected = Shallowest(w, all);
            if (expected != Shallowest(w, all, moves: false))
            {
                changed++;
            }

            var planner = new Planner(w.Verbs, Slots, Depth, 1.0);
            var targetSlot = w.WantThing == 0 ? 0 : w.WantThing - 1;
            var want = new Fact(w.WantThing == 0 ? FactSubject.Self : FactSubject.This, w.WantBit, w.WantValue);
            var n = planner.Plan(w.Perception, w.SelfFacts, 0f, 0f, 0f, targetSlot, want, Depth, plan);
            Assert.True(n == expected, $"world {i}: planner {n}, forward search {expected}{Dump(w, plan, n)}");
            Assert.True(planner.LastNodes <= Planner.NodeBudget, $"world {i} spent the budget{Dump(w, plan, n)}");

            if (n < 0)
            {
                none++;
                continue;
            }

            solved++;
            var slotOf = Enumerable.Range(0, Slots).ToDictionary(s => w.Perception.Percept(s).Id, s => s);
            var steps = new Step[n];
            var state = w.Start;
            for (var k = 0; k < n; k++)
            {
                steps[k] = new Step(plan[k].Verb, slotOf[plan[k].This], plan[k].Tool == 0UL ? -1 : slotOf[plan[k].Tool]);
                var next = Apply(w, state, steps[k]);
                Assert.True(next is not null, $"world {i}: step {k} is not applicable");
                state = next!;
            }

            Assert.True(Holds(state, w.WantThing, w.WantBit, w.WantValue), $"world {i}: the plan does not reach the want");

            if (n <= 3 && n > 0)
            {
                ordered++;
                var first = First(w, all, n)!;
                Assert.True(first.SequenceEqual(steps), $"world {i}: tie order, planner {string.Join(" ", steps)} against {string.Join(" ", first)}");
                if (expected == Shallowest(w, all, moves: false) && First(w, all, n, moves: false) is { } without && !without.SequenceEqual(first))
                {
                    changed++;
                }
            }
        }

        Assert.True(solved > 1000 && none > 500 && ordered > 100, $"the worlds are too one-sided: {solved} solved, {none} none, {ordered} ordered");
        Assert.True(changed > 50, $"the move rule changes the plan or verdict of only {changed} worlds");
    }
}
