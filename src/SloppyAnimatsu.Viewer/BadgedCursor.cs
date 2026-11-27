// The viewport's modifier pointers are the system's own arrow with a badge drawn beside it, so the
// pointer keeps the shape of the user's cursor scheme and only gains the "+", "-" or selection box.
// On Windows the arrow image is read from the system cursor (drawn once over black and once over
// white to recover its alpha); elsewhere, or if that fails, a plain white arrow stands in.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SloppyAnimatsu.Viewer;

internal static class BadgedCursor
{
    /// <summary>
    /// A cursor made of the system arrow plus <paramref name="badge"/>, which draws in a 32-unit
    /// space whose origin is the arrow's hot spot (scaled to the system cursor's size).
    /// </summary>
    public static Cursor Make(Action<DrawingContext> badge, Cursor fallback)
    {
        try
        {
            var arrow = SystemArrow();
            int size = arrow?.Image.PixelSize.Width ?? 32;
            var hotSpot = arrow?.HotSpot ?? new PixelPoint(1, 1);
            double scale = size / 32.0;
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Avalonia.Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                if (arrow != null)
                    context.DrawImage(arrow.Image, new Rect(0, 0, size, size));
                else
                    DrawPlainArrow(context);
                using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(hotSpot.X, hotSpot.Y)))
                    badge(context);
            }
            return new Cursor(bitmap, hotSpot);
        }
        catch (Exception)
        {
            // No bitmap cursors on this platform.
            return fallback;
        }
    }

    /// <summary>The "+" (add) or "-" (remove) mark, white over a dark halo, centred on (cx, cy) in badge units.</summary>
    public static void DrawSign(DrawingContext context, bool add, double cx, double cy)
    {
        var halo = new Pen(Brushes.Black, 4.0);
        var mark = new Pen(Brushes.White, 2.0);
        var a = new Point(cx - 4, cy);
        var b = new Point(cx + 4, cy);
        var top = new Point(cx, cy - 4);
        var bottom = new Point(cx, cy + 4);
        context.DrawLine(halo, a, b);
        if (add) context.DrawLine(halo, top, bottom);
        context.DrawLine(mark, a, b);
        if (add) context.DrawLine(mark, top, bottom);
    }

    private static void DrawPlainArrow(DrawingContext context)
    {
        var arrow = new PolylineGeometry(
        [
            new Point(1, 1), new Point(1, 18), new Point(5, 14), new Point(8, 21),
            new Point(10.5, 20), new Point(7.5, 13), new Point(13, 13),
        ], true);
        context.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.0), arrow);
    }

    private sealed record ArrowImage(WriteableBitmap Image, PixelPoint HotSpot);

    private static bool _arrowRead;
    private static ArrowImage? _arrow;

    private static ArrowImage? SystemArrow()
    {
        if (_arrowRead) return _arrow;
        _arrowRead = true;
        if (!OperatingSystem.IsWindows()) return null;
        try { _arrow = ReadWindowsArrow(); }
        catch (Exception) { _arrow = null; }
        return _arrow;
    }

    // --- Win32 -----------------------------------------------------------------------------------

    private const int IdcArrow = 32512;   // IDC_ARROW, winuser.h
    private const int DiNormal = 0x0003;  // DI_NORMAL = DI_IMAGE | DI_MASK
    private const int BiRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int FIcon;
        public int XHotspot;
        public int YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes;
        public short Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr obj, int size, out BitmapInfo info);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, int usage, out IntPtr bits, IntPtr section, int offset);

    private static ArrowImage? ReadWindowsArrow()
    {
        IntPtr cursor = LoadCursorW(IntPtr.Zero, IdcArrow);
        if (cursor == IntPtr.Zero || !GetIconInfo(cursor, out var info)) return null;
        int size;
        try
        {
            // The mask is twice the cursor's height when there is no colour bitmap (monochrome).
            IntPtr sizing = info.HbmColor != IntPtr.Zero ? info.HbmColor : info.HbmMask;
            if (GetObjectW(sizing, Marshal.SizeOf<BitmapInfo>(), out var bitmap) == 0) return null;
            size = bitmap.Width;
        }
        finally
        {
            if (info.HbmMask != IntPtr.Zero) DeleteObject(info.HbmMask);
            if (info.HbmColor != IntPtr.Zero) DeleteObject(info.HbmColor);
        }
        if (size <= 0 || size > 256) return null;

        var overBlack = Render(cursor, size, 0x00000000u);
        var overWhite = Render(cursor, size, 0xFFFFFFFFu);
        if (overBlack == null || overWhite == null) return null;

        // Premultiplied BGRA: the colour over black is c*a, and white - black = 255*(1-a).
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            uint b = overBlack[i], w = overWhite[i];
            int coverage = 255 - Math.Clamp(((int)(w & 0xFF) - (int)(b & 0xFF)
                + (int)((w >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)
                + (int)((w >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) / 3, 0, 255);
            pixels[i * 4 + 0] = (byte)Math.Min(b & 0xFF, (uint)coverage);
            pixels[i * 4 + 1] = (byte)Math.Min((b >> 8) & 0xFF, (uint)coverage);
            pixels[i * 4 + 2] = (byte)Math.Min((b >> 16) & 0xFF, (uint)coverage);
            pixels[i * 4 + 3] = (byte)coverage;
        }
        var image = new WriteableBitmap(new PixelSize(size, size), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = image.Lock())
        {
            for (int row = 0; row < size; row++)
                Marshal.Copy(pixels, row * size * 4, locked.Address + row * locked.RowBytes, size * 4);
        }
        return new ArrowImage(image, new PixelPoint(info.XHotspot, info.YHotspot));
    }

    /// <summary>The cursor drawn over a solid <paramref name="background"/>, top-down BGRA pixels.</summary>
    private static uint[]? Render(IntPtr cursor, int size, uint background)
    {
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size, // top-down
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        IntPtr dib = CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(dc);
            return null;
        }
        IntPtr old = SelectObject(dc, dib);
        try
        {
            var fill = new int[size * size];
            Array.Fill(fill, unchecked((int)background));
            Marshal.Copy(fill, 0, bits, fill.Length);
            if (!DrawIconEx(dc, 0, 0, cursor, size, size, 0, IntPtr.Zero, DiNormal)) return null;
            var read = new int[size * size];
            Marshal.Copy(bits, read, 0, read.Length);
            return Array.ConvertAll(read, p => unchecked((uint)p));
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }
}
