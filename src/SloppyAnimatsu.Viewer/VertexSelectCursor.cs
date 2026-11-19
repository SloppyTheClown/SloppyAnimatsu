// The vertex selection pointer: a red arrow with a small
// selection box beside it, and a "+" (Ctrl, add) or "-" (Alt, remove) badge; Shift (replace) has no
// badge. Drawn once per mode into a 32x32 bitmap, hot spot at the arrow's tip.

using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

internal static class VertexSelectCursor
{
    private static readonly Dictionary<VertexSelectOp, Cursor> Cache = new();

    public static Cursor For(VertexSelectOp op)
    {
        if (!Cache.TryGetValue(op, out var cursor))
        {
            try
            {
                cursor = new Cursor(Draw(op), new PixelPoint(1, 1));
            }
            catch (Exception)
            {
                // No bitmap cursors on this platform: a plain cross still says "selecting".
                cursor = new Cursor(StandardCursorType.Cross);
            }
            Cache[op] = cursor;
        }
        return cursor;
    }

    private static RenderTargetBitmap Draw(VertexSelectOp op)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Avalonia.Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var outline = new Pen(Brushes.Black, 1.0);
            // The arrow, red.
            var arrow = new PolylineGeometry(
            [
                new Point(1, 1), new Point(1, 18), new Point(5, 14), new Point(8, 21),
                new Point(10.5, 20), new Point(7.5, 13), new Point(13, 13),
            ], true);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xe8, 0x20, 0x20)), outline, arrow);

            // The selection box, dashed white over a dark halo.
            var box = new Rect(14.5, 17.5, 11, 8);
            context.DrawRectangle(null, new Pen(Brushes.Black, 3.0), box);
            context.DrawRectangle(null, new Pen(Brushes.White, 1.0) { DashStyle = new DashStyle([2, 1], 0) }, box);

            // The badge: + adds, - removes, nothing replaces.
            if (op != VertexSelectOp.Replace)
            {
                var halo = new Pen(Brushes.Black, 4.0);
                var mark = new Pen(Brushes.White, 2.0);
                var a = new Point(23, 9.5);
                var b = new Point(31, 9.5);
                context.DrawLine(halo, a, b);
                if (op == VertexSelectOp.Add) context.DrawLine(halo, new Point(27, 5.5), new Point(27, 13.5));
                context.DrawLine(mark, a, b);
                if (op == VertexSelectOp.Add) context.DrawLine(mark, new Point(27, 5.5), new Point(27, 13.5));
            }
        }
        return bitmap;
    }
}
