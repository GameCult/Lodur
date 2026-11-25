using System;
using Lodur;

namespace Lodur.Core.Tests;

public sealed class PerceptTests
{
    private const double Dt = 0.01;
    private static readonly float[] Zero = { 0f };
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();

    private static Perception Make(int capacity = 4, double memory = 0.05, NerveParameters? senses = null) =>
        new Perception(new NerveParameters(), Dt, 1, senses ?? new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 },
            0, new PerceptParameters { Capacity = capacity, MemorySeconds = memory });

    private static void Sense(Perception p, Emitter[] emitters, double gain = 1.0) => p.Sense(Zero, Zero, Zero, NoBoxes, emitters, gain);

    private static Emitter At(ulong id, float x = 0f, float radius = 2f, double intensity = 0.5, uint kind = 7, Channel channel = Channel.Audio) =>
        new Emitter(channel, x, 0f, 0f, radius, intensity, id, kind);

    private static int SlotOf(Perception p, ulong id)
    {
        for (var s = 0; s < p.PerceptCount; s++)
        {
            if (p.Percept(s).Id == id) return s;
        }

        return -1;
    }

    private static int Occupied(Perception p)
    {
        var n = 0;
        for (var s = 0; s < p.PerceptCount; s++)
        {
            if (p.Percept(s).Id != 0) n++;
        }

        return n;
    }

    [Fact]
    public void A_remembered_id_sensed_with_a_new_kind_takes_the_new_kind()
    {
        var p = Make();
        Sense(p, new[] { At(1, kind: 7) });
        Sense(p, new[] { At(1, kind: 9) });
        Assert.Equal(9u, p.Percept(SlotOf(p, 1)).Kind);
    }

    [Fact]
    public void A_free_slot_is_all_zero_after_forgetting_and_an_anonymous_emitter_cannot_write_into_it()
    {
        var p = Make(capacity: 2, memory: 0.05);
        Sense(p, new[] { At(1, x: 0.5f) });
        for (var i = 0; i < 20; i++) Sense(p, Array.Empty<Emitter>());
        Assert.Equal(0, Occupied(p));
        for (var i = 0; i < 3; i++) Sense(p, new[] { At(0, x: 0.5f) });
        for (var s = 0; s < p.PerceptCount; s++)
        {
            var c = p.Percept(s);
            Assert.Equal(0UL, c.Id);
            Assert.Equal(0u, c.Kind);
            Assert.Equal(0f, c.X);
            Assert.Equal(0f, c.Y);
            Assert.Equal(0f, c.Z);
            Assert.Equal(0.0, c.Salience);
            Assert.Equal(0.0, c.SecondsSinceSensed);
        }
    }

    [Fact]
    public void An_anonymous_emitter_loads_the_nerve_and_makes_no_percept()
    {
        var anonymous = Make();
        var named = Make();
        var emitters = new[] { At(0, 0.5f, 2f, 0.6), At(0, 1f, 3f, 0.4, channel: Channel.Visual) };
        var withIds = new[] { At(11, 0.5f, 2f, 0.6), At(12, 1f, 3f, 0.4, channel: Channel.Visual) };
        for (var i = 0; i < 30; i++)
        {
            Sense(anonymous, emitters);
            Sense(named, withIds);
            Assert.Equal(named.Load, anonymous.Load);
            Assert.Equal(named.ChannelAfferent(Channel.Audio), anonymous.ChannelAfferent(Channel.Audio));
        }

        Assert.True(anonymous.Load > 0.0);
        Assert.Equal(0, Occupied(anonymous));
        Assert.Equal(2, Occupied(named));
    }

    [Fact]
    public void A_thing_is_sensed_strictly_inside_its_radius_and_only_with_gain()
    {
        var p = Make();
        Sense(p, new[] { At(1, x: 2f, radius: 2f) });                  // exactly at the radius
        Assert.Equal(0, Occupied(p));
        Sense(p, new[] { At(1, x: 1.999f, radius: 2f) });
        Assert.Equal(1, Occupied(p));
        Assert.Equal(0.0, p.Percept(SlotOf(p, 1)).SecondsSinceSensed);

        var deaf = Make();
        Sense(deaf, new[] { At(1) }, gain: 0.0);
        Assert.Equal(0, Occupied(deaf));
        Sense(deaf, new[] { At(1) }, gain: 0.25);
        Assert.Equal(1, Occupied(deaf));

        var noRadius = Make();
        Sense(noRadius, new[] { At(1, radius: 0f) });
        Assert.Equal(0, Occupied(noRadius));
    }

    [Fact]
    public void Salience_is_the_largest_received_times_the_gain_and_position_follows_it()
    {
        var p = Make();
        // Received: 0.5 * (1 - 1/2) = 0.25 at x=1; 0.5 * (1 - 0.5/2) = 0.375 at x=0.5; a tied second emitter at x=-0.5 must not take the position.
        Sense(p, new[] { At(5, 1f, 2f, 0.5, kind: 9), At(5, 0.5f, 2f, 0.5, kind: 9), At(5, -0.5f, 2f, 0.5, kind: 9), At(0, 0f, 9f, 9.0) }, gain: 0.5);
        var percept = p.Percept(SlotOf(p, 5));
        Assert.Equal(1, Occupied(p));
        Assert.Equal(0.375 * 0.5, percept.Salience, 12);
        Assert.Equal(0.5f, percept.X);
        Assert.Equal(9u, percept.Kind);

        // A thing that is not sensed this tick keeps its place and has no salience.
        Sense(p, Array.Empty<Emitter>());
        Assert.Equal(0.0, p.Percept(SlotOf(p, 5)).Salience);
        Assert.Equal(0.5f, p.Percept(SlotOf(p, 5)).X);
    }

    [Fact]
    public void An_unsensed_percept_ages_and_is_forgotten_after_the_memory_time()
    {
        var p = Make(memory: Dt);
        Sense(p, new[] { At(3) });
        Sense(p, Array.Empty<Emitter>());
        var s = SlotOf(p, 3);
        Assert.True(s >= 0);                                             // since == memory is still remembered
        Assert.Equal(Dt, p.Percept(s).SecondsSinceSensed);
        Sense(p, Array.Empty<Emitter>());
        Assert.Equal(-1, SlotOf(p, 3));                                  // since > memory frees it

        var instant = Make(memory: 0.0);
        Sense(instant, new[] { At(3) });
        Assert.Equal(1, Occupied(instant));
        Sense(instant, Array.Empty<Emitter>());
        Assert.Equal(0, Occupied(instant));

        // Out of range or deaf, a thing ages the same; sensed again it returns to zero.
        var q = Make(memory: 0.05);
        Sense(q, new[] { At(3) });
        Sense(q, new[] { At(3, x: 5f) });
        Sense(q, new[] { At(3) }, gain: 0.0);
        Assert.Equal(2 * Dt, q.Percept(SlotOf(q, 3)).SecondsSinceSensed, 12);
        Sense(q, new[] { At(3) });
        Assert.Equal(0.0, q.Percept(SlotOf(q, 3)).SecondsSinceSensed);
    }

    [Fact]
    public void A_full_table_evicts_the_longest_unsensed_and_drops_a_newcomer_when_all_are_sensed()
    {
        var p = Make(capacity: 3, memory: 1.0);
        Sense(p, new[] { At(1), At(2), At(3) });
        Assert.Equal(0, SlotOf(p, 1));
        Assert.Equal(1, SlotOf(p, 2));
        Assert.Equal(2, SlotOf(p, 3));

        // 2 and 3 are sensed on, 1 goes unsensed: the newcomer takes slot 0.
        Sense(p, new[] { At(2), At(3), At(4) });
        Assert.Equal(0, SlotOf(p, 4));
        Assert.Equal(1, SlotOf(p, 2));
        Assert.Equal(2, SlotOf(p, 3));
        Assert.Equal(-1, SlotOf(p, 1));

        // 3 has gone unsensed longest (two ticks to 2's one): it is the one evicted, and the others keep their slots.
        Sense(p, new[] { At(2), At(4) });
        Sense(p, new[] { At(4), At(6) });
        Assert.Equal(2, SlotOf(p, 6));
        Assert.Equal(-1, SlotOf(p, 3));
        Assert.Equal(1, SlotOf(p, 2));
        Assert.Equal(0, SlotOf(p, 4));

        // Every slot sensed this tick: the newcomer is dropped, and it stays dropped for every emitter of its id.
        var full = Make(capacity: 2, memory: 1.0);
        Sense(full, new[] { At(1), At(2), At(8), At(8, 0.5f) });
        Assert.Equal(-1, SlotOf(full, 8));
        Assert.Equal(0, SlotOf(full, 1));
        Assert.Equal(1, SlotOf(full, 2));

        // Equal time unsensed: the lowest slot goes.
        var tie = Make(capacity: 2, memory: 1.0);
        Sense(tie, new[] { At(1), At(2) });
        Sense(tie, new[] { At(9) });
        Assert.Equal(0, SlotOf(tie, 9));
        Assert.Equal(1, SlotOf(tie, 2));
    }

    [Fact]
    public void Sense_refuses_a_phantom_id_and_a_kind_disagreement_before_stepping()
    {
        var p = Make();
        Sense(p, new[] { At(1, intensity: 0.6) });
        var load = p.Load;
        var since = p.Percept(0).SecondsSinceSensed;
        var afferent = p.ChannelStimulus(Channel.Audio);

        Assert.Throws<ArgumentException>(() => Sense(p, new[] { At(1), At(Perception.PhantomIdBit | 2UL) }));
        Assert.Throws<ArgumentException>(() => Sense(p, new[] { At(ulong.MaxValue) }));
        Assert.Throws<ArgumentException>(() => Sense(p, new[] { At(1, kind: 7), At(2), At(1, kind: 8) }));
        Assert.Equal(load, p.Load);
        Assert.Equal(since, p.Percept(0).SecondsSinceSensed);
        Assert.Equal(afferent, p.ChannelStimulus(Channel.Audio));

        // The state did not step: the next good tick is the second, not the fourth.
        Sense(p, Array.Empty<Emitter>());
        Assert.Equal(Dt, p.Percept(0).SecondsSinceSensed);

        // Anonymous emitters may share kinds freely, and one id may speak on several channels.
        Sense(p, new[] { At(0, kind: 1), At(0, kind: 2), At(4, kind: 3), At(4, kind: 3, channel: Channel.Visual) });
    }

    [Fact]
    public void Percept_parameters_are_validated()
    {
        static void Build(PerceptParameters pp) =>
            _ = new Perception(new NerveParameters(), Dt, 1, new NerveParameters(), 0, pp);

        Build(new PerceptParameters { Capacity = 1, MemorySeconds = 0.0 });
        Build(new PerceptParameters { Capacity = 256 });
        Assert.Throws<ArgumentException>(() => Build(new PerceptParameters { Capacity = 0 }));
        Assert.Throws<ArgumentException>(() => Build(new PerceptParameters { Capacity = 257 }));
        Assert.Throws<ArgumentException>(() => Build(new PerceptParameters { MemorySeconds = -0.001 }));
        Assert.Throws<ArgumentException>(() => Build(new PerceptParameters { MemorySeconds = double.NaN }));
        Assert.Throws<ArgumentException>(() => Build(new PerceptParameters { MemorySeconds = double.PositiveInfinity }));
        Assert.Equal(16, new PerceptParameters().Capacity);
        Assert.Equal(5.0, new PerceptParameters().MemorySeconds);

        // No senses, no percepts, whatever the parameters.
        var deaf = new Perception(new NerveParameters(), Dt, 1);
        Assert.Equal(0, deaf.PerceptCount);
        Assert.Equal(4, Make().PerceptCount);
    }

    [Fact]
    public void Sensing_with_percepts_allocates_nothing()
    {
        var p = Make(capacity: 4, memory: 0.05);
        var near = new[] { At(1), At(2), At(3), At(4), At(5), At(5, 0.5f), At(0, 0.2f) };
        var far = new[] { At(1, x: 9f), At(6) };
        for (var i = 0; i < 50; i++) Sense(p, i % 2 == 0 ? near : far);   // warm, and churn the table
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Sense(p, i % 3 == 0 ? near : far);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void The_actor_passes_percepts_through()
    {
        var nerve = new NerveParameters();
        var senses = new NerveParameters { SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 };
        var actor = new Actor(Dt, 1, nerve, new WithdrawalParameters(), new DriveParameters(), PainBoxBrain.Make(0.5),
            senses, 0, new PerceptParameters { Capacity = 3, MemorySeconds = 1.0 });
        Assert.Equal(3, actor.PerceptCount);

        var strengths = new float[2];
        var targets = new Quat[2];
        actor.Tick(Zero, Zero, Zero, NoBoxes, strengths, targets, new[] { At(42, 0.5f, 2f, 0.8, kind: 5) });
        var seen = actor.Percept(0);
        Assert.Equal(42UL, seen.Id);
        Assert.Equal(5u, seen.Kind);
        Assert.Equal(0.5f, seen.X);
        Assert.True(seen.Salience > 0.0);
        Assert.Equal(0UL, actor.Percept(1).Id);

        var deaf = new Actor(Dt, 1, nerve, new WithdrawalParameters(), new DriveParameters(), PainBoxBrain.Make(0.5));
        Assert.Equal(0, deaf.PerceptCount);
    }
}
