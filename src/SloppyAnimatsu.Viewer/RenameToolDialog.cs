using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The Rename Tool: a non-modal window with its five fields
/// that renames the selection of the panel used last (the scene tree or the Anim Bank). It stays open
/// so several selections can be renamed in a row; the preview shows what Rename will write before the
/// panel's unique-name rule numbers any clash.
/// </summary>
public sealed class RenameToolDialog : Window
{
    private readonly Func<(string Panel, IReadOnlyList<string> Names)> _targets;
    private readonly Action<RenameToolOptions> _apply;
    private readonly TextBox _search = Field("everything");
    private readonly TextBox _replace = Field("keep the name");
    private readonly TextBox _prefix = Field("");
    private readonly TextBox _suffix = Field("");
    private readonly TextBox _number = Field("e.g. _01");
    private readonly TextBlock _panel = new() { FontSize = 12, Foreground = Brushes.Gainsboro };
    private readonly TextBlock _preview = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "Rename", Padding = new Thickness(14, 4) };

    /// <param name="targets">The panel being renamed and the current names of its selection, in order.</param>
    /// <param name="apply">Renames that selection with the options (one undo step).</param>
    public RenameToolDialog(Func<(string Panel, IReadOnlyList<string> Names)> targets, Action<RenameToolOptions> apply)
    {
        _targets = targets;
        _apply = apply;
        Title = "Rename Tool";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 6,
            ColumnSpacing = 10,
        };
        Row(fields, 0, "Search for", _search, "Text to find in each name. Empty: the whole name.");
        Row(fields, 1, "Replace with", _replace, "Replaces what was found. Empty: the original name is kept.");
        Row(fields, 2, "Add prefix", _prefix, "Put in front of the name.");
        Row(fields, 3, "Add suffix", _suffix, "Put after the name.");
        Row(fields, 4, "Number suffix", _number,
            "Put after the suffix; its first block of digits counts up through the selection (_01, _02, ...). " +
            "Empty: names that clash are numbered automatically.");
        foreach (var box in new[] { _search, _replace, _prefix, _suffix, _number })
            box.TextChanged += (_, _) => Refresh();

        _run.Click += (_, _) => Apply();
        var close = new Button { Content = "Close", Padding = new Thickness(10, 4) };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 10,
            Children =
            {
                _panel,
                fields,
                new Border
                {
                    BorderBrush = Brushes.DimGray,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6),
                    MaxHeight = 140,
                    Child = new ScrollViewer { Content = _preview },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { close, _run },
                },
            },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && _run.IsEnabled) { Apply(); e.Handled = true; }
        };
        // Only the last activated window takes the rename, so coming back to the tool
        // shows that panel's current selection.
        Activated += (_, _) => Refresh();
        Opened += (_, _) => _search.Focus();
        Refresh();
    }

    /// <summary>The five fields as options.</summary>
    public RenameToolOptions Options => new(_search.Text ?? "", _replace.Text ?? "", _prefix.Text ?? "",
        _suffix.Text ?? "", _number.Text ?? "");

    /// <summary>Redraws the target line and the preview from the current selection.</summary>
    public void Refresh()
    {
        var (panel, names) = _targets();
        var options = Options;
        _panel.Text = names.Count == 0 ? $"{panel}: nothing selected." : $"{panel}: {names.Count} selected.";
        var after = RenameTool.Preview(names, options);
        _preview.Text = names.Count == 0
            ? "Select objects in the scene tree or items in the Anim Bank, then come back here."
            : string.Join("\n", names.Zip(after, (b, a) => b == a ? b : $"{b}  ->  {a}").Take(200));
        _run.IsEnabled = names.Count > 0 && names.Zip(after).Any(p => p.First != p.Second);
    }

    private void Apply()
    {
        _apply(Options);
        Refresh();
    }

    private static TextBox Field(string watermark) => new() { Watermark = watermark, MinWidth = 220 };

    private static void Row(Grid grid, int row, string label, TextBox box, string tip)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(text, tip);
        ToolTip.SetTip(box, tip);
        Grid.SetRow(text, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(text);
        grid.Children.Add(box);
    }
}
