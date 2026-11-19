using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The solid gizmo handles (plane-translate triangles, thick shafts), the Spinner dial's layout and
/// drag amounts, and the preferences fallback behind the Recent Scenes menu. No sample is needed.
/// </summary>
public class GizmoHandleTests
{
    private static readonly Vector2 Viewport = new(800, 600);

    private static OrbitCamera Tilted() => new() { Target = Vector3.Zero, Distance = 5, Yaw = 0.6f, Pitch = 0.4f };

    private static OrbitCamera Front() => new() { Target = Vector3.Zero, Distance = 5, Yaw = 0, Pitch = 0 };

    [Fact]
    public void PlaneHandles_AreHitInsideTheirTriangle_AndNamedAfterTheAxisTheyLieAcross()
    {
        var camera = Tilted();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true };
        var triangles = gizmo.PlaneTriangles(camera).ToList();
        Assert.Equal([TransformGizmo.PlaneX, TransformGizmo.PlaneX + 1, TransformGizmo.PlaneX + 2],
            triangles.Select(t => t.Handle).OrderBy(h => h));

        foreach (var (a, b, c, handle) in triangles)
        {
            Assert.True(TransformGizmo.IsPlane(handle));
            int normal = TransformGizmo.PlaneNormal(handle);
            Assert.Equal(handle - TransformGizmo.PlaneX, normal);
            var (first, second) = TransformGizmo.PlaneAxes(handle);
            Assert.Equal([0, 1, 2], new[] { normal, first, second }.OrderBy(i => i));
            // The triangle lies in the plane of the two other axes.
            foreach (var corner in new[] { a, b, c })
                Assert.Equal(0f, Vector3.Dot(corner - gizmo.Center, gizmo.Axis(normal)), 4);
            var centroid = camera.Project((a + b + c) / 3, Viewport)!.Value;
            Assert.Equal(handle, gizmo.HitTest(camera, Viewport, centroid, 4));
        }

        Assert.False(TransformGizmo.IsPlane(0));
        Assert.False(TransformGizmo.IsPlane(TransformGizmo.Uniform));

        // No plane handles outside translate mode, or when translation cannot be keyed.
        gizmo.Mode = GizmoMode.Rotate;
        Assert.Empty(gizmo.PlaneTriangles(camera));
        gizmo.Mode = GizmoMode.Translate;
        gizmo.CanTranslate = false;
        var inside = camera.Project((triangles[0].A + triangles[0].B + triangles[0].C) / 3, Viewport)!.Value;
        Assert.Equal(-1, gizmo.HitTest(camera, Viewport, inside, 4));
    }

    [Fact]
    public void PlaneHandles_SeenEdgeOn_AreLeftOut()
    {
        // Looking straight down an axis, the two planes through it are edge-on.
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true };
        Assert.Single(gizmo.PlaneTriangles(Front()));
    }

    [Fact]
    public void DragPlane_ReturnsTheLocalDistancesAlongBothAxes()
    {
        var camera = Front();
        // A parent three times as large: one local unit is three world units.
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true,
            ParentFrame = Matrix4x4.CreateScale(3) };
        int handle = gizmo.PlaneTriangles(camera).Single().Handle;
        var (first, second) = TransformGizmo.PlaneAxes(handle);
        Vector3 Unit(int axis) => axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

        var from = camera.Project(Vector3.Zero, Viewport)!.Value;
        var to = camera.Project(3 * (0.2f * Unit(first) - 0.1f * Unit(second)), Viewport)!.Value;
        var moved = gizmo.DragPlane(camera, Viewport, handle, to - from);
        Assert.Equal(0.2f, moved.X, 3);
        Assert.Equal(-0.1f, moved.Y, 3);
        Assert.Equal(Vector2.Zero, gizmo.DragPlane(camera, Viewport, handle, Vector2.Zero));
    }

    [Fact]
    public void ThickShaft_IsHitOffItsCentreLine_ButNotFarFromIt()
    {
        var camera = Front();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, CanTranslate = true };
        float size = gizmo.Size(camera);
        var onX = camera.Project(new Vector3(size * 0.8f, 0, 0), Viewport)!.Value;
        float half = TransformGizmo.ShaftHalfWidth * TransformGizmo.SizeOfView * Viewport.Y;
        Assert.Equal(0, gizmo.HitTest(camera, Viewport, onX + new Vector2(0, half + 3), 4));
        Assert.Equal(-1, gizmo.HitTest(camera, Viewport, onX + new Vector2(0, half + 12), 4));
    }

    [Theory]
    [InlineData(GizmoMode.Translate)]
    [InlineData(GizmoMode.Rotate)]
    [InlineData(GizmoMode.Scale)]
    public void WriteTriangles_EmitsWholeTriangles_AndNothingWhenHidden(GizmoMode mode)
    {
        var gizmo = new TransformGizmo { Visible = true, Mode = mode, CanTranslate = true, CanScale = true };
        var vertices = new List<float>();
        gizmo.WriteTriangles(vertices, Tilted());
        Assert.NotEmpty(vertices);
        Assert.Equal(0, vertices.Count % 18); // three vertices of position + colour
        Assert.All(vertices, v => Assert.True(float.IsFinite(v)));

        gizmo.Visible = false;
        vertices.Clear();
        gizmo.WriteTriangles(vertices, Tilted());
        Assert.Empty(vertices);
    }

    [Fact]
    public void RotateRings_DrawTheFrameQuadrantFacingTheCamera_UnlessSeenFaceOn()
    {
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Rotate };
        var camera = Tilted();
        float size = gizmo.Size(camera);
        var toEye = Vector3.Normalize(camera.Eye - gizmo.Center);
        for (int axis = 0; axis < 3; axis++)
        {
            var segments = gizmo.Segments(camera).Where(s => s.Axis == axis).ToList();
            Assert.Equal(16, segments.Count);
            // The quadrant is fixed by the gizmo frame: bounded by the other two axes, on the eye's side of both.
            var a = gizmo.Axis((axis + 1) % 3);
            var b = gizmo.Axis((axis + 2) % 3);
            var mid = Vector3.Normalize(MathF.Sign(Vector3.Dot(toEye, a)) * a + MathF.Sign(Vector3.Dot(toEye, b)) * b);
            foreach (var (p, q, _) in segments)
                foreach (var x in new[] { p, q })
                    Assert.True(Vector3.Dot(x - gizmo.Center, mid) >= size * MathF.Cos(MathF.PI / 4) - 1e-3f * size);
        }

        // A small camera move inside the same quadrants leaves the arcs where they are (they snap, not slide).
        var nudged = Tilted();
        nudged.Yaw += 0.05f;
        var before = gizmo.Segments(camera).Select(s => (s.A - gizmo.Center) / size).ToList();
        var after = gizmo.Segments(nudged).Select(s => (s.A - gizmo.Center) / gizmo.Size(nudged)).ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++) Assert.True(Vector3.Distance(before[i], after[i]) < 1e-4f);

        // Looking down one axis, its ring faces the viewer and is drawn whole.
        var counts = Enumerable.Range(0, 3).Select(axis => gizmo.Segments(Front()).Count(s => s.Axis == axis)).OrderBy(n => n).ToArray();
        Assert.Equal([16, 16, 64], counts);
    }

    [Fact]
    public void RotateRings_FaceOnToTheImagePlane_DrawWhole_WhereverTheGizmoSits()
    {
        // A ring whose plane is parallel to the image plane projects to a circle, and no part of it is
        // nearer than another, so it is drawn whole wherever the gizmo sits.
        // That is decided by the view direction, not by the eye ray to an off-centre gizmo, and Screen
        // space's depth ring always lies in the image plane.
        var camera = Front();
        var gizmo = new TransformGizmo
        {
            Visible = true, Mode = GizmoMode.Rotate, Frame = Matrix4x4.CreateTranslation(1.5f, 1.2f, 0f),
        };
        int[] Counts() => Enumerable.Range(0, 3).Select(axis => gizmo.Segments(camera).Count(s => s.Axis == axis)).ToArray();

        // Local: the frame is a translation, so its axes are the world's and Z faces the view.
        Assert.Equal([16, 16, 64], Counts());

        // Screen: right and up are edge-on, the depth ring is face-on, at any gizmo position.
        gizmo.Space = TransformSpace.Screen;
        Assert.Equal([16, 16, 64], Counts());

        // The plane handles use the same direction: only the screen plane is usable.
        gizmo.Mode = GizmoMode.Translate;
        gizmo.CanTranslate = true;
        Assert.Equal([TransformGizmo.PlaneX + 2], gizmo.PlaneTriangles(camera).Select(p => p.Handle));
    }

    [Fact]
    public void ScreenSpaceRings_KeepTheSameQuarter_UpAndLeftOnScreen_WhereverTheGizmoSits()
    {
        // In Screen space the frame is the camera's, so the two edge-on rings have a fixed quarter on
        // the image plane instead of one that follows the eye: the red (X) ring runs from the centre
        // up to the rim small enough to see, the green (Y) ring from the rim left to the centre, and
        // the blue (Z) depth ring is always a full circle. Neither the gizmo's place on screen nor
        // where the eye is turns them.
        var camera = Front();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Rotate, Space = TransformSpace.Screen };
        foreach (var where in new[] { Vector3.Zero, new Vector3(1.4f, -1.1f, 0f), new Vector3(-1.2f, 1.3f, 0f) })
        {
            gizmo.Frame = Matrix4x4.CreateTranslation(where);
            float size = gizmo.Size(camera);
            var center = camera.Project(where, Viewport)!.Value;
            Vector2 P(Vector3 p) => camera.Project(p, Viewport)!.Value - center;
            // How far the handle length reaches on screen along the camera's up axis.
            float reach = -P(where + gizmo.Axis(1, camera) * size).Y;

            // X's quarter is the one above the centre: its midpoint leans from the camera's up towards
            // the viewer, and it is the same quarter whichever gizmo this is.
            var (ux, _, fromX, toX) = gizmo.RingArc(0, camera);
            Assert.Equal(-MathF.PI / 4, fromX, 4);
            Assert.Equal(MathF.PI / 4, toX, 4);
            Assert.True(Vector3.Dot(ux, gizmo.Axis(1, camera)) > 0.5f);
            Assert.True(Vector3.Dot(ux, gizmo.Axis(2, camera)) > 0.5f);
            Assert.True(P(where + ux * size).Y < 0);
            Assert.Equal(16, gizmo.Segments(camera).Count(s => s.Axis == 0));

            // Y's quarter is the one to the left of the centre.
            var (uy, _, fromY, toY) = gizmo.RingArc(1, camera);
            Assert.Equal(-MathF.PI / 4, fromY, 4);
            Assert.Equal(MathF.PI / 4, toY, 4);
            Assert.True(Vector3.Dot(uy, -gizmo.Axis(0, camera)) > 0.5f);
            Assert.True(Vector3.Dot(uy, gizmo.Axis(2, camera)) > 0.5f);
            Assert.True(P(where + uy * size).X < 0);
            Assert.Equal(16, gizmo.Segments(camera).Count(s => s.Axis == 1));

            // Z's ring is whole and projects to a circle about the gizmo's centre, wherever it sits.
            var z = gizmo.Segments(camera).Where(s => s.Axis == 2).ToList();
            Assert.Equal(64, z.Count);
            foreach (var (p, q, _) in z)
                foreach (var r in new[] { P(p).Length(), P(q).Length() })
                    Assert.Equal(reach, r, 2);
        }
    }

    [Fact]
    public void HandleUnits_IsTheHandleLengthInTheParentsUnits()
    {
        var camera = Front();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, ParentFrame = Matrix4x4.CreateScale(4) };
        Assert.Equal(gizmo.Size(camera) / 4, gizmo.HandleUnits(camera, 1), 4);
        gizmo.ParentFrame = new Matrix4x4();
        Assert.Equal(0f, gizmo.HandleUnits(camera, 1));
    }

    [Fact]
    public void SpinnerDial_Hit_FindsSectorsButtonsAndTheScaleCentre()
    {
        var size = new Vector2(SpinnerDial.Width, SpinnerDial.Height);
        var c = SpinnerDial.Center(size);
        float r = SpinnerDial.Radius(size);
        Vector2 At(float radius, float angle) => c + radius * r * new Vector2(MathF.Cos(angle), MathF.Sin(angle));

        // Red at the top, green bottom-left, blue bottom-right.
        Assert.Equal(new SpinnerHit(SpinnerPart.Axis, 0), SpinnerDial.Hit(size, c + new Vector2(0, -0.7f * r), GizmoMode.Rotate));
        Assert.Equal(new SpinnerHit(SpinnerPart.Axis, 1), SpinnerDial.Hit(size, c + new Vector2(-0.5f * r, 0.4f * r), GizmoMode.Rotate));
        Assert.Equal(new SpinnerHit(SpinnerPart.Axis, 2), SpinnerDial.Hit(size, c + new Vector2(0.5f * r, 0.4f * r), GizmoMode.Rotate));
        for (int axis = 0; axis < 3; axis++)
            Assert.Equal(axis, SpinnerDial.Hit(size, At(0.6f, SpinnerDial.SectorAngle(axis)), GizmoMode.Translate).Axis);

        // The gap between two sectors, the hub and the rim past the disc are the frame (it moves the window).
        float gap = (SpinnerDial.SectorAngle(0) + MathF.PI / 3);
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, At(0.6f, gap), GizmoMode.Rotate).Part);
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, c, GizmoMode.Rotate).Part);
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, c, GizmoMode.Translate).Part);
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, At(1.2f, SpinnerDial.SectorAngle(2)), GizmoMode.Rotate).Part);
        var frame = SpinnerDial.FrameCenter(size);
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, frame + new Vector2(-SpinnerDial.FrameRadius + 3, 0), GizmoMode.Rotate).Part);
        // Outside the round frame: nothing, even inside the window's rectangle.
        Assert.Equal(SpinnerPart.None, SpinnerDial.Hit(size, new Vector2(3, 3), GizmoMode.Rotate).Part);

        // The centre circle: uniform scale in scale mode, the IK swivel in rotate mode, nothing in translate.
        Assert.Equal(new SpinnerHit(SpinnerPart.Uniform, TransformGizmo.Uniform), SpinnerDial.Hit(size, c, GizmoMode.Scale));
        Assert.Equal(new SpinnerHit(SpinnerPart.Uniform, TransformGizmo.Uniform), SpinnerDial.Hit(size, c, GizmoMode.Rotate, swivel: true));
        Assert.Equal(SpinnerPart.Frame, SpinnerDial.Hit(size, c, GizmoMode.Translate, swivel: true).Part);
        Assert.Equal(SpinnerPart.Axis, SpinnerDial.Hit(size, At(0.6f, SpinnerDial.SectorAngle(1)), GizmoMode.Scale).Part);

        // The space button sits in the ring on the right: inside the rim, just clear of the disc.
        var space = SpinnerDial.SpaceCenter(size);
        Assert.True(Vector2.Distance(space, frame) + SpinnerDial.SpaceRadius <= SpinnerDial.FrameRadius);
        Assert.True(Vector2.Distance(space, frame) - SpinnerDial.SpaceRadius >= SpinnerDial.DiscRadius);
        Assert.True(space.X > frame.X);
        Assert.Equal(SpinnerPart.Space, SpinnerDial.Hit(size, space, GizmoMode.Rotate).Part);
        Assert.Equal(SpinnerPart.None, SpinnerDial.Hit(size, space + new Vector2(SpinnerDial.FrameRadius, 0), GizmoMode.Rotate).Part);
        // The button cycles in the order of the 1 Local, 2 Screen, 3 World keys.
        Assert.Equal(TransformSpace.Screen, SpinnerDial.NextSpace(TransformSpace.Local));
        Assert.Equal(TransformSpace.World, SpinnerDial.NextSpace(TransformSpace.Screen));

        // Mode buttons: translate, rotate, scale, left to right, leaning round the top of the ring.
        Assert.Equal([GizmoMode.Translate, GizmoMode.Rotate, GizmoMode.Scale], SpinnerDial.Modes);
        float lastLeft = float.NegativeInfinity;
        for (int i = 0; i < SpinnerDial.Modes.Length; i++)
        {
            var (min, max) = SpinnerDial.ModeButton(size, i);
            var mid = (min + max) / 2;
            Assert.Equal(SpinnerDial.ModeButtonCenter(size, i), mid);
            Assert.True(mid.X > lastLeft);
            lastLeft = mid.X;
            var hit = SpinnerDial.Hit(size, mid, GizmoMode.Scale);
            Assert.Equal(SpinnerPart.Mode, hit.Part);
            Assert.Equal(SpinnerDial.Modes[i], hit.Mode);
            Assert.True(SpinnerDial.Contains(size, mid));
            // In the band between the disc's edge and the rim, above the centre.
            Assert.True(Vector2.Distance(mid, frame) > SpinnerDial.DiscRadius);
            Assert.True(Vector2.Distance(mid, frame) + SpinnerDial.ButtonHeight / 2 <= SpinnerDial.FrameRadius);
            Assert.True(mid.Y < frame.Y);
        }
        // The buttons follow the curvature: the outer two lean the other way, the middle one is level.
        Assert.True(SpinnerDial.ModeButtonRotation(0) < 0 && SpinnerDial.ModeButtonRotation(2) > 0);
        Assert.Equal(0f, SpinnerDial.ModeButtonRotation(1), 5);
        // A turned button is hit along its long side, and only within its short one.
        var turned = SpinnerDial.ModeButtonCenter(size, 0);
        float lean = SpinnerDial.ModeButtonRotation(0);
        var alongLong = new Vector2(MathF.Cos(lean), MathF.Sin(lean));
        var alongShort = new Vector2(-MathF.Sin(lean), MathF.Cos(lean));
        Assert.Equal(SpinnerPart.Mode, SpinnerDial.Hit(size, turned + 14 * alongLong, GizmoMode.Rotate).Part);
        Assert.Equal(SpinnerPart.Mode, SpinnerDial.Hit(size, turned + 10 * alongShort, GizmoMode.Rotate).Part);
        Assert.NotEqual(SpinnerPart.Mode, SpinnerDial.Hit(size, turned + 16 * alongLong, GizmoMode.Rotate).Part);
        Assert.NotEqual(SpinnerPart.Mode, SpinnerDial.Hit(size, turned + 13 * alongShort, GizmoMode.Rotate).Part);
        Assert.True(SpinnerDial.Contains(size, c));
        Assert.False(SpinnerDial.Contains(size, new Vector2(1, size.Y - 1)));
    }

    [Fact]
    public void SpinnerDial_Swept_IsClockwisePositive_AndSumsPastAFullTurn()
    {
        var c = new Vector2(100, 100);
        // Screen y runs down: from the top of the dial to its right side is a clockwise quarter turn.
        Assert.Equal(MathF.PI / 2, SpinnerDial.Swept(c, c + new Vector2(0, -50), c + new Vector2(50, 0)), 4);
        Assert.Equal(-MathF.PI / 2, SpinnerDial.Swept(c, c + new Vector2(0, -50), c + new Vector2(-20, 0)), 4);
        Assert.Equal(0f, SpinnerDial.Swept(c, c, c + new Vector2(5, 5)));

        float total = 0;
        var last = c + new Vector2(40, 0);
        for (int i = 1; i <= 30; i++)
        {
            float angle = i * (3 * MathF.PI / 30);
            var next = c + 40 * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            total += SpinnerDial.Swept(c, last, next);
            last = next;
        }
        Assert.Equal(3 * MathF.PI, total, 3);
    }

    [Fact]
    public void SpinnerDial_DragAmounts()
    {
        Assert.Equal(1f, SpinnerDial.MoveHandles(MathF.PI / 2), 5);
        Assert.Equal(-2f, SpinnerDial.MoveHandles(-MathF.PI), 5);
        Assert.Equal(1f, SpinnerDial.ScaleFactor(0));
        Assert.Equal(2f, SpinnerDial.ScaleFactor(MathF.PI), 5);
        Assert.Equal(0.5f, SpinnerDial.ScaleFactor(-MathF.PI / 2), 5);
        Assert.Equal(0f, SpinnerDial.ScaleFactor(-3 * MathF.PI));
        Assert.Equal(2f, SpinnerDial.UniformFactor(new Vector2(0, -70), 70), 5);
        Assert.Equal(1.5f, SpinnerDial.UniformFactor(new Vector2(40, -35), 70), 5);
        Assert.Equal(1f, SpinnerDial.UniformFactor(new Vector2(500, 0), 70));
        Assert.Equal(0f, SpinnerDial.UniformFactor(new Vector2(0, 500), 70));
        Assert.Equal(1f, SpinnerDial.UniformFactor(new Vector2(10, 0), 0));
    }

    [Fact]
    public void SpinnerDial_SectorDragsUseOnlyVerticalMovement()
    {
        // 180 DIPs up is a quarter turn, whatever the horizontal movement.
        Assert.Equal(MathF.PI / 2, SpinnerDial.VerticalTurn(new Vector2(0, -180)), 5);
        Assert.Equal(MathF.PI / 2, SpinnerDial.VerticalTurn(new Vector2(300, -180)), 5);
        Assert.Equal(0f, SpinnerDial.VerticalTurn(new Vector2(-250, 0)));
        Assert.Equal(-10f, SpinnerDial.FootDegrees(new Vector2(99, 20)), 5);
    }

    [Fact]
    public void SpinnerDial_Place_DefaultsClearOfThePanels_AndStaysInside()
    {
        var host = new Vector2(1600, 870);
        var at = SpinnerDial.Place(host, null);
        Assert.Equal(host.X - SpinnerDial.DefaultRight - SpinnerDial.Width, at.X);
        Assert.Equal(host.Y - SpinnerDial.DefaultBottom - SpinnerDial.Height, at.Y);
        Assert.Equal(new Vector2(300, 200), SpinnerDial.Place(host, new Vector2(300, 200)));
        Assert.Equal(new Vector2(host.X - SpinnerDial.Width, 0), SpinnerDial.Place(host, new Vector2(5000, -40)));
        // A view area smaller than the dial pins it to the corner.
        Assert.Equal(Vector2.Zero, SpinnerDial.Place(new Vector2(100, 100), null));
    }

    [Fact]
    public void Preferences_OpenFirstUsable_FallsBackPastAnUnusableFile_AndKeepsRecentScenes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // A folder where the file should be cannot be read as preferences.
            string blocked = Path.Combine(folder, "blocked.akt");
            Directory.CreateDirectory(blocked);
            string garbage = Path.Combine(folder, "garbage.akt");
            File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8]);
            string portable = Path.Combine(folder, "profile", "preferences.akt");

            var preferences = Preferences.OpenFirstUsable([blocked, garbage, portable], requireWritable: true, out var problems);
            Assert.NotNull(preferences);
            Assert.Equal(portable, preferences.Path);
            Assert.True(File.Exists(portable));
            Assert.Equal(2, problems.Count);
            Assert.StartsWith(blocked, problems[0]);
            Assert.StartsWith(garbage, problems[1]);

            // What the Recent Scenes menu needs: an entry survives a save and a second start.
            string scene = Path.Combine(folder, "scene.akt");
            preferences.AddRecentFile(scene);
            preferences.Save();
            var again = Preferences.OpenFirstUsable([blocked, portable], requireWritable: true, out problems);
            Assert.NotNull(again);
            Assert.Single(problems);
            Assert.Contains(again.RecentFiles, f => string.Equals(
                Path.GetFullPath(f), Path.GetFullPath(scene), StringComparison.OrdinalIgnoreCase));

            Assert.Null(Preferences.OpenFirstUsable([blocked], requireWritable: true, out problems));
            Assert.Single(problems);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
