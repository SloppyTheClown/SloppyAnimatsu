// The Material panel: for the selected mesh, a preview sphere
// of one of its materials, the material's texture swap list (add from a file picker or by dropping
// image files, remove, rename), Transparent and Pixel filtering check boxes, and stepped texture
// keys on the current animation (facial expressions).
//
// Picking a row changes what the viewport shows now: without texture keys on the current animation
// it sets the rest texture; once that animation has keys for the material, it keys the row at the
// current frame. Every edit is one undo step through PoseEditSession.EditObjects.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private const int MatPreviewSize = 112;

    private StackPanel? _materialPanel;
    private readonly ComboBox _matSlot = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = PropRowHeight };
    private readonly Image _matPreview = new Image { Width = MatPreviewSize, Height = MatPreviewSize, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ListBox _matList = new ListBox { MinHeight = 90, MaxHeight = 220 };
    private readonly TextBox _matName = CompactField();
    private readonly Button _matAdd = new Button { Content = "Add…" };
    private readonly Button _matRemove = new Button { Content = "Remove" };
    private readonly CheckBox _matTransparent = new CheckBox { Content = "Transparent", MinHeight = PropRowHeight };
    private readonly CheckBox _matNearest = new CheckBox { Content = "Pixel filtering (nearest)", MinHeight = PropRowHeight };
    private readonly Button _matKey = new Button { Content = "Key" };
    private readonly Button _matUnkey = new Button { Content = "Delete key" };
    private readonly TextBlock _matKeys = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _matEmpty = new TextBlock { Text = "Select a mesh to edit its materials.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private StackPanel? _matBody;
    private bool _matSyncing;
    private Mesh? _matMesh;
    private (Texture? Texture, bool Transparent, bool Nearest, Material? Material) _matPreviewKey;
    private readonly Dictionary<Texture, DecodedImage?> _matDecoded = new(ReferenceEqualityComparer.Instance);

    private Control MaterialPanel => _materialPanel ??= BuildMaterialPanel();

    private Control MaterialBody()
    {
        RefreshMaterial();
        return MaterialPanel;
    }

    /// <summary>The mesh the panel shows: the current selection when it is a mesh.</summary>
    private Mesh? MaterialMesh => PropertiesTarget as Mesh;

    /// <summary>The material picked in the slot combo of <see cref="MaterialMesh"/>.</summary>
    private Material? MaterialTarget
    {
        get
        {
            if (MaterialMesh is not { } mesh) return null;
            var materials = mesh.Materials;
            if (materials.Count == 0) return null;
            int i = Math.Clamp(_matSlot.SelectedIndex, 0, materials.Count - 1);
            return materials[i];
        }
    }

    private StackPanel BuildMaterialPanel()
    {
        StackPanel panel = new StackPanel { Margin = new Thickness(8.0), Spacing = 4.0 };
        panel.Children.Add(_matEmpty);
        _matBody = new StackPanel { Spacing = 4.0 };
        _matBody.Children.Add(_matSlot);
        _matBody.Children.Add(_matPreview);
        _matBody.Children.Add(new TextBlock { Text = "Textures (drop image files here)", Opacity = 0.75 });
        _matBody.Children.Add(_matList);
        DockPanel nameRow = new DockPanel { LastChildFill = true };
        TextBlock nameLabel = new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        nameRow.Children.Add(_matName);
        _matBody.Children.Add(nameRow);
        _matBody.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4.0, Children = { _matAdd, _matRemove } });
        _matBody.Children.Add(_matTransparent);
        _matBody.Children.Add(_matNearest);
        _matBody.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4.0, Children = { _matKey, _matUnkey } });
        _matBody.Children.Add(_matKeys);
        panel.Children.Add(_matBody);
        ToolTip.SetTip(_matKey, "Key the selected texture at the current frame of the current animation");
        ToolTip.SetTip(_matUnkey, "Delete this material's texture key at the current frame");
        ToolTip.SetTip(_matTransparent, "Draw the texture's alpha (cut-outs, the collar); off draws it opaque");
        ToolTip.SetTip(_matNearest, "Sample the nearest texel (Blender's Closest), for pixel-art textures");

        _matSlot.SelectionChanged += (_, _) => { if (!_matSyncing) RefreshMaterial(); };
        _matList.SelectionChanged += (_, _) => { if (!_matSyncing) PickTexture(_matList.SelectedIndex); };
        _matName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTextureName(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshMaterial(); e.Handled = true; }
        };
        _matName.LostFocus += (_, _) => CommitTextureName();
        _matAdd.Click += async (_, _) => await AddTexturesFromPicker();
        _matRemove.Click += (_, _) => RemoveSelectedTexture();
        _matTransparent.IsCheckedChanged += (_, _) =>
        {
            if (_matSyncing || MaterialTarget is not { } m) return;
            bool on = _matTransparent.IsChecked == true;
            EditMaterial(m, $"{(on ? "Enable" : "Disable")} transparency on '{m.Name}'", [], () => m.SetTransparent(on));
        };
        _matNearest.IsCheckedChanged += (_, _) =>
        {
            if (_matSyncing || MaterialTarget is not { } m) return;
            bool on = _matNearest.IsChecked == true;
            EditMaterial(m, $"{(on ? "Nearest" : "Linear")} filtering on '{m.Name}'", [], () => m.SetNearestFilter(on));
        };
        _matKey.Click += (_, _) => KeySelectedTexture();
        _matUnkey.Click += (_, _) =>
        {
            if (MaterialTarget is not { } m || _animation is not { } a) return;
            int frame = CurrentKeyFrame;
            EditMaterial(m, $"Delete texture key of '{m.Name}' at frame {frame}", [a], () => m.RemoveTextureKey(a, frame));
        };

        DragDrop.SetAllowDrop(panel, true);
        panel.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (DroppedImages(e).Count > 0 && MaterialTarget != null)
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        });
        panel.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = DroppedImages(e);
            if (paths.Count == 0) return;
            e.Handled = true;
            AddTextureFiles(paths);
        });
        return panel;
    }

    private int CurrentKeyFrame => (int)MathF.Round(_time);

    /// <summary>Fills the panel from <see cref="MaterialMesh"/> and the current frame.</summary>
    /// <summary>
    /// Whether the Material panel is drawn: only while a mesh is selected, unless Material
    /// preferences "Show the Material panel without a selected mesh" is on.
    /// </summary>
    private bool MaterialPanelShown => MaterialMesh != null || _preferences?.MaterialPanelAlwaysShown == true;

    private void UpdateMaterialPanelVisibility()
    {
        bool shown = MaterialPanelShown;
        foreach (PanelView view in _panelViews)
            if (view.State.Key == PanelKeys.Material && view.Frame.IsVisible != shown)
                view.Frame.IsVisible = shown;
    }

    private void RefreshMaterial()
    {
        if (_materialPanel == null) return;
        Mesh? mesh = MaterialMesh;
        UpdateMaterialPanelVisibility();
        _matSyncing = true;
        try
        {
            var materials = mesh?.Materials ?? [];
            _matEmpty.IsVisible = materials.Count == 0;
            _matEmpty.Text = mesh == null ? "Select a mesh to edit its materials." : $"'{mesh.Name}' has no material.";
            if (_matBody != null) _matBody.IsVisible = materials.Count > 0;
            if (materials.Count == 0) { _matMesh = mesh; return; }

            string[] slots = materials.Select((m, i) => $"{i + 1}: {m.Name}").ToArray();
            if (!ReferenceEquals(mesh, _matMesh) || _matSlot.ItemsSource is not string[] oldSlots || !oldSlots.SequenceEqual(slots))
            {
                int keep = ReferenceEquals(mesh, _matMesh) ? Math.Clamp(_matSlot.SelectedIndex, 0, materials.Count - 1) : 0;
                _matSlot.ItemsSource = slots;
                _matSlot.SelectedIndex = keep;
                _matMesh = mesh;
            }
            _matSlot.IsVisible = materials.Count > 1;

            Material m = MaterialTarget!;
            var textures = m.SwapTextures;
            // Rebuilt only when a name changed: SetTime refreshes the panel on every played frame.
            string[] names = Enumerable.Range(0, textures.Count).Select(m.TextureName).ToArray();
            if (_matList.ItemsSource is not string[] old || !old.SequenceEqual(names)) _matList.ItemsSource = names;
            Animation? animation = KeyAnimationFor(m);
            int shown = m.TextureIndexAt(animation, _time);
            _matList.SelectedIndex = shown;
            _matName.Text = shown >= 0 ? m.TextureName(shown) : "";
            _matName.IsEnabled = shown >= 0;
            _matRemove.IsEnabled = shown >= 0;
            _matTransparent.IsChecked = m.Transparent;
            _matNearest.IsChecked = m.NearestFilter;
            _matKey.IsEnabled = animation != null && shown >= 0;
            var keys = animation != null ? m.TextureKeys(animation) : [];
            _matUnkey.IsEnabled = keys.Any(k => k.Time == CurrentKeyFrame);
            _matKeys.Text = animation == null
                ? "No animation of this mesh's character is current; picks set the rest texture."
                : keys.Count == 0
                    ? $"No texture keys in '{animation.Name}'; picks set the rest texture."
                    : $"Keys in '{animation.Name}': " + string.Join(", ", keys.Select(k => $"{k.Time} {m.TextureName(k.Index)}"));
            UpdateMaterialPreview(m, m.TextureAt(animation, _time));
        }
        finally
        {
            _matSyncing = false;
        }
    }

    /// <summary>
    /// The animation texture keys go into: the current one, when the mesh belongs to the current
    /// character (the renderer applies keys only there, ScenePose.Update).
    /// </summary>
    private Animation? KeyAnimationFor(Material m)
    {
        if (_animation == null || _character == null || MaterialMesh is not { } mesh) return null;
        Character? owner = mesh.Ancestor<Character>();
        return owner == null || ReferenceEquals(owner, _character) ? _animation : null;
    }

    private void PickTexture(int index)
    {
        if (MaterialTarget is not { } m || index < 0) return;
        Animation? a = KeyAnimationFor(m);
        if (a != null && m.TextureKeys(a).Count > 0)
        {
            if (m.TextureIndexAt(a, _time) == index) return;
            int frame = CurrentKeyFrame;
            EditMaterial(m, $"Key texture '{m.TextureName(index)}' of '{m.Name}' at frame {frame}", [a], () => m.SetTextureKey(a, frame, index));
        }
        else
        {
            if (m.ActiveTextureIndex == index) { RefreshMaterial(); return; }
            EditMaterial(m, $"Show texture '{m.TextureName(index)}' on '{m.Name}'", [], () => m.SetActiveTexture(index));
        }
    }

    /// <summary>The timeline's texture layer moved a key.</summary>
    private void MoveTextureKey(Material m, int from, int to)
    {
        if (_animation is not { } a || from == to) return;
        EditMaterial(m, $"Move texture key of '{m.Name}' from frame {from} to {to}", [a], () => m.MoveTextureKey(a, from, to));
    }

    private void KeySelectedTexture()
    {
        if (MaterialTarget is not { } m || KeyAnimationFor(m) is not { } a) return;
        int index = _matList.SelectedIndex;
        if (index < 0) return;
        int frame = CurrentKeyFrame;
        EditMaterial(m, $"Key texture '{m.TextureName(index)}' of '{m.Name}' at frame {frame}", [a], () => m.SetTextureKey(a, frame, index));
    }

    private void CommitTextureName()
    {
        if (_matSyncing || MaterialTarget is not { } m) return;
        int index = _matList.SelectedIndex;
        if (index < 0) return;
        string name = (_matName.Text ?? "").Trim();
        if (name == m.TextureName(index)) return;
        EditMaterial(m, $"Rename texture '{m.TextureName(index)}' to '{(name.Length > 0 ? name : "(file name)")}'", [], () => m.RenameTexture(index, name));
    }

    private void RemoveSelectedTexture()
    {
        if (MaterialTarget is not { } m) return;
        int index = _matList.SelectedIndex;
        if (index < 0) return;
        string name = m.TextureName(index);
        EditMaterial(m, $"Remove texture '{name}' from '{m.Name}'", KeyedAnimations(m), () => m.RemoveTexture(index));
    }

    private async Task AddTexturesFromPicker()
    {
        if (MaterialTarget == null) return;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add textures",
            AllowMultiple = true,
            FileTypeFilter = [
                new FilePickerFileType("Images") { Patterns = Material.ImageExtensions.Select(e => "*" + e).ToArray() },
                FilePickerFileTypes.All
            ]
        });
        AddTextureFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray());
    }

    /// <summary>Adds image files to the material's swap list as one undo step; undecodable files are refused.</summary>
    private void AddTextureFiles(IReadOnlyList<string> paths)
    {
        if (MaterialTarget is not { } m || paths.Count == 0) return;
        var accepted = new List<(string Name, byte[] Data)>();
        var refused = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (ImageDecoding.Decode(data) is { Width: > 0 }) accepted.Add((Path.GetFileName(path), data));
                else refused.Add(Path.GetFileName(path));
            }
            catch (Exception)
            {
                refused.Add(Path.GetFileName(path));
            }
        }
        if (accepted.Count > 0)
        {
            string what = accepted.Count == 1 ? $"'{accepted[0].Name}'" : $"{accepted.Count} textures";
            EditMaterial(m, $"Add {what} to '{m.Name}'", [], () =>
            {
                foreach (var (name, data) in accepted) m.AddTexture(name, data);
                // Material preferences "Pixel filtering (nearest) for added textures".
                if (_preferences?.NearestFilterForNewTextures == true) m.SetNearestFilter(true);
                return true;
            });
        }
        if (refused.Count > 0)
            _status.Text = $"Could not read {string.Join(", ", refused)} as an image" + (accepted.Count > 0 ? $"; added {accepted.Count}." : ".");
    }

    private static IReadOnlyList<string> DroppedImages(DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return [];
        return (e.Data.GetFiles() ?? [])
            .Select(i => i.TryGetLocalPath()).OfType<string>()
            .Where(p => Material.ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private IReadOnlyList<Animation> KeyedAnimations(Material m) =>
        _pose?.Model.Scene.Descendants<Animation>().Where(a => m.TextureKeys(a).Count > 0).ToArray() ?? [];

    /// <summary>
    /// One material edit: an undo step over the material, the scene, its <c>__Textures</c> folder
    /// and <paramref name="animations"/> when a session exists, else a direct edit.
    /// </summary>
    private void EditMaterial(Material m, string description, IReadOnlyList<Animation> animations, Func<bool> edit)
    {
        if (_pose == null) return;
        // The schema members are added outside the snapshot: undo restores values, not the schema.
        m.EnsureMembers();
        var objects = new List<SceneObject> { m, _pose.Model.Scene, m.TexturesFolder() };
        objects.AddRange(animations);
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
        RefreshMaterial();
        // The texture layer's rows in the timeline and the Mixer follow the keys.
        _timeline.Refresh();
        RefreshMixer();
        _viewport.RequestNextFrameRendering();
    }

    // ---- the preview sphere, drawn on the CPU (equirectangular UVs, one light) ----

    private void UpdateMaterialPreview(Material m, Texture? texture)
    {
        var key = (texture, m.Transparent, m.NearestFilter, m);
        if (_matPreview.Source != null && key == _matPreviewKey) return;
        _matPreviewKey = key;
        DecodedImage? image = null;
        if (texture != null && !_matDecoded.TryGetValue(texture, out image))
        {
            try { image = texture.Data.Length > 0 ? ImageDecoding.Decode(texture.Data) : null; }
            catch (Exception) { image = null; }
            _matDecoded[texture] = image;
        }
        var color = m.Diffuse.Value;
        int size = _preferences?.MaterialPreviewPixels ?? Preferences.DefaultMaterialPreviewSize;
        _matPreview.Width = _matPreview.Height = size;
        _matPreview.Source = RenderSphere(image, color, m.Transparent, m.NearestFilter, size);
    }

    private static WriteableBitmap RenderSphere(DecodedImage? image, System.Numerics.Vector4 color, bool transparent, bool nearest, int size)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        byte[] pixels = new byte[size * size * 4];
        var light = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-0.5f, 0.6f, 0.8f));
        float radius = size / 2f - 1f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = (x + 0.5f - size / 2f) / radius, ny = (size / 2f - y - 0.5f) / radius;
            float r2 = nx * nx + ny * ny;
            int o = (y * size + x) * 4;
            if (r2 > 1f) continue; // outside: fully transparent
            float nz = MathF.Sqrt(1f - r2);
            // Checkerboard behind the sphere, so transparency reads.
            float bg = ((x / 8 + y / 8) & 1) == 0 ? 0.55f : 0.35f;
            float r = color.X, g = color.Y, b = color.Z, a = 1f;
            if (image != null)
            {
                float u = 0.5f + MathF.Atan2(nx, nz) / (2f * MathF.PI);
                float v = 0.5f - MathF.Asin(Math.Clamp(ny, -1f, 1f)) / MathF.PI;
                (r, g, b, a) = Sample(image, u, v, nearest);
            }
            if (!transparent) a = 1f;
            float shade = 0.25f + 0.75f * Math.Max(0f, System.Numerics.Vector3.Dot(new(nx, ny, nz), light));
            r = r * shade * a + bg * (1 - a);
            g = g * shade * a + bg * (1 - a);
            b = b * shade * a + bg * (1 - a);
            // Soft edge: one pixel of coverage.
            float cover = Math.Clamp((1f - MathF.Sqrt(r2)) * radius, 0f, 1f);
            pixels[o + 0] = (byte)(Math.Clamp(b, 0, 1) * cover * 255);
            pixels[o + 1] = (byte)(Math.Clamp(g, 0, 1) * cover * 255);
            pixels[o + 2] = (byte)(Math.Clamp(r, 0, 1) * cover * 255);
            pixels[o + 3] = (byte)(cover * 255);
        }
        using (var frame = bitmap.Lock())
            for (int y = 0; y < size; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * size * 4, frame.Address + y * frame.RowBytes, size * 4);
        return bitmap;
    }

    private static (float R, float G, float B, float A) Sample(DecodedImage image, float u, float v, bool nearest)
    {
        int w = image.Width, h = image.Height;
        u -= MathF.Floor(u);
        v = Math.Clamp(v, 0f, 1f);
        if (nearest)
            return Texel(image, Math.Min((int)(u * w), w - 1), Math.Min((int)(v * h), h - 1));
        float fx = u * w - 0.5f, fy = v * h - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        var c00 = Texel(image, Wrap(x0, w), Math.Clamp(y0, 0, h - 1));
        var c10 = Texel(image, Wrap(x0 + 1, w), Math.Clamp(y0, 0, h - 1));
        var c01 = Texel(image, Wrap(x0, w), Math.Clamp(y0 + 1, 0, h - 1));
        var c11 = Texel(image, Wrap(x0 + 1, w), Math.Clamp(y0 + 1, 0, h - 1));
        float L(float a, float b, float c, float d) => (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        return (L(c00.R, c10.R, c01.R, c11.R), L(c00.G, c10.G, c01.G, c11.G), L(c00.B, c10.B, c01.B, c11.B), L(c00.A, c10.A, c01.A, c11.A));

        static int Wrap(int i, int n) => ((i % n) + n) % n;
    }

    private static (float R, float G, float B, float A) Texel(DecodedImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        var p = image.Rgba;
        return (p[i] / 255f, p[i + 1] / 255f, p[i + 2] / 255f, p[i + 3] / 255f);
    }
}
