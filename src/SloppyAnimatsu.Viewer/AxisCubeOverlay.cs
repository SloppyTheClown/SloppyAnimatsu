using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The axis cube in the outer corner of every orbit view,
/// drawn by Avalonia over the GL viewport. It follows each view's orbit camera (it redraws after
/// every GL frame) and only takes the pointer over its own shapes, so a press anywhere else
/// reaches the viewport. Clicking a handle turns that view's camera to the axis; clicking a face
/// turns it there too and swaps between perspective and flat; dragging on the cube orbits.
/// </summary>
public sealed class AxisCubeOverlay : Control, ICustomHitTest
{
    /// <summary>Pixels (DIPs) per cube unit: the cube is about 90 DIPs across with its handles.</summary>
    public const float Scale = 28.5f;

    /// <summary>The cube centre's distance from the view's edges in its corner (DIPs).</summary>
    public const float Inset = 54f;

    /// <summary>The cube's footprint for placement: its farthest part plus a small margin (DIPs).</summary>
    public const float Footprint = AxisCube.Extent * Scale + 3f;

    /// <summary>A handle pointing this squarely at the viewer covers its face, so it toggles flat like the face.</summary>
    public const float FacingToggle = 0.98f;

    /// <summary>The rectangles (in this control's coordinates) of whatever floats over the views: panels, the curveboard.</summary>
    public Func<IEnumerable<Rect>>? Obstacles { get; set; }

    private Vector2?[] _centers = [];
    private int _centersKey;

    /// <summary>A press that moves further than this (DIPs) is a drag that orbits, not a click.</summary>
    private const double DragThreshold = 3;

    private readonly Viewport _viewport;
    private (int View, int Axis, AxisCube.PartKind Kind)? _hover;
    private (int View, AxisCube.Part Part, Point Start, Point Last, bool Dragging)? _press;
    private bool _invalidatePosted;

    public AxisCubeOverlay(Viewport viewport)
    {
        _viewport = viewport;
        ClipToBounds = true;
        _viewport.Rendered += () =>
        {
            if (_invalidatePosted) return;
            _invalidatePosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _invalidatePosted = false;
                InvalidateVisual();
            }, DispatcherPriority.Render);
        };
        // A panel moved or resized over the views: the cube may have to move out of its way.
        LayoutUpdated += (_, _) =>
        {
            if (PlacementKey() != _centersKey) InvalidateVisual();
        };
    }

    /// <summary>Raised on the UI thread after a click turned a view (the view index): the window refreshes its menu and status.</summary>
    public event Action<int, int, bool>? Aligned;

    /// <summary>The cube centre of view <paramref name="index"/> (DIPs), or null when it is no orbit view or too small.</summary>
    private Vector2? CenterOf(int index, out OrbitCamera? orbit)
    {
        orbit = _viewport.Renderer.OrbitOf(index);
        if (orbit == null) return null;
        var centers = Centers();
        return index < centers.Length ? centers[index] : null;
    }

    private List<(Vector2 Min, Vector2 Max)> ObstacleBoxes()
    {
        var boxes = new List<(Vector2, Vector2)>();
        if (Obstacles == null) return boxes;
        foreach (var r in Obstacles())
            if (r.Width > 0 && r.Height > 0)
                boxes.Add((new Vector2((float)r.X, (float)r.Y), new Vector2((float)r.Right, (float)r.Bottom)));
        return boxes;
    }

    /// <summary>A hash of everything the placement depends on: the views' rectangles, the area and the obstacles.</summary>
    private int PlacementKey() => PlacementKey(ObstacleBoxes());

    private int PlacementKey(List<(Vector2 Min, Vector2 Max)> boxes)
    {
        var hash = new HashCode();
        hash.Add(Bounds.Size);
        for (int i = 0; i < ViewCount; i++)
        {
            var (origin, size) = _viewport.ViewArea(i);
            hash.Add(origin);
            hash.Add(size);
        }
        foreach (var box in boxes) hash.Add(box);
        return hash.ToHashCode();
    }

    /// <summary>Every view's cube centre, placed in its outer corner clear of the panels (AxisCube.Place); cached until something moves.</summary>
    private Vector2?[] Centers()
    {
        var boxes = ObstacleBoxes();
        int key = PlacementKey(boxes);
        if (key == _centersKey && _centers.Length == ViewCount) return _centers;
        var area = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        var centers = new Vector2?[ViewCount];
        for (int i = 0; i < centers.Length; i++)
        {
            var (origin, size) = _viewport.ViewArea(i);
            centers[i] = AxisCube.Place(origin, size, area, boxes, Footprint, Inset);
        }
        _centers = centers;
        _centersKey = key;
        return centers;
    }

    private int ViewCount => Math.Max(_viewport.Renderer.Views.Count, 1);

    /// <summary>The front-most cube part under a point, with its view.</summary>
    private (int View, AxisCube.Part Part)? PartAt(Point point)
    {
        var p = new Vector2((float)point.X, (float)point.Y);
        for (int i = 0; i < ViewCount; i++)
        {
            if (CenterOf(i, out var orbit) is not { } center || orbit == null) continue;
            if (Vector2.Distance(p, center) > AxisCube.Extent * Scale + 2) continue;
            if (AxisCube.HitTest(AxisCube.Layout(orbit.View, center, Scale), p) is { } part) return (i, part);
        }
        return null;
    }

    public bool HitTest(Point point) => _press != null || PartAt(point) != null;

    public override void Render(DrawingContext context)
    {
        if (_viewport.Renderer.Pose == null) return;
        for (int i = 0; i < ViewCount; i++)
        {
            if (CenterOf(i, out var orbit) is not { } center || orbit == null) continue;
            foreach (var part in AxisCube.Layout(orbit.View, center, Scale))
                DrawPart(context, i, part);
        }
    }

    private void DrawPart(DrawingContext context, int view, AxisCube.Part part)
    {
        bool hovered = _hover is { } h && h.View == view && h.Axis == part.Axis && h.Kind == part.Kind;
        var axisColor = AxisCube.Color(part.Axis);
        bool positive = AxisCube.IsPositive(part.Axis);
        // Parts on the far side are dimmed so the near side reads first.
        float depthShade = 0.75f + 0.25f * Math.Clamp(part.Facing, -1f, 1f);
        switch (part.Kind)
        {
            case AxisCube.PartKind.Face:
            {
                var grey = new Vector3(0.42f);
                var fill = Vector3.Lerp(grey, axisColor, 0.22f) * (0.6f + 0.4f * part.Facing);
                if (hovered) fill = Vector3.Lerp(fill, Vector3.One, 0.3f);
                context.DrawGeometry(Brush(fill, 235), new Pen(Brush(new Vector3(0.12f), 255), 1), Polygon(part.Outline));
                break;
            }
            case AxisCube.PartKind.Cone:
            {
                var fill = axisColor * (positive ? 1f : 0.6f) * depthShade;
                if (hovered) fill = Vector3.Lerp(fill, Vector3.One, 0.35f);
                context.DrawGeometry(Brush(fill, 255), new Pen(Brush(fill * 0.55f, 255), 0.8), Polygon(part.Outline));
                break;
            }
            case AxisCube.PartKind.Ball:
            {
                var fill = (positive ? axisColor : axisColor * 0.45f) * depthShade;
                if (hovered) fill = Vector3.Lerp(fill, Vector3.One, 0.35f);
                var rim = positive ? fill * 0.55f : axisColor * depthShade;
                var centre = new Point(part.Center.X, part.Center.Y);
                context.DrawEllipse(Brush(fill, 255), new Pen(Brush(rim, 255), positive ? 1 : 1.4), centre, part.Radius, part.Radius);
                var text = new FormattedText(AxisCube.Label(part.Axis), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                    positive ? part.Radius * 1.25 : part.Radius * 0.95, Brushes.White);
                context.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
                break;
            }
        }
    }

    private static IBrush Brush(Vector3 rgb, byte alpha) => new SolidColorBrush(Color.FromArgb(alpha,
        (byte)Math.Clamp(rgb.X * 255, 0, 255), (byte)Math.Clamp(rgb.Y * 255, 0, 255), (byte)Math.Clamp(rgb.Z * 255, 0, 255)));

    private static StreamGeometry Polygon(Vector2[] points)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(points[0].X, points[0].Y), true);
        for (int i = 1; i < points.Length; i++) g.LineTo(new Point(points[i].X, points[i].Y));
        g.EndFigure(true);
        return geometry;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_press is { } press)
        {
            bool dragging = press.Dragging || Distance(point, press.Start) > DragThreshold;
            if (dragging && _viewport.Renderer.OrbitOf(press.View) is { } orbit)
            {
                var delta = point - press.Last;
                orbit.Orbit(-(float)delta.X * 0.01f, (float)delta.Y * 0.01f);
                _viewport.RequestNextFrameRendering();
            }
            _press = press with { Last = point, Dragging = dragging };
            e.Handled = true;
            return;
        }
        var hit = PartAt(point);
        var hover = hit is { } h ? (h.View, h.Part.Axis, h.Part.Kind) : ((int, int, AxisCube.PartKind)?)null;
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
        Cursor = hover != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || PartAt(point) is not { } hit) return;
        _viewport.FocusView(point);
        _viewport.Focus();
        _press = (hit.View, hit.Part, point, point, false);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_press is not { } press) return;
        _press = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (press.Dragging || _viewport.Renderer.OrbitOf(press.View) is not { } orbit) return;
        // A face - or the handle pointing straight at the viewer, which covers its face - also
        // swaps between the perspective and the flat view; AlignTo keeps the projection.
        bool toggles = press.Part.Kind == AxisCube.PartKind.Face || press.Part.Facing > FacingToggle;
        orbit.AlignTo(press.Part.Axis);
        if (toggles) orbit.Orthographic = !orbit.Orthographic;
        _viewport.RequestNextFrameRendering();
        Aligned?.Invoke(press.View, press.Part.Axis, orbit.Orthographic);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => _press = null;

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
