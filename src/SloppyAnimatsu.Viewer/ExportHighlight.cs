using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SloppyAnimatsu.Viewer;

/// <summary>
/// The one look every list uses for "the Export Settings window would write this": an amber bar on
/// the row's left edge over a faint amber tint, drawn behind the row and never hit-tested, so
/// selection colours and clicks are unchanged.
/// </summary>
internal static class ExportHighlight
{
    public static readonly IBrush Bar = new SolidColorBrush(Color.FromRgb(0xf0, 0xa0, 0x32));
    public static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(46, 0xf0, 0xa0, 0x32));

    /// <summary>A row's mark, off until <see cref="Apply"/> lights it.</summary>
    public static Border NewMark() => new()
    {
        IsHitTestVisible = false,
        BorderBrush = Bar,
        BorderThickness = new Thickness(3, 0, 0, 0),
        Background = Fill,
        IsVisible = false,
    };

    public static void Apply(Border mark, bool lit) => mark.IsVisible = lit;
}
