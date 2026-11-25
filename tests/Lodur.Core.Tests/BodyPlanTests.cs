using Lodur;

namespace Lodur.Core.Tests;

public sealed class BodyPlanTests
{
    private static PartDef Box(string label, Side side = Side.Center) =>
        new(label, side, PartShape.Box, (0.1f, 0.1f, 0.1f), (0f, 0f, 0f), Quat.Identity, 1000f, 0.8f, 0f);

    private static JointDef Joint(string label, int parent, int child, Side side = Side.Center) =>
        new(label, side, JointKind.Spherical, parent, child, (0f, 0f, 0f), (0f, 1f, 0f), -1f, 1f, 0.5f, new MuscleGains(1f, 1f, 1f, 1f));

    private static BodyPlan Plan(PartDef[] parts, JointDef[] joints, SensePointDef[]? senses = null) =>
        new(parts, joints, senses ?? Array.Empty<SensePointDef>());

    private static BodyPlan Valid() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) });

    private static void Refuses(Action build, string saying)
    {
        var e = Assert.Throws<ArgumentException>(build);
        Assert.Contains(saying, e.Message);
    }

    [Fact]
    public void A_valid_plan_resolves_labels_and_derives_the_tree_from_its_joints()
    {
        var plan = Valid();
        Assert.Equal(1, plan.PartIndex("B"));
        Assert.Equal(0, plan.JointIndex("J"));
        Assert.Equal(new[] { -1, 0 }, new[] { plan.ParentOf(0), plan.ParentOf(1) });
    }

    // Rule 1: (label, side) unique per list, labels non-empty.
    [Fact]
    public void Rule1_a_repeated_label_and_side_is_refused_in_each_list_and_another_side_is_not()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("A") }, new[] { Joint("J", 0, 1) }), "two parts are labelled 'A'");
        Refuses(() => Plan(new[] { Box("A"), Box("B"), Box("C") }, new[] { Joint("J", 0, 1), Joint("J", 0, 2) }), "two joints are labelled 'J'");
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) },
            new[] { new SensePointDef("S", Side.Left, 0, default), new SensePointDef("S", Side.Left, 1, default) }), "two sense points are labelled 'S'");
        var plan = Plan(new[] { Box("A"), Box("L", Side.Left), Box("L", Side.Right) }, new[] { Joint("J", 0, 1, Side.Left), Joint("J", 0, 2, Side.Right) });
        Assert.Equal(2, plan.PartIndex("L", Side.Right));
    }

    [Fact]
    public void Rule1_an_empty_label_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("") }, new[] { Joint("J", 0, 1) }), "empty label");
    }

    // Rule 2: joint ends in range, parent before child.
    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 1)]
    [InlineData(5, 1)]
    public void Rule2_a_joint_end_that_is_not_a_part_is_refused(int parent, int child)
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", parent, child) }), "is not a part");
    }

    [Fact]
    public void Rule2_a_parent_that_does_not_precede_its_child_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 1, 0) }), "must precede");
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 1, 1) }), "must precede");
    }

    // Rule 3: part 0 the root, every other part the child of exactly one joint.
    [Fact]
    public void Rule3_a_part_with_no_joint_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B"), Box("C") }, new[] { Joint("J", 0, 1) }), "child of 0 joints");
    }

    [Fact]
    public void Rule3_a_part_with_two_joints_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B"), Box("C") }, new[] { Joint("J", 0, 1), Joint("K", 0, 2), Joint("M", 1, 2) }), "child of 2 joints");
    }

    [Fact]
    public void Rule3_a_plan_without_parts_is_refused()
    {
        Refuses(() => Plan(Array.Empty<PartDef>(), Array.Empty<JointDef>()), "needs a root");
    }

    // Rule 4: sense points name parts.
    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Rule4_a_sense_point_on_no_part_is_refused(int part)
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) }, new[] { new SensePointDef("S", Side.Center, part, default) }), "is not a part");
    }

    // Rule 5: numbers a physics world can take.
    [Fact]
    public void Rule5_a_non_finite_part_number_is_refused()
    {
        var nan = Box("B") with { Position = (float.NaN, 0f, 0f) };
        Refuses(() => Plan(new[] { Box("A"), nan }, new[] { Joint("J", 0, 1) }), "non-finite");
        var inf = Box("B") with { Friction = float.PositiveInfinity };
        Refuses(() => Plan(new[] { Box("A"), inf }, new[] { Joint("J", 0, 1) }), "non-finite");
    }

    [Fact]
    public void Rule5_a_size_the_shape_uses_must_be_positive_and_one_it_does_not_use_may_be_zero()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Size = (0.1f, 0f, 0.1f) } }, new[] { Joint("J", 0, 1) }), "size its shape uses");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Capsule, Size = (0.1f, 0f, 0f) } }, new[] { Joint("J", 0, 1) }), "size its shape uses");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Sphere, Size = (-0.1f, 0f, 0f) } }, new[] { Joint("J", 0, 1) }), "size its shape uses");
        var capsule = Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Capsule, Size = (0.1f, 0.2f, 0f) } }, new[] { Joint("J", 0, 1) });
        Assert.Equal(2, capsule.Parts.Count);
    }

    [Fact]
    public void Rule5_a_box_depth_that_is_not_positive_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Size = (0.1f, 0.1f, 0f) } }, new[] { Joint("J", 0, 1) }), "size its shape uses");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Size = (0.1f, 0.1f, -0.1f) } }, new[] { Joint("J", 0, 1) }), "size its shape uses");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rule5_a_non_finite_rotation_component_is_refused(int component)
    {
        var q = new[] { 0f, 0f, 0f, 1f };
        q[component] = float.NaN;
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Rotation = new Quat(q[0], q[1], q[2], q[3]) } }, new[] { Joint("J", 0, 1) }), "non-finite");
        q[component] = float.PositiveInfinity;
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Rotation = new Quat(q[0], q[1], q[2], q[3]) } }, new[] { Joint("J", 0, 1) }), "non-finite");
    }

    [Fact]
    public void Rule5_a_field_the_shape_cannot_carry_is_refused()
    {
        var turned = Quat.FromAxisAngle(0f, 0f, 1f, 0.5f);
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Sphere, Size = (0.1f, 0f, 0f), Rotation = turned } }, new[] { Joint("J", 0, 1) }), "sphere carries no rotation");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { RollingResistance = 0.1f } }, new[] { Joint("J", 0, 1) }), "box carries no rolling resistance");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Capsule, Size = (0.1f, 0.2f, 0.3f) } }, new[] { Joint("J", 0, 1) }), "does not use is not zero");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Sphere, Size = (0.1f, 0.2f, 0f) } }, new[] { Joint("J", 0, 1) }), "does not use is not zero");
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Shape = PartShape.Sphere, Size = (0.1f, 0f, 0.3f) } }, new[] { Joint("J", 0, 1) }), "does not use is not zero");
        // what each shape does carry is accepted
        var ok = Plan(new[] { Box("A") with { Rotation = turned }, Box("B") with { Shape = PartShape.Capsule, Size = (0.1f, 0.2f, 0f), Rotation = turned, RollingResistance = 0.1f },
            Box("C") with { Shape = PartShape.Sphere, Size = (0.1f, 0f, 0f), RollingResistance = 0.1f } }, new[] { Joint("J", 0, 1), Joint("K", 0, 2) });
        Assert.Equal(3, ok.Parts.Count);
    }

    [Fact]
    public void Rule5_a_density_that_is_not_positive_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") with { Density = 0f } }, new[] { Joint("J", 0, 1) }), "density");
    }

    [Fact]
    public void Rule5_a_joint_with_a_non_finite_number_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) with { Axis = (0f, float.NaN, 0f) } }), "non-finite");
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) with { Gains = new MuscleGains(float.PositiveInfinity, 1f, 1f, 1f) } }), "non-finite");
    }

    [Fact]
    public void Rule5_a_lower_limit_above_the_upper_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) with { Lower = 0.5f, Upper = 0.4f } }), "lower limit");
    }

    [Fact]
    public void Rule5_a_negative_cone_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) with { Cone = -0.1f } }), "cone");
    }

    [Fact]
    public void Rule5_a_non_finite_sense_offset_is_refused()
    {
        Refuses(() => Plan(new[] { Box("A"), Box("B") }, new[] { Joint("J", 0, 1) }, new[] { new SensePointDef("S", Side.Center, 0, (0f, float.NaN, 0f)) }), "non-finite");
    }

    [Fact]
    public void The_plan_copies_its_lists()
    {
        var parts = new List<PartDef> { Box("A"), Box("B") };
        var plan = Plan(parts.ToArray(), new[] { Joint("J", 0, 1) });
        var viaList = new BodyPlan(parts, new[] { Joint("J", 0, 1) }, Array.Empty<SensePointDef>());
        parts.Add(Box("C"));
        Assert.Equal(2, viaList.Parts.Count);
        Assert.Equal(2, plan.Parts.Count);
    }

    // Airway openings: rules 1, 4, 5, 6.
    private static BodyPlan WithAirway(PartDef part, params AirwayOpeningDef[] openings) =>
        new(new[] { part }, Array.Empty<JointDef>(), Array.Empty<SensePointDef>(), openings);

    private static PartDef Shaped(PartShape shape, (float, float, float) size) =>
        new("P", Side.Center, shape, size, (0f, 0f, 0f), Quat.Identity, 1000f, 0.8f, 0f);

    private static AirwayOpeningDef Opening((float, float, float) offset, float radius = 0.025f, string label = "O") =>
        new(label, Side.Center, 0, offset, radius);

    [Fact]
    public void Rule1_a_repeated_airway_label_and_side_is_refused_and_another_side_is_not()
    {
        var part = Shaped(PartShape.Sphere, (0.11f, 0f, 0f));
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f)), Opening((0f, 0.05f, 0.1f))), "two airway openings are labelled 'O'");
        var plan = WithAirway(part, Opening((0f, 0f, 0.11f)), Opening((0f, 0.05f, 0.1f), label: "O") with { Side = Side.Left });
        Assert.Equal(1, plan.AirwayIndex("O", Side.Left));
        Refuses(() => plan.AirwayIndex("O", Side.Right), "no airway opening labelled 'O' on side Right");
    }

    [Fact]
    public void Rule4_an_airway_opening_on_no_part_is_refused()
    {
        var part = Shaped(PartShape.Sphere, (0.11f, 0f, 0f));
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f)) with { Part = 1 }), "part 1 is not a part");
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f)) with { Part = -1 }), "part -1 is not a part");
    }

    [Fact]
    public void Rule5_a_non_finite_offset_or_radius_and_a_radius_not_above_zero_are_refused()
    {
        var part = Shaped(PartShape.Sphere, (0.11f, 0f, 0f));
        Refuses(() => WithAirway(part, Opening((0f, float.NaN, 0.11f))), "non-finite");
        Refuses(() => WithAirway(part, Opening((float.PositiveInfinity, 0f, 0.11f))), "non-finite");
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f), float.NaN)), "non-finite");
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f), 0f)), "radius is not positive");
        Refuses(() => WithAirway(part, Opening((0f, 0f, 0.11f), -0.01f)), "radius is not positive");
    }

    [Fact]
    public void Rule6_an_opening_wholly_inside_its_part_is_refused_and_one_straddling_the_surface_is_accepted_for_each_shape()
    {
        // sphere r 0.11: centre at 0.05 with radius 0.025 is wholly inside (distance -0.06); at the surface it straddles.
        var sphere = Shaped(PartShape.Sphere, (0.11f, 0f, 0f));
        Refuses(() => WithAirway(sphere, Opening((0f, 0f, 0.05f))), "lies inside its part");
        WithAirway(sphere, Opening((0f, 0f, 0.11f)));
        WithAirway(sphere, Opening((0f, 0f, 0.12f)));

        // capsule r 0.05, half length 0.2 along y: past the cap is measured from the end of the segment, beside the side from the axis.
        var capsule = Shaped(PartShape.Capsule, (0.05f, 0.2f, 0f));
        Refuses(() => WithAirway(capsule, Opening((0f, 0.1f, 0.01f))), "lies inside its part");
        Refuses(() => WithAirway(capsule, Opening((0f, 0.2f, 0f))), "lies inside its part");
        Refuses(() => WithAirway(capsule, Opening((0f, 0.22f, 0f))), "lies inside its part"); // 0.02 past the segment end, r 0.05: inside by 0.03, more than the radius 0.025
        WithAirway(capsule, Opening((0.05f, 0.1f, 0f)));
        WithAirway(capsule, Opening((0f, 0.25f, 0f)));
        WithAirway(capsule, Opening((0f, 0.3f, 0f)));

        // box half extents (0.1, 0.05, 0.2): the signed distance is the box's, so a point deep in one axis is not rescued by another.
        var box = Shaped(PartShape.Box, (0.1f, 0.05f, 0.2f));
        Refuses(() => WithAirway(box, Opening((0f, 0f, 0f))), "lies inside its part");
        Refuses(() => WithAirway(box, Opening((0.05f, 0.01f, 0.1f))), "lies inside its part");
        WithAirway(box, Opening((0.1f, 0f, 0f)));
        WithAirway(box, Opening((0f, 0.05f, 0f)));
        WithAirway(box, Opening((0.11f, 0.06f, 0.21f)));
    }

    [Fact]
    public void The_airway_opening_list_is_copied_and_null_means_none()
    {
        var part = Shaped(PartShape.Sphere, (0.11f, 0f, 0f));
        var list = new List<AirwayOpeningDef> { Opening((0f, 0f, 0.11f)) };
        var plan = new BodyPlan(new[] { part }, Array.Empty<JointDef>(), Array.Empty<SensePointDef>(), list);
        list.Add(Opening((0f, 0f, 0.12f), label: "P"));
        Assert.Single(plan.AirwayOpenings);
        Assert.Empty(Valid().AirwayOpenings);
    }

    // The humanoid.
    [Fact]
    public void The_humanoid_has_19_parts_18_joints_and_3_sense_points_labelled_in_build_order()
    {
        var plan = BodyPlans.Humanoid();
        string Name(string label, Side side) => side == Side.Center ? label : label + side;
        Assert.Equal(new[]
        {
            "Pelvis", "Torso", "Head",
            "UpperArmLeft", "LowerArmLeft", "ThighLeft", "ShinLeft", "FootLeft",
            "UpperArmRight", "LowerArmRight", "ThighRight", "ShinRight", "FootRight",
            "PalmLeft", "ThumbLeft", "FingersLeft", "PalmRight", "ThumbRight", "FingersRight",
        }, plan.Parts.Select(p => Name(p.Label, p.Side)).ToArray());
        Assert.Equal(new[]
        {
            "Spine", "Neck",
            "ShoulderLeft", "ElbowLeft", "HipLeft", "KneeLeft", "AnkleLeft",
            "ShoulderRight", "ElbowRight", "HipRight", "KneeRight", "AnkleRight",
            "WristLeft", "ThumbLeft", "FingersLeft", "WristRight", "ThumbRight", "FingersRight",
        }, plan.Joints.Select(j => Name(j.Label, j.Side)).ToArray());
        Assert.Equal(new[] { "HandLeft", "HandRight", "Head" }, plan.SensePoints.Select(s => Name(s.Label, s.Side)).ToArray());
        Assert.Equal(new[] { plan.PartIndex("Palm", Side.Left), plan.PartIndex("Palm", Side.Right), plan.PartIndex("Head") }, plan.SensePoints.Select(s => s.Part).ToArray());
    }

    [Fact]
    public void The_humanoid_has_a_mouth_and_a_nose_on_the_head_each_on_its_surface()
    {
        var plan = BodyPlans.Humanoid();
        Assert.Equal(new[] { "Mouth", "Nose" }, plan.AirwayOpenings.Select(a => a.Label).ToArray());
        foreach (var a in plan.AirwayOpenings)
        {
            Assert.Equal(plan.PartIndex("Head"), a.Part);
            var o = a.Offset;
            Assert.InRange(MathF.Sqrt(o.X * o.X + o.Y * o.Y + o.Z * o.Z), 0.109f, 0.111f);
        }
    }

    [Fact]
    public void The_humanoid_parent_tree_is_the_old_one()
    {
        var plan = BodyPlans.Humanoid();
        //                     pelvis torso head uaL laL thL shL ftL uaR laR thR shR ftR plL thL fiL plR thR fiR
        var old = new[] { -1, 0, 1, 1, 3, 0, 5, 6, 1, 8, 0, 10, 11, 4, 13, 13, 9, 16, 16 };
        Assert.Equal(old, Enumerable.Range(0, plan.Parts.Count).Select(plan.ParentOf).ToArray());
    }

    [Fact]
    public void The_humanoid_hand_joints_hang_palm_thumb_fingers_off_the_lower_arm_per_side()
    {
        var plan = BodyPlans.Humanoid();
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            Assert.Equal((plan.PartIndex("LowerArm", side), plan.PartIndex("Palm", side)),
                (plan.Joints[plan.JointIndex("Wrist", side)].Parent, plan.Joints[plan.JointIndex("Wrist", side)].Child));
            Assert.Equal((plan.PartIndex("Palm", side), plan.PartIndex("Thumb", side)),
                (plan.Joints[plan.JointIndex("Thumb", side)].Parent, plan.Joints[plan.JointIndex("Thumb", side)].Child));
            Assert.Equal((plan.PartIndex("Palm", side), plan.PartIndex("Fingers", side)),
                (plan.Joints[plan.JointIndex("Fingers", side)].Parent, plan.Joints[plan.JointIndex("Fingers", side)].Child));
        }
    }

    [Fact]
    public void A_lookup_with_the_wrong_side_refuses_and_names_label_and_side()
    {
        var plan = BodyPlans.Humanoid();
        Refuses(() => plan.PartIndex("Palm"), "'Palm' on side Center");
        Refuses(() => plan.PartIndex("Pelvis", Side.Left), "'Pelvis' on side Left");
        Refuses(() => plan.JointIndex("Knee"), "'Knee' on side Center");
        Refuses(() => plan.SenseIndex("Head", Side.Right), "'Head' on side Right");
        Refuses(() => plan.PartIndex("Tail"), "'Tail'");
    }
}
