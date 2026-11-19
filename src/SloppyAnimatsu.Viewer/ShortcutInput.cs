using Avalonia.Input;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// Translates between an Avalonia key press and the .akt shortcut key code
///: a Win32 virtual-key code shifted left by seven,
/// with Control/Alt/Shift in the low bits. Which code a named key is, and which key a code is,
/// comes from <see cref="ShortcutKeyMap"/> (Core, tested); this file only parses the UI key names.
/// </summary>
internal static class ShortcutInput
{
    /// <summary>The shortcut key code for an Avalonia key press, or null for a key the key map does not name.</summary>
    public static ShortcutKey? FromKey(Key key, KeyModifiers modifiers)
    {
        if (!ShortcutKeyMap.TryVirtualKey(key.ToString(), out int vk)) return null;
        if ((modifiers & ~(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return null;
        var result = ShortcutModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= ShortcutModifiers.Shift;
        return new ShortcutKey(vk, result);
    }

    /// <summary>The menu gesture for a shortcut key code, or false when this build has no Avalonia key for it.</summary>
    public static bool TryGesture(ShortcutKey key, out KeyGesture gesture)
    {
        gesture = null!;
        if (key.IsEmpty || ShortcutKeyMap.KeyName(key.VirtualKey) is not { } name) return false;
        if (!Enum.TryParse(name, out Key avalonia)) return false;
        var modifiers = KeyModifiers.None;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Control)) modifiers |= KeyModifiers.Control;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= KeyModifiers.Alt;
        if (key.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= KeyModifiers.Shift;
        gesture = new KeyGesture(avalonia, modifiers);
        return true;
    }
}
