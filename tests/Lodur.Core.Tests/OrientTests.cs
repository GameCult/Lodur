using System;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>The orient layers: Look, Arms (Reach, WardOff, Present) and Recoil composed onto a pose, joint targets only.</summary>
public sealed class OrientTests
{
    private const double Dt = 0.01;
    private const uint Thing = 7;
    private static readonly BodyPlan Plan = BodyPlans.Humanoid();
    private static readonly int Joints = Plan.Joints.Count;
    private static readonly int Torso = Plan.PartIndex("Torso");
    private static readonly int Head = Plan.PartIndex("Head");
    private static readonly int Neck = Plan.JointIndex("Neck");
    private static readonly BoxStimulus[] NoBoxes = Array.Empty<BoxStimulus>();

    private static (float X, float Y, float Z) Add((float X, float Y, float Z) a, (float X, float Y, float Z) b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static (float X, float Y, float Z) Sub((float X, float Y, float Z) a, (float X, float Y, float Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static float Len((float X, float Y, float Z) a) => MathF.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);

    private static (float X, float Y, float Z) Rot(Quat q, (float X, float Y, float Z) v)
    {
        q.Rotate(v.X, v.Y, v.Z, out var x, out var y, out var z);
        return (x, y, z);
    }

    /// <summary>The creation pose of every part: where an unmoved body is.</summary>
    private static PartPose[] Rest()
    {
        var poses = new PartPose[Plan.Parts.Count];
        for (var p = 0; p < poses.Length; p++)
        {
            var d = Plan.Parts[p];
            poses[p] = new PartPose(d.Rotation, d.Position.X, d.Position.Y, d.Position.Z);
        }

        return poses;
    }

    /// <summary>The unmoved body with the torso turned about y and moved and the head carried with it, so a dropped conjugate or a swapped frame shows.</summary>
    private static PartPose[] Turned()
    {
        var poses = Rest();
        var rot = Quat.FromAxisAngle(0f, 1f, 0f, 0.3f);
        var c = Plan.Parts[Torso].Position;
        var shift = (X: 0.05f, Y: -0.02f, Z: 0.1f);
        poses[Torso] = new PartPose(rot, c.X + shift.X, c.Y + shift.Y, c.Z + shift.Z);
        var h = Plan.Parts[Head].Position;
        var carried = Rot(rot, Sub(h, c));
        poses[Head] = new PartPose(rot, c.X + shift.X + carried.X, c.Y + shift.Y + carried.Y, c.Z + shift.Z + carried.Z);
        return poses;
    }

    /// <summary>A key pose with a distinct rotation per joint, so a layer that touches a joint it should not shows.</summary>
    private static Quat[] Key()
    {
        var key = new Quat[Joints];
        for (var m = 0; m < Joints; m++) key[m] = Quat.FromAxisAngle(0f, 0f, 1f, 0.02f * (m + 1));
        return key;
    }

    private static Quat[] Identity() { var k = new Quat[Joints]; Array.Fill(k, Quat.Identity); return k; }

    private static PoseBehaviour Behaviour(LookLayer? look = null, ArmLayer? arms = null, RecoilLayer? recoil = null, HoldParameters? hold = null) => new()
    {
        Keys = new[] { Key() }, FlinchPose = Identity(), Look = look, Arms = arms, Recoil = recoil, Hold = hold ?? new HoldParameters(),
    };

    private static Brain BrainOf(PoseBehaviour b) => new() { Goals = new[] { new GoalSpec { Id = "g", Behaviour = b } } };

    private static OrientRig RigOf(PoseBehaviour b) => new(Plan, BrainOf(b), Dt);

    private static Percept At((float X, float Y, float Z) p, ulong id = 1) => new(id, Thing, p.X, p.Y, p.Z, 1.0, 0.0);

    private static Quat[] Run(PoseBehaviour b, PartPose[] parts, Percept target, Side arm = Side.Right, double weight = 1.0, double leak = 0.0, float[]? strengths = null)
    {
        var targets = new Quat[Joints];
        var rig = RigOf(b);
        b.Write(weight, leak, 0.0, 0.0, 0.0, strengths ?? new float[Joints], targets, new OrientInput(rig, parts, target, arm));
        return targets;
    }

    /// <summary>What the layerless behaviour writes with the same keys and flinch.</summary>
    private static Quat[] Base(PoseBehaviour b) => Run(b with { Look = null, Arms = null, Recoil = null }, Rest(), default);

    private static void Same(Quat a, Quat b, float tol = 1e-6f)
    {
        var dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        Assert.True(dot > 1f - tol, $"{a} is not {b} (dot {dot})");
    }

    private static void SameBits(Quat a, Quat b) => Assert.Equal(a, b);

    private static Quat Flinched(Quat q, Quat flinch, float w) => Quat.Nlerp(q, flinch, w);

    private static (float X, float Y, float Z) Wrist(ArmChain arm, PartPose parent, Quat shoulder, Quat elbow)
    {
        var fs = Ik.JointFrame(Plan.Joints[arm.ShoulderJoint]);
        var fe = Ik.JointFrame(Plan.Joints[arm.ElbowJoint]);
        var rs = fs * shoulder * fs.Conjugate();
        var re = fe * elbow * fe.Conjugate();
        var a = Sub(arm.ElbowAnchor, arm.ShoulderAnchor);
        var b = Sub(arm.WristAnchor, arm.ElbowAnchor);
        var local = Add(arm.ShoulderAnchor, Rot(rs, Add(a, Rot(re, b))));
        var def = Plan.Parts[arm.ParentPart];
        var rel = parent.Rotation * def.Rotation.Conjugate();
        return Add((parent.X, parent.Y, parent.Z), Rot(rel, Sub(local, def.Position)));
    }

    private static readonly (float X, float Y, float Z) Ahead = (0.2f, 1.45f, 0.6f);

    [Fact]
    public void A_layered_behaviour_with_no_target_writes_the_base_and_only_Present_still_writes()
    {
        var look = new LookLayer();
        var reach = new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Both };
        var recoil = Recoiling();
        var layered = Behaviour(look, reach, recoil);
        var baseline = Base(layered);
        var got = Run(layered, Rest(), default);
        for (var m = 0; m < Joints; m++) SameBits(baseline[m], got[m]);

        var present = Behaviour(arms: new ArmLayer { Mode = ArmMode.Present, Side = ArmSide.Both, Point = (-0.2f, 1.3f, 0.3f) });
        var written = Run(present, Rest(), default);
        var arm = new[] { Plan.JointIndex("Shoulder", Side.Left), Plan.JointIndex("Elbow", Side.Left), Plan.JointIndex("Shoulder", Side.Right), Plan.JointIndex("Elbow", Side.Right) };
        for (var m = 0; m < Joints; m++)
        {
            if (Array.IndexOf(arm, m) >= 0) Assert.NotEqual(Base(present)[m], written[m]);
            else SameBits(Base(present)[m], written[m]);
        }
    }

    [Fact]
    public void Look_solves_the_neck_and_blends_by_weight()
    {
        var look = LookChain.Of(Plan);
        foreach (var parts in new[] { Rest(), Turned() })
        {
            var t = parts[Torso];
            Assert.True(look.Solve(t.Rotation, t.X, t.Y, t.Z, Ahead.X, Ahead.Y, Ahead.Z, out var solved));
            var b1 = Behaviour(new LookLayer { Weight = 1.0 });
            var baseline = Base(b1);
            var full = Run(b1, parts, At(Ahead));
            SameBits(Flinched(solved, Quat.Identity, 0f), full[Neck]);
            var half = Run(Behaviour(new LookLayer { Weight = 0.5 }), parts, At(Ahead));
            SameBits(Flinched(Quat.Nlerp(Key()[Neck], solved, 0.5f), Quat.Identity, 0f), half[Neck]);
            var none = Run(Behaviour(new LookLayer { Weight = 0.0 }), parts, At(Ahead));
            Same(baseline[Neck], none[Neck]);
            for (var m = 0; m < Joints; m++)
            {
                if (m != Neck) SameBits(baseline[m], full[m]);
            }
        }
    }

    [Theory]
    [InlineData(ArmSide.Left)]
    [InlineData(ArmSide.Right)]
    [InlineData(ArmSide.Both)]
    public void Reach_solves_the_chosen_arm_and_leaves_the_other(ArmSide side)
    {
        var parts = Turned();
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = side });
        var baseline = Base(b);
        var got = Run(b, parts, At(Ahead));
        var t = parts[Torso];
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            var arm = ArmChain.Of(Plan, s, Dt);
            var used = side == ArmSide.Both || (side == ArmSide.Left) == (s == Side.Left);
            if (used)
            {
                arm.Solve(t.Rotation, t.X, t.Y, t.Z, Ahead.X, Ahead.Y, Ahead.Z, out var sh, out var el);
                SameBits(Flinched(sh, Quat.Identity, 0f), got[arm.ShoulderJoint]);
                SameBits(Flinched(el, Quat.Identity, 0f), got[arm.ElbowJoint]);
            }
            else
            {
                SameBits(baseline[arm.ShoulderJoint], got[arm.ShoulderJoint]);
                SameBits(baseline[arm.ElbowJoint], got[arm.ElbowJoint]);
            }
        }

        for (var m = 0; m < Joints; m++)
        {
            if (Plan.Joints[m].Label is not ("Shoulder" or "Elbow")) SameBits(baseline[m], got[m]);
        }
    }

    [Fact]
    public void Nearer_uses_the_arm_the_actor_holds()
    {
        var parts = Rest();
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Nearer });
        var baseline = Base(b);
        var left = Plan.JointIndex("Shoulder", Side.Left);
        var right = Plan.JointIndex("Shoulder", Side.Right);
        var asLeft = Run(b, parts, At(Ahead), Side.Left);
        Assert.NotEqual(baseline[left], asLeft[left]);
        SameBits(baseline[right], asLeft[right]);
        var asRight = Run(b, parts, At(Ahead), Side.Right);
        Assert.NotEqual(baseline[right], asRight[right]);
        SameBits(baseline[left], asRight[left]);
    }

    [Fact]
    public void WardOff_puts_the_wrists_between_head_and_target()
    {
        var parts = Turned();
        var distance = 0.3f;
        var spread = 0.15f;
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Side = ArmSide.Both, Distance = distance, Spread = spread });
        var target = (0.0f, 1.1f, 1.2f);
        var got = Run(b, parts, At(target));
        var h = parts[Head];
        var head = (h.X, h.Y, h.Z);
        var u = Sub(target, head);
        u = (u.X / Len(u), u.Y / Len(u), u.Z / Len(u));
        var meet = Add(head, (u.X * distance, u.Y * distance, u.Z * distance));
        var t = parts[Torso];
        var leftAxis = Rot(t.Rotation * Plan.Parts[Torso].Rotation.Conjugate(), (1f, 0f, 0f));
        var wristL = Wrist(ArmChain.Of(Plan, Side.Left, Dt), t, Unflinch(got, Side.Left, out var elL), elL);
        var wristR = Wrist(ArmChain.Of(Plan, Side.Right, Dt), t, Unflinch(got, Side.Right, out var elR), elR);
        Assert.True(Len(Sub(wristL, meet)) < spread + 1e-3f, $"left wrist {Len(Sub(wristL, meet))} m from the meeting point");
        Assert.True(Len(Sub(wristR, meet)) < spread + 1e-3f, $"right wrist {Len(Sub(wristR, meet))} m from the meeting point");
        // Either side of the line, the left toward the torso's left: the across sign is what separates these from each other.
        var leftSide = Sub(wristL, meet);
        var rightSide = Sub(wristR, meet);
        Assert.True(leftSide.X * leftAxis.X + leftSide.Y * leftAxis.Y + leftSide.Z * leftAxis.Z > 0.05f);
        Assert.True(rightSide.X * leftAxis.X + rightSide.Y * leftAxis.Y + rightSide.Z * leftAxis.Z < -0.05f);
    }

    // The targets the layer wrote for one arm (the flinch blend is zero here, so they are the solution).
    private static Quat Unflinch(Quat[] targets, Side side, out Quat elbow)
    {
        var arm = ArmChain.Of(Plan, side, Dt);
        elbow = targets[arm.ElbowJoint];
        return targets[arm.ShoulderJoint];
    }

    [Fact]
    public void Present_ignores_the_target_and_the_left_arm_mirrors_the_right_in_x()
    {
        var parts = Rest();
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Present, Side = ArmSide.Both, Point = (-0.2f, 1.3f, 0.3f) });
        var without = Run(b, parts, default);
        var with = Run(b, parts, At((3f, 1f, -2f)));
        for (var m = 0; m < Joints; m++) SameBits(without[m], with[m]);
        var t = parts[Torso];
        var wl = Wrist(ArmChain.Of(Plan, Side.Left, Dt), t, Unflinch(with, Side.Left, out var el), el);
        var wr = Wrist(ArmChain.Of(Plan, Side.Right, Dt), t, Unflinch(with, Side.Right, out var er), er);
        Assert.True(MathF.Abs(wr.X - -0.2f) < 2e-3f && MathF.Abs(wr.Y - 1.3f) < 2e-3f && MathF.Abs(wr.Z - 0.3f) < 2e-3f, $"right wrist {wr}");
        Assert.True(MathF.Abs(wl.X + wr.X) < 1e-4f && MathF.Abs(wl.Y - wr.Y) < 1e-4f && MathF.Abs(wl.Z - wr.Z) < 1e-4f, $"left {wl} does not mirror right {wr}");
    }

    private static Quat[] Away(float angle, float x, float y, float z)
    {
        var q = new Quat[Joints];
        for (var m = 0; m < Joints; m++) q[m] = Quat.FromAxisAngle(x, y, z, angle * (1f + 0.1f * m));
        return q;
    }

    private static RecoilLayer Recoiling() => new()
    {
        AwayLeft = Away(0.5f, 1f, 0f, 0f), AwayRight = Away(0.5f, 0f, 1f, 0f),
        Proximity = new Consideration(ConsiderationInput.Distance, 0.2, 2.0, 1.0, 0.0),
    };

    // A target `r` metres from the head centre at lateral coordinate s in the torso frame (+ is the actor's left), level with the head.
    private static (float X, float Y, float Z) Bearing(float s, float r)
    {
        var h = Plan.Parts[Head].Position;
        return (h.X + s * r, h.Y, h.Z + MathF.Sqrt(1f - s * s) * r);
    }

    [Fact]
    public void Recoil_blends_by_bearing_and_is_continuous_across_the_midline()
    {
        var recoil = Recoiling();
        var b = Behaviour(recoil: recoil);
        var baseline = Base(b);
        var depth = (float)recoil.Proximity.Response(0.5);
        Assert.InRange(depth, 0.5f, 0.99f);

        var far = Run(b, Rest(), At(Bearing(0.6f, 3f)));   // past the curve's far end: depth 0
        for (var m = 0; m < Joints; m++) Same(baseline[m], far[m]);

        var leftward = Run(b, Rest(), At(Bearing(1f, 0.5f)));
        var rightward = Run(b, Rest(), At(Bearing(-1f, 0.5f)));
        var ahead = Run(b, Rest(), At(Bearing(0f, 0.5f)));
        var key = Key();
        for (var m = 0; m < Joints; m++)
        {
            Same(Flinched(Quat.Nlerp(key[m], recoil.Left[m], depth), Quat.Identity, 0f), leftward[m]);
            Same(Flinched(Quat.Nlerp(key[m], recoil.Right[m], depth), Quat.Identity, 0f), rightward[m]);
            var half = Quat.Nlerp(Quat.Nlerp(key[m], recoil.Left[m], depth * 0.5f), recoil.Right[m], depth * 0.5f);
            Same(Flinched(half, Quat.Identity, 0f), ahead[m]);
        }

        var a = Run(b, Rest(), At(Bearing(1e-4f, 0.5f)));
        var c = Run(b, Rest(), At(Bearing(-1e-4f, 0.5f)));
        for (var m = 0; m < Joints; m++)
        {
            Same(a[m], ahead[m], 1e-5f);
            Same(c[m], ahead[m], 1e-5f);
        }
    }

    [Fact]
    public void Recoil_applies_before_look_and_arms_so_a_partial_look_or_reach_blends_from_the_recoiled_pose()
    {
        var recoil = Recoiling();
        var b = Behaviour(new LookLayer { Weight = 0.5 }, new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Right, Weight = 0.5 }, recoil);
        var parts = Rest();
        var target = Bearing(0.5f, 0.5f);
        var got = Run(b, parts, At(target));

        var depth = (float)recoil.Proximity.Response(0.5);
        var key = Key();
        var recoiled = new Quat[Joints];
        for (var m = 0; m < Joints; m++) recoiled[m] = Quat.Nlerp(Quat.Nlerp(key[m], recoil.Left[m], depth * 0.75f), recoil.Right[m], depth * 0.25f);

        var t = parts[Torso];
        LookChain.Of(Plan).Solve(t.Rotation, t.X, t.Y, t.Z, target.X, target.Y, target.Z, out var neck);
        var arm = ArmChain.Of(Plan, Side.Right, Dt);
        var p = parts[arm.ParentPart];
        arm.Solve(p.Rotation, p.X, p.Y, p.Z, target.X, target.Y, target.Z, out var shoulder, out var elbow);
        for (var m = 0; m < Joints; m++)
        {
            var want = m == Neck ? Quat.Nlerp(recoiled[m], neck, 0.5f)
                : m == arm.ShoulderJoint ? Quat.Nlerp(recoiled[m], shoulder, 0.5f)
                : m == arm.ElbowJoint ? Quat.Nlerp(recoiled[m], elbow, 0.5f)
                : recoiled[m];
            Same(want, got[m], 1e-5f);
        }
    }

    [Fact]
    public void The_layers_treat_a_phantom_target_as_they_treat_a_real_one_at_the_same_place()
    {
        // Stare-at-hands is Look and Reach on a phantom at the actor's own hand: the layers read the percept's place, never its id.
        var b = Behaviour(new LookLayer(), new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Both }, Recoiling());
        foreach (var parts in new[] { Rest(), Turned() })
        {
            var real = Run(b, parts, At(Ahead, 1UL));
            var phantom = Run(b, parts, At(Ahead, Perception.PhantomIdBit | 1UL));
            for (var m = 0; m < Joints; m++) SameBits(real[m], phantom[m]);
            Assert.NotEqual(Base(b)[Neck], phantom[Neck]);
        }
    }

    [Fact]
    public void The_flinch_applies_after_the_layers_and_strength_stays_the_pose_rule()
    {
        var parts = Rest();
        var hold = new HoldParameters { HoldStrength = 0.8f, FlinchGain = 0.3 };
        var flinchPose = Away(0.7f, 0f, 0f, 1f);
        var b = Behaviour(new LookLayer(), new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Both }, Recoiling(), hold) with { FlinchPose = flinchPose };
        var tgt = At(Ahead);
        var quiet = Run(b, parts, tgt);
        foreach (var weight in new[] { 0.0, 0.25, 0.5, 1.0 })
        {
            foreach (var leak in new[] { 0.0, 0.4, 1.0 })
            {
                var strengths = new float[Joints];
                var moved = Run(b, parts, tgt, weight: weight, leak: leak, strengths: strengths);
                var w = (float)b.Blend(leak, 0.0, 0.0);
                var want = hold.HoldStrength * (float)Math.Min(1.0, weight + leak * hold.FlinchGain);
                for (var m = 0; m < Joints; m++)
                {
                    Assert.Equal(want, strengths[m]);
                    // The flinch is the last blend, over the layered pose and for every joint, the solved shoulder included.
                    var layered = Run(b with { FlinchPose = Identity(), Hold = hold with { FlinchGain = 0.0 } }, parts, tgt)[m];
                    Same(Quat.Nlerp(layered, flinchPose[m], w), moved[m], 1e-5f);
                }
            }
        }

        var shoulder = Plan.JointIndex("Shoulder", Side.Left);
        var leaking = Run(b, parts, tgt, leak: 1.0);
        Assert.NotEqual(quiet[shoulder], leaking[shoulder]);
    }

    [Fact]
    public void Validation_names_the_goal()
    {
        static void Refuses(PoseBehaviour b) => Assert.Contains("Goal g:", Assert.Throws<ArgumentException>(() => b.Validate("g")).Message);
        var good = Behaviour();
        good.Validate("g");
        foreach (var weight in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
        {
            Refuses(Behaviour(look: new LookLayer { Weight = weight }));
            Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Weight = weight }));
        }

        Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Distance = 0f, Spread = 0.1f }));
        Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Distance = -0.3f, Spread = 0.1f }));
        Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Distance = 0.3f, Spread = -0.01f }));
        Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Distance = float.NaN, Spread = 0.1f }));
        Refuses(Behaviour(arms: new ArmLayer { Mode = ArmMode.Present, Side = ArmSide.Nearer }));
        Behaviour(arms: new ArmLayer { Mode = ArmMode.Present, Side = ArmSide.Both }).Validate("g");
        Behaviour(arms: new ArmLayer { Mode = ArmMode.WardOff, Distance = 0.3f, Spread = 0f }).Validate("g");

        var recoil = Recoiling();
        Behaviour(recoil: recoil).Validate("g");
        Refuses(Behaviour(recoil: recoil with { AwayLeft = new Quat[Joints - 1] }));
        Refuses(Behaviour(recoil: recoil with { AwayRight = new Quat[Joints + 1] }));
        Refuses(Behaviour(recoil: recoil with { Proximity = recoil.Proximity with { Input = ConsiderationInput.Salience } }));
        Refuses(Behaviour(recoil: new RecoilLayer { AwayLeft = Identity(), AwayRight = Identity() }));
    }

    [Fact]
    public void A_layered_behaviour_refuses_to_write_without_the_orient_input()
    {
        var b = Behaviour(new LookLayer());
        Assert.Throws<ArgumentException>(() => b.Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[Joints], new Quat[Joints]));
        Assert.True(b.HasLayers);
        Assert.False(Behaviour().HasLayers);
        Behaviour().Write(1.0, 0.0, 0.0, 0.0, 0.0, new float[Joints], new Quat[Joints]);
    }

    [Fact]
    public void Layers_copy_their_poses_and_read_back_read_only()
    {
        var left = Away(0.5f, 1f, 0f, 0f);
        var recoil = new RecoilLayer { AwayLeft = left, AwayRight = Identity(), Proximity = new Consideration(ConsiderationInput.Distance, 0, 1, 1, 0) };
        var was = recoil.AwayLeft[0];
        left[0] = Quat.Identity;
        Assert.Equal(was, recoil.AwayLeft[0]);
        Assert.False(recoil.AwayLeft is Quat[]);
    }

    // The Actor: targets come from the percepts, the layers from the goal's behaviour, the poses from the caller.

    private static Emitter Emit(ulong id, (float X, float Y, float Z) p) =>
        new() { Channel = Channel.Audio, X = p.X, Y = p.Y, Z = p.Z, Radius = 40f, Intensity = 1.0, Id = id, Kind = Thing };

    private static Brain Watching(PoseBehaviour layered) => new()
    {
        Goals = new[]
        {
            new GoalSpec { Id = "idle", Priority = 0.0, Behaviour = Behaviour() },
            new GoalSpec
            {
                Id = "watch", Priority = 1.0, Target = new TargetFilter { Kinds = new[] { Thing } },
                Considerations = new[] { new Consideration(ConsiderationInput.Distance, 0.0, 60.0, 1.0, 0.0) },
                Behaviour = layered,
            },
        },
    };

    private static Actor Watcher(Brain brain, BodyPlan? body) =>
        new(Dt, 1, new NerveParameters(), new WithdrawalParameters(), new DriveParameters(), brain,
            new NerveParameters { Threshold = 0.05, SensitisationGrowth = 0.0, ConductionDelaySeconds = 0.0 }, 0,
            new PerceptParameters { Capacity = 4, MemorySeconds = 5.0 }, body: body);

    private static readonly float[] Origin = { 0f };

    private static TickEvents Step(Actor a, PartPose[] parts, Quat[] targets, params Emitter[] emitters) =>
        a.Tick(Origin, Origin, Origin, NoBoxes, new float[Joints], targets, emitters, parts: parts);

    [Fact]
    public void Nearer_holds_one_arm_per_pair_and_a_recommit_picks_again()
    {
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Nearer });
        var a = Watcher(Watching(b), Plan);
        var parts = Rest();
        var targets = new Quat[Joints];
        var baseline = Base(b);
        var left = Plan.JointIndex("Shoulder", Side.Left);
        var right = Plan.JointIndex("Shoulder", Side.Right);
        var onLeft = (0.9f, 1.5f, 0.5f);
        var onRight = (-0.9f, 1.5f, 0.5f);

        for (var i = 0; i < 40; i++) Step(a, parts, targets, Emit(1, onLeft));
        Assert.Equal(1UL, a.TargetId);
        Assert.NotEqual(baseline[left], targets[left]);
        Same(baseline[right], targets[right]);

        // The same thing moves to the right: the pair is the same pair, so the arm stays.
        for (var i = 0; i < 40; i++) Step(a, parts, targets, Emit(1, onRight));
        Assert.Equal(1UL, a.TargetId);
        Assert.NotEqual(baseline[left], targets[left]);
        Same(baseline[right], targets[right]);

        // Another thing, nearer and on the right, is another pair: it commits a latency later and picks the nearer arm again.
        for (var i = 0; i < 60; i++) Step(a, parts, targets, Emit(1, onRight), Emit(2, (-0.5f, 1.5f, 0.3f)));
        Assert.Equal(2UL, a.TargetId);
        Assert.NotEqual(baseline[right], targets[right]);
        Same(baseline[left], targets[left]);
    }

    [Theory]
    [InlineData(Side.Right)]
    [InlineData(Side.Left)]
    public void ResetArms_makes_both_arms_solve_the_stateless_rule_again(Side side)
    {
        // Reach with both arms along the feasible tie of one arm's shoulder (goal (dx, -0.25, -0.25) from it, mirrored for the left), from
        // dx -10 mm to 2 mm past the tie: that arm holds its basin, away from the stateless target. After ResetArms the next tick writes the
        // stateless solve for both arms, bit for bit.
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Both });
        var a = Watcher(Watching(b), Plan);
        var parts = Rest();
        var targets = new Quat[Joints];
        var arms = new[] { ArmChain.Of(Plan, Side.Left, Dt), ArmChain.Of(Plan, Side.Right, Dt) };
        var tie = side == Side.Left ? arms[0] : arms[1];
        var sign = side == Side.Left ? -1f : 1f;
        var s = tie.ShoulderAnchor;
        (float X, float Y, float Z) Goal(float dx) => (s.X + sign * dx, s.Y - 0.25f, s.Z - 0.25f);
        for (var i = 0; i < 40; i++) Step(a, parts, targets, Emit(1, Goal(-0.01f)));
        Assert.Equal(1UL, a.TargetId);
        for (var i = -50; i <= 10; i++) Step(a, parts, targets, Emit(1, Goal(i * 0.0002f)));
        var held = (Quat[])targets.Clone();
        a.ResetArms();
        Step(a, parts, targets, Emit(1, Goal(0.002f)));

        var t = a.Target;
        Assert.Equal(Goal(0.002f), (t.X, t.Y, t.Z));
        foreach (var arm in arms)
        {
            var p = parts[arm.ParentPart];
            arm.Solve(p.Rotation, p.X, p.Y, p.Z, t.X, t.Y, t.Z, out var shoulder, out var elbow);
            SameBits(Flinched(shoulder, Quat.Identity, 0f), targets[arm.ShoulderJoint]);
            SameBits(Flinched(elbow, Quat.Identity, 0f), targets[arm.ElbowJoint]);
        }

        var held2 = held[tie.ShoulderJoint];
        var reset = targets[tie.ShoulderJoint];
        var turn = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(held2.X * reset.X + held2.Y * reset.Y + held2.Z * reset.Z + held2.W * reset.W)));
        Assert.True(turn > 1f, $"the held {side} arm was only {turn} rad from the stateless target, so the reset proves nothing");
    }

    [Fact]
    public void A_tie_goes_to_the_right_arm()
    {
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Nearer });
        var a = Watcher(Watching(b), Plan);
        var targets = new Quat[Joints];
        var baseline = Base(b);
        for (var i = 0; i < 40; i++) Step(a, Rest(), targets, Emit(1, (0f, 1.5f, 0.8f)));
        Assert.Equal(1UL, a.TargetId);
        Assert.NotEqual(baseline[Plan.JointIndex("Shoulder", Side.Right)], targets[Plan.JointIndex("Shoulder", Side.Right)]);
        Same(baseline[Plan.JointIndex("Shoulder", Side.Left)], targets[Plan.JointIndex("Shoulder", Side.Left)]);
    }

    [Fact]
    public void The_actor_gives_the_layers_the_committed_target_and_the_callers_poses()
    {
        var b = Behaviour(look: new LookLayer());
        var a = Watcher(Watching(b), Plan);
        var parts = Turned();
        var targets = new Quat[Joints];
        for (var i = 0; i < 40; i++) Step(a, parts, targets, Emit(1, Ahead));
        var t = parts[Torso];
        LookChain.Of(Plan).Solve(t.Rotation, t.X, t.Y, t.Z, Ahead.X, Ahead.Y, Ahead.Z, out var solved);
        SameBits(Flinched(solved, Quat.Identity, 0f), targets[Neck]);
        Assert.Same(Plan, a.Body);
    }

    [Fact]
    public void Construction_refuses_what_the_layers_cannot_stand_on()
    {
        var layered = Behaviour(new LookLayer());
        var noBody = Assert.Throws<ArgumentException>(() => Watcher(Watching(layered), null));
        Assert.Contains("Goal watch", noBody.Message);

        // One muscle too few for the body.
        var short3 = new PoseBehaviour { Keys = new[] { new Quat[3] { Quat.Identity, Quat.Identity, Quat.Identity } }, FlinchPose = new Quat[3] { Quat.Identity, Quat.Identity, Quat.Identity }, Look = new LookLayer() };
        var brain = new Brain { Goals = new[] { new GoalSpec { Id = "short", Behaviour = short3 } } };
        Assert.Contains("Goal short", Assert.Throws<ArgumentException>(() => Watcher(brain, Plan)).Message);

        // Layers on goals that none use build without a body when no goal is layered.
        Watcher(Watching(Behaviour()), null);
    }

    private static BodyPlan WithJoint(string label, Side side, Func<JointDef, JointDef> change)
    {
        var joints = new JointDef[Joints];
        for (var j = 0; j < Joints; j++) joints[j] = Plan.Joints[j];
        var i = Plan.JointIndex(label, side);
        joints[i] = change(joints[i]);
        return new BodyPlan(Plan.Parts, joints, Plan.SensePoints, Plan.AirwayOpenings, Plan.AirwaySumps);
    }

    [Fact]
    public void An_arm_the_closed_form_cannot_solve_is_refused_at_construction_naming_goal_and_side()
    {
        var b = Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Both });

        // Bent at creation: the elbow anchor off the shoulder-to-wrist line.
        var bent = WithJoint("Elbow", Side.Right, j => j with { Anchor = (j.Anchor.X - 0.08f, j.Anchor.Y, j.Anchor.Z + 0.05f) });
        var bentError = Assert.Throws<ArgumentException>(() => Watcher(Watching(b), bent)).Message;
        Assert.Contains("Goal watch", bentError);
        Assert.Contains("Right", bentError);
        Assert.DoesNotContain("Left", bentError);

        // An elbow range straddling zero.
        var straddle = WithJoint("Elbow", Side.Left, j => j with { Lower = -1f, Upper = 1f });
        var straddleError = Assert.Throws<ArgumentException>(() => Watcher(Watching(b), straddle)).Message;
        Assert.Contains("Goal watch", straddleError);
        Assert.Contains("Left", straddleError);

        // The same arm on the unused side is no matter: a Right-only layer builds on a plan whose left arm is bent.
        var leftBent = WithJoint("Elbow", Side.Left, j => j with { Anchor = (j.Anchor.X + 0.08f, j.Anchor.Y, j.Anchor.Z + 0.05f) });
        Watcher(Watching(Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Right })), leftBent);
        Assert.Throws<ArgumentException>(() => Watcher(Watching(Behaviour(arms: new ArmLayer { Mode = ArmMode.Reach, Side = ArmSide.Nearer })), leftBent));
        Assert.Throws<ArgumentException>(() => Watcher(Watching(Behaviour(arms: new ArmLayer { Mode = ArmMode.Present, Side = ArmSide.Both })), leftBent));
    }

    [Fact]
    public void Parts_of_the_wrong_length_throw_at_tick_for_a_layered_goal_only()
    {
        var a = Watcher(Watching(Behaviour(look: new LookLayer())), Plan);
        var targets = new Quat[Joints];
        // The Start goal (idle) has no layers: parts are ignored, whatever they are.
        Step(a, Array.Empty<PartPose>(), targets);
        Step(a, new PartPose[3], targets);
        for (var i = 0; i < 40; i++) Step(a, Rest(), targets, Emit(1, Ahead));
        Assert.Equal(1, a.GoalIndex);
        Assert.Throws<ArgumentException>(() => Step(a, new PartPose[Plan.Parts.Count - 1], targets, Emit(1, Ahead)));
        Assert.Throws<ArgumentException>(() => Step(a, Array.Empty<PartPose>(), targets, Emit(1, Ahead)));
    }

    [Fact]
    public void Ticking_with_layers_allocates_nothing()
    {
        var layered = Behaviour(
            new LookLayer(),
            new ArmLayer { Mode = ArmMode.WardOff, Side = ArmSide.Both, Distance = 0.4f, Spread = 0.1f },
            Recoiling());
        var a = Watcher(Watching(layered), Plan);
        var parts = Turned();
        var targets = new Quat[Joints];
        var strengths = new float[Joints];
        var emitters = new[] { Emit(1, Ahead) };
        for (var i = 0; i < 60; i++) a.Tick(Origin, Origin, Origin, NoBoxes, strengths, targets, emitters, parts: parts);
        Assert.Equal(1, a.GoalIndex);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) a.Tick(Origin, Origin, Origin, NoBoxes, strengths, targets, emitters, parts: parts);
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
