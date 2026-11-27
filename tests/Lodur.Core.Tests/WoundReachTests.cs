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

        Assert.Throws<ArgumentException>(() => Reach(stack, (float.NaN, 0f, 0f), 0.01));
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
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, Labels("Head", (0.0778f, -0.0778f, 0f), 0.02));
        Assert.Equal(new[] { "Skin", "Fat", "Muscle" }, Labels("Head", (0f, -0.0778f, -0.0778f), 0.02));

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
