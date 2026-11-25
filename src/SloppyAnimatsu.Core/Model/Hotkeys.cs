namespace SloppyAnimatsu.Core.Model;

/// <summary>Where a <see cref="HotkeyAction"/> listens.</summary>
public enum HotkeyScope
{
    /// <summary>Anywhere in the main window, unless a focused control claimed the key first.</summary>
    Window,
    /// <summary>Only while the 3D viewport has the keyboard focus.</summary>
    Viewport,
    /// <summary>Only while the TREE (outliner) has the keyboard focus.</summary>
    Outliner,
    /// <summary>Only while the key timeline has the keyboard focus.</summary>
    Timeline,
    /// <summary>Only while the curveboard has the keyboard focus.</summary>
    Curveboard,
}

/// <summary>
/// A key action of one panel. <c>InSharedMap</c> actions are in the shared key map (their keys live in
/// <c>shortcuts.slopa</c> and are edited in the Shortcut Manager); the others are SloppyAnimatsu's
/// and live in <c>panelShortcuts.slopa</c> (<see cref="Hotkeys.PanelDefaults"/>, the Panel keys dialog).
/// </summary>
public sealed record HotkeyAction(string Name, HotkeyScope Scope, string Does, bool InSharedMap = false);

/// <summary>
/// The one place that says how a key press becomes a command. Every configurable binding lives in
/// The shortcuts map (<see cref="Shortcuts"/>, edited in the shortcut dialog); the menus take
/// their gestures from it (<see cref="MenuModel.Gesture"/>), and the window runs a menu row by its
/// gesture through <see cref="MenuCommandFor"/>. The keys a focused panel (viewport, TREE, timeline,
/// curveboard) handles itself are listed in <see cref="PanelActions"/> and looked up through a
/// <see cref="HotkeyMap"/>: shared actions in its map, SloppyAnimatsu's own in <c>panelShortcuts.slopa</c>.
///
/// The text-field rule (<see cref="ReachesCommands"/>): while a text field has the focus, only keys
/// that cannot type or edit text run commands - chords with Ctrl or Alt, Escape and F1-F24. A plain
/// or Shift-only letter, digit, Delete, Backspace or arrow key belongs to the field.
/// </summary>
public static class Hotkeys
{
    private const int VkEscape = 0x1B, VkF1 = 0x70, VkF24 = 0x87;

    /// <summary>True when <paramref name="key"/> may run a command while a text field has the focus.</summary>
    public static bool RunsWhileTyping(ShortcutKey key) =>
        (key.Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) != 0
        || key.VirtualKey == VkEscape
        || key.VirtualKey is >= VkF1 and <= VkF24;

    /// <summary>Whether a key press may run a command, given whether a text field has the focus.</summary>
    public static bool ReachesCommands(ShortcutKey key, bool textFieldFocused) =>
        !key.IsEmpty && (!textFieldFocused || RunsWhileTyping(key));

    /// <summary>
    /// The enabled main-menu row a key press runs, or null. Only the menu bar's rows count (the
    /// context menus show the same gestures but are not live while closed), and the first match wins.
    /// </summary>
    public static MenuEntry? MenuCommandFor(MenuState state, ShortcutKey key, bool textFieldFocused = false)
    {
        if (!ReachesCommands(key, textFieldFocused)) return null;
        return Flatten(MenuModel.Build(state)).FirstOrDefault(e =>
            e.Command.HasValue && e.Enabled && e.Gesture is { } g && g.Code == key.Code);
    }

    /// <summary>Every gesture shown on a main-menu row, with the row, for listing and conflict checks.</summary>
    public static IEnumerable<(ShortcutKey Key, MenuEntry Entry)> MenuGestures(MenuState state) =>
        Flatten(MenuModel.Build(state))
            .Where(e => e.Command.HasValue && e.Gesture is { IsEmpty: false })
            .Select(e => (e.Gesture!.Value, e));

    /// <summary>The file holding SloppyAnimatsu's own panel keys, beside its preferences.</summary>
    public const string PanelFileName = "panelShortcuts.slopa";

    /// <summary><c>%APPDATA%/SloppyAnimatsu/panelShortcuts.slopa</c>, whether it exists or not.</summary>
    public static string PanelDefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SloppyAnimatsu", PanelFileName);

    /// <summary>A panel-keys map holding <see cref="PanelDefaults"/>.</summary>
    public static Shortcuts CreatePanelDefault() => Shortcuts.CreateDefault(PanelDefaults);

    /// <summary>Loads a panel-keys file (its list and resets use <see cref="PanelDefaults"/>).</summary>
    public static Shortcuts LoadPanel(string path) => Shortcuts.Load(path, PanelDefaults);

    /// <summary>The user's panel-keys file, or the defaults when it does not exist.</summary>
    public static Shortcuts LoadPanelDefault() =>
        File.Exists(PanelDefaultPath) ? LoadPanel(PanelDefaultPath) : CreatePanelDefault();

    private const ShortcutModifiers NoMods = ShortcutModifiers.None, Shift = ShortcutModifiers.Shift,
        Ctrl = ShortcutModifiers.Control;

    private static ShortcutBinding Bind(string action, int vk, ShortcutModifiers modifiers = NoMods) =>
        new(new ShortcutKey(vk, modifiers).Code, action);

    /// <summary>
    /// The shipped keys of SloppyAnimatsu's own panel actions (the reset target of <c>panelShortcuts.slopa</c>).
    /// An action may have several keys; a key belongs to one action in this file.
    /// </summary>
    public static IReadOnlyList<ShortcutBinding> PanelDefaults { get; } =
    [
        Bind("Hammer", 0x48),
        Bind("DeleteKeysCloseGaps", 0x2E, Shift),
        Bind("DeleteKeysCloseGaps", 0x08, Shift),
        Bind("PasteKeysAfter", 0x56, Ctrl | Shift),
        Bind("RenameRow", 0x71),
        Bind("TimelineDeleteKeys", 0x08),
        Bind("SelectAllKeys", 0x41, Ctrl),
        Bind("MoveKeysLeft", 0x25, Ctrl),
        Bind("MoveKeysRight", 0x27, Ctrl),
        Bind("WidenKeyGap", 0xBB),
        Bind("WidenKeyGap", 0xBB, Shift),
        Bind("WidenKeyGap", 0x6B),
        Bind("WidenKeyGap", 0x6B, Shift),
        Bind("NarrowKeyGap", 0xBD),
        Bind("NarrowKeyGap", 0xBD, Shift),
        Bind("NarrowKeyGap", 0x6D),
        Bind("NarrowKeyGap", 0x6D, Shift),
        Bind("TimelinePreviousFrame", 0x25),
        Bind("TimelineNextFrame", 0x27),
        Bind("CurveboardFit", 0x46),
        Bind("CurveboardClearSelection", 0x1B),
    ];

    /// <summary>
    /// Every key action a panel handles while it has the focus, in lookup order. Escape's
    /// cancel-the-drag in the viewport and the timeline is not an action: it always cancels.
    /// </summary>
    public static IReadOnlyList<HotkeyAction> PanelActions { get; } =
    [
        new("CopyKeys", HotkeyScope.Window, "Copy keys (not in a text field)", InSharedMap: true),
        new("PasteKeys", HotkeyScope.Window, "Paste keys (not in a text field)", InSharedMap: true),
        new("CenterCamera", HotkeyScope.Viewport, "Focus on selection (frame all when nothing is selected)", InSharedMap: true),
        new("Hammer", HotkeyScope.Viewport, "Viewport: Hammer"),
        new("DeleteKeysCloseGaps", HotkeyScope.Viewport, "Viewport and timeline: delete keys and close the gaps"),
        new("PasteKeysAfter", HotkeyScope.Viewport, "Viewport and timeline: paste keys after"),
        new("SelectAllControllers", HotkeyScope.Viewport, "Viewport: select every bone and handle of the current character", InSharedMap: true),
        new("DeleteObject", HotkeyScope.Outliner, "Delete the selected rows", InSharedMap: true),
        new("RenameRow", HotkeyScope.Outliner, "TREE: rename the selected row"),
        new("SelectAllControllers", HotkeyScope.Outliner, "Select every row", InSharedMap: true),
        new("StackerCreateKey", HotkeyScope.Timeline, "Create a key", InSharedMap: true),
        new("DeleteObject", HotkeyScope.Timeline, "Delete the selected keys", InSharedMap: true),
        new("TimelineDeleteKeys", HotkeyScope.Timeline, "Timeline: delete the selected keys (a second key beside Delete)"),
        new("DeleteKeysCloseGaps", HotkeyScope.Timeline, "Delete the selected keys and close the gaps"),
        new("SelectAllKeys", HotkeyScope.Timeline, "Timeline and curveboard: select every key"),
        new("CopyKeys", HotkeyScope.Timeline, "Copy keys", InSharedMap: true),
        new("PasteKeys", HotkeyScope.Timeline, "Paste keys", InSharedMap: true),
        new("PasteKeysAfter", HotkeyScope.Timeline, "Paste keys after"),
        new("MoveKeysLeft", HotkeyScope.Timeline, "Timeline: move the selected keys left"),
        new("MoveKeysRight", HotkeyScope.Timeline, "Timeline: move the selected keys right"),
        new("WidenKeyGap", HotkeyScope.Timeline, "Timeline: widen the gap after the selected keys"),
        new("NarrowKeyGap", HotkeyScope.Timeline, "Timeline: narrow the gap after the selected keys"),
        new("TimelinePreviousFrame", HotkeyScope.Timeline, "Timeline: previous frame"),
        new("TimelineNextFrame", HotkeyScope.Timeline, "Timeline: next frame"),
        new("CurveboardFit", HotkeyScope.Curveboard, "Curveboard: fit the curves"),
        new("SelectAllKeys", HotkeyScope.Curveboard, "Select every key"),
        new("CurveboardClearSelection", HotkeyScope.Curveboard, "Curveboard: clear the key selection"),
    ];

    /// <summary>What a SloppyAnimatsu panel action does (its first row's wording), or null.</summary>
    public static string? Describe(string action) =>
        PanelActions.FirstOrDefault(a => a.Name == action && !a.InSharedMap)?.Does;

    private static IEnumerable<MenuEntry> Flatten(IEnumerable<MenuEntry> entries)
    {
        foreach (MenuEntry entry in entries)
        {
            yield return entry;
            foreach (MenuEntry child in Flatten(entry.Children)) yield return child;
        }
    }
}

/// <summary>
/// The live panel keys: <see cref="Hotkeys.PanelActions"/> resolved against the map and
/// SloppyAnimatsu's panel map. The shortcut dialogs edit both maps in place, so a map built once follows
/// every edit.
/// </summary>
public sealed class HotkeyMap
{
    public HotkeyMap(Shortcuts shared, Shortcuts panel)
    {
        Shared = shared;
        Panel = panel;
    }

    public Shortcuts Shared { get; }
    public Shortcuts Panel { get; }

    /// <summary>The shipped keys of both maps, used by a panel before the window hands it the live map.</summary>
    public static HotkeyMap Default { get; } = new(Shortcuts.CreateDefault(), Hotkeys.CreatePanelDefault());

    /// <summary>The action <paramref name="key"/> runs while a panel of <paramref name="scope"/> has the focus, or null.</summary>
    public string? ActionFor(HotkeyScope scope, ShortcutKey? key)
    {
        if (key is not { IsEmpty: false } pressed) return null;
        foreach (var action in Hotkeys.PanelActions)
        {
            if (action.Scope != scope) continue;
            var map = action.InSharedMap ? Shared : Panel;
            if (map.ActionFor(pressed) == action.Name) return action.Name;
        }
        return null;
    }

    /// <summary>Every key of every action of <paramref name="scope"/>, for listing and conflict checks.</summary>
    public IEnumerable<(ShortcutKey Key, HotkeyAction Action)> KeysIn(HotkeyScope scope) =>
        Hotkeys.PanelActions.Where(a => a.Scope == scope)
            .SelectMany(a => (a.InSharedMap ? Shared : Panel).KeysFor(a.Name).Select(k => (k, a)));
}
