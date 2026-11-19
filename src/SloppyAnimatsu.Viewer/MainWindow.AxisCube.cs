// The axis cube of the orbit views: the overlay control
// (AxisCubeOverlay) sits over the GL viewport; this part creates it, hands it the floating
// windows to keep clear of, and reports its turns.
// It replaced the bottom-left camera button and its fixed Front/Left/Top... views.
// Part of the partial MainWindow type.
using Avalonia;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private AxisCubeOverlay? _axisCubeOverlay;

    private AxisCubeOverlay _axisCubes => _axisCubeOverlay ??= new AxisCubeOverlay(_viewport);

    private void HookAxisCubes()
    {
        // The panels, the curveboard and the dial float over the views; a cube moves inward out of their way.
        _axisCubes.Obstacles = AxisCubeObstacles;
        _axisCubes.Aligned += (view, axis, flat) =>
        {
            if (view >= 0 && view < _viewStates.Count) _focusedView = view;
            RefreshMenu();
            string prefix = _viewStates.Count > 1 ? $"View {view + 1}: " : "";
            _status.Text = $"{prefix}{AxisCube.Label(axis)} side, {(flat ? "flat" : "perspective")}.";
        };
    }

    /// <summary>The visible windows floating over the view area, in the cube overlay's coordinates.</summary>
    private IEnumerable<Rect> AxisCubeObstacles()
    {
        if (_floatHost == null) yield break;
        foreach (var child in _floatHost.Children)
        {
            if (!child.IsVisible || child.Opacity <= 0 || child.Bounds.Width <= 0 || child.Bounds.Height <= 0) continue;
            if (child.TranslatePoint(default, _axisCubes) is not { } topLeft) continue;
            yield return new Rect(topLeft, child.Bounds.Size);
        }
    }

    /// <summary>Redraws the axis cubes after the views or the layout changed.</summary>
    private void UpdateAxisCubes() => _axisCubes.InvalidateVisual();
}
