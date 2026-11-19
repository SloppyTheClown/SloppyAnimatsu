// The bodies of the dock panels (Anim Bank, Scene, Properties, Transform, Picker, Mixer,
// Blendshapes, Point of View) and the timeline body.
//
// Each body is built once and then re-parented by the dock, so none of them may assume where it is
// shown. The panel own state (fold, size, dock side) lives in FloatingPanelState; these methods
// only fill the content and read or write the document.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
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
    private Control BankBody()
    {
        // The bank of the character chosen in the Scene panel: no character combo of its own
        // (_characters stays as the record of the choice, but is not shown).
        Grid grid = new Grid
        {
            Margin = new Thickness(4.0)
        };
        // The list with a small button bar under it; the bar follows the list's visibility (it is
        // hidden with the list while the panel shows the CHAR BANK).
        Button BarButton(string text, string tip, Action click)
        {
            Button b = new Button
            {
                Content = text,
                Padding = new Thickness(6.0, 1.0),
                Margin = new Thickness(0.0, 0.0, 3.0, 0.0),
                FontSize = 11.0
            };
            ToolTip.SetTip(b, tip);
            b.Click += delegate { click(); };
            return b;
        }
        WrapPanel bar = new WrapPanel
        {
            Margin = new Thickness(0.0, 3.0, 0.0, 0.0),
            Children =
            {
                BarButton("New", "New animation", NewAnimation),
                BarButton("Folder", "New folder", NewFolder),
                BarButton("Delete", "Delete the selected animations and folders (Delete)", DeleteBankSelection),
                BarButton("Retarget…", "Retarget animations from one character onto another", () => _ = OpenRetarget())
            }
        };
        bar.Bind(Visual.IsVisibleProperty, _animations.GetObservable(Visual.IsVisibleProperty));
        DockPanel.SetDock(bar, Dock.Bottom);
        Control viewBar = BankViewBar();
        viewBar.Bind(Visual.IsVisibleProperty, _animations.GetObservable(Visual.IsVisibleProperty));
        DockPanel.SetDock(viewBar, Dock.Top);
        DockPanel bankList = new DockPanel { LastChildFill = true };
        bankList.Children.Add(viewBar);
        bankList.Children.Add(bar);
        bankList.Children.Add(BankListWithDragLayer());
        grid.Children.Add(bankList);
        // In the Rigging context the same panel is the CHAR BANK (CCharacterBankDialog).
        grid.Children.Add(CharBankBody());
        RefreshCharBank();
        return grid;
    }

    private Control SceneBody()
    {
        return _outliner;
    }

    private Control PropertiesBody()
    {
        RefreshProperties();
        return PropertiesPanel;
    }

    private Control TransformBody()
    {
        EnsureTransformTools();
        // Top: the action buttons, two to a row.
        UniformGrid actions = new UniformGrid { Columns = 2 };
        foreach (Button action in _transformActions!)
        {
            action.Margin = new Thickness(0.0, 0.0, 3.0, 3.0);
            actions.Children.Add(action);
        }
        // Below: one block per group - [symbol][branch lines][on/off tick][label][drag strip + value].
        // The symbol (its name as hover text) ticks or unticks the whole group; the drag strip is
        // the SpinnerField's own left edge.
        const double rowSpacing = 1.0;
        Grid values = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,40,*"),
            RowSpacing = rowSpacing,
            ColumnSpacing = 2.0
        };
        int row = 0;
        for (int g = 0; g < ChannelGroups.Length; g++)
        {
            var (_, _, first, labels) = ChannelGroups[g];
            Group(_groupButtons![g], labels.Length);
            for (int i = 0; i < labels.Length; i++)
                ValueRow(_channelBoxes![first + i], labels[i], ChannelField(first + i));
            if (g == 4)
            {
                // Space Switch sits after the reverse foot: the parent space of the current object,
                // "Parent" until a space switch changes it.
                TextBlock space = new TextBlock
                {
                    Text = "⇄", FontSize = 12.0, Width = 22.0, TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTip.SetTip(space, "Space Switch");
                Group(space, 1);
                ValueRow(null, "Space", SpaceButton());
            }
        }
        // Compensate Scale, under everything else: keyable per animation (compensate-scale-keys.md).
        TextBlock compensate = new TextBlock
        {
            Text = "⇲", FontSize = 12.0, Width = 22.0, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(compensate, "Compensate Scale");
        // [symbol][branch line][Compensate Scale checkbox], like the other groups' rows.
        Group(compensate, 1);
        values.RowDefinitions.Add(new RowDefinition(new GridLength(21.0)));
        CheckBox compensateBox = CompensateBox();
        compensateBox.VerticalAlignment = VerticalAlignment.Center;
        compensateBox.HorizontalAlignment = HorizontalAlignment.Left;
        compensateBox.MinHeight = 0.0;
        Grid.SetRow(compensateBox, row);
        Grid.SetColumn(compensateBox, 2);
        Grid.SetColumnSpan(compensateBox, 3);
        values.Children.Add(compensateBox);
        row++;
        SyncCompensateBox();
        return new StackPanel
        {
            Spacing = 3.0,
            Margin = new Thickness(8.0),
            Children =
            {
                (Control)actions,
                (Control)new Separator { Margin = new Thickness(0.0, 2.0) },
                (Control)values
            }
        };
        // A group's symbol and branch lines, spanning its rows; a gap row separates groups.
        void Group(Control symbol, int rows)
        {
            if (row > 0)
            {
                values.RowDefinitions.Add(new RowDefinition(new GridLength(2.0)));
                row++;
            }
            symbol.VerticalAlignment = VerticalAlignment.Center;
            symbol.Margin = new Thickness(0.0);
            Grid.SetRow(symbol, row);
            Grid.SetRowSpan(symbol, rows);
            values.Children.Add(symbol);
            BranchLines lines = new BranchLines(rows, rowSpacing) { IsHitTestVisible = false };
            Grid.SetRow(lines, row);
            Grid.SetRowSpan(lines, rows);
            Grid.SetColumn(lines, 1);
            values.Children.Add(lines);
        }
        // One value row: the channel's on/off tick, its label and its value.
        void ValueRow(CheckBox? box, string label, Control field)
        {
            values.RowDefinitions.Add(new RowDefinition(new GridLength(21.0)));
            if (box != null)
            {
                box.HorizontalAlignment = HorizontalAlignment.Center;
                box.VerticalAlignment = VerticalAlignment.Center;
                box.MinHeight = 0.0;
                Grid.SetRow(box, row);
                Grid.SetColumn(box, 2);
                values.Children.Add(box);
            }
            TextBlock name = new TextBlock { Text = label, FontSize = 11.0, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 3);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 4);
            field.VerticalAlignment = VerticalAlignment.Center;
            values.Children.Add(name);
            values.Children.Add(field);
            row++;
        }
    }

    private Control PickerBody()
    {
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            Margin = new Thickness(8.0),
            RowSpacing = 4.0
        };
        // The move/rotate/scale mode buttons are not the Picker's: W/E/R and the viewport set the
        // mode. Only the layer-edit hammer stays in this row.
        Control layerToggle = PickerLayerToggle();
        layerToggle.HorizontalAlignment = HorizontalAlignment.Left;
        layerToggle.Width = 32.0;
        Grid.SetRow(layerToggle, 0);
        grid.Children.Add(layerToggle);
        // The eleven Aux_Pick_Select_* stamps: everything, a side, IK, reverse foot, the
        // blendshape controllers and the four face directions.
        // IK and RF carry the Scene window's own glyphs (a hand and a boot), drawn yellow.
        var pickerStamps = new StackPanel { Spacing = 2.0 };
        pickerStamps.Children.Add(PickerStampRow(
            PickerStamp("All", "Pick every controller", PickerGroup.All),
            PickerStamp("L", "Pick the left-side controllers (_L / .L)", PickerGroup.LeftSide),
            PickerStamp("R", "Pick the right-side controllers (_R / .R)", PickerGroup.RightSide),
            PickerStamp("IK", "Pick the IK controllers", PickerGroup.IK, OutlinerStyle.GlyphOf(OutlinerKind.IkController)),
            PickerStamp("RF", "Pick the reverse-foot controllers", PickerGroup.ReverseFoot, OutlinerStyle.GlyphOf(OutlinerKind.ReverseFootController))));
        pickerStamps.Children.Add(PickerStampRow(
            PickerStamp("BS", "Pick the blendshape controllers", PickerGroup.Blendshape),
            PickerStamp("Head", "Pick the blendshape face controllers", PickerGroup.Face),
            PickerStamp("▲", "Pick the upward blendshape channels", PickerGroup.FaceUp),
            PickerStamp("▼", "Pick the downward blendshape channels", PickerGroup.FaceDown),
            PickerStamp("◀", "Pick the leftward blendshape channels", PickerGroup.FaceLeft),
            PickerStamp("▶", "Pick the rightward blendshape channels", PickerGroup.FaceRight)));
        Grid.SetRow(pickerStamps, 1);
        grid.Children.Add(pickerStamps);
        Grid.SetRow(_controllers, 2);
        grid.Children.Add(_controllers);
        return grid;
    }

    /// <summary>One row of the Picker's stamps, evenly divided like the two icon rows.</summary>
    private static Control PickerStampRow(params Control[] stamps)
    {
        var row = new UniformGrid { Columns = stamps.Length };
        foreach (var stamp in stamps) row.Children.Add(stamp);
        return row;
    }

    private Button PickerStamp(string label, string tip, PickerGroup group, string? glyph = null)
    {
        var stamp = new Button
        {
            Content = glyph == null ? label : new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(glyph),
                Fill = PickerStampBrush,
                Width = 12.0,
                Height = 12.0,
                Stretch = Stretch.Uniform
            },
            FontSize = 9.0,
            Height = 18.0,
            MinHeight = 0.0,
            Margin = new Thickness(1.0, 0.0),
            Padding = new Thickness(0.0, 0.0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(stamp, tip);
        stamp.Click += delegate
        {
            PickPickerGroup(group);
        };
        return stamp;
    }

    /// <summary>
    /// Picks the controllers a stamp names and makes them the selection (the Picker rows, the
    /// viewport highlight and the transform panel follow, as when they were clicked one by one).
    /// The list shows the layer's controllers or every one of them, so a pick is narrowed to the
    /// rows that are listed: a stamp never selects something the Picker cannot show.
    /// </summary>
    private void PickPickerGroup(PickerGroup group)
    {
        if (_character == null || _pose == null)
        {
            _status.Text = "Open a scene to pick controllers.";
            return;
        }
        var picked = PickerGroups.Pick(_character, group).ToList();
        int members = picked.Count;
        var shown = (_controllers.ItemsSource as IEnumerable<Item<Object3D>>)?
            .Where(i => i.Value != null).Select(i => i.Value!).ToHashSet(ReferenceEqualityComparer.Instance);
        if (shown != null)
        {
            picked = picked.Where(shown.Contains).ToList();
        }
        _selectedAll.Clear();
        _selectedAll.AddRange(picked);
        SyncSelection(picked.Count > 0 ? picked[^1] : null);
        _status.Text = picked.Count == 0
            ? (members > 0
                ? $"The Picker lists none of the {members} {PickerGroups.Describe(group)} in this layer."
                : $"The scene has no {PickerGroups.Describe(group)}.")
            : $"Picked {picked.Count} controller(s): {PickerGroups.Describe(group)}.";
    }

    /// <summary>
    /// The Mixer window: the
    /// stack list (eye, stack type, name, opacity) with its six-button toolbar, and the current
    /// stack's properties below it.
    /// </summary>
    private Control MixerBody()
    {
        EnsureMixerControls();
        _mixerList.MinHeight = 48.0;
        _stackEnabled.FontSize = 9.0;
        _stackCycle.FontSize = 9.0;
        _stackAdditive.FontSize = 9.0;
        _stackOpacity.FontSize = 9.0;
        _stackOpacity.ShowButtonSpinner = false;
        _stackOpacity.Width = 62.0;
        _stackOpacity.Height = 20.0;
        ToolTip.SetTip(_stackOpacity, "Opacity of the current stack (percent)");
        UniformGrid toolbar = new UniformGrid
        {
            Columns = 3,
            Rows = 2,
            Margin = new Thickness(3.0, 1.0),
            Children =
            {
                (Control)_mixerNew,
                (Control)_mixerDuplicate,
                (Control)_mixerMerge,
                (Control)_mixerExtract,
                (Control)_mixerAudio,
                (Control)_mixerDelete
            }
        };
        WrapPanel properties = new WrapPanel
        {
            Margin = new Thickness(4.0, 1.0, 4.0, 3.0),
            Children =
            {
                (Control)_stackEnabled,
                (Control)_stackCycle,
                (Control)_stackAdditive,
                (Control)_stackOpacity
            }
        };
        Grid.SetRow(_mixerList, 0);
        Grid.SetRow(_mixerTextures, 1);
        Grid.SetRow(toolbar, 2);
        Grid.SetRow(properties, 3);
        Grid grid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(2.0)
        };
        grid.Children.Add(_mixerList);
        grid.Children.Add(_mixerTextures);
        grid.Children.Add(toolbar);
        grid.Children.Add(properties);
        // The body is built after the scene may already be loaded, so it fills itself once here.
        RefreshMixer();
        return grid;
    }

    private Control BlendshapesBody()
    {
        _blendshapeStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 11.0
        };
        _blendshapeControllers = new StackPanel
        {
            Spacing = 6.0
        };
        _blendshapeFaces = new UniformGrid
        {
            Columns = 2
        };
        StackPanel content = new StackPanel
        {
            Spacing = 4.0,
            Margin = new Thickness(8.0),
            Children = 
            {
                (Control)Heading("Camera"),
                PointOfViewButtons(),
                SetUpButtons(),
                (Control)_blendshapeStatus,
                (Control)Heading("Controllers"),
                (Control)_blendshapeControllers,
                (Control)Heading("Faces"),
                (Control)_blendshapeFaces
            }
        };
        ScrollViewer result = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        RefreshBlendshapePanel(rebuild: true);
        return result;
    }

    private Control PointOfViewButtons()
    {
        Button button = new Button
        {
            Content = "Set point of view",
            FontSize = 11.0
        };
        button.Click += delegate
        {
            SetBlendshapePointOfView();
        };
        Button button2 = new Button
        {
            Content = "Camera",
            FontSize = 11.0
        };
        button2.Click += delegate
        {
            ToggleBlendshapeCamera();
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6.0,
            Children = 
            {
                (Control)button,
                (Control)button2
            }
        };
    }

    private Control TimelineBody()
    {
        // Play, its mode, then the stepping and key buttons (a viewer design: the frame field, key
        // range, gap field and key info left the toolbar; the Curves tab rides above the panel).
        Button ToolButton(string content, string tip, Action click)
        {
            var b = new Button { Content = content, MinWidth = 30.0, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += delegate { click(); };
            return b;
        }
        WrapPanel wrapPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
        };
        wrapPanel.Children.AddRange([
            _play,
            _playModeButton,
            ToolButton("|◀", "Previous key", () => StepKey(-1)),
            ToolButton("◀", "Previous frame", () => SetTime(_time - 1f)),
            ToolButton("▶", "Next frame", () => SetTime(_time + 1f)),
            ToolButton("▶|", "Next key", () => StepKey(1)),
            ToolButton("Key", "Key the playhead in the current stack (K)", () => CreateKey()),
            ToolButton("Delete", "Delete the selected keys (Del)", () => DeleteKeys(closeGaps: false)),
        ]);
        DockPanel dockPanel = new DockPanel
        {
            Margin = new Thickness(8.0, TimelineTopMargin, 8.0, 0.0)
        };
        _timelineToolbar = wrapPanel;
        DockPanel.SetDock(wrapPanel, Dock.Top);
        dockPanel.Children.Add(wrapPanel);
        // The timebar grows to "Displayed stacks" rows, then scrolls with this bar or the wheel.
        EnsureTimelineScrollBar();
        Grid rows = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Top
        };
        (_timeline.Parent as Panel)?.Children.Remove(_timeline);
        (_timelineScroll!.Parent as Panel)?.Children.Remove(_timelineScroll);
        rows.Children.Add(_timeline);
        Grid.SetColumn(_timelineScroll, 1);
        rows.Children.Add(_timelineScroll);
        // The range bar under the timebar, with its fit buttons.
        foreach (Control c in new Control[] { _rangeBar, _fitAll, _fitLayer })
            (c.Parent as Panel)?.Children.Remove(c);
        _fitAll.Padding = _fitLayer.Padding = new Thickness(6.0, 0.0);
        _fitAll.Height = _fitLayer.Height = _rangeBar.Height;
        _fitAll.FontSize = _fitLayer.FontSize = 10.0;
        ToolTip.SetTip(_fitAll, "Fit the range to every key of the animation");
        ToolTip.SetTip(_fitLayer, "Fit the range to the active stack's selected keys (or all its keys)");
        DockPanel rangeRow = new DockPanel { Margin = new Thickness(0.0, RangeBarGap, 0.0, 0.0) };
        StackPanel fitButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.0, Margin = new Thickness(4.0, 0.0, 0.0, 0.0) };
        fitButtons.Children.Add(_fitAll);
        fitButtons.Children.Add(_fitLayer);
        DockPanel.SetDock(fitButtons, Dock.Right);
        rangeRow.Children.Add(fitButtons);
        rangeRow.Children.Add(_rangeBar);
        StackPanel body = new StackPanel { Orientation = Orientation.Vertical };
        body.Children.Add(rows);
        body.Children.Add(rangeRow);
        dockPanel.Children.Add(body);
        return dockPanel;
    }

    private const double TimelineTopMargin = 6.0;

    /// <summary>The space between the timebar and the range bar under it.</summary>
    private const double RangeBarGap = 3.0;

    private WrapPanel? _timelineToolbar;

    private ScrollBar? _timelineScroll;

    private bool _timelineScrollSyncing;

    private bool _fittingTimeline;

    /// <summary>Creates the timebar's scroll bar once and ties it to the timebar both ways.</summary>
    private void EnsureTimelineScrollBar()
    {
        if (_timelineScroll != null) return;
        _timelineScroll = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            AllowAutoHide = false,
            Minimum = 0.0,
            SmallChange = TimelineLayout.RowHeight,
            LargeChange = TimelineLayout.RowHeight * 2,
            Margin = new Thickness(2.0, TimelineLayout.RulerHeight, 0.0, 0.0),
            IsVisible = false
        };
        _timelineScroll.ValueChanged += delegate
        {
            if (_timelineScrollSyncing) return;
            _timeline.ScrollOffset = _timelineScroll.Value;
        };
        _timeline.ScrollChanged += SyncTimelineScrollBar;
        _timeline.PreferredHeightChanged += FitTimelinePanel;
    }

    /// <summary>Shows the scroll bar only when the timebar has more rows than it displays.</summary>
    private void SyncTimelineScrollBar()
    {
        if (_timelineScroll == null) return;
        _timelineScrollSyncing = true;
        double max = _timeline.ScrollMaximum;
        _timelineScroll.IsVisible = max > 0.5;
        _timelineScroll.Maximum = max;
        _timelineScroll.ViewportSize = _timeline.ViewportHeight;
        _timelineScroll.Value = _timeline.ScrollOffset;
        _timelineScrollSyncing = false;
    }

    /// <summary>
    /// Sizes the Timeline panel to its toolbar and the timebar's preferred height (up to
    /// "Displayed stacks" rows), so the panel follows the stack count instead of a fixed height.
    /// </summary>
    private void FitTimelinePanel()
    {
        if (_fittingTimeline || _timelineToolbar == null) return;
        FloatingLayout? layout = ActiveLayout;
        if (layout?.Panel(PanelKeys.Timeline) is not { Collapsed: false } state) return;
        _fittingTimeline = true;
        try
        {
            double inner = Math.Max(10.0, state.Width - 2.0 - 16.0);
            _timelineToolbar.Measure(new Size(inner, double.PositiveInfinity));
            double height = FloatingLayout.HeaderHeight + 2.0 + TimelineTopMargin
                + _timelineToolbar.DesiredSize.Height + _timeline.PreferredHeight + RangeBarGap + _rangeBar.Height + 4.0;
            if (Math.Abs(height - state.Height) > 0.5)
            {
                layout.Resize(PanelKeys.Timeline, state.Width, height);
                PlacePanels();
            }
        }
        finally
        {
            _fittingTimeline = false;
        }
    }

    private Button? _stackerKey;

    private Button? _stackerDelete;

    private Button? _stackerSelect;

    private Button? _stackerPrevious;

    private Button? _stackerNext;

    private ToggleButton? _stackerLoop;

    private Button? _cycleStepButton;

    private Button? _cycleSideButton;

    private bool _stackerSyncing;

    /// <summary>
    /// The STACKER: key
    /// creation and deletion in the current stack, its loop toggle and a stack selector. The two
    /// ghost buttons are left out, since SloppyAnimatsu draws no ghosts.
    /// </summary>
    private Control StackerBody()
    {
        _stackerKey ??= MixerButton("Key", "Create a key at the playhead in the current stack (K)", () => CreateKey());
        _stackerDelete ??= MixerButton("Delete", "Delete the selected keys (Del)", () => DeleteKeys(closeGaps: false));
        _stackerSelect ??= MixerButton("All keys", "Select every key of the current stack (Ctrl+A in the timebar)", SelectAllStackKeys);
        _stackerPrevious ??= MixerButton("◀ Stack", "Make the stack above the current one current", () => StepActiveStack(-1));
        _stackerNext ??= MixerButton("Stack ▶", "Make the stack below the current one current", () => StepActiveStack(1));
        if (_stackerLoop == null)
        {
            _stackerLoop = new ToggleButton
            {
                Content = "Loop",
                FontSize = 9.0,
                Height = 18.0,
                MinHeight = 0.0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1.0),
                Padding = new Thickness(1.0, 0.0)
            };
            ToolTip.SetTip(_stackerLoop, "Loop the current stack (its cycle flag)");
            _stackerLoop.IsCheckedChanged += delegate
            {
                if (_stackerSyncing) return;
                AnimStack? stack = _timeline.ActiveStack;
                if (stack != null && stack.IsCycle != (_stackerLoop.IsChecked == true))
                    RunBankEdit(session => session.ToggleStackFlag(stack, 1u));
            };
        }
        foreach (Control control in new Control[] { _stackerKey, _stackerDelete, _stackerSelect, _stackerPrevious, _stackerNext, _stackerLoop })
            (control.Parent as Panel)?.Children.Remove(control);
        UniformGrid grid = new UniformGrid
        {
            Columns = 3,
            Margin = new Thickness(3.0, 2.0),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _stackerKey, _stackerDelete, _stackerLoop, _stackerPrevious, _stackerNext, _stackerSelect }
        };
        RefreshAnimationPanels();
        return grid;
    }

    /// <summary>
    /// The CYCLEMAKER: its two one-shot
    /// buttons, the same commands as the Animation menu's Cycle Step and Cycle Side.
    /// </summary>
    private Control CyclemakerBody()
    {
        _cycleStepButton ??= MixerButton("Cycle Step", "Generate keyframes for a second step based on existing keys", () => RunMenuCommand(MenuCommand.CycleStep, null));
        _cycleSideButton ??= MixerButton("Cycle Side", "Mirror selected joints/controllers to the opposite side for the other step of your cycle", () => RunMenuCommand(MenuCommand.CycleSide, null));
        foreach (Control control in new Control[] { _cycleStepButton, _cycleSideButton })
            (control.Parent as Panel)?.Children.Remove(control);
        UniformGrid grid = new UniformGrid
        {
            Columns = 2,
            Margin = new Thickness(3.0, 2.0),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _cycleStepButton, _cycleSideButton }
        };
        RefreshAnimationPanels();
        return grid;
    }

    /// <summary>Enables the Stacker's and the Cyclemaker's buttons for the current stack, keys and selection.</summary>
    private void RefreshAnimationPanels()
    {
        AnimStack? stack = _timeline.ActiveStack;
        IReadOnlyList<AnimStack> stacks = _timeline.Animation?.Stacks ?? (IReadOnlyList<AnimStack>)Array.Empty<AnimStack>();
        int index = stack == null ? -1 : stacks.ToList().IndexOf(stack);
        if (_stackerKey != null) _stackerKey.IsEnabled = stack != null;
        if (_stackerDelete != null) _stackerDelete.IsEnabled = stack != null && _timeline.SelectedKeys.Count > 0;
        if (_stackerSelect != null) _stackerSelect.IsEnabled = stack != null && stack.Keys.Count > 0;
        if (_stackerPrevious != null) _stackerPrevious.IsEnabled = index > 0;
        if (_stackerNext != null) _stackerNext.IsEnabled = index >= 0 && index < stacks.Count - 1;
        if (_stackerLoop != null)
        {
            _stackerSyncing = true;
            _stackerLoop.IsEnabled = stack != null;
            _stackerLoop.IsChecked = stack?.IsCycle ?? false;
            _stackerSyncing = false;
        }
        if (_cycleStepButton != null || _cycleSideButton != null)
        {
            MenuState state = CurrentMenuState();
            if (_cycleStepButton != null) _cycleStepButton.IsEnabled = state.CanCycleStep;
            if (_cycleSideButton != null) _cycleSideButton.IsEnabled = state.CanCycleSide && state.SelectionCount > 0;
        }
    }

    private void SelectAllStackKeys()
    {
        if (_timeline.ActiveStack is { } stack)
            _timeline.Select(stack, Enumerable.Range(0, stack.Keys.Count));
    }

    /// <summary>Makes the stack <paramref name="step"/> rows away the current one, with no key selected.</summary>
    private void StepActiveStack(int step)
    {
        IReadOnlyList<AnimStack> stacks = _timeline.Animation?.Stacks ?? (IReadOnlyList<AnimStack>)Array.Empty<AnimStack>();
        int index = _timeline.ActiveStack == null ? -1 : stacks.ToList().IndexOf(_timeline.ActiveStack);
        int target = index + step;
        if (index < 0 || target < 0 || target >= stacks.Count) return;
        _timeline.Select(stacks[target], Array.Empty<int>());
    }
}
