using System.Numerics;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The viewport's pointer wiring: which gesture a press
/// starts, the 4-DIP slop that separates a click from an orbit, where a gizmo drag reports, and
/// whether a release picks or keeps the edit. The fake host records what
/// <see cref="ViewportInput"/> asks of the window, so the sequence is checked without a window.
/// </summary>
public class ViewportInputTests
{
    /// <summary>
    /// A window that records every call, with switchable answers for the parts a real window
    /// decides: the handle under the pointer, whether the editor takes the drag, and which view is
    /// under the pointer.
    /// </summary>
    private sealed class Host : IViewportPointerHost
    {
        /// <summary>The handle <see cref="GizmoAt"/> reports; -1 is off the gizmo.</summary>
        public int GizmoAxis = -1;

        /// <summary>Whether the editor takes a gizmo drag.</summary>
        public bool AcceptEdit = true;

        /// <summary>Whether a tool (the Skin Workshop's brush) takes a free drag.</summary>
        public bool AcceptFree;

        /// <summary>Whether the active view is the orbit camera.</summary>
        public bool IsOrbiting = true;

        /// <summary>Whether <see cref="ViewAt"/> follows the active view (a single view) or <see cref="ViewUnder"/>.</summary>
        public bool SingleView = true;

        public int ViewUnder;
        public int Active;
        public readonly List<string> Calls = [];
        public Vector2 EditFrom, EditTo, FreeFrom, FreeTo, OrbitDelta, PanDelta, ZoomDelta, PickedAt, RightClickedAt;
        public bool PickedToggle;

        public bool Orbiting => IsOrbiting;

        /// <summary>Whether the active view is an orthographic scene camera, which pans and zooms.</summary>
        public bool IsOrthographic;

        public bool Slides => IsOrbiting || IsOrthographic;

        public int ActiveView => Active;

        public int ViewAt(Vector2 position) => SingleView ? Active : ViewUnder;

        /// <summary>A viewport point in the edit area: this fake letterboxes with a 10-DIP top band.</summary>
        public Vector2 EditPoint(Vector2 position) => position + new Vector2(0, 10);

        public void FocusView(Vector2 position)
        {
            int view = ViewAt(position);
            Calls.Add("Focus:" + view);
            if (view >= 0) Active = view;
        }

        public int GizmoAt(Vector2 editPoint)
        {
            Calls.Add("GizmoAt");
            return GizmoAxis;
        }

        public bool BeginEdit(int axis, Vector2 editPoint)
        {
            Calls.Add("BeginEdit:" + axis);
            return AcceptEdit;
        }

        public bool BeginFree(Vector2 editPoint)
        {
            Calls.Add("BeginFree");
            return AcceptFree;
        }

        public void Edit(Vector2 from, Vector2 to)
        {
            Calls.Add("Edit");
            EditFrom = from;
            EditTo = to;
        }

        public void EndEdit(bool commit) => Calls.Add("EndEdit:" + commit);

        public void Free(Vector2 from, Vector2 to)
        {
            Calls.Add("Free");
            FreeFrom = from;
            FreeTo = to;
        }

        public void EndFree(bool commit) => Calls.Add("EndFree:" + commit);

        public void Pick(Vector2 editPoint, bool toggle)
        {
            Calls.Add("Pick:" + toggle);
            PickedAt = editPoint;
            PickedToggle = toggle;
        }

        public void RightClick(Vector2 editPoint)
        {
            Calls.Add("RightClick");
            RightClickedAt = editPoint;
        }

        public void Hover(Vector2 position) => Calls.Add("Hover");

        public void ClearHighlight() => Calls.Add("ClearHighlight");

        public void Orbit(Vector2 delta)
        {
            Calls.Add("Orbit");
            OrbitDelta = delta;
        }

        public void Pan(Vector2 delta)
        {
            Calls.Add("Pan");
            PanDelta = delta;
        }

        public void Zoom(Vector2 delta)
        {
            Calls.Add("Zoom");
            ZoomDelta = delta;
        }

        /// <summary>Whether a vertex selection gesture takes a modified press.</summary>
        public bool AcceptMarquee;

        public bool BeginMarquee(Vector2 editPoint, bool lasso)
        {
            Calls.Add("BeginMarquee:" + (lasso ? "lasso" : "rect"));
            return AcceptMarquee;
        }

        public void Marquee(Vector2 editPoint) => Calls.Add("Marquee");

        public void EndMarquee(bool commit) => Calls.Add("EndMarquee:" + commit);
    }

    [Fact]
    public void Press_WithASelectionModifier_DrawsAMarqueeInsteadOfPickingOrOrbiting()
    {
        var host = new Host { AcceptMarquee = true, GizmoAxis = 1 };
        var input = new ViewportInput();

        // A left press: a rectangle, ahead of the gizmo under the pointer.
        input.Press(host, Press, left: true, middle: false, selecting: true);
        Assert.True(input.Marquee);
        Assert.Equal(["Focus:0", "BeginMarquee:rect"], host.Calls);
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(20, 20), left: true, middle: false, right: false);
        Assert.Equal(["Marquee"], host.Calls);
        host.Calls.Clear();
        input.Release(host, Press + new Vector2(20, 20), left: true, right: false, toggle: false);
        Assert.Equal(["EndMarquee:True", "Hover"], host.Calls);
        Assert.False(input.Marquee);

        // A middle press: a lasso, which does not pan; Escape drops it.
        host.Calls.Clear();
        input.Press(host, Press, left: false, middle: true, selecting: true);
        Assert.Contains("BeginMarquee:lasso", host.Calls);
        input.Move(host, Press + new Vector2(5, 0), left: false, middle: true, right: false);
        Assert.DoesNotContain("Pan", host.Calls);
        Assert.True(input.Cancel(host));
        Assert.Equal("EndMarquee:False", host.Calls[^1]);

        // Refused (outside the Rigging gate): the press falls through to the gizmo.
        host.Calls.Clear();
        host.AcceptMarquee = false;
        input.Press(host, Press, left: true, middle: false, selecting: true);
        Assert.False(input.Marquee);
        Assert.Contains("BeginEdit:1", host.Calls);
    }

    private static readonly Vector2 Press = new(10, 10);

    [Fact]
    public void Press_OnAGizmoHandle_StartsThatHandlesDragAndMovesDragAtOnce()
    {
        var host = new Host { GizmoAxis = 2 };
        var input = new ViewportInput();

        input.Press(host, Press, left: true);

        Assert.True(input.Editing);
        Assert.False(input.Free);
        Assert.True(input.Tracking);
        Assert.Equal(["Focus:0", "GizmoAt", "BeginEdit:2"], host.Calls);

        // A handle drag is a drag from the first DIP: no slop applies.
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(1, 0), left: true, middle: false, right: false);
        Assert.Equal(["Edit"], host.Calls);
        Assert.Equal(new Vector2(10, 20), host.EditFrom); // the press point, in the edit area
        Assert.Equal(new Vector2(11, 20), host.EditTo);
        // The drag is reported although the pointer has not left the click: the slop is the
        // camera's rule, not the handle's.
        Assert.False(input.Moved);

        // The release keeps the edit and does not pick.
        host.Calls.Clear();
        input.Release(host, Press + new Vector2(1, 0), left: true, right: false, toggle: false);
        Assert.Equal(["EndEdit:True", "Hover"], host.Calls);
        Assert.False(input.Editing);
        Assert.False(input.Tracking);
    }

    [Fact]
    public void Press_WithoutAHandle_LetsAToolTakeTheDragAndOtherwiseLeavesTheCamera()
    {
        var brush = new Host { AcceptFree = true };
        var input = new ViewportInput();
        input.Press(brush, Press, left: true);
        Assert.True(input.Free);
        Assert.False(input.Editing);
        Assert.Equal(["Focus:0", "GizmoAt", "BeginFree"], brush.Calls);

        brush.Calls.Clear();
        input.Move(brush, Press + new Vector2(6, 0), left: true, middle: false, right: false);
        Assert.Equal(["Free"], brush.Calls);
        Assert.Equal(new Vector2(10, 20), brush.FreeFrom);
        Assert.Equal(new Vector2(16, 20), brush.FreeTo);

        brush.Calls.Clear();
        input.Release(brush, Press + new Vector2(6, 0), left: true, right: false, toggle: false);
        Assert.Equal(["EndFree:True", "Hover"], brush.Calls);
        Assert.False(input.Free);

        // Refused by the tool too, the same press leaves the camera the gesture: no call until the
        // pointer has moved past the slop.
        var camera = new Host();
        var plain = new ViewportInput();
        plain.Press(camera, Press, left: true);
        camera.Calls.Clear();
        plain.Move(camera, Press + new Vector2(3, 0), left: true, middle: false, right: false);
        Assert.Empty(camera.Calls);
        plain.Move(camera, Press + new Vector2(9, 0), left: true, middle: false, right: false);
        Assert.Equal(["Orbit"], camera.Calls);
        Assert.Equal(new Vector2(6, 0), camera.OrbitDelta);
    }

    [Fact]
    public void ARefusedGizmoDrag_FallsBackToTheTool()
    {
        var host = new Host { GizmoAxis = 1, AcceptEdit = false, AcceptFree = true };
        var input = new ViewportInput();

        input.Press(host, Press, left: true);

        Assert.False(input.Editing);
        Assert.True(input.Free);
        Assert.Equal(["Focus:0", "GizmoAt", "BeginEdit:1", "BeginFree"], host.Calls);
    }

    [Fact]
    public void ALeftPress_StaysAClickUpToTheSlop_ThenOrbits()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        host.Calls.Clear();

        // Exactly 4 DIP is still a click (the rule is "more than 4"), and 4.1 DIP starts the drag.
        input.Move(host, Press + new Vector2(4, 0), left: true, middle: false, right: false);
        Assert.Empty(host.Calls);
        Assert.False(input.Moved);
        input.Move(host, Press + new Vector2(4, 0), left: true, middle: false, right: false);
        Assert.Empty(host.Calls);
        input.Move(host, Press + new Vector2(4.5f, 0), left: true, middle: false, right: false);
        Assert.Equal(["Orbit"], host.Calls);
        Assert.True(input.Moved);
    }

    [Fact]
    public void AClickThatDidNotMove_PicksAtTheReleasePointAndCarriesTheToggle()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        input.Move(host, Press + new Vector2(2, 1), left: true, middle: false, right: false);
        host.Calls.Clear();

        input.Release(host, Press + new Vector2(2, 1), left: true, right: false, toggle: true);

        Assert.Equal(["Pick:True", "Hover"], host.Calls);
        Assert.Equal(new Vector2(12, 21), host.PickedAt); // the release point, in the edit area
        Assert.True(host.PickedToggle);
    }

    [Fact]
    public void ARightClick_ActsOnTheReleasePoint_AndARightDragOnlyZooms()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: false);
        Assert.False(input.Editing); // a right press starts no gizmo edit
        host.Calls.Clear();

        input.Release(host, Press + new Vector2(1, 1), left: false, right: true, toggle: false);

        Assert.Equal(["RightClick", "Hover"], host.Calls);
        Assert.Equal(new Vector2(11, 21), host.RightClickedAt); // the release point, in the edit area

        // The same right press with a drag is the zoom, and a drag that comes back does not act.
        var drag = new Host();
        var zooming = new ViewportInput();
        zooming.Press(drag, Press, left: false);
        drag.Calls.Clear();
        zooming.Move(drag, Press + new Vector2(20, 0), left: false, middle: false, right: true);
        Assert.Equal(["Zoom"], drag.Calls);
        drag.Calls.Clear();
        zooming.Move(drag, Press, left: false, middle: false, right: true);
        zooming.Release(drag, Press, left: false, right: true, toggle: false);
        Assert.Equal(["Zoom", "Hover"], drag.Calls); // the drag back zooms out again, and acts on nothing

        // A middle press released without moving is not a right click.
        var middle = new Host();
        var panning = new ViewportInput();
        panning.Press(middle, Press, left: false);
        middle.Calls.Clear();
        panning.Release(middle, Press, left: false, right: false, toggle: false);
        Assert.Equal(["Hover"], middle.Calls);
    }

    [Fact]
    public void ADragThatCameBackToThePress_DoesNotPick()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        input.Move(host, Press + new Vector2(10, 0), left: true, middle: false, right: false);
        input.Move(host, Press, left: true, middle: false, right: false);
        Assert.True(input.Moved);
        host.Calls.Clear();

        input.Release(host, Press, left: true, right: false, toggle: false);

        Assert.Equal(["Hover"], host.Calls);
    }

    [Fact]
    public void AReleaseOverAnotherView_DoesNotPick()
    {
        var host = new Host { SingleView = false, Active = 0, ViewUnder = 0 };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        Assert.Equal(0, host.Active);

        // The pointer moves into the second view and comes back to the active one: a move over
        // another view clears the gizmo's hover handle, it does not orbit.
        host.ViewUnder = 1;
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(20, 0), left: true, middle: false, right: false);
        Assert.Equal(["ClearHighlight"], host.Calls);
        Assert.True(input.Moved);

        host.Calls.Clear();
        input.Release(host, Press + new Vector2(20, 0), left: true, right: false, toggle: false);
        Assert.Equal(["Hover"], host.Calls);
    }

    [Fact]
    public void APressInAnotherView_FocusesItBeforeTheHandleIsAsked()
    {
        var host = new Host { SingleView = false, Active = 0, ViewUnder = 1, GizmoAxis = 0 };
        var input = new ViewportInput();

        input.Press(host, Press, left: true);

        Assert.Equal(["Focus:1", "GizmoAt", "BeginEdit:0"], host.Calls);
        Assert.Equal(1, host.Active);
    }

    [Fact]
    public void ASceneCameraView_DoesNotOrbitTheLeftDrag()
    {
        var host = new Host { IsOrbiting = false };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        host.Calls.Clear();

        input.Move(host, Press + new Vector2(30, 0), left: true, middle: false, right: false);

        Assert.Empty(host.Calls);
        Assert.True(input.Moved);
    }

    [Fact]
    public void AnOrthographicView_PansAndZoomsButNeverOrbits()
    {
        var host = new Host { IsOrbiting = false, IsOrthographic = true };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(30, 0), left: true, middle: false, right: false);
        Assert.Empty(host.Calls);
        input.Release(host, Press + new Vector2(30, 0), left: true, right: false, toggle: false);

        input.Press(host, Press, left: false);
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(4, 2), left: false, middle: true, right: false);
        Assert.Equal(["Pan"], host.Calls);
        Assert.Equal(new Vector2(4, 2), host.PanDelta);

        host.Calls.Clear();
        input.Move(host, Press + new Vector2(4, 9), left: false, middle: false, right: true);
        Assert.Equal(["Zoom"], host.Calls);

        // A stored perspective or animated camera view still takes neither.
        host.IsOrthographic = false;
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(8, 9), left: false, middle: true, right: false);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void AnOrthographicCamera_PansByTheDragInSceneUnitsAndZoomsItsHeight()
    {
        // 500 units across 250 DIPs: one DIP is two units. Dragging right moves the scene right,
        // so the offset goes left; dragging down moves the scene down, so the offset goes up.
        var offset = SceneCameraView.PannedOffset(new Vector2(10, 20), 500, new Vector2(5, 3), 250);
        Assert.Equal(new Vector2(0, 26), offset);

        // The point under the pointer stays under it: project before and after.
        var viewport = new Vector2(400, 250);
        var before = SceneCameraView.Ortho(0, 500, new Vector2(10, 20)).Project(new Vector3(30, 40, 0), viewport)!.Value;
        var after = SceneCameraView.Ortho(0, 500, offset).Project(new Vector3(30, 40, 0), viewport)!.Value;
        Assert.Equal(5, after.X - before.X, 2);
        Assert.Equal(3, after.Y - before.Y, 2);

        Assert.Equal(250, SceneCameraView.ZoomedHeight(500, 0.5f), 3);
        Assert.Equal(SceneCameraView.DefaultOrthoHeight * 2, SceneCameraView.ZoomedHeight(0, 2f), 3);
        Assert.Equal(SceneCameraView.MinOrthoHeight, SceneCameraView.ZoomedHeight(2, 1e-6f), 3);
        Assert.Equal(SceneCameraView.MaxOrthoHeight, SceneCameraView.ZoomedHeight(1e5f, 1e6f), 3);
    }

    [Fact]
    public void AMiddleDrag_PansAtOnceAndNeverPicks()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: false);
        host.Calls.Clear();

        input.Move(host, Press + new Vector2(1, 0), left: false, middle: true, right: false);
        Assert.Equal(["Pan"], host.Calls);
        Assert.Equal(new Vector2(1, 0), host.PanDelta);

        host.Calls.Clear();
        input.Release(host, Press + new Vector2(1, 0), left: false, right: false, toggle: false);
        Assert.Equal(["Hover"], host.Calls); // a non-left press is never a pick
    }

    [Fact]
    public void ARightDrag_ZoomsAtOnceAndNeverPicks()
    {
        var host = new Host();
        var input = new ViewportInput();
        input.Press(host, Press, left: false);
        host.Calls.Clear();

        input.Move(host, Press + new Vector2(1, -1), left: false, middle: false, right: true);
        Assert.Equal(["Zoom"], host.Calls);
        Assert.Equal(new Vector2(1, -1), host.ZoomDelta);

        // With both buttons down the middle drag wins, so a right button pressed by accident
        // during a pan cannot dolly the camera as well.
        host.Calls.Clear();
        input.Move(host, Press + new Vector2(2, -1), left: false, middle: true, right: true);
        Assert.Equal(["Pan"], host.Calls);

        host.Calls.Clear();
        input.Release(host, Press + new Vector2(2, -1), left: false, right: false, toggle: false);
        Assert.Equal(["Hover"], host.Calls);
    }

    [Fact]
    public void ARightDragZoom_IsInOnRightAndUp_AndOutOnLeftAndDown()
    {
        var camera = new OrbitCamera { Distance = 10 };

        // Right (and up, a negative y) zoom in, left and down zoom out; either axis alone is enough.
        camera.DragZoom(new Vector2(40, 0));
        Assert.True(camera.Distance < 10f, $"right: {camera.Distance}");
        camera.Distance = 10;
        camera.DragZoom(new Vector2(0, -40));
        Assert.True(camera.Distance < 10f, $"up: {camera.Distance}");
        camera.Distance = 10;
        camera.DragZoom(new Vector2(-40, 0));
        Assert.True(camera.Distance > 10f, $"left: {camera.Distance}");
        camera.Distance = 10;
        camera.DragZoom(new Vector2(0, 40));
        Assert.True(camera.Distance > 10f, $"down: {camera.Distance}");

        // Both axes count: a diagonal up-right drag dollies twice as far as either leg alone, and a
        // drag back to the start undoes the dolly it made.
        camera.Distance = 10;
        camera.DragZoom(new Vector2(40, -40));
        Assert.Equal(10 * MathF.Pow(OrbitCamera.DragZoomPerDip, 80), camera.Distance, 5);
        camera.DragZoom(new Vector2(-40, 40));
        Assert.Equal(10f, camera.Distance, 4);

        // A right drag never passes the focus point: the distance stays positive.
        camera.DragZoom(new Vector2(100_000, 0));
        Assert.True(camera.Distance > 0f);
    }

    [Fact]
    public void Escape_DropsTheEdit_AndTheNextReleaseNeitherKeepsItNorPicks()
    {
        var host = new Host { GizmoAxis = 1 };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        input.Move(host, Press + new Vector2(5, 0), left: true, middle: false, right: false);
        host.Calls.Clear();

        Assert.True(input.Cancel(host));
        Assert.Equal(["EndEdit:False"], host.Calls);
        Assert.False(input.Editing);
        Assert.False(input.Tracking);

        host.Calls.Clear();
        input.Release(host, Press + new Vector2(5, 0), left: true, right: false, toggle: false);
        Assert.Equal(["Hover"], host.Calls);

        // Nothing to cancel once the drag is gone.
        Assert.False(input.Cancel(host));
    }

    [Fact]
    public void ALostCapture_KeepsTheDragOnce_AndTheReleaseAddsNothing()
    {
        var host = new Host { AcceptFree = true };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);
        input.Move(host, Press + new Vector2(5, 0), left: true, middle: false, right: false);
        host.Calls.Clear();

        input.CaptureLost(host);
        Assert.Equal(["EndFree:True"], host.Calls);
        Assert.False(input.Free);
        Assert.False(input.Tracking);

        host.Calls.Clear();
        input.Release(host, Press + new Vector2(5, 0), left: true, right: false, toggle: false);
        Assert.Equal(["Hover"], host.Calls);
    }

    [Fact]
    public void ADragAlwaysReportsThePressPointAsItsStart()
    {
        var host = new Host { GizmoAxis = 0 };
        var input = new ViewportInput();
        input.Press(host, Press, left: true);

        input.Move(host, Press + new Vector2(10, 0), left: true, middle: false, right: false);
        Assert.Equal(new Vector2(10, 20), host.EditFrom);
        Assert.Equal(new Vector2(20, 20), host.EditTo);

        input.Move(host, Press + new Vector2(20, 0), left: true, middle: false, right: false);
        Assert.Equal(new Vector2(10, 20), host.EditFrom);
        Assert.Equal(new Vector2(30, 20), host.EditTo);
    }

    [Fact]
    public void AMoveWithNoPress_IsOnlyTheHover()
    {
        var host = new Host();
        var input = new ViewportInput();

        input.Move(host, Press, left: true, middle: false, right: false);

        Assert.Equal(["Hover"], host.Calls);
        Assert.False(input.Tracking);
    }
}
