// Skinned Mesh Vertices Selection: Shift + click or a left-button
// rectangle, Shift + a middle-button lasso, Ctrl adds and Alt removes, numpad + - * / grow, shrink,
// select all (flood) and invert. The gesture and its red pointer run anywhere in the Rigging context;
// it selects only on the skinned meshes of the character that is selected (or whose object is). The selection is the Skin Workshop session's, so the
// weight list, typed weights, copy/paste, Hide and the brush all follow it.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.

using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The rectangle or lasso being drawn, over the viewport (edit-area points).</summary>
    private readonly Avalonia.Controls.Shapes.Polyline _marqueeShape = new()
    {
        StrokeThickness = 1.0,
        IsHitTestVisible = false,
        IsVisible = false,
    };

    private readonly List<Vector2> _marqueePoints = new();
    private bool _marqueeLasso;
    private VertexSelectOp _marqueeOp;

    /// <summary>Selection colour of the vertex dots; the last selected one is white.</summary>
    private static readonly Vector3 VertexSelectedColor = new(1f, 0.55f, 0.1f);

    /// <summary>
    /// The operation a press with <paramref name="m"/> starts, or null when it is no vertex gesture.
    /// The gesture runs anywhere in the Rigging context (the app draws it with nothing selected and
    /// then selects nothing). Without Shift, a Ctrl/Alt click that does not drag still picks objects
    /// (<see cref="IViewportEditor.EndMarquee"/>), so only the drag selects control points.
    /// </summary>
    private VertexSelectOp? VertexGestureOp(KeyModifiers m)
    {
        if (_pose == null || _pose.Model.Scene.Context != InterfaceContext.Rigging) return null;
        return VertexSelectRegion.OpFor((m & KeyModifiers.Shift) != 0, (m & KeyModifiers.Control) != 0, (m & KeyModifiers.Alt) != 0);
    }

    /// <summary>
    /// Whether the gesture has vertices to work on: a character with a skinned mesh, and the
    /// character itself or one of its objects (joint, controller, mesh) in the selection. Opens the
    /// Skin Workshop session on its mesh when there is none yet.
    /// </summary>
    private bool VertexSelectionAvailable()
    {
        if (_pose == null || _character == null) return false;
        if (_pose.Model.Scene.Context != InterfaceContext.Rigging) return false;
        if (!_character.Meshes.Any(m => m.Skinned)) return false;
        var evaluator = _pose.Evaluator(_character);
        bool owned = _selectedAll.Any(o => ReferenceEquals(o, _character) || evaluator.IndexOf(o) >= 0
            || (o is Mesh mesh && _character.Meshes.Contains(mesh)));
        if (!owned) return false;
        if (_skin == null) RefreshSkinPanel();
        return _skin != null;
    }

    /// <summary>A Shift/Ctrl/Alt key went down or up: the viewport's pointer follows when the pointer is over it.</summary>
    private void ModifiersChanged(KeyEventArgs e, bool down)
    {
        KeyModifiers changed = e.Key switch
        {
            Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
            Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
            Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
            _ => KeyModifiers.None,
        };
        if (changed == KeyModifiers.None || !_viewport.IsPointerOver) return;
        var modifiers = down ? e.KeyModifiers | changed : e.KeyModifiers & ~changed;
        _viewport.RefreshSelectCursor(modifiers);
    }

    /// <summary>
    /// The vertex selection pointer while a vertex gesture would start; otherwise, for a click on
    /// objects, a "-" badge while Alt is held (removes) and a "+" badge while Ctrl is (adds), as
    /// <see cref="ClickCombine"/> reads them; null keeps the default pointer.
    /// </summary>
    Cursor? IViewportEditor.SelectCursor(KeyModifiers modifiers) =>
        VertexGestureOp(modifiers) is { } op ? VertexSelectCursor.For(op)
        : (modifiers & KeyModifiers.Alt) != 0 ? PickCursor.For(add: false)
        : (modifiers & KeyModifiers.Control) != 0 ? PickCursor.For(add: true)
        : null;

    bool IViewportEditor.BeginMarquee(Vector2 at, bool lasso)
    {
        if (VertexGestureOp(_viewport.PressModifiers) is not { } chosen) return false;
        _marqueeOp = chosen;
        _marqueeLasso = lasso;
        _marqueePoints.Clear();
        _marqueePoints.Add(at);
        UpdateMarqueeShape();
        return true;
    }

    void IViewportEditor.Marquee(Vector2 at)
    {
        if (_marqueeLasso)
        {
            if (Vector2.Distance(_marqueePoints[^1], at) >= 2f) _marqueePoints.Add(at);
        }
        else if (_marqueePoints.Count == 1) _marqueePoints.Add(at);
        else _marqueePoints[1] = at;
        UpdateMarqueeShape();
    }

    void IViewportEditor.EndMarquee(bool commit)
    {
        var points = _marqueePoints.ToList();
        _marqueePoints.Clear();
        _marqueeShape.IsVisible = false;
        if (!commit || points.Count == 0) return;
        // Ctrl/Alt without Shift: a click that did not drag keeps its object meaning (Ctrl adds or
        // toggles a joint, Alt removes one; a middle click picks the hierarchy), and only a drag
        // selects control points. Shift + click still picks the nearest control point.
        if ((_viewport.PressModifiers & KeyModifiers.Shift) == 0
            && points.All(p => Vector2.Distance(points[0], p) <= ViewportInput.ClickSlop))
        {
            if (_marqueeLasso) ((IViewportEditor)this).MiddleClick(points[0]);
            else ((IViewportEditor)this).Pick(points[0], (_viewport.PressModifiers & KeyModifiers.Control) != 0);
            return;
        }
        if (!VertexSelectionAvailable())
        {
            _status.Text = "Select a skinned character (or one of its joints) to select its control points.";
            return;
        }
        var view = _viewport.Renderer.ActiveView;
        var size = _viewport.EditSize;
        Func<Vector3, Vector2?> project = p => view.Project(p, size);
        bool click = !_marqueeLasso && (points.Count < 2 || Vector2.Distance(points[0], points[1]) <= ViewportInput.ClickSlop);
        Func<Vector2, bool> inside = _marqueeLasso
            ? VertexSelectRegion.Lasso(points)
            : VertexSelectRegion.Rectangle(points[0], points.Count > 1 ? points[1] : points[0]);

        // The gesture works on the Skin Workshop's mesh; when it takes nothing there, it moves to
        // the character's skinned mesh that it does reach.
        if (CountHits(_skin!.Mesh) == 0)
        {
            Mesh? best = null;
            int bestHits = 0;
            foreach (var mesh in _character!.Meshes.Where(x => x.Skinned && x != _skin.Mesh))
            {
                int hits = CountHits(mesh);
                if (hits > bestHits) { best = mesh; bestHits = hits; }
            }
            if (best != null) SelectSkinMesh(best);
        }
        var skin = _skin!;
        skin.Eye = view.Eye;
        var meshSkin = new MeshSkin(skin.Mesh);
        var positions = meshSkin.Positions(_pose!.ControllerWorlds);
        var normals = meshSkin.Normals(_pose.ControllerWorlds);
        if (click)
        {
            int v = skin.PickVertex(positions, normals, project, points[0], _marqueeOp);
            _status.Text = v < 0
                ? $"No control point under the pointer; {skin.Selection.SelectedCount} selected on {skin.Mesh.Name}."
                : $"Control point {v} ({OpWord(_marqueeOp)}); {skin.Selection.SelectedCount} selected on {skin.Mesh.Name}.";
        }
        else
        {
            int changed = skin.SelectRegion(positions, normals, project, inside, _marqueeOp);
            _status.Text = $"{(_marqueeLasso ? "Lasso" : "Rectangle")} {OpWord(_marqueeOp)} {changed} control point(s); {skin.Selection.SelectedCount} selected on {skin.Mesh.Name}.";
        }
        AfterVertexSelection();

        int CountHits(Mesh mesh)
        {
            var ms = new MeshSkin(mesh);
            var pos = ms.Positions(_pose!.ControllerWorlds);
            int hits = 0;
            float tolerance = VertexSelectRegion.PickTolerance;
            for (int i = 0; i < pos.Length; i++)
            {
                if (mesh.HasHiddenVertices && mesh.IsVertexHidden(i)) continue;
                if (project(pos[i]) is not { } s) continue;
                if (click ? Vector2.Distance(s, points[0]) <= tolerance : inside(s)) hits++;
            }
            return hits;
        }
    }

    private static string OpWord(VertexSelectOp op) => op switch
    {
        VertexSelectOp.Add => "added",
        VertexSelectOp.Remove => "removed",
        _ => "selected",
    };

    /// <summary>Draws the rectangle or lasso in the viewport's coordinates; red when Full Paint &amp; Vertex Select Mode is on.</summary>
    private void UpdateMarqueeShape()
    {
        var origin = _viewport.EditArea.Origin;
        var points = new List<Point>();
        if (_marqueeLasso)
        {
            foreach (var p in _marqueePoints) points.Add(new Point(p.X + origin.X, p.Y + origin.Y));
            if (points.Count > 2) points.Add(points[0]);
        }
        else if (_marqueePoints.Count > 1)
        {
            var a = _marqueePoints[0];
            var b = _marqueePoints[1];
            double ax = a.X + origin.X, ay = a.Y + origin.Y, bx = b.X + origin.X, by = b.Y + origin.Y;
            points.AddRange([new Point(ax, ay), new Point(bx, ay), new Point(bx, by), new Point(ax, by), new Point(ax, ay)]);
        }
        bool fullPaint = _skin?.FrontBackSelect ?? true;
        _marqueeShape.Stroke = fullPaint ? Brushes.Red : Brushes.White;
        _marqueeShape.Points = points;
        _marqueeShape.IsVisible = points.Count > 1;
    }

    /// <summary>Numpad + - * / on the vertex selection; false when the keys do not apply now.</summary>
    private bool RunVertexSelectionKey(Key key)
    {
        if (!VertexSelectionAvailable()) return false;
        var skin = _skin!;
        switch (key)
        {
            case Key.Add:
                _status.Text = $"Grew the selection by {skin.GrowSelection()}; {skin.Selection.SelectedCount} selected.";
                break;
            case Key.Subtract:
                _status.Text = $"Shrank the selection by {skin.ShrinkSelection()}; {skin.Selection.SelectedCount} selected.";
                break;
            case Key.Multiply:
                _status.Text = $"Selected {skin.SelectAllVertices()} control point(s) of {skin.Mesh.Name}.";
                break;
            case Key.Divide:
                _status.Text = $"Inverted the selection: {skin.InvertSelection()} control point(s) of {skin.Mesh.Name}.";
                break;
            default:
                return false;
        }
        AfterVertexSelection();
        return true;
    }

    /// <summary>Q (<c>PaintFrontBack</c>): Full Paint &amp; Vertex Select Mode, paint and select through the mesh.</summary>
    private void ToggleFullPaint()
    {
        _skinFrontBack.IsChecked = _skinFrontBack.IsChecked != true;
        _status.Text = _skinFrontBack.IsChecked == true
            ? "Full Paint & Vertex Select Mode on: paint and select through the mesh."
            : "Full Paint & Vertex Select Mode off: front-facing control points only.";
    }

    /// <summary>After a selection change: the weight list follows it and the dots are redrawn.</summary>
    private void AfterVertexSelection()
    {
        RebuildSkinJoints();
        RefreshVertexPoints();
        _viewport.RequestNextFrameRendering();
    }

    /// <summary>Rebuilds the selected control points' dots for the current pose.</summary>
    private void RefreshVertexPoints()
    {
        var renderer = _viewport.Renderer;
        var skin = _skin;
        if (skin == null || _pose == null || skin.Selection.SelectedCount == 0 ||
            _pose.Model.Scene.Context != InterfaceContext.Rigging)
        {
            renderer.VertexPoints = [];
            return;
        }
        var positions = SkinMeshPositions(skin.Mesh);
        var data = new List<float>(skin.Selection.SelectedCount * 6);
        foreach (int v in skin.Selection.SelectedVertices())
        {
            if (v >= positions.Length) continue;
            var c = v == skin.CurrentVertex ? Vector3.One : VertexSelectedColor;
            var p = positions[v];
            data.AddRange([p.X, p.Y, p.Z, c.X, c.Y, c.Z]);
        }
        renderer.VertexPoints = data.ToArray();
    }
}
