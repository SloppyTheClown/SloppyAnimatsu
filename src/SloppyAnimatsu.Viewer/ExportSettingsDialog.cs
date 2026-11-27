using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The Export Settings window (File > Export…): a row of target presets, one lit at a time; the
/// scene/selection drop-down; one toggle per option, each with hover text; Export and Cancel. Every
/// change is reported at once, so the main window can light what the settings would write while the
/// window is open. The window is not modal. Closing it or Escape answers nothing.
/// </summary>
public sealed class ExportSettingsDialog : Window
{
    private static readonly IBrush LitForeground = new SolidColorBrush(Color.FromRgb(0x20, 0x18, 0x08));

    /// <summary>Amber, readable on the dark theme.</summary>
    private static readonly IBrush WarningForeground = new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x40));

    private FbxExportSettings _settings;
    private bool _ok;
    private bool _syncing;
    private readonly List<(FbxExportTarget Target, Button Button)> _presets = [];
    private readonly List<CheckBox> _toggles = [];
    private readonly ComboBox _scope;
    private readonly TextBox _separator;
    private readonly TextBox _scale;

    private static string FormatScale(float factor) => factor.ToString("0.######", CultureInfo.InvariantCulture);
    private readonly TextBlock _summary;
    private readonly TextBlock _warnings;
    private readonly Action<FbxExportSettings>? _changed;
    private readonly Func<FbxExportSettings, string>? _describe;


    private ExportSettingsDialog(FbxExportSettings initial, Action<FbxExportSettings>? changed,
        Func<FbxExportSettings, string>? describe, bool settingsOnly = false)
    {
        _settings = initial;
        _changed = changed;
        _describe = describe;
        Title = "Export Settings";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (target, label, tip) in FbxExportSettings.Targets)
        {
            var button = new Button { Content = label, MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Change(FbxExportSettings.For(target, _settings));
            _presets.Add((target, button));
            presetRow.Children.Add(button);
        }

        _scope = new ComboBox
        {
            ItemsSource = FbxExportSettings.Scopes.Select(s => s.Label).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(_scope, string.Join("\n", FbxExportSettings.Scopes.Select(s => s.Label + ": " + s.Tip)));
        _scope.SelectionChanged += (_, _) =>
        {
            if (_syncing || _scope.SelectedIndex < 0) return;
            Change(_settings with { Scope = FbxExportSettings.Scopes[_scope.SelectedIndex].Scope });
        };

        _separator = new TextBox { Width = 48, MaxLength = 8, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 1) };
        ToolTip.SetTip(_separator,
            "The separator between the folders and the animation name in per-animation file names (underscore by default). Characters a file name cannot hold become '-'.");
        _separator.TextChanged += (_, _) =>
        {
            if (!_syncing) Change(_settings with { FolderSeparator = _separator.Text ?? "" });
        };

        var toggles = new StackPanel { Spacing = 2 };
        foreach (var option in FbxExportSettings.Options)
        {
            var box = new CheckBox { Content = option.Label };
            ToolTip.SetTip(box, option.Tip);
            box.IsCheckedChanged += (_, _) =>
            {
                if (!_syncing) Change(option.Set(_settings, box.IsChecked == true));
            };
            _toggles.Add(box);
            if (_toggles.Count - 1 == FbxExportSettings.FolderPathOptionIndex)
                toggles.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, _separator } });
            else
                toggles.Children.Add(box);
        }

        // Compact and centred: the theme's default 32 px minimum height made the box taller than the
        // label beside it, with the number sitting at the top left.
        _scale = new TextBox
        {
            Width = 56,
            MinHeight = 0,
            Height = 24,
            Padding = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_scale,
            "Multiplies the size of everything exported (positions, animation translations, mesh vertices); 1 keeps the scene's own size. The scene itself is not changed.");
        _scale.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            bool valid = float.TryParse((_scale.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float factor)
                && FbxExportSettings.IsValidScaleFactor(factor);
            // ClearValue, not null: a null Foreground brush draws the text invisibly.
            if (valid) _scale.ClearValue(ForegroundProperty);
            else _scale.Foreground = WarningForeground;
            if (valid && factor != _settings.ScaleFactor) Change(_settings with { ScaleFactor = factor });
        };
        _scale.LostFocus += (_, _) => { _syncing = true; _scale.Text = FormatScale(_settings.ScaleFactor); _scale.ClearValue(ForegroundProperty); _syncing = false; };
        var scaleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "Scale factor", VerticalAlignment = VerticalAlignment.Center }, _scale },
        };

        var export = new Button { Content = settingsOnly ? "OK" : "Export…" };
        var cancel = new Button { Content = "Cancel" };
        ToolTip.SetTip(export, settingsOnly
            ? "Keep these settings."
            : "Choose where to write the file(s) and export with these settings. The settings are kept for next time.");
        ToolTip.SetTip(cancel, "Close without exporting. Changes made here are not kept.");
        export.Click += (_, _) => { _ok = true; Close(); };
        cancel.Click += (_, _) => Close();

        var legendMark = ExportHighlight.NewMark();
        legendMark.IsVisible = true;
        legendMark.Width = 14;
        legendMark.Height = 14;
        _summary = new TextBlock { FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { legendMark, _summary } };
        ToolTip.SetTip(legend, "While this window is open, the scene tree and the Anim Bank mark every object and animation these settings export.");

        _warnings = new TextBlock
        {
            FontSize = 11,
            Foreground = WarningForeground,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
        };
        ToolTip.SetTip(_warnings, "Settings that the chosen target reads badly or that have no effect. Exporting still works.");

        // A click on empty space (no control handled it) takes the focus out of the text boxes, so
        // the scale factor commits and the box is deselected. The transparent background on the
        // window makes the gaps between rows hit-testable.
        Background ??= Brushes.Transparent;
        PointerPressed += (_, e) =>
        {
            if (!e.Handled) FocusManager?.ClearFocus();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            MinWidth = 340,
            Children =
            {
                presetRow,
                _scope,
                toggles,
                scaleRow,
                _warnings,
                legend,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { export, cancel } },
            },
        };

        if (settingsOnly)
        {
            Title = "Export Settings (preset)";
            _scope.IsVisible = false;
            legend.IsVisible = false;
            for (int i = 0; i < _toggles.Count; i++)
                if (FbxExportSettings.SettingsOnlyHiddenOptions.Contains(FbxExportSettings.Options[i].Label))
                    _toggles[i].IsVisible = false;
        }

        var stops = new List<(DialogStop, Control)>();
        stops.AddRange(_presets.Select(p => (DialogStop.ExportPreset, (Control)p.Button)));
        if (_scope.IsVisible) stops.Add((DialogStop.ExportScope, _scope));
        for (int i = 0; i < _toggles.Count; i++)
        {
            if (_toggles[i].IsVisible) stops.Add((DialogStop.ExportOption, _toggles[i]));
            if (i == FbxExportSettings.FolderPathOptionIndex) stops.Add((DialogStop.ExportSeparator, _separator));
        }
        stops.Add((DialogStop.ExportScale, _scale));
        stops.Add((DialogStop.ExportRun, export));
        stops.Add((DialogStop.ExportCancel, cancel));
        new DialogChrome(this, DialogId.ExportSettings).BindTo(DialogLayout.ExportSettingsStops(settingsOnly), stops.ToArray());

        Sync();
    }

    /// <summary>
    /// Shows the window over <paramref name="owner"/> starting from <paramref name="initial"/>. It is
    /// not modal: the main window stays usable (scroll the Anim Bank and the Scene tree, change the
    /// selection) while it is open. <paramref name="changed"/> hears the starting settings and every
    /// change; <paramref name="describe"/> gives the line under the toggles that says what would be
    /// written; <paramref name="shown"/> receives the open window so the owner can
    /// <see cref="Refresh"/> it. Completes with the chosen settings, or null when the user cancelled.
    /// </summary>
    public static Task<FbxExportSettings?> Ask(Window owner, FbxExportSettings initial,
        Action<FbxExportSettings>? changed, Func<FbxExportSettings, string>? describe,
        Action<ExportSettingsDialog>? shown = null)
    {
        var dialog = new ExportSettingsDialog(initial, changed, describe);
        var done = new TaskCompletionSource<FbxExportSettings?>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog._ok ? dialog._settings : null);
        changed?.Invoke(initial);
        shown?.Invoke(dialog);
        dialog.Show(owner);
        return done.Task;
    }

    /// <summary>
    /// The settings alone, for a caller that decides itself what gets exported and where (an EZ
    /// Exports preset): no scope, no file-splitting toggles, OK instead of Export…. Modal over
    /// <paramref name="owner"/>; completes with the settings, or null when cancelled.
    /// </summary>
    public static async Task<FbxExportSettings?> AskSettingsOnly(Window owner, FbxExportSettings initial)
    {
        var dialog = new ExportSettingsDialog(initial, null, null, settingsOnly: true);
        await dialog.ShowDialog(owner);
        return dialog._ok ? dialog._settings : null;
    }

    /// <summary>Re-reports the current settings and rewrites the summary, for when what they
    /// export has changed outside the window (the selection, the Anim Bank).</summary>
    public void Refresh()
    {
        _changed?.Invoke(_settings);
        _summary.Text = _describe?.Invoke(_settings) ?? "";
    }

    private void Change(FbxExportSettings settings)
    {
        _settings = settings;
        Sync();
        _changed?.Invoke(settings);
    }

    /// <summary>Shows <see cref="_settings"/>: the lit preset, the scope, every toggle and the summary.</summary>
    private void Sync()
    {
        _syncing = true;
        foreach (var (target, button) in _presets)
        {
            if (target == _settings.Target)
            {
                button.Background = ExportHighlight.Bar;
                button.Foreground = LitForeground;
                button.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                button.ClearValue(BackgroundProperty);
                button.ClearValue(ForegroundProperty);
                button.ClearValue(FontWeightProperty);
            }
        }
        _scope.SelectedIndex = FbxExportSettings.Scopes.ToList().FindIndex(s => s.Scope == _settings.Scope);
        for (int i = 0; i < _toggles.Count; i++)
            _toggles[i].IsChecked = FbxExportSettings.Options[i].Get(_settings);
        if (_separator.Text != _settings.FolderSeparator) _separator.Text = _settings.FolderSeparator;
        DisabledHint.SetEnabled(_separator, _settings.FolderPathInName, "\"Folder path in file names\" ticked");
        if (!_scale.IsFocused) _scale.Text = FormatScale(_settings.ScaleFactor);
        _summary.Text = _describe?.Invoke(_settings) ?? "";
        var warnings = _settings.Warnings();
        _warnings.Text = string.Join("\n", warnings.Select(w => "⚠ " + w));
        _warnings.IsVisible = warnings.Count > 0;
        _syncing = false;
    }
}
