// Saving and the Anim Bank panel: New/Copy/Delete/Rename animation, stack create/duplicate/delete/
// move, folders, the bank item tree with its drag and drop, stack flags and the animation list.
//
// Bank actions run through RunBankEdit so a rename or a flag change is one undo record. The scripted
// --bank dispatcher is in MainWindow.BankCommands.cs.
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
    private async Task Save(bool choosePath)
    {
        if (_pose == null || _drag != null)
        {
            return;
        }
        string path = _path;
        if (choosePath || path == null)
        {
            path = (await base.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save scene",
                SuggestedStartLocation = await SceneFolder(forSave: true),
                //.slopa keeps everything;.akt is the format (asked first, SaveToAsync).
                SuggestedFileName = ((path != null) ? Path.GetFileNameWithoutExtension(path) : "scene") + SceneFiles.Extension,
                DefaultExtension = SceneFiles.Extension.TrimStart('.'),
                FileTypeChoices = [
                    new FilePickerFileType("Sloppy Animatsu scene")
                    {
                        Patterns = ["*" + SceneFiles.Extension]
                    },
                    new FilePickerFileType("akeytsu scene")
                    {
                        Patterns = ["*" + SceneFiles.AkeytsuExtension]
                    }
                ]
            }))?.TryGetLocalPath();
            if (path == null)
            {
                return;
            }
        }
        await SaveToAsync(path);
    }

    /// <summary>
    /// Saves on a worker thread behind a progress bar (<see cref="ProgressDialog"/>). An <c>.akt</c>
    /// is written in the plain .akt format, so <see cref="AkeytsuSaveDialog"/> says what is lost first;
    /// cancelling it cancels the save.
    /// </summary>
    private async Task<bool> SaveToAsync(string path)
    {
        if (SceneFiles.IsAkeytsuName(path) && !await AkeytsuSaveDialog.Ask(this, Path.GetFileName(path)))
        {
            _status.Text = "Save cancelled.";
            return false;
        }
        SceneModel model = _pose.Model;
        _play.IsChecked = false;
        try
        {
            ScenePaths.Remember(model, "SavePath", path);
            StoreViewState(model);
            await ProgressDialog.Run(this, "Saving", "Saving " + Path.GetFileName(path) + "...", progress => model.Save(path, progress));
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
        return Saved(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            SceneModel model = _pose.Model;
            ScenePaths.Remember(model, "SavePath", path);
            StoreViewState(model);
            model.Save(path);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
        return Saved(path);
    }

    /// <summary>
    /// Writes what the window is showing into the scene just before every save: the current
    /// character, its open animation and that animation's playhead frame. These never mark the scene
    /// dirty (changing them is not an edit), but any save, of a clean scene too, brings them up to
    /// date, so the file reopens where it was left.
    /// </summary>
    private void StoreViewState(SceneModel model)
    {
        StoreSceneViewCamera(model);
        if (_character == null) return;
        model.Scene.CurrentCharacter = _character;
        if (_animation == null || !_character.Animations.Contains(_animation)) return;
        _character.CurrentAnimation = _animation;
        _animation.Source.Set("CurrentTime", (int)MathF.Round(_time));
    }

    /// <summary>The window's side of a finished save: the document's path, name, title, tabs and recent list.</summary>
    private bool Saved(string path)
    {
        try
        {
            _document?.Document.Saved(path);
            _path = path;
            _documentName = Path.GetFileName(path);
            _dirty = false;
            _fileLabel.Text = Path.GetFileName(path);
            _status.Text = $"Saved {Path.GetFileName(path)} in {ScenePaths.Folder(path).TrimEnd('/')}.";
            UpdateTitle();
            UpdateTabs();
            RememberRecent(path);
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save " + Path.GetFileName(path) + ": " + ex.Message;
            return false;
        }
    }

    private void UpdateTitle()
    {
        string text = ((_path != null) ? Path.GetFileName(_path) : _documentName);
        base.Title = (string.IsNullOrEmpty(text) ? "Sloppy Animatsu" : ("Sloppy Animatsu — " + text + (_dirty ? " *" : "")));
        // The document tab shows the same mark (SceneDocument.Title), so the document follows the
        // window's flag at once, not only when another tab is chosen.
        if (_document != null && _document.Document.Dirty != _dirty)
        {
            _document.Dirty = _dirty;
            _document.Document.Dirty = _dirty;
            UpdateTabs();
        }
    }

    /// <summary>
    /// Runs a bank edit (an animation, a stack or a folder) as one key edit, then activates the
    /// animation the edit asks to show. Ignored while a drag is in progress, so a rename cannot
    /// race the tree.
    /// </summary>
    private void RunBankEdit(Action<PoseEditSession> edit, Func<Animation?>? show = null)
    {
        if (_drag != null || _timeline.IsDragging)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            _play.IsChecked = false;
            RunKeyEdit(delegate
            {
                edit(session);
            }, "Could not edit: ");
            Animation animation = show?.Invoke();
            if (animation != null && animation != _animation)
            {
                SelectAnimation(animation, null);
            }
        }
    }

    private void NewAnimation()
    {
        Animation created = null;
        RunBankEdit(delegate(PoseEditSession session)
        {
            created = session.CreateAnimation();
        }, () => created);
    }

    private void CopyAnimation()
    {
        Animation original = _animation;
        if (original != null)
        {
            Animation copy = null;
            RunBankEdit(delegate(PoseEditSession session)
            {
                copy = session.CopyAnimation(original);
            }, () => copy);
        }
    }

    private void DeleteAnimation()
    {
        Animation animation = _animation;
        if (animation == null) return;
        // Every animation selected in the bank goes, the current one included.
        List<SceneObject> doomed = SelectedBankAnimations().Cast<SceneObject>().ToList();
        if (!doomed.Contains(animation)) doomed.Insert(0, animation);
        DeleteBankRows(doomed);
    }

    /// <summary>
    /// The bank's one Delete (menu row and the Delete key): removes whatever rows are selected,
    /// folders with everything inside them and animations alike. With nothing selected it falls back
    /// to the highlighted folder, then the current animation. The whole delete is one undo step.
    /// </summary>
    private void DeleteBankSelection()
    {
        List<SceneObject> items = SelectedBankItems();
        if (items.Count == 0)
        {
            if (_bankFolder != null) items.Add(_bankFolder);
            else if (_animation != null) items.Add(_animation);
        }
        DeleteBankRows(items);
    }

    /// <summary>
    /// Deletes bank rows as one undo step (the session's DeleteBankItems). The row
    /// selected afterwards is the next one still listed (else the one before): a folder row is only
    /// highlighted, never opened, and a deleted current animation gives way to the next animation in
    /// bank order without opening the folder that holds it.
    /// </summary>
    private void DeleteBankRows(List<SceneObject> items)
    {
        if (items.Count == 0) return;
        // A row inside a selected folder goes with that folder.
        List<Folder> folders = items.OfType<Folder>().ToList();
        items = items.Where(item => !folders.Any(f => f != item && IsUnder(item, f))).ToList();
        bool Doomed(SceneObject o) => items.Contains(o) || folders.Any(f => IsUnder(o, f));

        List<SceneObject> listed = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?
            .Select(i => i.Value).OfType<SceneObject>().ToList() ?? [];
        int first = listed.FindIndex(Doomed);
        SceneObject? after = first < 0 ? null
            : listed.Skip(first).FirstOrDefault(o => !Doomed(o)) ?? listed.Take(first).LastOrDefault(o => !Doomed(o));

        List<Animation> all = _character?.Animations.ToList() ?? new List<Animation>();
        Animation? next = _animation;
        if (after is Animation listedNext) next = listedNext;
        else if (_animation != null && Doomed(_animation))
        {
            int index = all.IndexOf(_animation);
            next = all.Skip(index + 1).FirstOrDefault(a => !Doomed(a))
                ?? all.Take(Math.Max(index, 0)).LastOrDefault(a => !Doomed(a));
        }
        if (_bankFolder != null && Doomed(_bankFolder)) _bankFolder = null;

        RunBankEdit(session => session.DeleteBankItems(items));
        if (next != null && next != _animation) SelectAnimation(next, null, reveal: false);
        if (after is Folder folder)
        {
            _syncing = true;
            _animations.SelectedItem = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?
                .FirstOrDefault(i => i.Value == folder);
            _syncing = false;
            _bankFolder = _animations.SelectedItem != null ? folder : null;
            UpdateKeyCommands();
        }
    }

    private static bool IsUnder(SceneObject item, SceneObject folder)
    {
        for (var p = item.Source.Parent; p != null; p = p.Parent)
            if (p == folder.Source) return true;
        return false;
    }

    /// <summary>The bank's selected rows (animations and folders), in list order.</summary>
    private List<SceneObject> SelectedBankItems() =>
        _animations.SelectedItems?.OfType<Item<SceneObject>>().Select(i => i.Value).OfType<SceneObject>().ToList() ?? [];

    /// <summary>The bank's selected animations.</summary>
    private List<Animation> SelectedBankAnimations() => SelectedBankItems().OfType<Animation>().ToList();

    private async Task RenameAnimation()
    {
        Animation animation = _animation;
        if (animation == null)
        {
            return;
        }
        string name = await TextPromptDialog.Ask(this, "Rename animation", "Name", animation.Name ?? "");
        if (name != null && name != animation.Name)
        {
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.Rename(animation, name);
            });
        }
    }

    private void NewStack()
    {
        Animation animation = _animation;
        if (animation == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            string reason;
            Object3D[] targets = _selectedAll.Where((Object3D c) => session.Evaluator.IndexOf(c) >= 0 && KeyEditor.CanAddToStack(c, out reason)).ToArray();
            AnimStack activeStack = _timeline.ActiveStack;
            if (activeStack != null)
            {
                session.SetCurrentStack(activeStack);
            }
            AnimStack created = null;
            RunBankEdit(delegate(PoseEditSession s)
            {
                created = s.CreateStack(animation, (IReadOnlyCollection<Object3D>)(object)targets);
            });
            if (created != null)
            {
                _timeline.Select(created, Array.Empty<int>());
            }
        }
    }

    private void DuplicateStack()
    {
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session != null)
        {
            session.SetCurrentStack(stack);
            AnimStack copy = null;
            RunBankEdit(delegate(PoseEditSession s)
            {
                copy = s.DuplicateStack(stack);
            });
            if (copy != null)
            {
                _timeline.Select(copy, Array.Empty<int>());
            }
        }
    }

    private void DeleteStack()
    {
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        Animation animation = _animation;
        if (animation != null)
        {
            bool deleted = false;
            RunBankEdit(delegate(PoseEditSession session)
            {
                deleted = session.DeleteStack(stack);
            });
            if (!deleted)
            {
                _status.Text = "An animation keeps at least one stack.";
            }
            else
            {
                _timeline.Select(animation.CurrentStack, Array.Empty<int>());
            }
        }
    }

    private void MoveStack(int delta)
    {
        AnimStack stack = _timeline.ActiveStack;
        if (stack != null)
        {
            int[] keys = _timeline.SelectedKeys.ToArray();
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.MoveStack(stack, delta);
            });
            _timeline.Select(stack, keys);
        }
    }

    private void MoveStackTo(AnimStack stack, int index)
    {
        int[] keys = ((stack == _timeline.ActiveStack) ? _timeline.SelectedKeys.ToArray() : Array.Empty<int>());
        RunBankEdit(delegate(PoseEditSession session)
        {
            session.MoveStackTo(stack, index);
        });
        _timeline.Select(stack, keys);
    }

    private void NewFolder()
    {
        Folder created = null;
        RunBankEdit(delegate(PoseEditSession session)
        {
            created = session.CreateFolder();
        });
        if (created != null)
        {
            ShowBankFolder(created);
        }
    }

    private void DeleteFolder()
    {
        Folder folder = _bankFolder;
        if (folder != null)
        {
            _bankFolder = null;
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.DeleteFolder(folder);
            });
        }
    }

    private async Task RenameFolder()
    {
        Folder folder = _bankFolder;
        if (folder == null)
        {
            return;
        }
        string name = await TextPromptDialog.Ask(this, "Rename folder", "Name", folder.Name ?? "");
        if (name != null && name != folder.Name)
        {
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.Rename(folder, name);
            });
        }
    }

    private void MoveToFolder(IReadOnlyCollection<SceneObject> items, SceneObject target)
    {
        Folder bankFolder = _bankFolder;
        RunBankEdit(delegate(PoseEditSession session)
        {
            session.MoveToFolder(items, target);
        });
        if (bankFolder != null)
        {
            ShowBankFolder(bankFolder);
        }
    }

    private void ShowBankFolder(Folder folder)
    {
        _syncing = true;
        RevealInBank(folder);
        _animations.SelectedItem = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?.FirstOrDefault((Item<SceneObject> i) => i.Value == folder);
        _syncing = false;
        _bankFolder = ((_animations.SelectedItem != null) ? folder : null);
        UpdateKeyCommands();
    }

    // The bank folders whose contents are shown, by their stored object; every folder starts closed.
    private readonly HashSet<object> _bankExpanded = new HashSet<object>(ReferenceEqualityComparer.Instance);

    private static readonly IBrush BankFolderBrush = new SolidColorBrush(Color.FromUInt32(0xFFE6C05C));
    private static readonly IBrush BankAnimationBrush = new SolidColorBrush(Color.FromUInt32(0xFF7FB2E5));
    private static readonly IBrush BankRestPoseBrush = new SolidColorBrush(Color.FromUInt32(0xFF9E9E9E));

    // The bank's pictures, path data in a 12 x 12 box (SloppyAnimatsu's own drawing).
    private const string BankFolderGlyph = "M0.5,2 H4.6 L5.9,3.4 H11.5 V10.6 H0.5 Z";
    // The folder with a plus cut out of it: closed, click to open.
    private const string BankFolderClosedGlyph = BankFolderGlyph + " M5.3,4.8 H6.7 V6.3 H8.2 V7.7 H6.7 V9.2 H5.3 V7.7 H3.8 V6.3 H5.3 Z";
    // The folder with a minus cut out of it: open, click to close.
    private const string BankFolderOpenGlyph = BankFolderGlyph + " M3.8,6.3 H8.2 V7.7 H3.8 Z";
    // A frame round a play triangle: an animation.
    private const string BankAnimationGlyph = "M1,2 H11 V10 H1 Z M2.2,3.2 V8.8 H9.8 V3.2 Z M4.6,4.4 L7.8,6 L4.6,7.6 Z";
    // A figure standing in a T: the rest pose.
    private const string BankRestPoseGlyph = "M5,0.8 H7 V2.8 H5 Z M1,3.6 H11 V5 H7 V7.4 L8.4,11.2 H6.9 L6,8.4 L5.1,11.2 H3.6 L5,7.4 V5 H1 Z";

    /// <summary>
    /// The bank's visible rows: the rest pose, then the bank's folders and animations in the panel's
    /// sort order (<see cref="AnimBankView"/>; free sort is file order), a folder's contents only
    /// while it is open. Internal folders are flattened. A search keeps the rows whose name matches,
    /// the folders above them (opened) and everything inside a matching folder.
    /// </summary>
    private List<Item<SceneObject>> BankItems(Character? character)
    {
        List<Item<SceneObject>> items = new List<Item<SceneObject>>
        {
            new Item<SceneObject>(null, RestPoseLabel)
        };
        Folder folder = character?.AnimBank;
        string? query = string.IsNullOrWhiteSpace(_bankQuery) ? null : _bankQuery;
        if (folder != null)
        {
            Add(folder, 0, query == null);
        }
        return items;
        void Add(SceneObject parent, int depth, bool showAll)
        {
            foreach (SceneObject child in AnimBankView.Order(Rows(parent), BankSort, BankSortReversed))
            {
                if (!showAll && !AnimBankView.Shows(child, query))
                {
                    continue;
                }
                if (child is Animation animation)
                {
                    items.Add(new Item<SceneObject>(animation, animation.Name ?? "(unnamed)", depth));
                }
                else if (child is Folder folder2)
                {
                    bool open = query != null || _bankExpanded.Contains(folder2.Source);
                    items.Add(new Item<SceneObject>(folder2, folder2.Name ?? "(unnamed)", depth, open));
                    if (open)
                    {
                        Add(folder2, depth + 1, showAll || AnimBankView.Matches(folder2.Name, query));
                    }
                }
            }
        }
        // A folder's animations and user folders, with internal folders' contents in their place.
        static IEnumerable<SceneObject> Rows(SceneObject parent)
        {
            foreach (SceneObject child in parent.Children)
            {
                if (child is Folder inner && child.IsInternal)
                {
                    foreach (SceneObject c in Rows(inner)) yield return c;
                }
                else if (child is Animation or Folder)
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>Whether <paramref name="o"/> is (still) inside the current character's bank.</summary>
    private bool InBank(SceneObject? o)
    {
        Folder? bank = _character?.AnimBank;
        for (SceneObject? p = o; p != null; p = p.Parent)
        {
            if (p == bank)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Opens the folders above <paramref name="o"/> so its row is listed, and rebuilds the list when
    /// one was closed. The caller holds <c>_syncing</c>.
    /// </summary>
    private void RevealInBank(SceneObject? o)
    {
        Folder? bank = _character?.AnimBank;
        bool opened = false;
        for (SceneObject? p = o?.Parent; p != null && p != bank; p = p.Parent)
        {
            if (p is Folder && !p.IsInternal)
            {
                opened |= _bankExpanded.Add(p.Source);
            }
        }
        if (opened)
        {
            _animations.ItemsSource = BankItems(_character);
        }
    }

    /// <summary>
    /// Opens or closes a bank folder, one level (its sub-folders keep their own state), or with
    /// <paramref name="deep"/> the folder and every folder below it.
    /// </summary>
    private void ToggleBankFolder(Folder folder, bool deep = false)
    {
        _bankDragItem = null;
        _bankDragging = false;
        bool open = !_bankExpanded.Contains(folder.Source);
        foreach (Folder f in deep ? BankFolderTree(folder) : [folder])
        {
            if (open) _bankExpanded.Add(f.Source);
            else _bankExpanded.Remove(f.Source);
        }
        List<Item<SceneObject>> list = BankItems(_character);
        _syncing = true;
        _animations.ItemsSource = list;
        _animations.SelectedItem = list.FirstOrDefault((Item<SceneObject> i) => i.Value == (((object)_bankFolder) ?? ((object)_animation)));
        _syncing = false;
    }

    /// <summary>A folder and every folder below it.</summary>
    private static IEnumerable<Folder> BankFolderTree(Folder folder)
    {
        yield return folder;
        foreach (Folder child in folder.Children.OfType<Folder>())
        {
            foreach (Folder f in BankFolderTree(child)) yield return f;
        }
    }

    /// <summary>
    /// One bank row: indented by its depth, a picture (a folder with a plus or minus, an animation,
    /// the rest pose) and the name. The folder picture and a double click open or close the folder.
    /// </summary>
    private Control BankRow(Item<SceneObject>? item)
    {
        if (item == null)
        {
            return new TextBlock();
        }
        Folder? folder = item.Value as Folder;
        bool hasContent = folder != null && folder.Children.Any((SceneObject c) => c is Animation || c is Folder);
        (string glyph, IBrush brush, string tip) = item.Value switch
        {
            Folder when !hasContent => (BankFolderGlyph, BankFolderBrush, "Empty folder"),
            Folder => item.Expanded == true
                ? (BankFolderOpenGlyph, BankFolderBrush, "Folder - click to close")
                : (BankFolderClosedGlyph, BankFolderBrush, "Folder - click to open"),
            Animation => (BankAnimationGlyph, BankAnimationBrush, "Animation"),
            _ => (BankRestPoseGlyph, BankRestPoseBrush, "The rest pose (Rigging)"),
        };
        Border icon = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox
            {
                Width = 15.0,
                Height = 15.0,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse(glyph),
                    Fill = brush,
                    Width = 12.0,
                    Height = 12.0
                }
            }
        };
        ToolTip.SetTip(icon, tip);
        StackPanel row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5.0,
            Margin = new Thickness(item.Depth * 16, 0.0, 0.0, 0.0),
            Background = Brushes.Transparent,
            Children =
            {
                (Control)icon,
                (Control)new TextBlock
                {
                    Text = item.Label,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        if (folder != null && hasContent)
        {
            icon.Cursor = new Cursor(StandardCursorType.Hand);
            // Ctrl+click opens (or closes) the folder and every folder below it.
            icon.AddHandler(InputElement.PointerPressedEvent, delegate(object? _, PointerPressedEventArgs e)
            {
                if (e.GetCurrentPoint(icon).Properties.IsLeftButtonPressed)
                {
                    e.Handled = true;
                    _bankPressKeepsSelection = false;
                    _bankDragItem = null;
                    ToggleBankFolder(folder, e.KeyModifiers.HasFlag(KeyModifiers.Control));
                }
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            row.DoubleTapped += delegate(object? _, TappedEventArgs e)
            {
                e.Handled = true;
                ToggleBankFolder(folder);
            };
        }
        return row;
    }

    private static string AnimationPath(Animation a)
    {
        return string.Join("/", a.FolderPath.Append<string>(a.Name ?? "(unnamed)"));
    }

    private Item<SceneObject>? BankItemAt(PointerEventArgs e) => BankRowAt(e).Item;

    /// <summary>The row under the pointer, and how far down that row the pointer is (0 top, 1 bottom).</summary>
    private (Item<SceneObject>? Item, double Fraction) BankRowAt(PointerEventArgs e)
    {
        for (Visual visual = _animations.InputHitTest(e.GetPosition(_animations)) as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem row && visual.DataContext is Item<SceneObject> result)
            {
                double height = row.Bounds.Height;
                double y = e.GetPosition(row).Y;
                return (result, height > 0 ? Math.Clamp(y / height, 0.0, 1.0) : 0.5);
            }
        }
        return (null, 0.5);
    }

    /// <summary>
    /// Where a bank drag would land. In free sort a row's upper half places the moving items before
    /// it and its lower half after it, in the row's own folder; the middle half of a folder row (and
    /// any row in alphabetical sort) drops into that folder, as <see cref="DropTarget"/> says.
    /// <c>Place</c> is false for a drop into a folder (to its end).
    /// </summary>
    private (SceneObject? Target, SceneObject? Before, bool Place) BankDropAt(PointerEventArgs e, IReadOnlyCollection<SceneObject> moving)
    {
        (Item<SceneObject>? item, double f) = BankRowAt(e);
        SceneObject? over = item?.Value;
        if (BankSort != AnimBankSort.Free)
        {
            return (DropTarget(over), null, false);
        }
        Folder? root = _character?.AnimBank;
        if (root == null)
        {
            return (null, null, false);
        }
        if (item == null)
        {
            // Below the last row: the end of the bank.
            return (root, null, true);
        }
        if (over == null)
        {
            // The rest pose row: the start of the bank.
            return (root, NextPlace(root, null, moving), true);
        }
        if (over is Folder && f > 0.25 && f < 0.75)
        {
            return (over, null, false);
        }
        SceneObject? parent = over.Parent;
        if (parent == null)
        {
            return (null, null, false);
        }
        if (moving.Contains(over))
        {
            return (null, null, false);
        }
        if (f < 0.5)
        {
            return (parent, over, true);
        }
        return (parent, NextPlace(parent, over, moving), true);
    }

    /// <summary>The first child of <paramref name="parent"/> after <paramref name="after"/> (or the first, for null) that is not moving; null is the end.</summary>
    private static SceneObject? NextPlace(SceneObject parent, SceneObject? after, IReadOnlyCollection<SceneObject> moving)
    {
        bool passed = after == null;
        foreach (SceneObject child in parent.Children)
        {
            if (!passed)
            {
                passed = child == after;
                continue;
            }
            if (!moving.Contains(child))
            {
                return child;
            }
        }
        return null;
    }

    /// <summary>The bank rows a drag of <paramref name="dragged"/> moves: the selection when it holds the row, else that row.</summary>
    private IReadOnlyCollection<SceneObject> BankMoving(SceneObject dragged)
    {
        List<SceneObject> selected = SelectedBankItems();
        return selected.Contains(dragged) ? selected : [dragged];
    }

    private SceneObject? DropTarget(SceneObject? over)
    {
        SceneObject result = ((over is Folder folder) ? folder : ((!(over is Animation animation)) ? _character?.AnimBank : animation.Parent));
        return result;
    }

    private void BankPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _bankDragging = false;
        bool isLeftButtonPressed = e.GetCurrentPoint(_animations).Properties.IsLeftButtonPressed;
        bool flag = isLeftButtonPressed;
        SceneObject sceneObject = default(SceneObject);
        if (flag)
        {
            sceneObject = BankItemAt(e)?.Value;
            bool flag2 = ((sceneObject is Animation || sceneObject is Folder) ? true : false);
            flag = flag2;
        }
        _bankDragItem = (flag ? sceneObject : null);
        _bankDragStart = e.GetPosition(_animations);
        // A plain press on a row of a multi-selection keeps the selection so it can be dragged;
        // the release (without a drag) then narrows it to that row.
        _bankPressKeepsSelection = false;
        if (flag && sceneObject != null && e.KeyModifiers == KeyModifiers.None
            && (_animations.SelectedItems?.Count ?? 0) > 1 && SelectedBankItems().Contains(sceneObject))
        {
            _bankPressKeepsSelection = true;
            e.Handled = true;
        }
    }

    /// <summary>Set when a press landed on a row of a multi-selection and was held back from the ListBox.</summary>
    private bool _bankPressKeepsSelection;

    private void BankPointerMoved(object? sender, PointerEventArgs e)
    {
        SceneObject bankDragItem = _bankDragItem;
        if (bankDragItem != null)
        {
            PoseEditSession session = Session;
            if (session != null && (_bankDragging || !(Point.Distance(e.GetPosition(_animations), _bankDragStart) < 6.0)))
            {
                _bankDragging = true;
                IReadOnlyCollection<SceneObject> moving = BankMoving(bankDragItem);
                (SceneObject? sceneObject, SceneObject? before, bool place) = BankDropAt(e, moving);
                string reason = "No folder here.";
                bool flag = sceneObject != null && session.Bank.CanMove(bankDragItem, sceneObject, out reason);
                // A drop into the folder every moving row is already in changes nothing: no mark.
                bool changes = flag && (place || moving.Any(m => m.Parent != sceneObject));
                ShowBankDrag(e, bankDragItem, moving, sceneObject, place, changes);
                string what = moving.Count > 1 ? $"{moving.Count} items" : $"'{bankDragItem.Name}'";
                _status.Text = !flag ? reason
                    : !place ? $"Move {what} into '{sceneObject!.Name}'"
                    : before != null ? $"Place {what} before '{before.Name}'"
                    : $"Place {what} at the end of '{sceneObject!.Name}'";
            }
        }
    }

    private void BankPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        SceneObject bankDragItem = _bankDragItem;
        bool bankDragging = _bankDragging;
        _bankDragItem = null;
        _bankDragging = false;
        HideBankDrag();
        bool keptSelection = _bankPressKeepsSelection;
        _bankPressKeepsSelection = false;
        if (bankDragItem != null && !bankDragging && keptSelection)
        {
            // The held-back click: now select just that row, as a normal click would have.
            _animations.SelectedItems?.Clear();
            _animations.SelectedItem = (_animations.ItemsSource as IEnumerable<Item<SceneObject>>)?.FirstOrDefault(i => i.Value == bankDragItem);
            return;
        }
        if (bankDragItem == null || !bankDragging)
        {
            return;
        }
        PoseEditSession session = Session;
        if (session == null)
        {
            return;
        }
        e.Handled = true;
        (SceneObject? sceneObject, SceneObject? before, bool place) = BankDropAt(e, BankMoving(bankDragItem));
        if (sceneObject != null)
        {
            if (!session.Bank.CanMove(bankDragItem, sceneObject, out string reason))
            {
                _status.Text = reason;
            }
            else if (place)
            {
                // Free sort: reorder, the selection (where it can move) going together in bank order.
                List<SceneObject> selected = SelectedBankItems();
                IReadOnlyCollection<SceneObject> moving = selected.Contains(bankDragItem)
                    ? selected.Where(o => o != sceneObject && session.Bank.CanMove(o, sceneObject, out _)).ToList()
                    : [bankDragItem];
                PlaceInBank(moving.Count > 0 ? moving : [bankDragItem], sceneObject, before);
            }
            else if (bankDragItem.Parent == sceneObject)
            {
                _status.Text = BankSort == AnimBankSort.Free
                    ? $"'{bankDragItem.Name}' is already in '{sceneObject.Name}'."
                    : $"'{bankDragItem.Name}' is already in '{sceneObject.Name}'. Switch the bank to free sort to reorder it.";
            }
            else
            {
                // Dragging one of several selected rows moves the whole selection.
                List<SceneObject> selected = SelectedBankItems();
                IReadOnlyCollection<SceneObject> moving = selected.Contains(bankDragItem)
                    ? selected.Where(o => o != sceneObject && o.Parent != sceneObject && session.Bank.CanMove(o, sceneObject, out _)).ToList()
                    : [bankDragItem];
                MoveToFolder(moving.Count > 0 ? moving : [bankDragItem], sceneObject);
            }
        }
    }

    private async Task RenameStack()
    {
        AnimStack stack = _timeline.ActiveStack;
        if (stack == null)
        {
            return;
        }
        string name = await TextPromptDialog.Ask(this, "Rename stack", "Name", stack.Name ?? "");
        if (name != null && name != stack.Name)
        {
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.Rename(stack, name);
            });
        }
    }

    private void ToggleStackFlag(CheckBox box, uint flag)
    {
        if (_syncing)
        {
            return;
        }
        AnimStack stack = _timeline.ActiveStack;
        if (stack != null && (stack.Flags & flag) != 0 != (box.IsChecked == true))
        {
            RunBankEdit(delegate(PoseEditSession session)
            {
                session.ToggleStackFlag(stack, flag);
            });
        }
    }

    private void RefreshAnimations()
    {
        if (_character == null)
        {
            return;
        }
        List<Item<SceneObject>> list = BankItems(_character);
        // A folded-away row is still in the bank; only a removed one drops the choice.
        if (_bankFolder != null && !InBank(_bankFolder))
        {
            _bankFolder = null;
        }
        if (!(_animations.ItemsSource is IEnumerable<Item<SceneObject>> source) || !source.SequenceEqual(list))
        {
            _syncing = true;
            _animations.ItemsSource = list;
            _animations.SelectedItem = list.FirstOrDefault((Item<SceneObject> i) => i.Value == (((object)_bankFolder) ?? ((object)_animation)));
            _syncing = false;
            if (_animation != null && !InBank(_animation))
            {
                SelectAnimation(_character.CurrentAnimation ?? _character.Animations.FirstOrDefault(), null);
            }
        }
    }
}
