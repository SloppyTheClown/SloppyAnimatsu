using System.Numerics;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The viewport axis cube and the orbit camera's turn to an axis and flat view
/// </summary>
public class AxisCubeTests
{
    private const float Tolerance = 1e-4f;

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = Tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AlignTo_PutsTheEyeOnThatHalfAxis(int axis)
    {
        var camera = new OrbitCamera { Target = new Vector3(1, 2, 3), Distance = 10, Yaw = 2.4f, Pitch = -0.7f };
        camera.AlignTo(axis, animate: false);
        Near(camera.Target + 10 * AxisCube.Direction(axis), camera.Eye, 1e-3f);
    }

    [Fact]
    public void SideViews_KeepYUp_TopHasMinusZUp_BottomHasPlusZUp()
    {
        var camera = new OrbitCamera();
        foreach (int axis in new[] { 0, 2, 3, 5 })
        {
            camera.AlignTo(axis, animate: false);
            Near(Vector3.UnitY, camera.Up);
        }
        camera.Yaw = 0.1f;
        camera.AlignTo(1, animate: false);
        Near(-Vector3.UnitZ, camera.Up);
        camera.AlignTo(4, animate: false);
        Near(Vector3.UnitZ, camera.Up);
        // The view matrix stays finite straight above the target.
        camera.AlignTo(1, animate: false);
        var view = camera.View;
        Assert.False(float.IsNaN(view.M11) || float.IsNaN(view.M22) || float.IsNaN(view.M33));
    }

    [Fact]
    public void TopView_KeepsTheNearestQuarterTurnOfTheYaw()
    {
        var (yaw, pitch) = OrbitCamera.AxisAngles(1, 1.4f);
        Assert.Equal(MathF.PI / 2, yaw, 4);
        Assert.Equal(OrbitCamera.MaxPitch, pitch, 4);
    }

    [Fact]
    public void AnimatedTurn_TakesTheShortWayAndEndsExactly()
    {
        var camera = new OrbitCamera { Yaw = 3.0f, Pitch = 0.2f };
        camera.AlignTo(5); // -Z: yaw pi, a small step from 3.0, not most of a turn back.
        Assert.True(camera.Animating);
        Assert.True(camera.Step(OrbitCamera.AlignSeconds / 2));
        Assert.InRange(camera.Yaw, 3.0f, MathF.PI + 1e-4f);
        Assert.False(camera.Step(OrbitCamera.AlignSeconds));
        Assert.False(camera.Animating);
        Assert.Equal(MathF.PI, MathF.Abs(camera.Yaw), 4);
        Assert.Equal(0f, camera.Pitch, 4);
    }

    [Fact]
    public void Orbiting_LeavesTheFlatViewAndStopsATurn()
    {
        var camera = new OrbitCamera { Orthographic = true };
        camera.AlignTo(0);
        camera.Orbit(0.1f, 0f);
        Assert.False(camera.Orthographic);
        Assert.False(camera.Animating);
    }

    [Fact]
    public void FlatView_CoversThePerspectiveHeightAtTheTarget()
    {
        var camera = new OrbitCamera { Distance = 20, Orthographic = true };
        var projection = camera.Projection(1.5f);
        Assert.Equal(0f, projection.M34, 6); // parallel: no perspective divide
        float height = 2 * 20 * MathF.Tan(camera.FieldOfView / 2);
        Assert.Equal(2 / height, projection.M22, 4);
        Assert.Equal(height, camera.ViewHeightAt(camera.Target + new Vector3(100, 0, 0)), 3);
        // A point at the target projects to the view centre, one half-height above to the top edge.
        var size = new Vector2(300, 200);
        var centre = camera.Project(camera.Target, size)!.Value;
        Assert.Equal(150f, centre.X, 2);
        Assert.Equal(100f, centre.Y, 2);
        var top = camera.Project(camera.Target + camera.Up * (height / 2), size)!.Value;
        Assert.Equal(0f, top.Y, 2);
    }

    [Fact]
    public void Layout_FacingTheFrontShowsOneFaceAndPutsPlusZInFront()
    {
        var camera = new OrbitCamera();
        camera.AlignTo(2, animate: false);
        var parts = AxisCube.Layout(camera.View, new Vector2(100, 100), 40);
        var faces = parts.Where(p => p.Kind == AxisCube.PartKind.Face).ToList();
        Assert.Single(faces);
        Assert.Equal(2, faces[0].Axis);
        // Back to front: the +Z ball is last, the -Z ball first.
        Assert.Equal((AxisCube.PartKind.Ball, 2), (parts[^1].Kind, parts[^1].Axis));
        Assert.Equal((AxisCube.PartKind.Ball, 5), (parts[0].Kind, parts[0].Axis));
        // +X to the right, +Y up on screen.
        var ball = (int axis) => parts.Single(p => p.Kind == AxisCube.PartKind.Ball && p.Axis == axis).Center;
        Assert.Equal(100 + 40 * AxisCube.BallDistance, ball(0).X, 3);
        Assert.Equal(100 - 40 * AxisCube.BallDistance, ball(1).Y, 3);
    }

    [Fact]
    public void HitTest_FindsTheFrontMostPart()
    {
        var camera = new OrbitCamera { Yaw = 0.6f, Pitch = 0.4f };
        var center = new Vector2(100, 100);
        var parts = AxisCube.Layout(camera.View, center, 40);
        foreach (int axis in new[] { 0, 1, 2 })
        {
            var ball = parts.Single(p => p.Kind == AxisCube.PartKind.Ball && p.Axis == axis);
            var hit = AxisCube.HitTest(parts, ball.Center);
            Assert.NotNull(hit);
            Assert.Equal(axis, hit!.Axis);
            Assert.Equal(AxisCube.PartKind.Ball, hit.Kind);
        }
        // The middle of a visible face hits that face.
        var face = parts.First(p => p.Kind == AxisCube.PartKind.Face);
        var faceHit = AxisCube.HitTest(parts, face.Center);
        Assert.Equal((AxisCube.PartKind.Face, face.Axis), (faceHit!.Kind, faceHit.Axis));
        Assert.Null(AxisCube.HitTest(parts, center + new Vector2(200, 0)));
    }

    [Fact]
    public void ConvexHull_DropsInnerPoints()
    {
        var hull = AxisCube.ConvexHull([new(0, 0), new(2, 0), new(2, 2), new(0, 2), new(1, 1)]);
        Assert.Equal(4, hull.Length);
        Assert.True(AxisCube.Contains(hull, new Vector2(1, 1)));
        Assert.False(AxisCube.Contains(hull, new Vector2(3, 1)));
    }

    private static readonly List<(Vector2 Min, Vector2 Max)> NoObstacles = [];

    [Fact]
    public void Place_SingleViewUsesTheBottomLeftCorner()
    {
        var c = AxisCube.Place(Vector2.Zero, new Vector2(800, 600), new Vector2(800, 600), NoObstacles, 45, 54);
        Assert.Equal(new Vector2(54, 546), c);
    }

    [Theory]
    // A 2x2 layout of 400x300 views in 800x600: each view takes the corner on the area's edge.
    [InlineData(0, 0, 54, 54)]        // top-left view: top-left corner
    [InlineData(400, 0, 746, 54)]     // top-right view: top-right corner
    [InlineData(0, 300, 54, 546)]     // bottom-left view: bottom-left corner
    [InlineData(400, 300, 746, 546)]  // bottom-right view: bottom-right corner
    public void Place_MultiViewTakesTheOuterCorner(float x, float y, float cx, float cy)
    {
        var c = AxisCube.Place(new Vector2(x, y), new Vector2(400, 300), new Vector2(800, 600), NoObstacles, 45, 54);
        Assert.Equal(new Vector2(cx, cy), c);
    }

    [Fact]
    public void Place_MovesInwardPastAnObstacle()
    {
        // A panel along the bottom-left 200x120: the cube moves up to clear it, staying at the left edge.
        var panel = new List<(Vector2, Vector2)> { (new Vector2(0, 480), new Vector2(200, 600)) };
        var c = AxisCube.Place(Vector2.Zero, new Vector2(800, 600), new Vector2(800, 600), panel, 45, 54)!.Value;
        Assert.Equal(54, c.X);
        Assert.True(c.Y <= 480 - 45, $"cube at {c} overlaps the panel");
        Assert.True(c.Y > 480 - 45 - 6, $"cube at {c} moved further than needed");
    }

    [Fact]
    public void Place_KeepsTheCornerWhenNothingIsFreeAndRefusesTinyViews()
    {
        var all = new List<(Vector2, Vector2)> { (Vector2.Zero, new Vector2(800, 600)) };
        Assert.Equal(new Vector2(54, 546), AxisCube.Place(Vector2.Zero, new Vector2(800, 600), new Vector2(800, 600), all, 45, 54));
        Assert.Null(AxisCube.Place(Vector2.Zero, new Vector2(100, 600), new Vector2(800, 600), NoObstacles, 45, 54));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void FlatAxis_IsTheAlignedHalfAxisOnlyWhenFlat(int axis)
    {
        var camera = new OrbitCamera { Distance = 10, Yaw = 1.1f, Pitch = 0.3f };
        camera.AlignTo(axis, animate: false);
        Assert.Null(camera.FlatAxis);
        camera.Orthographic = true;
        Assert.Equal(axis, camera.FlatAxis);
        camera.Orbit(0.2f, 0f);
        Assert.Null(camera.FlatAxis);
    }

    [Fact]
    public void PlaneGridLines_TurnTheGroundGridToFaceTheAxis()
    {
        float[] ground = [1, 0, 2, 0.5f, 0.5f, 0.5f];
        Assert.Equal([0, 1, 2, 0.5f, 0.5f, 0.5f], SceneRenderer.PlaneGridLines(ground, 0));
        Assert.Equal(ground, SceneRenderer.PlaneGridLines(ground, 4));
        Assert.Equal([1, 2, 0, 0.5f, 0.5f, 0.5f], SceneRenderer.PlaneGridLines(ground, 5));
    }
}
