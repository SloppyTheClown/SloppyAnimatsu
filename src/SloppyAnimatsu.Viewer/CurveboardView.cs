using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The Curveboard: the displayed channels of some tracks of one stack
/// drawn as curves over the animation's frames, lined up with the timeline below it. Rotation is
/// shown in Euler degrees. Clicking a key selects its value (Shift adds, Ctrl toggles) and dragging
/// selected keys moves their values; the in and out handles of selected keys can be dragged, which
/// makes those sides Custom. Dragging on empty space draws a selection box, and a plain click there
/// clears the selection and sets the time; Ctrl+A selects every key and Escape none. The wheel
/// zooms the values and a middle drag pans them; F fits the view. The edits themselves are made by
/// the window, through the drag events. The geometry, the selection and what a press hits live in
/// <see cref="CurveboardLayout"/> so they can be tested and printed; the layout and colours are a
/// viewer design, not the.
/// </summary>
public sealed class CurveboardView : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c));
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0xb0));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0x30));
    private static readonly IBrush BoxBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0xa0, 0x30));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));
    private static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x48)));
    private static readonly IPen KeyLinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)));
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.FromRgb(0xe0, 0x40, 0x40)), 1.5);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd0)));
    private static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0x30)));
    private static readonly IPen KeyOutline = new Pen(Brushes.Black);
    private static readonly Typeface Font = new(FontFamily.Default);

    /// <summary>One colour per channel.</summary>
    public static readonly Color[] ChannelColors =
    [
        Color.FromRgb(0xe0, 0x50, 0x50), Color.FromRgb(0x50, 0xc0, 0x50), Color.FromRgb(0x50, 0x80, 0xf0),
        Color.FromRgb(0xff, 0x80, 0x80), Color.FromRgb(0x90, 0xf0, 0x80), Color.FromRgb(0x80, 0xb0, 0xff),
        Color.FromRgb(0xa0, 0x30, 0x30), Color.FromRgb(0x30, 0x80, 0x30), Color.FromRgb(0x30, 0x50, 0xa0),
        Color.FromRgb(0xe0, 0xc0, 0x40), Color.FromRgb(0xe0, 0x90, 0x40), Color.FromRgb(0xc0, 0x60, 0xd0), Color.FromRgb(0x40, 0xd0, 0xd0),
    ];

    private static readonly IPen[] CurvePens = ChannelColors.Select(c => (IPen)new Pen(new SolidColorBrush(c), 1.5)).ToArray();
    private static readonly IBrush[] KeyBrushes = ChannelColors.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

    private readonly CurveboardLayout _layout = new();

    private CurveboardGesture _gesture;
    private bool _pan;
    private Point _press, _pointer;
    private double _panMin, _panMax;

    public CurveboardView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>A plain click on empty space: the new frame.</summary>
    public event Action<float>? TimeChanged;

    /// <summary>The selection changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>A drag of key values or a handle began: its description for the undo history.</summary>
    public event Action<string>? DragStarted;

    /// <summary>The drag moved: the whole edit from the drag's start, to apply to the keys as they were then.</summary>
    public event Action<Action<KeyEditor>>? Dragged;

    /// <summary>The drag ended.</summary>
    public event Action? DragEnded;

    /// <summary>The evaluator the curves are sampled with.</summary>
    public PoseEvaluator? Evaluator
    {
        get => _layout.Evaluator;
        set => _layout.Evaluator = value;
    }

    /// <summary>The channels shown and edited.</summary>
    public AnimChannels Displayed
    {
        get => _layout.Displayed;
        set => _layout.Displayed = value;
    }

    /// <summary>The animation whose frame range is shown.</summary>
    public Animation? Animation
    {
        get => _layout.Animation;
        set => _layout.Animation = value;
    }

    /// <summary>The stack whose tracks are shown, and the tracks of it to show.</summary>
    public void Show(AnimStack? stack, IReadOnlyList<AnimTrack> tracks) => _layout.Show(stack, tracks);

    public AnimStack? Stack => _layout.Stack;

    public float Time
    {
        get => _layout.Time;
        set
        {
            _layout.Time = value;
            InvalidateVisual();
        }
    }

    /// <summary>The range bar's window, shared with the timeline so their frames line up.</summary>
    public (int Start, int End)? ViewRange
    {
        get => _layout.ViewRange;
        set { if (_layout.ViewRange != value) { _layout.ViewRange = value; Refresh(); } }
    }

    /// <summary>The selection as the Core edits take it.</summary>
    public IReadOnlyCollection<CurveKeySelection> Selection => _layout.Selection;

    /// <summary>Resamples the curves after the keys or the shown channels changed.</summary>
    public void Refresh()
    {
        SyncSize();
        _layout.AllowFit = _gesture == CurveboardGesture.None;
        if (_layout.Refresh()) SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Fits the values of the shown curves into the view.</summary>
    public void Fit()
    {
        SyncSize();
        _layout.Fit();
        InvalidateVisual();
    }

    /// <summary>Selects the values of every key of the shown curves.</summary>
    public void SelectAll()
    {
        _layout.SelectAll();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (_layout.Selection.Count == 0) return;
        _layout.ClearSelection();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>The layout reads the pointer against the size the board is drawn at.</summary>
    private void SyncSize()
    {
        _layout.Width = Bounds.Width;
        _layout.Height = Bounds.Height;
    }

    private static Vector2 At(Point p) => new((float)p.X, (float)p.Y);

    private static Point To(Vector2 v) => new(v.X, v.Y);

    public override void Render(DrawingContext context)
    {
        SyncSize();
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        context.FillRectangle(GutterBrush, new Rect(0, 0, CurveboardLayout.Gutter, bounds.Height));
        if (bounds.Width <= CurveboardLayout.Gutter + 10 || bounds.Height < 10) return;

        // Value grid at a round step, labelled in the gutter.
        double span = _layout.YMax - _layout.YMin;
        double step = Math.Pow(10, Math.Floor(Math.Log10(span / 4)));
        foreach (double m in new[] { 1, 2, 5, 10 })
            if (span / (step * m) <= 8) { step *= m; break; }
        for (double v = Math.Ceiling(_layout.YMin / step) * step; v <= _layout.YMax; v += step)
        {
            double y = Math.Round(_layout.Y(v)) + 0.5;
            context.DrawLine(Math.Abs(v) < step / 2 ? ZeroPen : GridPen, new Point(CurveboardLayout.Gutter, y), new Point(bounds.Width, y));
            context.DrawText(Text(Math.Round(v, 6).ToString("0.###", CultureInfo.InvariantCulture), TextBrush, 10),
                new Point(CurveboardLayout.Gutter - 50, y - 7));
        }

        using var clip = context.PushClip(new Rect(CurveboardLayout.Gutter, 0, bounds.Width - CurveboardLayout.Gutter, bounds.Height));
        if (_layout.Stack is { } stack)
            foreach (var key in stack.Keys)
            {
                double x = Math.Round(_layout.X(key.Time)) + 0.5;
                context.DrawLine(KeyLinePen, new Point(x, 0), new Point(x, bounds.Height));
            }

        foreach (var curve in _layout.CurvesOnScreen)
        {
            var values = curve.Values;
            if (values.Length < 2) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(_layout.X(_layout.First), _layout.Y(values[0])), false);
                for (int i = 1; i < values.Length; i++)
                    g.LineTo(new Point(_layout.X(_layout.First + i), _layout.Y(values[i])));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, CurvePens[curve.Channel], geometry);
        }

        foreach (var handle in _layout.Handles())
        {
            context.DrawLine(HandlePen, To(handle.KeyPoint), To(handle.End));
            context.DrawEllipse(_layout.IsSelected(handle) ? SelectedBrush : Brushes.White, KeyOutline, To(handle.End), 3, 3);
        }

        foreach (var point in _layout.KeyPoints())
        {
            context.DrawRectangle(_layout.IsSelected(point) ? SelectedBrush : KeyBrushes[point.Channel], KeyOutline,
                new Rect(point.Point.X - CurveboardLayout.KeyRadius, point.Point.Y - CurveboardLayout.KeyRadius,
                    2 * CurveboardLayout.KeyRadius, 2 * CurveboardLayout.KeyRadius));
        }

        double cx = Math.Round(_layout.X(_layout.Time)) + 0.5;
        context.DrawLine(CursorPen, new Point(cx, 0), new Point(cx, bounds.Height));

        if (_gesture == CurveboardGesture.Box) context.DrawRectangle(BoxBrush, BoxPen, new Rect(_press, _pointer).Normalize());

        if (_layout.CurvesOnScreen.Count == 0)
            context.DrawText(Text(_layout.Stack == null ? "No stack." : "Select an animated controller to see its curves.", TextBrush, 12),
                new Point(CurveboardLayout.Gutter + 10, 8));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        SyncSize();
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        _press = _pointer = p;
        _pan = false;
        if (point.Properties.IsMiddleButtonPressed)
        {
            _gesture = CurveboardGesture.None;
            _pan = true;
            (_panMin, _panMax) = (_layout.YMin, _layout.YMax);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        _gesture = CurveboardGesture.None;
        if (!point.Properties.IsLeftButtonPressed || p.X < CurveboardLayout.Gutter) return;
        bool add = (e.KeyModifiers & KeyModifiers.Shift) != 0, toggle = (e.KeyModifiers & KeyModifiers.Control) != 0;

        var press = _layout.Press(At(p), add, toggle);
        _gesture = press.Gesture;
        if (press.SelectionTouched) SelectionChanged?.Invoke();
        if (_gesture == CurveboardGesture.HandleDrag && _layout.PressedHandle is { } handle)
            DragStarted?.Invoke($"Drag {Curves.ChannelNames[handle.Channel]} tangent");
        if (_gesture != CurveboardGesture.None) e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _pointer = p;
        SyncSize();
        if (_pan)
        {
            (_layout.YMin, _layout.YMax) = _layout.Panned(_press.Y, p.Y, _panMin, _panMax);
            InvalidateVisual();
            return;
        }
        switch (_gesture)
        {
            case CurveboardGesture.Press when !CurveboardLayout.IsClick(At(_press), At(p)):
                _gesture = CurveboardGesture.KeyDrag;
                DragStarted?.Invoke("Move key values");
                goto case CurveboardGesture.KeyDrag;
            case CurveboardGesture.KeyDrag:
            {
                float delta = _layout.MovedValue(_press.Y, p.Y);
                var selection = _layout.Selection;
                Dragged?.Invoke(editor => editor.MoveKeyValues(selection, delta));
                break;
            }
            case CurveboardGesture.HandleDrag:
            {
                if (_layout.PressedHandle is not { } handle) break;
                float slope = _layout.Slope(handle, At(p));
                Dragged?.Invoke(editor => editor.SetTangentSlope(handle.Track, handle.Key, handle.Channel, handle.Side, slope));
                break;
            }
            case CurveboardGesture.Box:
                InvalidateVisual();
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        SyncSize();
        if (_gesture == CurveboardGesture.Box)
        {
            bool add = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
            if (CurveboardLayout.IsClick(At(_press), At(p)))
            {
                if (!add && _layout.Selection.Count > 0)
                {
                    _layout.ClearSelection();
                    SelectionChanged?.Invoke();
                }
                TimeChanged?.Invoke(_layout.FrameUnder(p.X));
            }
            else
            {
                _layout.BoxSelect(At(_press), At(p), add);
                SelectionChanged?.Invoke();
            }
        }
        EndGesture();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndGesture();
    }

    private void EndGesture()
    {
        if (_gesture is CurveboardGesture.KeyDrag or CurveboardGesture.HandleDrag)
        {
            _gesture = CurveboardGesture.None;
            DragEnded?.Invoke();
        }
        _gesture = CurveboardGesture.None;
        _pan = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Zooms the values around the pointer.
        SyncSize();
        _layout.ZoomAt(e.GetPosition(this).Y, e.Delta.Y);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>The live key map (Hotkeys.PanelActions); the shipped keys until the window sets it.</summary>
    public HotkeyMap Keys { get; set; } = HotkeyMap.Default;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (Keys.ActionFor(HotkeyScope.Curveboard, ShortcutInput.FromKey(e.Key, e.KeyModifiers)))
        {
            case "CurveboardFit":
                Fit();
                e.Handled = true;
                break;
            case "SelectAllKeys":
                SelectAll();
                e.Handled = true;
                break;
            case "CurveboardClearSelection":
                ClearSelection();
                e.Handled = true;
                break;
        }
    }

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
}
