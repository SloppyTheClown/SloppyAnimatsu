namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// What one key press asked the shortcut dialog to do: whether the press belongs to the capture,
/// whether an action took the key (so the rows must be redrawn), and the line to show.
/// </summary>
public readonly record struct ShortcutCapture(bool Handled, bool Assigned, string Status);

/// <summary>
/// The keyboard shortcut dialog's own code, moved out of the window so it can be tested without
/// Avalonia. It owns the
/// capture state machine - armed by Assign key, refusing a modifier, an unnamed key and a key
/// another action already uses, and cancelled by Escape - the Unbind/Reset/Reset all edits, and the
/// save-on-close that creates a missing <c>shortcuts.slopa</c>.
/// </summary>
public sealed class ShortcutSession
{
    /// <summary>The action column's width in the list (a Consolas row).</summary>
    public const int NameWidth = 54;

    /// <summary>There is always a selection, because Unbind keeps the action listed; this is the guard's text.</summary>
    public const string NoSelectionText = "Select an action first.";

    public const string CancelledText = "Capture cancelled.";

    public const string ModifierText = "Hold the modifiers and press a key.";

    public const string NothingToUnbindText = "That action has no key to remove.";

    public const string NotShippedText = "That action is not one of the shipped actions, so it has no key to reset.";

    /// <summary>The no-key mark of a row.</summary>
    public const string NoKeyMark = "—";

    private readonly Shortcuts _shortcuts;
    private readonly string _defaultPath;
    private readonly Func<string, string?>? _describe;
    private readonly List<string> _actions;

    /// <param name="shortcuts">The map the dialog edits.</param>
    /// <param name="defaultPath">
    /// Where a file the document was not loaded from is created. Defaults to
    /// <see cref="Shortcuts.DefaultPath"/>; the tests pass a folder inside TEMP, because a process
    /// started from this checkout cannot write to the user's Documents folder.
    /// </param>
    /// <param name="describe">What an action does, shown in the help line (the Panel keys dialog passes <see cref="Hotkeys.Describe"/>).</param>
    public ShortcutSession(Shortcuts shortcuts, string? defaultPath = null, Func<string, string?>? describe = null)
    {
        _shortcuts = shortcuts;
        _describe = describe;
        _defaultPath = defaultPath ?? Shortcuts.DefaultPath;
        _actions = shortcuts.ActionNames.ToList();
        SelectedIndex = _actions.Count > 0 ? 0 : -1;
        Status = SelectionHelp;
    }

    public Shortcuts Shortcuts => _shortcuts;

    /// <summary>Every listed action, alphabetically: the shipped ones plus any custom binding.</summary>
    public IReadOnlyList<string> Actions => _actions;

    /// <summary>The file a save writes (a missing one is created here).</summary>
    public string FilePath => _shortcuts.Path ?? _defaultPath;

    /// <summary>The selected row, or -1 in a map with no action at all.</summary>
    public int SelectedIndex { get; private set; }

    public string? SelectedAction =>
        SelectedIndex >= 0 && SelectedIndex < _actions.Count ? _actions[SelectedIndex] : null;

    /// <summary>The action waiting for a key, or null when Assign key is not armed.</summary>
    public string? Capturing { get; private set; }

    /// <summary>Whether an edit still has to be written.</summary>
    public bool Dirty { get; private set; }

    /// <summary>The line under the list.</summary>
    public string Status { get; private set; }

    /// <summary>The dialog's header: what to do, and the file a save writes.</summary>
    public string Heading => $"Select an action and press Assign key, then the new key. {FilePath}";

    /// <summary>The list a save writes, and what Escape cancels.</summary>
    public string SelectionHelp
    {
        get
        {
            if (SelectedAction is not { } action) return "Select an action.";
            var keys = _shortcuts.KeysFor(action);
            string does = _describe?.Invoke(action) is { } text ? $" ({text})" : "";
            return keys.Count > 0
                ? $"{action}{does} is bound to {string.Join(", ", keys)}. Press Assign key to change it."
                : $"{action}{does} has no key.";
        }
    }

    /// <summary>One row per action: its name in a fixed column and its key, or the no-key mark.</summary>
    public IReadOnlyList<string> Rows
    {
        get
        {
            var rows = new List<string>(_actions.Count);
            foreach (string action in _actions)
            {
                var keys = _shortcuts.KeysFor(action);
                rows.Add($"{action,-NameWidth} {(keys.Count > 0 ? string.Join(", ", keys) : NoKeyMark)}");
            }
            return rows;
        }
    }

    /// <summary>The list's selection moved: the help line follows it, unless a capture is armed.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _actions.Count) SelectedIndex = index;
        if (Capturing == null) Status = SelectionHelp;
    }

    /// <summary>Assign key: arm the capture for the selected action.</summary>
    public bool BeginCapture()
    {
        if (SelectedAction is not { } action)
        {
            Status = NoSelectionText;
            return false;
        }
        Capturing = action;
        Status = $"Press the new key for {action}; Escape cancels.";
        return true;
    }

    /// <summary>Escape: cancel an armed capture. Not handled when no capture is armed (the window closes).</summary>
    public ShortcutCapture Escape()
    {
        if (Capturing == null) return new ShortcutCapture(false, false, Status);
        Capturing = null;
        Status = CancelledText;
        return new ShortcutCapture(true, false, Status);
    }

    /// <summary>A modifier alone: the app waits for the key it modifies.</summary>
    public ShortcutCapture Modifier()
    {
        Status = ModifierText;
        return new ShortcutCapture(Capturing != null, false, Status);
    }

    /// <summary>
    /// A key press while the capture is armed. <paramref name="key"/> is null when the viewer has no
    /// The app code for the pressed key, which the app refuses by name; a key another action uses is
    /// refused instead of stolen.
    /// </summary>
    public ShortcutCapture Press(ShortcutKey? key, string pressedName)
    {
        if (Capturing == null) return new ShortcutCapture(false, false, Status);
        if (key is not { } pressed)
        {
            Status = $"'{pressedName}' is not a key the key map names.";
            return new ShortcutCapture(true, false, Status);
        }
        // The key goes to the row selected now, in case another row was clicked while armed.
        string action = SelectedAction ?? Capturing;
        Capturing = action;
        if (!_shortcuts.TrySet(action, pressed, out string? reason))
        {
            Status = reason!;
            return new ShortcutCapture(true, false, Status);
        }
        Capturing = null;
        Dirty = true;
        Status = $"{action} = {pressed}.";
        return new ShortcutCapture(true, true, Status);
    }

    /// <summary>Unbind: remove the selected action's key. False and a reason when it has none.</summary>
    public bool Unbind()
    {
        if (SelectedAction is not { } action)
        {
            Status = NoSelectionText;
            return false;
        }
        if (!_shortcuts.Unbind(action))
        {
            Status = NothingToUnbindText;
            return false;
        }
        Dirty = true;
        Status = SelectionHelp;
        return true;
    }

    /// <summary>Reset: put the selected action's shipped key back.</summary>
    public bool Reset()
    {
        if (SelectedAction is not { } action)
        {
            Status = NoSelectionText;
            return false;
        }
        if (!_shortcuts.Reset(action))
        {
            Status = NotShippedText;
            return false;
        }
        Dirty = true;
        Status = SelectionHelp;
        return true;
    }

    /// <summary>Reset all: the shipped map, dropping every custom binding.</summary>
    public bool ResetAll()
    {
        if (SelectedAction is not { } action)
        {
            Status = NoSelectionText;
            return false;
        }
        _shortcuts.ResetAll();
        Dirty = true;
        Status = SelectionHelp;
        return true;
    }

    /// <summary>
    /// Writes the map, creating a missing file (and its folder) as the app does. False leaves the
    /// dialog open with <see cref="Status"/> saying why.
    /// </summary>
    public bool TrySave()
    {
        try
        {
            if (_shortcuts.Path == null)
            {
                string? folder = System.IO.Path.GetDirectoryName(_defaultPath);
                if (folder != null) Directory.CreateDirectory(folder);
                _shortcuts.SaveAs(_defaultPath);
            }
            else
            {
                _shortcuts.Save();
            }
            Dirty = false;
            Status = $"Saved {FilePath}.";
            return true;
        }
        catch (Exception e)
        {
            Status = "Could not save the shortcuts: " + e.Message;
            return false;
        }
    }

    /// <summary>Save and close: the button promises a write even when nothing was edited.</summary>
    public void RequestSave() => Dirty = true;

    /// <summary>Closing the dialog: an edit is saved first, and a failed save keeps the window open.</summary>
    public bool TryClose() => !Dirty || TrySave();
}
