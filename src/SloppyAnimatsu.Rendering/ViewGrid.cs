using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>A view's rectangle in the viewport, normalized to 0–1 with the origin at the top left.</summary>
public readonly record struct ViewRect(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) =>
        x >= X && y >= Y && x < X + Width && y < Y + Height;

    /// <summary>The rectangle in pixels (or DIPs) for a viewport of the given size.</summary>
    public (float X, float Y, float Width, float Height) Scale(float width, float height) =>
        (X * width, Y * height, Width * width, Height * height);
}

/// <summary>
/// Where the multi-view layouts put their views. The
/// view rectangles are runtime UI and are not stored, so this split is a viewer design: the
/// single view fills the area and the two-view layouts split it left/right. The four-view grid
/// follows the order the app fills it in - clockwise from the top left: view 0 top left, view 1
/// top right, view 2 bottom right, view 3 bottom left
/// quadrants Photo, Front in the top row and Top, Left in the bottom row for the written views
/// Photo, Front, Left, Top).
/// </summary>
public static class ViewGrid
{
    /// <summary>The view rectangles of <paramref name="layout"/>, in view order.</summary>
    public static IReadOnlyList<ViewRect> Rects(SceneLayout layout) => layout switch
    {
        SceneLayout.SingleView => [new(0, 0, 1, 1)],
        SceneLayout.TwoViews or SceneLayout.TwoViewsCurveboard =>
        [
            new(0, 0, 0.5f, 1),
            new(0.5f, 0, 0.5f, 1),
        ],
        SceneLayout.FourViews =>
        [
            new(0, 0, 0.5f, 0.5f),
            new(0.5f, 0, 0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f, 0.5f),
            new(0, 0.5f, 0.5f, 0.5f),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    /// <summary>The index of the view under a point in a viewport of the given size, or -1.</summary>
    public static int ViewAt(IReadOnlyList<ViewRect> rects, float x, float y, float width, float height)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            var (rx, ry, rw, rh) = rects[i].Scale(width, height);
            if (x >= rx && y >= ry && x < rx + rw && y < ry + rh) return i;
        }
        return -1;
    }
}
