using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Plugins.EzExports;

namespace SloppyAnimatsu.Viewer.Plugins.EzExports;

/// <summary>
/// The EZ Exports window (not modal): the active scene's presets on the left with their Run boxes,
/// the selected preset's editor on the right (name, destination, filename, items, settings) and what
/// running it writes. It follows the active scene: switching documents shows the other scene's
/// presets. Presets are reordered by dragging them in the list, as in the Anim Bank.
/// </summary>
public sealed class EzExportsWindow : Window
{
    private static readonly IBrush WarningForeground = new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x40));
    // The same blue as the Anim Bank's drop mark.
    private static readonly IBrush DropBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));

    /// <summary>How far (pixels) a press must move before it becomes a drag, as in the Anim Bank.</summary>
    private const double DragThreshold = 6.0;

    private readonly EzExportsPlugin _plugin;
    private readonly ListBox _presetList = new() { SelectionMode = SelectionMode.Single, MinHeight = 260, Width = 220 };
    private readonly Button _new = new() { Content = "New" };
    private readonly Button _duplicate = new() { Content = "Duplicate" };
    private readonly Button _delete = new() { Content = "Del" };
    private readonly Button _runChecked = new() { Content = "Run checked" };
    private readonly Button _runAll = new() { Content = "Run all" };

    private readonly TextBox _name = new();
    private readonly TextBox _folder = new() { Watermark = "(the scene's folder)" };
    private readonly Button _browse = new() { Content = "…", Padding = new Thickness(8, 2) };
    private readonly TextBox _key = new() { Watermark = "(the preset's name)" };
    private readonly ListBox _items = new() { SelectionMode = SelectionMode.Multiple, Height = 150 };
    private readonly Button _addSelected = new() { Content = "Add selected from Anim Bank" };
    private readonly Button _addCharacter = new() { Content = "Add selected character" };
    private readonly Button _remove = new() { Content = "Remove" };
    private readonly TextBlock _settings = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Opacity = 0.85, FontSize = 11 };
    private readonly Button _editSettings = new() { Content = "Edit…" };
    private readonly TextBlock _willWrite = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 11 };
    private readonly TextBlock _problems = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 11, Foreground = WarningForeground };
    private readonly Button _runThis = new() { Content = "Run this preset" };
    private readonly Control _editor;

    private SceneModel? _model;
    private bool _syncing;

    // Dragging a preset in the list to reorder it.
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Border _dropLine = new() { BorderBrush = DropBrush, BorderThickness = new Thickness(0, 2, 0, 0), Height = 2, IsVisible = false };
    private EzPreset? _dragPreset;
    private Point _dragStart;
    private bool _dragging;

    public EzExportsWindow(EzExportsPlugin plugin)
    {
        _plugin = plugin;
        Title = "EZ Exports";
        Width = 760;
        Height = 560;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        ToolTip.SetTip(_new, "Adds a preset holding the Anim Bank's selected animations and folders.");
        ToolTip.SetTip(_duplicate, "Adds a copy of the selected preset.");
        ToolTip.SetTip(_delete, "Deletes the selected preset.");
        ToolTip.SetTip(_runChecked, "Writes the files of every ticked preset, overwriting files already there.");
        ToolTip.SetTip(_runAll, "Writes the files of every preset, overwriting files already there.");
        ToolTip.SetTip(_presetList, "Drag a preset to reorder the list.");
        ToolTip.SetTip(_folder, "Where the files go. A relative folder is read from the scene file's folder; empty is that folder itself.");
        ToolTip.SetTip(_browse, "Choose the destination folder. One inside the scene's folder is kept relative, so it moves with the scene.");
        ToolTip.SetTip(_key, "The file name: <Filename>.fbx for one file, <Filename>_<Animation>.fbx or <Filename>_<Character>.fbx for several. Empty uses the preset's name.");
        ToolTip.SetTip(_items, "What the preset exports, under the character each belongs to. A character's (model) row writes its model without animations; an animation or folder row writes each animation under it. Items follow renames and moves; a deleted one shows as (missing) and is skipped.");
        ToolTip.SetTip(_addSelected, "Adds the Anim Bank's selected animations and folders to this preset.");
        ToolTip.SetTip(_addCharacter, "Adds the selected character's model (written without animations) to this preset: the characters of the TREE's selection, or else the Anim Bank's character.");
        ToolTip.SetTip(_remove, "Removes the selected rows from this preset; a character's row removes its model and everything listed under it (the scene itself is not touched).");
        ToolTip.SetTip(_editSettings, "The export settings this preset writes with: target, options and scale.");
        ToolTip.SetTip(_runThis, "Writes this preset's files, overwriting files already there.");

        _new.Click += delegate { NewPreset(); };
        _duplicate.Click += delegate { DuplicatePreset(); };
        _delete.Click += delegate { DeletePreset(); };
        _runChecked.Click += async delegate
        {
            if (_model != null) await _plugin.RunPresets(_model, Presets.Where(p => p.Checked).ToList());
        };
        _runAll.Click += async delegate
        {
            if (_model != null) await _plugin.RunPresets(_model, Presets.ToList());
        };
        _runThis.Click += async delegate
        {
            if (_model != null && Current is { } preset) await _plugin.RunPresets(_model, [preset]);
        };
        _presetList.SelectionChanged += delegate
        {
            if (!_syncing) ShowPreset();
        };
        _name.TextChanged += delegate
        {
            if (_syncing || Current is not { } preset) return;
            preset.Name = _name.Text ?? "";
            Edited(refreshList: true);
        };
        _folder.TextChanged += delegate
        {
            if (_syncing || Current is not { } preset) return;
            preset.Folder = _folder.Text ?? "";
            Edited();
        };
        _key.TextChanged += delegate
        {
            if (_syncing || Current is not { } preset) return;
            preset.Key = _key.Text ?? "";
            Edited();
        };
        _browse.Click += async delegate { await BrowseFolder(); };
        _addSelected.Click += delegate { AddSelected(); };
        _addCharacter.Click += delegate { AddSelectedCharacters(); };
        _presetList.AddHandler(InputElement.PointerPressedEvent, PresetPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _presetList.AddHandler(InputElement.PointerMovedEvent, PresetPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _presetList.AddHandler(InputElement.PointerReleasedEvent, PresetPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _presetList.AddHandler(InputElement.PointerCaptureLostEvent, delegate { EndDrag(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        _dragLayer.Children.Add(_dropLine);
        _remove.Click += delegate { RemoveItems(); };
        _items.SelectionChanged += delegate { UpdateButtons(); };
        _editSettings.Click += async delegate
        {
            if (Current is not { } preset) return;
            var settings = await _plugin.Host.EditExportSettings(preset.Settings);
            if (settings == null || Current != preset) return;
            preset.Settings = settings;
            Edited();
            ShowPreset();
        };

        var left = new DockPanel { Width = 220 };
        var listButtons = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _new, _duplicate, _delete } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _runChecked, _runAll } },
            },
        };
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(new Panel { Children = { _presetList, _dragLayer } });

        var folderRow = new DockPanel();
        DockPanel.SetDock(_browse, Dock.Right);
        folderRow.Children.Add(_browse);
        folderRow.Children.Add(_folder);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6, ColumnSpacing = 8 };
        AddRow(grid, 0, "Name", _name);
        AddRow(grid, 1, "Destination", folderRow);
        AddRow(grid, 2, "Filename", _key);

        _editor = new StackPanel
        {
            Spacing = 8,
            // Room on the right for the scroll bar, which otherwise covers the right-docked buttons.
            Margin = new Thickness(0, 0, 18, 0),
            Children =
            {
                grid,
                new TextBlock { Text = "Items", FontWeight = FontWeight.SemiBold },
                _items,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _addSelected, _addCharacter, _remove } },
                new TextBlock { Text = "Settings", FontWeight = FontWeight.SemiBold },
                new DockPanel { Children = { WithDock(_editSettings, Dock.Right), _settings } },
                new TextBlock { Text = "Will write", FontWeight = FontWeight.SemiBold },
                _willWrite,
                _problems,
                new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { _runThis } },
            },
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Margin = new Thickness(12, 0, 0, 0), Content = _editor });
        Content = root;

        ShowScene();
    }

    private List<EzPreset> Presets => _model == null ? [] : _plugin.Presets(_model);

    private EzPreset? Current => (_presetList.SelectedItem as PresetRow)?.Preset;

    /// <summary>Shows the active scene's presets (after a document switch, a close or a reload).</summary>
    public void ShowScene()
    {
        _model = _plugin.Host.Scene;
        Title = _model == null ? "EZ Exports" : "EZ Exports - " + (Path.GetFileName(_plugin.Host.ScenePath) is { Length: > 0 } name ? name : "unsaved scene");
        RebuildList(Presets.FirstOrDefault());
    }

    /// <summary>Re-reads everything shown (after a run, or a rename in the Anim Bank).</summary>
    public void Refresh() => RebuildList(Current);

    public void BankSelectionChanged() => UpdateButtons();

    private void RebuildList(EzPreset? select)
    {
        _syncing = true;
        var rows = Presets.Select(p => new PresetRow(p, this)).ToList();
        _presetList.ItemsSource = rows;
        _presetList.SelectedItem = rows.FirstOrDefault(r => r.Preset == select);
        _syncing = false;
        ShowPreset();
    }

    private void ShowPreset()
    {
        _syncing = true;
        var preset = Current;
        _editor.IsEnabled = preset != null;
        _name.Text = preset?.Name ?? "";
        _folder.Text = preset?.Folder ?? "";
        _key.Text = preset?.Key ?? "";
        _items.ItemsSource = preset == null ? [] : ItemRows(preset);
        _settings.Text = preset == null ? "" : Describe(preset.Settings);
        _syncing = false;
        ShowPlan();
        UpdateButtons();
    }

    /// <summary>The "Will write" lines and the preset's problems.</summary>
    private void ShowPlan()
    {
        if (_model == null || Current is not { } preset)
        {
            _willWrite.Text = _model == null ? "No scene is open." : "No preset selected.";
            _problems.IsVisible = false;
            return;
        }
        var plan = EzExportPlanner.Plan(_model, _plugin.Host.ScenePath, preset);
        const int shown = 12;
        var lines = plan.Files.Take(shown).Select(f => Path.GetFileName(f.Path)).ToList();
        if (plan.Files.Count > shown) lines.Add($"… and {plan.Files.Count - shown} more");
        _willWrite.Text = plan.Folder == null ? "—"
            : plan.Files.Count == 0 ? "Nothing, into " + plan.Folder
            : $"{plan.Files.Count} file(s) into {plan.Folder}\n" + string.Join("\n", lines);
        var problems = new List<string>();
        if (plan.Error != null) problems.Add(plan.Error);
        problems.AddRange(plan.Warnings);
        problems.AddRange(preset.Settings.Warnings());
        _problems.Text = string.Join("\n", problems.Select(p => "⚠ " + p));
        _problems.IsVisible = problems.Count > 0;
    }

    private void UpdateButtons()
    {
        bool scene = _model != null;
        bool preset = Current != null;
        _new.IsEnabled = scene;
        _duplicate.IsEnabled = preset;
        _delete.IsEnabled = preset;
        _runChecked.IsEnabled = Presets.Any(p => p.Checked);
        _runAll.IsEnabled = Presets.Count > 0;
        _runThis.IsEnabled = preset;
        _addSelected.IsEnabled = preset && BankSelection().Count > 0;
        _addCharacter.IsEnabled = preset && _model?.Scene.Characters.Any() == true;
        _remove.IsEnabled = preset && _items.SelectedItems?.Count > 0;
    }

    /// <summary>The Anim Bank's selected rows that belong to the scene shown here.</summary>
    private List<SceneObject> BankSelection() =>
        _model == null ? [] : _plugin.Host.AnimBankSelection.Where(o => o.Model == _model && o is Animation or Folder).ToList();

    private void Edited(bool refreshList = false)
    {
        if (_model == null) return;
        _plugin.Changed(_model);
        if (refreshList)
            foreach (var row in _presetList.ItemsSource?.OfType<PresetRow>() ?? []) row.Update();
        ShowPlan();
        UpdateButtons();
    }

    private void NewPreset()
    {
        if (_model == null) return;
        var preset = new EzPreset { Name = UniqueName("Preset") };
        preset.Items.AddRange(BankSelection());
        Presets.Add(preset);
        _plugin.Changed(_model);
        RebuildList(preset);
        _name.Focus();
        _name.SelectAll();
    }

    private void DuplicatePreset()
    {
        if (_model == null || Current is not { } preset) return;
        var copy = preset.Clone();
        copy.Name = UniqueName(preset.Name + " copy");
        Presets.Insert(Presets.IndexOf(preset) + 1, copy);
        _plugin.Changed(_model);
        RebuildList(copy);
    }

    private void DeletePreset()
    {
        if (_model == null || Current is not { } preset) return;
        int index = Presets.IndexOf(preset);
        Presets.Remove(preset);
        _plugin.Changed(_model);
        RebuildList(Presets.Count == 0 ? null : Presets[Math.Min(index, Presets.Count - 1)]);
    }

    private string UniqueName(string name)
    {
        string unique = name;
        for (int n = 2; Presets.Any(p => p.Name == unique); n++) unique = $"{name} {n}";
        return unique;
    }

    private void AddSelected()
    {
        if (Current is not { } preset) return;
        int added = 0;
        foreach (var item in BankSelection())
        {
            if (preset.Items.Contains(item)) continue;
            preset.Items.Add(item);
            added++;
        }
        if (added == 0) return;
        Edited();
        ShowPreset();
    }

    /// <summary>
    /// The characters "Add selected character" adds: those of the TREE's selection (a selected
    /// character, or the character a selected joint or mesh belongs to), in tree order; with none
    /// there, the Anim Bank's character.
    /// </summary>
    private List<Character> SelectedCharacters()
    {
        if (_model == null) return [];
        var result = new List<Character>();
        foreach (var o in _plugin.Host.SceneSelection)
            if (o.Model == _model && EzExportPlanner.CharacterOf(o) is { } c && !result.Contains(c)) result.Add(c);
        if (result.Count == 0 && _plugin.Host.ActiveCharacter is { } active && active.Model == _model) result.Add(active);
        return result;
    }

    private void AddSelectedCharacters()
    {
        if (Current is not { } preset) return;
        int added = 0;
        foreach (var character in SelectedCharacters())
        {
            if (preset.Items.Contains(character)) continue;
            preset.Items.Add(character);
            added++;
        }
        if (added == 0) return;
        Edited();
        ShowPreset();
    }

    private void RemoveItems()
    {
        if (Current is not { } preset || _items.SelectedItems == null) return;
        var indexes = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        foreach (var row in _items.SelectedItems.OfType<ItemRow>())
        {
            if (row.Index >= 0) indexes.Add(row.Index);
            // A character's row takes its model and everything listed under it.
            if (row.Item is Character character)
                for (int i = 0; i < preset.Items.Count; i++)
                    if (preset.Items[i] is { } item && EzExportPlanner.CharacterOf(item) == character) indexes.Add(i);
        }
        if (indexes.Count == 0) return;
        foreach (int i in indexes)
            if (i < preset.Items.Count) preset.Items.RemoveAt(i);
        Edited();
        ShowPreset();
    }

    /// <summary>
    /// The Items rows: one per character, in the order its first item was added (its "(model)" row
    /// when the character itself is an item, else a heading), with its animations and folders indented
    /// under it in item order; missing items and ones of no character last.
    /// </summary>
    private static List<ItemRow> ItemRows(EzPreset preset)
    {
        var groups = new List<(Character Character, List<int> Indexes)>();
        var loose = new List<int>();
        for (int i = 0; i < preset.Items.Count; i++)
        {
            var item = preset.Items[i];
            if (item is Character) continue;
            if (EzExportPlanner.CharacterOf(item) is not { } owner)
            {
                loose.Add(i);
                continue;
            }
            int g = groups.FindIndex(x => x.Character == owner);
            if (g < 0) groups.Add((owner, [i]));
            else groups[g].Indexes.Add(i);
        }
        // Order the characters by their first appearance, counting the character's own item too.
        int First(Character c)
        {
            int own = preset.Items.IndexOf(c);
            int child = groups.FirstOrDefault(x => x.Character == c).Indexes?.FirstOrDefault() ?? int.MaxValue;
            return own >= 0 ? Math.Min(own, child) : child;
        }
        var characters = preset.Items.OfType<Character>().Concat(groups.Select(x => x.Character)).Distinct().OrderBy(First).ToList();
        var rows = new List<ItemRow>();
        foreach (var character in characters)
        {
            rows.Add(new ItemRow(preset.Items.IndexOf(character), character, Heading: true));
            foreach (int i in groups.FirstOrDefault(x => x.Character == character).Indexes ?? [])
                rows.Add(new ItemRow(i, preset.Items[i], Heading: false));
        }
        rows.AddRange(loose.Select(i => new ItemRow(i, preset.Items[i], Heading: false)));
        return rows;
    }

    private void PresetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        if (!e.GetCurrentPoint(_presetList).Properties.IsLeftButtonPressed) return;
        _dragPreset = PresetRowAt(e.GetPosition(_presetList))?.Preset;
        _dragStart = e.GetPosition(_presetList);
    }

    private void PresetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPreset == null) return;
        Point p = e.GetPosition(_presetList);
        if (!_dragging && Point.Distance(p, _dragStart) < DragThreshold) return;
        _dragging = true;
        var (index, lineY) = DropIndex(p);
        _dropLine.Width = _dragLayer.Bounds.Width;
        Canvas.SetLeft(_dropLine, 0);
        Canvas.SetTop(_dropLine, Math.Max(0, lineY - 1));
        _dropLine.IsVisible = index >= 0;
    }

    private void PresetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var preset = _dragPreset;
        bool dragging = _dragging;
        var (index, _) = DropIndex(e.GetPosition(_presetList));
        EndDrag();
        if (!dragging || preset == null || _model == null || index < 0) return;
        // The release ends the drag; it must not also tick or untick the row's Run box.
        e.Handled = true;
        var presets = Presets;
        int from = presets.IndexOf(preset);
        if (from < 0) return;
        int to = index > from ? index - 1 : index;
        if (to == from) return;
        presets.RemoveAt(from);
        presets.Insert(to, preset);
        _plugin.Changed(_model);
        RebuildList(preset);
    }

    private void EndDrag()
    {
        _dragPreset = null;
        _dragging = false;
        _dropLine.IsVisible = false;
    }

    /// <summary>The preset row under <paramref name="p"/> (in list coordinates), or null.</summary>
    private PresetRow? PresetRowAt(Point p)
    {
        foreach (var (row, bounds) in RowBounds())
            if (p.Y >= bounds.Top && p.Y < bounds.Bottom) return row;
        return null;
    }

    /// <summary>
    /// Where a drop at <paramref name="p"/> inserts: the index in the preset list (before the row whose
    /// upper half is under the pointer, after it for the lower half) and the y of the line marking it.
    /// -1 when the list shows no rows.
    /// </summary>
    private (int Index, double LineY) DropIndex(Point p)
    {
        var rows = RowBounds();
        if (rows.Count == 0) return (-1, 0);
        for (int i = 0; i < rows.Count; i++)
        {
            var b = rows[i].Bounds;
            if (p.Y < b.Top + b.Height / 2) return (i, b.Top);
        }
        return (rows.Count, rows[^1].Bounds.Bottom);
    }

    // Each shown preset row with its bounds in the list's coordinates, top to bottom.
    private List<(PresetRow Row, Rect Bounds)> RowBounds()
    {
        var result = new List<(PresetRow, Rect)>();
        if (_presetList.ItemsSource is not IEnumerable<PresetRow> rows) return result;
        int i = 0;
        foreach (var row in rows)
        {
            if (_presetList.ContainerFromIndex(i++) is { } container
                && container.TranslatePoint(new Point(0, 0), _presetList) is { } topLeft)
                result.Add((row, new Rect(topLeft, container.Bounds.Size)));
        }
        return result;
    }

    private async Task BrowseFolder()
    {
        if (Current is not { } preset) return;
        string? start = EzExportPlanner.ResolveFolder(preset.Folder, _plugin.Host.ScenePath, out _);
        var options = new FolderPickerOpenOptions { Title = "EZ Exports folder", AllowMultiple = false };
        if (start != null && Directory.Exists(start))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await StorageProvider.OpenFolderPickerAsync(options);
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path || Current != preset) return;
        _folder.Text = RelativeToScene(path);
    }

    // A folder inside the scene's folder is stored relative, so it moves with the scene.
    private string RelativeToScene(string path)
    {
        if (string.IsNullOrEmpty(_plugin.Host.ScenePath)) return path;
        string sceneFolder = Path.GetDirectoryName(Path.GetFullPath(_plugin.Host.ScenePath))!;
        string relative = Path.GetRelativePath(sceneFolder, path);
        if (relative == ".") return "";
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    /// <summary>One line: the target, every option that is on, the scale.</summary>
    private static string Describe(FbxExportSettings settings)
    {
        string target = FbxExportSettings.Targets.FirstOrDefault(t => t.Target == settings.Target).Label ?? settings.Target.ToString();
        var on = FbxExportSettings.Options.Where(o => o.Get(settings) && !IgnoredOptions.Contains(o.Label)).Select(o => o.Label).ToList();
        string text = target + (on.Count > 0 ? ": " + string.Join(", ", on) : "");
        return settings.ScaleFactor != 1f ? text + $"; scale {settings.ScaleFactor:0.######}" : text;
    }

    // The preset itself decides which animations and files; these settings are not used.
    private static readonly string[] IgnoredOptions =
        ["Only Selected Animations", "Multi-FBX (one file per animation)", "Multi-FBX (one file per character)"];

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
    }

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        control.Margin = new Thickness(8, 0, 0, 0);
        control.VerticalAlignment = VerticalAlignment.Top;
        return control;
    }

    /// <summary>A preset in the list: its Run box and name.</summary>
    private sealed class PresetRow : ContentControl
    {
        private readonly CheckBox _box;

        public PresetRow(EzPreset preset, EzExportsWindow window)
        {
            Preset = preset;
            _box = new CheckBox { IsChecked = preset.Checked, MinHeight = 0, Padding = new Thickness(4, 0) };
            ToolTip.SetTip(_box, "Ticked presets run with Run checked.");
            _box.IsCheckedChanged += delegate
            {
                if (Preset.Checked == (_box.IsChecked == true)) return;
                Preset.Checked = _box.IsChecked == true;
                window.Edited();
            };
            Update();
            Content = _box;
        }

        public EzPreset Preset { get; }

        public void Update() => _box.Content = Preset.Name.Length > 0 ? Preset.Name : "(unnamed)";
    }

    /// <summary>
    /// An Items row. A heading is a character: "Name (model)" when its model is an item
    /// (<see cref="Index"/> &gt;= 0), else just its name (-1). The other rows are animations and folders,
    /// shown by their path inside the character's Anim Bank, or "(missing)".
    /// </summary>
    private sealed class ItemRow : ContentControl
    {
        public ItemRow(int index, SceneObject? item, bool Heading)
        {
            Index = index;
            Item = item;
            string text = Heading
                ? (index >= 0 ? EzExportPlanner.ItemName(item) : (item?.Name ?? ""))
                : WithoutCharacter(EzExportPlanner.ItemPath(item));
            Content = new TextBlock
            {
                Text = text,
                FontWeight = Heading ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = Heading && index < 0 ? 0.7 : 1.0,
                Margin = new Thickness(Heading ? 0 : 16, 0, 0, 0),
            };
        }

        /// <summary>The row's index in the preset's items; -1 for a heading of a character that is not an item itself.</summary>
        public int Index { get; }

        public SceneObject? Item { get; }

        private static string WithoutCharacter(string path) =>
            path.IndexOf(": ", StringComparison.Ordinal) is int i and >= 0 ? path[(i + 2)..] : path;
    }
}
