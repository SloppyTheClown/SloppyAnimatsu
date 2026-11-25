// The Mixer window: the stack list
// with its eye/type/name/hammer/weight-bar rows, the six icon-only toolbar buttons (New, Copy,
// Merge, Extract, Audio, Delete), the Blend (Override/Additive) dropdown and the selection written
// to the animation's __SelectedStackList.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core.Audio;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    // The Mixer's controls live for the window's lifetime: ApplyDockLayout may rebuild the panel
    // bodies (one per context), and the list and buttons are detached and re-parented like the
    // bank's (MainWindow.Panels.cs SharedControls/ReleaseDockBodies).
    private readonly ListBox _mixerList = new ListBox
    {
        SelectionMode = SelectionMode.Multiple,
        Margin = new Thickness(2.0),
        ItemsPanel = new FuncTemplate<Panel>(() => new StackPanel())
    };
    // The texture layer's row: texture keys belong
    // to the animation, not to a stack, so it is a fixed line under the stacks, not a stack row.
    private readonly TextBlock _mixerTextures = new TextBlock
    {
        FontSize = 10.0,
        Margin = new Thickness(6.0, 1.0, 4.0, 2.0),
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsVisible = false
    };
    private Button _mixerNew = null!;
    private Button _mixerDuplicate = null!;
    private Button _mixerMerge = null!;
    private Button _mixerExtract = null!;
    private Button _mixerAudio = null!;
    private Button _mixerDelete = null!;
    private List<MixerItem> _mixerItems = [];
    private bool _mixerReady;

    /// <summary>Creates the Mixer's buttons and template once; the dock re-parents them on every relayout.</summary>
    private void EnsureMixerControls()
    {
        if (_mixerReady) return;
        _mixerReady = true;
        _mixerNew = MixerButton("New", MixerNewIcon(), "Create a stack from the selected objects", NewStack);
        _mixerDuplicate = MixerButton("Copy", MixerCopyIcon(), "Duplicate the selected stacks", DuplicateSelectedStacks);
        _mixerMerge = MixerButton("Merge", MixerMergeIcon(), "Bake the selected stacks into one (select two or more)", () => MergeSelectedStacks());
        _mixerExtract = MixerButton("Extract", MixerExtractIcon(), "Move the selected objects' channels into a new stack", () => ExtractSelectedChannels());
        _mixerAudio = MixerButton("Audio…", MixerAudioIcon(), "Add a sound file as a new audio stack", AddAudioStackFromFile);
        _mixerDelete = MixerButton("Delete", MixerDeleteIcon(), "Delete the selected stacks", DeleteSelectedStacks);
        _mixerList.SelectionChanged += delegate
        {
            MixerSelectionChanged();
        };
        // A row is picked by clicking it, and its opacity cell is the only thing that takes the
        // keyboard: a focusable row would steal the focus the inline field asks for.
        _mixerList.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters =
            {
                new Avalonia.Styling.Setter(InputElement.FocusableProperty, false),
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0)
            }
        });
        _mixerList.ItemTemplate = new FuncDataTemplate<MixerItem>((item, _) => MixerRowView(item), supportsRecycling: false);
        // A right-click on the list item's padding, outside the row grid, still opens that row's menu.
        _mixerList.ContextRequested += delegate(object? _, ContextRequestedEventArgs e)
        {
            if (e.Handled) return;
            ListBoxItem? container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            Grid? row = container?.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.ContextMenu != null);
            if (row?.ContextMenu == null) return;
            row.ContextMenu.Open(row);
            e.Handled = true;
        };
    }

    /// <summary>One row of the Mixer: the stack and what the row shows.</summary>
    private sealed record MixerItem(AnimStack Stack, string Label, bool Enabled, bool Audio, float Opacity);

    // Mixer toolbar icons, 12 px: the subject in gray, a yellow plus on the buttons that add a new
    // stack (New, Audio), a red cross on the one that deletes. Copy and Extract stay all gray: their
    // own shape already says what they do. The plus/cross sits in the bottom-right quarter.
    private static readonly IBrush MixerIconGray = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8));
    private static readonly IBrush MixerIconYellow = new SolidColorBrush(Color.FromRgb(0xF0, 0xC8, 0x30));
    private static readonly IBrush MixerIconRed = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x40));

    private static Canvas MixerIcon(params Control[] parts)
    {
        var canvas = new Canvas { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts) canvas.Children.Add(part);
        return canvas;
    }

    private static Avalonia.Controls.Shapes.Path MixerFill(string data, IBrush brush) =>
        new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Fill = brush };

    private static Avalonia.Controls.Shapes.Path MixerStroke(string data, IBrush brush, double thickness = 1.0) =>
        new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = thickness };

    /// <summary>The yellow plus, centred at (9,9).</summary>
    private static Control MixerPlus() => MixerStroke("M9,6 V12 M6,9 H12", MixerIconYellow, 2.0);

    /// <summary>The red cross, centred at (9,9).</summary>
    private static Control MixerCross() => MixerStroke("M6.5,6.5 L11.5,11.5 M11.5,6.5 L6.5,11.5", MixerIconRed, 2.0);

    /// <summary>One stack layer (a bar) and a plus.</summary>
    private static Control MixerNewIcon() => MixerIcon(MixerFill("M0.5,2 H8.5 V5.5 H0.5 Z", MixerIconGray), MixerPlus());

    /// <summary>Two overlapping layers: an outline behind, a filled copy in front.</summary>
    private static Control MixerCopyIcon() => MixerIcon(
        MixerStroke("M0.5,1.5 H8 V6.5 H0.5 Z", MixerIconGray),
        MixerFill("M3.5,5 H11.5 V10.5 H3.5 Z", MixerIconGray));

    /// <summary>Two layers at the top joining into one at the bottom.</summary>
    private static Control MixerMergeIcon() => MixerIcon(
        MixerFill("M0.5,0.5 H5 V3 H0.5 Z M7,0.5 H11.5 V3 H7 Z", MixerIconGray),
        MixerStroke("M2.75,3 L6,7.5 L9.25,3", MixerIconGray),
        MixerFill("M1.5,8 H10.5 V11.5 H1.5 Z", MixerIconGray));

    /// <summary>A layer with an arrow pulling down out of it into a smaller one.</summary>
    private static Control MixerExtractIcon() => MixerIcon(
        MixerFill("M0.5,0.5 H11.5 V3.5 H0.5 Z", MixerIconGray),
        MixerStroke("M6,3.5 V7", MixerIconGray, 1.2),
        MixerFill("M3.8,6.3 H8.2 L6,8.6 Z", MixerIconGray),
        MixerStroke("M3,9.5 H9 V11.5 H3 Z", MixerIconGray));

    /// <summary>A speaker with one sound wave, and a plus.</summary>
    private static Control MixerAudioIcon() => MixerIcon(
        MixerFill("M0.5,3 H2.5 L5.5,0.5 V8.5 L2.5,6 H0.5 Z", MixerIconGray),
        MixerStroke("M7,2 Q8.8,4.5 7,7", MixerIconGray),
        MixerPlus());

    /// <summary>One stack layer and a red cross.</summary>
    private static Control MixerDeleteIcon() => MixerIcon(MixerFill("M0.5,2 H8.5 V5.5 H0.5 Z", MixerIconGray), MixerCross());

    private static Button MixerButton(string text, Control icon, string tip, Action action)
    {
        // Icon only: the name leads the tooltip and is the button's accessible name.
        Button button = MixerButton(text, text.TrimEnd('…') + ": " + tip, action);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        button.Content = icon;
        button.Height = 20.0;
        Avalonia.Automation.AutomationProperties.SetName(button, text.TrimEnd('…'));
        // The icon's paths carry their own colours, which the theme's disabled look does not
        // touch, so fade the icon itself while the button is disabled.
        button.PropertyChanged += delegate(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == InputElement.IsEffectivelyEnabledProperty)
                icon.Opacity = button.IsEffectivelyEnabled ? 1.0 : 0.3;
        };
        icon.Opacity = button.IsEffectivelyEnabled ? 1.0 : 0.3;
        return button;
    }

    /// <summary>A small text button in the Mixer's style (also used by other panels' toolbars).</summary>
    private static Button MixerButton(string text, string tip, Action action)
    {
        Button button = new Button
        {
            Content = text,
            FontSize = 9.0,
            Height = 18.0,
            MinHeight = 0.0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(1.0),
            Padding = new Thickness(1.0, 0.0)
        };
        ToolTip.SetTip(button, tip);
        button.Click += delegate
        {
            action();
        };
        return button;
    }

    /// <summary>One Mixer row: the enabled eye, the stack-type glyph, the name and the opacity.</summary>
    private Control MixerRowView(MixerItem? item)
    {
        // A recycled row's content is cleared before it is rebuilt, which builds the template with
        // a null item (the same guard as BankRow and PickerRowView).
        if (item == null) return new TextBlock();
        Button eye = new Button
        {
            Content = item.Enabled ? "◉" : "○",
            Width = 20.0,
            Height = 18.0,
            Padding = new Thickness(0.0),
            MinWidth = 0.0,
            FontSize = 11.0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(eye, item.Enabled ? "Enabled: click to turn the stack off" : "Disabled: click to turn the stack on");
        eye.Click += delegate
        {
            if (_syncing) return;
            ToggleMixerStack(item.Stack, !item.Stack.IsEnabled);
        };
        TextBlock kind = new TextBlock
        {
            Text = item.Audio ? "♪" : "↗",
            FontSize = 12.0,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(kind, item.Audio ? "Audio stack" : "Pose stack");
        var (name, startRename) = MixerNameEditor(item.Stack, item.Label);
        var opacity = MixerOpacityEditor(item.Stack, item.Opacity);
        // The hammer next to the layer name: edit this layer's joints in the Picker.
        bool layerMode = _pickerLayerMode && ReferenceEquals(PickerLayer, item.Stack);
        Button hammer = new Button
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
            Opacity = layerMode ? 0.95 : 0.35
        };
        ToolTip.SetTip(hammer, layerMode
            ? "The Picker is editing this layer's joints: click to leave"
            : "Edit this layer's joints in the Picker (list every controller and add or remove it)");
        hammer.Click += delegate
        {
            if (_syncing) return;
            if (layerMode) SetPickerLayerMode(false);
            else EditLayerJoints(item.Stack);
        };
        Grid row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("22,18,*,18,58"),
            Margin = new Thickness(2.0, 1.0),
            // A transparent background makes the row's empty space hit-testable, so a right-click
            // there still opens the row's menu (a Grid without one only takes clicks on its children).
            Background = Brushes.Transparent
        };
        Grid.SetColumn(eye, 0);
        row.Children.Add(eye);
        Grid.SetColumn(kind, 1);
        row.Children.Add(kind);
        Grid.SetColumn(name, 2);
        row.Children.Add(name);
        Grid.SetColumn(hammer, 3);
        row.Children.Add(hammer);
        Grid.SetColumn(opacity, 4);
        row.Children.Add(opacity);
        row.ContextMenu = MixerRowMenu(item, startRename);
        return row;
    }

    /// <summary>
    /// A row's right-click menu: the blend mode (pose stacks only), whether the stack is on, and
    /// Rename, which opens the row's name field. It acts on the clicked row's stack.
    /// </summary>
    private ContextMenu MixerRowMenu(MixerItem item, Action startRename)
    {
        AnimStack stack = item.Stack;
        var menu = new ContextMenu();
        menu.Opening += delegate
        {
            var items = new List<Control>();
            if (!item.Audio)
            {
                foreach (bool additive in new[] { false, true })
                {
                    var blend = new MenuItem
                    {
                        Header = additive ? "Additive" : "Override",
                        ToggleType = MenuItemToggleType.Radio,
                        GroupName = "mixerBlend",
                        IsChecked = stack.IsAdditive == additive
                    };
                    ToolTip.SetTip(blend, additive
                        ? "Add this stack's motion on top of the stacks below it"
                        : "Replace the motion of the stacks below it on the joints this stack keys");
                    blend.Click += delegate
                    {
                        if (stack.IsAdditive != additive)
                            RunBankEdit(delegate(PoseEditSession session) { session.SetStackAdditive(stack, additive); });
                    };
                    items.Add(blend);
                }
                items.Add(new Separator());
            }
            var enabled = new MenuItem
            {
                Header = "Enabled",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = stack.IsEnabled
            };
            enabled.Click += delegate { ToggleMixerStack(stack, !stack.IsEnabled); };
            items.Add(enabled);
            items.Add(new Separator());
            var rename = new MenuItem { Header = "_Rename" };
            rename.Click += delegate { startRename(); };
            items.Add(rename);
            menu.ItemsSource = items;
        };
        return menu;
    }

    /// <summary>
    /// The row's name: a plain click on the text of the row that is already the one selected row
    /// turns it into a field (Enter, Tab or a click anywhere else renames the stack as one undo
    /// step, Escape cancels). The first click on a row only selects it, and Ctrl/Shift clicks only
    /// select rows. Returns the cell and the action that opens the field (the context menu's Rename).
    /// </summary>
    private (Control Cell, Action StartRename) MixerNameEditor(AnimStack stack, string label)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11.0,
            // Sized to the text, so only a click on the name itself starts a rename.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Ibeam)
        };
        ToolTip.SetTip(text, "Click to rename");
        var field = new TextBox
        {
            FontSize = 11.0,
            Height = 18.0,
            MinHeight = 0.0,
            Padding = new Thickness(2.0, 0.0),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        var cell = new Grid { Children = { text, field } };
        bool focused = false, committing = false;
        Action? stopWatching = null;

        void Show()
        {
            if (_syncing || field.IsVisible) return;
            field.Text = stack.Name ?? "";
            text.IsVisible = false;
            field.IsVisible = true;
            stopWatching = CloseOnOutsidePress(field, () => Finish(commit: true));
            // The list item takes the focus as the click finishes, so focus the field after it.
            Dispatcher.UIThread.Post(delegate
            {
                if (!field.IsVisible) return;
                field.SelectAll();
                field.Focus();
            }, DispatcherPriority.Input);
        }
        void Finish(bool commit)
        {
            if (committing || !field.IsVisible) return;
            committing = true;
            focused = false;
            stopWatching?.Invoke();
            stopWatching = null;
            field.IsVisible = false;
            text.IsVisible = true;
            string wanted = (field.Text ?? "").Trim();
            if (commit && wanted.Length > 0 && wanted != stack.Name)
                RunBankEdit(delegate(PoseEditSession session) { session.Rename(stack, wanted); });
            committing = false;
        }

        // A click renames only when this row was already the one selected row as the press came in,
        // so the first click on a row just selects it. The press is checked here (the text sees it
        // before the list selects on it), and Tapped then opens the field once the click completes
        // without moving. Only a plain left press arms it: Tapped can also follow a right click,
        // and opening the field there would swallow the row's context menu.
        bool armed = false;
        text.AddHandler(PointerPressedEvent, delegate(object? _, PointerPressedEventArgs e)
        {
            armed = e.GetCurrentPoint(text).Properties.IsLeftButtonPressed
                && e.KeyModifiers == KeyModifiers.None
                && e.ClickCount == 1
                && SelectedStacks() is [var only] && only == stack;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        text.Tapped += delegate
        {
            if (!armed) return;
            armed = false;
            Show();
        };
        field.GotFocus += delegate { focused = true; };
        field.KeyDown += delegate(object? _, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Finish(commit: true);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Finish(commit: false);
                    e.Handled = true;
                    break;
            }
        };
        field.LostFocus += delegate
        {
            if (focused) Finish(commit: true);
        };
        return (cell, Show);
    }

    /// <summary>
    /// Calls <paramref name="onOutside"/> once the next pointer press lands outside
    /// <paramref name="field"/> anywhere in the window. Focus alone cannot close an inline field: a
    /// press on something that takes no focus (the viewport, a panel's background) never moves it.
    /// The call is posted, so the press that closes the field still does its own work first.
    /// Returns the action that stops watching.
    /// </summary>
    private Action CloseOnOutsidePress(Control field, Action onOutside)
    {
        void Handler(object? _, PointerPressedEventArgs e)
        {
            if (e.Source is Visual source && (source == field || field.IsVisualAncestorOf(source))) return;
            Dispatcher.UIThread.Post(onOutside);
        }
        AddHandler(PointerPressedEvent, Handler, RoutingStrategies.Tunnel, handledEventsToo: true);
        return () => RemoveHandler(PointerPressedEvent, Handler);
    }

    /// <summary>Fill of the Mixer rows' weight bar (a muted blue that reads on both row states).</summary>
    private static readonly IBrush MixerWeightFill = new SolidColorBrush(Color.FromArgb(0xB0, 0x4A, 0x78, 0xA8));
    private static readonly IBrush MixerWeightTrack = new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00));

    /// <summary>
    /// The row's weight (opacity) cell: a bar filled to the value with the number on it. Dragging
    /// left or right changes the value relative to where the press started (a full bar width is
    /// 0..1), previewed live and committed as one undo step on release; a double-click turns it
    /// into a field (Enter or leaving it applies, Escape cancels). It edits the row's own stack,
    /// not the current one.
    /// </summary>
    private Control MixerOpacityEditor(AnimStack stack, float opacity)
    {
        var fill = new ColumnDefinition(new GridLength(opacity, GridUnitType.Star));
        var rest = new ColumnDefinition(new GridLength(1.0 - opacity, GridUnitType.Star));
        var bar = new Grid { ColumnDefinitions = { fill, rest } };
        var filled = new Border { Background = MixerWeightFill, CornerRadius = new CornerRadius(2.0) };
        Grid.SetColumn(filled, 0);
        bar.Children.Add(filled);
        var value = new TextBlock
        {
            Text = opacity.ToString("0.00", CultureInfo.InvariantCulture),
            FontSize = 10.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var track = new Border
        {
            Background = MixerWeightTrack,
            CornerRadius = new CornerRadius(2.0),
            Height = 16.0,
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = new Grid { Children = { bar, value } }
        };
        ToolTip.SetTip(track, "Weight: drag left or right to change it, double-click to type a value");
        var field = new TextBox
        {
            Text = opacity.ToString("0.##", CultureInfo.InvariantCulture),
            FontSize = 10.0,
            Height = 18.0,
            MinHeight = 0.0,
            Padding = new Thickness(1.0, 0.0),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        Grid cell = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        cell.Children.Add(track);
        cell.Children.Add(field);

        void ShowValue(float v)
        {
            fill.Width = new GridLength(v, GridUnitType.Star);
            rest.Width = new GridLength(1.0 - v, GridUnitType.Star);
            value.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Drag state: the press point and value, and whether the pointer has moved far enough to
        // count as a drag (a plain click changes nothing).
        bool pressed = false, dragging = false;
        // Field state: a commit in progress, and whether the field has had the focus yet.
        bool applying = false, focused = false;
        Action? stopWatching = null;
        double startX = 0.0;
        float startValue = opacity, dragValue = opacity;

        void EndDrag(bool commit)
        {
            if (!pressed) return;
            pressed = false;
            if (!dragging) return;
            dragging = false;
            // Put the value back first, so the undo step records the change from the old weight.
            Session?.Bank.SetOpacity(stack, startValue);
            if (commit && MathF.Abs(dragValue - startValue) > 0.0001f)
            {
                float final = dragValue;
                RunBankEdit(delegate(PoseEditSession session) { session.SetStackOpacity(stack, final); });
            }
            else
            {
                ShowValue(startValue);
                Apply();
            }
        }

        track.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            if (_syncing || _drag != null || !e.GetCurrentPoint(track).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                EndDrag(commit: false);
                ShowField();
                return;
            }
            pressed = true;
            dragging = false;
            startX = e.GetPosition(track).X;
            startValue = dragValue = stack.Opacity;
            e.Pointer.Capture(track);
        };
        track.PointerMoved += delegate(object? _, PointerEventArgs e)
        {
            if (!pressed) return;
            double dx = e.GetPosition(track).X - startX;
            if (!dragging && Math.Abs(dx) < 3.0) return;
            dragging = true;
            double width = Math.Max(track.Bounds.Width, 1.0);
            dragValue = (float)Math.Clamp(startValue + dx / width, 0.0, 1.0);
            dragValue = MathF.Round(dragValue * 100f) / 100f;
            ShowValue(dragValue);
            // Live preview without an undo step; the release commits one.
            Session?.Bank.SetOpacity(stack, dragValue);
            Apply();
            e.Handled = true;
        };
        track.PointerReleased += delegate(object? _, PointerReleasedEventArgs e)
        {
            if (!pressed) return;
            e.Pointer.Capture(null);
            EndDrag(commit: true);
            e.Handled = true;
        };
        track.PointerCaptureLost += delegate { EndDrag(commit: true); };

        void ShowField()
        {
            if (applying || field.IsVisible) return;
            field.Text = stack.Opacity.ToString("0.##", CultureInfo.InvariantCulture);
            track.IsVisible = false;
            field.IsVisible = true;
            stopWatching = CloseOnOutsidePress(field, () => CommitField(commit: true));
            // The row takes the focus as the click finishes, so the field's own focus comes after it.
            Dispatcher.UIThread.Post(delegate
            {
                if (!field.IsVisible) return;
                field.SelectAll();
                field.Focus();
            }, DispatcherPriority.Input);
        }
        void HideField()
        {
            focused = false;
            stopWatching?.Invoke();
            stopWatching = null;
            field.IsVisible = false;
            track.IsVisible = true;
        }
        void CommitField(bool commit)
        {
            if (applying || !field.IsVisible) return;
            if (commit
                && float.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float typed))
            {
                applying = true;
                var current = _mixerItems.FirstOrDefault(i => i.Stack == stack)?.Opacity ?? stack.Opacity;
                float wanted = Math.Clamp(typed, 0f, 1f);
                if (MathF.Abs(wanted - current) > 0.0001f)
                    RunBankEdit(delegate(PoseEditSession session)
                    {
                        session.SetStackOpacity(stack, wanted);
                    });
                applying = false;
            }
            HideField();
        }
        field.GotFocus += delegate
        {
            focused = true;
        };
        field.KeyDown += delegate(object? _, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    CommitField(commit: true);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CommitField(commit: false);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    CommitField(commit: true);
                    break;
            }
        };
        field.LostFocus += delegate
        {
            if (focused) CommitField(commit: true);
        };
        return cell;
    }

    /// <summary>Re-reads the stack rows and their selection after any edit or animation change.</summary>
    private void RefreshMixer()
    {
        if (_mixerList == null) return;
        Animation? animation = _animation;
        var rows = animation == null
            ? []
            : animation.Stacks
                .Select(s => new MixerItem(s, s.Name ?? "(unnamed)", s.IsEnabled, s is AudioStack, s.Opacity))
                .ToList();
        var textured = animation == null ? [] : Material.Keyed(animation);
        _mixerTextures.IsVisible = textured.Count > 0;
        _mixerTextures.Text = textured.Count == 0 ? "" :
            $"Textures layer: {textured.Sum(m => m.TextureKeys(animation!).Count)} keys on {string.Join(", ", textured.Select(m => m.Name))}";
        ToolTip.SetTip(_mixerTextures, "Texture keys of this animation, shared by every stack; edit them on the timeline's TX rows or in the Material panel.");
        _syncing = true;
        if (!_mixerItems.SequenceEqual(rows))
        {
            _mixerItems = rows;
            _mixerList.ItemsSource = rows;
        }

        List<AnimStack> selected = (animation?.SelectedStacks ?? []).ToList();
        if (selected.Count == 0 && _timeline.ActiveStack is { } active && animation != null && active.Animation == animation)
            selected = [active];
        _mixerList.SelectedItems?.Clear();
        foreach (var row in rows)
            if (selected.Contains(row.Stack)) _mixerList.SelectedItems?.Add(row);
        _syncing = false;
        UpdateMixerButtons();
    }

    /// <summary>Enables the toolbar for the current selection; Merge needs at least two stacks.</summary>
    private void UpdateMixerButtons()
    {
        if (!_mixerReady) return;
        bool hasAnimation = _animation != null;
        int count = SelectedStacks().Count;
        DisabledHint.SetEnabled(_mixerNew, hasAnimation, "an animation");
        DisabledHint.SetEnabled(_mixerAudio, hasAnimation, "an animation");
        DisabledHint.SetEnabled(_mixerDuplicate, count > 0, "selected stacks in the Mixer");
        DisabledHint.SetEnabled(_mixerDelete, count > 0, "selected stacks in the Mixer");
        DisabledHint.SetEnabled(_mixerMerge, count > 1, "two or more selected stacks in the Mixer");
        DisabledHint.SetEnabled(_mixerExtract, count > 0, "selected stacks in the Mixer");
    }

    /// <summary>The stacks the Mixer's actions work on: the selected rows, else the timeline's row.</summary>
    private List<AnimStack> SelectedStacks()
    {
        if (_animation == null) return [];
        var chosen = _mixerList?.SelectedItems?.OfType<MixerItem>().Select(i => i.Stack).ToList() ?? [];
        if (chosen.Count == 0 && _timeline.ActiveStack is { } active && active.Animation == _animation) chosen.Add(active);
        return chosen;
    }

    private void MixerSelectionChanged()
    {
        if (_syncing || _animation == null || _timeline.IsDragging) return;
        var stacks = _mixerList.SelectedItems?.OfType<MixerItem>().Select(i => i.Stack).ToList() ?? [];
        _animation.SelectStacks(stacks);
        if (stacks.Count == 1)
        {
            Session?.SetCurrentStack(stacks[0]);
            _timeline.Select(stacks[0], Array.Empty<int>());
        }
        else
        {
            UpdateKeyCommands();
        }
        UpdateMixerButtons();
    }

    private void ToggleMixerStack(AnimStack stack, bool enabled)
    {
        if ((stack.IsEnabled) == enabled) return;
        RunBankEdit(delegate(PoseEditSession session)
        {
            session.ToggleStackFlag(stack, AnimationBankEditor.EnabledFlag);
        });
    }

    private void DuplicateSelectedStacks()
    {
        Animation? animation = _animation;
        var stacks = SelectedStacks();
        PoseEditSession? session = Session;
        if (animation == null || session == null || stacks.Count == 0) return;
        var copies = new List<AnimStack>();
        RunBankEdit(delegate(PoseEditSession s)
        {
            foreach (var stack in stacks) copies.Add(s.DuplicateStack(stack));
        }, () => animation);
        if (copies.Count > 0)
        {
            animation.SelectStacks(copies);
            _timeline.Select(copies[^1], Array.Empty<int>());
        }
    }

    private void DeleteSelectedStacks()
    {
        var stacks = SelectedStacks();
        if (stacks.Count == 0) return;
        int deleted = 0;
        RunBankEdit(delegate(PoseEditSession s)
        {
            foreach (var stack in stacks) if (s.DeleteStack(stack)) deleted++;
        });
        if (deleted == 0) _status.Text = "An animation keeps at least one stack.";
        else _timeline.Select(_animation?.CurrentStack, Array.Empty<int>());
    }

    /// <summary>
    /// The Mixer's Merge button and the <c>--bank merge</c> scripted action. Returns whether a stack
    /// was baked, so a scripted run stops instead of saving a scene the merge never touched.
    /// </summary>
    private bool MergeSelectedStacks()
    {
        Animation? animation = _animation;
        var stacks = SelectedStacks();
        PoseEditSession? session = Session;
        if (animation == null || session == null || stacks.Count == 0) return false;
        AnimStack? merged = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            merged = s.MergeStacks(animation, stacks);
        });
        if (merged == null) return false;
        _timeline.Select(merged, Array.Empty<int>());
        _status.Text = $"Merged {stacks.Count} stack(s) into '{merged.Name}'.";
        return true;
    }

    private bool ExtractSelectedChannels()
    {
        AnimStack? stack = _timeline.ActiveStack ?? SelectedStacks().FirstOrDefault();
        PoseEditSession? session = Session;
        if (stack == null || session == null) return false;
        var targets = _selectedAll.Where(o => stack.TrackFor(o) != null).ToList();
        if (targets.Count == 0)
        {
            _status.Text = "Select the objects whose tracks to extract: their channels move into the new stack.";
            return false;
        }
        var tracks = stack.Tracks.Where(t => t.Target != null && targets.Contains(t.Target)).ToList();
        AnimChannels channels = 0;
        foreach (var track in tracks) channels |= track.Channels;
        AnimStack? created = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            created = s.ExtractChannels(stack, channels, tracks);
        });
        if (created == null) return false;
        _timeline.Select(created, Array.Empty<int>());
        _status.Text = $"Extracted {tracks.Count} track(s) into '{created.Name}'.";
        return true;
    }

    private async void AddAudioStackFromFile()
    {
        Animation? animation = _animation;
        if (animation == null || Session == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add an audio stack",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sound") { Patterns = ["*.wav"] }]
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) AddAudioStack(path);
    }

    /// <summary>
    /// Adds <paramref name="path"/> as an audio stack (the Audio button and <c>--bank audio:&lt;file&gt;</c>).
    /// Returns false when the file cannot be read or is not a PCM WAV, after saying why in the status line.
    /// </summary>
    private bool AddAudioStack(string path)
    {
        Animation? animation = _animation;
        PoseEditSession? session = Session;
        if (animation == null || session == null) return false;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Could not read " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
        WaveFile? sound = WaveFile.TryDecode(bytes);
        if (sound == null)
        {
            _status.Text = "That file is not a PCM WAV sound.";
            return false;
        }
        AudioStack? created = null;
        RunBankEdit(delegate(PoseEditSession s)
        {
            created = s.AddAudioStack(animation, path, bytes, sound.Duration);
        });
        if (created == null) return false;
        _timeline.Select(created, Array.Empty<int>());
        _status.Text = $"Added '{Path.GetFileName(path)}' as audio stack '{created.Name}' ({sound.Duration:0.###} s).";
        return true;
    }
}
