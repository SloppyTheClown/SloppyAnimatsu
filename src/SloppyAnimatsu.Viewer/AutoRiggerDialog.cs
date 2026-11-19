using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The AutoRigger window: the
/// character's meshes, the naming set and counts, a row per hot point (the fifteen drag
/// labels) and Generate. It is not modal: a row's button arms the next viewport click, which
/// <see cref="MainWindow"/> turns into <see cref="AutoRiggerSession.Place"/>; the session owns every
/// rule, so this window only draws it.
/// </summary>
public sealed class AutoRiggerDialog : Window
{
    private readonly AutoRiggerSession _session;
    private readonly Action<HotPoint> _arm;
    private readonly Func<AutoRigResult?> _build;
    private readonly Action _pointsChanged;
    private readonly Action _closed;
    private readonly Dictionary<HotPoint, Button> _buttons = [];
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _prompt = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
    private readonly Button _generate = new() { Content = "Generate rig", Padding = new Thickness(14, 4) };
    private readonly CheckBox[] _meshBoxes;
    private readonly ComboBox _naming = new() { Width = 200 };
    private readonly NumericUpDown _spine = new() { Width = 100, Minimum = 1, Maximum = 6, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _fingers = new() { Width = 100, Minimum = 1, Maximum = 10, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _phalanx = new() { Width = 100, Minimum = 1, Maximum = 4, Increment = 1, FormatString = "0" };
    private bool _syncing;

    public AutoRiggerDialog(AutoRiggerSession session, IEnumerable<Mesh> availableMeshes,
        Action<HotPoint> arm, Func<AutoRigResult?> build, Action pointsChanged, Action closed)
    {
        _session = session;
        _arm = arm;
        _build = build;
        _pointsChanged = pointsChanged;
        _closed = closed;
        var meshes = availableMeshes.Distinct().ToList();

        Title = $"AutoRigger — {session.Character.Name}";
        Width = 640;
        Height = 740;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---- the character's meshes ----------------------------------------------------------
        _meshBoxes = meshes.Select(mesh => new CheckBox
        {
            Content = new TextBlock { Text = mesh.Name }, // a bare string loses its first '_' (access key)
            IsChecked = session.Meshes.Contains(mesh),
            Margin = new Thickness(0, 0, 12, 0),
        }).ToArray();
        foreach (var box in _meshBoxes)
            box.IsCheckedChanged += (_, _) =>
            {
                _session.SetMeshes(meshes.Where((_, index) => _meshBoxes[index].IsChecked == true));
                Refresh();
            };
        var meshPanel = new WrapPanel();
        foreach (var box in _meshBoxes) meshPanel.Children.Add(box);

        // ---- naming set and counts -----------------------------------------------------------
        _naming.ItemsSource = new[] { "Default names", "Mixamo names", "Preferences names" };
        _naming.SelectedIndex = (int)session.Naming;
        _naming.SelectionChanged += (_, _) =>
        {
            if (_syncing || _naming.SelectedIndex < 0) return;
            _session.Naming = (AutoRigNaming)_naming.SelectedIndex;
            Refresh();
        };
        _spine.Value = session.SpineJoints;
        _fingers.Value = session.Fingers;
        _phalanx.Value = session.PhalanxJoints;
        _spine.ValueChanged += (_, _) => { if (!_syncing && _spine.Value is { } v) { _session.SpineJoints = (int)v; Refresh(); } };
        _fingers.ValueChanged += (_, _) => { if (!_syncing && _fingers.Value is { } v) { _session.Fingers = (int)v; Refresh(); } };
        _phalanx.ValueChanged += (_, _) => { if (!_syncing && _phalanx.Value is { } v) { _session.PhalanxJoints = (int)v; Refresh(); } };

        var settings = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 6,
        };
        Add(settings, new TextBlock { Text = "Skeleton", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Grid.SetColumnSpan(_naming, 3);
        Add(settings, _naming, 0, 1);
        Add(settings, new TextBlock { Text = "Spine", VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Add(settings, _spine, 1, 1);
        Add(settings, new TextBlock { Text = "Fingers", VerticalAlignment = VerticalAlignment.Center }, 1, 2);
        Add(settings, _fingers, 1, 3);
        Add(settings, new TextBlock { Text = "Phalanx", VerticalAlignment = VerticalAlignment.Center }, 1, 4);
        Add(settings, _phalanx, 1, 5);

        // ---- the hot points ------------------------------------------------------------------
        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*,*"),
            RowDefinitions = new RowDefinitions(
                string.Join(",", Enumerable.Repeat("Auto", 1 + AutoRiggerParts.Rows.Count))),
            RowSpacing = 4,
            ColumnSpacing = 6,
        };
        int row = 0;
        Add(table, new TextBlock { Text = "Hot point", Opacity = 0.7, FontSize = 11 }, row, 0);
        Add(table, new TextBlock { Text = "Left", Opacity = 0.7, FontSize = 11 }, row, 1);
        Add(table, new TextBlock { Text = "Right", Opacity = 0.7, FontSize = 11 }, row, 2);
        row++;
        foreach (AutoRigPartRow part in AutoRiggerParts.Rows)
        {
            Add(table, new TextBlock { Text = part.Label, VerticalAlignment = VerticalAlignment.Center }, row, 0);
            if (part.HasSides)
            {
                Add(table, PartButton(part.Left!.Value), row, 1);
                Add(table, PartButton(part.Right!.Value), row, 2);
            }
            else
            {
                var button = PartButton(part.Centre!.Value);
                Grid.SetColumnSpan(button, 2);
                Add(table, button, row, 1);
            }
            row++;
        }

        // ---- the buttons ---------------------------------------------------------------------
        // The Generate IK is gated by the "RIGID AND SMOOTH MODIFIED" prompt when the character's rigid
        // and smooth versions were both edited
        // the session decides whether it applies, this window only asks it.
        _generate.Click += async (_, _) =>
        {
            if (_session.MustChooseWeightVersion)
            {
                AutoRigWeightVersion? answer = await SmoothAgainDialog.Ask(this);
                if (answer is not { } version) return;
                _session.WeightVersion = version;
                Refresh();
            }
            AutoRigResult? result = _build();
            if (result != null) Close();
        };
        // The hot points are placed per side, as the drag list offers the same labels to both;
        // these copy one placed side onto the other with the position reflected about the
        // character's local YZ plane. The session holds the rule; this window only reports it.
        var mirrorLeftToRight = new Button { Content = "Mirror L → R", Padding = new Thickness(10, 4) };
        mirrorLeftToRight.Click += (_, _) => MirrorPoints(_session.MirrorLeftToRight, "left", "right");
        var mirrorRightToLeft = new Button { Content = "Mirror R → L", Padding = new Thickness(10, 4) };
        mirrorRightToLeft.Click += (_, _) => MirrorPoints(_session.MirrorRightToLeft, "right", "left");
        var clear = new Button { Content = "Clear points", Padding = new Thickness(10, 4) };
        clear.Click += (_, _) =>
        {
            _session.Clear();
            _pointsChanged();
            Refresh();
            SetPrompt("Cleared every hot point.");
        };
        var close = new Button { Content = "Close", Padding = new Thickness(10, 4) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { mirrorLeftToRight, mirrorRightToLeft, clear, close, _generate },
        };

        var header = new StackPanel
        {
            Margin = new Thickness(14, 12, 14, 6),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"Build a rig for {session.Character.Name}. Pick the skin meshes, then click a " +
                           "hot point's button and the character in the viewport.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                },
                meshPanel,
                settings,
            },
        };
        var footer = new StackPanel
        {
            Margin = new Thickness(14, 6, 14, 12),
            Spacing = 8,
            Children = { _prompt, _state, buttons },
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var root = new DockPanel();
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Margin = new Thickness(14, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = table,
        });
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        Opened += (_, _) => _meshBoxes.FirstOrDefault()?.Focus();
        Closed += (_, _) => _closed();
        Refresh();
    }

    private Button PartButton(HotPoint point)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3),
            FontSize = 11,
        };
        button.Click += (_, _) =>
        {
            _arm(point);
            SetArmed(point);
            SetPrompt($"Click the character in the viewport to place {AutoRiggerParts.Describe(point)}; Escape cancels.");
        };
        _buttons[point] = button;
        return button;
    }

    /// <summary>Runs one mirroring gesture, redraws the table and markers, and says what it wrote.</summary>
    private void MirrorPoints(Func<AutoRigMirrorResult> mirror, string from, string to)
    {
        AutoRigMirrorResult result = mirror();
        _pointsChanged();
        Refresh();
        SetPrompt(result.Mirrored == 0
            ? $"No {from} hot point placed yet: place one on each row to mirror, then press Mirror."
            : $"Mirrored {result.Mirrored} {from} hot point(s) onto the {to} side" +
              (result.Replaced > 0 ? $", replacing {result.Replaced} already placed." : "."));
    }

    private static void Add(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    /// <summary>Highlights the row whose point the next viewport click places.</summary>
    public void SetArmed(HotPoint? point)
    {
        foreach (var (kind, button) in _buttons)
            button.FontWeight = kind == point ? FontWeight.Bold : FontWeight.Normal;
    }

    /// <summary>The line above the state: what the viewport click is for, or null.</summary>
    public void SetPrompt(string? prompt)
    {
        _prompt.Text = prompt ?? "";
        _prompt.IsVisible = prompt != null;
    }

    /// <summary>Redraws the placed coordinates, the mesh count and the Generate state.</summary>
    public void Refresh()
    {
        _syncing = true;
        _spine.Value = _session.SpineJoints;
        _fingers.Value = _session.Fingers;
        _phalanx.Value = _session.PhalanxJoints;
        _naming.SelectedIndex = (int)_session.Naming;
        _syncing = false;

        foreach (var (point, button) in _buttons)
        {
            button.Content = _session.PositionOf(point) is { } position
                ? $"{position.X:0.#}, {position.Y:0.#}, {position.Z:0.#}"
                : "Place…";
            // The drag list only holds the labels of the configured fingers (5 + F of them),
            // so a finger row past the count is not offered at all; the points a trim dropped are
            // therefore not placeable again until the count grows.
            button.IsEnabled = _session.Required.Contains(point);
            button.Opacity = button.IsEnabled ? 1 : 0.6;
        }

        _generate.IsEnabled = _session.CanBuild;
        string meshes = _session.Meshes.Count == 1 ? "1 mesh" : $"{_session.Meshes.Count} meshes";
        _state.Text = _session.Refusal is { } refusal
            ? refusal
            : $"{_session.Points.Count} hot point(s) placed on {meshes}; ready to generate.";
        if (_session.MustChooseWeightVersion)
            _state.Text += " Rigid and smooth weights are both modified: Generate asks which version " +
                           $"to keep (currently {_session.WeightVersion}).";
        Title = $"AutoRigger — {_session.Character.Name}";
    }
}
