using System.Globalization;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>The modifier bits of a shortcut key code.</summary>
[Flags]
public enum ShortcutModifiers : uint
{
    None = 0,
    /// <summary>Bit 0: Shift.</summary>
    Shift = 1,
    /// <summary>Bit 1: Control.</summary>
    Control = 2,
    /// <summary>Bit 2: Alt (Win32's "menu" key).</summary>
    Alt = 4,
}

/// <summary>
/// The key code: a Win32 virtual-key code shifted left by seven, with
/// <see cref="ShortcutModifiers"/> in the low bits. <see cref="ShortcutKey.None"/> (code 0) means
/// no key.
/// </summary>
public readonly record struct ShortcutKey(int VirtualKey, ShortcutModifiers Modifiers)
{
    public static readonly ShortcutKey None = new(0, ShortcutModifiers.None);

    public bool IsEmpty => VirtualKey <= 0;

    public uint Code => ((uint)VirtualKey << 7) | ((uint)Modifiers & 0x7F);

    public static ShortcutKey FromCode(uint code) =>
        new((int)(code >> 7), (ShortcutModifiers)(code & 0x7F));

    /// <summary>Parses <c>[CTRL+][ALT+][SHIFT+]KEY</c> (any order, case-insensitive).</summary>
    public static ShortcutKey Parse(string text)
    {
        string s = text.Trim();
        if (s.Length == 0 || s is "-" or "—" ||
            s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("unbound", StringComparison.OrdinalIgnoreCase))
            return None;

        var modifiers = ShortcutModifiers.None;
        string? keyName = null;
        foreach (string raw in s.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                case "CMD":
                case "COMMAND":
                    modifiers |= ShortcutModifiers.Control;
                    break;
                case "ALT":
                case "MENU":
                case "OPTION":
                    modifiers |= ShortcutModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ShortcutModifiers.Shift;
                    break;
                default:
                    if (keyName != null)
                        throw new FormatException($"'{text}' names more than one key ('{keyName}' and '{part}').");
                    keyName = part;
                    break;
            }
        }

        if (keyName == null)
            throw new FormatException($"'{text}' has modifiers but no key.");
        if (!TryVirtualKey(keyName, out int vk))
            throw new FormatException($"Unknown key name '{keyName}'.");
        return new ShortcutKey(vk, modifiers);
    }

    /// <summary>The display form the app uses: <c>CTRL+ALT+SHIFT+KEY</c>, or an empty string for no key.</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        string prefix = "";
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) prefix += "CTRL+";
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) prefix += "ALT+";
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) prefix += "SHIFT+";
        return prefix + Shortcuts.DisplayName(VirtualKey);
    }

    /// <summary>
    /// The name of a Win32 virtual-key code, as the table spells it
    /// Unknown codes are <c>VK_XX</c>.
    /// </summary>
    public static string KeyName(int virtualKey)
    {
        if (VirtualKeyNames.TryGetValue(virtualKey, out string? name))
            return name;
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();
        return $"VK_{virtualKey:X2}";
    }

    public static bool IsKnownKey(int virtualKey) => VirtualKeyNames.ContainsKey(virtualKey);

    private static bool TryVirtualKey(string name, out int vk)
    {
        if (NameToVirtualKey.TryGetValue(name.ToUpperInvariant(), out vk))
            return true;
        // A lone printable character (letters and digits).
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }
        // A numeric VK_5A or VK_0x5A spelling, for keys the table does not name.
        if (name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase))
        {
            string hex = name[3..];
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk))
                return true;
        }
        vk = 0;
        return false;
    }

    private static readonly Dictionary<int, string> VirtualKeyNames = BuildVirtualKeyNames();

    private static Dictionary<int, string> BuildVirtualKeyNames()
    {
        var table = new Dictionary<int, string>
        {
            [0x01] = "LBUTTON", [0x02] = "RBUTTON", [0x03] = "CANCEL", [0x04] = "MBUTTON",
            [0x05] = "XBUTTON1", [0x06] = "XBUTTON2", [0x08] = "BACKSPACE", [0x09] = "TAB",
            [0x0C] = "CLEAR", [0x0D] = "RETURN", [0x10] = "SHIFT", [0x11] = "CONTROL",
            [0x12] = "MENU", [0x13] = "PAUSE", [0x14] = "CAPITAL", [0x1B] = "ESCAPE",
            [0x20] = "SPACE", [0x21] = "PRIOR", [0x22] = "NEXT", [0x23] = "END", [0x24] = "HOME",
            [0x25] = "LEFT", [0x26] = "UP", [0x27] = "RIGHT", [0x28] = "DOWN", [0x2C] = "SNAPSHOT",
            [0x2D] = "INSERT", [0x2E] = "DELETE", [0x2F] = "HELP", [0x5B] = "LWIN", [0x5C] = "RWIN",
            [0x5D] = "APPS", [0x5F] = "SLEEP", [0x6A] = "MULTIPLY", [0x6B] = "ADD",
            [0x6C] = "SEPARATOR", [0x6D] = "SUBTRACT", [0x6E] = "DECIMAL", [0x6F] = "DIVIDE",
            [0x90] = "NUMLOCK", [0x91] = "SCROLL", [0xA0] = "LSHIFT", [0xA1] = "RSHIFT",
            [0xA2] = "LCONTROL", [0xA3] = "RCONTROL", [0xA4] = "LMENU", [0xA5] = "RMENU",
            [0xBA] = "OEM_1", [0xBB] = "OEM_PLUS", [0xBC] = "OEM_COMMA", [0xBD] = "OEM_MINUS",
            [0xBE] = "OEM_PERIOD", [0xBF] = "OEM_2", [0xC0] = "OEM_3", [0xDB] = "OEM_4",
            [0xDC] = "OEM_5", [0xDD] = "OEM_6", [0xDE] = "OEM_7", [0xDF] = "OEM_8",
            [0xE1] = "OEM_AX", [0xE2] = "OEM_102", [0xE3] = "ICO_HELP", [0xE4] = "ICO_00",
            [0xE5] = "PROCESSKEY", [0xE6] = "ICO_CLEAR", [0xE7] = "PACKET", [0xE9] = "OEM_RESET",
            [0xEA] = "OEM_JUMP", [0xEB] = "OEM_PA1", [0xEC] = "OEM_PA2", [0xED] = "OEM_PA3",
            [0xEE] = "OEM_WSCTRL", [0xEF] = "OEM_CUSEL", [0xF0] = "OEM_ATTN", [0xF1] = "OEM_FINISH",
            [0xF2] = "OEM_COPY", [0xF3] = "OEM_AUTO", [0xF4] = "OEM_ENLW", [0xF5] = "OEM_BACKTAB",
            [0xF6] = "ATTN", [0xF7] = "CRSEL", [0xF8] = "EXSEL", [0xF9] = "EREOF", [0xFA] = "PLAY",
            [0xFB] = "ZOOM", [0xFC] = "NONAME", [0xFD] = "PA1", [0xFE] = "OEM_CLEAR",
        };
        for (int i = 0x30; i <= 0x39; i++) table[i] = ((char)i).ToString();
        for (int i = 0x41; i <= 0x5A; i++) table[i] = ((char)i).ToString();
        for (int i = 0; i < 10; i++) table[0x60 + i] = "NUMPAD" + i;
        for (int i = 0; i < 24; i++) table[0x70 + i] = "F" + (i + 1);
        return table;
    }

    private static readonly Dictionary<string, int> NameToVirtualKey = BuildNameTable();

    private static Dictionary<string, int> BuildNameTable()
    {
        var table = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in VirtualKeyNames) table[name] = code;
        void Alias(string alias, int code) => table[alias] = code;
        Alias("ENTER", 0x0D);
        Alias("ESC", 0x1B);
        Alias("DEL", 0x2E);
        Alias("INS", 0x2D);
        Alias("PAGEUP", 0x21);
        Alias("PAGEDOWN", 0x22);
        Alias("PAGE_UP", 0x21);
        Alias("PAGE_DOWN", 0x22);
        Alias("SPACEBAR", 0x20);
        Alias("BACK", 0x08);
        Alias("PLUS", 0xBB);
        Alias("MINUS", 0xBD);
        Alias("COMMA", 0xBC);
        Alias("PERIOD", 0xBE);
        Alias("SLASH", 0xBF);
        Alias("SEMICOLON", 0xBA);
        Alias("QUOTE", 0xDE);
        Alias("BACKTICK", 0xC0);
        Alias("LBRACKET", 0xDB);
        Alias("BACKSLASH", 0xDC);
        Alias("RBRACKET", 0xDD);
        Alias("PANEL", 0xA5);
        Alias("WINDOWS", 0x5B);
        return table;
    }
}

/// <summary>One <c>ShortCutMap</c> entry: a key code and the action it runs.</summary>
public sealed record ShortcutBinding(uint Code, string Action)
{
    public ShortcutKey Key => ShortcutKey.FromCode(Code);
    public override string ToString() => $"{Action} = {Key}";
}

/// <summary>
/// The shared key map (<c>shortcuts.slopa</c>): the
/// <c>CShortCutPreferences.ShortCutMap</c> from key code to action name. The map is kept sorted by
/// key code, the order <c>std::map</c> writes.
/// </summary>
public sealed class Shortcuts
{
    /// <summary>SloppyAnimatsu's key map file's name, beside its preferences.</summary>
    public const string FileName = "shortcuts.slopa";

    /// <summary>
    /// A key's display name: the single character the **active keyboard layout** prints on it when
    /// the host supplies one (<see cref="LayoutKeyName"/>; Windows' <c>GetKeyNameText</c>), else
    /// The US spelling from <see cref="ShortcutKey.KeyName"/>. Only single characters are
    /// taken from the layout and upper-cased, as the app does, so named keys keep its words
    /// ("RETURN", "SPACE") and a French layout shows "Ù" instead of "OEM_1". The stored shortcut is
    /// the virtual-key code, so display and file stay independent.
    /// </summary>
    public static string DisplayName(int virtualKey)
    {
        if (LayoutKeyName?.Invoke(virtualKey) is { Length: 1 } printed) return printed.ToUpperInvariant();
        return ShortcutKey.KeyName(virtualKey);
    }

    /// <summary>
    /// Set by the viewer on Windows (<c>WindowsKeyNames.Name</c>) for display only; null elsewhere.
    /// Parsing and writing still use the table.
    /// </summary>
    public static Func<int, string?>? LayoutKeyName { get; set; }

    private readonly AktDocument _document;
    private readonly AktObject _root;

    private Shortcuts(AktDocument document, AktObject root, string? path, IReadOnlyList<ShortcutBinding>? defaults)
    {
        _document = document;
        _root = root;
        Path = path;
        Defaults = defaults ?? DefaultBindings;
    }

    /// <summary>
    /// The shipped table this map lists and resets to: the <see cref="DefaultBindings"/>, or
    /// SloppyAnimatsu's panel keys (<see cref="Hotkeys.PanelDefaults"/>) for <c>panelShortcuts.slopa</c>.
    /// </summary>
    public IReadOnlyList<ShortcutBinding> Defaults { get; }

    public AktDocument Document => _document;

    /// <summary>The file it was loaded from, or null for a document created from the defaults.</summary>
    public string? Path { get; private set; }

    /// <summary>
    /// SloppyAnimatsu's key map, <c>%APPDATA%/SloppyAnimatsu/shortcuts.slopa</c> (beside its
    /// preferences), whether it exists or not.
    /// </summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SloppyAnimatsu", FileName);

    private AktMap Map =>
        _root["ShortCutMap"] as AktMap
        ?? throw new InvalidDataException($"'{Path ?? "the shortcuts document"}' has no CShortCutPreferences.ShortCutMap.");

    /// <summary>Loads a shortcuts file. Throws when it is not a <c>CShortCutPreferences</c> document.</summary>
    public static Shortcuts Load(string path, IReadOnlyList<ShortcutBinding>? defaults = null)
    {
        var document = AktDocument.Load(path);
        var root = document.Roots.FirstOrDefault(o => o.ClassKey == "CShortCutPreferences")
                   ?? throw new InvalidDataException($"'{path}' is not a shortcuts file: it has no CShortCutPreferences.");
        return new Shortcuts(document, root, path, defaults);
    }

    /// <summary>
    /// Loads the user's file; without one, the key map detached (the first save writes
    /// <see cref="DefaultPath"/>), else a default document.
    /// </summary>
    public static Shortcuts LoadDefault() =>
        File.Exists(DefaultPath) ? Load(DefaultPath)
        : CreateDefault();

    /// <summary>A shortcuts document holding <paramref name="defaults"/> (the <see cref="DefaultBindings"/> when null).</summary>
    public static Shortcuts CreateDefault(IReadOnlyList<ShortcutBinding>? defaults = null)
    {
        defaults ??= DefaultBindings;
        var schema = new AktSchema();
        var dataObject = new AktClassDef("class CDataObject");
        dataObject.Members.Add(new AktMember("Name", "CAtom"));
        schema.AddClass(dataObject, null);
        var preferenceNode = new AktClassDef("class CPreferenceNode");
        schema.AddClass(preferenceNode, dataObject);
        var preferences = new AktClassDef("class CShortCutPreferences");
        preferences.Members.Add(new AktMember("ShortCutMap", "map<uint,CAtom>"));
        schema.AddClass(preferences, preferenceNode);

        var root = schema.NewObject("class CShortCutPreferences");
        root.Set("Name", "Default name");
        var map = (AktMap)root["ShortCutMap"]!;
        foreach (var binding in defaults)
            map.Add(binding.Code, binding.Action);
        root.Set("ShortCutMap", map);
        return new Shortcuts(new AktDocument(schema, new[] { root }), root, null, defaults);
    }

    /// <summary>The bindings in file order (key code ascending).</summary>
    public IReadOnlyList<ShortcutBinding> Bindings =>
        Map.Entries.Where(e => e.Key is uint && e.Value is string)
            .Select(e => new ShortcutBinding((uint)e.Key!, (string)e.Value!))
            .ToList();

    public IReadOnlyList<string> Actions =>
        Bindings.Select(b => b.Action).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every action the shortcut dialog lists: the shipped table's actions plus any custom binding,
    /// alphabetically. An action stays listed after Unbind, so it can be bound again.
    /// </summary>
    public IReadOnlyList<string> ActionNames =>
        Defaults.Select(b => b.Action)
            .Concat(Actions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    /// <summary>Every key of <paramref name="action"/> in file order (an action may have several).</summary>
    public IReadOnlyList<ShortcutKey> KeysFor(string action) =>
        Bindings.Where(b => b.Action == action && b.Code != 0).Select(b => b.Key).ToList();

    /// <summary>The key of <paramref name="action"/>, or null when it has none.</summary>
    public ShortcutKey? KeyFor(string action)
    {
        var entry = Map.Entries.FirstOrDefault(e => e.Value is string s && s == action);
        return entry == null ? null : ShortcutKey.FromCode((uint)entry.Key!);
    }

    /// <summary>The action bound to <paramref name="key"/>, or null. The no-key code 0 is not an action.</summary>
    public string? ActionFor(ShortcutKey key)
    {
        if (key.IsEmpty) return null;
        return Bindings.FirstOrDefault(b => b.Code == key.Code)?.Action;
    }

    /// <summary>
    /// Binds <paramref name="action"/> to <paramref name="key"/>. Returns false and a reason when
    /// another action already uses the key (the app refuses the assignment rather than stealing it)
    /// or when the key's name is unknown. An empty key unbinds the action.
    /// </summary>
    public bool TrySet(string action, ShortcutKey key, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(action))
        {
            reason = "The action name is empty.";
            return false;
        }
        if (!key.IsEmpty)
        {
            if (!ShortcutKey.IsKnownKey(key.VirtualKey))
            {
                reason = $"'{ShortcutKey.KeyName(key.VirtualKey)}' is not a key the key map knows.";
                return false;
            }
            string? other = ActionFor(key);
            if (other != null && other != action)
            {
                reason = $"The key {key} is already assigned to {other}.";
                return false;
            }
        }

        var map = Map;
        int at = map.Entries.FindIndex(e => e.Value is string s && s == action);
        if (key.IsEmpty)
        {
            if (at >= 0) map.Entries.RemoveAt(at);
            return true;
        }
        if (at >= 0)
            map.Entries[at].Key = key.Code;
        else
            map.Add(key.Code, action);
        Sort();
        return true;
    }

    /// <summary>Binds without the duplicate refusal (used by reset and default construction).</summary>
    private void ForceSet(string action, ShortcutKey key)
    {
        var map = Map;
        if (!key.IsEmpty)
        {
            int conflicting = map.Entries.FindIndex(e => e.Key is uint c && c == key.Code && !Equals(e.Value, action));
            if (conflicting >= 0) map.Entries.RemoveAt(conflicting);
        }
        int at = map.Entries.FindIndex(e => e.Value is string s && s == action);
        if (key.IsEmpty)
        {
            if (at >= 0) map.Entries.RemoveAt(at);
        }
        else if (at >= 0)
            map.Entries[at].Key = key.Code;
        else
            map.Add(key.Code, action);
        Sort();
    }

    /// <summary>Removes the action's binding. Returns false when it had none.</summary>
    public bool Unbind(string action)
    {
        int at = Map.Entries.FindIndex(e => e.Value is string s && s == action);
        if (at < 0) return false;
        Map.Entries.RemoveAt(at);
        return true;
    }

    /// <summary>
    /// Restores the shipped key of one action (removing any other action that a custom map put on
    /// that key), or unbinds it when it has no default. Returns false when the action is unknown.
    /// </summary>
    public bool Reset(string action)
    {
        var defaults = Defaults.Where(b => b.Action == action).ToList();
        if (defaults.Count == 0) return false;
        var key = defaults[0].Key;
        if (!key.IsEmpty)
        {
            ForceSet(action, key);
            // An action shipped with several keys (Backspace beside Shift+Delete) gets them all back.
            foreach (var extra in defaults.Skip(1).Where(b => b.Code != 0))
            {
                Map.Entries.RemoveAll(e => e.Key is uint c && c == extra.Code);
                Map.Add(extra.Code, action);
            }
            Sort();
            return true;
        }
        // The one shipped action at code 0 stays in the map as the no-key placeholder.
        var map = Map;
        int zero = map.Entries.FindIndex(e => e.Key is uint c && c == 0 && !Equals(e.Value, action));
        if (zero >= 0) map.Entries.RemoveAt(zero);
        int at = map.Entries.FindIndex(e => e.Value is string s && s == action);
        if (at >= 0) map.Entries[at].Key = 0u;
        else map.Add(0u, action);
        Sort();
        return true;
    }

    /// <summary>Restores every shipped binding and drops any action that is not part of it.</summary>
    public void ResetAll()
    {
        var map = Map;
        map.Entries.Clear();
        foreach (var binding in Defaults)
            map.Add(binding.Code, binding.Action);
        Sort();
    }

    public void Save()
    {
        if (Path == null) throw new InvalidOperationException("The shortcuts document has no save path.");
        _document.Save(Path, SettingsFiles.FormatFor(Path));
    }

    public void SaveAs(string path)
    {
        _document.Save(path, SettingsFiles.FormatFor(path));
        Path = path;
    }

    /// <summary>
    /// Forgets the file this map was read from, so the next save goes to the session's default file:
    /// how the shared key map seeds SloppyAnimatsu's without ever being written.
    /// </summary>
    public Shortcuts Detach()
    {
        Path = null;
        return this;
    }

    private void Sort() =>
        Map.Sort((a, b) => ((uint)a!).CompareTo((uint)b!));

    /// <summary>
    /// The default key bindings: what a reset of the key map goes back to.
    /// </summary>
    public static IReadOnlyList<ShortcutBinding> DefaultBindings { get; } = new ShortcutBinding[]
    {
        new(0x0000, "ResetAndFreeze"),
        new(0x0480, "ChangeInterfaceContext"),
        new(0x0680, "Play"),
        new(0x0682, "PlayRange"),
        new(0x1000, "MiscToggleCurveboard"),
        new(0x1180, "LastController"),
        new(0x1200, "FirstController"),
        new(0x1280, "PreviousKey"),
        new(0x1282, "PreviousFrame"),
        new(0x1300, "PreviousController"),
        new(0x1380, "NextKey"),
        new(0x1382, "NextFrame"),
        new(0x1400, "NextController"),
        new(0x1700, "DeleteObject"),
        new(0x1800, "SetCameraRight"),
        new(0x1880, "SetSpinnerModeLocalFrame"),
        new(0x1900, "SetSpinnerModeScreenFrame"),
        new(0x1980, "SetSpinnerModeWorldFrame"),
        new(0x1A80, "SetCameraLeft"),
        new(0x1B00, "SetCameraTop"),
        new(0x1B80, "SetCameraBottom"),
        new(0x2080, "ActivatePaint"),
        new(0x2082, "SelectAllControllers"),
        new(0x2083, "QuickSelectAllKeys"),
        new(0x2100, "StackerTogglePreviousGhosts"),
        new(0x2180, "BindSkinEx"),
        new(0x2182, "CopyKeys"),
        new(0x2184, "CopyWeights"),
        new(0x2200, "UnbindSkin"),
        new(0x2280, "SetSpinnerModeRotate"),
        new(0x2282, "ExportMenu"),
        new(0x2300, "CenterCamera"),
        new(0x2380, "ToggleGrid"),
        new(0x2400, "ToggleShowSelected"),
        new(0x2402, "HelpMenu"),
        new(0x2482, "ImportFromFile"),
        new(0x2500, "ToggleCreateJointButton"),
        new(0x2580, "StackerCreateKey"),
        new(0x2600, "ToggleLayoutFromMouse"),
        new(0x2680, "PaintModeAim"),
        new(0x2682, "MirrorSkin"),
        new(0x2700, "StackerToggleNextGhosts"),
        new(0x2702, "NewFile"),
        new(0x2782, "OpenFile"),
        new(0x2783, "RecentScenes"),
        new(0x2880, "PaintFrontBack"),
        new(0x2882, "Quit"),
        new(0x2900, "SetSpinnerModeScale"),
        new(0x2980, "PaintScanner"),
        new(0x2982, "SaveFile"),
        new(0x2983, "SaveFileAs"),
        new(0x2A00, "PaintModeSelect"),
        new(0x2A80, "BindSkin"),
        new(0x2B00, "MiscTranslateZoomRotateWhileInteractiveAction"),
        new(0x2B01, "SetCameraIfLastSelected"),
        new(0x2B02, "PasteKeys"),
        new(0x2B03, "SymmetricalKeys"),
        new(0x2B04, "PasteWeights"),
        new(0x2B80, "SetSpinnerModeMove"),
        new(0x2B82, "CloseFile"),
        new(0x2C00, "ResetChannelValue"),
        new(0x2C80, "AddIK"),
        new(0x2D00, "PaintModeAdd"),
        new(0x2D02, "Undo"),
        new(0x2D03, "Redo"),
        new(0x3000, "ToggleUnlit"),
        new(0x3080, "ToggleWireframe"),
        new(0x3100, "ToggleTextured"),
        new(0x3180, "ToggleUntextured"),
        new(0x3280, "ToggleHiddenBones"),
        new(0x3300, "TogglePivots"),
        new(0x3380, "ToggleSkinning"),
        new(0x3400, "ToggleBlendshapeInteractiveZones"),
        new(0x3480, "ToggleHUD"),
        new(0x3500, "FloodVerticesSelection"),
        new(0x3780, "InvertVerticesSelection"),
        new(0x3800, "ToggleLayoutSingleView"),
        new(0x3880, "ToggleLayout2Views"),
        new(0x3900, "ToggleLayout2ViewsCurveboard"),
        new(0x3980, "ToggleLayout4Views"),
    };
}
