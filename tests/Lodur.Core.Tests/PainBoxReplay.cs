using Lodur;

namespace Lodur.Core.Tests;

/// <summary>SplitMix64, as the pain box in Bad Day Simulator draws its seeded stream.</summary>
internal struct SplitMix64
{
    private ulong _s;
    public SplitMix64(ulong seed) => _s = seed;

    public double NextSigned()
    {
        var z = _s += 0x9E3779B97F4A7C15ul;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0) * 2.0 - 1.0;
    }
}

/// <summary>
/// The pain box as a Brain: the objective holds the hold pose against the reflex; the release, entered once the
/// will breaks, is a pose behaviour with the withdraw pose as its key and no gains.
/// </summary>
internal static class PainBoxBrain
{
    public static readonly Quat[] Hold = { Quat.Identity, Quat.Identity };
    public static readonly Quat[] Withdraw = { Quat.FromAxisAngle(1f, 0f, 0f, 1f), Quat.FromAxisAngle(0f, 0f, 1f, -1f) };

    public static Brain Make(double pull, HoldParameters? hold = null) => new Brain
    {
        Goals = new[]
        {
            new GoalSpec
            {
                Id = "objective", Priority = 0.0, Pull = pull,
                Behaviour = new PoseBehaviour { Keys = new[] { Hold }, FlinchPose = Withdraw, Hold = hold ?? new HoldParameters() },
            },
            new GoalSpec
            {
                Id = "release", Priority = 1.0, Pull = pull,
                When = new[] { new Condition(DriveQuantity.WillBroken, ConditionOp.AtLeast, 1.0) },
                Behaviour = new PoseBehaviour
                {
                    Keys = new[] { Withdraw }, FlinchPose = Withdraw,
                    Hold = (hold ?? new HoldParameters()) with { FlinchGain = 0.0, StrainShake = 0.0 },
                },
            },
        },
    };
}

/// <summary>
/// Replays the pain box's stimulus for a seed (traits gain then drain, then per tick jitter, ramp, noise) into an
/// Actor whose one region sits inside the box. A small fixture, not a model: all the arithmetic is the core's.
/// </summary>
internal sealed class PainBoxReplay
{
    public const double Dt = 0.01;

    public readonly Actor Actor;
    public readonly BoxStimulus[] Sources = { new BoxStimulus(0f, 0f, 0f, 1f, 1f, 1f) };
    public readonly float[] Strengths = new float[2];
    public readonly Quat[] Targets = new Quat[2];
    public static Quat[] Hold => PainBoxBrain.Hold;
    public static Quat[] Withdraw => PainBoxBrain.Withdraw;

    private readonly float[] _p = { 0f };
    private SplitMix64 _rng;
    private readonly double _intensity;

    public PainBoxReplay(ulong seed, double intensity = 0.8, double pull = 0.5, WithdrawalParameters? withdrawal = null,
        DriveParameters? drive = null, double jitter = 0.2, Brain? brain = null)
    {
        _rng = new SplitMix64(seed);
        _intensity = intensity;
        var gainTrait = 1.0 + jitter * _rng.NextSigned();
        var drainTrait = 1.0 + jitter * _rng.NextSigned();
        Actor = new Actor(Dt, 1, new NerveParameters { Gain = gainTrait }, withdrawal ?? new WithdrawalParameters(),
            (drive ?? new DriveParameters()) with { DrainTrait = drainTrait }, brain ?? PainBoxBrain.Make(pull));
    }

    /// <summary>One tick of the replayed stimulus; the tick index is Actor.TickCount before the call.</summary>
    public TickEvents Tick(bool boxOn = true)
    {
        var tick = Actor.TickCount;
        var noise = _rng.NextSigned();
        var ramp = Math.Min(1.0, tick * Dt / 2.0);
        Sources[0].Intensity = boxOn ? Math.Max(0.0, _intensity * ramp * (1.0 + 0.1 * noise)) : 0.0;
        return Actor.Tick(_p, _p, _p, Sources, Strengths, Targets);
    }

    /// <summary>Runs to GoalChanged or the cap; returns the tick of WillBroke and GoalChanged (-1 if never) and the flinch count.</summary>
    public (long Broke, long Withdrew, int Flinches) Run(double maxSeconds)
    {
        long broke = -1, withdrew = -1;
        var flinches = 0;
        var max = (long)Math.Round(maxSeconds / Dt);
        for (long i = 0; i <= max && withdrew < 0; i++)
        {
            var e = Tick();
            if ((e & TickEvents.Flinch) != 0) flinches++;
            if ((e & TickEvents.WillBroke) != 0) broke = i;
            if ((e & TickEvents.GoalChanged) != 0) withdrew = i;
        }
        return (broke, withdrew, flinches);
    }
}
