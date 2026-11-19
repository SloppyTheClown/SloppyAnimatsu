// The Picker panel's list: the flat rows, the current mixer layer's filter, the layer-edit
// ("hammer") mode and the row's dim/hammer decorations. The rules are Core's (PickerRows); this
// part only draws them and runs the layer's add/remove.
//
// The mode is what the app enters from the hammer next to a layer's name in the Mixer: every
// controller is listed, each row carries a hammer that is lit when the layer has a track for it,
// and clicking it adds or removes that track (the same edit as H on the selection).
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The layer-edit mode (the hammer next to a Mixer layer name).</summary>
    private bool _pickerLayerMode;

    /// <summary>The yellow the two glyph stamps (IK, RF) are drawn in.</summary>
    private static readonly IBrush PickerStampBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E));

    /// <summary>The evaluator's controller count at the last build, for <see cref="RigStructureChanged"/>.</summary>
    private int _pickerControllerCount;

    /// <summary>The bones an IK controller solves, rebuilt with the list: those rows are dimmed.</summary>
    private HashSet<Object3D> _pickerIkDriven = new(ReferenceEqualityComparer.Instance);

    /// <summary>The layer the list is filtered by: the timeline's stack, and none in the rigging context.</summary>
    private AnimStack? PickerLayer => _animation != null ? _timeline.ActiveStack : null;

    private readonly ToggleButton _pickerLayerToggle = new()
    {
        Content = "\u2692",
        FontSize = 11.0,
        Height = 18.0,
        MinHeight = 0.0,
        Padding = new Thickness(0.0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The layer-edit mode's own toggle, in the Picker's button row.</summary>
    private ToggleButton PickerLayerToggle()
    {
        ToolTip.SetTip(_pickerLayerToggle,
            "Edit the current layer's joints: list every controller and add or remove its track with the row's hammer");
        _pickerLayerToggle.Click += delegate
        {
            SetPickerLayerMode(!_pickerLayerMode);
        };
        return _pickerLayerToggle;
    }

    /// <summary>
    /// Rebuilds the list from the character, the current layer and the mode, keeping the selection
    /// (the rows are new objects, so the ListBox items are matched by their controller).
    /// </summary>
    private void RefreshPicker()
    {
        // The layer filter and the hammer mode are the Animation context's: without a current
        // stack (the rigging context, or no animation) the list is every controller, and the
        // toggle that would enter the mode is off.
        bool hasLayer = _animation != null && _timeline.ActiveStack != null;
        if (!hasLayer) _pickerLayerMode = false;
        _pickerLayerToggle.IsEnabled = _animation != null;
        _pickerLayerToggle.IsChecked = _pickerLayerMode;
        if (_pose == null)
        {
            _controllers.ItemsSource = null;
            _pickerControllerCount = 0;
            _pickerIkDriven.Clear();
            return;
        }
        // Shoe locators belong to the Rigging context's bind pose and are never picker rows.
        IReadOnlyList<Object3D> all = _character != null
            ? _pose.Evaluator(_character).Controllers.Where(c => c is not ReverseFootLocator).ToList()
            : [];
        _pickerControllerCount = all.Count;
        _pickerIkDriven = _character != null
            ? new HashSet<Object3D>(PickerRows.IkDriven(_character), ReferenceEqualityComparer.Instance)
            : new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        var rows = PickerRows.Rows(all, PickerLayer, _pickerLayerMode);
        var items = rows.Select(c => new Item<Object3D>(c, c.Name ?? "(unnamed)")).ToList();
        bool syncing = _syncing;
        _syncing = true;
        _controllers.ItemsSource = items;
        _controllers.SelectedItems?.Clear();
        foreach (Item<Object3D> item in items.Where(i => i.Value != null && _selectedAll.Contains(i.Value)))
        {
            _controllers.SelectedItems?.Add(item);
        }
        _syncing = syncing;
    }

    /// <summary>Enters or leaves the layer-edit mode; both entry points (the Picker and the Mixer) come here.</summary>
    private void SetPickerLayerMode(bool on)
    {
        if (on && (_animation == null || _timeline.ActiveStack == null))
        {
            _status.Text = "The layer's joints are edited in the Animation context, with a stack picked in the Mixer.";
            return;
        }
        _pickerLayerMode = on;
        _pickerLayerToggle.IsChecked = on;
        RefreshPicker();
        RefreshMixer();
        _status.Text = on
            ? "Picker: every controller is listed; the hammer adds or removes it in the current layer."
            : "Picker: only the current layer's controllers are listed.";
    }

    /// <summary>The Mixer's hammer on a layer row: make that layer current and edit its joints.</summary>
    private void EditLayerJoints(AnimStack stack)
    {
        if (_syncing) return;
        _timeline.Select(stack, []);
        Session?.SetCurrentStack(stack);
        SetPickerLayerMode(true);
    }

    /// <summary>One Picker row: the flat name, dimmed for an IK-driven bone or a missing layer member, and the hammer.</summary>
    private Control PickerRowView(Item<Object3D>? item)
    {
        // Avalonia's item container clears its content while it recycles a row, and a template then
        // builds with a null item: a rebuild of the list (a new ItemsSource) must not crash the app.
        if (item == null) return new TextBlock();
        Object3D? controller = item.Value;
        bool ikDriven = controller != null && _pickerIkDriven.Contains(controller);
        bool inLayer = controller != null && PickerRows.InLayer(PickerLayer, controller);
        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 11.0,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = ikDriven || (_pickerLayerMode && !inLayer) ? 0.42 : 1.0
        };
        ToolTip.SetTip(label, ikDriven
            ? item.Label + ": solved by an IK controller"
            : item.Label);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,16"),
            Margin = new Thickness(2.0, 0.0, 2.0, 0.0)
        };
        grid.Children.Add(label);
        if (_pickerLayerMode && controller != null)
        {
            var hammer = new Button
            {
                Content = "\u2692",
                FontSize = 10.0,
                Width = 16.0,
                Height = 16.0,
                MinWidth = 0.0,
                MinHeight = 0.0,
                Padding = new Thickness(0.0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0.0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Opacity = inLayer ? 0.95 : 0.3
            };
            ToolTip.SetTip(hammer, inLayer
                ? "'" + item.Label + "' is in the layer: click to remove it"
                : "'" + item.Label + "' is not in the layer: click to add it");
            hammer.Click += delegate
            {
                TogglePickerLayerMember(controller);
            };
            Grid.SetColumn(hammer, 1);
            grid.Children.Add(hammer);
        }
        return grid;
    }

    /// <summary>Adds or removes one controller's track in the current layer: the row's hammer.</summary>
    private void TogglePickerLayerMember(Object3D controller)
    {
        if (_drag != null || _timeline.IsDragging) return;
        if (Session is not { } session) return;
        AnimStack? layer = PickerLayer;
        if (layer == null)
        {
            _status.Text = "Pick a stack in the Mixer to edit its joints.";
            return;
        }
        if (layer.TrackFor(controller) != null)
        {
            RunKeyEdit(delegate
            {
                session.RemoveFromStack(layer, [controller], controller);
            });
        }
        else
        {
            if (!KeyEditor.CanAddToStack(controller, out string? reason))
            {
                _status.Text = reason;
                return;
            }
            RunKeyEdit(delegate
            {
                session.AddToStack(layer, [controller]);
            });
        }
    }
}