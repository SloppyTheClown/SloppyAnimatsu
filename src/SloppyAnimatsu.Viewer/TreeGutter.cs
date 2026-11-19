using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The left part of a Scene panel row: the connector lines of the tree and, on a row with
/// children, the squared plus/minus box that expands or collapses it. One level of indentation is
/// <see cref="Indent"/> wide; a level's line runs down the centre of its parent's box, so the lines
/// leave a box downwards and turn right into each child row. This is SloppyAnimatsu's own drawing, not
/// The tree control.
/// </summary>
public sealed class TreeGutter : Control
{
    /// <summary>The width of one level of indentation.</summary>
    public const double Indent = 16;

    private const double BoxSize = 9;
    private static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 200, 200, 200)), 1);
    private static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 210, 210, 210)), 1);
    private static readonly IPen SignPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 230, 230, 230)), 1);
    private static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(255, 45, 45, 48));

    private readonly int _depth;
    // For each ancestor level 0..depth-2: whether that level's line carries on below this row.
    private readonly bool[] _continues;
    private readonly bool _isLast;
    private readonly bool _hasChildren;
    private bool _expanded;

    /// <summary>The box was clicked; the argument is true when Ctrl was held (the whole hierarchy).</summary>
    public event Action<bool>? Toggled;

    public TreeGutter(int depth, bool[] continues, bool isLast, bool hasChildren)
    {
        _depth = depth;
        _continues = continues;
        _isLast = isLast;
        _hasChildren = hasChildren;
        Width = (depth + 1) * Indent;
        Background = Brushes.Transparent;
        if (hasChildren) Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Whether the row has children, so has a box.</summary>
    public bool HasChildren => _hasChildren;

    /// <summary>Lets the transparent gutter take clicks over its whole area.</summary>
    public IBrush? Background { get; set; }

    /// <summary>Whether the row's children are shown; the box draws a minus while they are.</summary>
    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_hasChildren || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Only the box's own level toggles; a press on an ancestor's line does nothing.
        if (e.GetPosition(this).X < _depth * Indent) return;
        e.Handled = true;
        Toggled?.Invoke(e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    public override void Render(DrawingContext context)
    {
        double h = Bounds.Height;
        double mid = Math.Floor(h / 2) + 0.5;
        if (Background != null) context.FillRectangle(Background, new Rect(Bounds.Size));
        static double Centre(int level) => Math.Floor(level * Indent + Indent / 2) + 0.5;

        // The lines of the ancestors that still have rows below this one.
        for (int level = 0; level < _depth - 1; level++)
            if (_continues[level])
                context.DrawLine(LinePen, new Point(Centre(level), 0), new Point(Centre(level), h));

        double own = Centre(_depth);
        if (_depth > 0)
        {
            // The elbow from the parent's line into this row.
            double parent = Centre(_depth - 1);
            context.DrawLine(LinePen, new Point(parent, 0), new Point(parent, _isLast ? mid : h));
            double end = _hasChildren ? own - BoxSize / 2 : Width;
            context.DrawLine(LinePen, new Point(parent, mid), new Point(end, mid));
        }
        if (!_hasChildren) return;

        // The line down to the children, then the box over it.
        if (_expanded) context.DrawLine(LinePen, new Point(own, mid), new Point(own, h));
        context.DrawLine(LinePen, new Point(own + BoxSize / 2, mid), new Point(Width, mid));
        var box = new Rect(own - BoxSize / 2, mid - BoxSize / 2, BoxSize, BoxSize);
        context.FillRectangle(BoxFill, box);
        context.DrawRectangle(BoxPen, box);
        context.DrawLine(SignPen, new Point(own - 2.5, mid), new Point(own + 2.5, mid));
        if (!_expanded) context.DrawLine(SignPen, new Point(own, mid - 2.5), new Point(own, mid + 2.5));
    }
}
