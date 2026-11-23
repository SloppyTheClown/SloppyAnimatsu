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

    // The Picker's icons, drawn on a 12x12 grid (the Scene window's glyph size, so its IK, RF and
    // blendshape glyphs sit beside them unchanged).

    /// <summary>The colour every Picker icon is drawn in: the IK/RF stamps' yellow.</summary>
    private static readonly IBrush PickerIconBrush = PickerStampBrush;

    /// <summary>The same yellow, faded: the half of the figure a side stamp does not pick.</summary>
    private static readonly IBrush PickerIconDimBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E), 0.28);

    /// <summary>The all-blendshapes stamp: three small heads (circles) in a triangle.</summary>
    private const string PickerHeadsIcon =
        "M3.2,1.2 A2.2,2.2 0 1 0 3.21,1.2 Z M8.8,1.2 A2.2,2.2 0 1 0 8.81,1.2 Z M6,6.4 A2.2,2.2 0 1 0 6.01,6.4 Z";

    /// <summary>The face's outline circle, inset by half the dotted stroke so it lines up with the filled edge.</summary>
    private const string PickerFaceOutline = "M6,1.2 A4.8,4.8 0 1 0 6.01,1.2 Z";

    /// <summary>
    /// A face-direction stamp: <see cref="PickerFaceIcon"/> filled on the <paramref name="shown"/>
    /// half (the direction it picks) and only a dotted outline on the other half.
    /// </summary>
    private static Control PickerHalfFace(Rect shown, Rect dotted)
    {
        var canvas = new Grid { Width = 12.0, Height = 12.0 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(PickerFaceIcon),
            Fill = PickerIconBrush,
            Clip = new RectangleGeometry(shown)
        });
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(PickerFaceOutline),
            Stroke = PickerIconBrush,
            StrokeThickness = 0.8,
            // Dash and gap are in stroke widths: about 1.4 px on, 1.2 px off at the stamp's 12 px.
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1.8, 1.5 },
            Clip = new RectangleGeometry(dotted)
        });
        return new Viewbox { Width = 12.0, Height = 12.0, Child = canvas, IsHitTestVisible = false };
    }

    // The halves of the 12x12 face, for PickerHalfFace.
    private static readonly Rect PickerTopHalf = new(0.0, 0.0, 12.0, 6.0);
    private static readonly Rect PickerBottomHalf = new(0.0, 6.0, 12.0, 6.0);
    private static readonly Rect PickerLeftHalf = new(0.0, 0.0, 6.0, 12.0);
    private static readonly Rect PickerRightHalf = new(6.0, 0.0, 6.0, 12.0);

    /// <summary>A standing figure's left half on screen (half head, one arm, one leg), split at x = 6.</summary>
    private const string PickerFigureScreenLeft =
        "M6,0.4 A1.6,1.6 0 0 0 6,3.6 Z M6,4.2 H3.6 L1,7.6 L1.9,8.2 L3.8,6.2 V7.6 L2.8,11.6 H4.3 L5.6,8 H6 Z";

    /// <summary>The figure's right half on screen: <see cref="PickerFigureScreenLeft"/> mirrored about x = 6.</summary>
    private const string PickerFigureScreenRight =
        "M6,0.4 A1.6,1.6 0 0 1 6,3.6 Z M6,4.2 H8.4 L11,7.6 L10.1,8.2 L8.2,6.2 V7.6 L9.2,11.6 H7.7 L6.4,8 H6 Z";

    /// <summary>A face: a round head with two eyes and a smile cut out (even-odd fill).</summary>
    private const string PickerFaceIcon =
        "M6,0.8 A5.2,5.2 0 1 0 6.01,0.8 Z M4.2,3.6 A0.9,0.9 0 1 0 4.21,3.6 Z M7.8,3.6 A0.9,0.9 0 1 0 7.81,3.6 Z " +
        "M3.3,7 Q6,10.2 8.7,7 Q6,8.7 3.3,7 Z";


    /// <summary>A hammer: a head with a bevelled striking face, and its handle.</summary>
    private const string PickerHammerIcon = "M1.6,1.4 H8.2 L10.6,2.2 V4.2 L8.2,5 H1.6 Z M5,5 H7 V11.4 H5 Z";

    /// <summary>A 12x12 icon of stacked paths (each with its own brush), scaled to <paramref name="size"/>.</summary>
    private static Control PickerIcon(double size, params (string Data, IBrush Fill)[] layers)
    {
        var canvas = new Grid { Width = 12.0, Height = 12.0 };
        foreach (var (data, fill) in layers)
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Fill = fill });
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    /// <summary>
    /// The hammer (layer-edit mode) icon, tilted 45 degrees: drawn upright it reads as a "T" at
    /// 10-12 px.
    /// </summary>
    private static Control PickerHammer(double size)
    {
        Control icon = PickerIcon(size, (PickerHammerIcon, PickerIconBrush));
        icon.RenderTransform = new RotateTransform(45.0);
        return icon;
    }

    /// <summary>A stamp-sized (12 px) Picker icon.</summary>
    private static Control PickerIcon(params (string Data, IBrush Fill)[] layers) => PickerIcon(12.0, layers);

    private readonly ToggleButton _pickerLayerToggle = new()
    {
        Content = PickerHammer(12.0),
        Height = 18.0,
        MinHeight = 0.0,
        Padding = new Thickness(0.0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
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
                Content = PickerHammer(10.0),
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