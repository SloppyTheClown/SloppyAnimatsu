using System.Runtime.InteropServices;
using System.Text;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The character the active Windows keyboard layout prints on a key, for the shortcut display
/// <c>GetKeyNameText</c> is fed the key's scan code.
/// Only a single printable character is returned; named keys ("Enter", "Space", localized names)
/// are left to the table, so the UI keeps its vocabulary.
/// </summary>
internal static class WindowsKeyNames
{
    private const uint MapVkToVsc = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, StringBuilder lpString, int nSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    /// <summary>The layout's name for <paramref name="virtualKey"/>, or null.</summary>
    public static string? Name(int virtualKey)
    {
        if (virtualKey <= 0) return null;
        uint scan = MapVirtualKeyW((uint)virtualKey, MapVkToVsc);
        if (scan == 0) return null;
        int lParam = (int)(scan << 16);
        if (IsExtended(virtualKey)) lParam |= 1 << 24;
        var buffer = new StringBuilder(32);
        int length = GetKeyNameTextW(lParam, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : null;
    }

    /// <summary>The keys whose scan code needs the extended bit (Win32's <c>MAPVK</c> rules).</summary>
    private static bool IsExtended(int virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
}