// The Spinner window over the viewport: a round floating window, dragged by its rim like the other
// floating windows by their header, holding a disc of three coloured sectors that drag the selected
// controller along the matching gizmo axis, mode buttons leaning round the ring above the disc, and
// a small space button in the ring on the right. The disc's centre circle is uniform scale in scale
// mode and, for an IK selection, the chain's swivel in rotate mode. The space button shows the
// transform space (Local, World or Screen) and cycles it on a click. Everything stays inside the
// round rim. Sector drags count only the pointer's vertical movement. With a reverse-foot controller
// selected, rotate mode's sectors also carry roll/twist/bank icons and take a right-button drag.
// Layout and drag amounts are SloppyAnimatsu.Rendering.SpinnerDial's; this control only draws them and
// forwards the pointer.
using System.Globalization;
using SloppyAnimatsu.Core.Model;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed class SpinnerDialView : Control, ICustomHitTest
{
    private static readonly Color[] AxisColors = [Color.FromRgb(242, 64, 64), Color.FromRgb(77, 230, 77), Color.FromRgb(77, 128, 255)];
    private static readonly Color ActiveColor = Color.FromRgb(255, 230, 51);
    // The frame takes the floating windows' colours: the rim is their header, the disc their body.
    private static readonly IBrush FrameBrush = new SolidColorBrush(Color.Parse("#2C2C31"));
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromArgb(175, 190, 190, 198)), 1);
    private static readonly IBrush DiscBrush = new SolidColorBrush(Color.Parse("#141416"));
    private static readonly IPen DiscPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 120, 120, 120)), 1);
    private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.FromArgb(235, 40, 40, 44));
    private static readonly IBrush ButtonOnBrush = new SolidColorBrush(Color.FromRgb(70, 110, 170));
    private static readonly IPen IconPen = new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 1.6, lineCap: PenLineCap.Round);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225));
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private GizmoMode _mode = GizmoMode.Rotate;
    private bool _available;
    private bool _swivel;
    private TransformSpace _space = TransformSpace.Local;
    private int _hover = -1;
    private bool _dragging;
    private int _handle = -1;
    private Vector2 _press;
    private bool _moving;
    private bool _foot;
    private readonly bool[] _footEditable = new bool[3];
    private int _footAxis = -1;
    private Point _moveLast;

    public SpinnerDialView()
    {
        Width = SpinnerDial.Width;
        Height = SpinnerDial.Height;
        ToolTip.SetTip(this, TipFor(new SpinnerHit(SpinnerPart.Frame)));
    }

    /// <summary>The gizmo mode the dial shows; its button is lit and scale adds the centre circle.</summary>
    public GizmoMode Mode
    {
        get => _mode;
        set { if (_mode != value) { _mode = value; InvalidateVisual(); } }
    }

    /// <summary>Whether the selection can be edited in the current mode (the sectors are dimmed otherwise).</summary>
    public bool Available
    {
        get => _available;
        set { if (_available != value) { _available = value; InvalidateVisual(); } }
    }

    /// <summary>Whether the selection holds an IK chain, so that rotate mode's centre circle swivels it.</summary>
    public bool Swivel
    {
        get => _swivel;
        set { if (_swivel != value) { _swivel = value; InvalidateVisual(); } }
    }

    private bool _snap;

    /// <summary>Whether the snap button is shown: an IK or reverse-foot handle is selected in an animation.</summary>
    public bool Snap
    {
        get => _snap;
        set { if (_snap != value) { _snap = value; InvalidateVisual(); } }
    }

    /// <summary>The snap button was clicked: put the selected handles on the end of their chains.</summary>
    public Action? SnapPicked { get; set; }

    /// <summary>The transform space the space button shows.</summary>
    public TransformSpace Space
    {
        get => _space;
        set { if (_space != value) { _space = value; InvalidateVisual(); } }
    }

    /// <summary>
    /// Shows the reverse-foot sectors: with a reverse-foot controller selected, rotate mode's X, Y
    /// and Z sectors carry a foot and a direction icon for roll, twist and bank, and a right-button
    /// drag on one changes that angle (<paramref name="editable"/>: per axis, dimmed and inert when false).
    /// </summary>
    public void SetFoot(bool foot, bool rollEditable, bool twistEditable, bool bankEditable)
    {
        if (_foot == foot && _footEditable[0] == rollEditable && _footEditable[1] == twistEditable && _footEditable[2] == bankEditable) return;
        _foot = foot;
        _footEditable[0] = rollEditable;
        _footEditable[1] = twistEditable;
        _footEditable[2] = bankEditable;
        InvalidateVisual();
    }

    private bool FootActive => _foot && _mode == GizmoMode.Rotate;

    /// <summary>A right-button drag starts on sector 0-2 (roll, twist, bank); false refuses it.</summary>
    public Func<int, bool>? FootStarted { get; set; }

    /// <summary>The right-button drag so far: the axis and the change in degrees (vertical only, up positive, Ctrl a tenth).</summary>
    public Action<int, float>? FootDragged { get; set; }

    /// <summary>The right-button drag ended; true commits it, false cancels.</summary>
    public Action<bool>? FootEnded { get; set; }

    /// <summary>The space button was clicked.</summary>
    public Action? SpacePicked { get; set; }

    /// <summary>A mode button was clicked.</summary>
    public Action<GizmoMode>? ModePicked { get; set; }

    /// <summary>A drag starts on a handle (0-2, or <see cref="TransformGizmo.Uniform"/>); false refuses it.</summary>
    public Func<int, bool>? DragStarted { get; set; }

    /// <summary>The drag so far: the angle swept around the dial's centre and the pointer's offset from the press.</summary>
    public Action<float, Vector2>? Dragged { get; set; }

    /// <summary>The drag ended; true commits it, false cancels.</summary>
    public Action<bool>? DragEnded { get; set; }

    /// <summary>The dial was dragged by its dark parts; the step it moved, in the parent's units.</summary>
    public Action<Vector2>? Moved { get; set; }

    /// <summary>The handle under the pointer changed (-1 for none), so the gizmo can light the same one.</summary>
    public Action<int>? Highlighted { get; set; }

    private Vector2 Size2 => new((float)Bounds.Width, (float)Bounds.Height);


    private static readonly string[] AxisNames = ["X (red)", "Y (green)", "Z (blue)"];

    /// <summary>The hover text of one part of the dial: each button and sector says what it does.</summary>
    private string? TipFor(SpinnerHit hit) => hit.Part switch
    {
        SpinnerPart.Mode => hit.Mode switch
        {
            GizmoMode.Translate => "Move mode: the sectors drag the selection along an axis.",
            GizmoMode.Scale => "Scale mode: the sectors scale along an axis, the centre circle scales uniformly.",
            _ => "Rotate mode: the sectors turn the selection around an axis.",
        },
        SpinnerPart.Space => "Transform space: Loc / Scr / Wld - click to cycle Local, Screen and World (keys: 1 Local, 2 Screen, 3 World).",
        SpinnerPart.Snap => "Snap: put the selected IK / reverse-foot handle back on the end of its chain at this key.",
        SpinnerPart.Uniform => _mode == GizmoMode.Scale
            ? "Uniform scale: drag up or down to scale on all three axes."
            : "Swivel: drag up or down to turn the IK chain around its axis.",
        SpinnerPart.Axis when hit.Axis is >= 0 and < 3 => _mode switch
        {
            GizmoMode.Translate => $"Move along {AxisNames[hit.Axis]}: drag up or down.",
            GizmoMode.Scale => $"Scale along {AxisNames[hit.Axis]}: drag up or down.",
            _ => $"Rotate around {AxisNames[hit.Axis]}: drag up or down."
                 + (FootActive ? " Right-drag: the reverse foot's roll, twist or bank shown by the icon." : ""),
        },
        SpinnerPart.Frame => "Spinner: drag the rim to move it.",
        _ => null,
    };
    public bool HitTest(Point point) => SpinnerDial.Contains(Size2, new Vector2((float)point.X, (float)point.Y));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var p = new Vector2((float)point.Position.X, (float)point.Position.Y);
        e.Handled = true;
        if (_dragging)
        {
            // Any other button during a drag cancels it.
            Finish(commit: false, e.Pointer);
            return;
        }
        if (_footAxis >= 0)
        {
            FinishFoot(commit: false, e.Pointer);
            return;
        }
        var hit = SpinnerDial.Hit(Size2, p, _mode, _swivel, _snap);
        if (point.Properties.IsRightButtonPressed)
        {
            // A right-button drag on a rotation sector of a reverse-foot controller: roll, twist or bank.
            if (FootActive && hit.Part == SpinnerPart.Axis && _footEditable[hit.Axis]
                && FootStarted?.Invoke(hit.Axis) == true)
            {
                _footAxis = hit.Axis;
                _press = p;
                e.Pointer.Capture(this);
                SetHover(_footAxis);
            }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (hit.Part == SpinnerPart.Mode)
        {
            ModePicked?.Invoke(hit.Mode);
            return;
        }
        if (hit.Part == SpinnerPart.Space)
        {
            SpacePicked?.Invoke();
            return;
        }
        if (hit.Part == SpinnerPart.Snap)
        {
            SnapPicked?.Invoke();
            return;
        }
        if (hit.Part == SpinnerPart.None) return;
        if (hit.Part == SpinnerPart.Frame)
        {
            // The frame (the rim, the hub, the gaps between sectors) moves the window itself.
            _moving = true;
            _moveLast = e.GetPosition(this.GetVisualParent());
            e.Pointer.Capture(this);
            return;
        }
        if (!_available || DragStarted?.Invoke(hit.Axis) != true) return;
        _dragging = true;
        _handle = hit.Axis;
        _press = p;
        e.Pointer.Capture(this);
        SetHover(_handle);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var p = new Vector2((float)position.X, (float)position.Y);
        if (_moving)
        {
            Point now = e.GetPosition(this.GetVisualParent());
            Moved?.Invoke(new Vector2((float)(now.X - _moveLast.X), (float)(now.Y - _moveLast.Y)));
            _moveLast = now;
            e.Handled = true;
            return;
        }
        if (_dragging)
        {
            // Only the vertical movement drives the drag (up positive); horizontal is ignored.
            Dragged?.Invoke(SpinnerDial.VerticalTurn(p - _press), p - _press);
            e.Handled = true;
            return;
        }
        if (_footAxis >= 0)
        {
            FootDragged?.Invoke(_footAxis, SpinnerDial.FootDegrees(p - _press) * (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 0.1f : 1f));
            e.Handled = true;
            return;
        }
        var hit = SpinnerDial.Hit(Size2, p, _mode, _swivel, _snap);
        Cursor = hit.Part switch
        {
            SpinnerPart.Frame => MoveCursor,
            SpinnerPart.Mode or SpinnerPart.Space or SpinnerPart.Snap => HandCursor,
            _ => Cursor.Default,
        };
        SetHover(_available && hit.Part is SpinnerPart.Axis or SpinnerPart.Uniform ? hit.Axis : -1);
        // Each part of the dial has its own hover text.
        string? tip = TipFor(hit);
        if (tip != null && !Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_moving)
        {
            _moving = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_dragging && e.InitialPressMouseButton == MouseButton.Left)
        {
            Finish(commit: true, e.Pointer);
            e.Handled = true;
        }
        else if (_footAxis >= 0 && e.InitialPressMouseButton == MouseButton.Right)
        {
            FinishFoot(commit: true, e.Pointer);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _moving = false;
        if (_dragging) Finish(commit: true, null);
        if (_footAxis >= 0) FinishFoot(commit: true, null);
    }

    private void FinishFoot(bool commit, IPointer? pointer)
    {
        _footAxis = -1;
        pointer?.Capture(null);
        FootEnded?.Invoke(commit);
        SetHover(-1);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging && _footAxis < 0) SetHover(-1);
    }

    private void Finish(bool commit, IPointer? pointer)
    {
        _dragging = false;
        _handle = -1;
        pointer?.Capture(null);
        DragEnded?.Invoke(commit);
        SetHover(-1);
    }

    private void SetHover(int handle)
    {
        if (_hover == handle) return;
        _hover = handle;
        Highlighted?.Invoke(handle);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Size2;
        // The round window: a soft shadow, the rim in the header colour, then the disc.
        var f = SpinnerDial.FrameCenter(size);
        var frameCenter = new Point(f.X, f.Y);
        for (int i = 3; i >= 1; i--)
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, frameCenter + new Point(0, 2), SpinnerDial.FrameRadius + i, SpinnerDial.FrameRadius + i);
        context.DrawEllipse(FrameBrush, FramePen, frameCenter, SpinnerDial.FrameRadius, SpinnerDial.FrameRadius);

        var c = SpinnerDial.Center(size);
        float radius = SpinnerDial.Radius(size);
        var center = new Point(c.X, c.Y);
        context.DrawEllipse(DiscBrush, DiscPen, center, radius, radius);

        double opacity = _available ? 1.0 : 0.3;
        for (int axis = 0; axis < 3; axis++)
        {
            var color = _hover == axis ? ActiveColor : AxisColors[axis];
            var brush = new SolidColorBrush(color, opacity);
            float mid = SpinnerDial.SectorAngle(axis);
            context.DrawGeometry(brush, null, Sector(c, radius * SpinnerDial.InnerRadius, radius * SpinnerDial.OuterRadius,
                mid - SpinnerDial.SectorHalfAngle, mid + SpinnerDial.SectorHalfAngle));
            if (FootActive)
            {
                // Roll, twist, bank: a foot and the direction it turns, in the middle of the sector.
                float ring = radius * (SpinnerDial.InnerRadius + SpinnerDial.OuterRadius) / 2;
                var at = new Point(c.X + ring * MathF.Cos(mid), c.Y + ring * MathF.Sin(mid));
                using (context.PushOpacity(opacity * (_footEditable[axis] ? 1 : 0.35)))
                    DrawFoot(context, axis, at);
            }
        }
        if (SpinnerDial.HasCentre(_mode, _swivel))
        {
            var color = _hover == TransformGizmo.Uniform ? ActiveColor : Color.FromRgb(215, 215, 215);
            float r = radius * SpinnerDial.UniformRadius;
            context.DrawEllipse(new SolidColorBrush(color, opacity), null, center, r, r);
            if (_mode == GizmoMode.Rotate)
            {
                // The swivel: a turning arrow on the centre circle.
                using (context.PushOpacity(opacity))
                    DrawIcon(context, GizmoMode.Rotate, center, DarkIconPen);
            }
        }

        // The transform-space button: a small disc in the button ring on the right, inside the rim.
        var s = SpinnerDial.SpaceCenter(size);
        var spaceCenter = new Point(s.X, s.Y);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, spaceCenter + new Point(0, 2), SpinnerDial.SpaceRadius + 2, SpinnerDial.SpaceRadius + 2);
        context.DrawEllipse(ButtonBrush, FramePen, spaceCenter, SpinnerDial.SpaceRadius, SpinnerDial.SpaceRadius);
        var text = new FormattedText(SpaceLabel(_space), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 8.5, TextBrush);
        context.DrawText(text, new Point(s.X - text.Width / 2, s.Y - text.Height / 2));

        // The snap button: the same small disc on the left, only while a handle is selected.
        if (_snap)
        {
            var n = SpinnerDial.SnapCenter(size);
            var snapCenter = new Point(n.X, n.Y);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, snapCenter + new Point(0, 2), SpinnerDial.SpaceRadius + 2, SpinnerDial.SpaceRadius + 2);
            context.DrawEllipse(ButtonBrush, FramePen, snapCenter, SpinnerDial.SpaceRadius, SpinnerDial.SpaceRadius);
            var snapText = new FormattedText("Snap", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 8, TextBrush);
            context.DrawText(snapText, new Point(n.X - snapText.Width / 2, n.Y - snapText.Height / 2));
        }

        // The mode buttons lean round the top of the ring, their long sides following its curvature.
        for (int i = 0; i < SpinnerDial.Modes.Length; i++)
        {
            var at = SpinnerDial.ModeButtonCenter(size, i);
            bool on = SpinnerDial.Modes[i] == _mode;
            var rect = new Rect(-SpinnerDial.ButtonWidth / 2, -SpinnerDial.ButtonHeight / 2, SpinnerDial.ButtonWidth, SpinnerDial.ButtonHeight);
            Matrix turn = Matrix.CreateRotation(SpinnerDial.ModeButtonRotation(i)) * Matrix.CreateTranslation(at.X, at.Y);
            using (context.PushTransform(turn))
            {
                context.DrawRectangle(on ? ButtonOnBrush : ButtonBrush, DiscPen, rect, 5, 5);
                DrawIcon(context, SpinnerDial.Modes[i], new Point(0, 0), IconPen);
            }
        }
    }

    /// <summary>The space button's short label: a word does not fit in the ring's width.</summary>
    private static string SpaceLabel(TransformSpace space) => space switch
    {
        TransformSpace.Screen => "Scr",
        TransformSpace.World => "Wld",
        _ => "Loc",
    };

    private static readonly IPen DarkIconPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 30, 34)), 1.8, lineCap: PenLineCap.Round);

    private static readonly Geometry FootGlyph = StreamGeometry.Parse(OutlinerStyle.GlyphOf(OutlinerKind.ReverseFootController));
    private static readonly IBrush DarkBrush = new SolidColorBrush(Color.FromRgb(30, 30, 34));

    /// <summary>
    /// The reverse-foot icon of sector <paramref name="axis"/> centred on <paramref name="c"/>: the
    /// foot glyph and, right of it, the direction - roll an up/down arrow (heel to toe), twist a
    /// left/right arrow (about the leg), bank a turning arrow (side to side).
    /// </summary>
    private static void DrawFoot(DrawingContext context, int axis, Point c)
    {
        using (context.PushTransform(Matrix.CreateScale(1.15, 1.15) * Matrix.CreateTranslation(c.X - 15, c.Y - 7)))
            context.DrawGeometry(DarkBrush, null, FootGlyph);
        var d = new Point(c.X + 8, c.Y);
        void Line(double x0, double y0, double x1, double y1) =>
            context.DrawLine(DarkIconPen, new Point(d.X + x0, d.Y + y0), new Point(d.X + x1, d.Y + y1));
        if (axis == 0)
        {
            Line(0, -7, 0, 7);
            Line(0, -7, -3, -4); Line(0, -7, 3, -4);
            Line(0, 7, -3, 4); Line(0, 7, 3, 4);
        }
        else if (axis == 1)
        {
            Line(-6, 0, 6, 0);
            Line(-6, 0, -3, -3); Line(-6, 0, -3, 3);
            Line(6, 0, 3, -3); Line(6, 0, 3, 3);
        }
        else
        {
            DrawIcon(context, GizmoMode.Rotate, new Point(d.X + 1, d.Y + 1), DarkIconPen);
        }
    }

    private static StreamGeometry Sector(Vector2 c, float inner, float outer, float from, float to)
    {
        Point At(float radius, float angle) => new(c.X + radius * MathF.Cos(angle), c.Y + radius * MathF.Sin(angle));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(outer, from), isFilled: true);
            g.ArcTo(At(outer, to), new Size(outer, outer), 0, false, SweepDirection.Clockwise);
            g.LineTo(At(inner, to));
            g.ArcTo(At(inner, from), new Size(inner, inner), 0, false, SweepDirection.CounterClockwise);
            g.EndFigure(true);
        }
        return geometry;
    }

    private static void DrawIcon(DrawingContext context, GizmoMode mode, Point c, IPen IconPen)
    {
        void Line(double x0, double y0, double x1, double y1) =>
            context.DrawLine(IconPen, new Point(c.X + x0, c.Y + y0), new Point(c.X + x1, c.Y + y1));

        if (mode == GizmoMode.Translate)
        {
            // A four-way arrow.
            Line(-7, 0, 7, 0);
            Line(0, -7, 0, 7);
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                double tx = dx * 7, ty = dy * 7;
                Line(tx, ty, tx - dx * 3 + dy * 3, ty - dy * 3 + dx * 3);
                Line(tx, ty, tx - dx * 3 - dy * 3, ty - dy * 3 - dx * 3);
            }
        }
        else if (mode == GizmoMode.Rotate)
        {
            // Three quarters of a circle ending in an arrow head.
            const double r = 6;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(c.X + r, c.Y), isFilled: false);
                g.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, true, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, IconPen, arc);
            Line(0, -r, -3.5, -r - 3);
            Line(0, -r, -3.5, -r + 3);
        }
        else
        {
            // A small box growing into a larger one.
            context.DrawRectangle(IconPen.Brush, null, new Rect(c.X - 7, c.Y + 1, 5, 5));
            context.DrawRectangle(null, IconPen, new Rect(c.X - 7, c.Y - 6, 12, 12));
            Line(0, -1, 4, -5);
            Line(4, -5, 1, -5);
            Line(4, -5, 4, -2);
        }
    }
}
