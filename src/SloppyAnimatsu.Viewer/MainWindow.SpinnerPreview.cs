// The TRANSFORM window's drag strips shown live
// every step of a drag writes its value through the same edit a typed value uses, so the
// viewport poses the character as the strip moves, and undoes the previous step first, so at most
// one preview edit is ever applied. When the drag ends the preview is undone and the field's
// Committed writes the final value once - the drag stays one undo step.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The history record of the drag-strip value applied right now, or null.</summary>
    private EditRecord? _previewRecord;

    /// <summary>Hooks a field's drag to <paramref name="write"/>, the edit its Committed runs.</summary>
    private void PreviewWhileDragging(SpinnerField field, Func<PoseEditSession, Object3D[], int, float, int> write)
    {
        field.Previewed += value => PreviewValue((session, controllers, frame) => write(session, controllers, frame, value));
        field.DragEnded += () => DropPreview(refresh: false);
    }

    /// <summary>
    /// The TRANSFORM window's drag rates, per pixel of drag (Ctrl: a tenth). They are fixed and
    /// independent of the Snap steps, which apply to the gizmo and the Spinner dial: the snap steps
    /// (45° rotation, 0.5 scale by default) made a 4-pixel strip step far too coarse, and the 0.1
    /// move step far too fine for scene units (centimetres in the samples).
    /// </summary>
    internal const float TranslatePerPixel = 0.5f, RotatePerPixel = 0.5f, ScalePerPixel = 0.005f,
        AnglePerPixel = 0.5f, BlendPerPixel = 0.005f;

    private void SetTransformDragRates()
    {
        static void Rate(SpinnerField field, float perPixel)
        {
            field.PixelsPerStep = 1;
            field.Step = perPixel;
        }
        for (int i = 0; i < 3; i++)
        {
            Rate(_translateFields[i], TranslatePerPixel);
            Rate(_rotateFields[i], RotatePerPixel);
            Rate(_scaleFields[i], ScalePerPixel);
        }
        for (int i = 0; i < _extraFields.Length; i++)
            Rate(_extraFields[i], 9 + i <= 12 ? AnglePerPixel : BlendPerPixel);
    }

    /// <summary>The roll/twist/bank value a right-button Spinner drag started from (degrees).</summary>
    private float _footDragFrom;

    /// <summary>The reverse-foot angle a right-button Spinner drag changes (10-12), or -1.</summary>
    private int _footDragChannel = -1;

    /// <summary>
    /// The Spinner dial's reverse-foot sectors: with a reverse-foot controller selected, a
    /// right-button drag on rotate mode's X/Y/Z sector (red, green, blue) changes roll, twist or bank
    /// (channels 10-12) - live, then one undo step on release - while a left-button drag still rotates.
    /// </summary>
    private void HookFootSpinner(SpinnerDialView dial)
    {
        dial.FootStarted = axis =>
        {
            if (_drag != null || !_extraFields[1 + axis].IsEditable) return false;
            _footDragChannel = 10 + axis;
            _footDragFrom = _extraFields[1 + axis].Value;
            return true;
        };
        dial.FootDragged = (axis, delta) =>
        {
            if (_footDragChannel < 0) return;
            int channel = _footDragChannel;
            float value = _footDragFrom + delta;
            _extraFields[1 + axis].Show(value, force: true);
            PreviewValue((s, ctl, f) => s.SetScalarChannel(_animation, ctl, f, channel, value * MathF.PI / 180f, out _, InPaintAndCheck));
        };
        dial.FootEnded = commit =>
        {
            int channel = _footDragChannel;
            _footDragChannel = -1;
            if (channel < 0) return;
            float value = _extraFields[channel - 9].Value;
            DropPreview(refresh: false);
            if (commit && MathF.Abs(value - _footDragFrom) >= 1e-6f) SetScalarValue(channel, value);
            else { Apply(); UpdateSelection(forceSpinner: true); }
        };
    }

    /// <summary>Shows the foot sectors on the Spinner for a selected reverse-foot controller, live where its roll/twist/bank are editable.</summary>
    private void SyncFootSpinner(Object3D? selected)
    {
        _spinnerDial?.SetFoot(selected is ControllerReverseFoot,
            _extraFields[1].IsEditable, _extraFields[2].IsEditable, _extraFields[3].IsEditable);
    }

    /// <summary>Hooks every value field of the TRANSFORM window to its live preview.</summary>
    private void HookSpinnerPreviews()
    {
        SetTransformDragRates();
        for (int i = 0; i < 3; i++)
        {
            int c = i;
            PreviewWhileDragging(_translateFields[i], (s, ctl, f, v) => s.SetChannelValue(_animation, ctl, f, c, v, out _, InPaintAndCheck));
            PreviewWhileDragging(_rotateFields[i], (s, ctl, f, v) => s.SetChannelValue(_animation, ctl, f, 3 + c, v, out _, InPaintAndCheck));
            PreviewWhileDragging(_scaleFields[i], (s, ctl, f, v) => s.SetChannelValue(_animation, ctl, f, 6 + c, v, out _, InPaintAndCheck));
        }
        PreviewWhileDragging(_fovField, (s, ctl, f, v) => s.SetFov(_animation, ctl, f, v, out _));
        for (int i = 0; i < _extraFields.Length; i++)
        {
            int channel = 9 + i;
            PreviewWhileDragging(_extraFields[i], (s, ctl, f, v) =>
                s.SetScalarChannel(_animation, ctl, f, channel, channel <= 12 ? v * MathF.PI / 180f : v, out _, InPaintAndCheck));
        }
    }

    private void PreviewValue(Func<PoseEditSession, Object3D[], int, int> write)
    {
        if (_syncing || _drag != null || _pose == null || _character == null) return;
        PoseEditSession? session = Session;
        if (session == null) return;
        Object3D[] controllers = _selectedAll.Where(c => session.Evaluator.IndexOf(c) >= 0).ToArray();
        if (controllers.Length == 0) return;
        DropPreview(refresh: false);
        _play.IsChecked = false;
        EditRecord? before = _history.NextUndo;
        try
        {
            write(session, controllers, (int)MathF.Round(_time));
        }
        catch (Exception ex)
        {
            _status.Text = "Edit failed: " + ex.Message;
        }
        if (!ReferenceEquals(_history.NextUndo, before)) _previewRecord = _history.NextUndo;
        Apply();
    }

    /// <summary>Undoes the live preview edit, if it is still the newest step.</summary>
    private void DropPreview(bool refresh)
    {
        EditRecord? record = _previewRecord;
        _previewRecord = null;
        if (record == null || !ReferenceEquals(_history.NextUndo, record)) return;
        Session?.Undo();
        _history.ForgetRedo(record);
        if (refresh) Apply();
    }
}
