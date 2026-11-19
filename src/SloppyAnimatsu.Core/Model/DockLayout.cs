using System.Globalization;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Which edge of the window a dock column is laid out from.</summary>
public enum DockAnchor
{
    /// <summary>The column starts at the left edge and grows to the right.</summary>
    Left,

    /// <summary>The column is stacked from the right edge to the left.</summary>
    Right,

    /// <summary>The panel spans the window at the bottom.</summary>
    Bottom,

    /// <summary>The panel is not docked: it keeps a saved floating position.</summary>
    Free,
}

/// <summary>
/// One panel of an interface context's saved dock layout, read from a <c>CDialogPosition</c> entry
/// of <c>preferences.akt</c>
/// its column, its saved height and the panel docked below it.
/// </summary>
public sealed class DockSlot
{
    internal DockSlot(DialogPlacement placement, DockAnchor anchor, int distance)
    {
        Placement = placement;
        Anchor = anchor;
        Distance = distance;
    }

    /// <summary>The entry this panel was read from; writes go through it.</summary>
    public DialogPlacement Placement { get; }

    /// <summary>The dialog class without the <c>class </c> prefix (<c>CTransformDialog</c>, ...).</summary>
    public string DialogClass => Placement.DialogClass;

    public DockAnchor Anchor { get; }

    /// <summary>The column's distance from its edge: 1 is the edge column, 2 the next one in.</summary>
    public int Distance { get; }

    /// <summary>The top of the column's first panel, as a fraction of the window height.</summary>
    public float Top => Placement.Y;

    /// <summary>The saved height in pixels, or 0 when the file stores none.</summary>
    public int Height =>
        Placement.SizeBarPositions.Count > 0 ? Placement.SizeBarPositions[0] : 0;

    /// <summary>Whether the file stores this panel's height.</summary>
    public bool HasStoredHeight => Placement.SizeBarPositions.Count > 0;

    /// <summary>
    /// Whether <c>Flags</c> bit 0 is set: the app folds the panel to its title bar when it restores
    /// such an entry.
    /// </summary>
    public bool Folded => (Placement.Flags & 1u) != 0;

    /// <summary>Whether the panel is floating instead of docked (see <see cref="DockLayout.Floating"/>).</summary>
    public bool IsFloating => Anchor == DockAnchor.Free;

    /// <summary>The class of the panel docked below this one, or null.</summary>
    public string? Down =>
        Placement.DockDown is { } down && down.StartsWith("class ", StringComparison.Ordinal)
            ? down[6..]
            : Placement.DockDown;

    public override string ToString() =>
        $"{DialogClass}: {Anchor.ToString().ToLowerInvariant()} {Distance}, top {Top:0.###}, " +
        $"height {(Height > 0 ? Height.ToString(CultureInfo.InvariantCulture) : "-")}";
}

/// <summary>One vertical column of a context's dock layout, top panel first.</summary>
public sealed class DockColumn
{
    internal DockColumn(DockAnchor anchor, int distance, IReadOnlyList<DockSlot> panels)
    {
        Anchor = anchor;
        Distance = distance;
        Panels = panels;
    }

    public DockAnchor Anchor { get; }

    /// <summary>1 is the column at its edge, 2 the next one in.</summary>
    public int Distance { get; }

    /// <summary>
    /// The column's saved width in pixels, or 0 when the file stores none: the largest second
    /// <c>SizeBarPositions</c> value of the column's entries (the app stores one width per splitter
    /// bar). Read live, so a <see cref="DockLayout.SetWidth"/> shows up without rereading the file.
    /// </summary>
    public int Width
    {
        get
        {
            int width = 0;
            foreach (var panel in Panels)
            {
                if (panel.Placement.SizeBarPositions.Count > 1)
                    width = Math.Max(width, panel.Placement.SizeBarPositions[1]);
            }
            return width;
        }
    }

    /// <summary>The panels, top to bottom (the <c>DockDown</c> chain).</summary>
    public IReadOnlyList<DockSlot> Panels { get; }

    public override string ToString() =>
        $"{Anchor.ToString().ToLowerInvariant()} {Distance} width " +
        $"{(Width > 0 ? Width.ToString(CultureInfo.InvariantCulture) : "-")}: " +
        string.Join(" -> ", Panels.Select(p => p.DialogClass));
}

/// <summary>A resolved panel rectangle; <see cref="Height"/> includes the panel's title bar.</summary>
public readonly record struct DockRect(string DialogClass, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int ContentHeight => Height - DockLayout.HeaderHeight;

    public override string ToString() => $"{DialogClass}: {X},{Y} {Width}x{Height}";
}

/// <summary>
/// The dock layout the app saves per interface context in <c>preferences.akt</c>
/// the columns at
/// the left and right edges, the panels each holds in its <c>DockDown</c> chain, the height of each
/// panel and the bottom panel. <see cref="Resolve"/> turns it into rectangles for a given window
/// size and <see cref="SetHeight"/>/<see cref="SetTop"/> write a new geometry back.
/// </summary>
public sealed class DockLayout
{
    /// <summary>The title bar above every panel, in pixels.</summary>
    public const int HeaderHeight = 22;

    /// <summary>The height of a panel the file does not size.</summary>
    public const int DefaultHeight = 200;

    /// <summary>The width of a column the file does not size.</summary>
    public const int DefaultWidth = 240;

    /// <summary>The least height a panel is given, so a bad file cannot collapse one.</summary>
    public const int MinimumContentHeight = 40;

    /// <summary>The least width a column is given (a splitter drag cannot close a column).</summary>
    public const int MinimumColumnWidth = 120;

    /// <summary>
    /// The pixel width the floating-dialog fraction counts in
    /// it writes
    /// <c>PosX = (left - Origin) / Unit</c> for a dialog on the left half and
    /// <c>PosX = (Origin + right + 1 - windowWidth) / Unit - 1</c> on the right half, against the
    /// window's <b>client rect</b> (1904x1001 at 1920x1040). The write and the restore are exact
    /// inverses and the unit does <b>not</b> scale with the window: the same <c>PosX 3.0</c> kept a
    /// client left of 551 px at 1920x1040, 1280x800 and 1600x700 while the top followed
    /// <c>PosY * clientHeight</c>.
    /// </summary>
    public const float FloatingUnit = 180f;

    /// <summary>The offset the floating-dialog fraction counts from (see <see cref="FloatingUnit"/>).</summary>
    public const float FloatingOrigin = 10f;

    private readonly Dictionary<string, DockSlot> _panels = new(StringComparer.Ordinal);
    private readonly List<DockSlot> _floating = [];

    private DockLayout(string context)
    {
        Context = context;
    }

    /// <summary>The interface context the entries were read from (<c>Rigging</c>, <c>Animation</c>).</summary>
    public string Context { get; }

    /// <summary>The side columns, left to right.</summary>
    public IReadOnlyList<DockColumn> Columns { get; private set; } = Array.Empty<DockColumn>();

    /// <summary>The panel across the bottom (the Animation timeline), or null.</summary>
    public DockSlot? Bottom { get; private set; }

    /// <summary>
    /// The panels that are not docked, with the floating position the app saved for them: a panel
    /// the user dragged out of its column (which clears the column's <c>DockDown</c> link) or a
    /// dialog that never had a column. The place is <see cref="FloatLeft"/>/<see cref="FloatTop"/>.
    /// </summary>
    public IReadOnlyList<DockSlot> Floating => _floating;

    public bool IsEmpty => Columns.Count == 0 && Bottom == null;

    /// <summary>The panel of <paramref name="dialogClass"/>, or null when this context has none.</summary>
    public DockSlot? Panel(string dialogClass) =>
        _panels.TryGetValue(dialogClass, out var panel) ? panel : null;

    private Preferences? _preferences;

    /// <summary>
    /// Makes sure this context has an entry for <paramref name="dialogClass"/>, adding a floating
    /// one to the file when it has none (a profile the app saved before the panel was ever shown in
    /// this context). False when the file cannot take one.
    /// </summary>
    public bool Ensure(string dialogClass)
    {
        if (_panels.ContainsKey(dialogClass)) return true;
        if (_preferences?.EnsureDialogPosition(Context, dialogClass) is not { } placement) return false;
        var slot = new DockSlot(placement, DockAnchor.Free, 0);
        _panels[dialogClass] = slot;
        _floating.Add(slot);
        return true;
    }

    /// <summary>
    /// Reads the saved layout of <paramref name="context"/>. Entries of other contexts and floating
    /// dialogs are ignored; a panel without a height gets a default.
    /// </summary>
    public static DockLayout For(Preferences preferences, string context)
    {
        var layout = new DockLayout(context) { _preferences = preferences };
        var entries = preferences.DialogPlacements
            .Where(p => string.Equals(p.Context, context, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var chained = entries
            .Select(p => ClassName(p.DockDown))
            .Where(down => down != null)
            .ToHashSet(StringComparer.Ordinal);

        // A panel hanging below another one belongs wherever the head of its chain is: in the head's
        // column, or floating with it when the head was dragged out.
        var parentOf = new Dictionary<string, DialogPlacement>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (ClassName(entry.DockDown) is { } down) parentOf.TryAdd(down, entry);
        var none = new HashSet<string?>();

        var found = new List<DockSlot>();
        DockSlot? bottom = null;
        foreach (var placement in entries)
        {
            var panel = Classify(placement, chained);
            if (panel != null && parentOf.ContainsKey(placement.DialogClass))
            {
                var head = placement;
                var seen = new HashSet<string>(StringComparer.Ordinal) { head.DialogClass };
                while (parentOf.TryGetValue(head.DialogClass, out var above) && seen.Add(above.DialogClass))
                    head = above;
                if (!ReferenceEquals(head, placement) && Classify(head, none) is { Anchor: DockAnchor.Free })
                    panel = new DockSlot(placement, DockAnchor.Free, 0);
            }
            if (panel == null) continue;
            layout._panels[panel.DialogClass] = panel;
            if (panel.Anchor == DockAnchor.Bottom)
            {
                bottom ??= panel;
                continue;
            }
            if (panel.Anchor == DockAnchor.Free)
            {
                layout._floating.Add(panel);
                continue;
            }
            found.Add(panel);
        }

        var columns = new List<DockColumn>();
        foreach (var group in found.GroupBy(p => (p.Anchor, p.Distance)))
        {
            var ordered = Chain(group.ToList());
            columns.Add(new DockColumn(group.Key.Anchor, group.Key.Distance, ordered));
        }
        // Left columns from the left edge, right columns inwards from the right edge.
        layout.Columns = columns
            .Where(c => c.Anchor == DockAnchor.Left).OrderBy(c => c.Distance)
            .Concat(columns.Where(c => c.Anchor == DockAnchor.Right).OrderByDescending(c => c.Distance))
            .ToList();
        layout.Bottom = bottom;
        return layout;
    }

    /// <summary>A layout with this context's name and no panels (a file that has none).</summary>
    public static DockLayout Empty(string context) => new(context);

    private static DockSlot? Classify(DialogPlacement placement, HashSet<string?> chained)
    {
        bool screenRight = string.Equals(placement.DockRight, "DockScreenRight", StringComparison.Ordinal);
        bool screenLeft = string.Equals(placement.DockLeft, "DockScreenLeft", StringComparison.Ordinal);
        // -1 is the column at the right screen edge, -2 the next one in. A docked panel stores
        // exactly that integer; a floating dialog stores a fraction of FloatingUnit pixels.
        bool columnNumber = placement.X <= -0.5f && placement.X >= -2.5f &&
            MathF.Abs(placement.X - MathF.Round(placement.X)) < 0.01f;

        if (screenRight || columnNumber)
        {
            int distance = screenRight ? 1 : Math.Max(1, (int)MathF.Round(-placement.X));
            return new DockSlot(placement, DockAnchor.Right, distance);
        }
        if (chained.Contains(placement.DialogClass) || screenLeft ||
            (placement.X >= -0.5f && placement.X < 0.05f && placement.Y < 0.5f))
            return new DockSlot(placement, DockAnchor.Left, 1);
        if (MathF.Abs(placement.X) < 0.5f && placement.Y >= 0.5f)
            return new DockSlot(placement, DockAnchor.Bottom, 1); // the timeline
        // No column and no chain: a panel the user dragged out, or a dialog that was never docked.
        return new DockSlot(placement, DockAnchor.Free, 0);
    }

    /// <summary>A dock partner's class without the <c>class </c> prefix.</summary>
    private static string? ClassName(string? atom) =>
        atom != null && atom.StartsWith("class ", StringComparison.Ordinal) ? atom[6..] : atom;

    /// <summary>The panels in <c>DockDown</c> order, starting with the panels no other panel names.</summary>
    private static List<DockSlot> Chain(List<DockSlot> panels)
    {
        var byClass = new Dictionary<string, DockSlot>(StringComparer.Ordinal);
        foreach (var panel in panels) byClass[panel.DialogClass] = panel;
        var downOf = panels.Where(p => p.Down != null).Select(p => p.Down!).ToHashSet(StringComparer.Ordinal);

        var ordered = new List<DockSlot>();
        foreach (var head in panels.Where(p => !downOf.Contains(p.DialogClass)).OrderBy(p => p.Top))
        {
            var current = head;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (current != null && seen.Add(current.DialogClass))
            {
                ordered.Add(current);
                current = current.Down != null && byClass.TryGetValue(current.Down, out var next) ? next : null;
            }
        }
        // A cycle or an unreachable panel is still shown, after the chains.
        foreach (var panel in panels)
            if (!ordered.Contains(panel))
                ordered.Add(panel);
        return ordered;
    }

    /// <summary>
    /// The rectangles of every panel for a window of <paramref name="width"/> ×
    /// <paramref name="height"/>: the left columns first, then the right columns from the right
    /// edge, then the bottom panel. Heights include <see cref="HeaderHeight"/>.
    /// </summary>
    public IReadOnlyList<DockRect> Resolve(int width, int height)
    {
        var rects = new List<DockRect>();
        if (width <= 0 || height <= 0) return rects;
        int bottomTop = Bottom != null
            ? Math.Clamp((int)MathF.Round(Bottom.Top * height),
                HeaderHeight + MinimumContentHeight, height)
            : height;

        int x = 0;
        foreach (var column in Columns.Where(c => c.Anchor == DockAnchor.Left).OrderBy(c => c.Distance))
        {
            int columnWidth = column.Width > 0 ? column.Width : DefaultWidth;
            ResolveColumn(column, x, columnWidth, height, bottomTop, rects);
            x += columnWidth;
        }

        int right = width;
        foreach (var column in Columns.Where(c => c.Anchor == DockAnchor.Right).OrderBy(c => c.Distance))
        {
            int columnWidth = column.Width > 0 ? column.Width : DefaultWidth;
            right -= columnWidth;
            ResolveColumn(column, right, columnWidth, height, bottomTop, rects);
        }

        if (Bottom != null)
            rects.Add(new DockRect(Bottom.DialogClass, 0, bottomTop, width, height - bottomTop));
        return rects;
    }

    private static void ResolveColumn(DockColumn column, int x, int width, int windowHeight, int bottom,
        List<DockRect> rects)
    {
        if (column.Panels.Count == 0) return;
        int top = Math.Clamp((int)MathF.Round(column.Panels[0].Top * windowHeight), 0,
            Math.Max(0, bottom - HeaderHeight - MinimumContentHeight));
        int y = top;
        for (int i = 0; i < column.Panels.Count; i++)
        {
            var panel = column.Panels[i];
            int available = Math.Max(0, bottom - y - HeaderHeight);
            int content = panel.Height > 0
                ? panel.Height
                : i == column.Panels.Count - 1
                    ? available
                    : Math.Min(DefaultHeight, available);
            content = Math.Clamp(content, Math.Min(MinimumContentHeight, available), available);
            rects.Add(new DockRect(panel.DialogClass, x, y, width, content + HeaderHeight));
            y += content + HeaderHeight;
        }
    }

    /// <summary>Writes a panel's height (its content, without the title bar) into the entry.</summary>
    public bool SetHeight(string dialogClass, int height)
    {
        if (Panel(dialogClass) is not { } panel) return false;
        height = Math.Max(MinimumContentHeight, height);
        var sizes = panel.Placement.SizeBarPositions.ToList();
        if (sizes.Count == 0) sizes.Add(0);
        sizes[0] = (ushort)Math.Clamp(height, 0, ushort.MaxValue);
        panel.Placement.SetSizeBarPositions(sizes.ToArray());
        return true;
    }

    /// <summary>
    /// Writes a column's width into every panel entry of the column, as the second
    /// <c>SizeBarPositions</c> value - the bar the app stores a column width in
    /// No sample
    /// has a second entry, so the meaning is the member's own ("one pixel width per splitter bar");
    /// the reader takes the column's width as the largest second entry it finds.
    /// </summary>
    public bool SetWidth(DockColumn column, int width)
    {
        if (column.Panels.Count == 0) return false;
        width = Math.Max(MinimumColumnWidth, width);
        foreach (var panel in column.Panels)
        {
            var sizes = panel.Placement.SizeBarPositions.ToList();
            while (sizes.Count < 2) sizes.Add(0);
            sizes[1] = (ushort)Math.Clamp(width, 0, ushort.MaxValue);
            panel.Placement.SetSizeBarPositions(sizes.ToArray());
        }
        return true;
    }

    /// <summary>
    /// Writes one panel's width into its own entry, as the second <c>SizeBarPositions</c> value. A
    /// floating panel has no column any more, so <see cref="SetWidth(DockColumn,int)"/> cannot write
    /// it; the reader still takes the column's width as the largest second entry of its panels.
    /// </summary>
    public bool SetWidth(string dialogClass, int width)
    {
        if (Panel(dialogClass) is not { } panel) return false;
        width = Math.Max(MinimumColumnWidth, width);
        var sizes = panel.Placement.SizeBarPositions.ToList();
        while (sizes.Count < 2) sizes.Add(0);
        sizes[1] = (ushort)Math.Clamp(width, 0, ushort.MaxValue);
        panel.Placement.SetSizeBarPositions(sizes.ToArray());
        return true;
    }

    /// <summary>Writes a panel's top, as the fraction of a window of <paramref name="windowHeight"/>.</summary>
    public bool SetTop(string dialogClass, int top, int windowHeight)
    {
        if (Panel(dialogClass) is not { } panel || windowHeight <= 0) return false;
        panel.Placement.Y = Math.Clamp(top, 0, windowHeight) / (float)windowHeight;
        return true;
    }

    /// <summary>
    /// The left edge a floating panel's saved <c>PosX</c> means in a window
    /// <paramref name="windowWidth"/> wide, for a panel <paramref name="width"/> wide: the file
    /// stores a left-half dialog as <c>Unit * PosX + Origin</c> and a right-half one from its
    /// right edge, <c>windowWidth + Unit * (PosX + 1) - Origin</c>
    /// </summary>
    public static int FloatLeft(DockSlot panel, int windowWidth, int width) =>
        (int)MathF.Round(panel.Placement.X >= 0
            ? FloatingUnit * panel.Placement.X + FloatingOrigin
            : windowWidth + FloatingUnit * (panel.Placement.X + 1f) - FloatingOrigin - width);

    /// <summary>The top a floating panel's saved <c>PosY</c> means in a window <paramref name="windowHeight"/> tall.</summary>
    public static int FloatTop(DockSlot panel, int windowHeight) =>
        (int)MathF.Round(panel.Placement.Y * windowHeight);

    /// <summary>
    /// Writes a panel's floating place: it stops being docked, so the drag clears the
    /// <c>DockDown</c> link of the panel above it (and the panel's own link and side partners) and
    /// stores the place as the fractions <see cref="FloatLeft"/>/<see cref="FloatTop"/> read. Only
    /// <c>PosX</c>/<c>PosY</c> and the links change; the panel's height and folded state are kept.
    /// Returns whether the entry changed.
    /// </summary>
    public bool SetFloating(string dialogClass, double x, double y, double width, int windowWidth,
        int windowHeight)
    {
        if (Panel(dialogClass) is not { } panel || windowWidth <= 0 || windowHeight <= 0) return false;
        var entry = panel.Placement;
        float posX = x + width / 2 < windowWidth / 2.0
            ? (float)(x - FloatingOrigin) / FloatingUnit
            : (FloatingOrigin + (float)(x + width) + 1 - windowWidth) / FloatingUnit - 1f;
        float posY = (float)y / windowHeight;
        bool changed = MathF.Abs(entry.X - posX) > 1e-6f || MathF.Abs(entry.Y - posY) > 1e-6f;
        entry.X = posX;
        entry.Y = posY;
        if (entry.DockDown != null) { entry.DockDown = null; changed = true; }
        if (entry.DockLeft != null) { entry.DockLeft = null; changed = true; }
        if (entry.DockRight != null) { entry.DockRight = null; changed = true; }
        foreach (var other in _panels.Values)
        {
            if (!string.Equals(ClassName(other.Placement.DockDown), dialogClass, StringComparison.Ordinal))
                continue;
            other.Placement.DockDown = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Hangs <paramref name="childClass"/> below <paramref name="parentClass"/>: the parent's
    /// <c>DockDown</c> names it, and no other panel's does. Returns whether an entry changed.
    /// </summary>
    public bool SetDown(string parentClass, string childClass)
    {
        if (Panel(parentClass) is not { } parent || Panel(childClass) == null) return false;
        bool changed = false;
        foreach (var other in _panels.Values)
        {
            if (other == parent ||
                !string.Equals(ClassName(other.Placement.DockDown), childClass, StringComparison.Ordinal))
                continue;
            other.Placement.DockDown = null;
            changed = true;
        }
        string atom = "class " + childClass;
        if (!string.Equals(parent.Placement.DockDown, atom, StringComparison.Ordinal))
        {
            parent.Placement.DockDown = atom;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Writes one docked column, top panel first: the column number the app stores in <c>PosX</c>
    /// (0 at the left edge, -1 at the right edge, -2 the next one in), each panel's top as a
    /// fraction of <paramref name="windowHeight"/> and the <c>DockDown</c> links between them.
    /// Returns whether an entry changed.
    /// </summary>
    public bool SetDocked(IReadOnlyList<(string DialogClass, int Top)> chain, DockAnchor anchor, int distance,
        int windowHeight)
    {
        if (windowHeight <= 0 || anchor is not (DockAnchor.Left or DockAnchor.Right)) return false;
        var members = chain.Where(member => Panel(member.DialogClass) != null).ToList();
        var names = members.Select(member => member.DialogClass).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        foreach (var other in _panels.Values)
        {
            if (names.Contains(other.DialogClass) || ClassName(other.Placement.DockDown) is not { } down ||
                !names.Contains(down))
                continue;
            other.Placement.DockDown = null;
            changed = true;
        }
        float posX = anchor == DockAnchor.Left ? 0f : -Math.Max(1, distance);
        for (int i = 0; i < members.Count; i++)
        {
            var entry = Panel(members[i].DialogClass)!.Placement;
            float posY = Math.Clamp(members[i].Top, 0, windowHeight) / (float)windowHeight;
            string? down = i + 1 < members.Count ? "class " + members[i + 1].DialogClass : null;
            string? left = anchor == DockAnchor.Left ? entry.DockLeft : null;
            string? right = anchor == DockAnchor.Right && distance <= 1 ? entry.DockRight : null;
            if (entry.X != posX || MathF.Abs(entry.Y - posY) > 1e-6f ||
                !string.Equals(entry.DockDown, down, StringComparison.Ordinal) ||
                !string.Equals(entry.DockLeft, left, StringComparison.Ordinal) ||
                !string.Equals(entry.DockRight, right, StringComparison.Ordinal))
                changed = true;
            entry.X = posX;
            entry.Y = posY;
            entry.DockDown = down;
            entry.DockLeft = left;
            entry.DockRight = right;
        }
        return changed;
    }

    /// <summary>
    /// Writes a panel as the one across the bottom (the timeline): column 0, a top in the
    /// lower half of the window, and no link to or from another panel. Returns whether it changed.
    /// </summary>
    public bool SetBottom(string dialogClass, int top, int windowHeight)
    {
        if (Panel(dialogClass) is not { } panel || windowHeight <= 0) return false;
        var entry = panel.Placement;
        float posY = Math.Clamp(top / (float)windowHeight, 0.5f, 1f);
        bool changed = entry.X != 0f || MathF.Abs(entry.Y - posY) > 1e-6f || entry.DockDown != null ||
                       entry.DockLeft != null || entry.DockRight != null;
        entry.X = 0f;
        entry.Y = posY;
        entry.DockDown = null;
        entry.DockLeft = null;
        entry.DockRight = null;
        foreach (var other in _panels.Values)
        {
            if (!string.Equals(ClassName(other.Placement.DockDown), dialogClass, StringComparison.Ordinal))
                continue;
            other.Placement.DockDown = null;
            changed = true;
        }
        return changed;
    }

    /// <summary>Sets or clears a panel's folded bit (<c>Flags</c> bit 0) and says whether it changed.</summary>
    public bool SetFolded(string dialogClass, bool folded)
    {
        if (Panel(dialogClass) is not { } panel) return false;
        uint flags = panel.Placement.Flags;
        uint next = folded ? flags | 1u : flags & ~1u;
        if (next == flags) return false;
        panel.Placement.Flags = next;
        return true;
    }
}
