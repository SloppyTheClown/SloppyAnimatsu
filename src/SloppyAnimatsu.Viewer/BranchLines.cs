using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The lines in the TRANSFORM window that join a group's symbol to its value rows: a stub from the
/// symbol's middle to a vertical spine, and one branch from the spine to each row. It spans the
/// group's rows and assumes they share one height and the grid's row spacing.
/// </summary>
public sealed class BranchLines : Control
{
    private static readonly IPen Pen = new Pen(new SolidColorBrush(Color.FromArgb(220, 150, 150, 150)), 1);

    public BranchLines(int rows, double rowSpacing)
    {
        Rows = Math.Max(1, rows);
        RowSpacing = rowSpacing;
        Width = 14;
    }

    public int Rows { get; }

    public double RowSpacing { get; }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        double pitch = (h + RowSpacing) / Rows;
        double spine = Math.Round(w * 0.4) + 0.5;
        double first = Centre(0), last = Centre(Rows - 1);
        double middle = Math.Round(h / 2) + 0.5;
        context.DrawLine(Pen, new Point(0, middle), new Point(spine, middle));
        if (Rows > 1) context.DrawLine(Pen, new Point(spine, Math.Min(first, middle)), new Point(spine, Math.Max(last, middle)));
        for (int i = 0; i < Rows; i++)
        {
            double y = Centre(i);
            context.DrawLine(Pen, new Point(spine, y), new Point(w, y));
        }

        double Centre(int i) => Math.Round(i * pitch + (pitch - RowSpacing) / 2) + 0.5;
    }
}
