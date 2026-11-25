using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The hierarchy panel: one row per 3D object of the
/// scene, drawn as a collapsible tree - every object with children starts collapsed behind a
/// squared plus (<see cref="TreeGutter"/>) that opens one level at a time - with a picture of the
/// object's kind before its name, a Show checkbox and, on a skinned mesh, a Lock checkbox. The
/// panel reports the requested changes; the window applies them as one undoable edit and rebuilds
/// the panel. Dragging a row's name onto another row asks for a hierarchy reparent (the drop is
/// the reparent action, not Parent Joints). Clicking a row's name selects the object. This is a
/// viewer design, not the custom-drawn tree control.
/// </summary>
public sealed class OutlinerView : UserControl
{
    /// <summary>The drag payload: the <see cref="Object3D"/> whose row is being dragged.</summary>
    private const string RowFormat = "sloppyanimatsu/outliner-row";

    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));

    // Every row draws the same handful of glyphs; a large scene has hundreds of rows, so each path
    // string is parsed once and its geometry (and each glyph colour's brush) shared by all rows.
    // Rows are built on the UI thread only, so plain dictionaries are enough.
    private static readonly Dictionary<string, Geometry> SharedGeometry = new();
    private static readonly Dictionary<uint, IBrush> SharedGlyphBrush = new();
    private static readonly IBrush CrossBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(230, 80, 70));

    private static Geometry GeometryOf(string data)
    {
        if (!SharedGeometry.TryGetValue(data, out var geometry))
            SharedGeometry[data] = geometry = Geometry.Parse(data);
        return geometry;
    }

    private static IBrush GlyphBrushOf(uint argb)
    {
        if (!SharedGlyphBrush.TryGetValue(argb, out var brush))
            SharedGlyphBrush[argb] = brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(argb));
        return brush;
    }
    private static readonly IBrush DropMarkBrush = new SolidColorBrush(Color.FromRgb(90, 170, 255));
    private static readonly IBrush DropFillBrush = new SolidColorBrush(Color.FromArgb(50, 90, 170, 255));
    private readonly Dictionary<Grid, Border> _dropMarks = [];

    /// <summary>Each row's export mark (<see cref="ExportHighlight"/>), lit while <see cref="ExportFilter"/> takes its object.</summary>
    private readonly Dictionary<Grid, Border> _exportMarks = [];

    private Func<Object3D, bool>? _exportFilter;

    /// <summary>
    /// The objects an open Export Settings window would write (null = no highlight). Setting it lights
    /// or clears every row's export mark at once.
    /// </summary>
    public Func<Object3D, bool>? ExportFilter
    {
        get => _exportFilter;
        set
        {
            _exportFilter = value;
            foreach (var (view, mark) in _exportMarks)
                ExportHighlight.Apply(mark, value != null && _byView.TryGetValue(view, out var o) && value(o));
        }
    }
    private Grid? _markedRow;
    // The other rows of a multi-selection: lighter than the current row.
    private static readonly IBrush SecondarySelectionBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Dictionary<Grid, Object3D> _byView = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Grid, TextBlock> _labels = [];
    private readonly Dictionary<Grid, StackPanel> _names = [];
    // The inline rename in progress: its text box and the row it replaces the label of.
    private TextBox? _editBox;
    private Grid? _editView;
    // A click on the row that was already the lone current one turns its name into a text field
    // after a short pause, unless the click becomes a double click or a drag first.
    private readonly Avalonia.Threading.DispatcherTimer _renameDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private Grid? _renamePending;
    // The tree, in row order: each row's view, gutter and parent row (-1 at the top level).
    private readonly List<(Grid View, TreeGutter Gutter, int Parent)> _tree = [];
    // The objects whose children are shown, by their stored object so a rebuild keeps them open.
    private readonly HashSet<object> _expanded = new(ReferenceEqualityComparer.Instance);
    private Grid? _selectedView;
    private bool _building;
    // A press on a row's name that could become a drag, and where it started.
    private Object3D? _dragSource;
    private Point _dragStart;

    /// <summary>A row's name was clicked; the flag is true for a Ctrl/Shift click that changed a multi-selection.</summary>
    public event Action<Object3D, bool>? ObjectSelected;

    /// <summary>The eye button of an object was clicked (true = show).</summary>
    public event Action<Object3D, bool>? ShownRequested;

    /// <summary>The pointer button of an object was clicked (true = clickable in the viewport).</summary>
    public event Action<Object3D, bool>? PickableRequested;

    /// <summary>An inline rename was committed with a new name.</summary>
    public event Action<Object3D, string>? RenameRequested;

    /// <summary>The Delete key was pressed with the panel focused (delete the selected rows).</summary>
    public event Action? DeleteRequested;

    /// <summary>A row was dropped on another row: reparent the first object under the second.</summary>
    public event Action<Object3D, Object3D, OutlinerDropPlace>? ReparentRequested;

    /// <summary>A row was dropped on the empty space below the rows: move it to the scene root.</summary>
    public event Action<Object3D>? SceneRootDropRequested;

    /// <summary>Ctrl+A was pressed with the panel focused: every row is now selected.</summary>
    public event Action? AllSelected;

    /// <summary>The camera button beside an animated camera's row was clicked: open or close its view.</summary>
    public event Action<CameraAnimated>? CameraViewRequested;

    /// <summary>Whether an animated camera's view window is open (its row button is lit).</summary>
    public Func<CameraAnimated, bool>? CameraViewOpen { get; set; }

    private readonly Dictionary<CameraAnimated, Button> _cameraButtons = new(ReferenceEqualityComparer.Instance);

    /// <summary>Re-colours the camera buttons after a camera view opened or closed.</summary>
    public void RefreshCameraButtons()
    {
        foreach (var (camera, button) in _cameraButtons) StyleCameraButton(camera, button);
    }

    private void StyleCameraButton(CameraAnimated camera, Button button)
    {
        bool open = CameraViewOpen?.Invoke(camera) == true;
        if (button.Content is Viewbox { Child: Grid canvas })
            foreach (var path in canvas.Children.OfType<Avalonia.Controls.Shapes.Path>())
                path.Fill = open ? new SolidColorBrush(Color.FromRgb(0x4D, 0xA6, 0xFF)) : Brushes.Gainsboro;
        button.Opacity = open ? 1.0 : 0.7;
        ToolTip.SetTip(button, open ? $"Close the view through '{camera.Name}'" : $"Open a view through '{camera.Name}'");
    }

    private const string CameraIcon = "M1,5 H10 V12 H1 Z M10,7.4 L15,5 V12 L10,9.6 Z M2.5,3.4 A1.4,1.4 0 1 1 5.3,3.4 A1.4,1.4 0 1 1 2.5,3.4 Z M5.9,3.4 A1.4,1.4 0 1 1 8.7,3.4 A1.4,1.4 0 1 1 5.9,3.4 Z";

    /// <summary>
    /// The row the last right-click landed on, or null when it landed on the panel's blank space
    /// (the context menu then offers the "all" commands instead of the hierarchy ones).
    /// </summary>
    public Object3D? ContextRow { get; private set; }

    // An open eye and a closed one (a lid line with lashes), drawn in a 16x16 box.
    private const string EyeOpen = "M1,8 C3.5,3.5 12.5,3.5 15,8 C12.5,12.5 3.5,12.5 1,8 Z M8,5.2 A2.8,2.8 0 1 0 8.01,5.2 Z";
    private const string EyeClosed = "M1,7 C3.5,11 12.5,11 15,7 L14.2,6.4 C12,9.6 4,9.6 1.8,6.4 Z M3.6,9.2 L2.4,11.4 L3.3,11.9 L4.5,9.7 Z M7.5,10.2 L7.5,12.8 L8.5,12.8 L8.5,10.2 Z M12.4,9.2 L13.6,11.4 L12.7,11.9 L11.5,9.7 Z";
    // A mouse arrow, and a cross drawn over it when the object cannot be clicked.
    private const string Pointer = "M3,1 L3,13.5 L6,10.6 L8.2,15 L10.2,14 L8,9.7 L12.2,9.7 Z";
    private const string Cross = "M9,9 L10,8 L15.5,13.5 L14.5,14.5 Z M14.5,8 L15.5,9 L10,14.5 L9,13.5 Z";

    public OutlinerView()
    {
        _scroll.Content = _rows;
        Content = _scroll;
        // The empty space below the rows is the scene root: a mesh dropped there leaves its
        // character. Rows mark their own drag events handled, so these see only the empty space.
        _scroll.Background = Brushes.Transparent;
        DragDrop.SetAllowDrop(_scroll, true);
        _scroll.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var dragged = Dragged(e.Data);
            e.DragEffects = dragged != null && OutlinerDrag.RootVerdict(dragged) == OutlinerDropVerdict.Move
                ? DragDropEffects.Move : DragDropEffects.None;
            ShowDropMark(null, OutlinerDropPlace.Inside);
        });
        _scroll.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (Dragged(e.Data) is not { } dragged) return;
            e.DragEffects = OutlinerDrag.RootVerdict(dragged) == OutlinerDropVerdict.Move
                ? DragDropEffects.Move : DragDropEffects.None;
            SceneRootDropRequested?.Invoke(dragged);
        });
        // Focusable so the Delete and F2 keys reach the panel once a row was clicked.
        Focusable = true;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // Every press clears the context row; a row's own handler (below this one in the tunnel) sets it.
        AddHandler(PointerPressedEvent, (_, _) => ContextRow = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        _renameDelay.Tick += (_, _) =>
        {
            _renameDelay.Stop();
            var view = _renamePending;
            _renamePending = null;
            if (view != null && ReferenceEquals(view, _selectedView) && _selectedViews.Count == 1) BeginRename(view);
        };
    }

    /// <summary>The live key map (Hotkeys.PanelActions); the shipped keys until the window sets it.</summary>
    public HotkeyMap Keys { get; set; } = HotkeyMap.Default;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editBox != null) return;
        switch (Keys.ActionFor(HotkeyScope.Outliner, ShortcutInput.FromKey(e.Key, e.KeyModifiers)))
        {
            case "SelectAllControllers":
                SelectAll();
                AllSelected?.Invoke();
                e.Handled = true;
                break;
            case "DeleteObject" when _selectedViews.Count > 0:
                DeleteRequested?.Invoke();
                e.Handled = true;
                break;
            // Ctrl+C / Ctrl+V are left to the window, which copies and pastes keys (poses) of the
            // selection: objects are copied with Duplicate hierarchy only.
            case "RenameRow" when _selectedView != null:
                BeginRename(_selectedView);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Turns the current row's name into a text field (the menu's Rename, F2, or a click on the selected row).</summary>
    public void BeginRename() { if (_selectedView != null) BeginRename(_selectedView); }

    private void BeginRename(Grid view)
    {
        if (_editBox != null || !_byView.TryGetValue(view, out var o)
            || !_labels.TryGetValue(view, out var label) || !_names.TryGetValue(view, out var name)) return;
        _renameDelay.Stop();
        _renamePending = null;
        var box = new TextBox
        {
            Text = o.Name ?? "",
            FontSize = 12,
            MinHeight = 0,
            MinWidth = 120,
            Padding = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            string text = (box.Text ?? "").Trim();
            name.Children.Remove(box);
            label.IsVisible = true;
            _editBox = null;
            _editView = null;
            Focus();
            if (commit && text.Length > 0 && text != o.Name) RenameRequested?.Invoke(o, text);
        }
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) => Finish(true);
        label.IsVisible = false;
        name.Children.Add(box);
        _editBox = box;
        _editView = view;
        box.AttachedToVisualTree += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }

    /// <summary>The object of the current (last clicked) selected row, or null.</summary>
    public Object3D? Selected =>
        _selectedView != null && _byView.TryGetValue(_selectedView, out var o) ? o : null;

    /// <summary>Every selected row's object, in selection order (Ctrl/Shift+click select several).</summary>
    public IReadOnlyList<Object3D> SelectedObjects =>
        _selectedViews.Where(_byView.ContainsKey).Select(v => _byView[v]).ToList();

    // Every highlighted row, in the order it joined; _selectedView is the current one among them.
    private readonly List<Grid> _selectedViews = [];

    /// <summary>Rebuilds the rows from the scene, keeping the selection when the object is still listed.</summary>
    public void Build(Scene scene)
    {
        var selected = Selected;
        var selectedAll = SelectedObjects;
        // A rebuild (after a show/hide, a lock, a rename...) keeps the list where it was scrolled to.
        var offset = _scroll.Offset;
        _building = true;
        _dragSource = null;
        _renameDelay.Stop();
        _renamePending = null;
        _editBox = null;
        _editView = null;
        _byView.Clear();
        _labels.Clear();
        _dropMarks.Clear();
        _exportMarks.Clear();
        _markedRow = null;
        _names.Clear();
        _cameraButtons.Clear();
        _tree.Clear();
        _selectedView = null;
        _selectedViews.Clear();
        _rows.Children.Clear();
        var rows = Outliner.Rows(scene);
        int n = rows.Count;
        // Parents and last siblings from the depths of the pre-order list.
        var parent = new int[n];
        var isLast = new bool[n];
        var chain = new List<int>(); // chain[d] = the latest row at depth d
        for (int i = 0; i < n; i++)
        {
            int depth = Math.Min(rows[i].Depth, chain.Count);
            parent[i] = depth > 0 ? chain[depth - 1] : -1;
            if (chain.Count > depth) chain.RemoveRange(depth, chain.Count - depth);
            chain.Add(i);
            isLast[i] = true;
            for (int j = i + 1; j < n && rows[j].Depth >= rows[i].Depth; j++)
                if (rows[j].Depth == rows[i].Depth) { isLast[i] = false; break; }
        }
        for (int i = 0; i < n; i++)
        {
            var row = rows[i];
            bool hasChildren = i + 1 < n && rows[i + 1].Depth > row.Depth;
            var continues = new bool[Math.Max(0, row.Depth - 1)];
            for (int a = parent[i]; a >= 0; a = parent[a])
            {
                int level = rows[a].Depth - 1;
                if (level >= 0 && level < continues.Length) continues[level] = !isLast[a];
            }
            var gutter = new TreeGutter(row.Depth, continues, isLast[i], hasChildren);
            var obj = row.Object;
            gutter.Toggled += deep => SetExpanded(obj, !_expanded.Contains(obj.Source), deep);
            var view = RowView(row, gutter);
            _byView[view] = row.Object;
            _tree.Add((view, gutter, parent[i]));
            _rows.Children.Add(view);
        }
        UpdateVisibility();
        _building = false;
        if (selectedAll.Count > 0) SetSelection(selectedAll, selected);
        RestoreOffset(offset);
    }

    /// <summary>
    /// Puts the scroll offset back once the rebuilt rows are measured; until then the extent is
    /// empty and the offset would be clamped to the top.
    /// </summary>
    private void RestoreOffset(Avalonia.Vector offset)
    {
        if (offset == default) return;
        _scroll.Offset = offset;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.Offset = offset,
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Selects the row of <paramref name="o"/> (none for null). Collapsed ancestors stay collapsed;
    /// the nearest shown one is marked instead (<see cref="UpdateHiddenSelectionMarks"/>).
    /// </summary>
    public void Select(Object3D? o) => SetSelection(o != null ? [o] : [], o);

    /// <summary>
    /// Highlights the rows of <paramref name="objects"/> (the rest are cleared) and makes
    /// <paramref name="current"/>'s row the current one. Selecting never expands the tree: a
    /// selected row under a collapsed ancestor lights that ancestor dimly instead.
    /// </summary>
    public void SetSelection(IEnumerable<Object3D> objects, Object3D? current)
    {
        var wanted = new HashSet<Object3D>(objects, ReferenceEqualityComparer.Instance);
        var views = new List<Grid>();
        Grid? currentView = null;
        int currentIndex = -1;
        for (int i = 0; i < _tree.Count; i++)
        {
            var view = _tree[i].View;
            var o = _byView[view];
            if (!wanted.Contains(o) && !ReferenceEquals(o, current)) continue;
            views.Add(view);
            if (!ReferenceEquals(o, current)) continue;
            currentView = view;
            currentIndex = i;
        }
        SetSelectedViews(views, currentView);
        int shown = currentIndex >= 0 ? ShownRowOf(currentIndex) : -1;
        if (shown >= 0) _tree[shown].View.BringIntoView();
    }

    /// <summary>The row itself when it is shown, else its nearest shown ancestor (-1 for none).</summary>
    private int ShownRowOf(int row)
    {
        for (int a = row; a >= 0; a = _tree[a].Parent)
            if (_tree[a].View.IsVisible) return a;
        return -1;
    }

    // Shown rows lit because a selected row is hidden somewhere below them.
    private readonly List<Grid> _hiddenSelectionMarks = [];

    private static readonly IBrush HiddenSelectionBrush = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));

    /// <summary>
    /// Lights, dimmer than a selection, each shown row that has a selected row hidden under it by a
    /// collapsed ancestor (a selected thigh under a collapsed root lights the root).
    /// </summary>
    private void UpdateHiddenSelectionMarks()
    {
        foreach (var view in _hiddenSelectionMarks)
            if (!_selectedViews.Contains(view)) view.Background = Brushes.Transparent;
        _hiddenSelectionMarks.Clear();
        if (_selectedViews.Count == 0) return;
        var selected = new HashSet<Grid>(_selectedViews, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _tree.Count; i++)
        {
            var view = _tree[i].View;
            if (!selected.Contains(view) || view.IsVisible) continue;
            int shown = ShownRowOf(i);
            if (shown < 0) continue;
            var mark = _tree[shown].View;
            if (selected.Contains(mark) || _hiddenSelectionMarks.Contains(mark)) continue;
            mark.Background = HiddenSelectionBrush;
            _hiddenSelectionMarks.Add(mark);
        }
    }

    /// <summary>A click on a row's name: plain replaces the selection, Ctrl toggles the row, Shift selects the visible range.</summary>
    private void ClickRow(Grid view, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift) && _selectedView != null)
        {
            int from = _tree.FindIndex(t => ReferenceEquals(t.View, _selectedView));
            int to = _tree.FindIndex(t => ReferenceEquals(t.View, view));
            var range = new List<Grid>();
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                if (_tree[i].View.IsVisible) range.Add(_tree[i].View);
            if (modifiers.HasFlag(KeyModifiers.Control))
                range = _selectedViews.Concat(range.Where(v => !_selectedViews.Contains(v))).ToList();
            // The clicked row becomes the current one; the anchor stays in the range.
            range.Remove(view);
            range.Add(view);
            SetSelectedViews(range, view);
        }
        else if (modifiers.HasFlag(KeyModifiers.Control))
        {
            var views = _selectedViews.ToList();
            if (views.Remove(view))
                SetSelectedViews(views, views.Count > 0 ? views[^1] : null);
            else
            {
                views.Add(view);
                SetSelectedViews(views, view);
            }
        }
        else
        {
            SetSelectedViews([view], view);
        }
    }

    private void SetSelectedViews(List<Grid> views, Grid? current)
    {
        foreach (var old in _selectedViews.Where(v => !views.Contains(v)))
        {
            old.Background = Brushes.Transparent;
            StyleRow(old, false);
        }
        _selectedViews.Clear();
        _selectedViews.AddRange(views);
        _selectedView = current;
        foreach (var view in views)
        {
            view.Background = ReferenceEquals(view, current) ? SelectionBrush : SecondarySelectionBrush;
            StyleRow(view, true);
        }
        UpdateHiddenSelectionMarks();
    }

    /// <summary>Selects every row of the tree, the first one current (Ctrl+A).</summary>
    public void SelectAll()
    {
        var views = _tree.Select(t => t.View).ToList();
        SetSelectedViews(views, views.FirstOrDefault());
    }

    /// <summary>
    /// The rows of <paramref name="roots"/> and every row below them in the tree, in row order -
    /// what the tree draws under an object (joints, meshes, controllers, folders and cameras alike).
    /// </summary>
    public IReadOnlyList<Object3D> Hierarchy(IEnumerable<Object3D> roots)
    {
        var wanted = new HashSet<Object3D>(roots, ReferenceEqualityComparer.Instance);
        var inside = new bool[_tree.Count];
        var result = new List<Object3D>();
        for (int i = 0; i < _tree.Count; i++)
        {
            var o = _byView[_tree[i].View];
            int parent = _tree[i].Parent;
            inside[i] = wanted.Contains(o) || (parent >= 0 && inside[parent]);
            if (inside[i]) result.Add(o);
        }
        return result;
    }

    /// <summary>Opens or closes every row that has children (Expand all / Collapse all).</summary>
    public void SetExpandedAll(bool expanded)
    {
        bool changed = false;
        foreach (var (view, gutter, _) in _tree)
        {
            if (!gutter.HasChildren) continue;
            object source = _byView[view].Source;
            changed |= expanded ? _expanded.Add(source) : _expanded.Remove(source);
        }
        if (changed) UpdateVisibility();
    }

    /// <summary>
    /// Shows or hides the children of <paramref name="o"/>: one level (theirs keep their own state),
    /// or with <paramref name="deep"/> (Ctrl+click on the box) the whole hierarchy below it.
    /// </summary>
    public void SetExpanded(Object3D o, bool expanded, bool deep = false)
    {
        bool changed = expanded ? _expanded.Add(o.Source) : _expanded.Remove(o.Source);
        if (deep)
        {
            int i = _tree.FindIndex(t => ReferenceEquals(_byView[t.View], o));
            // Pre-order rows: the descendants of row i follow it until a row that is not below it.
            for (int j = i + 1; i >= 0 && j < _tree.Count && IsBelow(j, i); j++)
            {
                if (!_tree[j].Gutter.HasChildren) continue;
                object source = _byView[_tree[j].View].Source;
                changed |= expanded ? _expanded.Add(source) : _expanded.Remove(source);
            }
        }
        if (changed) UpdateVisibility();
    }

    private bool IsBelow(int row, int ancestor)
    {
        for (int a = _tree[row].Parent; a >= 0; a = _tree[a].Parent)
            if (a == ancestor) return true;
        return false;
    }

    /// <summary>A row is shown while every ancestor is expanded; each box draws its own state.</summary>
    private void UpdateVisibility()
    {
        var shown = new bool[_tree.Count];
        for (int i = 0; i < _tree.Count; i++)
        {
            var (view, gutter, parent) = _tree[i];
            gutter.Expanded = gutter.HasChildren && _expanded.Contains(_byView[view].Source);
            shown[i] = parent < 0 || (shown[parent] && _tree[parent].Gutter.Expanded);
            view.IsVisible = shown[i];
        }
        UpdateHiddenSelectionMarks();
    }

    /// <summary>
    /// Paints a row's name in its kind's colour, dimmed while the object is hidden unless the row is
    /// the selected one.
    /// </summary>
    private void StyleRow(Grid view, bool selected)
    {
        if (!_byView.TryGetValue(view, out var o) || !_labels.TryGetValue(view, out var label)) return;
        uint color = OutlinerStyle.For(o, selected).Color;
        label.Foreground = new SolidColorBrush(Color.FromUInt32(color));
    }

    private Grid RowView(OutlinerRow row, TreeGutter gutter)
    {
        var o = row.Object;
        var style = OutlinerStyle.For(o);
        // The eye: open while the object is shown, closed while it is hidden; a click flips it.
        // An object whose own eye is open but sits under a hidden parent keeps the open eye, faded.
        bool underHidden = o.IsShown && !o.IsShownInScene;
        var show = IconButton(o.IsShown ? [EyeOpen] : [EyeClosed], o.IsShown ? (underHidden ? 0.45 : 0.9) : 0.45,
            !o.IsShown ? "Hidden (click to show)"
            : underHidden ? "Shown, but a parent is hidden (click to hide)" : "Shown in the viewport (click to hide)");
        show.Click += (_, _) =>
        {
            if (!_building) ShownRequested?.Invoke(o, !o.IsShown);
        };
        // The pointer: crossed out while the object cannot be clicked or selected in the viewport. A
        // character's flag covers everything under it, drawn faint on the rows below.
        bool own = o.IsUnpickable;
        bool inherited = !own && !Outliner.IsPickable(o);
        var pick = IconButton(own ? [Pointer, Cross] : [Pointer], own ? 0.9 : inherited ? 0.3 : 0.6,
            own ? "Not clickable in the viewport (click to allow)"
                : inherited ? "Not clickable: its character is set unclickable"
                : "Clickable in the viewport (click to make it unclickable)");
        pick.Click += (_, _) =>
        {
            if (!_building) PickableRequested?.Invoke(o, own);
        };
        // The row's picture: the per-class Tree_*.png artwork is not copied; the kind it stands
        // for is drawn with SloppyAnimatsu's own glyph (OutlinerStyle).
        var glyph = new Viewbox
        {
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = GeometryOf(style.Glyph),
                Fill = GlyphBrushOf(style.Color),
                Width = OutlinerStyle.Box,
                Height = OutlinerStyle.Box,
            },
        };
        string kind = OutlinerStyle.Describe(style.Kind);
        ToolTip.SetTip(glyph, kind);
        var label = new TextBlock
        {
            Text = o.Name ?? "(unnamed)",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromUInt32(style.Color)),
        };
        bool skinLocked = o is Mesh { Skinned: true, SkinnedMeshIsLocked: true };
        if (skinLocked) label.Text += "  (locked)";
        ToolTip.SetTip(label, $"{kind} - {o.ClassName}" + (skinLocked ? " - skin locked: Unbind Skin leaves it attached" : ""));
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Children = { glyph, label },
        };
        // An animated camera's row carries a button that opens a view through it (lit while open).
        if (o is CameraAnimated camera)
        {
            var view = IconButton([CameraIcon], 0.7, "");
            view.Height = 18;
            StyleCameraButton(camera, view);
            view.Click += (_, _) =>
            {
                if (!_building) CameraViewRequested?.Invoke(camera);
            };
            // The button's press must not select or drag the row.
            view.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
            _cameraButtons[camera] = view;
            name.Children.Add(view);
        }
        var tree = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 0, 0),
            // Transparent so the blank space right of the name, up to the Show box, is part of the
            // row's hit area; the gutter's own expand box handles its press first.
            Background = Brushes.Transparent,
            Children = { gutter, name },
        };

        var grid = new Grid
        {
            // The last column stays empty: room for the overlaid scroll bar, so it never covers the
            // buttons. A skinned mesh's lock is in the panel's right-click menu.
            ColumnDefinitions = new ColumnDefinitions("*,22,22,14"),
            Background = Brushes.Transparent,
            Height = 22,
        };
        _labels[grid] = label;
        _names[grid] = name;
        // A right-click anywhere on the row (its buttons included) makes it the context menu's row.
        grid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) ContextRow = o;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The whole row left of the buttons selects: indentation, glyph, name and the blank after it.
        tree.PointerPressed += (_, e) =>
        {
            if (_building) return;
            // A press inside the rename field belongs to the field.
            if (_editBox != null && e.Source is Visual v
                && (ReferenceEquals(v, _editBox) || v.GetVisualAncestors().Contains(_editBox))) return;
            Focus();
            var point = e.GetCurrentPoint(tree).Properties;
            // A right-click on a row already in a multi-selection keeps it for the context menu.
            bool keep = !point.IsLeftButtonPressed && _selectedViews.Contains(grid);
            var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift);
            // A plain click on the row that is already the lone current one renames it (after a
            // pause, so a double click still opens the row instead).
            bool wasCurrent = point.IsLeftButtonPressed && modifiers == KeyModifiers.None && e.ClickCount == 1
                              && ReferenceEquals(_selectedView, grid) && _selectedViews.Count == 1;
            if (!keep) ClickRow(grid, modifiers);
            ObjectSelected?.Invoke(o, modifiers != KeyModifiers.None);
            _renameDelay.Stop();
            _renamePending = null;
            if (wasCurrent)
            {
                _renamePending = grid;
                _renameDelay.Start();
            }
            if (point.IsLeftButtonPressed)
            {
                _dragSource = o;
                _dragStart = e.GetPosition(this);
            }
        };
        // A double click on the name opens or closes the row, like its box.
        name.DoubleTapped += (_, _) =>
        {
            _renameDelay.Stop();
            _renamePending = null;
            if (_editBox != null) return;
            if (gutter.HasChildren) SetExpanded(o, !_expanded.Contains(o.Source));
        };
        tree.PointerMoved += (_, e) => StartDrag(o, e);
        tree.PointerReleased += (_, _) => _dragSource = null;
        // The whole row accepts a drop, including its check boxes and indentation.
        DragDrop.SetAllowDrop(grid, true);
        grid.AddHandler(DragDrop.DragOverEvent, (_, e) => DragOver(o, grid, e));
        grid.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDropMark(null, OutlinerDropPlace.Inside));
        grid.AddHandler(DragDrop.DropEvent, (_, e) => Drop(o, grid, e));
        // Where a drop would land: a frame round the row (as its child) or a line on its top or
        // bottom edge (beside it). Drawn over the row and never hit-tested.
        var mark = new Border
        {
            IsHitTestVisible = false,
            BorderBrush = DropMarkBrush,
            IsVisible = false,
        };
        Grid.SetColumnSpan(mark, 4);
        _dropMarks[grid] = mark;
        // Lit while an open Export Settings window would write this object; behind the row's content.
        var exportMark = ExportHighlight.NewMark();
        Grid.SetColumnSpan(exportMark, 4);
        ExportHighlight.Apply(exportMark, _exportFilter?.Invoke(o) == true);
        _exportMarks[grid] = exportMark;
        grid.Children.Add(exportMark);
        grid.Children.Add(tree);
        grid.Children.Add(mark);
        Grid.SetColumn(show, 1);
        grid.Children.Add(show);
        Grid.SetColumn(pick, 2);
        grid.Children.Add(pick);
        return grid;
    }

    /// <summary>A flat 22x20 button drawing the given 16x16 paths on top of each other.</summary>
    private static Button IconButton(string[] paths, double opacity, string tip)
    {
        var canvas = new Grid { Width = 16, Height = 16 };
        foreach (string data in paths)
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = GeometryOf(data),
                Fill = data == Cross ? CrossBrush : Brushes.Gainsboro,
            });
        var button = new Button
        {
            Content = new Viewbox { Width = 14, Height = 14, Child = canvas },
            Width = 22,
            Height = 20,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = opacity,
            Focusable = false,
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>Starts the row's drag once the pointer has left the click, so a click stays a click.</summary>
    private async void StartDrag(Object3D o, PointerEventArgs e)
    {
        if (!ReferenceEquals(_dragSource, o)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragSource = null;
            return;
        }
        var moved = e.GetPosition(this);
        if (!OutlinerDrag.StartsDrag(new Vector2((float)_dragStart.X, (float)_dragStart.Y),
                new Vector2((float)moved.X, (float)moved.Y))) return;
        _dragSource = null;
        // A drag is not a click on the selected row: no rename.
        _renameDelay.Stop();
        _renamePending = null;
        var data = new DataObject();
        data.Set(RowFormat, o);
        try
        {
            // The platform's drag loop; a platform without one leaves the click's selection as it is.
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
        }
    }

    private void DragOver(Object3D target, Grid row, DragEventArgs e)
    {
        var dragged = Dragged(e.Data);
        var place = OutlinerDrag.PlaceAt(e.GetPosition(row).Y, row.Bounds.Height);
        bool allowed = dragged != null && OutlinerDrag.AllowsDrop(dragged, target, place);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        ShowDropMark(allowed ? row : null, place);
        e.Handled = true;
    }

    private void Drop(Object3D target, Grid row, DragEventArgs e)
    {
        var dragged = Dragged(e.Data);
        var place = OutlinerDrag.PlaceAt(e.GetPosition(row).Y, row.Bounds.Height);
        ShowDropMark(null, place);
        e.Handled = true;
        if (dragged == null) return;
        e.DragEffects = OutlinerDrag.AllowsDrop(dragged, target, place) ? DragDropEffects.Move : DragDropEffects.None;
        // A refused drop is still reported, so the window can say why.
        ReparentRequested?.Invoke(dragged, target, place);
    }

    /// <summary>Shows the drop mark on <paramref name="row"/> for <paramref name="place"/>, or hides it (null).</summary>
    private void ShowDropMark(Grid? row, OutlinerDropPlace place)
    {
        if (_markedRow != null && !ReferenceEquals(_markedRow, row) && _dropMarks.TryGetValue(_markedRow, out var old))
            old.IsVisible = false;
        _markedRow = row;
        if (row == null || !_dropMarks.TryGetValue(row, out var mark)) return;
        mark.BorderThickness = place switch
        {
            OutlinerDropPlace.Before => new Thickness(0, 2, 0, 0),
            OutlinerDropPlace.After => new Thickness(0, 0, 0, 2),
            _ => new Thickness(1),
        };
        mark.Background = place == OutlinerDropPlace.Inside ? DropFillBrush : null;
        mark.IsVisible = true;
    }

    /// <summary>The object a drag carries, or null when this is not our own row drag.</summary>
    private static Object3D? Dragged(IDataObject? data) =>
        data?.Contains(RowFormat) == true && data.Get(RowFormat) is Object3D o ? o : null;
}
