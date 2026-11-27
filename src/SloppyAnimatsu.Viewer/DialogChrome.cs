using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Binds a dialog's controls to <see cref="DialogLayout"/>: opening focuses the model's initial stop,
/// Tab and Shift+Tab walk its order, Enter invokes its default and Escape cancels, closes, or cancels
/// a key capture first. <see cref="Bind"/> refuses a control order the model does not describe, so a
/// window cannot silently drift from the tested rules. A key handler registered before this one keeps
/// the keys it needs: the shortcut dialog's capture takes every key while it is armed.
/// </summary>
internal sealed class DialogChrome
{
    private readonly Window _window;
    private readonly DialogId _dialog;
    private readonly Func<bool>? _cancelCapture;
    private IReadOnlyList<DialogStop> _stops = Array.Empty<DialogStop>();
    private IReadOnlyList<Control> _controls = Array.Empty<Control>();

    /// <param name="cancelCapture">
    /// Escape with nothing else to do: called first, and the window closes when it returns false. The
    /// shortcut dialog passes its armed-capture cancel here.
    /// </param>
    public DialogChrome(Window window, DialogId dialog, Func<bool>? cancelCapture = null)
    {
        _window = window;
        _dialog = dialog;
        _cancelCapture = cancelCapture;
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        window.Opened += (_, _) => FocusInitial();
    }

    /// <summary>
    /// The dialog's controls in the model's tab order. A preferences page passes one
    /// <see cref="DialogStop.PageValue"/> per value box; the count tells the model what to expect, so
    /// a missing or reordered control throws here instead of changing what Tab does.
    /// </summary>
    public void Bind(params (DialogStop Stop, Control Control)[] controls)
    {
        int values = controls.Count(control => control.Stop is DialogStop.PageValue or DialogStop.WelcomeTemplate);
        BindTo(DialogLayout.Stops(_dialog, values), controls);
    }

    /// <summary>
    /// <see cref="Bind"/> against an order the caller takes from the model itself, for a dialog with
    /// more than one shape (the Export Settings window an export preset opens).
    /// </summary>
    public void BindTo(IReadOnlyList<DialogStop> expected, params (DialogStop Stop, Control Control)[] controls)
    {
        if (expected.Count != controls.Length)
            throw new InvalidOperationException(
                $"{_dialog} binds {controls.Length} controls but DialogLayout describes {expected.Count}.");
        for (int i = 0; i < controls.Length; i++)
            if (expected[i] != controls[i].Stop)
                throw new InvalidOperationException(
                    $"{_dialog} binds {controls[i].Stop} at {i}, where DialogLayout expects {expected[i]}.");
        _stops = controls.Select(control => control.Stop).ToList();
        _controls = controls.Select(control => control.Control).ToList();
    }

    private void FocusInitial()
    {
        var initial = DialogLayout.Initial(_dialog);
        for (int i = 0; i < _stops.Count; i++)
        {
            if (_stops[i] != initial) continue;
            FocusAt(i);
            return;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        DialogKey? key = e.Key switch
        {
            Key.Tab => (e.KeyModifiers & KeyModifiers.Shift) != 0 ? DialogKey.ShiftTab : DialogKey.Tab,
            Key.Enter => DialogKey.Enter,
            Key.Escape => DialogKey.Escape,
            _ => null,
        };
        if (key is not { } pressed) return;
        var action = DialogLayout.Key(_dialog, _stops, FocusedIndex(), pressed);
        switch (action.Kind)
        {
            case DialogKeyKind.Focus:
                FocusAt(action.Index);
                e.Handled = true;
                break;
            case DialogKeyKind.Invoke:
                InvokeAt(action.Index);
                e.Handled = true;
                break;
            case DialogKeyKind.Close:
                e.Handled = true;
                _window.Close();
                break;
            case DialogKeyKind.CancelCapture:
                e.Handled = true;
                if (_cancelCapture is null || !_cancelCapture()) _window.Close();
                break;
        }
    }

    /// <summary>The bound control the window's focus is on, or -1 when it is on something else.</summary>
    private int FocusedIndex()
    {
        if (_window.FocusManager?.GetFocusedElement() is not Control focused) return -1;
        for (int i = 0; i < _controls.Count; i++)
            if (ReferenceEquals(_controls[i], focused)) return i;
        return -1;
    }

    private void FocusAt(int index)
    {
        if (index >= 0 && index < _controls.Count) _controls[index].Focus();
    }

    /// <summary>
    /// Press a control as a click would: a button's Click handlers run, and a check box flips as
    /// Space would flip it. The Welcome window's Enter rule presses whichever of the two has focus.
    /// </summary>
    private void InvokeAt(int index)
    {
        if (index < 0 || index >= _controls.Count) return;
        switch (_controls[index])
        {
            // Avalonia's ToggleButton derives from Button, so the check box is matched first.
            case ToggleButton toggle:
                toggle.IsChecked = !(toggle.IsChecked ?? false);
                break;
            case Button button:
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                break;
        }
    }
}