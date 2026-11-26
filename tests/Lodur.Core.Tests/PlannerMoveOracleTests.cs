using System;
using System.Collections.Generic;
using System.Linq;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The planner's move rule and budget against an independent forward oracle on wide and deep random worlds (Soul's probes of cut lodur-planner-moves, committed:
/// follow-up lodur:follow_up:planner-moves-s1-lows). The deep generator makes plans of 3 to 5 steps through several movers; the asserted counts keep the fuzz honest.
/// </summary>
public sealed class PlannerMoveOracleTests
{
    private const int Reach = 63;
    private static readonly float[] Zero = { 0f }; public static double R = 1.0; private static readonly int[] Depths = { 0, 2, 5 };

    private readonly record struct Step(int Verb, int This, int Tool);

    private sealed class World
    {
        public Perception P = null!; public Verb[] Verbs = null!; public ulong SelfFacts; public ulong[] Start = null!; public uint[] Kind = null!;
        public int WantThing, WantBit; public bool WantValue; public int Slots; public int Depth = 4;
    }

    // mode: 0 old generator rates (base), 1 head generator, 2 wide (3-D, boundary distances, 5 slots, high reach rates)
    private static Fact[] RandomFacts(Random rng, int count, bool hasTool, bool selfAllowed, int reachOneIn)
    {
        var chosen = new List<Fact>();
        for (var i = 0; i < count; i++)
        {
            var subject = (FactSubject)rng.Next(hasTool ? 3 : 2);
            if (subject == FactSubject.Self && !selfAllowed) subject = FactSubject.This;
            var bit = subject != FactSubject.Self && rng.Next(reachOneIn) == 0 ? Reach : rng.Next(4);
            if (chosen.Any(f => f.Subject == subject && f.Bit == bit)) continue;
            chosen.Add(new Fact(subject, bit, rng.Next(2) == 0));
        }
        return chosen.ToArray();
    }

    private static TargetFilter? RandomFilter(Random rng, int chance)
    {
        if (rng.Next(100) >= chance) return null;
        var kinds = new List<uint> { (uint)(1 + rng.Next(3)) };
        if (rng.Next(2) == 0) kinds.Add((uint)(1 + rng.Next(3)));
        return new TargetFilter { Kinds = kinds.ToArray() };
    }

    private static World Generate(Random rng, int mode)
    {
        var slots = mode == 2 ? 5 : 4;
        var w = new World { Kind = new uint[slots], Slots = slots };
        var things = 3 + rng.Next(mode == 2 ? 3 : 2);
        var emitters = new List<Emitter>();
        w.Start = new ulong[slots + 1];
        (float, float, float)[] lattice = { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0.5f, 0, 0), (1.5f, 0, 0), (1.2f, 0, 0), (0, 0.9f, 0.9f), (5, 0, 0), (0, 5, 0), (0.7f, 0.7f, 0), (2, 0, 0) };
        for (var i = 0; i < things; i++)
        {
            var kind = (uint)(1 + rng.Next(3));
            var facts = (ulong)rng.Next(16);
            float x = 0, y = 0, z = 0;
            if (mode == 0) x = rng.Next(2) == 0 ? 0.5f : 5f;
            else if (mode == 1) x = rng.Next(3) switch { 0 => 0.5f, 1 => 1.2f, _ => 5f };
            else (x, y, z) = lattice[rng.Next(lattice.Length)];
            emitters.Add(new Emitter(Channel.Visual, (float)(x * R), (float)(y * R), (float)(z * R), 100f, 1.0, (ulong)(10 + i), kind, facts));
        }
        w.P = new Perception(new NerveParameters(), 0.01, 1,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = slots, MemorySeconds = 5.0 });
        w.P.Sense(Zero, Zero, Zero, Array.Empty<BoxStimulus>(), emitters.ToArray());
        w.SelfFacts = (ulong)rng.Next(16);
        w.Start[0] = w.SelfFacts;
        for (var s = 0; s < slots; s++)
        {
            var p = w.P.Percept(s);
            w.Kind[s] = p.Kind;
            w.Start[s + 1] = p.Id == 0 ? 0UL : (p.Facts & ~Perception.InReachFact) | (p.X * p.X + p.Y * p.Y + p.Z * p.Z <= R * R ? Perception.InReachFact : 0UL);
        }
        var needs1 = mode == 0 ? 4 : mode == 1 ? 3 : 3;
        var gives1 = mode == 0 ? 4 : mode == 1 ? 4 : 3;
        var verbs = new List<Verb>();
        for (var v = 0; v < 2 + rng.Next(mode == 2 ? 6 : 5); v++)
        {
            var tool = RandomFilter(rng, 30);
            verbs.Add(new Verb
            {
                Id = "v" + v, This = RandomFilter(rng, 60), Tool = tool,
                Needs = RandomFacts(rng, rng.Next(4), tool is not null, true, needs1),
                Gives = RandomFacts(rng, 1 + rng.Next(3), tool is not null, false, gives1).DefaultIfEmpty(new Fact(FactSubject.This, 0, true)).ToArray(),
            });
        }
        w.Verbs = verbs.ToArray();
        var occupied = Enumerable.Range(0, slots).Where(s => w.P.Percept(s).Id != 0).ToArray();
        if (rng.Next(2) == 0) { w.WantThing = 0; w.WantBit = rng.Next(4); }
        else { w.WantThing = 1 + occupied[rng.Next(occupied.Length)]; w.WantBit = rng.Next(mode == 0 ? 4 : 2) == 0 ? Reach : rng.Next(4); }
        w.WantValue = rng.Next(2) == 0;
        return w;
    }

    private static IEnumerable<Step> Groundings(World w)
    {
        for (var v = 0; v < w.Verbs.Length; v++)
        {
            var verb = w.Verbs[v];
            for (var a = 0; a < w.Slots; a++)
            {
                if (w.P.Percept(a).Id == 0 || (verb.This is not null && !verb.This.Kinds.Contains(w.Kind[a]))) continue;
                if (verb.Tool is null) { yield return new Step(v, a, -1); continue; }
                for (var b = 0; b < w.Slots; b++)
                    if (b != a && w.P.Percept(b).Id != 0 && verb.Tool.Kinds.Contains(w.Kind[b])) yield return new Step(v, a, b);
            }
        }
    }

    private static int ThingOf(Fact f, Step s) => f.Subject == FactSubject.Self ? 0 : 1 + (f.Subject == FactSubject.This ? s.This : s.Tool);
    private static bool Holds(ulong[] st, int thing, int bit, bool v) => (((st[thing] >> bit) & 1UL) != 0UL) == v;

    private static bool Near(World w, int a, int b)
    {
        Percept pa = w.P.Percept(a), pb = w.P.Percept(b);
        double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
        return dx * dx + dy * dy + dz * dz <= R * R;
    }

    // order 0: no moves; 1: move then gives (Hands); 2: gives then move (spec's oracle)
    private static ulong[]? Apply(World w, ulong[] state, Step s, int order)
    {
        var verb = w.Verbs[s.Verb];
        foreach (var n in verb.Needs) if (!Holds(state, ThingOf(n, s), n.Bit, n.Value)) return null;
        var next = (ulong[])state.Clone();
        var mover = order != 0 && verb.Gives.Any(g => g.Subject == FactSubject.This && g.Bit == Reach && g.Value);
        void Move()
        {
            for (var o = 0; o < w.Slots; o++)
                if (o != s.This && w.P.Percept(o).Id != 0)
                    next[o + 1] = Near(w, o, s.This) ? next[o + 1] | Perception.InReachFact : next[o + 1] & ~Perception.InReachFact;
        }
        if (mover && order == 1) Move();
        foreach (var g in verb.Gives)
        {
            var t = ThingOf(g, s);
            next[t] = g.Value ? next[t] | (1UL << g.Bit) : next[t] & ~(1UL << g.Bit);
        }
        if (mover && order == 2) Move();
        return next;
    }

    private static string Key(ulong[] st) => string.Join(",", st);

    private static int Shallowest(World w, List<Step> all, int order)
    {
        var frontier = new List<ulong[]> { w.Start };
        var seen = new HashSet<string> { Key(w.Start) };
        bool Goal(ulong[] st) => Holds(st, w.WantThing, w.WantBit, w.WantValue);
        if (Goal(w.Start)) return 0;
        for (var d = 1; d <= w.Depth; d++)
        {
            var next = new List<ulong[]>();
            foreach (var st in frontier)
                foreach (var step in all)
                {
                    var n = Apply(w, st, step, order);
                    if (n is not null && seen.Add(Key(n))) { if (Goal(n)) return d; next.Add(n); }
                }
            frontier = next;
        }
        return -1;
    }

    private static Fact WantOf(World w) => new(w.WantThing == 0 ? FactSubject.Self : FactSubject.This, w.WantBit, w.WantValue);
    private static int TargetSlot(World w) => w.WantThing == 0 ? 0 : w.WantThing - 1;

    private static bool IsMover(Verb v) => v.Gives.Any(g => g.Subject == FactSubject.This && g.Bit == Reach && g.Value);

    // A forward breadth-first oracle that shares none of the planner's machinery, on worlds with boundary distances, 3-D positions and a high rate of reach facts.
    // The oracle applies a move first, then the verb's gives (order 1); gives-then-move (order 2) is the other reading a mutation could fake.
    [Fact]
    public void The_planner_equals_a_forward_oracle_on_12000_wide_worlds()
    {
        var rng = new Random(777);
        int N = 12000, mism1 = 0, mism2 = 0, spec2only = 0, exhausted = 0, solved = 0, moverWorlds = 0, planMover = 0, deep3 = 0, toolGiveMover = 0;
        var plan = new PlanStep[4];
        for (var i = 0; i < N; i++)
        {
            var w = Generate(rng, 2);
            var all = Groundings(w).ToList();
            var e1 = Shallowest(w, all, 1); var e2 = Shallowest(w, all, 2);
            var pl = new Planner(w.Verbs, w.Slots, 4, R);
            var n = pl.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, TargetSlot(w), WantOf(w), 4, plan);
            if (pl.LastExhausted) { exhausted++; continue; }
            if (w.Verbs.Any(IsMover)) moverWorlds++;
            if (w.Verbs.Any(v => IsMover(v) && v.Gives.Any(g => g.Subject == FactSubject.Tool && g.Bit == Reach))) toolGiveMover++;
            if (n >= 0) solved++;
            if (n >= 3) deep3++;
            if (n > 0 && Enumerable.Range(0, n).Any(k => IsMover(w.Verbs[plan[k].Verb]))) planMover++;
            if (n != e1) { mism1++; }
            if (n != e2) { mism2++; if (e1 != e2) spec2only++; }
        }
        Assert.True(moverWorlds > 5000 && planMover > 300 && toolGiveMover > 200 && solved > 5000, $"the worlds are too one-sided: {moverWorlds} with a mover, {planMover} plans through one, {toolGiveMover} giving a tool reach, {solved} solved");
        Assert.Equal(0, mism1);
    }

    // chain-rich generator: bit ladders, movers, reach needs; makes plans of length 3-5
    private static World GenerateDeep(Random rng)
    {
        var slots = 5; var w = new World { Kind = new uint[slots], Slots = slots, Depth = 5 };
        var things = 3 + rng.Next(3);
        (float, float, float)[] lattice = { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0.5f, 0, 0), (1.5f, 0, 0), (5, 0, 0), (5.8f, 0, 0), (0, 0, 5), (2, 0, 0), (1.4f, 0.1f, 0) };
        var emitters = new List<Emitter>(); w.Start = new ulong[slots + 1];
        for (var i = 0; i < things; i++)
        {
            var (x, y, z) = lattice[rng.Next(lattice.Length)];
            emitters.Add(new Emitter(Channel.Visual, (float)(x * R), (float)(y * R), (float)(z * R), 100f, 1.0, (ulong)(10 + i), (uint)(1 + rng.Next(2)), (ulong)(rng.Next(3) == 0 ? 1 : 0)));
        }
        w.P = new Perception(new NerveParameters(), 0.01, 1, new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0, new PerceptParameters { Capacity = slots, MemorySeconds = 5.0 });
        w.P.Sense(Zero, Zero, Zero, Array.Empty<BoxStimulus>(), emitters.ToArray());
        w.SelfFacts = 0; w.Start[0] = 0;
        for (var s = 0; s < slots; s++)
        {
            var p = w.P.Percept(s); w.Kind[s] = p.Kind;
            w.Start[s + 1] = p.Id == 0 ? 0UL : (p.Facts & ~Perception.InReachFact) | (p.X * p.X + p.Y * p.Y + p.Z * p.Z <= R * R ? Perception.InReachFact : 0UL);
        }
        var verbs = new List<Verb>();
        var nv = 3 + rng.Next(5);
        for (var v = 0; v < nv; v++)
        {
            var tool = rng.Next(100) < 30 ? new TargetFilter { Kinds = new[] { (uint)(1 + rng.Next(2)) } } : null;
            var thisF = rng.Next(100) < 50 ? new TargetFilter { Kinds = new[] { (uint)(1 + rng.Next(2)) } } : null;
            var needs = new List<Fact>(); var gives = new List<Fact>();
            var k = rng.Next(4);
            if (k > 0 && rng.Next(100) < 80) needs.Add(new Fact(FactSubject.This, k - 1 + 1 - 1 >= 0 ? k - 1 : 0, true));
            if (rng.Next(100) < 45) needs.Add(new Fact(FactSubject.This, Reach, rng.Next(5) != 0));
            if (tool is not null && rng.Next(100) < 50) needs.Add(new Fact(FactSubject.Tool, Reach, rng.Next(4) != 0));
            if (tool is not null && rng.Next(100) < 30) needs.Add(new Fact(FactSubject.Tool, rng.Next(3), true));
            var mover = rng.Next(100) < 45;
            if (mover) gives.Add(new Fact(FactSubject.This, Reach, true));
            if (!mover || rng.Next(100) < 70) gives.Add(new Fact(FactSubject.This, k, true));
            if (tool is not null && rng.Next(100) < 12) gives.Add(new Fact(FactSubject.Tool, Reach, rng.Next(2) == 0));
            if (rng.Next(100) < 10) gives.Add(new Fact(FactSubject.Self, rng.Next(2), true));
            verbs.Add(new Verb { Id = "v" + v, This = thisF, Tool = tool, Needs = needs.GroupBy(f => (f.Subject, f.Bit)).Select(g => g.First()).ToArray(), Gives = gives.GroupBy(f => (f.Subject, f.Bit)).Select(g => g.First()).ToArray() });
        }
        w.Verbs = verbs.ToArray();
        var occ = Enumerable.Range(0, slots).Where(s => w.P.Percept(s).Id != 0).ToArray();
        w.WantThing = 1 + occ[rng.Next(occ.Length)]; w.WantBit = rng.Next(5) == 0 ? Reach : 2 + rng.Next(2); w.WantValue = w.WantBit == Reach ? rng.Next(2) == 0 : true;
        return w;
    }

    [Fact]
    public void The_planner_equals_a_forward_oracle_on_6000_deep_worlds_of_plans_up_to_five_steps()
    {
        var rng = new Random(4242);
        int N = 6000, mism = 0, exhausted = 0, specOnly = 0; long nodesTot = 0; var hist = new int[7]; int planMover2 = 0, twoMoves = 0;
        var plan = new PlanStep[5];
        for (var i = 0; i < N; i++)
        {
            var w = GenerateDeep(rng);
            var all = Groundings(w).ToList();
            var e1 = Shallowest(w, all, 1); var e2 = Shallowest(w, all, 2); var e0 = Shallowest(w, all, 0);
            var pl = new Planner(w.Verbs, w.Slots, 5, R);
            var n = pl.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, TargetSlot(w), WantOf(w), 5, plan);
            if (pl.LastExhausted) { exhausted++; continue; }
            hist[n + 1]++; nodesTot += pl.LastNodes;
            if (n >= 2 && Enumerable.Range(0, n).Count(k => IsMover(w.Verbs[plan[k].Verb])) >= 2) twoMoves++;
            if (e0 != e1) planMover2++;
            if (n != e1) { mism++; }
            if (e1 != e2) specOnly++;
        }
        Assert.True(hist[4] + hist[5] + hist[6] >= 200 && twoMoves >= 100 && planMover2 >= 150 && specOnly >= 3,
            $"the deep worlds are too shallow: {hist[4] + hist[5] + hist[6]} plans of 3+ steps, {twoMoves} through 2+ movers, {planMover2} where a move changes the length, {specOnly} where gives-then-move would differ");
        Assert.Equal(0, mism);
    }
    [Fact]
    public void The_budget_boundary_is_exact_at_every_budget_up_to_the_full_cost()
    {
        var rng = new Random(99);
        int worlds = 0, solvedW = 0, unsolvedW = 0, bad = 0, checks = 0, skippedExh = 0;
        var plan = new PlanStep[5]; var plan2 = new PlanStep[5];
        for (var i = 0; i < 3000; i++)
        {
            var w = GenerateDeep(rng);
            var pl = new Planner(w.Verbs, w.Slots, 5, R);
            var ts = w.WantThing == 0 ? 0 : w.WantThing - 1;
            var want = new Fact(w.WantThing == 0 ? FactSubject.Self : FactSubject.This, w.WantBit, w.WantValue);
            var n = pl.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, 5, plan);
            if (pl.LastExhausted) { skippedExh++; continue; }
            var full = pl.LastNodes; worlds++;
            if (n > 0) solvedW++; else if (n < 0) unsolvedW++; else continue;
            var steps = plan.Take(Math.Max(n, 0)).ToArray();
            for (var b = 1; b <= Math.Min(full + 2, 40); b++)
            {
                var m = pl.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, 5, plan2, b);
                checks++;
                bool shouldFind = n > 0 && full <= b;
                bool shouldExh = full > b;
                bool ok = shouldExh ? (m == -1 && pl.LastExhausted && pl.LastNodes == b + 1)
                                    : (m == n && !pl.LastExhausted && pl.LastNodes == full && plan2.Take(Math.Max(m, 0)).SequenceEqual(steps));
                if (!ok) { bad++; }
            }
        }
        Assert.Equal(0, bad);
    }

    [Fact]
    public void The_planner_is_deterministic_and_allocates_nothing_with_movers()
    {
        var rng = new Random(5); int bad = 0, n0 = 0;
        var plan = new PlanStep[5]; var plan2 = new PlanStep[5]; var junk = new PlanStep[5];
        for (var i = 0; i < 400; i++)
        {
            var w = GenerateDeep(rng);
            var shared = new Planner(w.Verbs, w.Slots, 5, R); var fresh = new Planner(w.Verbs, w.Slots, 5, R);
            var occ = Enumerable.Range(0, w.Slots).Where(s => w.P.Percept(s).Id != 0).ToArray();
            for (var k = 0; k < 8; k++)
            {
                var s = occ[rng.Next(occ.Length)];
                shared.Plan(w.P, (ulong)rng.Next(4), 0f, 0f, 0f, s, new Fact(FactSubject.This, rng.Next(4) == 0 ? 63 : rng.Next(4), rng.Next(2) == 0), rng.Next(6), junk, rng.Next(3) == 0 ? 3 : 4096);
            }
            var ts = w.WantThing == 0 ? 0 : w.WantThing - 1;
            var want = new Fact(w.WantThing == 0 ? FactSubject.Self : FactSubject.This, w.WantBit, w.WantValue);
            var a = shared.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, 5, plan);
            var b = fresh.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, 5, plan2);
            n0++;
            if (a != b || shared.LastNodes != fresh.LastNodes || shared.LastExhausted != fresh.LastExhausted || !plan.Take(Math.Max(a, 0)).SequenceEqual(plan2.Take(Math.Max(b, 0)))) bad++;
        }
        Assert.Equal(0, bad);

        // allocation: movers, budgets, exhausted calls, success, none
        var ws = Enumerable.Range(0, 40).Select(_ => GenerateDeep(rng)).ToArray();
        var pls = ws.Select(w => new Planner(w.Verbs, w.Slots, 5, R)).ToArray();
        void Round()
        {
            for (var q = 0; q < ws.Length; q++)
            {
                var w = ws[q]; var ts = w.WantThing == 0 ? 0 : w.WantThing - 1;
                var want = new Fact(w.WantThing == 0 ? FactSubject.Self : FactSubject.This, w.WantBit, w.WantValue);
                foreach (var d in Depths) { pls[q].Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, d, plan); pls[q].Plan(w.P, w.SelfFacts, 0f, 0f, 0f, ts, want, d, plan, 7); }
            }
        }
        for (var k = 0; k < 5; k++) Round();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 50; k++) Round();
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
        // LastExhausted after a throw and after a depth-0 return
        var wx = ws[0];
        var px = pls[0];
        try { px.Plan(wx.P, wx.SelfFacts, 0f, 0f, 0f, 0, new Fact(FactSubject.Self, 0, true), 4, plan, 0); } catch (ArgumentOutOfRangeException) { }
        Assert.False(px.LastExhausted);
    }

    [Fact]
    public void The_move_rule_holds_at_other_reach_radii()
    {
        foreach (var r in new[] { 0.5, 2.5 })
        {
            R = r;
            try
            {
                var rng = new Random(31); int mism = 0, exh = 0, tested = 0; var plan = new PlanStep[5];
                for (var i = 0; i < 4000; i++)
                {
                    var w = i % 2 == 0 ? GenerateDeep(rng) : Generate(rng, 2);
                    var all = Groundings(w).ToList(); var e1 = Shallowest(w, all, 1);
                    var pl = new Planner(w.Verbs, w.Slots, w.Depth, r);
                    var n = pl.Plan(w.P, w.SelfFacts, 0f, 0f, 0f, TargetSlot(w), WantOf(w), w.Depth, plan);
                    if (pl.LastExhausted) { exh++; continue; }
                    tested++; if (n != e1) mism++;
                }
                Assert.Equal(0, mism);
            }
            finally { R = 1.0; }
        }
    }

    // M11/M12: a verb that gives This reach false, or Tool reach true, is not a move
    [Fact]
    public void A_verb_that_drops_its_This_reach_or_gives_a_Tool_reach_is_not_a_move()
    {
        const uint A = 1, B = 2;
        Emitter E(ulong id, uint k, float x) => new Emitter(Channel.Visual, x, 0f, 0f, 100f, 1.0, id, k, 0UL);
        Perception Mk(params Emitter[] es)
        {
            var p = new Perception(new NerveParameters(), 0.01, 1, new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0, new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 });
            p.Sense(Zero, Zero, Zero, Array.Empty<BoxStimulus>(), es); return p;
        }
        var plan = new PlanStep[4];
        // (a) Drop gives This reach false (not a move): the knife B stays in reach, Use(B, tool A) goes
        var verbsA = new[]
        {
            new Verb { Id = "Drop", This = new TargetFilter { Kinds = new[] { A } }, Gives = new[] { new Fact(FactSubject.This, 1, true), new Fact(FactSubject.This, Reach, false) } },
            new Verb { Id = "Use", This = new TargetFilter { Kinds = new[] { B } }, Tool = new TargetFilter { Kinds = new[] { A } }, Needs = new[] { new Fact(FactSubject.Tool, 1, true), new Fact(FactSubject.This, Reach, true) }, Gives = new[] { new Fact(FactSubject.This, 2, true) } },
        };
        var pa = Mk(E(10, A, 5f), E(11, B, 0.5f));
        var plA = new Planner(verbsA, 4, 4, 1.0);
        var nA = plA.Plan(pa, 0UL, 0f, 0f, 0f, 1, new Fact(FactSubject.This, 2, true), 4, plan);
        // (b) Fetch gives Tool reach true only (not a move): same shape
        var verbsB = new[]
        {
            new Verb { Id = "Fetch", This = new TargetFilter { Kinds = new[] { B } }, Tool = new TargetFilter { Kinds = new[] { A } }, Gives = new[] { new Fact(FactSubject.Tool, 1, true), new Fact(FactSubject.Tool, Reach, true) } },
            new Verb { Id = "Use", This = new TargetFilter { Kinds = new[] { B } }, Tool = new TargetFilter { Kinds = new[] { A } }, Needs = new[] { new Fact(FactSubject.Tool, 1, true), new Fact(FactSubject.This, Reach, true) }, Gives = new[] { new Fact(FactSubject.This, 2, true) } },
        };
        var plB = new Planner(verbsB, 4, 4, 1.0);
        var nB = plB.Plan(pa, 0UL, 0f, 0f, 0f, 1, new Fact(FactSubject.This, 2, true), 4, plan);
        Assert.Equal(2, nA); Assert.Equal(2, nB);
    }

}
