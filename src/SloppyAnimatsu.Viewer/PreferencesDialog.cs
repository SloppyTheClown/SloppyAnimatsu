using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The preferences dialog
/// the left list is 2020-3-13's flat, alphabetical list of page
/// titles - Rig Atelier also shows the IK page - and the right side draws that row's curated rows with
/// The hand-written labels: check boxes for bools and flags bits, numeric fields, colour swatches,
/// the combo boxes with their option names, text fields and the one hyperlink. Which rows a
/// page has, what it draws and when they are written into the document is
/// <see cref="PreferencesSession"/> and <see cref="PreferenceLayout"/>; this window only draws that and
/// types into it.
/// </summary>
public sealed class PreferencesDialog : Window
{
    private readonly PreferencesSession _session;
    private readonly Action<bool> _saved;
    private readonly ListBox _pageList = new() { Width = 260 };
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };
    private readonly Button _save = new() { Content = "Save" };
    private readonly Button _cancel = new() { Content = "Cancel" };
    private readonly List<(PreferenceRow Row, Control Control, Func<string?> Text)> _editors = [];
    private readonly DialogChrome _chrome;
    private bool _switching;

    private PreferencesDialog(
        Preferences preferences, Action<bool> saved, string? startClass, int? sceneFrameRate, Scene? scene)
    {
        _session = new PreferencesSession(preferences, sceneFrameRate, scene);
        _saved = saved;
        Title = "Preferences";
        Width = 900;
        Height = 660;
        MinWidth = 680;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _pageList.ItemsSource = _session.Entries.Select(entry => entry.Title).ToList();
        _pageList.SelectionChanged += (_, _) => { if (!_switching) SwitchPage(_pageList.SelectedIndex); };

        _save.Click += (_, _) => SaveAndClose();
        _cancel.Click += (_, _) => { _session.Restore(); Close(); };

        var header = new TextBlock
        {
            Text = $"Preferences ({preferences.Path})",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 16,
        };
        Grid.SetColumn(_pageList, 0);
        body.Children.Add(_pageList);
        _scroll.Content = _rows;
        Grid.SetColumn(_scroll, 1);
        body.Children.Add(_scroll);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(_status, 0);
        footer.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_save);
        buttons.Children.Add(_cancel);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        Content = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children = { header, body, footer },
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);

        // Closing any other way (the title bar's X, Escape through Cancel) behaves like Cancel:
        // the values captured when the dialog opened are put back. Only Save keeps the edits.
        Closing += (_, _) =>
        {
            if (!_session.SavedOnClose) _session.Restore();
        };

        _status.Text = _session.Status;
        _chrome = new DialogChrome(this, DialogId.Preferences);
        BindChrome();
        if (_session.Entries.Count > 0)
        {
            int start = startClass is null ? _session.Page : _session.IndexOf(startClass);
            if (start < 0) start = _session.Page;
            _session.SelectPage(start, []);
            _pageList.SelectedIndex = start;
            ShowPage(start);
        }
    }

    /// <summary>
    /// Shows the dialog and waits for it to close, on the row that shows <paramref name="startClass"/>.
    /// <paramref name="sceneFrameRate"/> is the open scene's rate for Timebar's *Scene Frame Rate* row,
    /// and <paramref name="scene"/> its values for the scene-backed rows; <paramref name="saved"/>
    /// is told whether a scene-backed row was written, so the window can mark the document dirty.
    /// </summary>
    public static Task ShowFor(
        Window owner, Preferences preferences, Action<bool> saved, string? startClass = null,
        int? sceneFrameRate = null, Scene? scene = null) =>
        new PreferencesDialog(preferences, saved, startClass, sceneFrameRate, scene).ShowDialog(owner);

    /// <summary>The edits the visible page sends: only the rows whose text changed (see <see cref="PreferencesSession.Changed"/>).</summary>
    private List<PreferenceEdit> Editors() =>
        _session.Entries.Count == 0
            ? []
            : PreferencesSession.Changed(_editors.Select(editor => (editor.Row, editor.Text()))).ToList();

    private void SwitchPage(int index)
    {
        switch (_session.SelectPage(index, Editors()))
        {
            case PreferencePageChange.Moved:
                ShowPage(_session.Page);
                break;
            case PreferencePageChange.Refused:
                // A bad value keeps the page open instead of losing the edit.
                _status.Text = _session.Status;
                _switching = true;
                _pageList.SelectedIndex = _session.Page;
                _switching = false;
                break;
        }
    }

    /// <summary>The tab order of the controls now on screen: the page list, the page's widgets, then Save and Cancel.</summary>
    private void BindChrome()
    {
        var controls = new List<(DialogStop, Control)> { (DialogStop.PageList, _pageList) };
        controls.AddRange(_editors.Select(editor => (DialogStop.PageValue, editor.Control)));
        controls.Add((DialogStop.PageSave, _save));
        controls.Add((DialogStop.PageCancel, _cancel));
        _chrome.Bind(controls.ToArray());
    }

    private void ShowPage(int index)
    {
        _editors.Clear();
        _rows.Children.Clear();
        var rows = _session.Rows(index);
        if (rows.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = PreferencesSession.NoValuesText });
            BindChrome();
            return;
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("330,*"),
            ColumnSpacing = 8,
            RowSpacing = 4,
        };
        var boxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        var dependents = new List<(PreferenceRow Row, CheckBox Box)>();
        int row = 0;
        foreach (var preferenceRow in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Text = preferenceRow.Label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            ToolTip.SetTip(label, Tooltip(preferenceRow));
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var editor = Editor(preferenceRow);
            Grid.SetRow(editor.Control, row);
            Grid.SetColumn(editor.Control, 1);
            grid.Children.Add(editor.Control);
            _editors.Add((preferenceRow, editor.Control, editor.Text));
            if (editor.Control is CheckBox check)
            {
                boxes[preferenceRow.Label] = check;
                if (preferenceRow.EnabledBy is { } source) dependents.Add((preferenceRow, check));
            }
            row++;
        }

        // Stacker's "Ghosts always wire" is dimmed unless "Ghosts on playing" is checked.
        foreach (var (dependent, box) in dependents)
        {
            if (!boxes.TryGetValue(dependent.EnabledBy!, out var source)) continue;
            box.IsEnabled = source.IsChecked == true;
            source.IsCheckedChanged += (_, _) => box.IsEnabled = source.IsChecked == true;
        }

        _rows.Children.Add(grid);
        BindChrome();
        _scroll.Offset = new Vector(0, 0);
    }

    /// <summary>The row's tooltip: the member it edits, the type, and what the measurement could not settle.</summary>
    private static string Tooltip(PreferenceRow row)
    {
        var parts = new List<string>();
        parts.Add(row.Member is { } member
            ? $"{row.ClassName}.{member} ({row.TypeName})"
            : $"{row.ClassName ?? "preferences"}: no editable member");
        if (row.IsDefault) parts.Add("drawn as the tool's default (the file stores ~0)");
        if (row.Metres) parts.Add("drawn in metres (the file stores centimetres)");
        if (row.Widget == PreferenceRowWidget.Combo)
            parts.Add("options: " + string.Join(", ", row.OptionNames));
        if (row.Note is { } note) parts.Add(note);
        return string.Join("\n", parts);
    }

    /// <summary>The widget for one row, as the app draws that kind of value.</summary>
    private static (Control Control, Func<string?> Text) Editor(PreferenceRow row)
    {
        switch (row.Widget)
        {
            case PreferenceRowWidget.Link:
            {
                var link = new TextBlock
                {
                    Text = row.Label,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xA9, 0xFF)),
                    TextDecorations = TextDecorations.Underline,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                };
                return (link, () => null);
            }
            case PreferenceRowWidget.Check:
            {
                var check = new CheckBox
                {
                    IsChecked = string.Equals(row.Text, "True", StringComparison.OrdinalIgnoreCase),
                    IsEnabled = row.Editable,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return (check, () => check.IsChecked == true ? "True" : "False");
            }
            case PreferenceRowWidget.Color:
            {
                var box = new TextBox
                {
                    Text = row.Text,
                    FontSize = 11,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var swatch = new Border
                {
                    Width = 18,
                    Height = 18,
                    Background = new SolidColorBrush(ParseColor(box.Text)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                box.TextChanged += (_, _) => swatch.Background = new SolidColorBrush(ParseColor(box.Text));
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                panel.Children.Add(swatch);
                panel.Children.Add(box);
                return (panel, () => box.Text ?? "");
            }
            case PreferenceRowWidget.Combo:
            {
                var names = row.OptionNames.ToList();
                var combo = new ComboBox
                {
                    ItemsSource = names,
                    FontSize = 11,
                    MinWidth = 220,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                int selected = row.OptionIndex;
                if (selected >= 0) combo.SelectedIndex = selected;
                else if (row.Field != null) combo.PlaceholderText = "value " + row.Text;
                return (combo, () => combo.SelectedIndex >= 0 ? names[combo.SelectedIndex] : null);
            }
            default:
            {
                var box = new TextBox
                {
                    Text = row.Text,
                    FontSize = 11,
                    IsReadOnly = row.Field == null,
                    MinWidth = row.Widget == PreferenceRowWidget.Number ? 180 : 320,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                return (box, () => box.Text ?? "");
            }
        }
    }

    /// <summary>An <c>RRGGBBAA</c> preference colour; anything else draws the neutral swatch.</summary>
    private static Color ParseColor(string? text)
    {
        string s = (text ?? "").Trim().TrimStart('#');
        if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint value))
            return Color.FromArgb((byte)(value & 0xFF), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8));
        return Color.FromArgb(255, 80, 80, 80);
    }

    private void SaveAndClose()
    {
        if (!_session.TrySave(Editors()))
        {
            _status.Text = _session.Status;
            return;
        }
        _saved(_session.SceneEdited);
        Close();
    }
}
