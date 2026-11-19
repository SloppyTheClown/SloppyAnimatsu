using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Rendering;
using Silk.NET.OpenGL;

namespace SloppyAnimatsu.Viewer;

/// <summary>What the viewport asks of its owner for picking and gizmo drags (points in viewport DIPs).</summary>
public interface IViewportEditor
{
    /// <summary>
    /// A click that did not drag; picks what is under <paramref name="at"/>, adding it to or
    /// removing it from the selection when <paramref name="toggle"/> (Ctrl held).
    /// </summary>
    void Pick(Vector2 at, bool toggle);

    /// <summary>
    /// A right click (a right press that did not move) at <paramref name="at"/>: acts on the object
    /// there, as the right-click on a controller does (the IK lock,
    ///).
    /// </summary>
    void RightClick(Vector2 at);

    /// <summary>A press on gizmo handle <paramref name="axis"/>; false refuses the drag (the press orbits instead).</summary>
    bool BeginDrag(int axis, Vector2 at);

    /// <summary>
    /// A left press that is not on a gizmo handle, before it turns into an orbit: true takes the
    /// drag for a tool of its own (the Skin Workshop's brush), so the camera does not move.
    /// </summary>
    bool BeginFreeDrag(Vector2 at);

    /// <summary>The pointer moved during a free drag that started at <paramref name="from"/>.</summary>
    void FreeDrag(Vector2 from, Vector2 to);

    /// <summary>The free drag ended: kept on release, dropped on Escape.</summary>
    void FreeDragEnd(bool commit);

    /// <summary>The pointer moved during a drag that started at <paramref name="from"/>.</summary>
    void Drag(Vector2 from, Vector2 to);

    /// <summary>The drag ended: kept on release, dropped on Escape.</summary>
    void EndDrag(bool commit);

    /// <summary>The pointer moved with no button held: the Skin Workshop's Scanner reads the point under it.</summary>
    void HoverAt(Vector2 at);

    /// <summary>A Shift/Ctrl/Alt press: true starts a vertex selection rectangle (left) or lasso (middle).</summary>
    bool BeginMarquee(Vector2 at, bool lasso) => false;

    /// <summary>The pointer to show while <paramref name="modifiers"/> are held: the vertex selection one, or null for the default.</summary>
    Cursor? SelectCursor(KeyModifiers modifiers) => null;

    /// <summary>The pointer moved during a vertex selection gesture.</summary>
    void Marquee(Vector2 at) { }

    /// <summary>The vertex selection gesture ended: applied on release, dropped on Escape.</summary>
    void EndMarquee(bool commit) { }
}

/// <summary>
/// Hosts <see cref="SceneRenderer"/> in Avalonia's GL surface. Left drag orbits, middle drag pans,
/// right drag zooms (right/up in, left/down out), the wheel zooms, F frames everything. With an
/// <see cref="Editor"/>, a left click picks and a left drag on a gizmo handle edits; Escape cancels
/// that drag. The press, move and release rules themselves are <see cref="ViewportInput"/>
/// this control only feeds them the events and follows the
/// calls they make.
/// </summary>
public sealed class Viewport : OpenGlControlBase, IViewportPointerHost
{
    /// <summary>How close (DIPs) the pointer must be to a gizmo handle.</summary>
    public const float HandleTolerance = ViewportInput.HandleTolerance;

    /// <summary>The pointer's press, move and release rules; this control only feeds and follows them.</summary>
    private readonly ViewportInput _input = new();

    public Viewport()
    {
        Focusable = true;
    }

    /// <summary>
    /// Avalonia hit-tests what a control draws, not its layout bounds: the GL image is a child
    /// composition surface that the hit tester does not map back to this control, so without a
    /// drawn area every press and wheel fell through to the black <c>Border</c> behind the
    /// viewport and no camera gesture ever began. A transparent fill over the bounds makes this
    /// control the pointer's target while showing nothing (the same rule as
    /// <c>Background="Transparent"</c>).
    /// </summary>
    public override void Render(DrawingContext context) =>
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    public SceneRenderer Renderer { get; } = new() { ImageDecoder = ImageDecoding.Decode };

    public IViewportEditor? Editor { get; set; }

    /// <summary>The viewport size in DIPs, the space of the <see cref="IViewportEditor"/> points.</summary>
    public Vector2 Size => new((float)Bounds.Width, (float)Bounds.Height);

    /// <summary>The area of the viewport the active view is drawn in (DIPs); a photo view is letterboxed.</summary>
    public (Vector2 Origin, Vector2 Size) EditArea => Renderer.RenderArea(Size);

    /// <summary>The edit area's size, the space of the <see cref="IViewportEditor"/> points.</summary>
    public Vector2 EditSize => EditArea.Size;

    /// <summary>The drawn area of one view of a multi-view layout (DIPs).</summary>
    public (Vector2 Origin, Vector2 Size) ViewArea(int index) => Renderer.ViewArea(index, Size);

    /// <summary>The view under a pointer position, or -1.</summary>
    public int ViewAt(Point position) =>
        Renderer.ViewAt(new Vector2((float)position.X, (float)position.Y), Size);

    /// <summary>Raised when a click focuses another view of a multi-view layout.</summary>
    public event Action<int>? ViewFocused;

    /// <summary>A pointer position in the viewport, in the edit area's coordinates.</summary>
    public Vector2 ToEdit(Point position) => new((float)position.X - EditArea.Origin.X, (float)position.Y - EditArea.Origin.Y);

    /// <summary>A pointer position as a point of the viewport's own space, the space of <see cref="ViewportInput"/>.</summary>
    private static Vector2 At(Point position) => new((float)position.X, (float)position.Y);

    /// <summary>Whether the active view is the orbit camera (a scene camera view is fixed).</summary>
    public bool Orbiting => Renderer.ActiveIsOrbit;

    /// <summary>Keys the viewport does not use itself.</summary>
    /// <summary>The live key map (Hotkeys.PanelActions); the shipped keys until the window sets it.</summary>
    public SloppyAnimatsu.Core.Model.HotkeyMap Keys { get; set; } = SloppyAnimatsu.Core.Model.HotkeyMap.Default;

    public event Action<KeyEventArgs>? UnhandledKey;

    /// <summary>Set to get the next rendered frame through <see cref="FrameCaptured"/>.</summary>
    public bool CaptureNextFrame { get; set; }

    /// <summary>RGBA pixels (top row first), width, height.</summary>
    public event Action<byte[], int, int>? FrameCaptured;

    /// <summary>Set when GL setup failed (e.g. a shader did not compile).</summary>
    public event Action<string>? GlError;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            Renderer.Initialize(GL.GetApi(gl.GetProcAddress), GlVersion.Type == GlProfileType.OpenGLES);
        }
        catch (Exception e)
        {
            GlError?.Invoke(e.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl) => Renderer.Dispose();

    private readonly System.Diagnostics.Stopwatch _cameraClock = new();

    /// <summary>Raised after every GL frame (on the render thread); the axis-cube overlay follows the cameras with it.</summary>
    public event Action? Rendered;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!Renderer.IsInitialized) return;
        double scaling = VisualRoot?.RenderScaling ?? 1;
        int width = Math.Max(1, (int)(Bounds.Width * scaling));
        int height = Math.Max(1, (int)(Bounds.Height * scaling));
        long allocatedBefore = MemoryLog.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long started = MemoryLog.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // An axis-cube turn (OrbitCamera.AlignTo) runs on the render clock and asks for frames until it ends.
        float seconds = _cameraClock.IsRunning ? (float)_cameraClock.Elapsed.TotalSeconds : 0f;
        _cameraClock.Restart();
        bool turning = Renderer.StepCameras(Math.Min(seconds, 0.05f));
        Renderer.Render(fb, width, height);
        if (turning) RequestNextFrameRendering();
        Rendered?.Invoke();
        if (MemoryLog.Enabled)
            MemoryLog.Frame(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                System.Diagnostics.Stopwatch.GetTimestamp() - started);
        if (CaptureNextFrame)
        {
            CaptureNextFrame = false;
            FrameCaptured?.Invoke(Renderer.ReadPixels(width, height), width, height);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var position = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
        var properties = e.GetCurrentPoint(this).Properties;
        PressModifiers = e.KeyModifiers;
        bool selecting = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt)) != 0;
        _input.Press(this, At(position), properties.IsLeftButtonPressed, properties.IsMiddleButtonPressed, selecting);
    }

    /// <summary>The modifiers held at the last press (the vertex selection gesture's Shift/Ctrl/Alt).</summary>
    public KeyModifiers PressModifiers { get; private set; }

    /// <summary>A press in another view of a multi-view layout focuses it.</summary>
    internal void FocusView(Point position)
    {
        int view = ViewAt(position);
        if (view < 0 || view == Renderer.ActiveViewIndex) return;
        Renderer.ActiveViewIndex = view;
        RequestNextFrameRendering();
        ViewFocused?.Invoke(view);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        _input.Move(this, At(position), properties.IsLeftButtonPressed,
            properties.IsMiddleButtonPressed, properties.IsRightButtonPressed);
        RefreshSelectCursor(e.KeyModifiers);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        RefreshSelectCursor(e.KeyModifiers);
    }

    private Cursor? _selectCursor;

    /// <summary>
    /// Shows the vertex selection pointer (red, a box and a +/- badge) while the held modifiers would
    /// start that gesture here, the default pointer otherwise. A running gesture keeps the pointer it
    /// started with. The window calls this on modifier key changes too, since the focus may be elsewhere.
    /// </summary>
    public void RefreshSelectCursor(KeyModifiers modifiers)
    {
        if (_input.Marquee && _input.Tracking) return;
        var cursor = Editor?.SelectCursor(modifiers);
        if (ReferenceEquals(cursor, _selectCursor)) return;
        _selectCursor = cursor;
        Cursor = cursor;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var position = e.GetPosition(this);
        bool left = e.InitialPressMouseButton == MouseButton.Left;
        bool right = e.InitialPressMouseButton == MouseButton.Right;
        bool toggle = (e.KeyModifiers & KeyModifiers.Control) != 0;
        _input.Release(this, At(position), left, right, toggle);
        e.Pointer.Capture(null);
        RefreshSelectCursor(e.KeyModifiers);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _input.CaptureLost(this);
        base.OnPointerCaptureLost(e);
    }

    /// <summary>Highlights the gizmo handle under the pointer.</summary>
    private void Hover(Point position)
    {
        var gizmo = Renderer.Gizmo;
        int axis = ViewAt(position) != Renderer.ActiveViewIndex ? -1
            : gizmo.Visible ? gizmo.HitTest(Renderer.ActiveView, EditSize, ToEdit(position), HandleTolerance)
            : -1;
        if (axis == gizmo.ActiveAxis) return;
        gizmo.ActiveAxis = axis;
        RequestNextFrameRendering();
    }

    // ---- IViewportPointerHost: what ViewportInput asks of this control ----

    bool IViewportPointerHost.Orbiting => Orbiting;

    int IViewportPointerHost.ActiveView => Renderer.ActiveViewIndex;

    int IViewportPointerHost.ViewAt(Vector2 position) => ViewAt(new Point(position.X, position.Y));

    Vector2 IViewportPointerHost.EditPoint(Vector2 position) => ToEdit(new Point(position.X, position.Y));

    void IViewportPointerHost.FocusView(Vector2 position) => FocusView(new Point(position.X, position.Y));

    int IViewportPointerHost.GizmoAt(Vector2 at)
    {
        var gizmo = Renderer.Gizmo;
        return Editor != null && gizmo.Visible
            ? gizmo.HitTest(Renderer.ActiveView, EditSize, at, HandleTolerance)
            : -1;
    }

    bool IViewportPointerHost.BeginEdit(int axis, Vector2 at)
    {
        if (Editor == null || !Editor.BeginDrag(axis, at)) return false;
        Renderer.Gizmo.ActiveAxis = axis;
        RequestNextFrameRendering();
        return true;
    }

    bool IViewportPointerHost.BeginFree(Vector2 at) => Editor != null && Editor.BeginFreeDrag(at);

    void IViewportPointerHost.Edit(Vector2 from, Vector2 to) => Editor?.Drag(from, to);

    void IViewportPointerHost.EndEdit(bool commit)
    {
        Renderer.Gizmo.ActiveAxis = -1;
        Editor?.EndDrag(commit);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.Free(Vector2 from, Vector2 to)
    {
        Editor?.FreeDrag(from, to);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.EndFree(bool commit)
    {
        Editor?.FreeDragEnd(commit);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.Pick(Vector2 at, bool toggle) => Editor?.Pick(at, toggle);

    void IViewportPointerHost.RightClick(Vector2 at) => Editor?.RightClick(at);

    bool IViewportPointerHost.BeginMarquee(Vector2 at, bool lasso) => Editor != null && Editor.BeginMarquee(at, lasso);

    void IViewportPointerHost.Marquee(Vector2 at)
    {
        Editor?.Marquee(at);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.EndMarquee(bool commit)
    {
        Editor?.EndMarquee(commit);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.Hover(Vector2 position)
    {
        var point = new Point(position.X, position.Y);
        Hover(point);
        Editor?.HoverAt(ToEdit(point));
    }

    void IViewportPointerHost.ClearHighlight() => Renderer.Gizmo.ActiveAxis = -1;

    void IViewportPointerHost.Orbit(Vector2 delta)
    {
        if (Renderer.ActiveOrbit is { } orbit)
        {
            // The selection is the orbit's pivot when there is one.
            if (OrbitPivot?.Invoke() is { } pivot) orbit.OrbitAbout(-delta.X * 0.01f, delta.Y * 0.01f, pivot);
            else orbit.Orbit(-delta.X * 0.01f, delta.Y * 0.01f);
        }
        RequestNextFrameRendering();
    }

    /// <summary>The point a viewport orbit turns about (the selection's), or null for the view's own target.</summary>
    public Func<System.Numerics.Vector3?>? OrbitPivot { get; set; }

    void IViewportPointerHost.Pan(Vector2 delta)
    {
        Renderer.ActiveOrbit?.Pan(delta.X, delta.Y, Renderer.RenderArea(Size).Size.Y);
        RequestNextFrameRendering();
    }

    void IViewportPointerHost.Zoom(Vector2 delta)
    {
        Renderer.ActiveOrbit?.DragZoom(delta);
        RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        float factor = MathF.Pow(0.85f, (float)e.Delta.Y);
        if (Renderer.ActiveOrbit is not { } orbit) { e.Handled = true; return; }
        orbit.Zoom(factor);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _input.Cancel(this))
        {
            e.Handled = true;
            return;
        }
        if (Keys.ActionFor(SloppyAnimatsu.Core.Model.HotkeyScope.Viewport, ShortcutInput.FromKey(e.Key, e.KeyModifiers)) == "CenterCamera")
        {
            bool focused = Renderer.FocusSelection();
            AppLog.Info(focused ? "View: focus on the selection (F)" : "View: frame all (F, nothing selected)");
            RequestNextFrameRendering();
            e.Handled = true;
            return;
        }
        UnhandledKey?.Invoke(e);
    }
}


