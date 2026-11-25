// The Properties panel: the selected object's Name, Visible and Compensate Scale, for an IK or
// reverse-foot controller (IK_LookAt included) its Target Size Multiplier
// and for
// an animated camera its Mode, Ratio, Near Clip and Far Clip.
//
// The panel is built once and re-parented by the dock (SharedControls). Every edit is one undo step
// through PoseEditSession.EditObject when a session exists, else a direct edit that dirties the scene.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    // Rows are compact (24 px instead of the theme's 32) so all four fit the 150 px content height
    // The layout stores for CPropertiesDialog.
    private const double PropRowHeight = 24.0;
    private static readonly Thickness PropFieldPadding = new Thickness(4.0, 2.0);

    private StackPanel? _propertiesPanel;
    private readonly TextBox _propName = CompactField();
    private readonly CheckBox _propVisible = new CheckBox { Content = "Visible", MinHeight = PropRowHeight };
    private readonly CheckBox _propCompensate = new CheckBox { Content = "Compensate Scale", MinHeight = PropRowHeight };
    private readonly TextBlock _propTargetSizeLabel = new TextBlock
    {
        Text = "Target Size Multiplier",
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBox _propTargetSize = CompactField();
    private DockPanel? _propTargetSizeRow;
    // An animated camera's frame: Mode (landscape / portrait / square), Ratio preset, clip planes.
    private static readonly string[] CameraModeNames = ["Landscape", "Portrait", "Square (1:1)"];
    private static readonly string[] CameraRatioNames = ["32:9", "21:9", "2:1", "16:9", "16:10", "4:3"];
    private readonly ComboBox _propCameraMode = new ComboBox { ItemsSource = CameraModeNames, MinHeight = PropRowHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _propCameraRatio = new ComboBox { MinHeight = PropRowHeight, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _propNearClip = CompactField();
    private readonly TextBox _propFarClip = CompactField();
    private StackPanel? _propCameraRows;
    private bool _propSyncing;

    /// <summary>The object the Properties panel shows: the current controller, else the tree's row.</summary>
    private Object3D? PropertiesTarget => _selected ?? _outliner.Selected;

    private Control PropertiesPanel => _propertiesPanel ??= BuildPropertiesPanel();

    private static TextBox CompactField() => new TextBox
    {
        MinWidth = 0.0,
        MinHeight = PropRowHeight,
        Padding = PropFieldPadding,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private StackPanel BuildPropertiesPanel()
    {
        // Each labelled row is its own DockPanel: the label keeps its width and the field fills the
        // rest of the panel, so the field neither moves with the other rows' labels nor grows with
        // its text.
        StackPanel panel = new StackPanel { Margin = new Thickness(8.0), Spacing = 4.0 };
        panel.Children.Add(LabelledRow("Name", _propName));
        panel.Children.Add(_propVisible);
        panel.Children.Add(_propCompensate);
        _propTargetSizeRow = LabelledRow(_propTargetSizeLabel, _propTargetSize);
        panel.Children.Add(_propTargetSizeRow);
        _propCameraRows = new StackPanel { Spacing = 4.0 };
        _propCameraRows.Children.Add(LabelledRow("Mode", _propCameraMode));
        _propCameraRows.Children.Add(LabelledRow("Ratio", _propCameraRatio));
        _propCameraRows.Children.Add(LabelledRow("Near Clip", _propNearClip));
        _propCameraRows.Children.Add(LabelledRow("Far Clip", _propFarClip));
        panel.Children.Add(_propCameraRows);
        _propCameraMode.SelectionChanged += (_, _) =>
        {
            if (_propSyncing || PropertiesTarget is not CameraAnimated cam || _propCameraMode.SelectedIndex < 0) return;
            int mode = _propCameraMode.SelectedIndex;
            EditProperty(cam, $"Set the mode of '{cam.Name}' to {CameraModeNames[mode]}", () =>
            {
                if (cam.Mode == mode) return false;
                cam.Mode = mode;
                return true;
            });
            RefreshProperties();
        };
        _propCameraRatio.SelectionChanged += (_, _) =>
        {
            if (_propSyncing || PropertiesTarget is not CameraAnimated cam || _propCameraRatio.SelectedIndex < 0) return;
            int ratio = _propCameraRatio.SelectedIndex;
            EditProperty(cam, $"Set the ratio of '{cam.Name}'", () =>
            {
                if (cam.Ratio == ratio) return false;
                cam.Ratio = ratio;
                return true;
            });
        };
        foreach (var (field, far) in new[] { (_propNearClip, false), (_propFarClip, true) })
        {
            field.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitClip(far); e.Handled = true; }
                else if (e.Key == Key.Escape) { RefreshProperties(); e.Handled = true; }
            };
            field.LostFocus += (_, _) => CommitClip(far);
        }

        _propName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitPropertyName(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshProperties(); e.Handled = true; }
        };
        _propName.LostFocus += (_, _) => CommitPropertyName();
        _propTargetSize.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitPropertyTargetSize(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshProperties(); e.Handled = true; }
        };
        _propTargetSize.LostFocus += (_, _) => CommitPropertyTargetSize();
        // Visible and Compensate Scale act on every selected object. A mixed selection shows the
        // indeterminate "-"; clicking it (or an unticked box) turns all of them on, a ticked box off.
        _propVisible.Click += (_, _) =>
        {
            var targets = PropertiesTargets();
            if (_propSyncing || targets.Count == 0) { RefreshProperties(); return; }
            bool shown = _propVisibleShown != true;
            var change = targets.Where(o => o.IsShown != shown).ToArray();
            if (change.Length > 0) ChangeShown(change, shown);
            RefreshProperties();
        };
        _propCompensate.Click += (_, _) =>
        {
            var targets = PropertiesTargets();
            if (_propSyncing || targets.Count == 0) { RefreshProperties(); return; }
            bool on = _propCompensateShown != true;
            string who = targets.Count == 1 ? $"'{targets[0].Name}'" : $"{targets.Count} objects";
            EditProperties(targets, $"{(on ? "Compensate" : "Stop compensating")} scale on {who}", () =>
            {
                bool changed = false;
                foreach (var o in targets)
                    if (o.CompensatesParentScale != on) { o.CompensatesParentScale = on; changed = true; }
                return changed;
            });
            UpdateSelection(forceSpinner: true);
        };
        return panel;

        static DockPanel LabelledRow(object label, Control field)
        {
            Control text = label as Control ?? new TextBlock { Text = (string)label, VerticalAlignment = VerticalAlignment.Center };
            text.Margin = new Thickness(0.0, 0.0, 6.0, 0.0);
            DockPanel.SetDock(text, Dock.Left);
            DockPanel row = new DockPanel { LastChildFill = true };
            row.Children.Add(text);
            row.Children.Add(field);
            return row;
        }
    }

    private bool? _propVisibleShown;
    private bool? _propCompensateShown;

    /// <summary>
    /// Every object the panel edits: the viewport selection when it holds several, else the tree's
    /// selected rows when they are several, else <see cref="PropertiesTarget"/> alone.
    /// </summary>
    private IReadOnlyList<Object3D> PropertiesTargets()
    {
        if (_selectedAll.Count > 1) return _selectedAll.ToArray();
        if (_selected == null && _outliner.SelectedObjects.Count > 1) return _outliner.SelectedObjects.ToArray();
        return PropertiesTarget is { } o ? [o] : [];
    }

    /// <summary>The common value of <paramref name="value"/> over <paramref name="objects"/>, null when mixed.</summary>
    private static bool? Common(IReadOnlyList<Object3D> objects, Func<Object3D, bool> value)
    {
        if (objects.Count == 0) return false;
        bool first = value(objects[0]);
        return objects.All(o => value(o) == first) ? first : null;
    }

    /// <summary>Fills the panel from <see cref="PropertiesTargets"/>: one object's values, or the selection's combined ones.</summary>
    private void RefreshProperties()
    {
        var targets = PropertiesTargets();
        bool multiple = targets.Count > 1;
        Object3D? o = multiple ? null : targets.FirstOrDefault();
        _propSyncing = true;
        try
        {
            bool any = targets.Count > 0;
            // Several objects: the name is a greyed "-"; the boxes show the shared value or "-".
            DisabledHint.SetEnabled(_propName, any && !multiple, "exactly one selected object");
            DisabledHint.SetEnabled(_propVisible, any, "a selected object");
            DisabledHint.SetEnabled(_propCompensate, any, "a selected object");
            _propName.Text = multiple ? "-" : o?.Name ?? "";
            _propVisibleShown = Common(targets, t => t.IsShown);
            _propCompensateShown = Common(targets, t => t.CompensatesParentScale);
            _propVisible.IsChecked = _propVisibleShown;
            _propCompensate.IsChecked = _propCompensateShown;
            bool ik = any && targets.All(t => t is ControllerIK);
            if (_propTargetSizeRow != null) _propTargetSizeRow.IsVisible = ik;
            var sized = targets.OfType<Controller3D>().ToArray();
            DisabledHint.SetEnabled(_propTargetSize, any && sized.Length == targets.Count && sized.All(c => c.HasTargetRenderSizeRatio), "selected controllers that all have a target size");
            _propTargetSize.Text = sized.Length > 0 && sized.All(c => c.TargetRenderSizeRatio == sized[0].TargetRenderSizeRatio)
                ? sized[0].TargetRenderSizeRatio.ToString("0.0##", CultureInfo.InvariantCulture)
                : "";
            var cam = o as CameraAnimated;
            if (_propCameraRows != null) _propCameraRows.IsVisible = cam != null;
            if (cam != null)
            {
                int mode = cam.Mode is 0 or 1 ? cam.Mode : 2;
                _propCameraMode.SelectedIndex = mode;
                _propCameraRatio.ItemsSource = mode == 1
                    ? CameraRatioNames.Select(r => string.Join(":", r.Split(':').Reverse())).ToArray()
                    : CameraRatioNames;
                _propCameraRatio.SelectedIndex = Math.Clamp(cam.Ratio, 0, CameraRatioNames.Length - 1);
                DisabledHint.SetEnabled(_propCameraRatio, mode != 2, "the Landscape or Portrait mode (Square is always 1:1)");
                _propNearClip.Text = cam.NearClipPlane.ToString("0.###", CultureInfo.InvariantCulture);
                _propFarClip.Text = cam.FarClipPlane.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _propSyncing = false;
        }
    }

    private void CommitPropertyName()
    {
        if (_propSyncing || PropertiesTargets() is not [var o]) return;
        string requested = (_propName.Text ?? "").Trim();
        if (requested.Length == 0 || requested == o.Name)
        {
            RefreshProperties();
            return;
        }
        Character? owner = (o as Character) == null ? o.Ancestor<Character>() : null;
        string name = owner != null ? RigEditor.UniqueControllerName(owner, requested) : requested;
        string old = o.Name ?? "";
        EditProperty(o, $"Rename '{old}' to '{name}'", () =>
        {
            o.Name = name;
            return true;
        });
        if (name != requested) _status.Text = $"'{requested}' is taken; renamed to '{name}'.";
    }

    /// <summary>Writes the Near Clip or Far Clip field into the selected animated camera.</summary>
    private void CommitClip(bool far)
    {
        if (_propSyncing || PropertiesTarget is not CameraAnimated cam) return;
        TextBox field = far ? _propFarClip : _propNearClip;
        string text = (field.Text ?? "").Trim().Replace(',', '.');
        float near = cam.NearClipPlane, farPlane = cam.FarClipPlane;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value) || value <= 0f
            || (far ? value <= near : value >= farPlane))
        {
            _status.Text = far ? "Far Clip must be a number above Near Clip." : "Near Clip must be a number above 0 and below Far Clip.";
            RefreshProperties();
            return;
        }
        if (value == (far ? farPlane : near))
        {
            RefreshProperties();
            return;
        }
        string label = far ? "far clip" : "near clip";
        EditProperty(cam, $"Set the {label} of '{cam.Name}' to {value.ToString("0.###", CultureInfo.InvariantCulture)}", () =>
        {
            if (far) cam.FarClipPlane = value; else cam.NearClipPlane = value;
            return true;
        });
    }

    private void CommitPropertyTargetSize()
    {
        var targets = PropertiesTargets();
        if (_propSyncing || targets.Count == 0
            || !targets.All(t => t is Controller3D { HasTargetRenderSizeRatio: true })) return;
        var sized = targets.Cast<Controller3D>().ToArray();
        string text = (_propTargetSize.Text ?? "").Trim().Replace(',', '.');
        if (text.Length == 0 && sized.Length > 1)
        {
            RefreshProperties();
            return;
        }
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            || !float.IsFinite(value) || value <= 0f)
        {
            _status.Text = "Target Size Multiplier must be a number above 0.";
            RefreshProperties();
            return;
        }
        if (sized.All(c => c.TargetRenderSizeRatio == value))
        {
            RefreshProperties();
            return;
        }
        string who = sized.Length == 1 ? $"'{sized[0].Name}'" : $"{sized.Length} controllers";
        EditProperties(sized, $"Set target size of {who} to {value.ToString("0.0##", CultureInfo.InvariantCulture)}", () =>
        {
            foreach (var c in sized) c.TargetRenderSizeRatio = value;
            return true;
        });
    }

    /// <summary>One Properties edit of several objects: one undo step when a session exists, else a direct edit.</summary>
    private void EditProperties(IReadOnlyList<Object3D> objects, string description, Func<bool> edit)
    {
        if (objects.Count == 1) { EditProperty(objects[0], description, edit); return; }
        if (_pose == null) return;
        PoseEditSession? session = Session;
        if (session != null)
            RunKeyEdit(delegate { session.EditObjects(objects, description, edit); });
        else if (edit())
        {
            _dirty = true;
            UpdateTitle();
            Apply();
        }
        _status.Text = description + ".";
        _outliner.Build(_pose.Model.Scene);
        RefreshPicker();
        RefreshProperties();
        _viewport.RequestNextFrameRendering();
    }

    /// <summary>One Properties edit: an undo step when a session exists, else a direct edit.</summary>
    private void EditProperty(Object3D o, string description, Func<bool> edit)
    {
        if (_pose == null) return;
        PoseEditSession? session = Session;
        if (session != null)
        {
            RunKeyEdit(delegate
            {
                session.EditObject(o, description, edit);
            });
        }
        else if (edit())
        {
            _dirty = true;
            UpdateTitle();
            Apply();
        }
        _status.Text = description + ".";
        _outliner.Build(_pose.Model.Scene);
        RefreshPicker();
        RefreshProperties();
        _viewport.RequestNextFrameRendering();
    }
}
