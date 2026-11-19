using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Curveboard's pointer rules: the frames
/// line up with the timeline, a press picks the key or tangent handle drawn under it, Shift adds and
/// Ctrl toggles, a drag moves values in value units and a handle on its own side, a box selects what
/// it encloses, and the wheel zooms about the pointer. The viewer draws and presses through the same
/// <see cref="CurveboardLayout"/>, so none of this needs a window.
/// </summary>
public class CurveboardLayoutTests
{
    private const double Width = 1200;
    private const double Height = 400;

    [Fact]
    public void WithoutAStack_NothingIsDrawnAndTheGutterIsNotAKey()
    {
        var layout = new CurveboardLayout { Width = 800, Height = 300 };
        Assert.Null(layout.Stack);
        Assert.Empty(layout.KeyPoints());
        Assert.Empty(layout.Handles());
        Assert.Equal((0, 1), layout.Range);
        Assert.True(layout.FrameWidth > 0);
        Assert.Null(layout.KeyAt(new Vector2(400, 150)));
        Assert.Null(layout.HandleAt(new Vector2(400, 150)));
        // Empty space is a box, but the gutter and the space left of it are not.
        Assert.Equal(CurveboardGesture.Box, layout.Press(new Vector2(400, 150), false, false).Gesture);
        Assert.Equal(CurveboardGesture.None, layout.Press(new Vector2(10, 150), false, false).Gesture);
        Assert.Empty(layout.Selection);
    }

    [Fact]
    public void FramesAndValues_RoundTrip()
    {
        var layout = new CurveboardLayout { Width = Width, Height = Height, YMin = -2, YMax = 6 };
        var (start, end) = layout.Range;
        // A click anywhere is clamped to the shown frames.
        Assert.Equal(start, layout.FrameUnder(-5000));
        Assert.Equal(end, layout.FrameUnder(Width + 5000));
        for (float frame = start; frame <= end; frame++)
        {
            Assert.Equal(frame, layout.FrameUnder(layout.X(frame)));
            Assert.Equal(frame, layout.FrameAt(layout.X(frame)), 4);
        }
        // Values map to y and back.
        for (double v = -2; v <= 6; v += 0.5)
            Assert.Equal(v, layout.ValueAt(layout.Y(v)), 4);
        Assert.Equal(0, layout.Y(layout.YMax), 4);
        Assert.Equal(Height, layout.Y(layout.YMin), 4);
        Assert.Equal(-10 / layout.UnitHeight, layout.MovedValue(100, 110), 4);
    }

    [Fact]
    public void PanAndZoom_MoveTheValuesUnderThePointer()
    {
        var layout = new CurveboardLayout { Width = 800, Height = 600, YMin = -1, YMax = 1 };
        // 600 DIPs over 2 units: a 30-DIP drag down moves the range up by 0.1.
        var (min, max) = layout.Panned(100, 130, layout.YMin, layout.YMax);
        Assert.Equal(-0.9, min, 6);
        Assert.Equal(1.1, max, 6);
        // Zooming keeps the value under the pointer where it is and shrinks the span.
        double before = layout.ValueAt(150);
        layout.ZoomAt(150, 1);
        Assert.Equal(before, layout.ValueAt(150), 6);
        Assert.Equal(0.85 * 2, layout.YMax - layout.YMin, 6);
    }

    [SceneTheory, SceneSamples]
    public void TheFramesLineUpWithTheTimeline(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FirstAnimation(model);
        if (found.Character == null) return;
        var (_, animation, _) = found;
        var board = new CurveboardLayout { Width = Width, Height = Height, Animation = animation };
        var timeline = new TimelineLayout { Width = Width, Animation = animation };

        Assert.Equal(timeline.Range, board.Range);
        Assert.Equal(timeline.FrameWidth, board.FrameWidth, 6);
        for (int frame = board.Range.Start; frame <= board.Range.End; frame++)
            Assert.Equal(timeline.X(frame), board.X(frame), 6);
    }

    [SceneTheory, SceneSamples]
    public void Keys_AreHitWhereTheyAreDrawn(string path)
    {
        var layout = Board(path);
        if (layout == null) return;
        var points = layout.KeyPoints();
        Assert.NotEmpty(points);
        int checkedPoints = 0;
        foreach (var point in points)
        {
            var hit = layout.KeyAt(point.Point);
            Assert.NotNull(hit);
            // A key drawn over another wins the tie, so compare the drawn position.
            Assert.Equal(point.Point, hit!.Value.Point);
            if (++checkedPoints >= 40) break;
        }
        // Below the lowest sampled value, padded under it, there is nothing to hit.
        var empty = new Vector2((float)(CurveboardLayout.Gutter + 40), (float)Height - 1);
        Assert.Null(layout.KeyAt(empty));
    }

    [SceneTheory, SceneSamples]
    public void A_PressSelectsTheKeyUnderIt_ShiftAdds_CtrlToggles(string path)
    {
        var layout = Board(path);
        if (layout == null) return;
        var points = layout.KeyPoints();
        var first = points[0];
        var other = points.FirstOrDefault(p => p.Key != first.Key || !ReferenceEquals(p.Track, first.Track));
        if (other.Track == null || Vector2.Distance(other.Point, first.Point) <= CurveboardLayout.HitRadius * 2)
            return;

        var press = layout.Press(first.Point, false, false);
        Assert.Equal(CurveboardGesture.Press, press.Gesture);
        Assert.True(press.SelectionTouched);
        Assert.True(layout.IsSelected(first));
        Assert.Equal(1, layout.Selection.Count);

        // A plain press elsewhere replaces the selection.
        layout.Press(other.Point, false, false);
        Assert.False(layout.IsSelected(first));
        Assert.True(layout.IsSelected(other));

        // Shift adds.
        layout.Press(first.Point, true, false);
        Assert.True(layout.IsSelected(first));
        Assert.Equal(2, layout.Selection.Count);

        // Ctrl toggles the pressed key off and leaves nothing to drag.
        var toggle = layout.Press(first.Point, false, true);
        Assert.Equal(CurveboardGesture.None, toggle.Gesture);
        Assert.False(layout.IsSelected(first));
        Assert.True(layout.IsSelected(other));
    }

    [SceneTheory, SceneSamples]
    public void Handles_BelongToSelectedKeys_AndStayOnTheirSide(string path)
    {
        var layout = Board(path);
        if (layout == null) return;
        Assert.Empty(layout.Handles());

        // The first key whose track has a segment draws handles once it is selected.
        CurveboardLayout.CurveboardHandle? picked = null;
        foreach (var point in layout.KeyPoints())
        {
            layout.ClearSelection();
            layout.Press(point.Point, false, false);
            foreach (var handle in layout.Handles())
            {
                var press = layout.Press(handle.End, false, false);
                if (press.Gesture == CurveboardGesture.HandleDrag && layout.PressedHandle is { } hit &&
                    ReferenceEquals(hit.Track, handle.Track) && hit.Key == handle.Key &&
                    hit.Channel == handle.Channel && hit.Side == handle.Side)
                {
                    picked = handle;
                    break;
                }
            }
            if (picked != null) break;
        }
        if (picked is not { } target) return;

        var handles = layout.Handles();
        // The handles drawn belong to the selected key, and the pressed side is selected.
        Assert.All(handles, h => Assert.True(ReferenceEquals(h.Track, target.Track) && h.Key == target.Key));
        Assert.True(layout.IsSelected(target));
        Assert.All(handles, h => Assert.InRange(Vector2.Distance(h.KeyPoint, h.End), 1e-3f, (float)CurveboardLayout.HandleLength + 1e-3f));

        // Dragging the out handle right and up gives a positive slope; the in handle mirrors it.
        var outward = new Vector2(target.KeyPoint.X + (float)(CurveboardLayout.HandleLength * 0.5), target.KeyPoint.Y - 20);
        Assert.True(layout.Slope(target with { Side = TangentSide.Out }, outward) > 0);
        var backward = new Vector2(target.KeyPoint.X - (float)(CurveboardLayout.HandleLength * 0.5), target.KeyPoint.Y - 20);
        Assert.True(layout.Slope(target with { Side = TangentSide.In }, backward) < 0);
        // A handle dragged past its key stays on its own side instead of flipping over.
        float across = layout.Slope(target with { Side = TangentSide.In }, outward);
        Assert.True(across < -10, $"the in handle kept its own side (slope {across})");
    }

    [SceneTheory, SceneSamples]
    public void BoxSelection_TakesTheKeysItEncloses(string path)
    {
        var layout = Board(path);
        if (layout == null) return;
        var points = layout.KeyPoints();
        var target = points[0];
        var a = target.Point - new Vector2((float)CurveboardLayout.KeyRadius, (float)CurveboardLayout.KeyRadius);
        var b = target.Point + new Vector2((float)CurveboardLayout.KeyRadius, (float)CurveboardLayout.KeyRadius);

        Assert.True(layout.BoxSelect(a, b, false) >= 1);
        Assert.True(layout.IsSelected(target));
        Assert.True(layout.Selection.Count > 0);

        // A box between the padded bottom edge and the last value encloses nothing.
        Assert.Equal(0, layout.BoxSelect(new Vector2((float)CurveboardLayout.Gutter + 1, (float)Height - 2),
            new Vector2((float)CurveboardLayout.Gutter + 2, (float)Height - 1), false));
        Assert.Empty(layout.Selection);

        Assert.True(CurveboardLayout.IsClick(new Vector2(10, 10), new Vector2(12, 12)));
        Assert.False(CurveboardLayout.IsClick(new Vector2(10, 10), new Vector2((float)(10 + CurveboardLayout.DragSlop + 1), 10)));
    }

    [SceneTheory, SceneSamples]
    public void Fit_ShowsEverySampledValue(string path)
    {
        var layout = Board(path);
        if (layout == null) return;
        layout.Fit();
        Assert.True(layout.YMin < layout.YMax);
        foreach (var curve in layout.CurvesOnScreen)
            foreach (float value in curve.Values)
                if (float.IsFinite(value))
                    Assert.InRange(value, layout.YMin, layout.YMax);
    }

    /// <summary>A board showing the first animation's first tracks, or null when the scene has none.</summary>
    private static CurveboardLayout? Board(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FirstAnimation(model);
        if (found.Character == null) return null;
        var (character, animation, stack) = found;
        var layout = new CurveboardLayout { Width = Width, Height = Height, Animation = animation, Evaluator = new PoseEvaluator(character) };
        var tracks = CurveboardLayout.TracksOf(stack, stack.Tracks.Where(t => t.Keys.Count > 1)).Take(3).ToArray();
        if (tracks.Length == 0) return null;
        layout.Show(stack, tracks);
        layout.Fit();
        return layout.CurvesOnScreen.Count == 0 ? null : layout;
    }

    private static (Character Character, Animation Animation, AnimStack Stack) FirstAnimation(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                foreach (var stack in animation.Stacks)
                    if (stack.Keys.Count > 1 && stack.Tracks.Any(t => t.Keys.Count > 1))
                        return (character, animation, stack);
        return (null!, null!, null!);
    }
}
