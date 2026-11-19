// The floating dock: applying a saved FloatingLayout, releasing and re-homing panel bodies, the
// drag and resize gestures, and persisting a panel state or size back into the preferences.
//
// Panel bodies are shared controls: the same instance moves between the fixed layout and the
// floating canvas, so every body is released through ReleaseDockBodies/SharedControls instead of
// being rebuilt. A body must not assume a parent.
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
    private void ApplyDockLayout()
    {
        if (_floatHost == null)
        {
            return;
        }
        if (_curvePanel == null)
        {
            _curvePanel = CurvePanel();
        }
        EnsureMixerControls();
        ReleaseDockBodies();
        _panelViews.Clear();
        _floatHost.Children.Clear();
        AddOverlays();
        _panelsContext = null;
        UpdateCurveVisibility();
        if (_floatHost.Bounds.Width < 2.0 || _floatHost.Bounds.Height < 2.0)
        {
            return;
        }
        FloatingLayout floatingLayout = LayoutFor(ContextLabel);
        // A layout kept from before a window resize is placed for the size the host has now.
        floatingLayout.Arrange((int)Math.Round(_floatHost.Bounds.Width), (int)Math.Round(_floatHost.Bounds.Height));
        foreach (FloatingPanelState panel in floatingLayout.Panels)
        {
            AddPanel(panel);
        }
        _panelsContext = ContextLabel;
        PlacePanels();
        RaiseOverlays();
        RefreshMixer();
    }

    private FloatingLayout LayoutFor(string context)
    {
        if (_floating.TryGetValue(context, out FloatingLayout value))
        {
            return value;
        }
        DockLayout dock = ((_preferences != null) ? DockLayout.For(_preferences, context) : DockLayout.Empty(context));
        int width = (int)Math.Round(_floatHost?.Bounds.Width ?? base.Width);
        int height = (int)Math.Round(_floatHost?.Bounds.Height ?? base.Height);
        // A profile that places no panel at all gets the default arrangement instead of
        // every panel piled at the same spot.
        value = (dock.Columns.Count == 0 && dock.Floating.Count == 0)
            ? FloatingLayout.FromPreset(LayoutPreset.Default, context, PanelCatalogue.For(context), width, height, dock)
            : FloatingLayout.FromDock(dock, PanelCatalogue.For(context), width, height);
        _floating[context] = value;
        return value;
    }

    /// <summary>
    /// Replaces both contexts' panel layouts with a preset (the Layout button), stores them in
    /// the preferences and rebuilds the panels.
    /// </summary>
    private void ResetLayout(LayoutPreset preset)
    {
        int width = (int)Math.Round(_floatHost?.Bounds.Width ?? base.Width);
        int height = (int)Math.Round(_floatHost?.Bounds.Height ?? base.Height);
        string text = "Panel layout: " + LayoutPresets.Name(preset) + ".";
        foreach (string context in new[] { "Rigging", "Animation" })
        {
            DockLayout? dock = (_preferences != null) ? DockLayout.For(_preferences, context) : null;
            FloatingLayout layout = FloatingLayout.FromPreset(preset, context, PanelCatalogue.For(context), width, height, dock);
            if (dock != null)
            {
                layout.WriteTo(dock);
            }
            _floating[context] = layout;
        }
        if (_preferences != null)
        {
            try
            {
                _preferences.Save();
            }
            catch (Exception ex)
            {
                text = text + " Could not save the panel layout: " + ex.Message;
            }
        }
        ApplyDockLayout();
        _status.Text = text;
    }

    private void FloatingHostSized()
    {
        if (_floatHost == null || _floatHost.Bounds.Width < 2.0 || _floatHost.Bounds.Height < 2.0)
        {
            return;
        }
        if (_panelViews.Count == 0 || _panelsContext != ContextLabel)
        {
            ApplyDockLayout();
            return;
        }
        FloatingLayout activeLayout = ActiveLayout;
        if (activeLayout != null)
        {
            activeLayout.Arrange((int)Math.Round(_floatHost.Bounds.Width), (int)Math.Round(_floatHost.Bounds.Height));
            PlacePanels();
        }
    }

    private void AddPanel(FloatingPanelState state)
    {
        Control control = PanelBody(state.Key);
        control.IsVisible = !state.Collapsed;
        TextBlock textBlock = new TextBlock
        {
            Text = (state.Collapsed ? "▶" : "▼"),
            FontSize = 10.0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Button button = new Button
        {
            Content = textBlock,
            Width = 18.0,
            Height = 18.0,
            Padding = new Thickness(0.0),
            Margin = new Thickness(2.0, 0.0, 0.0, 0.0),
            MinWidth = 0.0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        ToolTip.SetTip(button, state.Collapsed ? "Expand" : "Collapse");
        button.Click += delegate
        {
            FloatingLayout activeLayout = ActiveLayout;
            if (activeLayout != null)
            {
                activeLayout.Toggle(state.Key);
                PlacePanels();
                PersistPanelState(state);
            }
        };
        TextBlock textBlock2 = new TextBlock
        {
            Text = state.Title.ToUpperInvariant(),
            FontSize = 10.0,
            FontWeight = FontWeight.DemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4.0, 0.0, 4.0, 0.0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Height = 20.0
        };
        grid.Children.Add(button);
        if (state.Key == PanelKeys.Bank)
        {
            _bankTitle = textBlock2;
            textBlock2.Text = CharBankShown ? "CHAR BANK" : "ANIM BANK";
        }
        Grid.SetColumn(textBlock2, 1);
        grid.Children.Add(textBlock2);
        string pageClass = PreferenceClassForPanel(state.Title);
        if (pageClass != null)
        {
            Button button2 = HeaderButton("⚙", "Related settings: open this panel's preferences page");
            button2.Click += delegate
            {
                OpenPreferencesDialog(pageClass);
            };
            Grid.SetColumn(button2, 2);
            grid.Children.Add(button2);
        }
        Button button3 = HeaderButton("?", "What this panel is");
        button3.Click += delegate
        {
            _status.Text = state.Title + ": drag the title bar to move the panel, the arrow folds and unfolds it.";
        };
        Grid.SetColumn(button3, 3);
        grid.Children.Add(button3);
        Border headerBorder = new Border
        {
            Background = PanelHeaderBackground,
            Child = grid
        };
        Grid grid2 = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        grid2.Children.Add(headerBorder);
        Grid.SetRow(control, 1);
        grid2.Children.Add(control);
        Border grip = new Border
        {
            Width = 26.0,
            Height = 7.0,
            CornerRadius = new CornerRadius(3.0),
            Background = new SolidColorBrush(Color.FromArgb(110, 230, 230, 235)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
            IsVisible = !state.Collapsed,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth)
        };
        ToolTip.SetTip(grip, "Drag to resize");
        Grid grid3 = new Grid();
        grid3.Children.Add(grid2);
        grid3.Children.Add(grip);
        Border frame = new Border
        {
            Background = PanelBackground,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(3.0),
            ClipToBounds = true,
            BoxShadow = PanelShadow,
            Child = grid3
        };
        frame.AddHandler(InputElement.PointerPressedEvent, delegate
        {
            ActiveLayout?.BringToFront(state.Key);
            frame.ZIndex = state.Order;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        headerBorder.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            BeginPanelDrag(state, headerBorder, e);
        };
        headerBorder.PointerMoved += delegate(object? _, PointerEventArgs e)
        {
            DragPanel(state, e);
        };
        headerBorder.PointerReleased += delegate(object? _, PointerReleasedEventArgs e)
        {
            EndPanelDrag(e);
        };
        grip.PointerPressed += delegate(object? _, PointerPressedEventArgs e)
        {
            BeginPanelResize(state, grip, e);
        };
        grip.PointerMoved += delegate(object? _, PointerEventArgs e)
        {
            ResizePanel(state, e);
        };
        grip.PointerReleased += delegate(object? _, PointerReleasedEventArgs e)
        {
            EndPanelResize(state, e);
        };
        if (state.Key == PanelKeys.Scene)
        {
            // The SCENE panel widens to twice its width while the pointer is over it, growing to
            // the left when it would leave the view, and comes back to its stored size on exit.
            frame.PointerEntered += delegate
            {
                if (state.Collapsed || _panelDrag == state || _panelResizing == state || _floatHost == null)
                {
                    return;
                }
                double hostWidth = _floatHost.Bounds.Width;
                double width = Math.Min(state.Width * 2.0, hostWidth);
                double left = Math.Max(0.0, Math.Min(state.X, hostWidth - width));
                Canvas.SetLeft(frame, left);
                frame.Width = width;
                frame.ZIndex = int.MaxValue - 1;
            };
            frame.PointerExited += delegate
            {
                if (_panelDrag == state || _panelResizing == state)
                {
                    return;
                }
                Canvas.SetLeft(frame, state.X);
                frame.Width = state.Width;
                frame.ZIndex = state.Order;
            };
        }
        _floatHost.Children.Add(frame);
        _panelViews.Add(new PanelView(state, frame, control, textBlock, button, grip));
    }

    private static Button HeaderButton(string glyph, string tip)
    {
        Button button = new Button
        {
            Content = glyph,
            FontSize = 11.0,
            Width = 20.0,
            Height = 18.0,
            Padding = new Thickness(0.0),
            Margin = new Thickness(0.0, 0.0, 2.0, 0.0),
            MinWidth = 0.0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0.0),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private Control PanelBody(string key)
    {
        Control result = key switch
        {
            "Bank" => BankBody(), 
            "Scene" => SceneBody(), 
            "Properties" => PropertiesBody(),
            "Material" => MaterialBody(),
            "Transform" => TransformBody(), 
            "Mixer" => MixerBody(), 
            "Picker" => PickerBody(), 
            "Skin Workshop" => SkinWorkshopBody(),
            "Painter" => PainterBody(),
            "Rig Workshop" => RigWorkshopBody(),
            "Blendshapes" => BlendshapesBody(),
            "Stacker" => StackerBody(),
            "Cyclemaker" => CyclemakerBody(),
            "Timeline" => TimelineBody(), 
            _ => new Panel(), 
        };
        return result;
    }

    private void PlacePanels()
    {
        foreach (PanelView panelView in _panelViews)
        {
            FloatingPanelState state = panelView.State;
            Canvas.SetLeft(panelView.Frame, state.X);
            Canvas.SetTop(panelView.Frame, state.Y);
            panelView.Frame.Width = state.Width;
            panelView.Frame.Height = state.OuterHeight;
            panelView.Frame.ZIndex = state.Order;
            panelView.Body.IsVisible = !state.Collapsed;
            panelView.Arrow.Text = (state.Collapsed ? "▶" : "▼");
            // The Timeline panel sizes itself to its rows (FitTimelinePanel), so it has no grip.
            panelView.Grip.IsVisible = !state.Collapsed && state.Key != PanelKeys.Timeline;
            ToolTip.SetTip(panelView.Fold, state.Collapsed ? "Expand" : "Collapse");
        }
        UpdateMaterialPanelVisibility();
        FitTimelinePanel();
        PlaceCurveboard();
        UpdateAxisCubes();
    }

    private void BeginPanelDrag(FloatingPanelState state, Control source, PointerPressedEventArgs e)
    {
        if (!(e.Source is Visual visual) || visual.FindAncestorOfType<Button>(includeSelf: true) == null)
        {
            PointerPoint currentPoint = e.GetCurrentPoint(_floatHost);
            if (currentPoint.Properties.IsLeftButtonPressed)
            {
                _panelDrag = state;
                _panelDragStart = currentPoint.Position;
                _panelDragX = state.X;
                _panelDragY = state.Y;
                e.Pointer.Capture(source);
                e.Handled = true;
            }
        }
    }

    private void DragPanel(FloatingPanelState state, PointerEventArgs e)
    {
        if (_panelDrag == state && _floatHost != null)
        {
            FloatingLayout activeLayout = ActiveLayout;
            if (activeLayout != null)
            {
                Point position = e.GetPosition(_floatHost);
                activeLayout.Move(state.Key, _panelDragX + position.X - _panelDragStart.X, _panelDragY + position.Y - _panelDragStart.Y);
                _panelDragMoved = true;
                PlacePanels();
            }
        }
    }

    private void EndPanelDrag(PointerReleasedEventArgs e)
    {
        if (_panelDrag != null)
        {
            FloatingPanelState panelDrag = _panelDrag;
            _panelDrag = null;
            e.Pointer.Capture(null);
            bool moved = _panelDragMoved;
            _panelDragMoved = false;
            PersistPanelState(panelDrag, moved);
        }
    }

    private void BeginPanelResize(FloatingPanelState state, Control source, PointerPressedEventArgs e)
    {
        if (!state.Collapsed)
        {
            PointerPoint currentPoint = e.GetCurrentPoint(_floatHost);
            if (currentPoint.Properties.IsLeftButtonPressed)
            {
                _panelResizing = state;
                _panelResizeStart = currentPoint.Position;
                _panelResizeWidth = state.Width;
                _panelResizeHeight = state.Height;
                e.Pointer.Capture(source);
                e.Handled = true;
            }
        }
    }

    private void ResizePanel(FloatingPanelState state, PointerEventArgs e)
    {
        if (_panelResizing == state && _floatHost != null)
        {
            FloatingLayout activeLayout = ActiveLayout;
            if (activeLayout != null)
            {
                Point position = e.GetPosition(_floatHost);
                activeLayout.Resize(state.Key, _panelResizeWidth + position.X - _panelResizeStart.X, _panelResizeHeight + position.Y - _panelResizeStart.Y);
                PlacePanels();
            }
        }
    }

    private void EndPanelResize(FloatingPanelState state, PointerReleasedEventArgs e)
    {
        if (_panelResizing != null)
        {
            _panelResizing = null;
            e.Pointer.Capture(null);
            PersistPanelSize(state);
        }
    }

    private void PersistPanelSize(FloatingPanelState state)
    {
        if (_preferences == null || state.DialogClass == null)
        {
            return;
        }
        DockLayout dockLayout = DockLayout.For(_preferences, ContextLabel);
        bool flag = dockLayout.SetHeight(state.DialogClass, (int)Math.Round(state.Height - 22.0));
        if (!(flag | dockLayout.SetWidth(state.DialogClass, (int)Math.Round(state.Width))))
        {
            return;
        }
        try
        {
            _preferences.Save();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save the panel layout: " + ex.Message;
        }
    }

    private void PersistPanelState(FloatingPanelState state, bool moved = false)
    {
        if (_preferences == null)
        {
            return;
        }
        DockLayout dockLayout = DockLayout.For(_preferences, ContextLabel);
        bool flag;
        if (moved && ActiveLayout != null)
        {
            // A move can re-chain several panels (the dragged one, what hangs under it and what
            // it left behind), so the whole arrangement is written, not one entry.
            flag = ActiveLayout.WriteTo(dockLayout, sizes: false);
        }
        else
        {
            flag = state.DialogClass != null && dockLayout.SetFolded(state.DialogClass, state.Collapsed);
        }
        if (!flag)
        {
            return;
        }
        try
        {
            _preferences.Save();
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save the panel layout: " + ex.Message;
        }
    }

    private void UpdateCurveVisibility()
    {
        bool valueOrDefault = _showCurves.IsChecked == true;
        if (_curvePanel != null)
        {
            _curvePanel.IsVisible = valueOrDefault;
        }
        if (_curveHost != null)
        {
            _curveHost.IsVisible = valueOrDefault;
        }
        PlaceCurveboard();
    }

    private void ReleaseDockBodies()
    {
        foreach (Control item in SharedControls())
        {
            Detach(item);
        }
        _floatHost?.Children.Clear();
    }

    private IEnumerable<Control> SharedControls()
    {
        yield return _characters;
        yield return _animations;
        yield return _bankDragLayer;
        yield return _outliner;
        if (_charBankPanel != null) yield return _charBankPanel;
        yield return _fileLabel;
        yield return _selection;
        yield return PropertiesPanel;
        yield return MaterialPanel;
        yield return _showMesh;
        yield return _showSkeleton;
        yield return _showControllers;
        yield return _showGrid;
        yield return _showOthers;
        yield return _rotateMode;
        yield return _translateMode;
        yield return _scaleMode;
        yield return _controllers;
        yield return _pickerLayerToggle;
        yield return _snapMove;
        yield return _snapRotate;
        yield return _snapScale;
        yield return _moveStep;
        yield return _rotateStep;
        yield return _scaleStep;
        yield return _fovField;
        foreach (Control tool in TransformToolControls())
        {
            yield return tool;
        }
        SpinnerField[] translateFields = _translateFields;
        for (int i = 0; i < translateFields.Length; i++)
        {
            yield return translateFields[i];
        }
        SpinnerField[] rotateFields = _rotateFields;
        for (int j = 0; j < rotateFields.Length; j++)
        {
            yield return rotateFields[j];
        }
        SpinnerField[] scaleFields = _scaleFields;
        for (int k = 0; k < scaleFields.Length; k++)
        {
            yield return scaleFields[k];
        }
        yield return _stackEnabled;
        yield return _stackCycle;
        yield return _stackAdditive;
        yield return _stackOpacity;
        yield return _mixerList;
        yield return _mixerTextures;
        if (_mixerNew != null)
        {
            yield return _mixerNew;
            yield return _mixerDuplicate;
            yield return _mixerMerge;
            yield return _mixerExtract;
            yield return _mixerAudio;
            yield return _mixerDelete;
        }
        yield return _skinModes;
        yield return _skinRadius;
        yield return _skinStrength;
        yield return _skinValue;
        yield return _skinHard;
        yield return _skinRigid;
        yield return _skinPaint;
        yield return _skinScanner;
        yield return _skinFrontBack;
        yield return _skinWeights;
        yield return _skinInfo;
        yield return _play;
        yield return _playModeButton;
        yield return _rangeBar;
        yield return _fitAll;
        yield return _fitLayer;
        yield return _frame;
        yield return _range;
        yield return _gap;
        yield return _keyInfo;
        yield return _timeline;
    }

    private static void Detach(Control control)
    {
        StyledElement parent = control.Parent;
        StyledElement styledElement = parent;
        if (!(styledElement is Panel panel))
        {
            if (!(styledElement is Decorator decorator))
            {
                if (styledElement is ContentControl contentControl && contentControl.Content == control)
                {
                    contentControl.Content = null;
                }
            }
            else if (decorator.Child == control)
            {
                decorator.Child = null;
            }
        }
        else
        {
            panel.Children.Remove(control);
        }
    }

    private static string? PreferenceClassForPanel(string title)
    {
        string result = title switch
        {
            "Bank" => "CAnimBankPreferences", 
            "Scene" => "CHierarchyPreferences", 
            "Transform" => "CTransformPreferences", 
            "Picker" => "CPickerPreferences", 
            "Skin Workshop" => "CSkinWorkshopPreferences",
            "Painter" => "CSkinWorkshopPreferences",
            "Rig Workshop" => "CSkeletonPreferences",
            "Timeline" => "CTimelinePreferences", 
            _ => null, 
        };
        return result;
    }

    private static TextBlock Heading(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.DemiBold,
            FontSize = 11.0,
            Opacity = 0.75
        };
    }
}
