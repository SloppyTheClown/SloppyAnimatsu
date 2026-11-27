// Controller selection and the keyboard: selection sync with the viewport, the shortcut table
// dispatch, key/controller stepping, the gizmo mode buttons, and the spinner fields that show and
// write a controller transform.
//
// Shortcuts are user data (SloppyAnimatsu.Core Shortcuts): RunShortcut only maps a key to a MenuCommand
// and then calls RunMenuCommand, so an action is never implemented twice.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private void Select(Object3D? controller, bool toggle = false)
    {
        if (!toggle)
        {
            _selectedAll.Clear();
        }
        if (controller != null && (!toggle || !_selectedAll.Remove(controller)))
        {
            _selectedAll.Add(controller);
        }
        SyncSelection(controller);
    }

    private void SyncSelection(Object3D? current)
    {
        _syncing = true;
        List<Item<Object3D>> source = (_controllers.ItemsSource as IEnumerable<Item<Object3D>>)?.ToList() ?? new List<Item<Object3D>>();
        _controllers.SelectedItems?.Clear();
        foreach (Item<Object3D> item2 in source.Where((Item<Object3D> i) => i.Value != null && _selectedAll.Contains(i.Value)))
        {
            _controllers.SelectedItems?.Add(item2);
        }
        Item<Object3D> item = source.FirstOrDefault((Item<Object3D> i) => i.Value == current);
        if ((object)item != null)
        {
            _controllers.ScrollIntoView(item);
        }
        _syncing = false;
        // A click in the tree already drew its own (possibly mixed) selection; anything else
        // mirrors the controller selection there.
        if (!_outlinerDriving)
        {
            _outliner.SetSelection(_selectedAll, current);
        }
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        UpdateSelection();
        SyncSkinToSelection();
        UpdateKeyCommands();
        RefreshMenu();
        RefreshExportPreview();
        _viewport.RequestNextFrameRendering();
    }

    private Point? _pointerInWindow;
    private bool _rerouting;

    /// <summary>
    /// Keys go to the window under the mouse: a key pressed while the
    /// pointer is over the viewport, the Scene tree, the timeline, the curveboard or the Anim Bank
    /// list runs that window's shortcuts even when the keyboard focus is in another one (Ctrl+A over
    /// the viewport selects the character's controllers, over the tree every row). The window under
    /// the pointer also takes the focus. A text field being typed in keeps its keys, and so do the
    /// modifier keys themselves.
    /// </summary>
    private void InstallPointerKeyRouting()
    {
        AddHandler(InputElement.PointerMovedEvent, (object? _, PointerEventArgs e) => _pointerInWindow = e.GetPosition(this),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerExited += delegate { _pointerInWindow = null; };
        AddHandler(InputElement.KeyDownEvent, delegate(object? _, KeyEventArgs e)
        {
            if (_rerouting || e.Handled || _pointerInWindow is not { } at) return;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin) return;
            var focused = FocusManager?.GetFocusedElement() as Visual;
            if (focused is TextBox) return;
            InputElement? hovered = KeyRegionOf(this.InputHitTest(at) as Visual);
            if (hovered == null || ReferenceEquals(hovered, KeyRegionOf(focused))) return;
            e.Handled = true;
            hovered.Focus();
            _rerouting = true;
            try
            {
                hovered.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = e.Key,
                    KeyModifiers = e.KeyModifiers,
                    PhysicalKey = e.PhysicalKey,
                    KeySymbol = e.KeySymbol,
                    Source = hovered
                });
            }
            finally { _rerouting = false; }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The window with its own keys that <paramref name="visual"/> lies in, or null.</summary>
    private InputElement? KeyRegionOf(Visual? visual)
    {
        for (Visual? v = visual; v != null; v = v.GetVisualParent())
            if (ReferenceEquals(v, _viewport) || ReferenceEquals(v, _outliner) || ReferenceEquals(v, _timeline)
                || ReferenceEquals(v, _curveboard) || ReferenceEquals(v, _animations))
                return (InputElement)v;
        return null;
    }

    private void LoadShortcuts()
    {
        try
        {
            string shortcutsPath = _options.ShortcutsPath;
            if (shortcutsPath != null)
            {
                _shortcuts = (File.Exists(shortcutsPath) ? Shortcuts.Load(shortcutsPath) : Shortcuts.CreateDefault());
                if (!File.Exists(shortcutsPath))
                {
                    _status.Text = "No shortcuts file at " + shortcutsPath + "; using the shipped map.";
                }
            }
            else
            {
                _shortcuts = Shortcuts.LoadDefault();
            }
        }
        catch (Exception ex) when (((ex is InvalidDataException || ex is AktFormatException || ex is IOException) ? 1 : 0) != 0)
        {
            _status.Text = "Could not read the shortcuts: " + ex.Message;
            _shortcuts = Shortcuts.CreateDefault();
        }
        try
        {
            _panelShortcuts = Hotkeys.LoadPanelDefault();
        }
        catch (Exception ex) when (ex is InvalidDataException or AktFormatException or IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not read the panel keys: " + ex.Message;
            _panelShortcuts = Hotkeys.CreatePanelDefault();
        }
        _hotkeys = new HotkeyMap(_shortcuts, _panelShortcuts);
        _viewport.Keys = _hotkeys;
        _outliner.Keys = _hotkeys;
        _timeline.Keys = _hotkeys;
        _curveboard.Keys = _hotkeys;
    }

    /// <summary>The SloppyAnimatsu panel action a key runs in the viewport (Hammer, Shift+Delete, Ctrl+Shift+V), when one does.</summary>
    private bool RunViewportKey(Key key, KeyModifiers modifiers)
    {
        switch (_hotkeys.ActionFor(HotkeyScope.Viewport, ShortcutInput.FromKey(key, modifiers)))
        {
            case "Hammer":
                Hammer();
                return true;
            case "DeleteKeysCloseGaps":
                DeleteKeys(closeGaps: true);
                return true;
            case "PasteKeysAfter":
                PasteKeys(after: true);
                return true;
            case "SelectAllControllers":
                SelectAllCharacterControllers();
                return true;
            default:
                return false;
        }
    }

    private bool RunShortcut(Key key, KeyModifiers modifiers)
    {
        if (_shortcuts == null)
        {
            return false;
        }
        ShortcutKey? shortcutKey = ShortcutInput.FromKey(key, modifiers);
        if (shortcutKey.HasValue)
        {
            ShortcutKey valueOrDefault = shortcutKey.GetValueOrDefault();
            string text = _shortcuts.ActionFor(valueOrDefault);
            if (text == null)
            {
                return false;
            }
            switch (text)
            {
            case "FloodVerticesSelection":
                return RunVertexSelectionKey(Key.Multiply);
            case "InvertVerticesSelection":
                return RunVertexSelectionKey(Key.Divide);
            case "PaintFrontBack":
                if (_skin == null) return false;
                ToggleFullPaint();
                return true;
            case "Undo":
                Undo();
                return true;
            case "Redo":
                Redo();
                return true;
            case "NewFile":
                NewScene();
                return true;
            case "OpenFile":
                OpenFile();
                return true;
            case "CloseFile":
                CloseDocument();
                return true;
            case "SaveFile":
                Save(choosePath: false);
                return true;
            case "SaveFileAs":
                Save(choosePath: true);
                return true;
            case "SetSpinnerModeMove":
                SetMode(GizmoMode.Translate);
                return true;
            case "SetSpinnerModeRotate":
                SetMode(GizmoMode.Rotate);
                return true;
            case "SetSpinnerModeScale":
                SetMode(GizmoMode.Scale);
                return true;
            case "SetSpinnerModeLocalFrame":
                SetSpace(TransformSpace.Local);
                return true;
            case "SetSpinnerModeScreenFrame":
                SetSpace(TransformSpace.Screen);
                return true;
            case "SetSpinnerModeWorldFrame":
                SetSpace(TransformSpace.World);
                return true;
            case "StackerCreateKey":
                CreateKey();
                return true;
            case "StackerTogglePreviousGhosts":
                ToggleGhosts(next: false);
                return true;
            case "StackerToggleNextGhosts":
                ToggleGhosts(next: true);
                return true;
            case "DeleteObject":
                DeleteKeys(closeGaps: false);
                return true;
            case "CopyKeys":
                CopyKeys();
                return true;
            case "PasteKeys":
                PasteKeys(after: false);
                return true;
            case "MiscToggleCurveboard":
                _showCurves.IsChecked = _showCurves.IsChecked != true;
                return true;
            case "ToggleGrid":
                _showGrid.IsChecked = _showGrid.IsChecked != true;
                return true;
            case "ToggleUnlit" or "ToggleWireframe" or "ToggleTextured" or "ToggleUntextured" or "ToggleHiddenBones"
                or "TogglePivots" or "ToggleSkinning" or "ToggleBlendshapeInteractiveZones" or "ToggleHUD":
                return RunViewPrefsAction(text);
            case "Play":
                _play.IsChecked = _play.IsChecked != true;
                return true;
            case "ChangeInterfaceContext":
                ToggleContext();
                return true;
            case "NextFrame":
                SetTime(_time + 1f);
                return true;
            case "PreviousFrame":
                SetTime(_time - 1f);
                return true;
            case "NextKey":
                StepKey(1);
                return true;
            case "PreviousKey":
                StepKey(-1);
                return true;
            case "NextController":
                StepController(1);
                return true;
            case "PreviousController":
                StepController(-1);
                return true;
            case "FirstController":
                SelectControllerEdge(last: false);
                return true;
            case "LastController":
                SelectControllerEdge(last: true);
                return true;
            case "SetCameraRight":
                SetView("Right");
                return true;
            case "SetCameraLeft":
                SetView("Left");
                return true;
            case "SetCameraTop":
                SetView("Top");
                return true;
            case "SetCameraBottom":
                SetView("Bottom");
                return true;
            case "SetCameraFront":
                SetView("Front");
                return true;
            case "SetCameraBack":
                SetView("Back");
                return true;
            case "SetCameraPerspective":
                SetView("Perspective");
                return true;
            case "ToggleLayoutSingleView":
                SetLayout(SceneLayout.SingleView);
                return true;
            case "ToggleLayout2Views":
                SetLayout(SceneLayout.TwoViews);
                return true;
            case "ToggleLayout2ViewsCurveboard":
                SetLayout(SceneLayout.TwoViewsCurveboard);
                return true;
            case "ToggleLayout4Views":
                SetLayout(SceneLayout.FourViews);
                return true;
            case "ToggleLayoutFromMouse":
                ToggleLayout();
                return true;
            default:
                return false;
            }
        }
        return false;
    }

    private void StepKey(int direction)
    {
        AnimStack activeStack = _timeline.ActiveStack;
        if (activeStack != null)
        {
            List<int> list = (from k in activeStack.Keys
                select k.Time into t
                where (direction > 0) ? ((float)t > _time + 0.001f) : ((float)t < _time - 0.001f)
                select t).ToList();
            if (list.Count != 0)
            {
                SetTime((direction > 0) ? list.Min() : list.Max());
            }
        }
    }

    private void StepController(int direction)
    {
        List<Item<Object3D>> list = (_controllers.ItemsSource as IEnumerable<Item<Object3D>>)?.Where((Item<Object3D> i) => i.Value != null).ToList();
        if (list != null && list.Count > 0)
        {
            int num = ((_selected == null) ? (-1) : list.FindIndex((Item<Object3D> i) => i.Value == _selected));
            num = ((direction > 0) ? ((num + 1) % list.Count) : ((num <= 0) ? (list.Count - 1) : (num - 1)));
            Select(list[num].Value);
        }
    }

    private void SelectControllerEdge(bool last)
    {
        List<Item<Object3D>> list = (_controllers.ItemsSource as IEnumerable<Item<Object3D>>)?.Where((Item<Object3D> i) => i.Value != null).ToList();
        if (list != null && list.Count > 0)
        {
            Object3D? value;
            if (!last)
            {
                value = list[0].Value;
            }
            else
            {
                value = list[list.Count - 1].Value;
            }
            Select(value);
        }
    }

    private void SetMode(GizmoMode mode)
    {
        if (_drag == null)
        {
            _viewport.Renderer.Gizmo.Mode = mode;
            _rotateMode.IsChecked = mode == GizmoMode.Rotate;
            _translateMode.IsChecked = mode == GizmoMode.Translate;
            _scaleMode.IsChecked = mode == GizmoMode.Scale;
            UpdateSelection();
            _viewport.RequestNextFrameRendering();
        }
    }

    private void UpdateSelection(bool forceSpinner = false)
    {
        // Display skinning follows the selected joint: re-pose once when it changed (Apply sets it,
        // so this does not recurse).
        if (SkinTintShown() && _pose != null && !ReferenceEquals(_pose.SkinDisplayJoint, _selected as Joint))
        {
            Apply();
            if (!forceSpinner) return;
        }
        SceneRenderer renderer = _viewport.Renderer;
        TransformGizmo gizmo = renderer.Gizmo;
        gizmo.Space = _space;
        Object3D selected = _selected;
        ScenePose pose = _pose;
        renderer.Selected = selected;
        renderer.AlsoSelected = _selectedAll.Take(_selectedAll.Count - 1).ToArray();
        if (pose != null && IsPlacedObject(selected))
        {
            // An object that is not a controller (a character, a mesh, a group): its own transform
            // is its placement in the scene (PlacementDrag), the same at every frame. It moves and
            // turns in both contexts and scales in Rigging; Paint & Check shows it read-only.
            Object3D placed = selected!;
            bool editable = !InPaintAndCheck;
            bool scalable = editable && _animation == null;
            gizmo.Visible = editable;
            gizmo.Frame = placed.WorldMatrix;
            gizmo.ParentFrame = placed.Parent3D?.WorldMatrix ?? Matrix4x4.Identity;
            gizmo.CanTranslate = true;
            gizmo.CanScale = scalable;
            SyncSpinnerDial();
            ShowSpinner(SloppyAnimatsu.Core.Model.Transform.FromMatrixLoose(placed.LocalMatrix), null, restEditable: editable, forceSpinner, scaleEditable: scalable);
            _fovField.Show(null, enabled: false, forceSpinner);
            ShowExtraFields(selected, null, null, null, forceSpinner);
            SyncFreezeButton();
            _spaceText.Text = "Parent";
            _selection.Text = !editable
                ? $"{placed.Name} ({placed.ClassName}): Paint & Check poses controllers only."
                : scalable
                    ? $"{placed.Name} ({placed.ClassName}): drag the gizmo or type a value to move, turn or scale it with everything below it (not animated)."
                    : $"{placed.Name} ({placed.ClassName}): drag the gizmo or type a value to move or turn it with everything below it (not animated); scale it in the Rigging context.";
            RefreshProperties();
            RefreshMaterial();
            return;
        }
        int num = ((selected != null && pose != null && _character != null && pose.Character == _character) ? pose.Evaluator(_character).IndexOf(selected) : (-1));
        if (selected == null || pose == null || _character == null || num < 0)
        {
            gizmo.Visible = false;
            SyncSpinnerDial();
            // Any other selected object still shows its own transform, read-only.
            ShowSpinner(selected != null ? SloppyAnimatsu.Core.Model.Transform.FromMatrixLoose(selected.LocalMatrix) : null, null, restEditable: false, forceSpinner);
            ShowExtraFields(selected, null, null, null, forceSpinner);
            SyncFreezeButton();
            _spaceText.Text = "Parent";
            _selection.Text = selected != null ? $"{selected.Name} ({selected.ClassName}) cannot be edited here." : "Click a joint or controller to select it.";
            RefreshProperties();
            RefreshMaterial();
            return;
        }
        PoseEvaluator poseEvaluator = pose.Evaluator(_character);
        AnimStack animStack = ((_animation != null) ? PoseEditSession.EditableStack(_animation, selected) : null);
        AnimTrack animTrack = animStack?.TrackFor(selected);
        Object3D object3D = ((_animation != null) ? poseEvaluator.SpaceOf(_animation, selected, _time) : null);
        _spaceText.Text = object3D?.Name ?? "Parent";
        RestPoseEditor restPose = Session.RestPose;
        string reason = null;
        bool flag = _animation == null && restPose.CanEdit(selected, out reason);
        // Paint & Check (no CHAR BANK pose row current) poses a runtime check pose that is never
        // saved; a Bind Pose row refuses rest edits.
        if (InPaintAndCheck)
        {
            flag = Session.BeginCheckPose(selected, out reason) != null;
        }
        else if (flag && BankRowRefusesRestEdit(out string bankReason))
        {
            flag = false;
            reason = bankReason;
        }
        gizmo.Visible = animStack != null || flag;
        gizmo.Frame = pose.WorldOf(selected);
        gizmo.ParentFrame = pose.WorldOf(object3D ?? selected.Parent3D);
        gizmo.CanTranslate = flag || (animTrack != null && (animTrack.Channels & AnimChannels.Translate) != 0);
        // No IK controller (chain or reverse foot) is scalable, in either context.
        gizmo.CanScale = TransformDrag.IsScalable(selected) && (flag || (animTrack != null && (animTrack.Channels & AnimChannels.Scale) != 0));
        SyncSpinnerDial();
        SloppyAnimatsu.Core.Model.Transform value = poseEvaluator.LocalTransforms(_animation, _time)[num];
        ShowSpinner(value, animTrack, flag, forceSpinner);
        if (selected is CameraAnimated camera)
        {
            float value2 = ((_animation == null) ? 30f : poseEvaluator.CameraFov(_animation, camera, _time));
            bool enabled = _animation != null && animTrack != null && animTrack.HasFov && animStack != null && !animStack.IsAdditive;
            _fovField.Show(value2, enabled, forceSpinner);
        }
        else
        {
            _fovField.Show(null, enabled: false, forceSpinner);
        }
        ShowExtraFields(selected, animStack, animTrack, poseEvaluator, forceSpinner);
        SyncFreezeButton();
        StringBuilder stringBuilder = new StringBuilder();
        StringBuilder stringBuilder2;
        StringBuilder.AppendInterpolatedStringHandler handler;
        if (_selectedAll.Count > 1)
        {
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder3 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(73, 1, stringBuilder2);
            handler.AppendFormatted(_selectedAll.Count);
            handler.AppendLiteral(" controllers selected; the gizmo is on the last one, drags move them all.");
            stringBuilder3.AppendLine(ref handler);
        }
        stringBuilder2 = stringBuilder;
        StringBuilder stringBuilder4 = stringBuilder2;
        handler = new StringBuilder.AppendInterpolatedStringHandler(3, 2, stringBuilder2);
        handler.AppendFormatted(selected.Name);
        handler.AppendLiteral(" (");
        handler.AppendFormatted(selected.ClassName);
        handler.AppendLiteral(")");
        stringBuilder4.AppendLine(ref handler);
        if (object3D != null)
        {
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder5 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
            handler.AppendLiteral("Space: ");
            handler.AppendFormatted(object3D.Name);
            stringBuilder5.AppendLine(ref handler);
        }
        if (_animation == null)
        {
            stringBuilder.Append(flag ? ("Drags edit '" + restPose.EditedPose?.Name + "'.") : reason);
            int num2 = (restPose.IsResolutionPending ? restPose.ChangedSlots.Count : 0);
            if (num2 > 0)
            {
                stringBuilder2 = stringBuilder;
                StringBuilder stringBuilder6 = stringBuilder2;
                handler = new StringBuilder.AppendInterpolatedStringHandler(94, 1, stringBuilder2);
                handler.AppendLiteral("\nThe ref pose changed (");
                handler.AppendFormatted(num2);
                handler.AppendLiteral(" controller(s)): picking an animation asks whether to convert the keys.");
                stringBuilder6.Append(ref handler);
            }
        }
        else if (animStack == null || animTrack == null)
        {
            AnimStack activeStack = _timeline.ActiveStack;
            stringBuilder.Append((activeStack != null && activeStack.TrackFor(selected) == null) ? ("No enabled stack animates it. Edit > Add to active stack (H) adds it to '" + activeStack.Name + "'.") : "No enabled stack animates it.");
        }
        else
        {
            int num3 = (int)MathF.Round(_time);
            int num4 = PoseEditSession.KeyAt(animStack, num3);
            stringBuilder2 = stringBuilder;
            StringBuilder stringBuilder7 = stringBuilder2;
            handler = new StringBuilder.AppendInterpolatedStringHandler(23, 4, stringBuilder2);
            handler.AppendLiteral("Stack '");
            handler.AppendFormatted(animStack.Name);
            handler.AppendLiteral("' (");
            handler.AppendFormatted(animTrack.Channels);
            handler.AppendLiteral("): ");
            handler.AppendFormatted((num4 >= 0) ? $"key {num4}" : "no key");
            handler.AppendLiteral(" at frame ");
            handler.AppendFormatted(num3);
            stringBuilder7.Append(ref handler);
            if (gizmo.Mode == GizmoMode.Translate && !gizmo.CanTranslate)
            {
                stringBuilder.Append("\nIts translation is not animated.");
            }
            if (gizmo.Mode == GizmoMode.Scale && !gizmo.CanScale)
            {
                stringBuilder.Append("\nIts scale is not animated.");
            }
        }
        _selection.Text = stringBuilder.ToString();
        RefreshProperties();
        RefreshMaterial();
    }

    private static string F(float v)
    {
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string F(Vector3 v)
    {
        return $"{F(v.X)} {F(v.Y)} {F(v.Z)}";
    }

    private void ShowSpinner(SloppyAnimatsu.Core.Model.Transform? local, AnimTrack? track, bool restEditable, bool force = false, bool? scaleEditable = null)
    {
        Vector3 vector = ((!local.HasValue) ? Vector3.Zero : Curves.ToEulerDegrees(local.GetValueOrDefault().Rotation));
        for (int i = 0; i < 3; i++)
        {
            _translateFields[i].Show(local?.Translation[i], Animated((AnimChannels)(1 << i)), force);
            SpinnerField obj = _rotateFields[i];
            float? value;
            if (local.HasValue)
            {
                SloppyAnimatsu.Core.Model.Transform valueOrDefault = local.GetValueOrDefault();
                value = vector[i];
            }
            else
            {
                value = null;
            }
            obj.Show(value, Animated(AnimChannels.Rotate), force);
            _scaleFields[i].Show(local?.Scale[i], scaleEditable ?? Animated((AnimChannels)(1 << 6 + i)), force);
        }
        bool Animated(AnimChannels bit)
        {
            return restEditable || (track != null && (track.Channels & bit) != 0);
        }
    }

    private int SetTypedValue(int channel, float value)
    {
        if (!_syncing && _drag == null && _pose != null && _character != null)
        {
            PoseEditSession session = Session;
            if (session != null && IsPlacedObject(_selected))
            {
                if (InPaintAndCheck)
                {
                    _status.Text = "Paint & Check poses controllers only.";
                    return 0;
                }
                Object3D placed = _selected!;
                bool set = session.SetPlacementValue(placed, channel, value, allowScale: _animation == null, out string? placeReason);
                if (set)
                {
                    _dirty = true;
                    UpdateTitle();
                    AfterKeyChange();
                    Apply();
                    _status.Text = $"Set {Curves.ChannelNames[channel]} of {placed.Name}.";
                }
                else
                {
                    _status.Text = placeReason ?? "The value was not applied.";
                }
                UpdateSelection(forceSpinner: true);
                return set ? 1 : 0;
            }
            if (session != null)
            {
                Object3D[] controllers = _selectedAll.Where((Object3D c) => session.Evaluator.IndexOf(c) >= 0).ToArray();
                if (controllers.Length == 0)
                {
                    _status.Text = "Select a controller to type a value.";
                    return 0;
                }
                _play.IsChecked = false;
                bool checkPose = InPaintAndCheck;
                int frame = (int)MathF.Round(_time);
                int written = 0;
                RunKeyEdit(delegate
                {
                    written = session.SetChannelValue(_animation, controllers, frame, channel, value, out string reason, checkPose);
                    if (written == 0)
                    {
                        _status.Text = reason ?? "The value was not applied.";
                    }
                });
                UpdateSelection(forceSpinner: true);
                return written;
            }
        }
        return 0;
    }
}
