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

    /// <summary>
    /// Light joint display: no two joint dots overlap (each shrinks to fit the gap to its nearest
    /// neighbour, scaled by the high-density factor), and the size batches hold every point and line.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void JointDots_ShrinkInDenseAreas_SoNeighboursDoNotOverlap(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.First(c => c.Joints.Count() >= 2);
        var pose = new ScenePose(model);
        pose.Update(character, null, 0f);
        AssertBatchesMatchTotals(pose);

        float standardDot = DecoratorSizes.For(model.Scene).Joint;
        var joints = DotPositions(pose, character);
        for (int a = 0; a < joints.Count; a++)
            for (int b = a + 1; b < joints.Count; b++)
            {
                float d = Vector3.Distance(joints[a].Position, joints[b].Position);
                if (d < standardDot * ScenePose.CoincidentFraction) continue;
                float radii = (joints[a].Scale + joints[b].Scale) / 2f * standardDot;
                Assert.True(radii <= d + standardDot / 32f,
                    $"{joints[a].Joint.Name} and {joints[b].Joint.Name}: dots {radii} over a gap of {d}");
            }
    }

    [SceneTheory, SceneSamples]
    public void ClassicJointDisplay_DrawsEveryDotAtTheStandardSize(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.First(c => c.Joints.Count() >= 2);
        var pose = new ScenePose(model) { AdaptiveJointSizes = false };
        pose.Update(character, null, 0f);
        AssertBatchesMatchTotals(pose);
        Assert.All(pose.JointBatches, b => Assert.Equal(1f, b.Scale));
        Assert.All(pose.BoneBatches, b => Assert.Equal(1f, b.Scale));
    }

    /// <summary>A hand's fingers: joints half a dot apart get small dots, the rest stay standard.</summary>
    [SceneTheory, SceneSamples]
    public void CloseFingerJoints_GetHighDensityDots(string path)
    {
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c =>
            c.Controllers.OfType<Joint>().Count() >= 2 && PoseEvaluator.RestPose(c, riggingContext: true) != null);
        if (character == null) return;
        var pose = new ScenePose(model);
        pose.Update(character, null, 0f);
        var lonely = DotPositions(pose, character).OrderByDescending(j => j.Scale).First();
        float standardDot = DecoratorSizes.For(model.Scene).Joint;

        model.Scene.Source.Set("JointRenderSizeHighDensity", 6f);
        var editor = new RigEditor(character);
        var hand = lonely.Joint;
        var fingers = new List<Joint>();
        for (int i = 1; i <= 3; i++)
            fingers.Add(editor.CreateJointAt(hand, lonely.Position + new Vector3(0, standardDot * 0.5f * i, 0), $"Finger{i}"));
        pose.Update(character, null, 0f);

        AssertBatchesMatchTotals(pose);
        foreach (var finger in fingers)
        {
            float scale = pose.DotScaleOf(finger);
            Assert.InRange(scale, ScenePose.MinDotScale, 0.5f);
        }
        // A larger high-density factor gives larger (still non-overlapping) finger dots.
        float before = pose.DotScaleOf(fingers[1]);
        model.Scene.Source.Set("JointRenderSizeHighDensity", model.Scene.JointRenderSizeHighDensity * 2f);
        pose.Update(character, null, 0f);
        Assert.True(pose.DotScaleOf(fingers[1]) > before, $"before {before}, after {pose.DotScaleOf(fingers[1])}, factor {model.Scene.JointRenderSizeHighDensity}, std {standardDot}");
    }

    private static List<(Joint Joint, Vector3 Position, float Scale)> DotPositions(ScenePose pose, Character character)
    {
        var result = new List<(Joint, Vector3, float)>();
        var evaluator = new PoseEvaluator(character, riggingContext: pose.RiggingContext);
        var worlds = pose.ControllerWorlds;
        for (int i = 0; i < evaluator.Controllers.Count && i < worlds.Length; i++)
            if (evaluator.Controllers[i] is Joint j && j.IsShownInScene)
                result.Add((j, worlds[i].Translation, pose.DotScaleOf(j)));
        return result;
    }

    private static void AssertBatchesMatchTotals(ScenePose pose)
    {
        Assert.Equal(pose.SkeletonPoints.Length, pose.JointBatches.Sum(b => b.Vertices.Length));
        Assert.Equal(pose.SkeletonLines.Length, pose.BoneBatches.Sum(b => b.Vertices.Length));
        Assert.All(pose.JointBatches, b => Assert.InRange(b.Scale, ScenePose.MinDotScale, 1f));
    }
}
