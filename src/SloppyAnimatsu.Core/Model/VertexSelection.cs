using System.Numerics;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// How a Skinned Mesh Vertices Selection gesture combines with the selection already there
/// Shift replaces, Ctrl adds, Alt removes.
/// </summary>
public enum VertexSelectOp
{
    Replace = 0,
    Add = 1,
    Remove = 2,
}

/// <summary>The screen shapes a vertex selection gesture draws: the pick, the rectangle and the lasso.</summary>
public static class VertexSelectRegion
{
    /// <summary>Pixels (DIPs) within which a click picks the nearest control point.</summary>
    public const float PickTolerance = 8f;

    /// <summary>The modifier rule: Alt removes, Ctrl adds, Shift alone replaces; null when none is held.</summary>
    public static VertexSelectOp? OpFor(bool shift, bool control, bool alt) =>
        alt ? VertexSelectOp.Remove : control ? VertexSelectOp.Add : shift ? VertexSelectOp.Replace : null;

    /// <summary>The rectangle between two corners, in either order.</summary>
    public static Func<Vector2, bool> Rectangle(Vector2 a, Vector2 b)
    {
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        return p => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
    }

    /// <summary>The closed polygon a lasso drew (even-odd rule); fewer than three points hold nothing.</summary>
    public static Func<Vector2, bool> Lasso(IReadOnlyList<Vector2> outline)
    {
        var points = outline.ToArray();
        if (points.Length < 3) return _ => false;
        var min = points.Aggregate(Vector2.Min);
        var max = points.Aggregate(Vector2.Max);
        return p =>
        {
            if (p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y) return false;
            bool inside = false;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                var a = points[i];
                var b = points[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        };
    }
}
