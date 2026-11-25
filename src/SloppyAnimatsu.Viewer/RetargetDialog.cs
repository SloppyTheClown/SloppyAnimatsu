using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Animation Retargeting, laid out in three
/// columns: <b>Source Skeleton</b> (its controllers, one row each), <b>Target Skeleton</b> (the
/// target controllers each source row drives, auto-matched by name, an empty cell reading "Drag here")
/// and the <b>Joint Bank</b> (the target controllers no row holds). Names are dragged between a row's
/// target cell and the bank, or from one row to another; a row holds several targets side by side
/// (one source driving the ring and the little finger), a drop on a chip swaps it, and a double click
/// on a chip sends its name back to the bank. The <b>Mirror L/R</b> toggle at the top right repeats
/// each edit on the other side. The window is modeless, so the scene stays usable beside it. The
/// rules - the characters offered, the default pair, the matching, the moves, the mirror, the
/// refusals and the stored matching - are <see cref="RetargetSession"/>; this window only draws them
/// and hands the confirmed pair to the caller, which owns the undo step.
/// </summary>
public sealed class RetargetDialog : Window
{
    private const string JointFormat = "sloppyanimatsu/retarget-joint";
    private const string FromRowFormat = "sloppyanimatsu/retarget-from-row";

    private static readonly IBrush CellBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(90, 90, 160, 255));
    private static readonly IBrush EmptyText = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

    private readonly RetargetSession _session;
    private readonly Func<IProgress<RetargetProgress>, Task<RetargetingResult?>> _run;
    private readonly Action? _closed;
    private readonly ComboBox _source;
    private readonly ComboBox _target;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly StackPanel _bank = new() { Spacing = 2 };
    private readonly Border _bankArea;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.OrangeRed };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _progressText = new() { FontSize = 11, IsVisible = false };
    private bool _running;
    private readonly Button _retarget = new() { Content = "Retarget", Padding = new Thickness(14, 4) };
    private readonly CheckBox _mirror = new() { Content = "Mirror L/R", FontSize = 11 };
    private Point _pressAt;
    private Control? _pressed;

    /// <summary>The session this window edits.</summary>
    public RetargetSession Session => _session;

    /// <param name="session">The pair, the grid and the transfer.</param>
    /// <param name="run">Runs the transfer and returns its result, or null when it did not run.</param>
    /// <param name="closed">Called when the window closes.</param>
    public RetargetDialog(RetargetSession session, Func<IProgress<RetargetProgress>, Task<RetargetingResult?>> run, Action? closed = null)
    {
        _session = session;
        _run = run;
        _closed = closed;

        Title = "Animation Retargeting";
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 360;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var names = session.Characters.Select(character => character.Name).ToList();
        _source = new ComboBox
        {
            ItemsSource = names,
            SelectedIndex = Math.Max(0, names.IndexOf(session.Source?.Name ?? "")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _target = new ComboBox
        {
            ItemsSource = names,
            SelectedIndex = session.Target is { } target ? Math.Max(0, names.IndexOf(target.Name)) : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _source.SelectionChanged += (_, _) => ChoosePair();
        _target.SelectionChanged += (_, _) => ChoosePair();
        _retarget.HorizontalAlignment = HorizontalAlignment.Right;
        _retarget.Click += async (_, _) => await Confirm();

        // Header: the three column labels, the two skeleton combos and the Retarget button.
        var header = Columns();
        header.RowDefinitions = new RowDefinitions("Auto,Auto");
        header.RowSpacing = 4;
        header.Margin = new Thickness(12, 12, 12, 8);
        Place(header, Heading("Source Skeleton"), 0, 0);
        Place(header, Heading("Target Skeleton"), 0, 1);
        Place(header, _source, 1, 0);
        Place(header, _target, 1, 1);
        // The Joint Bank heading sits on the combos' line, level with the Retarget button.
        var bankHeading = Heading("Joint Bank");
        bankHeading.VerticalAlignment = VerticalAlignment.Center;
        var bankLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bankLine.Children.Add(bankHeading);
        Grid.SetColumn(_retarget, 1);
        bankLine.Children.Add(_retarget);
        Place(header, bankLine, 1, 2);
        // Mirror, top right: an edit on a side-named source repeats on the other side.
        _mirror.IsChecked = session.Mirror;
        _mirror.HorizontalAlignment = HorizontalAlignment.Right;
        _mirror.VerticalAlignment = VerticalAlignment.Bottom;
        _mirror.MinHeight = 0;
        _mirror.IsCheckedChanged += (_, _) => _session.Mirror = _mirror.IsChecked == true;
        ToolTip.SetTip(_mirror, "Mirror: matching a left or right source joint also matches the other side's " +
                                "joint to the other side's target (Thumb_Distal_L -> Thumb_3_L also sets " +
                                "Thumb_Distal_R -> Thumb_3_R). Unmatching and swapping mirror too.");
        Place(header, _mirror, 0, 2);

        // Footer: a small bar with the advice, the counts and any refusal.
        var close = new Button { Content = "Close", Padding = new Thickness(10, 2), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => Close();
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        // The advice popup about aligning reference poses, kept as a line of the bar.
        if (session.ShowAdvice)
            info.Children.Add(new TextBlock
            {
                Text = RetargetSession.Advice,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brushes.Gainsboro,
            });
        info.Children.Add(_summary);
        info.Children.Add(_problem);
        info.Children.Add(_progress);
        info.Children.Add(_progressText);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        bar.Children.Add(info);
        Grid.SetColumn(close, 1);
        bar.Children.Add(close);
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 6),
            Child = bar,
        };

        // Body: the source/target rows (scrolled together) beside the joint bank (scrolled alone).
        var body = Columns();
        body.Margin = new Thickness(12, 0, 12, 8);
        var rowsScroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetColumnSpan(rowsScroll, 2);
        body.Children.Add(rowsScroll);
        _bankArea = new Border
        {
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                Content = _bank,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
        };
        ToolTip.SetTip(_bankArea, "Target joints no source row uses. Drag one onto a row; drag a row's joint here to " +
                                  "unmatch it.");
        AcceptDrop(_bankArea, row: null);
        Grid.SetColumn(_bankArea, 2);
        body.Children.Add(_bankArea);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Closed += (_, _) => _closed?.Invoke();

        var chrome = new DialogChrome(this, DialogId.Retarget);
        chrome.Bind(
            (DialogStop.RetargetSource, _source),
            (DialogStop.RetargetTarget, _target),
            (DialogStop.RetargetRun, _retarget),
            (DialogStop.RetargetCancel, close));
        Rebuild();
    }

    private static Grid Columns() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*"),
        ColumnSpacing = 10,
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Bottom,
    };

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    /// <summary>A skeleton combo changed: the session takes the pair and the lists are rebuilt for it.</summary>
    private void ChoosePair()
    {
        if (_source.SelectedIndex >= 0 && _source.SelectedIndex < _session.Characters.Count)
            _session.Source = _session.Characters[_source.SelectedIndex];
        if (_target.SelectedIndex >= 0 && _target.SelectedIndex < _session.Characters.Count)
            _session.Target = _session.Characters[_target.SelectedIndex];
        Rebuild();
    }

    /// <summary>Redraws the rows, the bank and the bar from the session.</summary>
    private void Rebuild()
    {
        _rows.Children.Clear();
        foreach (var row in _session.Rows)
            _rows.Children.Add(RowLine(row.Source));

        _bank.Children.Clear();
        foreach (var joint in _session.UnusedTargets)
            _bank.Children.Add(Chip(joint, fromRow: null));
        if (_bank.Children.Count == 0)
            _bank.Children.Add(new TextBlock
            {
                Text = _session.TargetChoices.Count == 0 ? "" : "Every target joint is matched.",
                FontSize = 11,
                Foreground = EmptyText,
                Margin = new Thickness(4),
            });

        _summary.Text = _session.Summary;
        DisabledHint.SetEnabledBecause(_retarget, _session.CanRetarget && !_running, _running ? "A retarget is running." : _session.Refusal);
        string? problem = _session.Refusal
            ?? (_session.StoreError is { } error ? $"The mapping could not be saved: {error}" : null);
        _problem.Text = problem ?? "";
        _problem.IsVisible = problem != null;
    }

    /// <summary>
    /// One grid line: the source name and every target it drives, as chips side by side, followed by
    /// a drop slot ("Drag here" while the row is empty, "+" once it has a target). A joint dropped on
    /// a chip replaces that chip; dropped anywhere else on the line it is added as one more target.
    /// </summary>
    private Control RowLine(Object3D source)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, MinHeight = 24 };
        var label = new TextBlock
        {
            Text = source.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0),
        };
        ToolTip.SetTip(label, $"{source.Name}: drop target joints on this row. A source can drive several targets.");
        line.Children.Add(label);

        var targets = _session.TargetsOf(source);
        var cell = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var target in targets)
        {
            var chip = Chip(target, source);
            chip.Margin = new Thickness(0, 1, 4, 1);
            var held = target;
            chip.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                Edit(() => _session.RemoveTarget(source, held));
            };
            AcceptDrop(chip, source, replacing: held);
            cell.Children.Add(chip);
        }
        cell.Children.Add(AddSlot(empty: targets.Count == 0));
        Grid.SetColumn(cell, 1);
        line.Children.Add(cell);
        line.Background = Brushes.Transparent;
        // The whole row takes a drop, not only its cell; a drop there adds a target.
        AcceptDrop(line, source, replacing: null);
        return line;
    }

    /// <summary>The slot after a row's chips: a grayed "Drag here" on an empty row, a small "+" otherwise.</summary>
    private static Control AddSlot(bool empty)
    {
        var slot = new Border
        {
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 1),
            Height = 22,
            MinWidth = empty ? 120 : 22,
            Child = new TextBlock
            {
                Text = empty ? "Drag here" : "+",
                FontStyle = empty ? FontStyle.Italic : FontStyle.Normal,
                Foreground = EmptyText,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = empty ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(6, 0),
            },
        };
        ToolTip.SetTip(slot, empty
            ? "Drag a target joint here"
            : "Drag another target joint here: this source then drives it too");
        return slot;
    }

    /// <summary>A joint name that can be dragged; <paramref name="fromRow"/> is the source holding it, null in the bank.</summary>
    private Border Chip(Object3D joint, Object3D? fromRow)
    {
        var chip = new Border
        {
            Background = CellBrush,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 1),
            Height = 22,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = joint.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0),
            },
        };
        ToolTip.SetTip(chip, fromRow == null
            ? $"{joint.Name}: drag onto a source row to match it (a row can hold several)"
            : $"{joint.Name}: drag to another row, onto another chip to swap it, or to the Joint Bank; " +
              "double-click to unmatch.");
        Draggable(chip, fromRow, joint);
        return chip;
    }

    /// <summary>Starts a drag from <paramref name="control"/> carrying the joint and the row it came from.</summary>
    private void Draggable(Control control, Object3D? fromRow, Object3D joint)
    {
        control.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
            _pressed = control;
            _pressAt = e.GetPosition(this);
        };
        control.PointerReleased += (_, _) => _pressed = null;
        control.PointerMoved += async (_, e) =>
        {
            if (!ReferenceEquals(_pressed, control) || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
            var at = e.GetPosition(this);
            if (Math.Abs(at.X - _pressAt.X) < 4 && Math.Abs(at.Y - _pressAt.Y) < 4) return;
            _pressed = null;
            var data = new DataObject();
            data.Set(JointFormat, joint);
            if (fromRow != null) data.Set(FromRowFormat, fromRow);
            try
            {
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
            }
        };
    }

    /// <summary>
    /// Makes <paramref name="area"/> take a dragged joint: a row (<paramref name="row"/> its source)
    /// adds it as a target, or swaps it for <paramref name="replacing"/> when the drop is on that chip;
    /// the bank (<paramref name="row"/> null) unmatches it from the row it came from.
    /// </summary>
    private void AcceptDrop(Control area, Object3D? row, Object3D? replacing = null)
    {
        DragDrop.SetAllowDrop(area, true);
        IBrush? rest = (area as Panel)?.Background ?? (area as Border)?.Background;
        void Paint(IBrush? brush)
        {
            if (area is Panel panel) panel.Background = brush;
            else if (area is Border border) border.Background = brush;
        }
        area.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool ok = e.Data.Get(JointFormat) is Object3D joint
                      && (row != null
                          ? !ReferenceEquals(e.Data.Get(FromRowFormat), row) && !ReferenceEquals(joint, replacing)
                          : e.Data.Get(FromRowFormat) is Object3D);
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            Paint(ok ? HoverBrush : rest);
            e.Handled = true;
        });
        area.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Paint(rest));
        area.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Paint(rest);
            e.Handled = true;
            if (e.Data.Get(JointFormat) is not Object3D joint) return;
            var from = e.Data.Get(FromRowFormat) as Object3D;
            if (row == null)
            {
                if (from != null) Edit(() => _session.RemoveTarget(from, joint));
                return;
            }
            if (ReferenceEquals(from, row)) return;
            if (replacing != null) Edit(() => _session.ReplaceTarget(row, replacing, joint));
            else Edit(() => _session.AddTarget(row, joint));
        });
    }

    /// <summary>Runs one session edit and redraws when it changed the grid.</summary>
    private void Edit(Func<bool> edit)
    {
        if (edit()) Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>
    /// Retarget: run the transfer as one undo step, with a progress bar over the converted
    /// animation stacks, and close when it ran.
    /// </summary>
    private async Task Confirm()
    {
        if (_running) return;
        if (_session.Refusal is { } refusal)
        {
            _problem.Text = refusal;
            _problem.IsVisible = true;
            return;
        }
        _running = true;
        DisabledHint.SetEnabledBecause(_retarget, false, "A retarget is running.");
        _problem.IsVisible = false;
        _progress.Value = 0;
        _progress.IsVisible = _progressText.IsVisible = true;
        _progressText.Text = "Retargeting...";
        var progress = new Progress<RetargetProgress>(p =>
        {
            _progress.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            _progressText.Text = p.Total > 0
                ? $"Retargeting {p.Done}/{p.Total} stack(s){(p.Animation is { } name ? $" - {name}" : "")}"
                : "Retargeting...";
        });
        RetargetingResult? result;
        try
        {
            result = await _run(progress);
        }
        finally
        {
            _running = false;
            _progress.IsVisible = _progressText.IsVisible = false;
            DisabledHint.SetEnabledBecause(_retarget, _session.CanRetarget, _session.Refusal);
        }
        if (result is null)
        {
            _problem.Text = "The retarget did not run; the status line says why.";
            _problem.IsVisible = true;
            return;
        }
        Close();
    }
}
