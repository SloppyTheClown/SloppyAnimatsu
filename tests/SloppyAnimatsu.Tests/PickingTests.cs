using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Turning a point of a view back into the scene
/// the ray through a viewport point meets
/// the drawn triangles, and a plane keeps a placement usable off the character. The interactive
/// joint placement and the Skin Workshop's brush both work from these.
/// </summary>
public class PickingTests
{
    [SceneTheory, SceneSamples]
    public void Through_AgreesWithTheViewsProjection(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        var center = (pose.Bounds.Min + pose.Bounds.Max) * 0.5f;
        if (camera.Project(center, viewport) is not { } projected) return;

        var ray = ScenePicking.Through(camera, viewport, projected);

        Assert.NotNull(ray);
        var toPoint = center - ray!.Value.Origin;
        float along = Vector3.Dot(toPoint, ray.Value.Direction);
        Assert.True(along > 0f, "the projected point is ahead of the ray's origin");
        float miss = Vector3.Distance(ray.Value.Origin + ray.Value.Direction * along, center);
        Assert.True(miss < 1e-3f * MathF.Max(1f, along), $"the ray misses the point by {miss}");
    }

    [SceneTheory, SceneSamples]
    public void Pick_HitsTheFrontMeshUnderTheViewCentre(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        var center = (pose.Bounds.Min + pose.Bounds.Max) * 0.5f;

        var hit = ScenePicking.Pick(pose, camera, viewport, viewport / 2f);

        Assert.NotNull(hit);
        Assert.Contains(hit!.Value.Mesh, pose.MeshParts.Where(p => p.Visible).Select(p => p.Mesh));
        Assert.InRange(hit.Value.Point.Z, pose.Bounds.Min.Z - 1e-2f, pose.Bounds.Max.Z + 1e-2f);
        var offset = hit.Value.Point - center;
        Assert.True(MathF.Abs(offset.X) < 1e-2f && MathF.Abs(offset.Y) < 1e-2f,
            $"the hit is not on the view's centre line: {offset}");
        Assert.True(MathF.Abs(hit.Value.Normal.Length() - 1f) < 1e-3f, "the hit normal is unit length");
    }

    [SceneTheory, SceneSamples]
    public void Pick_OnlyTestsTheChosenMesh(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        if (ScenePicking.Pick(pose, camera, viewport, viewport / 2f) is not { } hit) return;

        var only = ScenePicking.Pick(pose, camera, viewport, viewport / 2f, hit.Mesh);

        Assert.NotNull(only);
        Assert.Same(hit.Mesh, only!.Value.Mesh);
        Assert.True(Vector3.Distance(hit.Point, only.Value.Point) < 1e-2f);
    }

    [SceneTheory, SceneSamples]
    public void Pick_ReportsTheHitTrianglesMeshIndex(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        if (ScenePicking.Pick(pose, camera, viewport, viewport / 2f) is not { } hit) return;

        // The index is the hit triangle's place in the mesh's own Triangles() order (the order a
        // blendshape zone's InteractiveFaces uses), so its corners are the ones the ray hit - not
        // the drawn order, which material grouping reorders.
        Assert.InRange(hit.Triangle, 0, hit.Mesh.Triangles().Count() - 1);
        Assert.True(pose.TryTriangle(hit.Mesh, hit.Triangle, out var a, out var b, out var c),
            "the reported triangle is one the last pose drew");
        var ray = ScenePicking.Through(camera, viewport, viewport / 2f);
        Assert.NotNull(ray);
        Assert.True(ScenePicking.IntersectTriangle(ray!.Value.Origin, ray.Value.Direction, a, b, c, out float distance),
            "the ray meets the reported triangle");
        Assert.True(MathF.Abs(distance - hit.Distance) < 1e-2f * MathF.Max(1f, distance),
            $"the reported triangle is the one at the hit's depth ({distance} against {hit.Distance})");
    }

    [SceneTheory, SceneSamples]
    public void Pick_ReturnsNothingOffTheScene(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);

        Assert.Null(ScenePicking.Pick(pose, camera, viewport, new Vector2(-2000, -2000)));
    }

    [SceneTheory, SceneSamples]
    public void OnPlane_MeetsThePlaneFacingTheView(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Pose(model) is not { } pose) return;
        var (camera, viewport) = ViewFor(pose);
        var center = (pose.Bounds.Min + pose.Bounds.Max) * 0.5f;
        var ray = ScenePicking.Through(camera, viewport, viewport / 2f);
        Assert.NotNull(ray);

        var point = ScenePicking.OnPlane(ray!.Value, new Vector3(center.X, center.Y, 0f),
            camera.Forward);

        Assert.NotNull(point);
        Assert.True(MathF.Abs(point!.Value.X - center.X) < 1e-2f && MathF.Abs(point.Value.Y - center.Y) < 1e-2f);
        Assert.True(MathF.Abs(point.Value.Z) < 1e-2f, $"the plane point is off the Z = 0 plane: {point}");
    }

    [Fact]
    public void Forward_IsTheLookDirection_EvenWhenTheViewIsTilted()
    {
        // The view matrix keeps the camera's basis in its columns, so the look direction is the
        // negated third column. Reading the third row gives a vector that only matches for a view
        // looking straight down a world axis.
        var camera = new OrbitCamera { Target = Vector3.Zero, Distance = 8, Yaw = 0.7f, Pitch = 0.35f };
        var look = Vector3.Normalize(camera.Target - camera.Eye);

        var forward = camera.Forward;

        Assert.True(Vector3.Distance(look, forward) < 1e-4f, $"forward {forward} is not the look direction {look}");
    }

    [Fact]
    public void IntersectTriangle_ReportsAHitAheadOnly()
    {
        var a = new Vector3(-1, -1, 0);
        var b = new Vector3(1, -1, 0);
        var c = new Vector3(0, 1, 0);

        Assert.True(ScenePicking.IntersectTriangle(new Vector3(0, 0, 3), -Vector3.UnitZ, a, b, c, out float distance));
        Assert.Equal(3f, distance, 3);
        Assert.False(ScenePicking.IntersectTriangle(new Vector3(0, 0, 3), Vector3.UnitZ, a, b, c, out _));
        Assert.False(ScenePicking.IntersectTriangle(new Vector3(2, 2, 3), -Vector3.UnitZ, a, b, c, out _));
        Assert.False(ScenePicking.IntersectTriangle(new Vector3(0, 0, 3), Vector3.UnitX, a, b, c, out _));
    }

    private static ScenePose? Pose(SceneModel model)
    {
        var pose = new ScenePose(model);
        var character = model.Scene.Characters.FirstOrDefault();
        try
        {
            pose.Update(character, null, 0);
        }
        catch (Exception)
        {
            return null;
        }
        // An orthographic view needs an extent to frame.
        return pose.Bounds.Min == pose.Bounds.Max ? null : pose;
    }

    /// <summary>An orthographic front view framed on the scene's bounds, 800 by 600.</summary>
    private static (IViewCamera Camera, Vector2 Viewport) ViewFor(ScenePose pose)
    {
        var center = (pose.Bounds.Min + pose.Bounds.Max) * 0.5f;
        var size = pose.Bounds.Max - pose.Bounds.Min;
        float height = MathF.Max(MathF.Max(size.Y, size.X) * 1.2f, 1f);
        return (SceneCameraView.Ortho(0, height, new Vector2(center.X, center.Y)), new Vector2(800, 600));
    }
}
