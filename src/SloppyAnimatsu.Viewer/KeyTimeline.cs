using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The timeline: a frame ruler and one row of keys per stack of the animation, with the current
/// frame as a cursor. Clicking or dragging outside the keys scrubs; clicking a key selects it
/// (Ctrl toggles, Shift adds) and makes its stack the active one; dragging selected keys asks for a
/// timeline move, and Alt+dragging a key asks for a ripple retime of it and the keys after it.
/// Extreme and breakdown marks are drawn above and below their keys. Fades are drawn as ramps with a
/// handle at their far end, which a drag lengthens or shortens
/// The layout and colours are a viewer design, not the.
/// </summary>
public sealed class KeyTimeline : Control
{
    // The layout and hit testing live in SloppyAnimatsu.Core.Model.TimelineLayout, so the pointer rules
    // can be tested and printed without a window; the names below are the drawing's.
    private const double Gutter = TimelineLayout.Gutter;
    private const double RightPad = TimelineLayout.RightPad;
    private const double RulerHeight = TimelineLayout.RulerHeight;
    private const double RowHeight = TimelineLayout.RowHeight;
    private const double KeyRadius = 5;
    private const double KeyBoxHeight = TimelineLayout.KeyHeight;
    private const double FadeHandle = TimelineLayout.FadeHandle;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c));
    private static readonly IBrush RulerBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly IBrush RowAltBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly IBrush ActiveRowBrush = new SolidColorBrush(Color.FromRgb(0x2c, 0x37, 0x4c));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xd0, 0xd0, 0xd0));
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78));
    private static readonly IBrush KeyBrush = new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xc8));
    private static readonly IBrush AudioClipBrush = new SolidColorBrush(Color.FromRgb(0x7a, 0xb8, 0xd8));
    private static readonly IBrush AudioMutedBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x68, 0x70));
    private static readonly IPen AudioSelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0x30)), 1.5);
    private static readonly IBrush SelectedKeyBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0x30));
    private static readonly IBrush BlendshapeKeyBrush = new SolidColorBrush(Color.FromRgb(0x5f, 0xd0, 0xc0));
    private static readonly IBrush TextureKeyBrush = new SolidColorBrush(Color.FromRgb(0xd8, 0x8f, 0xe0));
    private static readonly IBrush GhostKeyBrush = new SolidColorBrush(Color.FromArgb(0xa0, 0xff, 0xa0, 0x30));
    private static readonly IBrush ExtremeBrush = new SolidColorBrush(Color.FromRgb(0xff, 0x55, 0x55));
    private static readonly IBrush BreakdownBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xaa, 0xff));
    private static readonly IBrush FadeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x70, 0xd0, 0x90));
    private static readonly IBrush FadeHandleBrush = new SolidColorBrush(Color.FromRgb(0x70, 0xd0, 0x90));
    private static readonly IPen FadePen = new Pen(new SolidColorBrush(Color.FromRgb(0x70, 0xd0, 0x90)));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));
    private static readonly IPen KeyOutline = new Pen(Brushes.Black);
    // The frame distances between keys: light grey, quieter than the key numbers but readable on every row colour.
    private static readonly IBrush GapMarkBrush = new SolidColorBrush(Color.FromRgb(0xa8, 0xa8, 0xa8));
    private static readonly IPen GapMarkPen = new Pen(GapMarkBrush, 1);
    private static readonly IBrush KeyNumberBrush = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));
    private static readonly IBrush LoopKeyBrush = new SolidColorBrush(Color.FromRgb(0x8c, 0xc8, 0xe8));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.FromRgb(0xe0, 0x40, 0x40));
    private static readonly IPen CursorPen = new Pen(CursorBrush, 1.5);
    private static readonly IPen InsertPen = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xa0, 0x30)), 2);
    private static readonly Typeface Font = new(FontFamily.Default);

    private readonly HashSet<int> _selected = [];
    private readonly TimelineLayout _layout = new();
    private Animation? _animation;
    private float _time;

    // The pointer gesture in progress.
    private enum Gesture { None, Scrub, KeyPress, KeyDrag, FadePress, FadeDrag, StackPress, StackDrag, TexturePress, TextureDrag }

    // The texture key being pressed or dragged.
    private Material? _textureMaterial;
    private int _textureFrom;
    private Gesture _gesture;
    private bool _ripple;
    private double _pressX;
    private int _pressKey = -1;
    private int _dragDelta;
    private bool _fadeIn;
    private int _pressFrame;
    private double _pressY;
    private int _pressRow = -1;
    private int _dropSlot = -1;

    public KeyTimeline()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>A stack was dragged by its name in the Mixer: the stack and its new place (0 = top).</summary>
    public event Action<AnimStack, int>? StackMoveRequested;

    /// <summary>The time was scrubbed or a key clicked: the new frame.</summary>
    public event Action<float>? TimeChanged;

    /// <summary>A texture key was dragged: its material, the frame it was on and the frame it was dropped on.</summary>
    public event Action<Material, int, int>? TextureKeyMoved;

    /// <summary>Selected keys of <see cref="ActiveStack"/> were dragged by a whole number of frames.</summary>
    public event Action<int>? MoveRequested;

    /// <summary>A key of <see cref="ActiveStack"/> was Alt+dragged by a whole number of frames: (key, delta), moving the later keys too.</summary>
    public event Action<int, int>? RippleRequested;

    /// <summary>Ctrl+Left/Right with keys selected: move them before (-1) or after (+1) their neighbour.</summary>
    public event Action<int>? ReorderRequested;

    /// <summary>+ or - with keys selected: widen (+1) or narrow (-1) the gap before each of them.</summary>
    public event Action<int>? GapChangeRequested;

    /// <summary>Delete was pressed with keys selected; true with Shift (close the gaps).</summary>
    public event Action<bool>? DeleteRequested;

    /// <summary>K was pressed: create a key in <see cref="ActiveStack"/> at the cursor.</summary>
    public event Action? CreateRequested;

    /// <summary>Ctrl+C was pressed: copy the selected keys.</summary>
    public event Action? CopyRequested;

    /// <summary>The key under the last right click, or -1 for an empty spot (picks the context menu).</summary>
    public int ContextKey { get; private set; } = -1;

    /// <summary>The frame under the last right click.</summary>
    public int ContextFrame { get; private set; }

    /// <summary>Ctrl+V (false) or Ctrl+Shift+V (true, paste after the selection) was pressed.</summary>
    public event Action<bool>? PasteRequested;

    /// <summary>A fade handle of a key of <see cref="ActiveStack"/> started moving: (key, whether it is the fade-in).</summary>
    public event Action<int, bool>? FadeDragStarted;

    /// <summary>The fade handle being dragged is a whole number of frames from where it was pressed.</summary>
    public event Action<int>? FadeDragged;

    /// <summary>The fade handle drag ended: true to keep the change, false (Escape) to put it back.</summary>
    public event Action<bool>? FadeDragEnded;

    /// <summary>The active stack or the key selection changed.</summary>
    public event Action? SelectionChanged;

    public Animation? Animation
    {
        get => _animation;
        set
        {
            if (ReferenceEquals(_animation, value)) { Refresh(); return; }
            _animation = value;
            ActiveStack = value?.CurrentStack is { } current && value.Stacks.Contains(current) ? current : value?.Stacks.FirstOrDefault();
            _selected.Clear();
            CancelGesture();
            Refresh();
        }
    }

    public float Time
    {
        get => _time;
        set
        {
            _time = value;
            InvalidateVisual();
        }
    }

    /// <summary>The stack whose row is highlighted: the one keys are selected, created and deleted in.</summary>
    public AnimStack? ActiveStack { get; private set; }

    /// <summary>The selected keys of <see cref="ActiveStack"/>.</summary>
    public IReadOnlyCollection<int> SelectedKeys => _selected;

    /// <summary>Whether a key drag is in progress.</summary>
    public bool IsDragging => _gesture is Gesture.KeyDrag or Gesture.FadeDrag or Gesture.StackDrag or Gesture.TextureDrag;

    /// <summary>The ripple drag's key, or -1.</summary>
    private int RippleKey => _ripple && _gesture == Gesture.KeyDrag && _dragDelta != 0 ? _pressKey : -1;

    /// <summary>Selects <paramref name="keys"/> of <paramref name="stack"/> (indices past its keys are dropped).</summary>
    public void Select(AnimStack? stack, IEnumerable<int> keys)
    {
        ActiveStack = stack;
        _selected.Clear();
        if (stack != null)
        {
            int n = stack.Keys.Count;
            foreach (int k in keys) if (k >= 0 && k < n) _selected.Add(k);
            ScrollRowIntoView(Stacks.ToList().IndexOf(stack));
        }
        InvalidateMeasure();
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>Re-reads the stacks and keys after an edit.</summary>
    public void Refresh()
    {
        if (ActiveStack != null && (_animation == null || !_animation.Stacks.Contains(ActiveStack))) ActiveStack = _animation?.Stacks.FirstOrDefault();
        int n = ActiveStack?.Keys.Count ?? 0;
        _selected.RemoveWhere(k => k >= n);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private IReadOnlyList<AnimStack> Stacks => _animation?.Stacks ?? [];

    /// <summary>The layout for the control's current size and animation.</summary>
    private TimelineLayout Layout
    {
        get
        {
            _layout.Width = Bounds.Width;
            _layout.Height = Bounds.Height;
            _layout.Animation = _animation;
            _layout.ActiveStack = ActiveStack;
            return _layout;
        }
    }

    /// <summary>The frames shown, set by the range bar; null shows the animation's key range.</summary>
    public (int Start, int End)? ViewRange
    {
        get => _layout.ViewRange;
        set { if (_layout.ViewRange != value) { _layout.ViewRange = value; InvalidateVisual(); } }
    }

    private (int Start, int End) Range => Layout.Range;

    private double FrameWidth => Layout.FrameWidth;

    private double X(float frame) => Layout.X(frame);


    private float FrameAt(double x) => Layout.FrameAt(x);

    /// <summary>The stack drawn on a row: a blendshape row below the stacks belongs to the active stack.</summary>
    private AnimStack? StackAtRow(int row) => Layout.StackAt(row);

    private static double RowTop(int row) => TimelineLayout.RowTop(row);

    private int _maxVisibleRows = 4;
    private double _scroll;
    private double _lastPreferredHeight = -1;

    /// <summary>
    /// The rows the timebar grows to before it scrolls (Timebar preferences "Displayed stacks",
    /// <c>NbVisibleStacks</c>); 0 or less means 4.
    /// </summary>
    public int MaxVisibleRows
    {
        get => _maxVisibleRows;
        set
        {
            int v = value > 0 ? value : 4;
            if (v == _maxVisibleRows) return;
            _maxVisibleRows = v;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The height the timebar asks for: the ruler and up to <see cref="MaxVisibleRows"/> rows.</summary>
    public double PreferredHeight =>
        RulerHeight + Math.Min(Math.Max(Layout.RowCount, 1), _maxVisibleRows) * RowHeight + 2;

    /// <summary>Raised (after layout) when <see cref="PreferredHeight"/> changes, so the panel can refit.</summary>
    public event Action? PreferredHeightChanged;

    /// <summary>Raised when the scroll offset or its range may have changed, so a scroll bar can follow.</summary>
    public event Action? ScrollChanged;

    /// <summary>The height of all rows (the scrolled content, without the ruler).</summary>
    private double ContentHeight => Math.Max(Layout.RowCount, 1) * RowHeight + 2;

    /// <summary>The rows' visible height: the control below its ruler.</summary>
    public double ViewportHeight => Math.Max(0, Bounds.Height - RulerHeight);

    /// <summary>How far the rows can scroll.</summary>
    public double ScrollMaximum => Math.Max(0, ContentHeight - ViewportHeight);

    /// <summary>How far the rows are scrolled up, in DIPs (the ruler and the cursor do not scroll).</summary>
    public double ScrollOffset
    {
        get => _scroll;
        set
        {
            double v = Math.Clamp(value, 0, ScrollMaximum);
            if (Math.Abs(v - _scroll) < 0.01) return;
            _scroll = v;
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }
    }

    /// <summary>Scrolls just enough to show <paramref name="row"/> whole.</summary>
    public void ScrollRowIntoView(int row)
    {
        if (row < 0 || ViewportHeight <= 0) return;
        double top = row * RowHeight, bottom = top + RowHeight;
        if (top < _scroll) ScrollOffset = top;
        else if (bottom > _scroll + ViewportHeight) ScrollOffset = bottom - ViewportHeight;
    }

    /// <summary>A point of the control in layout space: rows shift with the scroll, the ruler does not.</summary>
    private Point Content(Point p) => p.Y < RulerHeight ? p : new Point(p.X, p.Y + _scroll);

    protected override Size MeasureOverride(Size availableSize)
    {
        double preferred = PreferredHeight;
        if (Math.Abs(preferred - _lastPreferredHeight) > 0.01)
        {
            _lastPreferredHeight = preferred;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PreferredHeightChanged?.Invoke());
        }
        return new(0, preferred);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        double max = Math.Max(0, ContentHeight - Math.Max(0, finalSize.Height - RulerHeight));
        if (_scroll > max) _scroll = max;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollChanged?.Invoke());
        return size;
    }

    /// <summary>
    /// The wheel zooms the frame window around the frame under the pointer, as the range bar's wheel
    /// does (<see cref="ZoomRequested"/>); with Shift held, or over the row names, it scrolls the rows.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        var p = e.GetPosition(this);
        bool rows = (e.KeyModifiers & KeyModifiers.Shift) != 0 || p.X < Gutter;
        if (rows)
        {
            if (ScrollMaximum <= 0) return;
            ScrollOffset -= e.Delta.Y * RowHeight;
        }
        else
        {
            if (ZoomRequested == null) return;
            ZoomRequested(FrameAt(p.X), e.Delta.Y);
        }
        e.Handled = true;
    }

    /// <summary>The wheel over the frames: zoom the frame window around this frame by these notches.</summary>
    public event Action<double, double>? ZoomRequested;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        if (bounds.Width <= Gutter + 10)
        {
            context.FillRectangle(RulerBrush, new Rect(0, 0, bounds.Width, RulerHeight));
            return;
        }
        // The rows scroll under the ruler: drawn in layout space, clipped below the ruler.
        using (context.PushClip(new Rect(0, RulerHeight, bounds.Width, Math.Max(0, bounds.Height - RulerHeight))))
        using (context.PushTransform(Matrix.CreateTranslation(0, -_scroll)))
            RenderRows(context, bounds);
        double cx = Math.Round(X(_time)) + 0.5;
        var frameChip = FrameChip(cx, bounds);
        RenderRuler(context, bounds, frameChip?.Rect);

        if (cx >= Gutter) context.DrawLine(CursorPen, new Point(cx, 0), new Point(cx, bounds.Height));
        if (frameChip is { } chip)
        {
            context.FillRectangle(CursorBrush, chip.Rect, 2);
            context.DrawText(chip.Text, new Point(chip.Rect.X + 3, chip.Rect.Y + (chip.Rect.Height - chip.Text.Height) / 2));
        }
    }

    /// <summary>
    /// The current frame's chip in the ruler: beside the cursor line, on its right unless that would
    /// leave the control, then on its left; null while the cursor is outside the frames.
    /// </summary>
    private (Rect Rect, FormattedText Text)? FrameChip(double cx, Rect bounds)
    {
        if (cx < Gutter || cx > bounds.Width) return null;
        float frame = MathF.Round(_time, 2);
        string label = frame == MathF.Round(frame) ? ((int)frame).ToString(CultureInfo.InvariantCulture)
            : frame.ToString("0.##", CultureInfo.InvariantCulture);
        var text = Text(label, Brushes.White, 10);
        double w = text.Width + 6, h = RulerHeight - 4;
        double x = cx + 2 + w <= bounds.Width ? cx + 2 : cx - 2 - w;
        return (new Rect(x, 2, w, h), text);
    }

    /// <summary>
    /// The ruler: a tick per frame when there is room, labels at a round step at least 40 DIPs apart.
    /// A label that would touch the current frame's chip (<paramref name="chip"/>) is left out until
    /// the cursor moves away.
    /// </summary>
    private void RenderRuler(DrawingContext context, Rect bounds, Rect? chip)
    {
        context.FillRectangle(RulerBrush, new Rect(0, 0, bounds.Width, RulerHeight));
        var (start, end) = Range;
        double fw = FrameWidth;
        int step = 1;
        foreach (int s in new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 })
        {
            step = s;
            if (s * fw >= 40) break;
        }
        int minor = fw >= 4 ? 1 : step;
        for (int f = start - Mod(start, minor); f <= end; f += minor)
        {
            if (f < start) continue;
            double x = Math.Round(X(f)) + 0.5;
            bool labelled = Mod(f, step) == 0;
            context.DrawLine(TickPen, new Point(x, labelled ? 10 : 15), new Point(x, RulerHeight));
            if (!labelled) continue;
            var text = Text(f.ToString(CultureInfo.InvariantCulture), TextBrush, 10);
            if (chip is { } c && x + 2 < c.Right + 3 && x + 2 + text.Width > c.X - 3) continue;
            context.DrawText(text, new Point(x + 2, 1));
        }
    }

    /// <summary>The stack and blendshape rows, in layout space (row 0 just below the ruler, unscrolled).</summary>
    private void RenderRows(DrawingContext context, Rect bounds)
    {
        var stacks = Stacks;
        for (int row = 0; row < stacks.Count; row++)
        {
            var stack = stacks[row];
            bool active = ReferenceEquals(stack, ActiveStack);
            var rowRect = new Rect(0, RowTop(row), bounds.Width, RowHeight);
            context.FillRectangle(active ? ActiveRowBrush : row % 2 == 0 ? RowBrush : RowAltBrush, rowRect);

            // Stack name, with its UI colour as a swatch and its kind as a suffix.
            var color = stack.Color;
            if (color.Length >= 4 && color[3] != 0)
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(color[0], color[1], color[2])), new Rect(4, rowRect.Y + 4, 4, RowHeight - 8));
            string name = (stack.Name ?? "(unnamed)") + (stack is AudioStack ? " ♪" : "") + (stack.IsAdditive ? " +" : "") + (stack.IsCycle ? " ↻" : "");
            using (context.PushClip(new Rect(12, rowRect.Y, Gutter - 16, RowHeight)))
                context.DrawText(Text(name, stack.IsEnabled ? TextBrush : DimTextBrush, 11), new Point(12, rowRect.Y + 2));

            using (context.PushClip(new Rect(Gutter, rowRect.Y, bounds.Width - Gutter, RowHeight)))
            {
                var keys = stack.Keys;
                double cy = rowRect.Y + RowHeight / 2;
                int ripple = active ? RippleKey : -1;
                for (int k = 0; k < keys.Count; k++)
                {
                    double x = X(keys[k].Time);
                    if (keys[k].FadeIn != 0)
                    {
                        double x0 = X(keys[k].Time - keys[k].FadeIn);
                        DrawRamp(context, x0, rowRect.Bottom - 2, x, rowRect.Y + 2);
                        context.FillRectangle(FadeHandleBrush, FadeHandleRect(row, keys[k], true));
                    }
                    if (keys[k].FadeOut != 0)
                    {
                        double x1 = X(keys[k].Time + keys[k].FadeOut);
                        DrawRamp(context, x, rowRect.Y + 2, x1, rowRect.Bottom - 2);
                        context.FillRectangle(FadeHandleBrush, FadeHandleRect(row, keys[k], false));
                    }
                }
                // Only the considered keys are drawn: a non-cycle stack's last stored key is an end
                // marker; audio events are all drawn.
                int drawn = stack is AudioStack ? keys.Count : Math.Min(keys.Count, stack.ConsideredKeyCount);
                // The frame distance between neighbouring keys. While this row's keys are being dragged
                // it measures where they would land, so the numbers follow the drag.
                if (stack is not AudioStack && drawn > 1)
                {
                    bool moving = active && (ripple >= 0 || (_gesture == Gesture.KeyDrag && _dragDelta != 0));
                    var marks = new (float Time, int Key)[drawn];
                    for (int k = 0; k < drawn; k++)
                    {
                        bool shifted = moving && (ripple >= 0 ? k >= ripple : _selected.Contains(k));
                        marks[k] = (keys[k].Time + (shifted ? _dragDelta : 0), k);
                    }
                    if (moving) Array.Sort(marks, (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Key.CompareTo(b.Key));
                    for (int i = 0; i + 1 < drawn; i++)
                        DrawGapMark(context, marks[i].Time, Layout.KeyWidth(stack, drawn, marks[i].Key),
                            marks[i + 1].Time, Layout.KeyWidth(stack, drawn, marks[i + 1].Key), cy);
                }
                for (int k = 0; k < drawn; k++)
                {
                    bool selected = active && _selected.Contains(k);
                    bool dragged = ripple >= 0 ? k >= ripple
                        : selected && _gesture == Gesture.KeyDrag && _dragDelta != 0;
                    double x = X(keys[k].Time);
                    if (stack is AudioStack audio && k < audio.Events.Count)
                    {
                        // An audio event is a clip whose width is the sound's duration
                        var clip = AudioBlock(audio.Events[k], rowRect);
                        context.FillRectangle(clip.Event.Audio == null || clip.Event.Mute ? AudioMutedBrush : AudioClipBrush, clip.Rect);
                        if (selected) context.DrawRectangle(null, AudioSelectedPen, clip.Rect);
                        if (clip.Rect.Width > 26)
                        {
                            using (context.PushClip(clip.Rect.Deflate(2)))
                                context.DrawText(Text(clip.Event.Entry?.DisplayName ?? "(no file)", Background, 9),
                                    new Point(clip.Rect.X + 3, clip.Rect.Y + 1));
                        }
                        continue;
                    }
                    // A key is a box with its number (index + 1); a cycle's loop key shows the loop glyph.
                    bool loopKey = stack.IsCycle && k == drawn - 1 && drawn > 1;
                    string label = loopKey ? "↻" : (k + 1).ToString(CultureInfo.InvariantCulture);
                    DrawKeyBox(context, x, cy, Layout.KeyWidth(stack, drawn, k), label, dragged ? DimTextBrush : selected ? SelectedKeyBrush : loopKey ? LoopKeyBrush : KeyBrush);
                    if (dragged) DrawKeyBox(context, X(keys[k].Time + _dragDelta), cy, Layout.KeyWidth(stack, drawn, k), label, GhostKeyBrush);
                    // Extreme marks above the key, breakdown marks below
                    // the loop key has none.
                    if (loopKey) continue;
                    if (keys[k].Flags == KeyEditor.ExtremeMark)
                        context.DrawGeometry(ExtremeBrush, null, Triangle(x, rowRect.Y + 3, -3));
                    else if (keys[k].Flags == KeyEditor.BreakdownMark)
                        context.DrawGeometry(BreakdownBrush, null, Triangle(x, rowRect.Bottom - 3, 3));
                }
            }
        }

        // Each blendshape track of the active stack gets a row below the stacks: its keys share the
        // stack's times but hold two values, which each key prints beside it
        // The keys are the stack's key indices, so selecting,
        // dragging and deleting them uses the same generic path as a stack row's keys.
        var blendshapeTracks = Layout.BlendshapeTracks;
        var activeKeys = Layout.ActiveStack?.Keys ?? [];
        for (int i = 0; i < blendshapeTracks.Count; i++)
        {
            int row = stacks.Count + i;
            var track = blendshapeTracks[i];
            var rowRect = new Rect(0, RowTop(row), bounds.Width, RowHeight);
            context.FillRectangle(row % 2 == 0 ? RowBrush : RowAltBrush, rowRect);
            using (context.PushClip(new Rect(12, rowRect.Y, Gutter - 16, RowHeight)))
                context.DrawText(Text("BS " + (track.Target?.Name ?? "(controller)"), TextBrush, 11), new Point(12, rowRect.Y + 2));
            using (context.PushClip(new Rect(Gutter, rowRect.Y, bounds.Width - Gutter, RowHeight)))
            {
                double cy = rowRect.Y + RowHeight / 2;
                int considered = Layout.ActiveStack?.ConsideredKeyCount ?? 0;
                for (int k = 0; k < track.Keys.Count && k < activeKeys.Count && k < considered; k++)
                {
                    if (track.Keys[k] is not { } key) continue;
                    double x = X(activeKeys[k].Time);
                    bool selected = _selected.Contains(k);
                    bool dragged = selected && _gesture == Gesture.KeyDrag && _dragDelta != 0;
                    context.DrawGeometry(dragged ? DimTextBrush : selected ? SelectedKeyBrush : BlendshapeKeyBrush,
                        KeyOutline, Diamond(x, cy, KeyRadius));
                    if (dragged) context.DrawGeometry(GhostKeyBrush, KeyOutline, Diamond(X(activeKeys[k].Time + _dragDelta), cy, KeyRadius));
                    var values = key.Values;
                    context.DrawText(Text($"{values[0]:0.##}/{values[1]:0.##}", DimTextBrush, 8),
                        new Point(x + KeyRadius + 1, rowRect.Y + 4));
                }
            }
        }
        // The texture layer: one row per material keyed in this animation, after the blendshape rows.
        // Its keys are stepped swaps, labelled with the texture they show.
        var textureMaterials = Layout.TextureMaterials;
        for (int i = 0; i < textureMaterials.Count && _animation != null; i++)
        {
            int row = Layout.TextureRowStart + i;
            var material = textureMaterials[i];
            var rowRect = new Rect(0, RowTop(row), bounds.Width, RowHeight);
            context.FillRectangle(row % 2 == 0 ? RowBrush : RowAltBrush, rowRect);
            using (context.PushClip(new Rect(12, rowRect.Y, Gutter - 16, RowHeight)))
                context.DrawText(Text("TX " + material.Name, TextBrush, 11), new Point(12, rowRect.Y + 2));
            using (context.PushClip(new Rect(Gutter, rowRect.Y, bounds.Width - Gutter, RowHeight)))
            {
                double cy = rowRect.Y + RowHeight / 2;
                bool dragging = _gesture == Gesture.TextureDrag && ReferenceEquals(_textureMaterial, material) && _dragDelta != 0;
                foreach (var key in material.TextureKeys(_animation))
                {
                    double x = X(key.Time);
                    bool dragged = dragging && key.Time == _textureFrom;
                    context.DrawGeometry(dragged ? DimTextBrush : TextureKeyBrush, KeyOutline, Diamond(x, cy, KeyRadius));
                    if (dragged) context.DrawGeometry(GhostKeyBrush, KeyOutline, Diamond(X(key.Time + _dragDelta), cy, KeyRadius));
                    context.DrawText(Text(material.TextureName(key.Index), DimTextBrush, 8), new Point(x + KeyRadius + 1, rowRect.Y + 4));
                }
            }
        }
        if (stacks.Count == 0)
            context.DrawText(Text(_animation == null ? "Rest pose: no keys." : "No stacks.", DimTextBrush, 11), new Point(12, RowTop(0) + 2));

        // The Mixer drag's insertion line, across the names.
        if (_gesture == Gesture.StackDrag && _dropSlot >= 0)
        {
            double y = RowTop(_dropSlot);
            context.DrawLine(InsertPen, new Point(0, y), new Point(Gutter, y));
        }
    }

    /// <summary>A fade ramp: the line from (x0, y0) to (x1, y1), shaded down to the row's bottom.</summary>
    private static void DrawRamp(DrawingContext context, double x0, double y0, double x1, double y1)
    {
        double bottom = Math.Max(y0, y1);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(x0, y0), true);
            g.LineTo(new Point(x1, y1));
            if (y1 != bottom) g.LineTo(new Point(x1, bottom));
            if (y0 != bottom) g.LineTo(new Point(x0, bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(FadeBrush, null, geometry);
        context.DrawLine(FadePen, new Point(x0, y0), new Point(x1, y1));
    }

    /// <summary>
    /// The handle of a key's fade-in or fade-out, half a row wide, at the far end of the ramp: ending
    /// one pixel before the frame <c>Time - FadeIn</c>, or starting at <c>Time + FadeOut</c>.
    /// </summary>
    private Rect FadeHandleRect(int row, StackKey key, bool fadeIn)
    {
        var (x, y, width, height) = Layout.HandleRect(row, key, fadeIn);
        return new Rect(x, y, width, height);
    }

    /// <summary>The fade handle under <paramref name="p"/>: (row, key, whether it is the fade-in), or row -1.</summary>
    private (int Row, int Key, bool FadeIn) HitTestFade(Point p) => Layout.FadeHandleAt(ToVector(Content(p)));

    /// <summary>The whole frame under <paramref name="x"/>, clamped to the time range.</summary>
    private int FrameUnder(double x) => Layout.FrameUnder(x);

    private static Vector2 ToVector(Point p) => new((float)p.X, (float)p.Y);

    private static int Mod(int a, int m) => ((a % m) + m) % m;

    private static FormattedText Text(string text, IBrush brush, double size) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);

    /// <summary>
    /// A key: a rectangle centred on the frame, <paramref name="w"/> wide (narrower as its neighbours
    /// crowd in, <see cref="TimelineLayout.KeyWidth"/>), with its number (or loop glyph) inside while
    /// the number fits.
    /// </summary>
    private static void DrawKeyBox(DrawingContext context, double x, double cy, double w, string label, IBrush fill)
    {
        var rect = new Rect(Math.Round(x - w / 2) + 0.5, Math.Round(cy - KeyBoxHeight / 2) + 0.5, Math.Round(w), KeyBoxHeight);
        context.DrawRectangle(fill, KeyOutline, rect, 2, 2);
        var text = Text(label, KeyNumberBrush, 10);
        if (text.Width + 3 > w) return;
        context.DrawText(text, new Point(x - text.Width / 2, cy - text.Height / 2));
    }

    /// <summary>
    /// The frame distance between a key at <paramref name="fromTime"/> and the next at
    /// <paramref name="toTime"/>, in light grey on the row's centre line: the number with a thin arrow
    /// out to each key while it fits, then a line with an arrowhead at each end, then a plain line,
    /// then nothing (<see cref="TimelineLayout.GapMark"/>).
    /// </summary>
    private void DrawGapMark(DrawingContext context, float fromTime, double fromWidth, float toTime, double toWidth, double cy)
    {
        double left = X(fromTime) + fromWidth / 2 + TimelineLayout.GapMarkPad;
        double right = X(toTime) - toWidth / 2 - TimelineLayout.GapMarkPad;
        double gap = right - left + 2 * TimelineLayout.GapMarkPad;
        if (gap < TimelineLayout.GapLineMinWidth) return;
        double y = Math.Round(cy) + 0.5;
        var text = Text(TimelineLayout.FrameDistanceText(fromTime, toTime), GapMarkBrush, 9);
        switch (TimelineLayout.GapMark(gap, text.Width))
        {
            case GapMarkKind.Labelled:
                double mid = (left + right) / 2;
                double textLeft = Math.Round(mid - text.Width / 2);
                context.DrawText(text, new Point(textLeft, cy - text.Height / 2));
                DrawArrow(context, textLeft - TimelineLayout.GapMarkPad, left, y);
                DrawArrow(context, textLeft + text.Width + TimelineLayout.GapMarkPad, right, y);
                break;
            case GapMarkKind.Arrows:
                context.DrawLine(GapMarkPen, new Point(left + 1, y), new Point(right - 1, y));
                context.DrawGeometry(GapMarkBrush, null, ArrowHead(left, y, +1));
                context.DrawGeometry(GapMarkBrush, null, ArrowHead(right, y, -1));
                break;
            case GapMarkKind.Line:
                context.DrawLine(GapMarkPen, new Point(left, y), new Point(right, y));
                break;
        }
    }

    /// <summary>A thin shaft from <paramref name="from"/> with its arrowhead touching <paramref name="tip"/>.</summary>
    private static void DrawArrow(DrawingContext context, double from, double tip, double y)
    {
        double dir = Math.Sign(tip - from);
        context.DrawLine(GapMarkPen, new Point(from, y), new Point(tip - dir, y));
        context.DrawGeometry(GapMarkBrush, null, ArrowHead(tip, y, -dir));
    }

    /// <summary>An arrowhead whose tip is at (<paramref name="x"/>, <paramref name="y"/>), its base toward <paramref name="dir"/> (+1 right, -1 left).</summary>
    private static StreamGeometry ArrowHead(double x, double y, double dir)
    {
        const double length = TimelineLayout.GapArrowHead, half = 2.5;
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x + dir * length, y - half));
        g.LineTo(new Point(x + dir * length, y + half));
        g.EndFigure(true);
        return geometry;
    }

    private static StreamGeometry Diamond(double x, double y, double r)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y - r), true);
        g.LineTo(new Point(x + r, y));
        g.LineTo(new Point(x, y + r));
        g.LineTo(new Point(x - r, y));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>A small triangle pointing at the key: its tip at (x, y), its base <paramref name="height"/> further away (negative: above).</summary>
    private static StreamGeometry Triangle(double x, double y, double height)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y), true);
        g.LineTo(new Point(x + 3.5, y + height));
        g.LineTo(new Point(x - 3.5, y + height));
        g.EndFigure(true);
        return geometry;
    }

    /// <summary>The clip of an audio event: from its start frame to start + duration, at least 3 DIPs wide.</summary>
    private (Rect Rect, AudioEvent Event) AudioBlock(AudioEvent e, Rect rowRect)
    {
        var (x0, x1) = Layout.AudioClip(e);
        return (new Rect(x0, rowRect.Y + 3, x1 - x0, RowHeight - 6), e);
    }

    /// <summary>The stack row under <paramref name="p"/>, and the key of that stack nearest to it within reach.</summary>
    private (int Row, int Key) HitTest(Point p) => Layout.KeyAt(ToVector(Content(p)));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            // A right click on an unselected key selects it alone, for the context menu; one on an
            // empty spot of a stack row activates that stack and records the frame for Create Key
            // and Paste Key(s).
            var (r, k) = HitTest(point.Position);
            ContextKey = k;
            ContextFrame = (int)MathF.Round(FrameAt(point.Position.X));
            if (k < 0 && r >= 0 && r < Stacks.Count && !ReferenceEquals(StackAtRow(r), ActiveStack))
            {
                ActiveStack = StackAtRow(r);
                _selected.Clear();
                InvalidateMeasure();
                InvalidateVisual();
                SelectionChanged?.Invoke();
            }
            if (r < 0 || k < 0 || (ReferenceEquals(StackAtRow(r), ActiveStack) && _selected.Contains(k))) return;
            ActiveStack = StackAtRow(r);
            _selected.Clear();
            _selected.Add(k);
            InvalidateMeasure();
            InvalidateVisual();
            SelectionChanged?.Invoke();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
        var p = point.Position;
        var (fadeRow, fadeKey, fadeIn) = HitTestFade(p);
        if (fadeRow >= 0)
        {
            // A fade handle: the drag starts once the pointer has moved a little.
            if (!ReferenceEquals(StackAtRow(fadeRow), ActiveStack)) { ActiveStack = StackAtRow(fadeRow); _selected.Clear(); InvalidateMeasure(); }
            _gesture = Gesture.FadePress;
            _pressX = p.X;
            _pressFrame = FrameUnder(p.X);
            _pressKey = fadeKey;
            _fadeIn = fadeIn;
            _dragDelta = 0;
            InvalidateVisual();
            SelectionChanged?.Invoke();
            return;
        }
        if (_animation != null && Layout.TextureKeyAt(ToVector(Content(p))) is { Row: >= 0, Material: { } textured } hit)
        {
            // A texture key: a click seeks to it, a drag moves it along its row.
            _gesture = Gesture.TexturePress;
            _textureMaterial = textured;
            _textureFrom = hit.Key.Time;
            _pressX = p.X;
            _dragDelta = 0;
            InvalidateVisual();
            return;
        }
        var (row, key) = HitTest(p);
        bool toggle = (e.KeyModifiers & KeyModifiers.Control) != 0, add = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if (row >= 0 && !ReferenceEquals(StackAtRow(row), ActiveStack))
        {
            ActiveStack = StackAtRow(row);
            _selected.Clear();
            InvalidateMeasure();
        }
        if (key >= 0)
        {
            if (toggle) { if (!_selected.Remove(key)) _selected.Add(key); }
            else if (add) _selected.Add(key);
            else if (!_selected.Contains(key)) { _selected.Clear(); _selected.Add(key); }
            _gesture = toggle ? Gesture.None : Gesture.KeyPress;
            _ripple = (e.KeyModifiers & KeyModifiers.Alt) != 0;
            _pressX = p.X;
            _pressKey = key;
            _dragDelta = 0;
        }
        else
        {
            if (!toggle && !add) _selected.Clear();
            if (p.X < Gutter && row >= 0 && row < Stacks.Count && !toggle && !add)
            {
                // A stack's name: a drag moves the stack in the Mixer.
                _gesture = Gesture.StackPress;
                _pressY = p.Y;
                _pressRow = row;
            }
            else if (p.X >= Gutter)
            {
                _gesture = Gesture.Scrub;
                TimeChanged?.Invoke(MathF.Round(FrameAt(p.X)));
            }
        }
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        switch (_gesture)
        {
            case Gesture.Scrub:
                TimeChanged?.Invoke(MathF.Round(Math.Clamp(FrameAt(p.X), Range.Start, Range.End)));
                break;
            case Gesture.TexturePress when Math.Abs(p.X - _pressX) >= 4:
                _gesture = Gesture.TextureDrag;
                goto case Gesture.TextureDrag;
            case Gesture.TextureDrag:
                int shift = Math.Max((int)Math.Round((p.X - _pressX) / FrameWidth), -_textureFrom);
                if (shift != _dragDelta) { _dragDelta = shift; InvalidateVisual(); }
                break;
            case Gesture.KeyPress when Math.Abs(p.X - _pressX) >= 4:
                _gesture = Gesture.KeyDrag;
                goto case Gesture.KeyDrag;
            case Gesture.FadePress when Math.Abs(p.X - _pressX) >= 4:
                _gesture = Gesture.FadeDrag;
                FadeDragStarted?.Invoke(_pressKey, _fadeIn);
                goto case Gesture.FadeDrag;
            case Gesture.FadeDrag:
                int frames = FrameUnder(p.X) - _pressFrame;
                if (frames != _dragDelta) { _dragDelta = frames; FadeDragged?.Invoke(frames); InvalidateVisual(); }
                break;
            case Gesture.StackPress when Math.Abs(p.Y - _pressY) >= 4:
                _gesture = Gesture.StackDrag;
                goto case Gesture.StackDrag;
            case Gesture.StackDrag:
                // The gap between rows nearest to the pointer: 0 = above the top stack.
                int slot = Layout.SlotAt(Content(p).Y);
                if (slot != _dropSlot) { _dropSlot = slot; InvalidateVisual(); }
                break;
            case Gesture.KeyDrag:
                int delta = (int)Math.Round((p.X - _pressX) / FrameWidth);
                // A ripple keeps the key at least one frame after the previous one.
                if (_ripple && _pressKey > 0 && ActiveStack is { } rippled && _pressKey < rippled.Keys.Count)
                    delta = Math.Max(delta, rippled.Keys[_pressKey - 1].Time + 1 - rippled.Keys[_pressKey].Time);
                if (delta != _dragDelta) { _dragDelta = delta; InvalidateVisual(); }
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var gesture = _gesture;
        int delta = _dragDelta, key = _pressKey;
        bool ripple = _ripple;
        int pressRow = _pressRow, slot = _dropSlot;
        var textureMaterial = _textureMaterial;
        int textureFrom = _textureFrom;
        _gesture = Gesture.None; // a fade drag ends here, kept, not cancelled
        CancelGesture();
        e.Pointer.Capture(null);
        if (gesture == Gesture.FadeDrag) FadeDragEnded?.Invoke(true);
        if (gesture == Gesture.TextureDrag && delta != 0 && textureMaterial != null)
            TextureKeyMoved?.Invoke(textureMaterial, textureFrom, textureFrom + delta);
        else if (gesture == Gesture.TexturePress) TimeChanged?.Invoke(textureFrom);
        if (gesture == Gesture.StackDrag && slot >= 0 && pressRow < Stacks.Count)
        {
            // Dropped below its own row, the stack's final place is one less (it left the gap above).
            int to = slot > pressRow ? slot - 1 : slot;
            if (to != pressRow) StackMoveRequested?.Invoke(Stacks[pressRow], to);
        }
        if (gesture == Gesture.KeyDrag && delta != 0 && ripple) RippleRequested?.Invoke(key, delta);
        else if (gesture == Gesture.KeyDrag && delta != 0) MoveRequested?.Invoke(delta);
        else if (gesture == Gesture.KeyPress && ActiveStack is { } stack && key < stack.Keys.Count)
        {
            // A plain click on a key also moves the cursor to it.
            _selected.Clear();
            _selected.Add(key);
            SelectionChanged?.Invoke();
            TimeChanged?.Invoke(stack.Keys[key].Time);
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelGesture();
        InvalidateVisual();
    }

    /// <summary>Drops the gesture in progress; a fade drag is put back.</summary>
    private void CancelGesture()
    {
        bool fadeDrag = _gesture == Gesture.FadeDrag;
        _gesture = Gesture.None;
        if (fadeDrag) FadeDragEnded?.Invoke(false);
        _ripple = false;
        _pressKey = -1;
        _pressRow = -1;
        _dropSlot = -1;
        _dragDelta = 0;
        _textureMaterial = null;
    }

    /// <summary>The live key map (Hotkeys.PanelActions); the shipped keys until the window sets it.</summary>
    public HotkeyMap Keys { get; set; } = HotkeyMap.Default;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape && _gesture != Gesture.None)
        {
            // Escape always cancels a drag; it is not a configurable action.
            CancelGesture();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        switch (Keys.ActionFor(HotkeyScope.Timeline, ShortcutInput.FromKey(e.Key, e.KeyModifiers)))
        {
            case "DeleteObject" or "TimelineDeleteKeys":
                if (_selected.Count > 0) DeleteRequested?.Invoke(false);
                break;
            case "DeleteKeysCloseGaps":
                if (_selected.Count > 0) DeleteRequested?.Invoke(true);
                break;
            case "StackerCreateKey":
                CreateRequested?.Invoke();
                break;
            case "SelectAllKeys":
                if (ActiveStack is { } stack) Select(stack, Enumerable.Range(0, stack.Keys.Count));
                break;
            case "CopyKeys":
                CopyRequested?.Invoke();
                break;
            case "PasteKeys":
                PasteRequested?.Invoke(false);
                break;
            case "PasteKeysAfter":
                PasteRequested?.Invoke(true);
                break;
            case "MoveKeysLeft":
                if (_selected.Count > 0) ReorderRequested?.Invoke(-1);
                break;
            case "MoveKeysRight":
                if (_selected.Count > 0) ReorderRequested?.Invoke(1);
                break;
            case "WidenKeyGap":
                if (_selected.Count > 0) GapChangeRequested?.Invoke(1);
                break;
            case "NarrowKeyGap":
                if (_selected.Count > 0) GapChangeRequested?.Invoke(-1);
                break;
            case "TimelinePreviousFrame":
                TimeChanged?.Invoke(MathF.Round(_time) - 1);
                break;
            case "TimelineNextFrame":
                TimeChanged?.Invoke(MathF.Round(_time) + 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
