// The window constructor: builds the menu bar, viewport, timeline, status strip and dock, wires up
// every event, restores the preferences and opens the document (or the Welcome window) the command
// line asked for.
//
// This is the only place that creates controls. The panel bodies and the menu bar come from the
// helper methods in the other partial files and are handed to the dock (MainWindow.Panels.cs),
// which owns their placement from then on.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    public MainWindow(ViewerOptions options)
    {
        _options = options;
        base.Title = "Sloppy Animatsu";
        base.Width = 1600.0;
        base.Height = 1000.0;
        try
        {
            base.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SloppyAnimatsu.Viewer/Assets/app.ico")));
        }
        catch (Exception)
        {
        }
        LoadPreferences();
        LoadShortcuts();
        base.Content = Layout();
        _showOthers.IsChecked = !options.HideOthers;
        for (int i = 0; i < 3; i++)
        {
            int channel = i;
            _translateFields[i].Committed += delegate(float value)
            {
                SetTypedValue(channel, value);
            };
            _rotateFields[i].Committed += delegate(float value)
            {
                SetTypedValue(3 + channel, value);
            };
            _scaleFields[i].Committed += delegate(float value)
            {
                SetTypedValue(6 + channel, value);
            };
        }
        HookSpinnerPreviews();
        _fovField.Committed += delegate(float value)
        {
            SetFovValue(value);
        };
        ApplyPreferenceValues();
        _characters.SelectionChanged += async delegate
        {
            if (!_syncing)
            {
                Character character = (_characters.SelectedItem as Item<Character>)?.Value;
                if (character != _character)
                {
                    if (await LeaveRestPose())
                    {
                        SelectCharacter(character, null, null);
                    }
                    else
                    {
                        _syncing = true;
                        _characters.SelectedItem = (_characters.ItemsSource as IEnumerable<Item<Character>>)?.FirstOrDefault((Item<Character> item) => item.Value == _character);
                        _syncing = false;
                    }
                }
            }
        };
        _animations.SelectionChanged += async delegate
        {
            if (!_syncing)
            {
                SceneObject value = (_animations.SelectedItem as Item<SceneObject>)?.Value;
                _bankFolder = value as Folder;
                if (_bankFolder != null)
                {
                    UpdateKeyCommands();
                }
                else
                {
                    Animation animation = value as Animation;
                    if (animation != _animation)
                    {
                        SelectAnimation((await LeaveRestPose()) ? animation : _animation, null);
                    }
                }
            }
        };
        _controllers.SelectionMode = SelectionMode.Multiple;
        _controllers.ItemTemplate = new FuncDataTemplate<Item<Object3D>>((item, _) => PickerRowView(item), supportsRecycling: false);
        // The Picker's rows are compact, like the list: the panel is small and the rows
        // are plain names (the decoration lives in the row).
        _controllers.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0),
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(2.0, 1.0))
            }
        });
        _controllers.SelectionChanged += delegate(object? _, SelectionChangedEventArgs e)
        {
            if (!_syncing)
            {
                foreach (Item<Object3D> item in e.RemovedItems.OfType<Item<Object3D>>())
                {
                    _selectedAll.Remove(item.Value);
                }
                foreach (Item<Object3D> item2 in e.AddedItems.OfType<Item<Object3D>>())
                {
                    if (item2.Value != null && !_selectedAll.Contains(item2.Value))
                    {
                        _selectedAll.Add(item2.Value);
                    }
                }
                SelectionChanged();
            }
        };
        _outliner.ObjectSelected += SelectFromOutliner;
        WireCameraView();
        WireStatusLog();
        _outliner.ShownRequested += delegate(Object3D o, bool shown)
        {
            // An eye clicked on a row of a multi-selection sets every selected row to the clicked
            // row's new state (the pointer button already works this way).
            IReadOnlyList<Object3D> rows = _outliner.SelectedObjects;
            ChangeShown(rows.Count > 1 && rows.Contains(o) ? rows : [o], shown);
        };
        _outliner.AllSelected += SelectAllTreeRows;
        _outliner.PickableRequested += delegate(Object3D o, bool pickable)
        {
            ChangePickable(o, pickable);
        };
        _outliner.RenameRequested += RenameFromOutliner;
        _outliner.DeleteRequested += delegate
        {
            _ = DeleteTreeObject();
        };
        _outliner.ReparentRequested += ReparentFromOutliner;
        _outliner.SceneRootDropRequested += dragged => MoveMeshFromOutliner(dragged, null, OutlinerDropPlace.Inside);
        // The Rename Tool works on the selection of the panel used last (the last activated
        // window), so remember which of the two naming panels that was.
        _outliner.AddHandler(InputElement.PointerPressedEvent, delegate { _renameFromBank = false; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _animations.AddHandler(InputElement.PointerPressedEvent, delegate { _renameFromBank = true; }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Delete in the Anim Bank removes the selected rows (animations and folders alike).
        _animations.AddHandler(InputElement.KeyDownEvent, delegate(object? _, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None)
            {
                DeleteBankSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None)
            {
                if (_bankFolder != null) _ = RenameFolder();
                else _ = RenameAnimation();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        _rotateMode.Click += delegate
        {
            SetMode(GizmoMode.Rotate);
        };
        _translateMode.Click += delegate
        {
            SetMode(GizmoMode.Translate);
        };
        _scaleMode.Click += delegate
        {
            SetMode(GizmoMode.Scale);
        };
        CheckBox[] array = new CheckBox[5] { _showMesh, _showSkeleton, _showControllers, _showGrid, _showOthers };
        foreach (CheckBox checkBox in array)
        {
            checkBox.IsCheckedChanged += delegate
            {
                Apply();
            };
        }
        _timeline.TimeChanged += delegate(float t)
        {
            ScrubTo(t);
        };
        _timeline.MoveRequested += MoveKeys;
        _timeline.TextureKeyMoved += MoveTextureKey;
        _timeline.DeleteRequested += DeleteKeys;
        _timeline.CreateRequested += delegate
        {
            CreateKey();
        };
        _timeline.CopyRequested += CopyKeys;
        _timeline.PasteRequested += PasteKeys;
        _timeline.SelectionChanged += delegate
        {
            AnimStack activeStack = _timeline.ActiveStack;
            if (activeStack != null)
            {
                Animation animation = activeStack.Animation;
                if (animation != null && animation.CurrentStack != activeStack)
                {
                    Session?.SetCurrentStack(activeStack);
                }
                // A row picked on the timeline becomes the Mixer's selection too, unless it is
                // already part of a multi-row selection made in the Mixer.
                if (animation != null && !_syncing && !animation.SelectedStacks.Contains(activeStack))
                {
                    animation.SelectStacks([activeStack]);
                    RefreshMixer();
                }
            }
            UpdateKeyCommands();
        };
        _stackBlend.SelectionChanged += delegate
        {
            if (_syncing || _stackBlend.SelectedIndex < 0) return;
            AnimStack stack = _timeline.ActiveStack;
            bool additive = _stackBlend.SelectedIndex == 1;
            if (stack != null && stack.IsAdditive != additive)
            {
                RunBankEdit(delegate(PoseEditSession session)
                {
                    session.SetStackAdditive(stack, additive);
                });
            }
        };
        // Ctrl/Shift+click select several rows; SelectedItem (the first selected) stays the
        // current animation, so adding rows does not switch it.
        _animations.SelectionMode = SelectionMode.Multiple;
        _animations.ContextMenu = _animationMenu;
        _animations.ItemTemplate = new FuncDataTemplate<Item<SceneObject>>((item, _) => BankRow(item), supportsRecycling: false);
        // Compact tree rows, like the Scene panel's.
        _animations.Styles.Add(new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.OfType<ListBoxItem>(x))
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(4.0, 2.0)),
                new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0)
            }
        });
        _outliner.ContextMenu = _outlinerMenu;
        // The row the right click picked decides the menu's rows (the skin lock shows for skinned meshes).
        _outlinerMenu.Opening += delegate
        {
            _outlinerMenu.ItemsSource = MenuModel.OutlinerItems(CurrentMenuState()).Select(ToMenuItem).ToList();
        };
        _animations.AddHandler(InputElement.PointerPressedEvent, BankPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _animations.AddHandler(InputElement.PointerMovedEvent, BankPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _animations.AddHandler(InputElement.PointerReleasedEvent, BankPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _timeline.StackMoveRequested += MoveStackTo;
        _timeline.RippleRequested += RippleKey;
        _timeline.ReorderRequested += ReorderKeys;
        _timeline.GapChangeRequested += ChangeGap;
        _timeline.FadeDragStarted += BeginFadeDrag;
        _timeline.FadeDragged += delegate(int delta)
        {
            if (_fadeDrag != null)
            {
                _fadeDrag.Update(delta);
                _status.Text = $"Fade-{(_fadeDrag.FadeIn ? "in" : "out")} of key {_fadeDrag.KeyIndex}: {_fadeDrag.Fade} frame(s)";
                SetTime(_time);
            }
        };
        _timeline.FadeDragEnded += EndFadeDrag;
        _timeline.ContextMenu = _keyMenu;
        // The right click decides the rows: on a key the key commands, on an empty spot Create/Paste.
        _keyMenu.Opening += delegate
        {
            _keyMenu.ItemsSource = TimelineMenuItems(CurrentMenuState());
        };
        InstallPointerKeyRouting();
        // Shift/Ctrl/Alt pressed or released over the viewport switch the vertex selection pointer,
        // wherever the focus is.
        AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) => ModifiersChanged(e, down: true),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyUpEvent, (object? _, KeyEventArgs e) => ModifiersChanged(e, down: false),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // CopyKeys / PasteKeys (Ctrl+C / Ctrl+V by default) wherever the focus is, unless a text field took them.
        AddHandler(InputElement.KeyDownEvent, delegate(object? _, KeyEventArgs e)
        {
            if (e.Handled || FocusManager?.GetFocusedElement() is TextBox) return;
            ShortcutKey? key = ShortcutInput.FromKey(e.Key, e.KeyModifiers);
            if (key is not { } pressed || !Hotkeys.RunsWhileTyping(pressed)) return;
            switch (_hotkeys.ActionFor(HotkeyScope.Window, pressed))
            {
                case "CopyKeys":
                    CopyKeys();
                    e.Handled = true;
                    break;
                case "PasteKeys":
                    PasteKeys(after: false);
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Bubble);
        // The window's key router: a key nothing focused claimed runs the main-menu row whose gesture
        // it is (gestures come from the shortcuts map). Hotkeys.ReachesCommands keeps plain and Shift
        // keys away from commands while a text field has the focus; such a KeyDown is left unhandled,
        // because on Windows a handled KeyDown also swallows the typed character.
        AddHandler(InputElement.KeyDownEvent, delegate(object? _, KeyEventArgs e)
        {
            if (e.Handled || ShortcutInput.FromKey(e.Key, e.KeyModifiers) is not { } key) return;
            bool typing = FocusManager?.GetFocusedElement() is TextBox;
            if (Hotkeys.MenuCommandFor(CurrentMenuState(), key, typing) is not { } entry) return;
            MenuCommand command = entry.Command!.Value;
            string? argument = entry.Argument;
            Dispatcher.UIThread.Post(() => RunMenuCommand(command, argument));
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        _showCurves.IsCheckedChanged += delegate
        {
            // An opened curveboard comes to the front, as a pressed window does.
            if (_showCurves.IsChecked == true && _curveHost != null && ActiveLayout is { } layout)
                _curveHost.ZIndex = layout.NextOrder();
            UpdateCurveVisibility();
            UpdateCurveboard();
        };
        _curveboard.TimeChanged += delegate(float t)
        {
            ScrubTo(t);
        };
        _curveboard.SelectionChanged += UpdateCurveInfo;
        _curveboard.DragStarted += delegate(string description)
        {
            PoseEditSession session = Session;
            if (session != null)
            {
                AnimStack stack = _curveboard.Stack;
                if (stack != null)
                {
                    _curveDrag = session.BeginCurveDrag(stack, description);
                }
            }
        };
        _curveboard.Dragged += delegate(Action<KeyEditor> edit)
        {
            if (_curveDrag != null)
            {
                try
                {
                    _curveDrag.Update(edit);
                }
                catch (Exception ex2)
                {
                    _status.Text = "Curve edit failed: " + ex2.Message;
                }
                _curveboard.Refresh();
                UpdateCurveInfo();
                SetTime(_time);
            }
        };
        _curveboard.DragEnded += delegate
        {
            CurveDrag drag = _curveDrag;
            _curveDrag = null;
            if (drag != null)
            {
                RunKeyEdit(delegate
                {
                    drag.Commit();
                }, "Curve edit failed: ");
            }
        };
        _gap.ValueChanged += delegate
        {
            if (!_syncing)
            {
                decimal? value = _gap.Value;
                if (value.HasValue)
                {
                    decimal valueOrDefault = value.GetValueOrDefault();
                    SetGap((int)valueOrDefault);
                }
            }
        };
        _skinModes.ItemsSource = new string[4] { "Add", "Aim", "Smooth", "Select" };
        _skinModes.SelectedIndex = 0;
        _skinRadius.Show(30f);
        _skinStrength.Show(1f);
        _skinValue.Show(1f);
        _skinModes.SelectionChanged += delegate
        {
            if (!_syncing && _skin != null)
            {
                _skin.Mode = (SkinPaintMode)Math.Clamp(_skinModes.SelectedIndex, 0, 3);
            }
        };
        _skinRadius.Committed += delegate(float value)
        {
            _skinRadiusPixels = Math.Max(1f, value);
        };
        _skinStrength.Committed += delegate(float value)
        {
            if (_skin != null)
            {
                _skin.Strength = Math.Clamp(value, 0f, 1f);
            }
        };
        _skinValue.Committed += delegate(float value)
        {
            if (_skin != null)
            {
                _skin.Value = Math.Clamp(value, 0f, 1f);
            }
        };
        _skinHard.IsCheckedChanged += delegate
        {
            if (_skin != null)
            {
                _skin.Hard = _skinHard.IsChecked == true;
            }
        };
        _skinRigid.IsCheckedChanged += delegate
        {
            if (!_syncing && _skin != null)
            {
                _skin.Rigid = _skinRigid.IsChecked == true;
                RebuildSkinJoints();
            }
        };
        _skinWeights.Content = _skinRows;
        _skinFrontBack.IsChecked = _skin?.FrontBackPaint ?? true;
        _skinScanner.IsCheckedChanged += delegate
        {
            if (_skin != null)
            {
                _skin.Scanner = _skinScanner.IsChecked == true;
                if (!_skin.Scanner)
                {
                    ShowScan(null, default(Vector2));
                }
            }
        };
        _skinFrontBack.IsCheckedChanged += delegate
        {
            if (_skin != null)
            {
                bool valueOrDefault = _skinFrontBack.IsChecked == true;
                _skin.FrontBackPaint = valueOrDefault;
                _skin.FrontBackSelect = valueOrDefault;
            }
        };
        _frame.ValueChanged += delegate
        {
            if (!_syncing)
            {
                decimal? value = _frame.Value;
                if (value.HasValue)
                {
                    decimal valueOrDefault = value.GetValueOrDefault();
                    SetTime((float)valueOrDefault);
                }
            }
        };
        _play.IsCheckedChanged += delegate
        {
            _play.Content = ((_play.IsChecked == true) ? "Pause" : "Play");
            if (_play.IsChecked == true)
            {
                _clock.Restart();
                _timer.Start();
                var (playStart, playEnd) = PlayBounds();
                // Play starts inside the frames it plays.
                if (_time < playStart || (_time > playEnd + 1 && !PlaysEndlessly)) SetTime(playStart);
                _audio.Start(_animation, _time, FrameRate, !PlaysEndlessly, (playStart, playEnd + 1));
                string error = _audio.Error;
                if (error != null)
                {
                    _status.Text = error;
                }
            }
            else
            {
                _timer.Stop();
                _audio.Stop();
                UpdateRange();
                Apply();
            }
        };
        _playModeButton.Click += delegate
        {
            // Loop -> Loop range -> Endless -> Loop. A running playback picks the new mode up at once.
            _playMode = _playMode switch
            {
                PlayMode.Loop => PlayMode.LoopRange,
                PlayMode.LoopRange => PlayMode.Endless,
                _ => PlayMode.Loop,
            };
            UpdatePlayModeButton();
            if (_play.IsChecked == true)
            {
                var (playStart, playEnd) = PlayBounds();
                _audio.Stop();
                _audio.Start(_animation, _time, FrameRate, !PlaysEndlessly, (playStart, playEnd + 1));
            }
        };
        UpdatePlayModeButton();
        _rangeBar.RangeChanged += UpdateRange;
        _timeline.ZoomRequested += delegate(double frame, double notches)
        {
            if (CurrentTimeBar is not { } bar) return;
            bar.Zoom(frame, notches);
            _rangeBar.InvalidateVisual();
            UpdateRange();
        };
        _fitAll.Click += delegate { FitAllRange(); };
        _fitLayer.Click += delegate { FitLayerRange(); };
        _timer.Tick += delegate
        {
            Advance();
        };
        _viewport.Editor = this;
        // A view orbits about the selected object, as posed.
        _viewport.OrbitPivot = () => _selected is { } s && _pose != null ? _pose.WorldOf(s).Translation : null;
        _viewport.UnhandledKey += delegate(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _hotPoint.HasValue)
            {
                _hotPoint = null;
                RefreshHotPointMarkers();
                _autoRiggerDialog?.SetArmed(null);
                _autoRiggerDialog?.SetPrompt(null);
                _status.Text = "Hot-point placement cancelled.";
                e.Handled = true;
            }
            else if (_placement != null && e.Key == Key.Escape)
            {
                _placement = null;
                _status.Text = "Placement cancelled.";
                e.Handled = true;
            }
            // A viewport key (panelShortcuts.slopa) wins over the window-wide the app map, as the
            // other panels' keys do while they have the focus.
            // Numpad + and - grow and shrink a vertex selection; the app names no action for them.
            else if (((e.Key == Key.Add || e.Key == Key.Subtract) && e.KeyModifiers == KeyModifiers.None && RunVertexSelectionKey(e.Key)) ||
                     RunViewportKey(e.Key, e.KeyModifiers) || RunShortcut(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
        };
        _viewport.GlError += delegate(string message)
        {
            Dispatcher.UIThread.Post(delegate
            {
                _status.Text = "OpenGL: " + message;
            });
        };
        _viewport.FrameCaptured += delegate(byte[] pixels, int w, int h)
        {
            MemoryLog.Step("frame captured");
            Dispatcher.UIThread.Post(delegate
            {
                SaveScreenshot(pixels, w, h);
            });
        };
        HookAxisCubes();
        _viewport.ViewFocused += delegate(int view)
        {
            Dispatcher.UIThread.Post(delegate
            {
                if (view >= 0 && view < _viewStates.Count)
                {
                    bool changed = view != _focusedView;
                    _focusedView = view;
                    if (changed) LoadFocusedViewDisplay();
                    RefreshMenu();
                    UpdateAxisCubes();
                    _status.Text = $"Focused view {view + 1}: {ViewDescription(_viewStates[view])}";
                }
            });
        };
        RefreshMenu();
        StartAutosave();
        base.Opened += async delegate
        {
            bool sceneOnCommandLine = _options.Path != null || _options.NewScene;
            NewScene();
            if (_options.Path != null)
            {
                Load(_options.Path);
            }
            RunScriptedEdit();
            if (_options.Curves)
            {
                _showCurves.IsChecked = true;
                _curveboard.SelectAll();
            }
            if (_options.Screenshot != null)
            {
                _viewport.CaptureNextFrame = true;
                _viewport.RequestNextFrameRendering();
            }
            StartupUpdateCheck();
            if (_options.Welcome || Welcome.ShowAtStartup(sceneOnCommandLine, _options.Scripted, Welcome.IsSkipped(_preferences)))
            {
                await ShowWelcome();
            }
        };
        base.Closing += (_, e) => GuardUnsavedOnClose(e);
        base.Closed += delegate
        {
            _autosaveTimer?.Stop();
            _audio.Dispose();
        };
    }
}
