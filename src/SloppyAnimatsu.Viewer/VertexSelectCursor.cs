// The vertex selection pointer: the system arrow with a small selection box beside it, and a "+"
// (Ctrl, add) or "-" (Alt, remove) badge; Shift (replace) has no sign.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

internal static class VertexSelectCursor
{
    private static readonly Dictionary<VertexSelectOp, Cursor> Cache = new();

    public static Cursor For(VertexSelectOp op)
    {
        if (!Cache.TryGetValue(op, out var cursor))
        {
            // Without bitmap cursors a plain cross still says "selecting".
            cursor = BadgedCursor.Make(context => DrawBadge(context, op), new Cursor(StandardCursorType.Cross));
            Cache[op] = cursor;
        }
        return cursor;
    }

    // Coordinates are 32-unit space from the hot spot: the box below-right of the arrow, the sign above it.
    private static void DrawBadge(DrawingContext context, VertexSelectOp op)
    {
        var box = new Rect(13.5, 16.5, 11, 8);
        context.DrawRectangle(null, new Pen(Brushes.Black, 3.0), box);
        context.DrawRectangle(null, new Pen(Brushes.Red, 1.0) { DashStyle = new DashStyle([2, 1], 0) }, box);
        if (op != VertexSelectOp.Replace) BadgedCursor.DrawSign(context, op == VertexSelectOp.Add, 26, 8.5);
    }
}
