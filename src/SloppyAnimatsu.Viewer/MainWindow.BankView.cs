// The Anim Bank panel's view controls: the search box and the sort buttons above the list
// (AnimBankView), the free sort's reorder edit, and what a bank drag draws (the floating copy of the
// dragged rows, the frame round the folder it would drop into, the line where it would be placed).
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The bank search text; blank lists everything.</summary>
    private string? _bankQuery;

    /// <summary>The sort used while no preferences file is open.</summary>
    private AnimBankSort _bankSortFallback = AnimBankSort.Alphabetical;
    private bool _bankReversedFallback;

    private readonly Dictionary<AnimBankSort, MenuItem> _bankSortItems = new();
    private Button? _bankSortButton;
    private TextBox? _bankSearchBox;

    /// <summary>How the bank lists its rows: the preference (A-Z unless another sort was chosen).</summary>
    private AnimBankSort BankSort => _preferences?.AnimBankSort ?? _bankSortFallback;

    /// <summary>Whether the A-Z or Added sort runs the other way (Z-A; oldest first).</summary>
    private bool BankSortReversed => BankSort != AnimBankSort.Free && (_preferences?.AnimBankSortReversed ?? _bankReversedFallback);

    /// <summary>The row above the bank list: a search box and the A-Z / Free / Added sort buttons.</summary>
    private Control BankViewBar()
    {
        _bankSearchBox = new TextBox
        {
            Text = _bankQuery,
            Watermark = "Search animations",
            FontSize = 11.0,
            MinHeight = 22.0,
            Padding = new Thickness(4.0, 2.0),
        };
        ToolTip.SetTip(_bankSearchBox, "Show only the animations and folders whose name holds this text (Esc clears)");
        _bankSearchBox.TextChanged += delegate
        {
            _bankQuery = _bankSearchBox.Text;
            RefreshBankView();
        };
        _bankSearchBox.KeyDown += delegate(object? _, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(_bankSearchBox.Text))
            {
                _bankSearchBox.Text = "";
                e.Handled = true;
            }
        };
        // One sort button: left click opens the list of sorts, right click reverses the current one.
        Button sorts = new Button
        {
            FontSize = 11.0,
            MinHeight = 22.0,
            MinWidth = 0.0,
            Padding = new Thickness(5.0, 1.0),
            Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(sorts,
            "Sort the Anim Bank.\nLeft click: choose A–Z, Free (your own drag order, saved with the scene) or Date Added.\n" +
            "Right click: reverse the sort (Z–A; oldest first). The arrow shows the direction: ▼ normal, ▲ reversed. Free has no reverse.");
        MenuFlyout sortMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach ((AnimBankSort sort, string label) in new[]
        {
            (AnimBankSort.Alphabetical, "A–Z"),
            (AnimBankSort.Free, "Free"),
            (AnimBankSort.Added, "Date Added"),
        })
        {
            MenuItem item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "bankSort" };
            item.Click += delegate { if (sort != BankSort) SetBankSort(sort, false); else SyncBankSortBox(); };
            _bankSortItems[sort] = item;
            sortMenu.Items.Add(item);
        }
        sorts.Flyout = sortMenu;
        sorts.AddHandler(PointerPressedEvent, delegate(object? _, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(sorts).Properties.IsRightButtonPressed) return;
            e.Handled = true;
            if (BankSort == AnimBankSort.Free)
            {
                _status.Text = "Anim Bank: the free order has no reverse - drag rows to rearrange it.";
                return;
            }
            SetBankSort(BankSort, !BankSortReversed);
        // Tunnel only: registering Bubble as well ran this twice per click, reversing and
        // un-reversing the sort.
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _bankSortButton = sorts;
        SyncBankSortBox();
        DockPanel bar = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 3.0) };
        DockPanel.SetDock(sorts, Dock.Right);
        bar.Children.Add(sorts);
        bar.Children.Add(_bankSearchBox);
        return bar;
    }

    /// <summary>
    /// Sets the bank's sort and direction (A-Z / Z-A, newest / oldest first; the free order has no
    /// reverse). Kept in the preferences, and the bank is relisted.
    /// </summary>
    private void SetBankSort(AnimBankSort sort, bool reversed)
    {
        if (sort == AnimBankSort.Free) reversed = false;
        _bankSortFallback = sort;
        _bankReversedFallback = reversed;
        _status.Text = sort switch
        {
            AnimBankSort.Free => "Anim Bank: free sort - drag rows to rearrange them.",
            AnimBankSort.Added => reversed ? "Anim Bank: oldest first." : "Anim Bank: newest first.",
            _ => (reversed ? "Anim Bank: sorted Z–A" : "Anim Bank: sorted A–Z") + " (your free order is kept).",
        };
        if (_preferences != null)
        {
            _preferences.AnimBankSort = sort;
            _preferences.AnimBankSortReversed = reversed;
            try { _preferences.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _status.Text = "Could not save the preferences: " + ex.Message;
            }
        }
        SyncBankSortBox();
        RefreshBankView();
    }

    /// <summary>The sort button and its list after the sort changed (here, or in the Preferences dialog).</summary>
    private void SyncBankSortBox()
    {
        AnimBankSort current = BankSort;
        foreach ((AnimBankSort sort, MenuItem item) in _bankSortItems)
            item.IsChecked = sort == current;
        if (_bankSortButton == null) return;
        string arrow = current == AnimBankSort.Free ? "" : BankSortReversed ? " ▲" : " ▼";
        _bankSortButton.Content = current switch
        {
            AnimBankSort.Alphabetical => "A–Z",
            AnimBankSort.Added => "Added",
            _ => "Free",
        } + arrow;
    }

    /// <summary>Relists the bank after its sort or search changed, keeping the selected row.</summary>
    private void RefreshBankView()
    {
        if (_character == null) return;
        List<Item<SceneObject>> list = BankItems(_character);
        _bankDragItem = null;
        _bankDragging = false;
        HideBankDrag();
        _syncing = true;
        _animations.ItemsSource = list;
        _animations.SelectedItem = list.FirstOrDefault(i => i.Value == ((object?)_bankFolder ?? _animation));
        _syncing = false;
    }

    /// <summary>The free sort's drop: places bank rows before <paramref name="before"/> in <paramref name="target"/> as one undo step.</summary>
    private void PlaceInBank(IReadOnlyCollection<SceneObject> items, SceneObject target, SceneObject? before)
    {
        Folder? bankFolder = _bankFolder;
        bool moved = false;
        RunBankEdit(session => moved = session.PlaceInBank(items, target, before));
        RefreshBankView();
        if (bankFolder != null && InBank(bankFolder))
        {
            ShowBankFolder(bankFolder);
        }
        if (moved)
        {
            _status.Text = items.Count == 1 ? $"Moved '{items.First().Name}'." : $"Moved {items.Count} items.";
        }
    }

    // ---- what a drag draws -----------------------------------------------------------------------

    private static readonly IBrush BankDropBrush = new SolidColorBrush(Color.FromRgb(90, 170, 255));
    private static readonly IBrush BankDropFill = new SolidColorBrush(Color.FromArgb(50, 90, 170, 255));

    /// <summary>Drawn over the bank list, never hit-tested: the drop mark and the floating copy.</summary>
    private readonly Canvas _bankDragLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
    private Border? _bankDropMark;
    private Border? _bankGhost;
    private TextBlock? _bankGhostText;

    /// <summary>The bank list with its drag layer on top.</summary>
    private Control BankListWithDragLayer() => new Panel { Children = { _animations, _bankDragLayer } };

    /// <summary>
    /// Shows the drag at the pointer: a floating copy of <paramref name="moving"/>, and where the drop
    /// would land - a frame round the folder it goes into, or a line between the rows it goes
    /// between (free sort). <paramref name="valid"/> false shows only the copy.
    /// </summary>
    private void ShowBankDrag(PointerEventArgs e, SceneObject dragged, IReadOnlyCollection<SceneObject> moving,
        SceneObject? target, bool place, bool valid)
    {
        if (_bankGhost == null)
        {
            _bankGhostText = new TextBlock { FontSize = 11.0, Foreground = Brushes.White };
            _bankGhost = new Border
            {
                Child = _bankGhostText,
                Background = new SolidColorBrush(Color.FromArgb(200, 45, 60, 80)),
                BorderBrush = BankDropBrush,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(3.0),
                Padding = new Thickness(6.0, 2.0),
                Opacity = 0.85,
            };
            _bankDropMark = new Border { BorderBrush = BankDropBrush };
            _bankDragLayer.Children.Add(_bankDropMark);
            _bankDragLayer.Children.Add(_bankGhost);
        }
        string glyph = dragged is Folder ? "▸ " : "";
        _bankGhostText!.Text = glyph + (dragged.Name ?? "(unnamed)") + (moving.Count > 1 ? $"  (+{moving.Count - 1})" : "");
        Point p = e.GetPosition(_bankDragLayer);
        Canvas.SetLeft(_bankGhost, p.X + 14.0);
        Canvas.SetTop(_bankGhost, p.Y + 4.0);
        _bankGhost.IsVisible = true;

        Rect? mark = valid && target != null ? BankDropRect(e, target, place) : null;
        if (mark is not Rect r)
        {
            _bankDropMark!.IsVisible = false;
            return;
        }
        bool line = place;
        _bankDropMark!.BorderThickness = line ? new Thickness(0.0, 2.0, 0.0, 0.0) : new Thickness(1.0);
        _bankDropMark.Background = line ? null : BankDropFill;
        _bankDropMark.CornerRadius = new CornerRadius(line ? 0.0 : 2.0);
        Canvas.SetLeft(_bankDropMark, r.X);
        Canvas.SetTop(_bankDropMark, r.Y);
        _bankDropMark.Width = r.Width;
        _bankDropMark.Height = r.Height;
        _bankDropMark.IsVisible = true;
    }

    private void HideBankDrag()
    {
        if (_bankGhost != null) _bankGhost.IsVisible = false;
        if (_bankDropMark != null) _bankDropMark.IsVisible = false;
    }

    /// <summary>
    /// Where the drop mark goes, in the drag layer: the target folder's row (the whole list for the
    /// bank itself), or for a placement a 2-pixel band at the top or bottom edge of the row under the
    /// pointer, indented to that row. Null when the row is not on screen.
    /// </summary>
    private Rect? BankDropRect(PointerEventArgs e, SceneObject target, bool place)
    {
        double width = _bankDragLayer.Bounds.Width;
        if (!place)
        {
            if (target == _character?.AnimBank)
            {
                return new Rect(1.0, 1.0, Math.Max(0.0, width - 2.0), Math.Max(0.0, _bankDragLayer.Bounds.Height - 2.0));
            }
            return BankRowRect(target) is Rect folderRow ? folderRow.Deflate(new Thickness(1.0, 0.0)) : null;
        }
        (ListBoxItem? row, Item<SceneObject>? item, double f) = BankRowControlAt(e);
        bool below = f >= 0.5;
        if (row == null)
        {
            // Below the last row: the end of the bank.
            var items = _animations.ItemsSource as IEnumerable<Item<SceneObject>>;
            item = items?.LastOrDefault();
            row = item != null ? _animations.ContainerFromItem(item) as ListBoxItem : null;
            below = true;
            if (row == null) return null;
        }
        else if (item?.Value == null)
        {
            below = true; // the rest pose row: the start of the bank
        }
        if (row.TranslatePoint(new Point(0.0, 0.0), _bankDragLayer) is not Point origin) return null;
        // Indent to the depth the dropped rows will get: the row's own (a bank-root drop is depth 0).
        double indent = item?.Value == null ? 0.0 : item.Depth * 16.0;
        double y = origin.Y + (below ? row.Bounds.Height : 0.0) - 1.0;
        return new Rect(indent + 4.0, y, Math.Max(0.0, width - indent - 8.0), 2.0);
    }

    /// <summary>The on-screen rectangle of <paramref name="value"/>'s row in the drag layer, or null.</summary>
    private Rect? BankRowRect(SceneObject value)
    {
        var item = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?.FirstOrDefault(i => i.Value == value);
        if (item == null || _animations.ContainerFromItem(item) is not Control row) return null;
        if (row.TranslatePoint(new Point(0.0, 0.0), _bankDragLayer) is not Point origin) return null;
        return new Rect(origin, row.Bounds.Size);
    }

    /// <summary>The row control under the pointer, its item and how far down it the pointer is.</summary>
    private (ListBoxItem? Row, Item<SceneObject>? Item, double Fraction) BankRowControlAt(PointerEventArgs e)
    {
        for (Visual? visual = _animations.InputHitTest(e.GetPosition(_animations)) as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem row && row.DataContext is Item<SceneObject> item)
            {
                double height = row.Bounds.Height;
                return (row, item, height > 0 ? Math.Clamp(e.GetPosition(row).Y / height, 0.0, 1.0) : 0.5);
            }
        }
        return (null, null, 0.5);
    }
}
