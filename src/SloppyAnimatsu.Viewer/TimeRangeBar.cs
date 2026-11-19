using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The range bar under the timeline: the bar's extent
/// across the width and a window over the frames the timeline shows, with a handle at each end.
/// A background click pages, a body drag slides, a handle drag resizes (faster past the bar's
/// ends), the wheel zooms about the pointer. The state is a <see cref="TimeBar"/>.
/// </summary>
public sealed class TimeRangeBar : Control
{
    private const double HandleWidth = 7;
    private static readonly IBrush BackBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
    private static readonly IBrush WindowBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x6a, 0x8c, 0xa8));
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.FromRgb(0x9c, 0xc0, 0xdc));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xe8, 0xe8, 0xe8));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly IBrush TimeBrush = new SolidColorBrush(Color.FromRgb(0xe8, 0x50, 0x40));
    private static readonly IBrush KeyTickBrush = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0x60));
    private static readonly Typeface Font = new("Segoe UI");

    private enum Part { None, Background, Left, Body, Right }

    private TimeBar? _bar;
    private Part _press;
    private double _pressX;
    private int _pressStart, _pressLength, _pressMin, _pressMax;
    private float _time;
    private IReadOnlyList<int> _keyTimes = [];

    public TimeRangeBar()
    {
        Height = 18;
        ClipToBounds = true;
    }

    /// <summary>Raised after a gesture changed the window.</summary>
    public event Action? RangeChanged;

    public TimeBar? Bar
    {
        get => _bar;
        set { _bar = value; InvalidateVisual(); }
    }

    /// <summary>The playhead, drawn as a tick.</summary>
    public float Time
    {
        get => _time;
        set { if (_time != value) { _time = value; InvalidateVisual(); } }
    }

    /// <summary>The key times of all stacks, drawn as small ticks so the bar shows where keys are.</summary>
    public IReadOnlyList<int> KeyTimes
    {
        get => _keyTimes;
        set { _keyTimes = value; InvalidateVisual(); }
    }

    private double XOf(double frame) => _bar == null ? 0 : _bar.FractionOf(frame) * Bounds.Width;

    private Rect WindowRect
    {
        get
        {
            if (_bar == null) return default;
            double x0 = XOf(_bar.ViewStart), x1 = XOf(_bar.ViewEnd);
            double w = Math.Max(2 * HandleWidth + 2, x1 - x0);
            return new Rect(Math.Min(x0, Bounds.Width - w), 1, w, Math.Max(0, Bounds.Height - 2));
        }
    }

    private Part HitTest(Point p)
    {
        if (_bar == null) return Part.None;
        var w = WindowRect;
        if (p.X >= w.X - 2 && p.X < w.X + HandleWidth) return Part.Left;
        if (p.X > w.Right - HandleWidth && p.X <= w.Right + 2) return Part.Right;
        if (p.X >= w.X && p.X <= w.Right) return Part.Body;
        return Part.Background;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackBrush, bounds);
        if (_bar == null || bounds.Width < 4) return;
        foreach (int t in _keyTimes)
            context.FillRectangle(KeyTickBrush, new Rect(XOf(t) - 0.5, bounds.Height - 5, 1, 4));
        var w = WindowRect;
        context.DrawRectangle(WindowBrush, null, w, 3, 3);
        context.DrawRectangle(HandleBrush, null, new Rect(w.X, w.Y, HandleWidth, w.Height), 3, 3);
        context.DrawRectangle(HandleBrush, null, new Rect(w.Right - HandleWidth, w.Y, HandleWidth, w.Height), 3, 3);
        context.FillRectangle(TimeBrush, new Rect(XOf(_time) - 1, 0, 2, bounds.Height));
        DrawLabel(context, _bar.Min.ToString(CultureInfo.InvariantCulture), 3, false);
        DrawLabel(context, _bar.Max.ToString(CultureInfo.InvariantCulture), bounds.Width - 3, true);
        var text = Text($"{_bar.ViewStart} – {_bar.ViewEnd}", TextBrush);
        if (text.Width + 2 * HandleWidth + 4 < w.Width)
            context.DrawText(text, new Point(w.X + (w.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
    }

    private static FormattedText Text(string s, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9, brush);

    private void DrawLabel(DrawingContext context, string s, double x, bool right)
    {
        var text = Text(s, DimBrush);
        context.DrawText(text, new Point(right ? x - text.Width : x, (Bounds.Height - text.Height) / 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_press == Part.None)
        {
            Cursor = HitTest(p) is Part.Left or Part.Right ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
            return;
        }
        if (_bar == null || Bounds.Width <= 0) return;
        int delta = (int)Math.Round((_pressMax - _pressMin) * (p.X - _pressX) / Bounds.Width);
        _bar.Set(_pressMin, _pressMax, _pressStart, _pressLength);
        switch (_press)
        {
            case Part.Body: _bar.Slide(_pressStart, delta); break;
            case Part.Left: _bar.DragLeft(_pressStart, _pressLength, _pressMin, delta); break;
            case Part.Right: _bar.DragRight(_pressStart, _pressLength, _pressMax, delta); break;
        }
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_bar == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        var part = HitTest(p);
        _pressX = p.X;
        _pressStart = _bar.ViewStart;
        _pressLength = _bar.ViewLength;
        _pressMin = _bar.Min;
        _pressMax = _bar.Max;
        if (part == Part.Background)
        {
            _bar.Page(_bar.FrameAt(p.X / Math.Max(1, Bounds.Width)));
            InvalidateVisual();
            RangeChanged?.Invoke();
        }
        else if (part != Part.None)
        {
            _press = part;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press == Part.None) return;
        _press = Part.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_bar == null || e.Delta.Y == 0 || Bounds.Width <= 0) return;
        _bar.Zoom(_bar.FrameAt(e.GetPosition(this).X / Bounds.Width), e.Delta.Y);
        InvalidateVisual();
        RangeChanged?.Invoke();
        e.Handled = true;
    }
}
