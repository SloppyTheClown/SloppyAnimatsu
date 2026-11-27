// The Spinner dial over the viewport: its sectors drive the same TransformDrag as the gizmo's
// handles, through the viewport editor's BeginDrag/EndDrag, and its mode buttons call SetMode.
// The dial itself is SpinnerDialView; its layout and drag amounts are SloppyAnimatsu.Rendering.SpinnerDial
// Part of the partial MainWindow type.
using System.Numerics;
using Avalonia;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private SpinnerDialView? _spinnerDial;

    /// <summary>
    /// The transform space the gizmo handles and the Spinner's space button show. Not saved and
    /// not per mode: one viewer-wide choice, like the dial's place (the app stores one per mode in
    /// its profile).
    /// </summary>
    private TransformSpace _space = TransformSpace.Local;

    private SpinnerDialView CreateSpinnerDial()
    {
        var dial = new SpinnerDialView
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Mode = _viewport.Renderer.Gizmo.Mode,
            Space = _space,
        };
        dial.Moved = delta =>
        {
            Vector2 at = _spinnerDialAt ?? new Vector2((float)dial.Margin.Left, (float)dial.Margin.Top);
            _spinnerDialAt = at + delta;
            PlaceSpinnerDial();
        };
        dial.ModePicked = SetMode;
        dial.SpacePicked = SpinnerSpacePicked;
        dial.SnapPicked = SpinnerSnapPicked;
        dial.DragStarted = SpinnerDragStarted;
        dial.Dragged = SpinnerDragged;
        dial.DragEnded = commit =>
        {
            ((IViewportEditor)this).EndDrag(commit);
            _viewport.Renderer.Gizmo.ActiveAxis = -1;
            _viewport.RequestNextFrameRendering();
        };
        dial.Highlighted = handle =>
        {
            if (_drag == null)
            {
                _viewport.Renderer.Gizmo.ActiveAxis = handle;
                _viewport.RequestNextFrameRendering();
            }
        };
        HookFootSpinner(dial);
        _spinnerDial = dial;
        return dial;
    }

    /// <summary>Where the user dragged the dial (its top-left in the view area); null until then.</summary>
    private Vector2? _spinnerDialAt;

    /// <summary>
    /// Puts the dial at its dragged place, or by default left of the right-hand panel column and
    /// above the Timeline of the default layout, and keeps it inside the view area.
    /// </summary>
    private void PlaceSpinnerDial()
    {
        if (_spinnerDial == null || _floatHost == null) return;
        var host = new Vector2((float)_floatHost.Bounds.Width, (float)_floatHost.Bounds.Height);
        Vector2 at = SpinnerDial.Place(host, _spinnerDialAt);
        _spinnerDial.Margin = new Thickness(at.X, at.Y, 0, 0);
    }

    /// <summary>Brings the dial in line with the gizmo: its mode, space, and whether the selection can be edited in it.</summary>
    private void SyncSpinnerDial()
    {
        if (_spinnerDial == null) return;
        TransformGizmo gizmo = _viewport.Renderer.Gizmo;
        _spinnerDial.Mode = gizmo.Mode;
        _spinnerDial.Space = _space;
        _spinnerDial.Available = gizmo.Visible && gizmo.Mode switch
        {
            GizmoMode.Translate => gizmo.CanTranslate,
            GizmoMode.Scale => gizmo.CanScale,
            _ => true,
        };
        // An IK chain in the selection gives rotate mode its centre circle: the chain's swivel.
        // Animation keys and the Paint & Check pose swivel; the rest pose itself does not.
        _spinnerDial.Swivel = (_animation != null || InPaintAndCheck) && _selectedAll.Any(o => o is ControllerIK);
        _spinnerDial.Snap = _animation != null && _selectedAll.Any(o => o is ControllerIK);
    }

    /// <summary>
    /// The Spinner's Snap button: every selected IK / reverse-foot handle goes back on the end of its
    /// chain at the key under the playhead in the current stack
    /// One undo step.
    /// </summary>
    private void SpinnerSnapPicked()
    {
        var handles = _selectedAll.OfType<ControllerIK>().ToList();
        AnimStack? stack = _timeline.ActiveStack;
        if (handles.Count == 0 || stack == null || _pose?.Character == null)
        {
            _status.Text = "Select an IK or reverse-foot handle in an animation to snap it to its chain.";
            return;
        }
        PoseEditSession? session = Session;
        if (session == null) return;
        int frame = (int)MathF.Round(_time);
        int snapped = 0;
        string? refusal = null;
        RunKeyEdit(delegate { snapped = session.SnapToChain(stack, handles, frame, out refusal); }, "Could not snap to the chain: ");
        if (snapped > 0)
            _status.Text = handles.Count == 1 ? $"Snapped '{handles[0].Name}' to the end of its chain." : $"Snapped {snapped} handles to their chains.";
        else if (refusal != null)
            _status.Text = refusal;
    }

    private bool SpinnerDragStarted(int handle)
    {
        if (_drag != null || !((IViewportEditor)this).BeginDrag(handle, default)) return false;
        if (_dragGizmo?.Mode == GizmoMode.Rotate && handle == TransformGizmo.Uniform && !_drag!.CanSwivel)
        {
            ((IViewportEditor)this).EndDrag(false);
            _status.Text = "No IK chain in the selection can swivel at this frame.";
            return false;
        }
        _viewport.Renderer.Gizmo.ActiveAxis = handle == TransformGizmo.Uniform && _dragGizmo?.Mode == GizmoMode.Rotate ? -1 : handle;
        return true;
    }

    /// <summary>
    /// The space button: shows the transform space and cycles Local, Screen, World on a click,
    /// The order of the 1, 2 and 3 frame shortcuts.
    /// </summary>
    private void SpinnerSpacePicked()
    {
        if (_spinnerDial == null) return;
        SetSpace(SpinnerDial.NextSpace(_space));
    }

    /// <summary>
    /// Sets the space the gizmo handles and the Spinner work in. A drag keeps the axes it started
    /// with, so a change waits for it to end.
    /// </summary>
    private void SetSpace(TransformSpace space)
    {
        if (_drag != null)
        {
            _status.Text = "Finish the drag before changing the transform space.";
            return;
        }
        _space = space;
        _viewport.Renderer.Gizmo.Space = space;
        if (_spinnerDial != null) _spinnerDial.Space = space;
        _status.Text = $"Transform space: {space}.";
        UpdateSelection();
        _viewport.RequestNextFrameRendering();
    }

    private void SpinnerDragged(float swept, Vector2 moved)
    {
        if (_drag == null || _dragGizmo == null) return;
        try
        {
            if (_dragGizmo.Mode == GizmoMode.Rotate)
            {
                if (_dragAxis == TransformGizmo.Uniform) _drag.Swivel(SpinnerDial.SwivelAngle(moved));
                else _drag.Rotate(_dragAxis, swept);
            }
            else if (_dragGizmo.Mode == GizmoMode.Scale)
            {
                float factor = _dragAxis == TransformGizmo.Uniform
                    ? SpinnerDial.UniformFactor(moved, SpinnerDial.Radius(new Vector2(SpinnerDial.Width, SpinnerDial.Height)))
                    : SpinnerDial.ScaleFactor(swept);
                _drag.Scale(_dragAxis, factor);
            }
            else
            {
                float handleUnits = _dragGizmo.HandleUnits(_viewport.Renderer.ActiveView, _dragAxis);
                _drag.Translate(_dragAxis, SpinnerDial.MoveHandles(swept) * handleUnits);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Edit failed: " + ex.Message;
        }
        Apply();
    }
}
