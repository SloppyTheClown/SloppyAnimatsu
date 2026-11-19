using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The keyboard shortcut dialog
/// one row per action, the current key, Assign (capture the next key), Unbind, Reset and
/// Reset all. The capture state machine and the edits are <see cref="ShortcutSession"/>; this window
/// only draws its rows and routes keys into it. The app refuses a key another action already uses
/// and a key it does not name; the file is written when the dialog is closed, creating a missing
/// shortcuts.slopa if it is missing.
/// </summary>
public sealed class ShortcutDialog : Window
{
    private readonly ShortcutSession _session;
    private readonly Action _saved;
    private readonly ListBox _list = new()
    {
        Width = 640,
        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
        FontSize = 12,
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };
    private bool _refreshing;

    private ShortcutDialog(Shortcuts shortcuts, Action saved, bool panel)
    {
        _session = panel
            ? new ShortcutSession(shortcuts, Hotkeys.PanelDefaultPath, Hotkeys.Describe)
            : new ShortcutSession(shortcuts);
        _saved = saved;
        Title = panel ? "Panel keys" : "Keyboard shortcuts";
        Width = 760;
        Height = 620;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            // Keep the capture prompt visible if the user clicks another row while assigning.
            _session.Select(_list.SelectedIndex);
            _status.Text = _session.Status;
        };

        var assign = new Button { Content = "Assign key…" };
        assign.Click += (_, _) =>
        {
            _session.BeginCapture();
            _status.Text = _session.Status;
            _list.Focus();
        };
        var unbind = new Button { Content = "Unbind" };
        unbind.Click += (_, _) => Edit(_session.Unbind);
        var reset = new Button { Content = "Reset" };
        reset.Click += (_, _) => Edit(_session.Reset);
        var resetAll = new Button { Content = "Reset all" };
        resetAll.Click += (_, _) => Edit(_session.ResetAll);
        var save = new Button { Content = "Save and close" };
        save.Click += (_, _) => { _session.RequestSave(); Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { assign, unbind, reset, resetAll, save },
        };
        var help = new TextBlock
        {
            Text = _session.Heading,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        var layout = new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10,
            Children = { help, _list, buttons, _status },
        };
        Grid.SetRow(help, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(buttons, 2);
        Grid.SetRow(_status, 3);
        Content = layout;

        // A key is captured only while Assign was pressed; otherwise the list keeps its normal
        // arrow-key navigation. This handler comes before the chrome's, so an armed capture takes
        // every key it is given and the tab order never steals one.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        var chrome = new DialogChrome(this, DialogId.Shortcuts, CancelCapture);
        chrome.Bind(
            (DialogStop.ActionList, _list),
            (DialogStop.ActionAssign, assign),
            (DialogStop.ActionUnbind, unbind),
            (DialogStop.ActionReset, reset),
            (DialogStop.ActionResetAll, resetAll),
            (DialogStop.ActionSave, save));
        Closing += (_, e) =>
        {
            // Nothing edited closes at once (the original's `if (!_dirty) return`): no save, and no
            // "Saved ..." line in the window that opened the dialog.
            if (!_session.Dirty) return;
            if (_session.TryClose()) _saved();
            else
            {
                _status.Text = _session.Status;
                e.Cancel = true;
            }
        };

        Refresh();
        _status.Text = _session.Status;
    }

    /// <summary>Shows the dialog and waits for it to close.</summary>
    public static Task ShowFor(Window owner, Shortcuts shortcuts, Action saved, bool panel = false) =>
        new ShortcutDialog(shortcuts, saved, panel).ShowDialog(owner);

    private void Refresh()
    {
        _refreshing = true;
        _list.ItemsSource = _session.Rows.ToList();
        _list.SelectedIndex = _session.SelectedIndex;
        _refreshing = false;
    }

    private void Edit(Func<bool> edit)
    {
        if (edit()) Refresh();
        _status.Text = _session.Status;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session.Capturing == null) return;
        e.Handled = true;
        var result = e.Key == Key.Escape
            ? _session.Escape()
            : IsModifierKey(e.Key)
                ? _session.Modifier()
                : _session.Press(ShortcutInput.FromKey(e.Key, e.KeyModifiers), e.Key.ToString());
        if (result.Assigned) Refresh();
        _status.Text = result.Status;
    }

    /// <summary>Escape with no capture armed: nothing to cancel, so the window closes (and closing saves pending edits).</summary>
    private bool CancelCapture()
    {
        var result = _session.Escape();
        _status.Text = result.Status;
        return result.Handled;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
