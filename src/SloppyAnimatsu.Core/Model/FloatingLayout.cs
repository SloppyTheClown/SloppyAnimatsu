using System.Globalization;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// How a panel is pinned while the user has not moved it. The panels start against the left
/// and right screen edges and across the bottom, which is what its saved <c>CDialogPosition</c>
/// dock layout describes; dragging one by its title bar unpins it (<see cref="Free"/>).
/// </summary>
public enum PanelAnchor
{
    Left,
    Right,
    Bottom,
    Free,
}

/// <summary>
/// One panel of the interface as the floating layout knows it: a stable key, the title its header
/// shows, the dialog classes it stands for in <c>preferences.akt</c>, its size before the
/// saved layout resizes it, and the side it is pinned to when the file does not name it.
/// </summary>
public sealed record PanelSpec(
    string Key,
    string Title,
    IReadOnlyList<string> Dialogs,
    double Width,
    double Height,
    PanelAnchor Fallback);

/// <summary>
/// One panel's place and state: its rectangle in the window (the title bar included), the edge it is
/// pinned to while it has not been dragged, whether it is folded to its title bar, and its order in
/// the stack (a press brings it to the front).
/// </summary>
public sealed class FloatingPanelState
{
    internal FloatingPanelState(PanelSpec spec, int order)
    {
        Spec = spec;
        Order = order;
        Width = spec.Width;
        Height = spec.Height;
        Anchor = spec.Fallback;
    }

    public PanelSpec Spec { get; }

    /// <summary>The stable key the window and the tests name the panel by (<c>Bank</c>, ...).</summary>
    public string Key => Spec.Key;

    /// <summary>The title the header shows, as the uppercase ones do.</summary>
    public string Title => Spec.Title;

    /// <summary>
    /// The <c>CDialogPosition</c> class the panel's saved height and width are written to, or null
    /// when this context's layout does not name it.
    /// </summary>
    public string? DialogClass { get; internal set; }

    public PanelAnchor Anchor { get; internal set; }

    /// <summary>The panel's left edge in the window (see <see cref="FloatingLayout.Arrange"/>).</summary>
    public double X { get; internal set; }

    /// <summary>The panel's top edge in the window.</summary>
    public double Y { get; internal set; }

    /// <summary>The panel's width in the window.</summary>
    public double Width { get; internal set; }

    /// <summary>The panel's height when it is open, title bar included.</summary>
    public double Height { get; internal set; }

    /// <summary>
    /// For <see cref="PanelAnchor.Left"/> the distance from the left window edge to the panel's left
    /// edge; for <see cref="PanelAnchor.Right"/> the distance from the right window edge to its right
    /// edge; unused for the other anchors.
    /// </summary>
    public double Edge { get; internal set; }

    /// <summary>
    /// Whether <see cref="Edge"/> is measured from the right window edge. A pinned panel follows its
    /// anchor; a dragged one takes the side its centre was dropped in, as the undocked dialogs
    /// do, so a thinner window carries a right-hand panel with its edge.
    /// </summary>
    public bool FromRight { get; internal set; }

    /// <summary>
    /// The panel's home top as a fraction of the window height (the <c>PosY</c>): a shorter
    /// window moves the panel up and the old height brings it back. Unused while the panel hangs
    /// below a <see cref="Parent"/>.
    /// </summary>
    public double TopFraction { get; internal set; }

    /// <summary>
    /// The panel this one hangs below (the <c>DockDown</c> link, read from the child's side):
    /// it sits against the parent's bottom edge and moves with it. Null for a panel placed on its own.
    /// </summary>
    public FloatingPanelState? Parent { get; internal set; }

    /// <summary>Whether the panel is folded to its title bar (the arrow).</summary>
    public bool Collapsed { get; internal set; }

    /// <summary>The stack order: a higher value is drawn over a lower one.</summary>
    public int Order { get; internal set; }

    /// <summary>The height the panel takes in the window now (the title bar alone when folded).</summary>
    public double OuterHeight => Collapsed ? FloatingLayout.HeaderHeight : Height;

    public double Bottom => Y + OuterHeight;

    public double Right => X + Width;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Key}: {Anchor} {X:0},{Y:0} {Width:0}x{OuterHeight:0} order {Order}{(Collapsed ? " collapsed" : "")}");
}

/// <summary>
/// The interface as one viewport with floating panels over it
/// every panel has
/// a rectangle, a title bar to drag it by, an arrow to fold it to that bar and a stack position.
/// The first arrangement comes from the saved dock layout - the columns against the left and right
/// edges, their <c>DockDown</c> order and saved heights, and the bottom panel - but nothing reserves
/// space in it: the viewport is behind every panel, and a panel only covers what it sits on.
/// The window places the panels; this class owns their geometry and state, so moving, folding and
/// resizing are testable without a window.
/// </summary>
public sealed class FloatingLayout
{
    /// <summary>The title bar's height, which is all a folded panel shows.</summary>
    public const int HeaderHeight = DockLayout.HeaderHeight;

    /// <summary>The gap between a floating panel and the window edge, and between panels.</summary>
    public const int Margin = 8;

    /// <summary>The gap between two panels that touch (a column's chain in the saved layout).</summary>
    public const int Gap = 6;

    /// <summary>The least width a panel can be dragged to or saved with.</summary>
    public const int MinimumWidth = 140;

    /// <summary>The least height a panel's content can be dragged to, below its title bar.</summary>
    public const int MinimumContentHeight = DockLayout.MinimumContentHeight;

    /// <summary>The least height an open panel can have, title bar included.</summary>
    public const int MinimumHeight = HeaderHeight + MinimumContentHeight;

    /// <summary>
    /// The share of the window's width (inside the margins) the bottom panel - the timeline - spans,
    /// centred; the curveboard above it takes the same span. A viewer design, so the viewport stays
    /// reachable on both sides of the timeline.
    /// </summary>
    public const double BottomWidthFraction = 0.85;

    /// <summary>The bottom panel's width in a window <paramref name="windowWidth"/> wide.</summary>
    public static double BottomWidth(double windowWidth) =>
        Math.Max(MinimumWidth, Math.Round((windowWidth - 2 * Margin) * BottomWidthFraction));

    /// <summary>The bottom panel's left edge: centred, never closer than the margin to the left edge.</summary>
    public static double BottomX(double windowWidth) =>
        Math.Max(Margin, Math.Round((windowWidth - BottomWidth(windowWidth)) / 2));

    private readonly List<FloatingPanelState> _panels = [];
    private int _top;

    private FloatingLayout(string context, int width, int height)
    {
        Context = context;
        WindowWidth = width;
        WindowHeight = height;
    }

    /// <summary>The interface context the layout was built for (<c>Rigging</c>, <c>Animation</c>).</summary>
    public string Context { get; }

    /// <summary>The width the panels were last arranged for.</summary>
    public int WindowWidth { get; private set; }

    /// <summary>The height the panels were last arranged for.</summary>
    public int WindowHeight { get; private set; }

    /// <summary>Every panel, in the catalogue's order.</summary>
    public IReadOnlyList<FloatingPanelState> Panels => _panels;

    /// <summary>The panel called <paramref name="key"/>, or null.</summary>
    public FloatingPanelState? Panel(string key)
    {
        foreach (var panel in _panels)
            if (string.Equals(panel.Key, key, StringComparison.Ordinal)) return panel;
        return null;
    }

    /// <summary>
    /// Places the catalogue's panels for a window of <paramref name="width"/> ×
    /// <paramref name="height"/>. A panel the saved layout names takes its saved side, order and
    /// height; one it does not name goes in a fresh column on its fallback side (or across the
    /// bottom), so it is still reachable instead of covering another panel.
    /// </summary>
    public static FloatingLayout FromDock(DockLayout dock, IReadOnlyList<PanelSpec> specs, int width, int height)
    {
        var layout = new FloatingLayout(dock.Context,
            Math.Max(MinimumWidth + 2 * Margin, width),
            Math.Max(MinimumHeight + 2 * Margin, height));
        for (int i = 0; i < specs.Count; i++) layout._panels.Add(new FloatingPanelState(specs[i], i));

        var rects = dock.Resolve(layout.WindowWidth, layout.WindowHeight)
            .ToDictionary(rect => rect.DialogClass, StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        // Where the bottom panel is drawn - the saved one, or the catalogue's fallback - so the side
        // columns fit above it, as the dock stops its columns above the timeline.
        DockRect? bottomRect = dock.Bottom != null && rects.TryGetValue(dock.Bottom.DialogClass, out var bottom) ? bottom : null;
        PanelSpec? fallbackBottom = specs.FirstOrDefault(spec => spec.Fallback == PanelAnchor.Bottom);
        double bottomHeight = bottomRect is { } reservedBottom
            ? BottomHeight(reservedBottom.Height)
            : fallbackBottom == null
                ? 0
                : BottomHeight(Math.Min(fallbackBottom.Height, layout.WindowHeight - 2 * Margin));
        double bottomTop = bottomHeight > 0 ? layout.WindowHeight - Margin - bottomHeight : layout.WindowHeight - Margin;
        double sideLimit = Math.Max(MinimumHeight, bottomTop - Gap);

        double BottomHeight(double saved) => Math.Clamp(saved, MinimumHeight,
            Math.Max(MinimumHeight, (int)(layout.WindowHeight * 0.45)));

        FloatingPanelState? Take(string dialogClass)
        {
            foreach (var spec in specs)
            {
                if (!spec.Dialogs.Contains(dialogClass, StringComparer.Ordinal)) continue;
                var state = layout.Panel(spec.Key);
                if (state == null || !placed.Add(state.Key)) return null;
                return state;
            }
            return null;
        }

        double ColumnWidth(DockColumn column)
        {
            double widest = 0;
            foreach (var slot in column.Panels)
                foreach (var spec in specs)
                {
                    if (!spec.Dialogs.Contains(slot.DialogClass, StringComparer.Ordinal)) continue;
                    widest = Math.Max(widest, spec.Width);
                }
            if (widest <= 0) widest = DockLayout.DefaultWidth;
            // A width the file stores applies to the whole column; the catalogue's widest panel caps
            // it, because the columns are about as wide as their panel needs.
            double value = column.Width > 0 ? Math.Min(column.Width, widest) : widest;
            return Math.Clamp(value, MinimumWidth, Math.Max(MinimumWidth, layout.WindowWidth - 2 * Margin));
        }

        // The saved heights are pixel heights from a taller window. When a column no longer fits
        // above the bottom panel, its panels shrink together rather than the last one being clipped
        // to its title bar (which is what the dock's own rectangle rule would do).
        double PlaceColumn(DockColumn column, PanelAnchor anchor, double edge)
        {
            double columnWidth = ColumnWidth(column);
            var members = new List<(FloatingPanelState State, string DialogClass, DockRect Rect, double Height, bool Folded)>();
            foreach (var slot in column.Panels)
            {
                var state = Take(slot.DialogClass);
                if (state == null || !rects.TryGetValue(slot.DialogClass, out var rect)) continue;
                double height = slot.HasStoredHeight && slot.Height > 0
                    ? slot.Height + HeaderHeight
                    : Math.Min(rect.Height, state.Spec.Height);
                members.Add((state, slot.DialogClass, rect, Math.Max(MinimumHeight, height), slot.Folded));
            }
            if (members.Count == 0) return 0;
            // The first panel shown keeps its own saved top when nothing in the column links down
            // to it (a chain of its own, below a dialog this catalogue has no panel for); a panel
            // hanging from such a dialog starts where the dock's stacking put it.
            string first = members[0].DialogClass;
            bool hangs = column.Panels.Any(slot => slot.Down == first);
            double ownTop = column.Panels.First(slot => slot.DialogClass == first).Top * layout.WindowHeight;
            double top = Math.Max(Margin, hangs ? members[0].Rect.Y : Math.Round(ownTop));
            double total = members.Sum(member => member.Folded ? HeaderHeight : member.Height);
            double available = sideLimit - top;
            if (available > 0 && total > available && available >= members.Count * MinimumHeight)
            {
                double scale = available / total;
                for (int i = 0; i < members.Count; i++)
                    members[i] = (members[i].State, members[i].DialogClass, members[i].Rect,
                        Math.Max(MinimumHeight, members[i].Height * scale), members[i].Folded);
            }
            double y = top;
            FloatingPanelState? above = null;
            foreach (var (state, dialogClass, _, height, folded) in members)
            {
                state.DialogClass = dialogClass;
                state.Anchor = anchor;
                state.FromRight = anchor == PanelAnchor.Right;
                state.Edge = edge;
                state.Width = columnWidth;
                state.Height = height;
                state.Collapsed = folded;
                state.Y = y;
                state.TopFraction = y / layout.WindowHeight;
                state.Parent = above;
                above = state;
                y += folded ? HeaderHeight : height;
            }
            return columnWidth;
        }

        double edge = Margin;
        foreach (var column in dock.Columns.Where(c => c.Anchor == DockAnchor.Left).OrderBy(c => c.Distance))
        {
            double used = PlaceColumn(column, PanelAnchor.Left, edge);
            if (used > 0) edge += used + Gap;
        }

        double rightEdge = layout.WindowWidth - Margin;
        foreach (var column in dock.Columns.Where(c => c.Anchor == DockAnchor.Right).OrderBy(c => c.Distance))
        {
            double used = PlaceColumn(column, PanelAnchor.Right, layout.WindowWidth - rightEdge);
            if (used > 0) rightEdge -= used + Gap;
        }

        if (dock.Bottom is { } bottomSlot && bottomRect is { } savedBottom && Take(bottomSlot.DialogClass) is { } bottomState)
        {
            bottomState.DialogClass = bottomSlot.DialogClass;
            bottomState.Anchor = PanelAnchor.Bottom;
            bottomState.Edge = Margin;
            bottomState.Width = BottomWidth(layout.WindowWidth);
            bottomState.Height = BottomHeight(savedBottom.Height);
            bottomState.Collapsed = bottomSlot.Folded;
            bottomState.Y = layout.WindowHeight - Margin - bottomState.Height;
        }

        // A panel the app saved undocked (a drag clears the chain link and stores PosX/PosY) keeps
        // that place and its folded bit instead of taking a column, so moving it is not lost.
        foreach (var slot in dock.Floating)
        {
            if (Take(slot.DialogClass) is not { } state) continue;
            state.DialogClass = slot.DialogClass;
            state.Anchor = PanelAnchor.Free;
            state.Collapsed = slot.Folded;
            // The size the file stores (SloppyAnimatsu writes it for every panel), else the catalogue's.
            var bars = slot.Placement.SizeBarPositions;
            double savedWidth = bars.Count > 1 && bars[1] > 0 ? bars[1] : state.Spec.Width;
            double savedHeight = slot.HasStoredHeight && slot.Height > 0 ? slot.Height + HeaderHeight : state.Spec.Height;
            state.Width = Math.Clamp(savedWidth, MinimumWidth, Math.Max(MinimumWidth, layout.WindowWidth - 2 * Margin));
            state.Height = Math.Clamp(savedHeight, MinimumHeight, Math.Max(MinimumHeight, layout.WindowHeight - 2 * Margin));
            layout.SetHome(state,
                DockLayout.FloatLeft(slot, layout.WindowWidth, (int)Math.Round(state.Width)),
                DockLayout.FloatTop(slot, layout.WindowHeight));
        }

        // The panels this context's saved layout does not name get a column of their own on their
        // fallback side, starting beside the columns above.
        double leftX = edge, rightX = rightEdge, leftY = Margin, rightY = Margin;
        FloatingPanelState? lastLeft = null, lastRight = null;
        var positioned = new HashSet<string>(placed, StringComparer.Ordinal);
        var presetAbove = new Dictionary<string, string>(StringComparer.Ordinal);
        var presetBelow = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in LayoutPresets.Columns(LayoutPreset.Default, dock.Context))
            for (int i = 1; i < column.Keys.Count; i++)
            {
                presetAbove[column.Keys[i]] = column.Keys[i - 1];
                presetBelow[column.Keys[i - 1]] = column.Keys[i];
            }
        foreach (var spec in specs)
        {
            var state = layout.Panel(spec.Key);
            if (state == null || placed.Contains(state.Key)) continue;
            double panelHeight = Math.Min(spec.Height, layout.WindowHeight - 2 * Margin);
            // A chain head the file has no entry for (PAINTER or RIG ATELIER in a profile written
            // before SloppyAnimatsu had them) takes the top of the chain the default arrangement puts it
            // over, and that chain hangs under it.
            if (spec.Fallback != PanelAnchor.Bottom && !presetAbove.ContainsKey(spec.Key) &&
                presetBelow.TryGetValue(spec.Key, out string? belowKey) &&
                layout.Panel(belowKey) is { } head && positioned.Contains(belowKey) &&
                head.Parent == null && head.Anchor is PanelAnchor.Left or PanelAnchor.Right)
            {
                var chain = layout.Below(head).ToList();
                state.Anchor = head.Anchor;
                state.FromRight = head.FromRight;
                state.Edge = head.Edge;
                state.Width = head.Width;
                state.Height = Math.Max(MinimumHeight, panelHeight);
                state.Y = head.Y;
                state.TopFraction = head.TopFraction;
                Hang(head, state);
                head.Y += state.Height;
                foreach (var member in chain) member.Y += state.Height;
                positioned.Add(spec.Key);
                continue;
            }
            // A panel the file has no entry for cannot be saved, so it follows the panel the default
            // arrangement puts it under, wherever that one is now.
            if (spec.Fallback != PanelAnchor.Bottom && presetAbove.TryGetValue(spec.Key, out string? aboveKey) &&
                layout.Panel(aboveKey) is { } neighbour && positioned.Contains(aboveKey) &&
                neighbour.Anchor != PanelAnchor.Bottom && !layout._panels.Any(p => p.Parent == neighbour))
            {
                state.Anchor = neighbour.Anchor;
                state.Width = neighbour.Width;
                // It takes the room left above the bottom panel rather than covering it.
                state.Height = Math.Clamp(sideLimit - neighbour.Bottom, MinimumHeight, Math.Max(MinimumHeight, panelHeight));
                state.Y = neighbour.Bottom;
                Hang(state, neighbour);
                positioned.Add(spec.Key);
                if (neighbour == lastLeft) lastLeft = state;
                if (neighbour == lastRight) lastRight = state;
                continue;
            }
            positioned.Add(spec.Key);
            // The head of another default column (PAINTER after the RIG ATELIER chain) starts a
            // column of its own beside the last one, as the default arrangement has it, instead of
            // stacking a six-panel column past the bottom of the window.
            if (presetBelow.ContainsKey(spec.Key) && !presetAbove.ContainsKey(spec.Key))
            {
                if (spec.Fallback == PanelAnchor.Left && lastLeft != null)
                {
                    leftX += layout._panels.Where(p => p == lastLeft || layout.Below(p).Contains(lastLeft))
                        .Max(p => p.Width) + Gap;
                    leftY = Margin;
                    lastLeft = null;
                }
                else if (spec.Fallback == PanelAnchor.Right && lastRight != null)
                {
                    rightX -= layout._panels.Where(p => p == lastRight || layout.Below(p).Contains(lastRight))
                        .Max(p => p.Width) + Gap;
                    rightY = Margin;
                    lastRight = null;
                }
            }
            switch (spec.Fallback)
            {
                case PanelAnchor.Left:
                    state.Anchor = PanelAnchor.Left;
                    state.Edge = leftX;
                    state.Width = spec.Width;
                    state.Height = Math.Clamp(sideLimit - leftY, MinimumHeight, Math.Max(MinimumHeight, panelHeight));
                    state.Y = leftY;
                    state.TopFraction = leftY / layout.WindowHeight;
                    state.Parent = lastLeft;
                    lastLeft = state;
                    leftY += state.Height;
                    break;
                case PanelAnchor.Bottom:
                    state.Anchor = PanelAnchor.Bottom;
                    state.Edge = Margin;
                    state.Width = Math.Max(MinimumWidth, layout.WindowWidth - 2 * Margin);
                    state.Height = BottomHeight(panelHeight);
                    state.Y = layout.WindowHeight - Margin - state.Height;
                    break;
                default:
                    state.Anchor = PanelAnchor.Right;
                    state.FromRight = true;
                    state.Edge = layout.WindowWidth - rightX;
                    state.Width = spec.Width;
                    state.Height = Math.Clamp(sideLimit - rightY, MinimumHeight, Math.Max(MinimumHeight, panelHeight));
                    state.Y = rightY;
                    state.TopFraction = rightY / layout.WindowHeight;
                    state.Parent = lastRight;
                    lastRight = state;
                    rightY += state.Height;
                    break;
            }
        }

        // A floating panel another one names in its DockDown link hangs below that one.
        foreach (var slot in dock.Floating)
        {
            if (slot.Down is not { } down) continue;
            var parent = layout._panels.FirstOrDefault(p => p.DialogClass == slot.DialogClass);
            var child = layout._panels.FirstOrDefault(p => p.DialogClass == down);
            if (parent == null || child == null || child == parent || child.Anchor != PanelAnchor.Free ||
                layout.Below(child).Contains(parent))
                continue;
            Hang(child, parent);
        }

        // A side column the catalogue panels made taller than the room above the bottom panel gives
        // the overflow back from those panels (never from the ones the file sized), down to their minimum.
        foreach (var root in layout._panels.ToList())
        {
            if (root.Parent != null || (root.Anchor != PanelAnchor.Left && root.Anchor != PanelAnchor.Right)) continue;
            var chain = new List<FloatingPanelState> { root };
            chain.AddRange(layout.Below(root));
            double overflow = chain.Max(p => p.Bottom) - sideLimit;
            var flexible = chain.Where(p => !placed.Contains(p.Key)).ToList();
            double spare = flexible.Sum(p => p.Height - MinimumHeight);
            if (overflow <= 0 || spare <= 0) continue;
            double share = Math.Min(1, overflow / spare);
            foreach (var state in flexible)
                state.Height = Math.Floor(state.Height - (state.Height - MinimumHeight) * share);
            layout.Stack(root);
        }

        layout.Finish();
        return layout;
    }

    /// <summary>The stack order of a fresh layout, and its first arrangement.</summary>
    private void Finish()
    {
        // The timeline is the backmost panel, so the side panels' saved heights can overlap its strip
        // without hiding them; pressing it brings it forward like any other.
        int order = 0;
        foreach (var state in _panels)
            state.Order = state.Anchor == PanelAnchor.Bottom ? 0 : ++order;
        _top = Math.Max(1, order);
        Arrange(WindowWidth, WindowHeight);
    }

    /// <summary>
    /// A built-in arrangement: the default columns for
    /// the context, or a variation of them. It is what a profile without a saved layout starts with
    /// and what the layout button restores. <paramref name="dock"/>, when given, only tells which
    /// dialog class of the file each panel is saved under.
    /// </summary>
    public static FloatingLayout FromPreset(LayoutPreset preset, string context, IReadOnlyList<PanelSpec> specs,
        int width, int height, DockLayout? dock = null)
    {
        var layout = new FloatingLayout(context,
            Math.Max(MinimumWidth + 2 * Margin, width),
            Math.Max(MinimumHeight + 2 * Margin, height));
        for (int i = 0; i < specs.Count; i++)
        {
            var state = new FloatingPanelState(specs[i], i);
            state.DialogClass = dock == null
                ? null
                : specs[i].Dialogs.FirstOrDefault(name => dock.Panel(name) != null);
            layout._panels.Add(state);
        }

        var bottom = layout._panels.FirstOrDefault(p => p.Spec.Fallback == PanelAnchor.Bottom);
        double sideLimit = layout.WindowHeight - Margin;
        if (bottom != null)
        {
            bottom.Anchor = PanelAnchor.Bottom;
            bottom.Edge = Margin;
            bottom.Width = BottomWidth(layout.WindowWidth);
            // A timeline the file already has across the bottom keeps its saved height.
            double bottomHeight = bottom.Spec.Height;
            if (dock?.Bottom is { } savedBottom && savedBottom.DialogClass == bottom.DialogClass)
                foreach (var rect in dock.Resolve(layout.WindowWidth, layout.WindowHeight))
                    if (rect.DialogClass == savedBottom.DialogClass) bottomHeight = rect.Height;
            bottom.Height = Math.Clamp(bottomHeight, MinimumHeight,
                Math.Max(MinimumHeight, (int)(layout.WindowHeight * 0.45)));
            bottom.Y = layout.WindowHeight - Margin - bottom.Height;
            sideLimit = Math.Max(MinimumHeight, bottom.Y - Gap);
        }

        var columns = LayoutPresets.Columns(preset, context);
        // A panel the table does not name joins the last column of its side, so nothing is left out.
        foreach (var state in layout._panels)
        {
            if (state == bottom || columns.Any(c => c.Keys.Contains(state.Key))) continue;
            bool right = state.Spec.Fallback != PanelAnchor.Left;
            int index = columns.FindLastIndex(c => c.Right == right);
            if (index < 0) columns.Add(new PresetColumn(right, 0.1, [state.Key]));
            else columns[index] = columns[index] with { Keys = [.. columns[index].Keys, state.Key] };
        }

        double leftEdge = Margin, rightEdge = Margin;
        foreach (var column in columns)
        {
            var members = column.Keys.Select(layout.Panel).Where(p => p != null && p != bottom).Select(p => p!).ToList();
            if (members.Count == 0) continue;
            bool folded = preset == LayoutPreset.Folded;
            // Only the panels the file can save set the width, so the column reads back as written.
            var sized = dock != null && members.Any(p => p.DialogClass != null)
                ? members.Where(p => p.DialogClass != null).ToList()
                : members;
            double columnWidth = Math.Clamp(sized.Max(p => p.Spec.Width), MinimumWidth,
                Math.Max(MinimumWidth, layout.WindowWidth - 2 * Margin));
            double top = Math.Max(Margin, Math.Round(column.Top * layout.WindowHeight));
            double total = members.Sum(p => p.Spec.Height);
            double available = sideLimit - top;
            double scale = available > 0 && total > available && available >= members.Count * MinimumHeight
                ? available / total
                : 1;
            double y = top;
            FloatingPanelState? above = null;
            foreach (var state in members)
            {
                state.Anchor = column.Right ? PanelAnchor.Right : PanelAnchor.Left;
                state.FromRight = column.Right;
                state.Edge = column.Right ? rightEdge : leftEdge;
                state.Width = columnWidth;
                state.Height = Math.Max(MinimumHeight, Math.Round(state.Spec.Height * scale));
                state.Collapsed = folded;
                state.Y = y;
                state.TopFraction = y / layout.WindowHeight;
                state.Parent = above;
                above = state;
                y += state.OuterHeight;
            }
            if (column.Right) rightEdge += columnWidth + Gap;
            else leftEdge += columnWidth + Gap;
        }

        layout.Finish();
        return layout;
    }

    /// <summary>
    /// Writes the arrangement into the saved layout the way the app stores its own: a chain pinned
    /// to a side as a docked column (column number, tops and <c>DockDown</c> links), a chain the user
    /// dragged as floating places with the same links, the bottom panel, the heights and the folded
    /// bits. A panel the file has no entry for is skipped. <paramref name="sizes"/> false leaves the
    /// saved heights alone (a move does not resize, and a small window may have squeezed a column).
    /// Returns whether an entry changed.
    /// </summary>
    public bool WriteTo(DockLayout dock, bool sizes = true)
    {
        bool changed = false;
        // A panel this context's file has no entry for yet (BLENDSHAPES in a profile that never
        // showed it there) gets one, so its place is saved like any other panel's.
        var added = new HashSet<FloatingPanelState>();
        foreach (var state in _panels)
        {
            if (state.DialogClass != null || state.Spec.Dialogs.Count != 1 || !dock.Ensure(state.Spec.Dialogs[0]))
                continue;
            state.DialogClass = state.Spec.Dialogs[0];
            added.Add(state);
            changed = true;
        }
        var rightEdges = _panels.Where(p => p.Parent == null && p.Anchor == PanelAnchor.Right)
            .Select(p => p.Edge).Distinct().OrderBy(e => e).ToList();
        // The.akt file has one left column (PosX 0); a chain docked further in on the left
        // (MATERIAL's column beside Bank/Scene/Properties) is written as floating at its place,
        // or it would read back merged into column 1.
        var leftEdges = _panels.Where(p => p.Parent == null && p.Anchor == PanelAnchor.Left)
            .Select(p => p.Edge).Distinct().OrderBy(e => e).ToList();
        foreach (var root in _panels.Where(p => p.Parent == null))
        {
            if (root.Anchor == PanelAnchor.Bottom)
            {
                // A timeline the file already has across the bottom keeps the numbers.
                if (root.DialogClass != null && dock.Bottom?.DialogClass != root.DialogClass)
                    // The saved top is the panel's height from the window's bottom edge: that is how
                    // it is read back, the margin under it being this window's own.
                    changed |= dock.SetBottom(root.DialogClass, (int)Math.Round(WindowHeight - root.Height), WindowHeight);
                continue;
            }
            var members = new List<FloatingPanelState>();
            for (var state = root; state != null; state = _panels.FirstOrDefault(p => p.Parent == state))
                if (state.DialogClass != null) members.Add(state);
            if (members.Count == 0) continue;
            // The head's home, not where a small window squeezed it to, so the place survives.
            double homeTop = Math.Round(root.TopFraction * WindowHeight);
            double shift = homeTop - root.Y;
            if (root.Anchor == PanelAnchor.Right || (root.Anchor == PanelAnchor.Left && leftEdges.IndexOf(root.Edge) == 0))
            {
                var chain = members.Select(m => (m.DialogClass!, (int)Math.Round(m.Y + shift))).ToList();
                int distance = root.Anchor == PanelAnchor.Right ? rightEdges.IndexOf(root.Edge) + 1 : 1;
                changed |= dock.SetDocked(chain, root.Anchor == PanelAnchor.Right ? DockAnchor.Right : DockAnchor.Left,
                    distance, WindowHeight);
                continue;
            }
            foreach (var member in members)
            {
                double homeX = member.FromRight ? WindowWidth - member.Edge - member.Width : member.Edge;
                changed |= dock.SetFloating(member.DialogClass!, homeX, member.Y + shift, member.Width,
                    WindowWidth, WindowHeight);
            }
            for (int i = 0; i + 1 < members.Count; i++)
                changed |= dock.SetDown(members[i].DialogClass!, members[i + 1].DialogClass!);
        }
        foreach (var state in _panels)
        {
            if (state.DialogClass == null) continue;
            if (sizes || added.Contains(state))
            {
                changed |= dock.SetHeight(state.DialogClass, (int)Math.Round(state.Height - HeaderHeight));
                if (state.Anchor != PanelAnchor.Bottom)
                    changed |= dock.SetWidth(state.DialogClass, (int)Math.Round(state.Width));
            }
            changed |= dock.SetFolded(state.DialogClass, state.Collapsed);
        }
        return changed;
    }

    /// <summary>How close a dragged panel's top has to come to another panel's bottom edge to hang from it.</summary>
    public const int SnapDistance = 12;

    /// <summary>The panels hanging below <paramref name="state"/>, nearest first.</summary>
    public IReadOnlyList<FloatingPanelState> Below(FloatingPanelState state)
    {
        var found = new List<FloatingPanelState>();
        void Walk(FloatingPanelState parent)
        {
            foreach (var panel in _panels)
            {
                if (panel.Parent != parent || found.Contains(panel)) continue;
                found.Add(panel);
                Walk(panel);
            }
        }
        Walk(state);
        return found;
    }

    private void SetHome(FloatingPanelState state, double x, double y)
    {
        state.X = x;
        state.Y = y;
        state.FromRight = x + state.Width / 2 > WindowWidth / 2.0;
        state.Edge = state.FromRight ? WindowWidth - x - state.Width : x;
        state.TopFraction = y / WindowHeight;
    }

    private static void Hang(FloatingPanelState state, FloatingPanelState parent)
    {
        state.Parent = parent;
        state.FromRight = parent.FromRight;
        state.Edge = parent.Edge;
    }

    /// <summary>
    /// The panel whose bottom edge a panel placed at (<paramref name="x"/>, <paramref name="y"/>)
    /// would rest against: within <paramref name="distance"/> vertically, overlapping it by a third
    /// of the narrower width, with nothing hanging from it yet, and not one of the panel's own chain.
    /// </summary>
    private FloatingPanelState? SnapTarget(FloatingPanelState state, double x, double y, double distance)
    {
        var own = Below(state);
        FloatingPanelState? best = null;
        double bestGap = double.MaxValue;
        foreach (var other in _panels)
        {
            if (other == state || other.Anchor == PanelAnchor.Bottom || own.Contains(other)) continue;
            if (_panels.Any(p => p.Parent == other && p != state)) continue;
            double gap = Math.Abs(y - other.Bottom);
            if (gap > distance || gap >= bestGap) continue;
            double overlap = Math.Min(x + state.Width, other.Right) - Math.Max(x, other.X);
            if (overlap < Math.Min(state.Width, other.Width) / 3) continue;
            best = other;
            bestGap = gap;
        }
        return best;
    }

    /// <summary>
    /// Re-places the pinned panels for a new window size: left and right panels keep their distance
    /// from their edge, the bottom panel stays across the bottom, and a panel the user dragged stays
    /// where it was put (clamped so its title bar is reachable). A dragged panel is not rescaled with
    /// The window, as the undocked dialogs are not.
    /// </summary>
    public void Arrange(int width, int height)
    {
        WindowWidth = Math.Max(1, width);
        WindowHeight = Math.Max(1, height);
        foreach (var state in _panels)
        {
            if (state.Anchor == PanelAnchor.Bottom)
            {
                state.Width = BottomWidth(WindowWidth);
                state.X = BottomX(WindowWidth);
                state.Y = Math.Max(0, WindowHeight - Margin - state.OuterHeight);
                continue;
            }
            // Never past the left or right window edge: a panel wider than the window starts at 0.
            double x = state.FromRight ? WindowWidth - state.Edge - state.Width : state.Edge;
            state.X = Math.Clamp(x, 0, Math.Max(0, WindowWidth - state.Width));
        }

        // A chain's head sits at its fraction of the window height, never above the window; the
        // panels hanging from it follow, and may run below the window when it is too short.
        foreach (var root in _panels)
        {
            if (root.Parent != null || root.Anchor == PanelAnchor.Bottom) continue;
            root.Y = Math.Clamp(Math.Round(root.TopFraction * WindowHeight), 0, Math.Max(0, WindowHeight - HeaderHeight));
            Stack(root);
        }

        // A shorter window would let a saved side column run behind the bottom panel; the column is
        // slid up as a whole, the way the dock keeps it above the timeline.
        double bottomTop = WindowHeight - Margin;
        foreach (var bottom in _panels)
            if (bottom.Anchor == PanelAnchor.Bottom) bottomTop = Math.Min(bottomTop, bottom.Y);
        double sideLimit = Math.Max(HeaderHeight, bottomTop - Gap);
        foreach (PanelAnchor side in new[] { PanelAnchor.Left, PanelAnchor.Right })
        {
            foreach (var root in _panels)
            {
                if (root.Anchor != side || root.Parent != null) continue;
                double lowest = root.Bottom;
                foreach (var below in Below(root)) lowest = Math.Max(lowest, below.Bottom);
                if (lowest <= sideLimit) continue;
                root.Y = Math.Max(Math.Min(Margin, root.Y), root.Y - (lowest - sideLimit));
                Stack(root);
            }
        }
    }

    /// <summary>Puts every panel hanging from <paramref name="root"/> against the bottom of the one above it.</summary>
    private void Stack(FloatingPanelState root)
    {
        foreach (var below in Below(root))
            below.Y = below.Parent!.Bottom;
    }

    /// <summary>
    /// Moves a panel by its title bar. It leaves the panel it was hanging from and takes the panels
    /// hanging from it along; dropped with its top against another panel's bottom edge it hangs from
    /// that one (the <c>DockDown</c>). It cannot leave the window by the top or the sides.
    /// </summary>
    public bool Move(string key, double x, double y)
    {
        if (Panel(key) is not { } state) return false;
        var own = Below(state);
        state.Anchor = PanelAnchor.Free;
        state.Parent = null;
        x = Math.Clamp(x, 0, Math.Max(0, WindowWidth - state.Width));
        y = Math.Clamp(y, 0, Math.Max(0, WindowHeight - HeaderHeight));
        // The bottom panel (the timeline) dropped with its foot at the window's bottom edge goes
        // back across the bottom.
        if (state.Spec.Fallback == PanelAnchor.Bottom && own.Count == 0 &&
            y + state.OuterHeight >= WindowHeight - Margin - SnapDistance)
        {
            state.Anchor = PanelAnchor.Bottom;
            state.FromRight = false;
            state.Edge = Margin;
            Arrange(WindowWidth, WindowHeight);
            return true;
        }
        var target = SnapTarget(state, x, y, SnapDistance);
        // Near the left or right window edge the panel takes the docked columns' distance from it
        // and becomes a column of that side.
        // The bottom panel is as wide as the window, so it would always touch a side: it only floats or goes back to the bottom.
        bool sides = target == null && state.Spec.Fallback != PanelAnchor.Bottom;
        bool snapLeft = sides && x <= Margin + SnapDistance;
        bool snapRight = sides && !snapLeft && x + state.Width >= WindowWidth - Margin - SnapDistance;
        if (snapLeft) x = Math.Min(Margin, Math.Max(0, WindowWidth - state.Width));
        if (snapRight) x = Math.Max(0, WindowWidth - Margin - state.Width);
        SetHome(state, x, y);
        if (snapLeft || snapRight)
        {
            state.Anchor = snapLeft ? PanelAnchor.Left : PanelAnchor.Right;
            state.FromRight = snapRight;
            state.Edge = snapLeft ? x : WindowWidth - x - state.Width;
        }
        if (target != null)
        {
            Hang(state, target);
            // Hanging from a docked column makes the panel part of that column.
            state.Anchor = target.Anchor;
        }
        foreach (var below in own)
        {
            below.Anchor = state.Anchor;
            below.FromRight = state.FromRight;
            below.Edge = state.Edge;
        }
        Arrange(WindowWidth, WindowHeight);
        return true;
    }

    /// <summary>
    /// Resizes an open panel by its grip; a folded or too-small one gets the minimum. A width or
    /// height within <see cref="SnapDistance"/> of another panel's in the same chain (the panels
    /// hanging from one another) snaps to it, so a column is easy to make even.
    /// </summary>
    public bool Resize(string key, double width, double height)
    {
        if (Panel(key) is not { } state) return false;
        width = Math.Clamp(width, MinimumWidth, Math.Max(MinimumWidth, WindowWidth - 2 * Margin));
        height = Math.Clamp(height, MinimumHeight, Math.Max(MinimumHeight, WindowHeight - Margin));
        var root = state;
        while (root.Parent is { } up) root = up;
        double bestW = SnapDistance + 1, bestH = SnapDistance + 1;
        double snappedW = width, snappedH = height;
        foreach (var other in Below(root).Prepend(root))
        {
            if (other == state) continue;
            double dw = Math.Abs(other.Width - width);
            if (dw <= SnapDistance && dw < bestW) { bestW = dw; snappedW = other.Width; }
            if (other.Collapsed) continue;
            double dh = Math.Abs(other.Height - height);
            if (dh <= SnapDistance && dh < bestH) { bestH = dh; snappedH = other.Height; }
        }
        state.Width = snappedW;
        state.Height = snappedH;
        Arrange(WindowWidth, WindowHeight);
        return true;
    }

    /// <summary>The next front-most stacking order, for a window outside the panel list (curveboard, spinner).</summary>
    public int NextOrder() => ++_top;

    /// <summary>
    /// Folds or unfolds a panel with its arrow. Folding leaves the saved height alone, so unfolding
    /// restores the panel as it was; the returned value is the new folded state.
    /// </summary>
    public bool Toggle(string key)
    {
        if (Panel(key) is not { } state) return false;
        state.Collapsed = !state.Collapsed;
        Arrange(WindowWidth, WindowHeight);
        return state.Collapsed;
    }

    /// <summary>Puts a panel above the others (a press anywhere in it brings it to the front).</summary>
    public void BringToFront(string key)
    {
        if (Panel(key) is not { } state) return;
        state.Order = ++_top;
    }

}

/// <summary>The built-in arrangements the layout button offers.</summary>
public enum LayoutPreset
{
    /// <summary>The default columns for the context.</summary>
    Default,

    /// <summary>The default columns with every side panel folded to its title bar.</summary>
    Folded,

    /// <summary>Every side panel in one column against the right edge.</summary>
    RightColumn,
}

/// <summary>One column of a preset: its side, its top as a fraction of the window height, its panels.</summary>
public sealed record PresetColumn(bool Right, double Top, IReadOnlyList<string> Keys);

/// <summary>
/// The columns of the built-in arrangements.
/// Right columns are listed from the screen edge inwards.
/// Painter and Rig Atelier have no SloppyAnimatsu panel; the columns they head start where
/// they do, with the next panel in their place.
/// </summary>
public static class LayoutPresets
{
    public static string Name(LayoutPreset preset) => preset switch
    {
        LayoutPreset.Folded => "Folded",
        LayoutPreset.RightColumn => "Right column",
        _ => "Classic",
    };

    public static List<PresetColumn> Columns(LayoutPreset preset, string context)
    {
        bool rigging = string.Equals(context, "Rigging", StringComparison.OrdinalIgnoreCase);
        string[] left = [PanelKeys.Bank, PanelKeys.Scene, PanelKeys.Properties];
        // SloppyAnimatsu's Material window (saved under its own class, CMyAkMaterialDialog) is a column
        // of its own beside the left one: hung under Properties it would squeeze that chain.
        if (preset == LayoutPreset.RightColumn)
            return
            [
                new(true, 0.105, rigging
                    ? [.. left, PanelKeys.Material, PanelKeys.RigWorkshop, PanelKeys.Transform, PanelKeys.Picker, PanelKeys.Painter, PanelKeys.SkinWorkshop, PanelKeys.Blendshapes]
                    : [.. left, PanelKeys.Material, PanelKeys.Stacker, PanelKeys.Transform, PanelKeys.Mixer, PanelKeys.Picker, PanelKeys.Cyclemaker]),
            ];
        return rigging
            ?
            [
                new(false, 0.123, left),
                new(false, 0.123, [PanelKeys.Material]),
                new(true, 0.105, [PanelKeys.RigWorkshop, PanelKeys.Transform, PanelKeys.Picker]),
                new(true, 0.105, [PanelKeys.Painter, PanelKeys.SkinWorkshop, PanelKeys.Blendshapes]),
            ]
            :
            [
                new(false, 0.064, left),
                new(false, 0.064, [PanelKeys.Material]),
                new(true, 0.116, [PanelKeys.Stacker, PanelKeys.Transform]),
                new(true, 0.116, [PanelKeys.Mixer, PanelKeys.Picker, PanelKeys.Cyclemaker]),
            ];
    }
}
