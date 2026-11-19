using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The key router (<see cref="Hotkeys"/>): which key press runs which menu row, and the text-field
/// rule that plain and Shift keys belong to a focused text field (renaming a row must be able to type
/// L or K without toggling the layout or creating a key).
/// </summary>
public class HotkeysTests
{
    private static MenuState State() => new()
    {
        HasDocument = true,
        HasStack = true,
        Shortcuts = Shortcuts.CreateDefault(),
    };

    private static ShortcutKey Plain(int vk) => new(vk, ShortcutModifiers.None);

    [Fact]
    public void L_And_K_RunTheirRowsOnlyOutsideATextField()
    {
        var state = State();
        Assert.Equal(MenuCommand.ToggleLayout, Hotkeys.MenuCommandFor(state, Plain(0x4C))?.Command);
        Assert.Equal(MenuCommand.CreateKey, Hotkeys.MenuCommandFor(state, Plain(0x4B))?.Command);
        Assert.Null(Hotkeys.MenuCommandFor(state, Plain(0x4C), textFieldFocused: true));
        Assert.Null(Hotkeys.MenuCommandFor(state, Plain(0x4B), textFieldFocused: true));
    }

    [Fact]
    public void NoTypingKeyReachesACommandWhileATextFieldHasFocus()
    {
        var state = State();
        var typing = new List<int>();
        for (int vk = 0x30; vk <= 0x39; vk++) typing.Add(vk);              // digits
        for (int vk = 0x41; vk <= 0x5A; vk++) typing.Add(vk);              // letters
        typing.AddRange([0x20, 0x08, 0x2E, 0x25, 0x26, 0x27, 0x28, 0x24, 0x23, 0xBB, 0xBD, 0xBC, 0xBE]);
        foreach (int vk in typing)
        foreach (var mods in new[] { ShortcutModifiers.None, ShortcutModifiers.Shift })
        {
            var key = new ShortcutKey(vk, mods);
            Assert.False(Hotkeys.ReachesCommands(key, textFieldFocused: true), key.ToString());
            Assert.Null(Hotkeys.MenuCommandFor(state, key, textFieldFocused: true));
        }
    }

    [Fact]
    public void EveryPlainMenuGestureIsHeldBackWhileTyping_AndChordsStillRun()
    {
        var state = State();
        var gestures = Hotkeys.MenuGestures(state).ToList();
        Assert.NotEmpty(gestures);
        foreach (var (key, entry) in gestures)
        {
            bool chord = (key.Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt)) != 0;
            bool functionKey = key.VirtualKey is 0x1B or (>= 0x70 and <= 0x87);
            Assert.Equal(chord || functionKey, Hotkeys.ReachesCommands(key, textFieldFocused: true));
            if (entry.Enabled)
                Assert.NotNull(Hotkeys.MenuCommandFor(state, key));
        }
        // Ctrl+S saves even from a text field.
        Assert.Equal(MenuCommand.Save,
            Hotkeys.MenuCommandFor(state, new ShortcutKey(0x53, ShortcutModifiers.Control), textFieldFocused: true)?.Command);
    }

    [Fact]
    public void NoKeyRunsTwoDifferentMenuRows()
    {
        var clashes = Hotkeys.MenuGestures(State())
            .GroupBy(g => g.Key.Code)
            .Where(g => g.Select(x => (x.Entry.Command, x.Entry.Argument)).Distinct().Count() > 1)
            .Select(g => $"{g.First().Key}: " + string.Join(", ", g.Select(x => x.Entry.Header)))
            .ToList();
        Assert.Empty(clashes);
    }

    private static ShortcutKey Key(int vk, ShortcutModifiers mods = ShortcutModifiers.None) => new(vk, mods);

    [Theory]
    [InlineData(HotkeyScope.Window, 0x43, ShortcutModifiers.Control, "CopyKeys")]
    [InlineData(HotkeyScope.Window, 0x56, ShortcutModifiers.Control, "PasteKeys")]
    [InlineData(HotkeyScope.Viewport, 0x46, ShortcutModifiers.None, "CenterCamera")]
    [InlineData(HotkeyScope.Viewport, 0x48, ShortcutModifiers.None, "Hammer")]
    [InlineData(HotkeyScope.Viewport, 0x2E, ShortcutModifiers.Shift, "DeleteKeysCloseGaps")]
    [InlineData(HotkeyScope.Viewport, 0x56, ShortcutModifiers.Control | ShortcutModifiers.Shift, "PasteKeysAfter")]
    [InlineData(HotkeyScope.Outliner, 0x2E, ShortcutModifiers.None, "DeleteObject")]
    [InlineData(HotkeyScope.Outliner, 0x71, ShortcutModifiers.None, "RenameRow")]
    [InlineData(HotkeyScope.Timeline, 0x4B, ShortcutModifiers.None, "StackerCreateKey")]
    [InlineData(HotkeyScope.Timeline, 0x2E, ShortcutModifiers.None, "DeleteObject")]
    [InlineData(HotkeyScope.Timeline, 0x08, ShortcutModifiers.None, "TimelineDeleteKeys")]
    [InlineData(HotkeyScope.Timeline, 0x08, ShortcutModifiers.Shift, "DeleteKeysCloseGaps")]
    [InlineData(HotkeyScope.Timeline, 0x41, ShortcutModifiers.Control, "SelectAllKeys")]
    [InlineData(HotkeyScope.Timeline, 0x25, ShortcutModifiers.Control, "MoveKeysLeft")]
    [InlineData(HotkeyScope.Timeline, 0x25, ShortcutModifiers.None, "TimelinePreviousFrame")]
    [InlineData(HotkeyScope.Timeline, 0x27, ShortcutModifiers.None, "TimelineNextFrame")]
    [InlineData(HotkeyScope.Timeline, 0x6B, ShortcutModifiers.Shift, "WidenKeyGap")]
    [InlineData(HotkeyScope.Timeline, 0xBD, ShortcutModifiers.None, "NarrowKeyGap")]
    [InlineData(HotkeyScope.Curveboard, 0x46, ShortcutModifiers.None, "CurveboardFit")]
    [InlineData(HotkeyScope.Curveboard, 0x1B, ShortcutModifiers.None, "CurveboardClearSelection")]
    public void ShippedPanelKeysResolveInTheirScope(HotkeyScope scope, int vk, ShortcutModifiers mods, string action) =>
        Assert.Equal(action, HotkeyMap.Default.ActionFor(scope, Key(vk, mods)));

    [Fact]
    public void EveryPanelActionHasAShippedKey()
    {
        var map = HotkeyMap.Default;
        foreach (var scope in Enum.GetValues<HotkeyScope>())
        {
            var bound = map.KeysIn(scope).Select(k => k.Action).ToHashSet();
            foreach (var action in Hotkeys.PanelActions.Where(a => a.Scope == scope))
                Assert.Contains(action, bound);
        }
    }

    [Fact]
    public void NoKeyRunsTwoPanelActionsInOneScope()
    {
        var map = HotkeyMap.Default;
        var clashes = Enum.GetValues<HotkeyScope>()
            .SelectMany(scope => map.KeysIn(scope).GroupBy(k => k.Key.Code)
                .Where(g => g.Select(k => k.Action.Name).Distinct().Count() > 1)
                .Select(g => $"{scope} {g.First().Key}: " + string.Join(", ", g.Select(k => k.Action.Name))))
            .ToList();
        Assert.Empty(clashes);
    }

    [Fact]
    public void WindowWideKeysAreChords()
    {
        // A window-wide key also fires while a text field has the focus unless it is a chord.
        foreach (var (key, action) in HotkeyMap.Default.KeysIn(HotkeyScope.Window))
            Assert.True(Hotkeys.RunsWhileTyping(key), action.Name);
    }

    [Fact]
    public void PanelActionNamesNeverCollideWithKeyMapActions()
    {
        var shared = Shortcuts.DefaultBindings.Select(b => b.Action).ToHashSet();
        foreach (var action in Hotkeys.PanelActions)
            Assert.Equal(action.InSharedMap, shared.Contains(action.Name));
        Assert.All(Hotkeys.PanelDefaults, b => Assert.DoesNotContain(b.Action, shared));
        Assert.All(Hotkeys.PanelActions.Where(a => !a.InSharedMap),
            a => Assert.NotNull(Hotkeys.Describe(a.Name)));
    }

    [Fact]
    public void RebindingAPanelKeyMovesTheAction()
    {
        var map = new HotkeyMap(Shortcuts.CreateDefault(), Hotkeys.CreatePanelDefault());
        var j = Key(0x4A);
        Assert.True(map.Panel.TrySet("Hammer", j, out _));
        Assert.Equal("Hammer", map.ActionFor(HotkeyScope.Viewport, j));
        Assert.Null(map.ActionFor(HotkeyScope.Viewport, Key(0x48)));

        map.Panel.Reset("Hammer");
        Assert.Equal("Hammer", map.ActionFor(HotkeyScope.Viewport, Key(0x48)));
        Assert.Null(map.ActionFor(HotkeyScope.Viewport, j));
    }

    [Fact]
    public void ResettingAMultiKeyActionRestoresEveryKey()
    {
        var panel = Hotkeys.CreatePanelDefault();
        Assert.True(panel.TrySet("WidenKeyGap", Key(0x57), out _));
        panel.Reset("WidenKeyGap");
        var keys = panel.KeysFor("WidenKeyGap").Select(k => k.Code).OrderBy(c => c).ToList();
        var shipped = Hotkeys.PanelDefaults.Where(b => b.Action == "WidenKeyGap").Select(b => b.Code).OrderBy(c => c).ToList();
        Assert.Equal(shipped, keys);
    }

    [Fact]
    public void PanelKeysSurviveASaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-panelkeys-{Guid.NewGuid():N}.akt");
        try
        {
            var panel = Hotkeys.CreatePanelDefault();
            Assert.True(panel.TrySet("CurveboardFit", Key(0x47, ShortcutModifiers.Shift), out _));
            panel.SaveAs(path);
            var map = new HotkeyMap(Shortcuts.CreateDefault(), Hotkeys.LoadPanel(path));
            Assert.Equal("CurveboardFit", map.ActionFor(HotkeyScope.Curveboard, Key(0x47, ShortcutModifiers.Shift)));
            Assert.Null(map.ActionFor(HotkeyScope.Curveboard, Key(0x46)));
            Assert.Equal("Hammer", map.ActionFor(HotkeyScope.Viewport, Key(0x48)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
