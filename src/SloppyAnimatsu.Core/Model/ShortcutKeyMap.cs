namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The virtual-key codes the shortcut file uses and the name of the UI key each one is
///. The viewer parses a name as an
/// <c>Avalonia.Input.Key</c> when it builds a menu gesture and reads a captured key's own name back
/// into a code, so the table lives here - where a test can check it covers every shipped binding -
/// instead of inside the window.
/// </summary>
public static class ShortcutKeyMap
{
    /// <summary>Code to UI key name (an <c>Avalonia.Input.Key</c> member name).</summary>
    public static IReadOnlyDictionary<int, string> Table => Codes;

    /// <summary>The UI key name for a virtual-key code, or null for a key this build has no name for.</summary>
    public static string? KeyName(int virtualKey) =>
        Codes.TryGetValue(virtualKey, out string? name) ? name: null;

    /// <summary>The virtual-key code of a UI key name, or false for a name that is not in the table.</summary>
    public static bool TryVirtualKey(string? keyName, out int virtualKey)
    {
        if (keyName != null && Names.TryGetValue(keyName, out virtualKey)) return true;
        virtualKey = 0;
        return false;
    }

    private static readonly Dictionary<int, string> Codes = Build();

    private static readonly Dictionary<string, int> Names =
        Codes.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static Dictionary<int, string> Build()
    {
        var map = new Dictionary<int, string>();
        void Add(int vk, string name) => map[vk] = name;

        for (int i = 0; i < 26; i++) Add('A' + i, ((char)('A' + i)).ToString());
        for (int i = 0; i <= 9; i++) Add('0' + i, "D" + i);
        for (int i = 0; i < 10; i++) Add(0x60 + i, "NumPad" + i);
        for (int i = 0; i < 24; i++) Add(0x70 + i, "F" + (i + 1));

        Add(0x08, "Back");
        Add(0x09, "Tab");
        Add(0x0D, "Return");
        Add(0x13, "Pause");
        Add(0x14, "CapsLock");
        Add(0x1B, "Escape");
        Add(0x20, "Space");
        Add(0x21, "PageUp");
        Add(0x22, "PageDown");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x2C, "PrintScreen");
        Add(0x2D, "Insert");
        Add(0x2E, "Delete");
        Add(0x2F, "Help");
        Add(0x6A, "Multiply");
        Add(0x6B, "Add");
        Add(0x6D, "Subtract");
        Add(0x6E, "Decimal");
        Add(0x6F, "Divide");
        Add(0x90, "NumLock");
        Add(0x91, "Scroll");
        Add(0xBA, "OemSemicolon");
        Add(0xBB, "OemPlus");
        Add(0xBC, "OemComma");
        Add(0xBD, "OemMinus");
        Add(0xBE, "OemPeriod");
        Add(0xBF, "OemQuestion");
        Add(0xC0, "OemTilde");
        Add(0xDB, "OemOpenBrackets");
        Add(0xDC, "OemPipe");
        Add(0xDD, "OemCloseBrackets");
        Add(0xDE, "OemQuotes");
        return map;
    }
}
