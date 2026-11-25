// The menu bar: Layout builds the window grid, RefreshMenu rebuilds the menu when the context or the
// selection changes, and MenuHandlers is the one table mapping every MenuCommand to the action that
// runs it.
//
// RunMenuCommand is the single entry point for a menu click. Add an entry to MenuHandlers together
// with the command in SloppyAnimatsu.Core MenuCatalog, and to CurrentMenuState when it needs a check mark.
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
    private Control Layout()
    {
        _menuBar = new Menu();
        DockPanel dockPanel = new DockPanel();
        DockPanel.SetDock(_menuBar, Dock.Top);
        dockPanel.Children.Add(_menuBar);
        _tabBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1.0
        };
        Border border = new Border
        {
            Padding = new Thickness(6.0, 2.0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _tabBar
            }
        };
        DockPanel.SetDock(border, Dock.Top);
        dockPanel.Children.Add(border);
        _tabStrip = border;
        DockPanel dockPanel2 = new DockPanel();
        _statusBar = dockPanel2;
        _contextButton.Margin = new Thickness(8.0, 0.0, 0.0, 0.0);
        _contextLabel.Margin = new Thickness(12.0, 0.0, 0.0, 0.0);
        _contextButton.Click += delegate
        {
            ToggleContext();
        };
        DockPanel.SetDock(_contextButton, Dock.Right);
        dockPanel2.Children.Add(_contextButton);
        DockPanel.SetDock(_contextLabel, Dock.Right);
        dockPanel2.Children.Add(_contextLabel);
        // "Update Available v…" (MainWindow.Updates.cs), hidden until a newer release is known.
        AddUpdateNotice(dockPanel2);
        // The layout button (the app keeps one in the bottom-left corner): resets the panels of
        // both contexts to a preset.
        MenuFlyout layoutMenu = new MenuFlyout();
        foreach (LayoutPreset preset in Enum.GetValues<LayoutPreset>())
        {
            LayoutPreset chosen = preset;
            MenuItem item = new MenuItem
            {
                Header = ((preset == LayoutPreset.Default) ? "Reset to default layout" : ("Preset: " + LayoutPresets.Name(preset)))
            };
            item.Click += delegate
            {
                ResetLayout(chosen);
            };
            layoutMenu.Items.Add(item);
        }
        Button layoutButton = new Button
        {
            Content = "Layout ▾",
            FontSize = 11.0,
            Padding = new Thickness(6.0, 1.0),
            Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
            Flyout = layoutMenu
        };
        ToolTip.SetTip(layoutButton, "Reset the panel layout or load a preset");
        // The View & Preferences button sits at the bottom-left corner (MainWindow.ViewPreferences.cs).
        Button viewPrefs = ViewPrefsButton();
        DockPanel.SetDock(viewPrefs, Dock.Left);
        dockPanel2.Children.Add(viewPrefs);
        DockPanel.SetDock(layoutButton, Dock.Left);
        dockPanel2.Children.Add(layoutButton);
        dockPanel2.Children.Add(_status);
        Border border2 = new Border
        {
            Padding = new Thickness(10.0, 3.0),
            Child = dockPanel2
        };
        DockPanel.SetDock(border2, Dock.Bottom);
        dockPanel.Children.Add(border2);
        _viewArea = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        Grid grid = new Grid();
        grid.Children.Add(new Border
        {
            Background = Brushes.Black,
            Child = _viewport
        });
        // The axis cube of every orbit view, over the GL viewport (MainWindow.AxisCube.cs).
        grid.Children.Add(_axisCubes);
        _scanText = new TextBlock
        {
            FontSize = 11.0,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260.0
        };
        _scanBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
            BorderThickness = new Thickness(1.0),
            Padding = new Thickness(6.0, 3.0),
            CornerRadius = new CornerRadius(3.0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Child = _scanText
        };
        grid.Children.Add(_scanBadge);
        grid.Children.Add(_marqueeShape);
        _viewArea.Children.Add(grid);
        if (_curvePanel == null)
        {
            _curvePanel = CurvePanel();
        }
        // The curveboard and the spinner dial float in the panels' canvas, so the last one pressed
        // is in front (MainWindow.Overlays.cs); the viewport keeps the whole view area.
        _curveHost = CurveboardFrame(_curvePanel);
        _floatHost = new Canvas
        {
            ClipToBounds = true
        };
        CreateSpinnerDial();
        AddOverlays();
        Grid grid2 = new Grid();
        grid2.Children.Add(_viewArea);
        grid2.Children.Add(_floatHost);
        dockPanel.Children.Add(grid2);
        _floatHost.SizeChanged += delegate
        {
            FloatingHostSized();
            PlaceSpinnerDial();
        };
        ApplyDockLayout();
        RefreshMenu();
        MenuHandlers();
        EnableFileDrop();
        return dockPanel;
    }

    /// <summary>The timeline's context menu: the key commands on a key, Create/Paste on an empty spot.</summary>
    private List<Control> TimelineMenuItems(MenuState state) =>
        (_timeline.ContextKey >= 0 ? MenuModel.TimelineKeyItems(state) : MenuModel.TimelineEmptyItems(state))
            .Select(ToMenuItem).ToList();

    private void RefreshMenu()
    {
        RefreshAnimationPanels();
        RefreshRigAtelier();
        if (_menuBar == null)
        {
            return;
        }
        MenuState state = CurrentMenuState();
        _menuBar.Items.Clear();
        foreach (MenuEntry item in MenuModel.Bar(state))
        {
            _menuBar.Items.Add(ToMenuItem(item));
        }
        _keyMenu.ItemsSource = TimelineMenuItems(state);
        _animationMenu.ItemsSource = MenuModel.AnimationItems(state).Select(ToMenuItem).ToList();
        _outlinerMenu.ItemsSource = MenuModel.OutlinerItems(state).Select(ToMenuItem).ToList();
        RefreshContextButton();
    }

    private Control ToMenuItem(MenuEntry entry)
    {
        if (entry.Kind == MenuEntryKind.Separator)
        {
            return new Separator();
        }
        MenuItem menuItem = new MenuItem
        {
            Header = entry.Header,
            IsEnabled = entry.Enabled
        };
        // A greyed-out row keeps its hover text and adds what it needs to work again.
        if (entry.HoverText != null) ToolTip.SetTip(menuItem, entry.HoverText);
        if (entry.Checkable)
        {
            menuItem.ToggleType = MenuItemToggleType.CheckBox;
            menuItem.IsChecked = entry.Checked;
        }
        ShortcutKey? gesture = entry.Gesture;
        if (gesture.HasValue)
        {
            ShortcutKey valueOrDefault = gesture.GetValueOrDefault();
            if (ShortcutInput.TryGesture(valueOrDefault, out KeyGesture gesture2))
            {
                // Shown only. A menu row's key is run by the window's key router (Hotkeys.MenuCommandFor),
                // never by MenuItem.HotKey: Avalonia's hot key marks the KeyDown handled even in a
                // focused TextBox, and on Windows a handled KeyDown also swallows the typed character.
                menuItem.InputGesture = gesture2;
            }
        }
        foreach (MenuEntry child in entry.Children)
        {
            menuItem.Items.Add(ToMenuItem(child));
        }
        if (entry.Command == MenuCommand.OpenRecent)
        {
            // A recent scene's row is its full path: a smaller font, never trimmed.
            menuItem.FontSize = 11.0;
        }
        if (entry.Children.Any(child => child.Command == MenuCommand.OpenRecent))
        {
            // The Fluent submenu popup caps its width at FlyoutThemeMaxWidth (456), which cut long
            // recent paths off; lift the cap for this submenu so the popup grows to the longest path.
            menuItem.Resources["FlyoutThemeMaxWidth"] = 4000.0;
        }
        MenuCommand? command = entry.Command;
        if (command.HasValue)
        {
            MenuCommand command2 = command.GetValueOrDefault();
            menuItem.Click += delegate
            {
                Dispatcher.UIThread.Post(delegate
                {
                    RunMenuCommand(command2, entry.Argument);
                });
            };
        }
        return menuItem;
    }

    private void RunMenuCommand(MenuCommand command, string? argument)
    {
        if (MenuHandlers().TryGetValue(command, out Action<MenuEntry> value))
        {
            SloppyAnimatsu.Core.Diagnostics.AppLog.Action(argument == null
                ? "command " + command
                : $"command {command} ({argument})");
            value(new MenuEntry
            {
                Header = command.ToString(),
                Command = command,
                Argument = argument
            });
        }
        else
        {
            _status.Text = $"The menu command {command} is not implemented in this build.";
        }
    }

    private MenuState CurrentMenuState()
    {
        AnimStack activeStack = _timeline.ActiveStack;
        return new MenuState
        {
            Context = _pose?.Model.Scene.Context ?? _lastContext,
            HasScene = _pose != null && _character != null,
            HasDocument = _documents.Count > 0,
            HasAnimation = _animation != null,
            HasStack = activeStack != null,
            HasAnimBank = _character?.AnimBank != null,
            HasBankFolder = _bankFolder != null,
            HasClipboard = _clipboard != null,
            KeepChildrenFixed = _keepChildrenFixed,
            HammerRemoves = activeStack != null && _selected != null && activeStack.TrackFor(_selected) != null,
            SelectionCount = _selectedAll.Count,
            JointsSelected = _selectedAll.OfType<Joint>().Count(),
            SelectionIsJoint = _selected is Joint,
            PoseRowCurrent = _character != null && !CharacterBank.IsPaintAndCheck(_character),
            JointEditBlocked = _character != null && !CharacterBank.IsBindPoseCurrent(_character),
            HasTreeRow = _outliner.ContextRow != null && _outliner.Selected != null,
            TreeRowSkinLocked = _outliner.Selected is Mesh { Skinned: true } treeMesh ? treeMesh.SkinnedMeshIsLocked : null,
            SelectedKeyCount = _timeline.SelectedKeys.Count,
            SelectedKeysHaveFadeIn = activeStack != null && _timeline.SelectedKeys.Any(k => k >= 0 && k < activeStack.Keys.Count && activeStack.Keys[k].FadeIn != 0),
            SelectedKeysHaveFadeOut = activeStack != null && _timeline.SelectedKeys.Any(k => k >= 0 && k < activeStack.Keys.Count && activeStack.Keys[k].FadeOut != 0),
            StackCount = _animation?.Stacks.Count ?? 0,
            StackIndex = ((activeStack != null && _animation != null) ? _animation.Stacks.ToList().IndexOf(activeStack) : (-1)),
            CanCycleStep = activeStack != null && KeyEditor.CanCycleStep(activeStack, out string reason),
            CanCycleSide = activeStack != null && KeyEditor.CanCycleSide(activeStack, out reason),
            Layout = _layout,
            ViewKey = ViewKey,
            FocusedView = _focusedView + 1,
            Cameras = FixedCameraNames(),
            AnimatedCameras = (from camera in (_pose?.Model.Scene.Cameras ?? Array.Empty<Camera>()).OfType<CameraAnimated>().Concat(_character?.AnimatedCameras ?? Enumerable.Empty<CameraAnimated>())
                select camera.Name ?? "(unnamed)").Distinct().ToList(),
            RecentFiles = _preferences?.RecentFiles ?? Array.Empty<string>(),
            JointAlignment = _jointAlignment,
            Shortcuts = _shortcuts,
        };
    }

    private Dictionary<MenuCommand, Action<MenuEntry>> MenuHandlers()
    {
        if (_menuHandlers != null)
        {
            return _menuHandlers;
        }
        Dictionary<MenuCommand, Action<MenuEntry>> dictionary = (_menuHandlers = new Dictionary<MenuCommand, Action<MenuEntry>>
        {
            [MenuCommand.NewScene] = delegate
            {
                NewScene();
            },
            [MenuCommand.Open] = delegate
            {
                OpenFile();
            },
            [MenuCommand.CloseFile] = delegate
            {
                CloseDocument();
            },
            [MenuCommand.Save] = delegate
            {
                Save(choosePath: false);
            },
            [MenuCommand.SaveAs] = delegate
            {
                Save(choosePath: true);
            },
            [MenuCommand.OpenRecent] = delegate(MenuEntry entry)
            {
                string argument = entry.Argument;
                if (argument != null)
                {
                    OpenRecent(argument);
                }
            },
            [MenuCommand.Preferences] = delegate
            {
                OpenPreferencesDialog();
            },
            [MenuCommand.Undo] = delegate
            {
                Undo();
            },
            [MenuCommand.Redo] = delegate
            {
                Redo();
            },
            [MenuCommand.CreateKey] = delegate
            {
                CreateKey();
            },
            [MenuCommand.DeleteKeys] = delegate
            {
                DeleteKeys(closeGaps: false);
            },
            [MenuCommand.DeleteKeysClosingGaps] = delegate
            {
                DeleteKeys(closeGaps: true);
            },
            [MenuCommand.Hammer] = delegate
            {
                Hammer();
            },
            [MenuCommand.KeepChildrenFixed] = delegate
            {
                SetKeepChildrenFixed(!_keepChildrenFixed);
            },
            [MenuCommand.MirrorPose] = delegate
            {
                MirrorPose();
            },
            [MenuCommand.KeyboardShortcuts] = delegate
            {
                OpenShortcutDialog();
            },
            [MenuCommand.PanelKeys] = delegate
            {
                OpenPanelKeysDialog();
            },
            [MenuCommand.AutoRigger] = delegate
            {
                OpenAutoRigger();
            },
            [MenuCommand.NewJoint] = delegate
            {
                if (JointEditAllowed()) NewJoint();
            },
            [MenuCommand.InsertJoint] = delegate
            {
                if (JointEditAllowed()) InsertJoint();
            },
            [MenuCommand.DeleteJoints] = delegate
            {
                DeleteJoints();
            },
            [MenuCommand.OrientJoints] = delegate
            {
                if (JointEditAllowed()) OrientJoints();
            },
            [MenuCommand.JointAxis] = delegate(MenuEntry entry)
            {
                if (MenuModel.TryParseJointAxis(entry.Argument, out var aim, out var axis, out var negative))
                {
                    SetJointAxis(aim, axis, negative);
                }
            },
            [MenuCommand.JointAlignOnCreate] = delegate
            {
                SetJointAlignOnCreate(!_jointAlignment.AlignOnCreate);
            },
            [MenuCommand.ParentJoints] = delegate
            {
                if (JointEditAllowed()) ParentJoints();
            },
            [MenuCommand.UnparentJoints] = delegate
            {
                if (JointEditAllowed()) UnparentJoints();
            },
            [MenuCommand.AddIk] = delegate
            {
                if (JointEditAllowed()) AddIk();
            },
            [MenuCommand.AddReverseFoot] = delegate
            {
                if (JointEditAllowed()) AddReverseFoot();
            },
            [MenuCommand.BindSkin] = delegate
            {
                BindSkin();
            },
            [MenuCommand.UnbindSkin] = delegate
            {
                UnbindSkin();
            },
            [MenuCommand.NormalizeScale] = delegate
            {
                NormalizeScale();
            },
            [MenuCommand.NormalizeTranslation] = delegate
            {
                NormalizeTransform(ReframeParts.Translation);
            },
            [MenuCommand.NormalizeRotation] = delegate
            {
                NormalizeTransform(ReframeParts.Rotation);
            },
            [MenuCommand.TimelineCreateKey] = delegate
            {
                CreateKey(_timeline.ContextFrame);
            },
            [MenuCommand.TimelinePasteKeys] = delegate
            {
                PasteKeysAt(_timeline.ContextFrame);
            },
            [MenuCommand.CopyKeys] = delegate
            {
                CopyKeys();
            },
            [MenuCommand.PasteKeys] = delegate
            {
                PasteKeys(after: false);
            },
            [MenuCommand.PasteKeysAfter] = delegate
            {
                PasteKeys(after: true);
            },
            [MenuCommand.MirrorKeys] = delegate
            {
                MirrorKeys();
            },
            [MenuCommand.MarkExtreme] = delegate
            {
                MarkKeys(1u, all: false);
            },
            [MenuCommand.MarkBreakdown] = delegate
            {
                MarkKeys(2u, all: false);
            },
            [MenuCommand.RemoveMarks] = delegate
            {
                MarkKeys(0u, all: false);
            },
            [MenuCommand.RemoveMarksAll] = delegate
            {
                MarkKeys(0u, all: true);
            },
            [MenuCommand.FadeIn] = delegate
            {
                FadeKeys(fadeIn: true, 5);
            },
            [MenuCommand.FadeOut] = delegate
            {
                FadeKeys(fadeIn: false, 5);
            },
            [MenuCommand.RemoveFadeIn] = delegate
            {
                FadeKeys(fadeIn: true, 0);
            },
            [MenuCommand.RemoveFadeOut] = delegate
            {
                FadeKeys(fadeIn: false, 0);
            },
            [MenuCommand.MoveKeysEarlier] = delegate
            {
                ReorderKeys(-1);
            },
            [MenuCommand.MoveKeysLater] = delegate
            {
                ReorderKeys(1);
            },
            [MenuCommand.WidenGap] = delegate
            {
                ChangeGap(1);
            },
            [MenuCommand.NarrowGap] = delegate
            {
                ChangeGap(-1);
            },
            [MenuCommand.ChangeSpace] = delegate
            {
                ChangeSpace();
            },
            [MenuCommand.ToggleSpaceable] = delegate
            {
                ToggleSpaceable();
            },
            [MenuCommand.NewAnimation] = delegate
            {
                NewAnimation();
            },
            [MenuCommand.CopyAnimation] = delegate
            {
                CopyAnimation();
            },
            [MenuCommand.DeleteAnimation] = delegate
            {
                DeleteBankSelection();
            },
            [MenuCommand.RenameAnimation] = delegate
            {
                RenameAnimation();
            },
            [MenuCommand.NewFolder] = delegate
            {
                NewFolder();
            },
            [MenuCommand.RenameFolder] = delegate
            {
                RenameFolder();
            },
            [MenuCommand.CycleStep] = delegate
            {
                CycleStep();
            },
            [MenuCommand.CycleSide] = delegate
            {
                CycleSide();
            },
            [MenuCommand.RetargetAnimations] = delegate
            {
                OpenRetarget();
            },
            [MenuCommand.NewStack] = delegate
            {
                NewStack();
            },
            [MenuCommand.DuplicateStack] = delegate
            {
                DuplicateStack();
            },
            [MenuCommand.DeleteStack] = delegate
            {
                DeleteStack();
            },
            [MenuCommand.MoveStackUp] = delegate
            {
                MoveStack(-1);
            },
            [MenuCommand.MoveStackDown] = delegate
            {
                MoveStack(1);
            },
            [MenuCommand.RenameStack] = delegate
            {
                RenameStack();
            },
            [MenuCommand.SetContext] = delegate(MenuEntry entry)
            {
                if (MenuModel.TryParseContext(entry.Argument, out var context))
                {
                    SetContext(context);
                }
            },
            [MenuCommand.RenameTreeObject] = delegate
            {
                _outliner.BeginRename();
            },
            [MenuCommand.RenameTool] = delegate
            {
                OpenRenameTool();
            },
            [MenuCommand.ShowHierarchy] = delegate
            {
                ShowTreeHierarchy(true);
            },
            [MenuCommand.HideHierarchy] = delegate
            {
                ShowTreeHierarchy(false);
            },
            [MenuCommand.SelectHierarchy] = delegate
            {
                SelectTreeHierarchy();
            },
            [MenuCommand.ExpandHierarchy] = delegate
            {
                ExpandTreeHierarchy(true);
            },
            [MenuCommand.CollapseHierarchy] = delegate
            {
                ExpandTreeHierarchy(false);
            },
            [MenuCommand.ShowAllObjects] = delegate
            {
                ChangeAllShown(true);
            },
            [MenuCommand.HideAllObjects] = delegate
            {
                ChangeAllShown(false);
            },
            [MenuCommand.SelectAllTreeRows] = delegate
            {
                _outliner.SelectAll();
                SelectAllTreeRows();
            },
            [MenuCommand.ExpandAllTreeRows] = delegate
            {
                _outliner.SetExpandedAll(true);
            },
            [MenuCommand.CollapseAllTreeRows] = delegate
            {
                _outliner.SetExpandedAll(false);
            },
            [MenuCommand.DuplicateHierarchy] = delegate
            {
                DuplicateSelectedHierarchy();
            },
            [MenuCommand.DeleteTreeObject] = delegate
            {
                _ = DeleteTreeObject();
            },
            [MenuCommand.ToggleSkinLock] = delegate
            {
                if (_outliner.Selected is Mesh { Skinned: true } mesh)
                {
                    ChangeLock([mesh], !mesh.SkinnedMeshIsLocked);
                }
            },
            [MenuCommand.SetLayout] = delegate(MenuEntry entry)
            {
                if (MenuModel.TryParseLayout(entry.Argument, out var layout))
                {
                    SetLayout(layout);
                }
            },
            [MenuCommand.ToggleLayout] = delegate
            {
                ToggleLayout();
            },
            [MenuCommand.SetView] = delegate(MenuEntry entry)
            {
                string argument = entry.Argument;
                if (argument != null)
                {
                    SetView(argument);
                }
            },
            [MenuCommand.AddAnimatedCamera] = delegate
            {
                AddAnimatedCamera();
            },
            [MenuCommand.Import] = delegate
            {
                ImportAny();
            },
            [MenuCommand.Export] = delegate
            {
                ShowExportSettings();
            },
            [MenuCommand.About] = delegate
            {
                ShowAbout();
            },
            [MenuCommand.ShowWelcome] = async delegate
            {
                await ShowWelcome();
            },
            [MenuCommand.ShowLog] = delegate
            {
                ShowLogWindow();
            },
            [MenuCommand.CheckForUpdates] = async delegate
            {
                await CheckForUpdates();
            }
        });
        IReadOnlyList<MenuCommand> readOnlyList = MenuModel.Unhandled(CurrentMenuState(), dictionary.Keys);
        if (readOnlyList.Count > 0)
        {
            string text = "The menu commands " + string.Join(", ", readOnlyList) + " are not implemented in this build.";
            _status.Text = text;
            Console.Error.WriteLine(text);
        }
        return dictionary;
    }
}
