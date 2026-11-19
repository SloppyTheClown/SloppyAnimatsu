using System.Numerics;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// What the viewport asks of its window while a pointer gesture runs (points in viewport DIPs
/// unless a method says "edit point", which is the letterboxed edit area's space).
/// </summary>
public interface IViewportPointerHost
{
    /// <summary>The active view is the orbit camera; a scene camera view is fixed.</summary>
    bool Orbiting { get; }

    /// <summary>
    /// The active view takes the middle-drag pan and the right-drag zoom: the orbit camera, and
    /// an orthographic scene camera, which cannot be orbited but is still moved over its plane.
    /// </summary>
    bool Slides => Orbiting;

    /// <summary>The index of the view presses edit and the active view of the layout.</summary>
    int ActiveView { get; }

    /// <summary>The view drawn under a viewport point, or -1.</summary>
    int ViewAt(Vector2 position);

    /// <summary>A viewport point in the active view's edit area.</summary>
    Vector2 EditPoint(Vector2 position);

    /// <summary>A press in another view of a multi-view layout makes it the active view.</summary>
    void FocusView(Vector2 position);

    /// <summary>The gizmo handle drawn under an edit point and within the tolerance, or -1.</summary>
    int GizmoAt(Vector2 editPoint);

    /// <summary>A gizmo drag began on <paramref name="axis"/>; false refuses it (the press may still become a tool's or the camera's).</summary>
    bool BeginEdit(int axis, Vector2 editPoint);

    /// <summary>A tool's own drag (the Skin Workshop's brush) began here; false leaves the camera the press.</summary>
    bool BeginFree(Vector2 editPoint);

    /// <summary>The pointer moved during a gizmo drag.</summary>
    void Edit(Vector2 from, Vector2 to);

    /// <summary>The gizmo drag ended: kept on release, dropped on Escape.</summary>
    void EndEdit(bool commit);

    /// <summary>The pointer moved during a tool's own drag.</summary>
    void Free(Vector2 from, Vector2 to);

    /// <summary>The tool's drag ended: kept on release, dropped on Escape.</summary>
    void EndFree(bool commit);

    /// <summary>A click (a press that did not move) picks at the edit point.</summary>
    void Pick(Vector2 editPoint, bool toggle);

    /// <summary>
    /// A right click (a right press that did not move) at the edit point: the viewer's right-click
    /// action on the object there (a right click toggles the
    /// IK lock).
    /// </summary>
    void RightClick(Vector2 editPoint);

    /// <summary>The pointer hovered with no button held: the gizmo's hover handle and the Scanner's point.</summary>
    void Hover(Vector2 position);

    /// <summary>The pointer left a view, so no gizmo handle is under it.</summary>
    void ClearHighlight();

    /// <summary>A left drag past the click slop orbits the camera by the pointer's delta.</summary>
    void Orbit(Vector2 delta);

    /// <summary>A middle drag pans the camera by the pointer's delta.</summary>
    void Pan(Vector2 delta);

    /// <summary>
    /// A right drag zooms the camera by the pointer's delta: right and up zoom in, left and down
    /// zoom out (both axes count, so an up-right drag zooms fastest).
    /// </summary>
    void Zoom(Vector2 delta);

    /// <summary>
    /// A press with a selection modifier (Shift, Ctrl or Alt) asks to start a vertex selection
    /// gesture: the rectangle (left button, a click picks) or the lasso (middle button)
    /// False leaves the press to the camera and the gizmo.
    /// </summary>
    bool BeginMarquee(Vector2 editPoint, bool lasso) => false;

    /// <summary>The pointer moved during a vertex selection gesture.</summary>
    void Marquee(Vector2 editPoint) { }

    /// <summary>The vertex selection gesture ended: applied on release, dropped on Escape.</summary>
    void EndMarquee(bool commit) { }
}

/// <summary>
/// The viewport's pointer wiring as a rule that is tested without a window
/// a left press on a gizmo handle starts that handle's
/// drag, a press a tool claims (the Skin Workshop's brush) starts the tool's, and every other
/// left press stays a click until the pointer has moved <see cref="ClickSlop"/> DIP - then it
/// orbits. A middle drag pans and a right drag zooms at once (in an orthographic view too, which
/// only refuses the orbit), a click that did not move picks on
/// release, Escape drops the edit and a lost capture keeps it. The viewer's <c>Viewport</c> feeds
/// this class its pointer events and does what the host calls say; the camera itself (how far a
/// drag turns, slides or dollies) is the host's.
/// </summary>
public sealed class ViewportInput
{
    /// <summary>How far (DIPs) a press may move and still be the click that picks.</summary>
    public const float ClickSlop = 4;

    /// <summary>How close (DIPs) the pointer must be to a gizmo handle to grab it.</summary>
    public const float HandleTolerance = 7;

    /// <summary>The last pointer position while a press is tracked, in viewport DIPs.</summary>
    private Vector2? _last;

    private Vector2 _pressPoint;
    private bool _moved;

    /// <summary>Whether a press is being tracked (from <see cref="Press"/> until a release, Escape or a lost capture).</summary>
    public bool Tracking => _last != null;

    /// <summary>Whether the pointer has left the click: it moved more than <see cref="ClickSlop"/> DIP since the press.</summary>
    public bool Moved => _moved;

    /// <summary>Whether a gizmo handle drag is running.</summary>
    public bool Editing { get; private set; }

    /// <summary>Whether a tool's own drag (the Skin Workshop's brush) is running.</summary>
    public bool Free { get; private set; }

    /// <summary>Whether a vertex selection rectangle or lasso is running.</summary>
    public bool Marquee { get; private set; }

    /// <summary>
    /// A pointer press at <paramref name="position"/> (viewport DIPs). <paramref name="left"/> says
    /// the pressed button is the left one: only a left press can start an edit or a tool drag, and
    /// only a left press becomes the pick on release.
    /// </summary>
    public void Press(IViewportPointerHost host, Vector2 position, bool left, bool middle = false, bool selecting = false)
    {
        _last = _pressPoint = position;
        _moved = false;
        Editing = false;
        Free = false;
        Marquee = false;
        host.FocusView(position);
        if (selecting && (left || middle) && host.BeginMarquee(host.EditPoint(position), lasso: !left))
        {
            Marquee = true;
            return;
        }
        if (!left) return;
        var at = host.EditPoint(position);
        int axis = host.GizmoAt(at);
        if (axis >= 0 && host.BeginEdit(axis, at))
        {
            Editing = true;
            return;
        }
        if (host.BeginFree(at)) Free = true;
    }

    /// <summary>
    /// The pointer moved to <paramref name="position"/> with the given buttons held. With no press
    /// tracked this is the hover; during an edit or a tool drag it is that drag (from the press
    /// point); otherwise a view switch clears the gizmo highlight, a left drag past the slop orbits,
    /// a middle drag pans and a right drag zooms.
    /// </summary>
    public void Move(IViewportPointerHost host, Vector2 position, bool left, bool middle, bool right)
    {
        if (_last is not { } last)
        {
            host.Hover(position);
            return;
        }
        var delta = position - last;
        _last = position;
        if (!_moved && Vector2.Distance(position, _pressPoint) > ClickSlop) _moved = true;
        if (Editing)
        {
            host.Edit(host.EditPoint(_pressPoint), host.EditPoint(position));
            return;
        }
        if (Free)
        {
            host.Free(host.EditPoint(_pressPoint), host.EditPoint(position));
            return;
        }
        if (Marquee)
        {
            host.Marquee(host.EditPoint(position));
            return;
        }
        if (host.ViewAt(position) != host.ActiveView)
        {
            // The pointer is over another view: it has no handle under it.
            host.ClearHighlight();
            return;
        }
        if (!host.Slides) return;
        if (left)
        {
            if (!_moved || !host.Orbiting) return;
            host.Orbit(delta);
        }
        else if (middle)
            host.Pan(delta);
        else if (right)
            host.Zoom(delta);
        else
            host.Hover(position);
    }

    /// <summary>
    /// The pointer was released at <paramref name="position"/>. <paramref name="left"/> and
    /// <paramref name="right"/> name the button the press was made with: a left press that did not
    /// move picks (with <paramref name="toggle"/> for Ctrl), a right press that did not move is the
    /// right click, both only while the pointer is still over the active view. An edit or tool drag
    /// is kept.
    /// </summary>
    public void Release(IViewportPointerHost host, Vector2 position, bool left, bool right, bool toggle)
    {
        bool pressed = _last != null;
        _last = null;
        if (Editing)
        {
            Editing = false;
            host.EndEdit(true);
        }
        else if (Free)
        {
            Free = false;
            host.EndFree(true);
        }
        else if (Marquee)
        {
            Marquee = false;
            host.EndMarquee(true);
        }
        else if (pressed && !_moved && host.ViewAt(position) == host.ActiveView)
        {
            if (left) host.Pick(host.EditPoint(position), toggle);
            else if (right) host.RightClick(host.EditPoint(position));
        }
        host.Hover(position);
    }

    /// <summary>
    /// The pointer capture was lost (the pointer left the window, another control took it): the
    /// drag is kept, as a release would keep it.
    /// </summary>
    public void CaptureLost(IViewportPointerHost host)
    {
        _last = null;
        bool editing = Editing, free = Free, marquee = Marquee;
        Editing = false;
        Free = false;
        Marquee = false;
        if (editing) host.EndEdit(true);
        else if (free) host.EndFree(true);
        else if (marquee) host.EndMarquee(true);
    }

    /// <summary>
    /// Escape: drops the running edit or tool drag, and no later release picks. Returns whether
    /// there was a drag to drop.
    /// </summary>
    public bool Cancel(IViewportPointerHost host)
    {
        if (!Editing && !Free && !Marquee) return false;
        _last = null;
        if (Editing)
        {
            Editing = false;
            host.EndEdit(false);
        }
        else if (Marquee)
        {
            Marquee = false;
            host.EndMarquee(false);
        }
        else
        {
            Free = false;
            host.EndFree(false);
        }
        return true;
    }
}
