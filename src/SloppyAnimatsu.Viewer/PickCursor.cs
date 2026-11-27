// The object picking pointer while a modifier changes what a click does: the system arrow with a
// "+" (Ctrl, adds to the selection) or "-" (Alt, removes from it) badge beside the tip. The badge
// matches the vertex selection pointer's so both gestures read the same way.

using Avalonia.Input;

namespace SloppyAnimatsu.Viewer;

internal static class PickCursor
{
    private static Cursor? _add, _remove;

    /// <summary>The pointer for a click that adds (true) or removes (false) objects.</summary>
    public static Cursor For(bool add) => add ? _add ??= Make(true) : _remove ??= Make(false);

    // Badge centre in 32-unit space from the hot spot: right of the arrow's head.
    private static Cursor Make(bool add) =>
        BadgedCursor.Make(context => BadgedCursor.DrawSign(context, add, 20, 8.5), Cursor.Default);
}
