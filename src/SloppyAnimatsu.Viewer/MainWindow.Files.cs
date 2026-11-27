// File commands and documents: File > Import… (FBX, scenes, .aknm; export is MainWindow.Export.cs),
// About, New/Open/Load, the Welcome window, the document tab strip, and closing (with the
// per-document unsaved-changes prompt).
//
// SloppyAnimatsu.Core does the serialization and the dialogs only name a path; this file keeps the
// DocumentSet and the tab strip in step with the current DocumentState.
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
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Updates;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>
    /// File > Import…: one picker for every importable type, and the extension picks the importer:
    /// an FBX opens as a new scene, a scene (.slopa/.akt) is merged into the current one, and an
    /// .aknm brings its animations onto the selected character.
    /// </summary>
    private async Task ImportAny()
    {
        string[] fbx = ["*.fbx"], aknm = ["*.aknm"];
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import",
            FileTypeFilter = [
                new FilePickerFileType("All importable files") { Patterns = [.. fbx, .. SceneFiles.Patterns, .. aknm] },
                new FilePickerFileType("FBX (opens as a new scene)") { Patterns = fbx },
                new FilePickerFileType("Scene (.slopa, .akt), merged into this scene") { Patterns = SceneFiles.Patterns },
                new FilePickerFileType("Animation (.aknm), onto the selected character") { Patterns = aknm },
            ]
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) ImportFbx(path);
        else if (path.EndsWith(".aknm", StringComparison.OrdinalIgnoreCase)) ImportAknm(path);
        else if (SceneFiles.HasSceneExtension(path)) ImportScene(path);
        else _status.Text = "Cannot import " + Path.GetFileName(path) + ": choose an .fbx, .slopa, .akt or .aknm file.";
    }

    private void ImportFbx(string path)
    {
        int warnings = 0;
        try
        {
            SceneModel model = FbxImporter.Import(path, new FbxImportOptions
            {
                Warning = message =>
                {
                    warnings++;
                    Console.WriteLine(message);
                }
            });
            ShowDocument(_documents.Add(model, Path.GetFileNameWithoutExtension(path) + " (imported)"));
            _dirty = true;
            UpdateTitle();
            _status.Text = $"Imported {Path.GetFileName(path)} as a new scene with {warnings} warning(s); File > Save As keeps it.";
        }
        catch (Exception ex)
        {
            _status.Text = "FBX import failed: " + ex.Message;
        }
    }

    private void ImportScene(string path)
    {
        if (_pose == null)
        {
            _status.Text = "Open a scene before importing another one into it.";
            return;
        }
        int warnings = 0;
        try
        {
            AktImportResult result = AktImport.Import(_pose.Model, path, new AktImportOptions
            {
                Warning = message =>
                {
                    warnings++;
                    Console.WriteLine(message);
                }
            });
            if (_document != null) ShowDocument(_document.Document);
            _dirty = true;
            UpdateTitle();
            _status.Text = $"Imported {result.Characters.Count} character(s), {result.Materials.Count} material(s) and {result.Textures.Count} texture(s) from {Path.GetFileName(path)} ({warnings} warning(s)).";
        }
        catch (Exception ex)
        {
            _status.Text = "Scene import failed: " + ex.Message;
        }
    }

    private void ImportAknm(string path)
    {
        if (_pose == null || _character == null)
        {
            _status.Text = "Select a character to import animations onto.";
            return;
        }
        int warnings = 0;
        try
        {
            AknmImportResult result = AknmImport.Import(file: AknmFile.Read(path), model: _pose.Model, options: new AknmImportOptions
            {
                Character = _character,
                Warning = message =>
                {
                    warnings++;
                    Console.WriteLine(message);
                }
            });
            _dirty = true;
            UpdateTitle();
            SelectCharacter(_character, _animation?.Name, _time);
            _status.Text = $"Imported {result.Animations.Count} animation(s) from {Path.GetFileName(path)} into '{result.Destination.Name}'" + ((result.UnmatchedControllers > 0) ? $"; {result.UnmatchedControllers} controller(s) had no counterpart" : "") + ((warnings > 0) ? $"; {warnings} warning(s)" : "") + ".";
        }
        catch (Exception ex)
        {
            _status.Text = "AKNM import failed: " + ex.Message;
        }
    }

    private async Task ShowAbout()
    {
        // A local build (a build tree or the dev release folder marked with DEV_BUILD.txt) says so,
        // since its version number is only the one the next release will carry.
        bool devBuild = UpdateCheck.IsDevelopmentFolder(AppFolder.Install.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string version = devBuild ? $"Version {BuildInfo.Version} (development build)" : $"Version {BuildInfo.Version}";
        StackPanel panel = new StackPanel
        {
            Spacing = 10.0,
            Margin = new Thickness(24.0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        try
        {
            // The window icon doubles as the logo; a decode failure just leaves it out.
            panel.Children.Add(new Image
            {
                Source = new Bitmap(AssetLoader.Open(new Uri("avares://SloppyAnimatsu.Viewer/Assets/app.ico"))),
                Width = 96.0,
                Height = 96.0,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("About: the logo could not be loaded: " + ex.Message);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Sloppy Animatsu",
            FontSize = 22.0,
            FontWeight = FontWeight.DemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = version,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 380.0,
            Text = "A 3D character animation and rigging tool, developed with usability in mind, first and foremost."
        });
        Button close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
        };
        panel.Children.Add(close);
        Window window = new Window
        {
            Title = "About Sloppy Animatsu",
            Width = 440.0,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += delegate
        {
            window.Close();
        };
        await window.ShowDialog(this);
    }

    private static string Bytes(string path)
    {
        long length = new FileInfo(path).Length;
        return (length >= 1048576) ? $"{(double)length / 1048576.0:0.0} MB" : $"{(double)length / 1024.0:0} KB";
    }

    /// <summary>
    /// Where the scene pickers start: Save in the current file's folder, a new scene and Open in
    /// <c>Documents/SloppyAnimatsu</c> (created when missing). Null leaves the
    /// platform's default.
    /// </summary>
    private async Task<IStorageFolder?> SceneFolder(bool forSave)
    {
        string? folder = forSave && _path != null ? Path.GetDirectoryName(Path.GetFullPath(_path)) : null;
        if (folder == null || !Directory.Exists(folder))
        {
            folder = UserFolders.Documents;
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private async Task OpenFile()
    {
        IReadOnlyList<IStorageFile> files = await base.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open scene",
            SuggestedStartLocation = await SceneFolder(forSave: false),
            FileTypeFilter = [
                new FilePickerFileType("Scene (.slopa, .akt)")
                {
                    Patterns = SceneFiles.Patterns
                },
                FilePickerFileTypes.All
            ]
        });
        string path = default(string);
        int num;
        if (files.Count > 0)
        {
            path = files[0].TryGetLocalPath();
            num = ((path != null) ? 1 : 0);
        }
        else
        {
            num = 0;
        }
        if (num != 0)
        {
            Load(path);
        }
    }

    private SceneDocument? NewScene()
    {
        if (_drag != null)
        {
            return null;
        }
        try
        {
            int num = Math.Clamp(_preferences?.DefaultFrameRate ?? 60, 1, 240);
            SceneModel model = SceneFactory.NewSceneModel((uint)num);
            SceneDocument sceneDocument = _documents.New(model);
            ShowDocument(sceneDocument);
            _status.Text = $"New scene, {num} fps. Add a character with Import FBX, or open a scene.";
            return sceneDocument;
        }
        catch (Exception ex)
        {
            _status.Text = "Could not create a new scene: " + ex.Message;
            return null;
        }
    }

    private SceneDocument? Load(string path)
    {
        if (_drag != null)
        {
            return null;
        }
        try
        {
            SceneDocument active = _documents.Active;
            // The window keeps the active document's dirty flag (and its undo history) until it
            // switches tabs, so hand it over first: Open reuses only a clean, untouched New scene.
            if (active != null && ReferenceEquals(_document?.Document, active) && (_dirty || _history.CanUndo))
            {
                active.Dirty = true;
            }
            MemoryLog.Step("open " + Path.GetFileName(path));
            SceneModel model = SceneModel.Load(path);
            MemoryLog.Step("parsed");
            SceneDocument sceneDocument = _documents.Open(model, path);
            ShowDocument(sceneDocument);
            if (active != null && active != sceneDocument && !_documents.Documents.Contains(active))
            {
                _documentStates.Remove(active);
            }
            RememberRecent(path);
            ReleaseLoadBuffers();
            return sceneDocument;
        }
        catch (Exception ex)
        {
            _status.Text = "Could not load " + Path.GetFileName(path) + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Loading a large scene leaves hundreds of MB of freed parse buffers (the file bytes, the
    /// decoded payload) that the GC keeps reserved; once the first frame is drawn, one compacting
    /// collection hands them back to the system.
    /// </summary>
    private static void ReleaseLoadBuffers() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            MemoryLog.Step("first frame drawn, releasing load buffers");
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            MemoryLog.Step("load buffers released");
        }, Avalonia.Threading.DispatcherPriority.Background);

    private async Task ShowWelcome()
    {
        string path = await WelcomeDialog.Show(this, _preferences);
        if (path != null)
        {
            Load(path);
        }
    }

    private void ShowDocument(SceneDocument document)
    {
        bool flag = !_documentStates.ContainsKey(document);
        SaveDocumentState();
        if (!_documentStates.TryGetValue(document, out DocumentState state))
        {
            state = new DocumentState(document);
            _documentStates[document] = state;
        }
        _documents.Activate(document);
        _document = state;
        _pose = new ScenePose(document.Model);
        _pose.TargetLocked = target => CameraEditor.IsTargetLocked(target, _timeline.ActiveStack, (int)MathF.Round(_time));
        MemoryLog.Step("pose built");
        // The modeless retarget window edits one scene; switching to another closes it.
        if (_retargetDialog is { } retarget && !ReferenceEquals(retarget.Session.Model, document.Model))
            retarget.Close();
        _viewport.Renderer.Pose = _pose;
        _path = document.Path;
        _documentName = document.Name;
        // Showing a document is not an edit: whatever the set-up below marks, it ends as it came.
        bool documentDirty = document.Dirty;
        _dirty = documentDirty;
        AttachHistory(state.History);
        _sessions = state.Sessions;
        _selectedAll = state.Selected;
        _fileLabel.Text = document.Name;
        UpdateTitle();
        _status.Text = $"{_pose.Characters.Count} character(s), {document.Model.Scene.Meshes.Count()} mesh(es), {_pose.MeshVertices.Length / 11 / 3} triangles, {FrameRate} fps";
        _syncing = true;
        _characters.ItemsSource = _pose.Characters.Select((Character c) => new Item<Character>(c, c.Name ?? "(unnamed)")).ToList();
        _syncing = false;
        Character character = ((flag && _options.Character != null) ? _pose.Characters.FirstOrDefault((Character c) => c.Name == _options.Character) : null) ?? ((state.Character != null) ? _pose.Characters.FirstOrDefault((Character c) => c.Name == state.Character) : null) ?? document.Model.Scene.CurrentCharacter ?? _pose.Characters.FirstOrDefault((Character c) => c.Animations.Any()) ?? _pose.Characters.FirstOrDefault();
        string animationName = state.Animation ?? (flag ? _options.Animation : null) ?? ((document.Model.Scene.Context == InterfaceContext.Rigging) ? "(rest pose)" : null);
        MemoryLog.Step("character list");
        SelectCharacter(character, animationName, (!flag) ? state.Time : (_options.Frame ?? state.Time));
        MemoryLog.Step("character selected");
        _focusedView = 0;
        LoadViews();
        RefreshMenu();
        PluginsSceneChanged();
        MemoryLog.Step("views and menu");
        _outliner.Build(document.Model.Scene);
        MemoryLog.Step("outliner");
        _lastContext = document.Model.Scene.Context;
        ApplyDockLayout();
        MemoryLog.Step("dock layout");
        _dirty = documentDirty;
        UpdateTitle();
        UpdateTabs();
        if (state.CameraDistance > 0f)
        {
            OrbitCamera camera = _viewport.Renderer.Camera;
            camera.Target = state.CameraTarget;
            camera.Distance = state.CameraDistance;
            camera.Yaw = state.CameraYaw;
            camera.Pitch = state.CameraPitch;
            camera.FieldOfView = state.CameraFieldOfView;
        }
        else
        {
            _viewport.Renderer.FrameAll();
            RestoreSceneViewCamera(document.Model);
        }
        _viewport.RequestNextFrameRendering();
        MemoryLog.Mark("show document " + document.Name, _history.Count);
    }

    /// <summary>
    /// The scene's stored Perspective camera (<c>CameraList[0]</c>): the app keeps its perspective
    /// view's eye pose in that camera's <c>LTM</c> and the orbit distance in <c>_targetDistance</c>
    /// Null when the scene has none
    /// or it hangs under a parent.
    /// </summary>
    private static PerspectiveCamera? SceneViewCamera(SceneModel model) =>
        model.Scene.Cameras.Count > 0 && model.Scene.Cameras[0] is PerspectiveCamera camera
            && camera is not CameraAnimated && camera.Parent3D == null ? camera : null;

    /// <summary>
    /// Opens a scene on the perspective view it was saved with, as the app does, instead of the
    /// framed default. Leaves the framed view when the stored pose is empty (identity / no distance).
    /// </summary>
    private void RestoreSceneViewCamera(SceneModel model)
    {
        if (SceneViewCamera(model) is not { } stored) return;
        Matrix4x4 pose = stored.ViewPose;
        float distance = stored.TargetDistance;
        if (pose.Translation.LengthSquared() < 1e-8f || !(distance > 0f) || !float.IsFinite(distance)) return;
        // The renderer's own camera is the orbit of the first view on slot 0 (BuildViewStates); a
        // view on an orthographic slot (the Front/Left...) keeps its aligned flat orbit.
        OrbitCamera camera = _viewport.Renderer.Camera;
        if (!_viewStates.Any(s => ReferenceEquals(s.Orbit, camera))) return;
        camera.Restore(pose, distance);
        // Flat or perspective: SloppyAnimatsu's own scene member; plain .akt files have none (perspective).
        camera.Orthographic = model.Scene.Source.Has(MaterialSchema.ViewFlatMember)
            && model.Scene.Source[MaterialSchema.ViewFlatMember] is true;
        UpdateViewportViews();
    }

    /// <summary>
    /// Writes the main view (the focused view when it is an orbit view, else the renderer's orbit) into
    /// the scene before a save: its pose into the Perspective camera, flat or perspective into
    /// <c>CScene.MyAkViewFlat</c>, and that view's slot onto camera 0 so the next open restores it
    /// there. The file reopens looking where it was left.
    /// Not an edit.
    /// </summary>
    private void StoreSceneViewCamera(SceneModel model)
    {
        if (SceneViewCamera(model) is not { } stored) return;
        int index = _focusedView;
        if (index < 0 || index >= _viewStates.Count || CameraFor(_viewStates[index]) is not OrbitCamera)
            index = _viewStates.FindIndex(s => CameraFor(s) is OrbitCamera);
        if (index < 0 || _viewStates[index].Orbit is not { } camera) return;
        Matrix4x4 world = camera.WorldMatrix;
        if (!float.IsFinite(world.Translation.X) || !(camera.Distance > 0f)) return;
        stored.LocalMatrix = world;
        stored.Source.Set("_targetDistance", camera.Distance);
        MaterialSchema.EnsureFields(model.Document.Schema, model.Document.Roots);
        if (model.Scene.Source.Has(MaterialSchema.ViewFlatMember))
            model.Scene.Source.Set(MaterialSchema.ViewFlatMember, camera.Orthographic);
        ViewState state = _viewStates[index];
        if (state.CameraIndex != 0 && _sceneViews != null && model == _pose?.Model)
        {
            _sceneViews.SetCamera(_layout, index, 0);
            state.CameraIndex = 0;
        }
    }

    private void UpdateTabs()
    {
        if (_tabBar == null)
        {
            return;
        }
        _tabBar.Children.Clear();
        foreach (SceneDocument document in _documents.Documents)
        {
            SceneDocument target = document;
            bool flag = _documents.Active == document;
            Button obj = new Button
            {
                // A TextBlock, not the bare string: a Button's presenter reads '_' as an access-key
                // marker and dropped the first underscore of the file name.
                Content = new TextBlock { Text = document.Title },
                FontSize = 12.0,
                Padding = new Thickness(10.0, 3.0)
            };
            IBrush background;
            if (!flag)
            {
                IBrush transparent = Brushes.Transparent;
                background = transparent;
            }
            else
            {
                IBrush transparent = new SolidColorBrush(Color.Parse("#343B48"));
                background = transparent;
            }
            obj.Background = background;
            obj.FontWeight = (flag ? FontWeight.DemiBold : FontWeight.Normal);
            obj.Opacity = (flag ? 1.0 : 0.7);
            Button button = obj;
            if (!flag)
            {
                button.Opacity = 0.7;
            }
            button.Click += delegate
            {
                if (_documents.Active != target)
                {
                    ShowDocument(target);
                }
            };
            Button button2 = new Button
            {
                Content = "×",
                FontSize = 12.0,
                Padding = new Thickness(6.0, 3.0),
                Background = Brushes.Transparent,
                Opacity = 0.7
            };
            button2.Click += delegate
            {
                if (_documents.Active != target)
                {
                    ShowDocument(target);
                }
                CloseDocument();
            };
            var tab = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    (Control)button,
                    (Control)button2
                }
            };
            // A middle click anywhere on the tab does what its close box does.
            tab.AddHandler(InputElement.PointerReleasedEvent, delegate(object? _, PointerReleasedEventArgs e)
            {
                if (e.InitialPressMouseButton != MouseButton.Middle) return;
                e.Handled = true;
                if (_documents.Active != target)
                {
                    ShowDocument(target);
                }
                CloseDocument();
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            _tabBar.Children.Add(tab);
        }
    }

    private async Task CloseDocument()
    {
        DocumentState state = _document;
        if (state == null || _drag != null)
        {
            return;
        }
        SceneDocument document = state.Document;
        if (!_documents.CanClose(document))
        {
            return;
        }
        bool dirty = document.Dirty;
        bool flag = dirty;
        if (flag)
        {
            flag = !(await ConfirmClose(document));
        }
        if (!flag)
        {
            SaveDocumentState();
            _documentStates.Remove(document);
            _document = null;
            SceneDocument next = _documents.Close(document);
            UpdateTabs();
            if (next != null)
            {
                ShowDocument(next);
                _status.Text = $"Closed {document.Name}. {next.Name} is now shown.";
            }
            else
            {
                NewScene();
                _status.Text = "Closed " + document.Name + "; started a new scene. Add a character with Import FBX, or open a scene.";
            }
        }
    }

    /// <summary>Set once every unsaved document was saved or let go, so the second Close goes through.</summary>
    private bool _closeConfirmed;

    /// <summary>
    /// Closing the window with unsaved scene changes asks first, once per dirty document (the same
    /// Save / Don't save / Cancel prompt as closing its tab); Cancel, or a save that was cancelled,
    /// keeps the window open. A scripted run (--screenshot, --save-as) closes without asking.
    /// </summary>
    private void GuardUnsavedOnClose(WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _options.Scripted) return;
        SaveDocumentState();
        var dirty = _documents.Documents.Where(d => d.Dirty).ToList();
        if (dirty.Count == 0) return;
        e.Cancel = true;
        if (_drag != null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            foreach (SceneDocument document in dirty)
            {
                if (!_documents.Documents.Contains(document)) continue;
                if (_documents.Active != document) ShowDocument(document);
                if (!await ConfirmClose(document)) return;
            }
            _closeConfirmed = true;
            Close();
        });
    }

    private async Task<bool> ConfirmClose(SceneDocument document)
    {
        Button save = new Button
        {
            Content = "Save",
            IsDefault = true
        };
        Button discard = new Button
        {
            Content = "Don't save"
        };
        Button cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true
        };
        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0,
            Children = 
            {
                (Control)save,
                (Control)discard,
                (Control)cancel
            }
        };
        Window window = new Window
        {
            Title = "Close document",
            Width = 440.0,
            Height = 170.0,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 10.0,
                Margin = new Thickness(18.0),
                Children = 
                {
                    (Control)new TextBlock
                    {
                        Text = document.Name + " has unsaved changes. Save it before closing?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    (Control)buttons
                }
            }
        };
        int choice = 0;
        save.Click += delegate
        {
            choice = 1;
            window.Close();
        };
        discard.Click += delegate
        {
            choice = 2;
            window.Close();
        };
        cancel.Click += delegate
        {
            choice = 0;
            window.Close();
        };
        await window.ShowDialog(this);
        if (choice == 1)
        {
            await Save(choosePath: false);
            return !document.Dirty;
        }
        return choice == 2;
    }
}
