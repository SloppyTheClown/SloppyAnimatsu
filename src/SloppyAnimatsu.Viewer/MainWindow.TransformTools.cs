// The TRANSFORM window's channel mask and action buttons: a
// master button per group (translation, rotation, scale, IK pole, reverse foot, blendshape, FOV),
// a check box per channel, and Reset to ref pose / Copy / Paste / Insert After Key / Mirror /
// Reset & Freeze. The mask is the channel filter of Reset, Copy and Mirror (the one
// channel-filter preference) and dims the fields it leaves out.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The channels with a check box: bits 0-15 of <see cref="AnimChannels"/>.</summary>
    private const int ChannelCount = 16;

    private const AnimChannels AllMaskChannels = (AnimChannels)((1u << ChannelCount) - 1);

    /// <summary>
    /// The TRANSFORM window's groups in bit order: the symbol on the master button, the group's
    /// name, its first channel bit, and the label of each channel row.
    /// </summary>
    private static readonly (string Symbol, string Name, int First, string[] Rows)[] ChannelGroups =
    [
        ("✥", "Translate", 0, ["X", "Y", "Z"]),
        ("⟳", "Rotation", 3, ["X", "Y", "Z"]),
        ("⤢", "Scale", 6, ["X", "Y", "Z"]),
        ("◎", "IK Pole", 9, ["Angle"]),
        ("⌂", "Reverse foot", 10, ["Roll", "Twist", "Bank"]),
        ("◈", "Blendshape", 13, ["X", "Y"]),
        ("◐", "Camera", 15, ["FOV"]),
    ];

    private AnimChannels _channelMask = AllMaskChannels;
    private CheckBox[]? _channelBoxes;
    private ToggleButton[]? _groupButtons;
    private Button[]? _transformActions;
    private bool _syncingMask;

    /// <summary>The value fields of the IK pole, reverse-foot (degrees) and blendshape channels (bits 9-14).</summary>
    private readonly SpinnerField[] _extraFields = [new(), new(), new(), new(), new(), new()];

    /// <summary>The Space Switch row's parent name.</summary>
    private readonly TextBlock _spaceText = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = "Parent" };

    /// <summary>The Space Switch row's value: shows the space in force and opens the Space Switch window.</summary>
    private Button? _spaceButton;

    private Button SpaceButton()
    {
        if (_spaceButton != null) return _spaceButton;
        _spaceButton = new Button
        {
            Content = _spaceText, FontSize = 11, Padding = new Thickness(6, 1), MinWidth = 0, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(_spaceButton, "The space the selected controller follows at this frame. Click to switch it (Space Switch).");
        _spaceButton.Click += async delegate { await SwitchSpaceFromTransform(); };
        return _spaceButton;
    }

    /// <summary>
    /// The Space row's click: the Space Switch window for the selected controllers at the playhead,
    /// then <see cref="PoseEditSession.ChangeSpaceAt"/> with the chosen space.
    /// </summary>
    private async Task SwitchSpaceFromTransform()
    {
        if (_drag != null || _timeline.IsDragging || _pose == null || _character == null) return;
        if (_animation == null)
        {
            _status.Text = "Space switching is keyed: open an animation (Animation context) first.";
            return;
        }
        PoseEditSession? session = Session;
        if (session == null) return;
        Object3D[] controllers = _selectedAll.Where(c => session.Evaluator.IndexOf(c) >= 0).ToArray();
        if (controllers.Length == 0)
        {
            _status.Text = "Select the controllers whose space to change.";
            return;
        }
        Scene scene = _pose.Model.Scene;
        List<Object3D> spaceable = KeyEditor.SpaceableObjects(scene).ToList();
        if (spaceable.Count == 0)
        {
            _status.Text = SpaceSwitchChoice.NoSpacesMessage;
            return;
        }
        Object3D lead = _selected ?? controllers[^1];
        Object3D? current = session.Evaluator.SpaceOf(_animation, lead, _time);
        if (ReferenceEquals(current, lead.Parent3D)) current = null;
        _play.IsChecked = false;
        int frame = (int)MathF.Round(_time);
        var (chosen, space) = await SpaceSwitchDialog.Ask(this, scene.Characters.ToList(), spaceable, current, lead,
            $"Space of {(controllers.Length == 1 ? lead.Name : $"{controllers.Length} controllers")} at frame {frame}");
        if (!chosen || _animation == null) return;
        Animation animation = _animation;
        int[] selectedKeys = _timeline.SelectedKeys.ToArray();
        RunKeyEdit(delegate
        {
            if (session.ChangeSpaceAt(animation, controllers, frame, space, selectedKeys, out string? reason) == 0)
                _status.Text = reason ?? "The space was not changed.";
        });
        UpdateSelection(forceSpinner: true);
    }

    /// <summary>The value field of channel <paramref name="c"/> (0-15).</summary>
    private SpinnerField ChannelField(int c) => c switch
    {
        < 3 => _translateFields[c],
        < 6 => _rotateFields[c - 3],
        < 9 => _scaleFields[c - 6],
        15 => _fovField,
        _ => _extraFields[c - 9],
    };

    private static AnimChannels GroupBits(int group)
    {
        var (_, _, first, rows) = ChannelGroups[group];
        return (AnimChannels)(((1u << rows.Length) - 1) << first);
    }

    /// <summary>The check box of channels 0-15, the master button of each group and the action buttons, built once.</summary>
    private void EnsureTransformTools()
    {
        if (_channelBoxes != null) return;
        _channelBoxes = new CheckBox[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            int channel = c;
            var box = new CheckBox
            {
                MinWidth = 0, MinHeight = 0, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(box, $"{(channel < Curves.ChannelNames.Length ? Curves.ChannelNames[channel] : "Channel " + channel)}: tick to include it in Reset, Copy and Mirror; double-click to keep only this channel.");
            box.IsCheckedChanged += delegate
            {
                if (_syncingMask) return;
                var bit = (AnimChannels)(1u << channel);
                _channelMask = box.IsChecked == true ? _channelMask | bit : _channelMask & ~bit;
                SyncChannelMask();
            };
            box.DoubleTapped += delegate
            {
                _channelMask = (AnimChannels)(1u << channel);
                SyncChannelMask();
            };
            _channelBoxes[c] = box;
        }
        _groupButtons = new ToggleButton[ChannelGroups.Length];
        for (int g = 0; g < ChannelGroups.Length; g++)
        {
            int group = g;
            var button = new ToggleButton
            {
                Width = 20, Height = 20, Padding = new Thickness(0),
            };
            // The hover text is the group's name; a click turns the whole group on or off.
            PanelIcons.Apply(button, ChannelGroups[g].Name, GroupIcon(g));
            button.Click += delegate
            {
                _channelMask = ToggleGroup(_channelMask, GroupBits(group));
                SyncChannelMask();
            };
            _groupButtons[g] = button;
        }
        ApplyTranslationUnit(); // the Translate button's hover text names the unit
        foreach (var field in _extraFields) field.Show(null, enabled: false);
        for (int i = 0; i < _extraFields.Length; i++)
        {
            int channel = 9 + i;
            _extraFields[i].Committed += value => SetScalarValue(channel, value);
        }
        _transformActions =
        [
            MakeAction("Reset to ref pose", PanelIcons.ResetToRefPose(), "Put the ticked channels of the selected controllers back to their rest values at this frame.", ResetTicked),
            MakeAction("Copy", PanelIcons.CopyKeys(), "Copy the ticked channels of the selected keys of the selected controllers (Ctrl+C).", CopyKeys),
            MakeAction("Paste", PanelIcons.PasteKeys(), "Paste onto the selected keys (Ctrl+V).", () => PasteKeys(after: false)),
            MakeAction("Insert After Key", PanelIcons.InsertAfterKey(), "Paste after the selected keys.", () => PasteKeys(after: true)),
            MakeAction("Mirror (flip)", PanelIcons.MirrorPose(), "Mirror the selected controllers' ticked channels onto the other side of the body (centre joints turn as if reflected). With timeline keys selected, mirrors those keys instead.", MirrorTicked),
            MakeAction("Reset & Freeze", PanelIcons.ResetAndFreeze(), "Rigging context: bake the selected unskinned mesh's transform into its geometry and reset the transform.", ResetAndFreeze),
        ];
        SyncFreezeButton();
        SyncChannelMask();

        // Each action is an icon button; its name and what it does are on the hover text.
        static Button MakeAction(string text, Control icon, string tip, Action click)
        {
            var button = new Button
            {
                Height = 22, Padding = new Thickness(0), MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(button, tip);
            PanelIcons.Apply(button, text, icon, 14);
            button.Click += delegate { click(); };
            return button;
        }

        // The channel-group toggles, in ChannelGroups order.
        static Control GroupIcon(int group) => group switch
        {
            0 => PanelIcons.TranslateGroup(),
            1 => PanelIcons.RotateGroup(),
            2 => PanelIcons.ScaleGroup(),
            3 => PanelIcons.PoleGroup(),
            4 => PanelIcons.FootGroup(),
            _ => PanelIcons.BlendshapeGroup(),
        };
    }

    /// <summary>The channel filter Copy and Mirror use: the ticked channels.</summary>
    private AnimChannels KeyChannelFilter => _channelMask;

    /// <summary>A group button: ticks the whole group unless it is already all ticked, then unticks it.</summary>
    internal static AnimChannels ToggleGroup(AnimChannels mask, int group) =>
        ToggleGroup(mask, (AnimChannels)(0b111u << (3 * group)));

    internal static AnimChannels ToggleGroup(AnimChannels mask, AnimChannels bits) =>
        (mask & bits) == bits ? mask & ~bits : mask | bits;

    private void SyncChannelMask()
    {
        if (_channelBoxes == null || _groupButtons == null) return;
        _syncingMask = true;
        try
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                bool on = (_channelMask & (AnimChannels)(1u << c)) != 0;
                _channelBoxes[c].IsChecked = on;
                ChannelField(c).Opacity = on ? 1.0 : 0.5;
            }
            for (int g = 0; g < _groupButtons.Length; g++)
            {
                var bits = GroupBits(g);
                _groupButtons[g].IsChecked = (_channelMask & bits) == bits;
            }
        }
        finally
        {
            _syncingMask = false;
        }
    }

    private void ResetTicked()
    {
        if (_drag != null || _timeline.IsDragging) return;
        PoseEditSession? session = Session;
        bool checkPose = InPaintAndCheck;
        if (session == null || (_animation == null && !checkPose))
        {
            _status.Text = "Reset works on an animation or on Paint & Check; the rest pose is the reference itself.";
            return;
        }
        Object3D[] controllers = _selectedAll.Where(c => session.Evaluator.IndexOf(c) >= 0).ToArray();
        if (controllers.Length == 0)
        {
            _status.Text = "Select the controllers to reset.";
            return;
        }
        _play.IsChecked = false;
        int frame = (int)MathF.Round(_time);
        RunKeyEdit(delegate
        {
            if (session.ResetChannels(_animation, controllers, frame, _channelMask & KeyEditor.MrsChannels, out string? reason, checkPose) == 0)
                _status.Text = reason ?? "Nothing was reset.";
        });
        UpdateSelection(forceSpinner: true);
    }

    /// <summary>
    /// Shows the IK pole, reverse-foot and blendshape rows for <paramref name="selected"/>: the
    /// editable stack's layer value at the playhead (angles in degrees), editable when that track
    /// animates the channel and the stack is not additive. A blendshape controller without such a
    /// track shows its mixed value read-only.
    /// </summary>
    private void ShowExtraFields(Object3D? selected, AnimStack? stack, AnimTrack? track, PoseEvaluator? evaluator, bool force)
    {
        for (int i = 0; i < _extraFields.Length; i++)
        {
            int channel = 9 + i;
            var bit = (AnimChannels)(1u << channel);
            bool animates = _animation != null && stack != null && track != null && (track.Channels & bit) != 0;
            bool editable = animates && !stack!.IsAdditive;
            float? value = null;
            if (channel is >= 10 and <= 12 && InPaintAndCheck && selected is ControllerReverseFoot foot && evaluator != null)
            {
                // Paint & Check: the check pose's own roll, twist and bank.
                var angles = evaluator.CheckFootAngles(foot);
                value = (channel == 10 ? angles.X : channel == 11 ? angles.Y : angles.Z) * 180f / MathF.PI;
                editable = true;
            }
            else if (channel >= 13)
            {
                if (animates && track is BlendshapeTrack blend && evaluator != null)
                    value = evaluator.LayerBlendshapeValues(stack!, blend, _time, out _)[channel - 13];
                else if (selected is BlendshapeController controller && evaluator != null)
                    value = evaluator.BlendshapeValues(_animation, controller, _time)[channel - 13];
            }
            else if (animates)
            {
                value = Curves.ScalarAt(stack!, track!, channel, _time) * 180f / MathF.PI;
            }
            _extraFields[i].Show(value, editable, force);
        }
        SyncFootSpinner(selected);
        SyncCompensateBox();
    }

    // Compensate Scale at the playhead: keyed per object
    // in an animation, the rigging value without one. Mixed selections show the indeterminate "-";
    // a click on a mixed or unticked box turns every selected object on.
    private CheckBox? _compensateBox;
    private bool? _compensateShown;

    private CheckBox CompensateBox()
    {
        if (_compensateBox != null) return _compensateBox;
        _compensateBox = new CheckBox { Content = "Compensate Scale", FontSize = 11.0, MinHeight = 0.0 };
        ToolTip.SetTip(_compensateBox, "Compensate the parent's scale. Keyed at the playhead in an animation (stepped), the rigging value otherwise.");
        _compensateBox.Click += (_, _) => SetCompensateFromBox();
        return _compensateBox;
    }

    private Object3D[] CompensateTargets()
    {
        PoseEditSession? session = Session;
        if (session == null) return [];
        return _selectedAll.Where(c => session.Evaluator.IndexOf(c) >= 0).ToArray();
    }

    private void SyncCompensateBox()
    {
        if (_compensateBox == null) return;
        PoseEditSession? session = Session;
        Object3D[] targets = CompensateTargets();
        bool? shown = null;
        bool keyed = false;
        if (session != null && targets.Length > 0)
        {
            bool first = session.Evaluator.Compensates(targets[0], _animation, _time);
            shown = targets.All(t => session.Evaluator.Compensates(t, _animation, _time) == first) ? first : null;
            if (_animation != null)
            {
                int frame = (int)MathF.Round(_time);
                keyed = targets.Any(t => CompensateScaleKeys.KeyAt(_animation, t, frame) != null);
            }
        }
        _compensateShown = shown;
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            DisabledHint.SetEnabled(_compensateBox, targets.Length > 0, "a selected object");
            _compensateBox.IsChecked = targets.Length == 0 ? false : shown;
            _compensateBox.Content = keyed ? "Compensate Scale ◆" : "Compensate Scale";
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void SetCompensateFromBox()
    {
        if (_syncing || _drag != null || _pose == null) { SyncCompensateBox(); return; }
        PoseEditSession? session = Session;
        Object3D[] targets = CompensateTargets();
        if (session == null || targets.Length == 0) { SyncCompensateBox(); return; }
        bool on = _compensateShown != true;
        int frame = (int)MathF.Round(_time);
        _play.IsChecked = false;
        RunKeyEdit(delegate
        {
            if (!session.SetCompensateScale(_animation, targets, frame, on))
                _status.Text = "Compensate Scale is already " + (on ? "on." : "off.");
        });
        Apply();
        UpdateSelection(forceSpinner: true);
        RefreshProperties();
    }

    /// <summary>Writes a typed pole, reverse-foot (degrees) or blendshape value at the playhead.</summary>
    private void SetScalarValue(int channel, float shown)
    {
        if (_syncing || _drag != null || _pose == null || _character == null) return;
        PoseEditSession? session = Session;
        if (session == null) return;
        Object3D[] controllers = _selectedAll.Where(c => session.Evaluator.IndexOf(c) >= 0).ToArray();
        float value = channel <= 12 ? shown * MathF.PI / 180f : shown;
        int frame = (int)MathF.Round(_time);
        _play.IsChecked = false;
        RunKeyEdit(delegate
        {
            if (session.SetScalarChannel(_animation, controllers, frame, channel, value, out string? reason, InPaintAndCheck) == 0)
                _status.Text = reason ?? "The value was not applied.";
        });
        UpdateSelection(forceSpinner: true);
    }

    /// <summary>The objects Reset &amp; Freeze acts on: the hierarchy panel's selected rows.</summary>
    private Object3D[] FreezeSelection() => _outliner.SelectedObjects.ToArray();

    private bool CanResetAndFreeze() =>
        _pose != null && _pose.Model.Scene.Context == InterfaceContext.Rigging && _animation == null
        && MeshFreeze.CanFreeze(FreezeSelection());

    private void SyncFreezeButton()
    {
        if (_transformActions != null) DisabledHint.SetEnabled(_transformActions[^1], CanResetAndFreeze(), "the Rigging context on the rest pose, and selected meshes that can be frozen");
        SyncTransformKeyActions();
    }

    /// <summary>
    /// Copy, Paste and Insert After Key work on timeline keys, so they are greyed out (with the
    /// reason on the hover text) when no animation stack is active, nothing is copied, or no key
    /// is selected. Called on selection, context and timeline-key changes.
    /// </summary>
    private void SyncTransformKeyActions()
    {
        if (_transformActions == null) return;
        AnimStack? stack = _animation != null ? _timeline.ActiveStack : null;
        bool keyed = stack != null && stack is not AudioStack;
        bool selected = keyed && _timeline.SelectedKeys.Count > 0;
        bool beforeEnd = keyed && _timeline.SelectedKeys.Any(k => k < stack!.Keys.Count - 1);
        DisabledHint.SetEnabled(_transformActions[1], selected, "an animation with keys selected on the timeline");
        DisabledHint.SetEnabled(_transformActions[2], keyed && _clipboard != null, "copied keys and an animation with an active stack");
        DisabledHint.SetEnabled(_transformActions[3], beforeEnd && _clipboard != null, "copied keys and a selected key before the end key");
    }

    private void ResetAndFreeze()
    {
        if (_drag != null || _pose == null) return;
        if (!CanResetAndFreeze())
        {
            _status.Text = "Reset & Freeze works in the Rigging context on an unskinned mesh picked in the hierarchy.";
            return;
        }
        PoseEditSession? session = Session;
        if (session == null)
        {
            _status.Text = "Open a scene with a character first.";
            return;
        }
        RunKeyEdit(delegate
        {
            if (session.ResetAndFreeze(FreezeSelection(), out string? reason) == 0)
                _status.Text = reason ?? "Nothing was frozen.";
        });
        _pose.ReloadGeometry();
        Apply();
        UpdateSelection(forceSpinner: true);
    }

    /// <summary>After undo or redo: a Reset &amp; Freeze step rewrote mesh geometry, which the drawn meshes cache.</summary>
    private void AfterHistoryStep(EditRecord? step)
    {
        if (_pose != null && step?.Description.StartsWith(PoseEditSession.FreezeDescription, StringComparison.Ordinal) == true)
        {
            _pose.ReloadGeometry();
            Apply();
        }
        else if (_pose != null && IsNormalizeStep(step?.Description))
            ReloadAfterRescale();
        AfterCharBankHistoryStep(step);
        AfterSceneDeleteHistoryStep(step);
        AfterSharedBankHistoryStep(step);
    }

    private IEnumerable<Control> TransformToolControls()
    {
        if (_channelBoxes == null || _groupButtons == null || _transformActions == null) yield break;
        foreach (var box in _channelBoxes) yield return box;
        foreach (var button in _groupButtons) yield return button;
        foreach (var button in _transformActions) yield return button;
        foreach (var field in _extraFields) yield return field;
        yield return SpaceButton();
        yield return CompensateBox();
    }
}
