using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The skeleton decorators' fixed world sizes from the scene's Decorators factor, their pixel size at a
/// distance, and the quads the wide-line program draws (bones, 3 px shoe locators).
/// </summary>
public class DecoratorSizesTests
{
    [Fact]
    public void Sizes_AreFixedWorldSizes_FromTheJointFactor()
    {
        var sizes = DecoratorSizes.FromFactor(5f);
        Assert.Equal(5f, sizes.Joint, 4);       // centimetres
        Assert.Equal(1f, sizes.BoneWidth, 4);
        Assert.True(sizes.Selected > sizes.AlsoSelected && sizes.AlsoSelected > sizes.Joint);
        Assert.Equal(2 * DecoratorSizes.FromFactor(2.5f).Joint, sizes.Joint, 4);
        Assert.Equal(DecoratorSizes.FromFactor(5f), DecoratorSizes.FromFactor(0f));
    }

    [Fact]
    public void Joints_ShrinkWithCameraDistance_AndPickingFollows()
    {
        var sizes = DecoratorSizes.FromFactor(5f);
        var near = new OrbitCamera { Distance = 20 };
        var far = new OrbitCamera { Distance = 80 };
        var viewport = new Vector2(800, 600);
        float nearPx = DecoratorSizes.JointPixels(sizes.Joint, DecoratorSizes.PixelsPerUnit(near, viewport.Y, near.Target));
        float farPx = DecoratorSizes.JointPixels(sizes.Joint, DecoratorSizes.PixelsPerUnit(far, viewport.Y, far.Target));
        Assert.Equal(4f, nearPx / farPx, 2);
        Assert.True(sizes.PickTolerance(near, viewport, near.Target) >= sizes.PickTolerance(far, viewport, far.Target));
        Assert.True(sizes.PickTolerance(far, viewport, far.Target) >= ViewportPick.Tolerance);
        // Very far away a dot stays a visible minimum.
        var away = new OrbitCamera { Distance = 1e6f };
        Assert.Equal(DecoratorSizes.MinJointPixels, DecoratorSizes.JointPixels(sizes.Joint, DecoratorSizes.PixelsPerUnit(away, viewport.Y, away.Target)));
    }

    [Fact]
    public void Scene_ReadsItsFactor_AndAnUnsetOneFallsBackToFive()
    {
        var scene = SceneFactory.NewSceneModel().Scene;
        Assert.Equal(5f, scene.JointRenderSize);
        scene.Source.Set("JointRenderSize", 1.1043035E-33f);
        Assert.Equal(Scene.DefaultJointRenderSize, scene.JointRenderSize);
        Assert.Equal(DecoratorSizes.FromFactor(5f), DecoratorSizes.For(scene));
        Assert.Equal(DecoratorSizes.FromFactor(5f), DecoratorSizes.For(null));
    }

    [Fact]
    public void WideLines_MakeTwoTrianglesPerSegment_WithBothSides()
    {
        float[] lines = [0, 0, 0, 1, 0, 0, 10, 0, 0, 0, 1, 0, 99];
        float[] quads = WideLines.Expand(lines);
        Assert.Equal(WideLines.VerticesPerSegment * WideLines.VertexFloats, quads.Length);
        int plus = 0, minus = 0;
        for (int v = 0; v < WideLines.VerticesPerSegment; v++)
        {
            int o = v * WideLines.VertexFloats;
            bool atP = quads[o] == 0f;
            // Each vertex points at the other end of the segment.
            Assert.Equal(atP ? 10f : 0f, quads[o + 3]);
            float side = quads[o + 6];
            // Sides are measured from each vertex's own end, so P's +1 edge is Q's -1 edge.
            if ((atP ? side : -side) > 0) plus++; else minus++;
            Assert.Equal(atP ? 1f : 0f, quads[o + 7]);
        }
        Assert.Equal(3, plus);
        Assert.Equal(3, minus);
    }
}
