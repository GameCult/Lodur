using Lodur;

namespace Lodur.Core.Tests;

/// <summary>Which layers a wound at a point and depth reaches: the rule on a hand-built stack, then the humanoid's content probed against it.</summary>
public sealed class WoundReachTests
{
    private static readonly PhysiologyPlan Humanoid = PhysiologyPlans.Humanoid();

    private static TissueDef Shell(string label, double t) => new(label, 10, 1) { Thickness = t };

    private static TissueDef Core(string label, TissueSite? site = null) => new(label, 10, 1) { Site = site };

    private static PartTissuesDef Stack(params TissueDef[] tissues) => new("A", Side.Center, tissues);

    private static int[] Reach(PartTissuesDef stack, (float X, float Y, float Z) point, double depth)
    {
        Span<int> layers = stackalloc int[stack.Tissues.Count];
        var n = stack.Reach(point, depth, layers);
        return layers[..n].ToArray();
    }

    private static string[] Labels(string part, (float X, float Y, float Z) point, double depth)
    {
        var stack = Humanoid.Parts.First(p => p.PartLabel == part && p.Side == Side.Center);
        return Reach(stack, point, depth).Select(i => stack.Tissues[i].Label).ToArray();
    }

    [Fact]
    public void Shells_are_reached_by_cumulative_thickness_strictly()
    {
        var stack = Stack(Shell("Skin", 0.002), Shell("Fat", 0.005), Core("Core"));
        var p = (0f, 0f, 0f);
        Assert.Equal(new[] { 0 }, Reach(stack, p, 0.001));
        Assert.Equal(new[] { 0, 1 }, Reach(stack, p, 0.003));
        Assert.Equal(new[] { 0, 1 }, Reach(stack, p, 0.007));
        Assert.Equal(new[] { 0, 1, 2 }, Reach(stack, p, 0.0071));
        Assert.Equal(new[] { 0 }, Reach(stack, p, 0.002));
    }

    [Fact]
    public void A_sited_core_is_reached_only_past_the_shells_and_its_sites_surface()
    {
        var stack = Stack(Shell("Skin", 0.002), Shell("Fat", 0.005), Core("Organ", new TissueSite((0f, 0f, 0.05f), 0.01f)));
        var p = (0f, 0f, 0.1f);
        Assert.Equal(new[] { 0, 1 }, Reach(stack, p, 0.039));
        Assert.Equal(new[] { 0, 1, 2 }, Reach(stack, p, 0.0401));
        // Close to the site, the shells still gate it.
        var near = (0f, 0f, 0.06f);
        Assert.Equal(new[] { 0, 1 }, Reach(stack, near, 0.007));
        Assert.Equal(new[] { 0, 1, 2 }, Reach(stack, near, 0.0071));
    }

    [Fact]
    public void A_sited_tissue_between_shells_is_gated_by_the_shells_before_it_only()
    {
        // Skin 2 mm, Fat 5 mm, a vessel sited on the point, Muscle sheet 15 mm, an unsited core: the vessel sits at 7 mm (the shells before it), the core at 22 mm (all of them).
        var stack = Stack(Shell("Skin", 0.002), Shell("Fat", 0.005), Core("Vessel", new TissueSite((0f, 0f, 0f), 0.01f)), Shell("Muscle", 0.015), Core("Fill"));
        var p = (0f, 0f, 0f);
        Assert.Equal(new[] { 0, 1 }, Reach(stack, p, 0.0069));           // kills: gate by nothing
        Assert.Equal(new[] { 0, 1, 2, 3 }, Reach(stack, p, 0.0071));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Reach(stack, p, 0.021));      // kills: gate by the total (22 mm)
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Reach(stack, p, 0.0221));  // the unsited core keeps the total
        // The distance still gates it: the site's surface is 0.04 m from this point.
        Assert.Equal(new[] { 0, 1, 3 }, Reach(stack, (0f, 0f, 0.05f), 0.021));
    }

    /// <summary>The rule before vessel sites: every core, sited or not, is gated by the shells' total.</summary>
    private static int[] ShippedReach(PartTissuesDef stack, (float X, float Y, float Z) point, double depth)
    {
        var shells = 0.0;
        foreach (var t in stack.Tissues) shells += t.Thickness;
        var hit = new List<int>();
        var before = 0.0;
        for (var i = 0; i < stack.Tissues.Count; i++)
        {
            var t = stack.Tissues[i];
            if (t.Thickness > 0.0)
            {
                if (depth > before) hit.Add(i);
                before += t.Thickness;
            }
            else if (depth > shells)
            {
                if (t.Site is not TissueSite site) hit.Add(i);
                else
                {
                    double dx = point.X - site.Centre.X, dy = point.Y - site.Centre.Y, dz = point.Z - site.Centre.Z;
                    if (depth > Math.Sqrt(dx * dx + dy * dy + dz * dz) - site.Radius) hit.Add(i);
                }
            }
        }

        return hit.ToArray();
    }

    [Fact]
    public void Over_the_humanoid_only_the_femoral_sites_differ_from_the_rule_before_vessel_sites()
    {
        // Every part, a 9 x 9 x 9 grid of points 4 cm apart about the part's origin, depths 1 to 60 mm: the carotids sit after every shell of the head, so they gate as before; only a Femoral,
        // listed before the thigh's muscle sheet, may be reached where the total would not.
        var cases = 0;
        var femoralGained = 0;
        foreach (var stack in Humanoid.Parts)
        {
            var femoral = stack.PartLabel == "Thigh" ? stack.Tissues.ToList().FindIndex(t => t.Label == "Femoral") : -1;
            for (var ix = -4; ix <= 4; ix++)
            for (var iy = -4; iy <= 4; iy++)
            for (var iz = -4; iz <= 4; iz++)
            for (var mm = 1; mm <= 60; mm++)
            {
                var point = (0.04f * ix, 0.04f * iy, 0.04f * iz);
                var now = Reach(stack, point, mm / 1000.0);
                var before = ShippedReach(stack, point, mm / 1000.0);
                cases++;
                if (femoral < 0) { Assert.Equal(before, now); continue; }
                var nowSansFemoral = now.Where(i => i != femoral).ToArray();
                Assert.Equal(before.Where(i => i != femoral), nowSansFemoral);
                if (now.Contains(femoral) && !before.Contains(femoral)) femoralGained++;
                Assert.True(!before.Contains(femoral) || now.Contains(femoral), "a Femoral the old rule reached is lost");
            }
        }

        Assert.True(cases > 800000, $"{cases} cases");
        Assert.True(femoralGained > 0, "no Femoral is reached where the shells' total would have gated it");
    }

    [Fact]
    public void A_depth_exactly_at_a_sites_surface_does_not_reach_it()
    {
        // Exact floats: the centre is 0.25 from the point, the radius 0.125, so the surface is 0.125 away.
        var stack = Stack(Shell("Skin", 0.002), Core("Organ", new TissueSite((0f, 0f, 0.25f), 0.125f)));
        var p = (0f, 0f, 0.5f);
        Assert.Equal(new[] { 0 }, Reach(stack, p, 0.125));
        Assert.Equal(new[] { 0, 1 }, Reach(stack, p, 0.1251));
    }

    [Fact]
    public void Unsited_cores_fill_the_part_and_output_is_in_stack_order()
    {
        var stack = Stack(Shell("Skin", 0.002), Core("Far", new TissueSite((0f, 0f, 0.5f), 0.01f)), Core("Fill"));
        Assert.Equal(new[] { 0, 2 }, Reach(stack, (0f, 0f, 0f), 0.01));
        var two = Stack(Shell("Skin", 0.002), Core("Fill"), Core("Near", new TissueSite((0f, 0f, 0f), 0.01f)));
        Assert.Equal(new[] { 0, 1, 2 }, Reach(two, (0f, 0f, 0f), 0.01));
    }

    [Fact]
    public void Reach_refuses_bad_input()
    {
        var stack = Stack(Shell("Skin", 0.002), Core("Fill"));
        var p = (0f, 0f, 0f);
        foreach (var depth in new[] { 0.0, -0.01, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => Reach(stack, p, depth));
        }

        foreach (var bad in new[] { float.NaN, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => Reach(stack, (bad, 0f, 0f), 0.01));
            Assert.Throws<ArgumentException>(() => Reach(stack, (0f, bad, 0f), 0.01));
            Assert.Throws<ArgumentException>(() => Reach(stack, (0f, 0f, bad), 0.01));
        }
        Assert.Throws<ArgumentException>(() =>
        {
            Span<int> short1 = stackalloc int[1];
            stack.Reach(p, 0.01, short1);
        });
    }

    [Fact]
    public void The_humanoid_throat_reaches_the_larynx_the_scalp_does_not()
    {
        Assert.Equal(new[] { "Skin", "Fat", "Larynx" }, Labels("Head", (0f, -0.0778f, 0.0778f), 0.02));
        var scalp = Labels("Head", (0f, 0.11f, 0f), 0.02);
        Assert.DoesNotContain("Larynx", scalp);
        Assert.DoesNotContain("Brain", scalp);
        Assert.Equal(new[] { "Skin", "Fat", "Skull" }, scalp);
        Assert.Contains("Brain", Labels("Head", (0f, 0.11f, 0f), 0.04));
        // The side of the neck reaches a carotid (mirrored left to right), the front of the throat none, the back of the neck neither.
        Assert.Equal(new[] { "Skin", "Fat", "CarotidLeft", "Muscle" }, Labels("Head", (0.0778f, -0.0778f, 0f), 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "CarotidRight", "Muscle" }, Labels("Head", (-0.0778f, -0.0778f, 0f), 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, Labels("Head", (0f, -0.0778f, -0.0778f), 0.02));
        Assert.DoesNotContain("CarotidLeft", Labels("Head", (0f, -0.0778f, 0.0778f), 0.04));

        var chest = Labels("Torso", (0.02f, 0.05f, 0.09f), 0.04);
        Assert.Contains("Heart", chest);
        Assert.DoesNotContain("Liver", chest);
        Assert.DoesNotContain("KidneyLeft", chest);
        Assert.DoesNotContain("KidneyRight", chest);
        Assert.DoesNotContain("Trachea", chest);
        Assert.Contains("LungLeft", Labels("Torso", (0.02f, 0.05f, 0.09f), 0.06));
        Assert.Contains("Liver", Labels("Torso", (-0.06f, -0.1f, 0.09f), 0.06));

        var thigh = Humanoid.Parts.First(p => p.PartLabel == "Thigh" && p.Side == Side.Left);
        string[] Thigh(double depth) => Reach(thigh, (0.07f, 0f, 0f), depth).Select(i => thigh.Tissues[i].Label).ToArray();
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, Thigh(0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Muscle", "Femur" }, Thigh(0.03));

        // The femoral vessel is at the inner thigh: -x on the left thigh, +x on the right.
        string[] ThighOf(Side side, float x, double depth)
        {
            var stack = Humanoid.Parts.First(p => p.PartLabel == "Thigh" && p.Side == side);
            return Reach(stack, (x, 0.12f, 0.0495f), depth).Select(i => stack.Tissues[i].Label).ToArray();
        }

        Assert.Equal(new[] { "Skin", "Fat", "Femoral", "Muscle" }, ThighOf(Side.Left, -0.0495f, 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Femoral", "Muscle" }, ThighOf(Side.Right, 0.0495f, 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, ThighOf(Side.Left, 0.0495f, 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, ThighOf(Side.Right, -0.0495f, 0.02));
    }

    [Fact]
    public void The_trachea_and_the_left_kidney_sit_where_the_content_puts_them()
    {
        // Trachea (0,0.2,0.06) r 0.015: reached from the front at its height, not from the back.
        Assert.Contains("Trachea", Labels("Torso", (0f, 0.2f, 0.09f), 0.04));
        Assert.DoesNotContain("Trachea", Labels("Torso", (0f, 0.2f, -0.09f), 0.04));
        Assert.DoesNotContain("Trachea", Labels("Torso", (0f, -0.1f, 0.09f), 0.04));
        // KidneyLeft (+x, 0.06,-0.15,-0.05) r 0.03: reached from the back on the left, not the right, not the front.
        Assert.Contains("KidneyLeft", Labels("Torso", (0.06f, -0.15f, -0.09f), 0.04));
        Assert.DoesNotContain("KidneyRight", Labels("Torso", (0.06f, -0.15f, -0.09f), 0.04));
        Assert.Contains("KidneyRight", Labels("Torso", (-0.06f, -0.15f, -0.09f), 0.04));
        Assert.DoesNotContain("KidneyLeft", Labels("Torso", (-0.06f, -0.15f, -0.09f), 0.04));
        Assert.DoesNotContain("KidneyLeft", Labels("Torso", (0.06f, -0.15f, 0.09f), 0.04));
    }

    [Fact]
    public void Every_head_and_torso_organ_core_is_sited()
    {
        foreach (var part in new[] { "Head", "Torso" })
        {
            var stack = Humanoid.Parts.First(p => p.PartLabel == part && p.Side == Side.Center);
            var unsited = stack.Tissues.Where(t => t.Thickness == 0.0 && t.Site == null).Select(t => t.Label).ToArray();
            Assert.Equal(part == "Torso" ? new[] { "Ribcage" } : Array.Empty<string>(), unsited);
        }
    }
}
