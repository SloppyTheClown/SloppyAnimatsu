using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Diagnostics;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The session log as the user sees it. Every status-bar message (what an edit, undo, redo, command,
/// open or save reported, and every error) is written to the run's log (<see cref="AppLog"/>), and
/// Help > Show log opens a window showing this run's log or the previous run's. The log folder keeps
/// those two files per program (viewer, CLI); each run starts a fresh file capped at 4 MiB.
/// SloppyAnimatsu's own addition.
/// </summary>
public partial class MainWindow
{
    private string? _lastLoggedStatus;
    private Window? _logWindow;

    /// <summary>Copies every new status-bar message into the log, warnings at their own level.</summary>
    private void WireStatusLog()
    {
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            string? text = _status.Text;
            if (string.IsNullOrWhiteSpace(text) || text == _lastLoggedStatus) return;
            _lastLoggedStatus = text;
            string line = text.ReplaceLineEndings(" | ");
            if (LooksLikeAFailure(text)) AppLog.Warn(line);
            else AppLog.Action(line);
        };
    }

    /// <summary>Whether a status message reports a failure (it is logged as a warning).</summary>
    internal static bool LooksLikeAFailure(string text)
    {
        string t = text.ToLowerInvariant();
        return t.Contains("failed") || t.Contains("could not") || t.Contains("couldn't")
               || t.Contains("cannot") || t.Contains("error") || t.Contains("not implemented")
               || t.Contains("denied") || t.Contains("invalid");
    }

    private void ShowLogWindow()
    {
        if (_logWindow != null)
        {
            _logWindow.Activate();
            return;
        }

        var which = new ComboBox { MinWidth = 220 };
        which.Items.Add("This run");
        which.Items.Add("Previous run");
        which.SelectedIndex = 0;
        var path = new TextBlock { Opacity = 0.7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
        };
        var refresh = PanelIcons.Apply(new Button { Padding = new Thickness(5.0, 3.0) }, "Refresh", PanelIcons.Refresh(), 14.0);
        var folder = PanelIcons.Apply(new Button { Padding = new Thickness(5.0, 3.0) }, "Open folder", PanelIcons.OpenFolder(), 14.0);

        string? Current() => which.SelectedIndex == 1 ? AppLog.PreviousFilePath : AppLog.FilePath;

        void Load()
        {
            string? file = Current();
            path.Text = file ?? "";
            text.Text = file == null
                ? (which.SelectedIndex == 1 ? "There is no previous run's log." : "No log file is open for this run.")
                : AppLog.ReadShared(file) ?? "The log file could not be read: " + file;
            text.CaretIndex = text.Text?.Length ?? 0;
        }

        which.SelectionChanged += delegate { Load(); };
        refresh.Click += delegate { Load(); };
        folder.Click += delegate
        {
            if (AppLog.FolderPath is not { } dir) return;
            try
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status.Text = "Could not open the log folder: " + ex.Message;
            }
        };

        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(which);
        buttons.Children.Add(refresh);
        buttons.Children.Add(folder);
        DockPanel.SetDock(buttons, Dock.Left);
        bar.Children.Add(buttons);
        path.Margin = new Thickness(10, 0, 0, 0);
        bar.Children.Add(path);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(text);

        _logWindow = new Window
        {
            Title = "Sloppy Animatsu log",
            Width = 900,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = root,
        };
        _logWindow.Closed += delegate { _logWindow = null; };
        Load();
        _logWindow.Show(this);
    }
}
