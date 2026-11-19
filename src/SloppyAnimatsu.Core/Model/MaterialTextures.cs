using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>One texture-swap key: at <see cref="Time"/> (frames) the material shows swap-list entry <see cref="Index"/>.</summary>
public readonly record struct TextureKey(int Time, int Index);

/// <summary>
/// The Material window's model: a list of textures a material
/// can switch between (facial expressions), their display names, the rest texture, transparency,
/// nearest filtering, and stepped texture-swap keys stored on the animation. All of it lives in
/// SloppyAnimatsu-only members (<see cref="MaterialSchema"/>); <c>Diffuse.Texture</c> is kept equal to
/// the rest texture so plain .akt readers, which ignore those members, still show it.
/// </summary>
public sealed partial class Material
{
    /// <summary>The image file types the swap list offers (the decoder decides what it can read).</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".avif", ".tga"];

    /// <summary>Whether the material carries its own swap list (otherwise the list is just the diffuse texture).</summary>
    public bool HasSwapList => Source[MaterialSchema.TexturesMember] is List<AktReference> { Count: > 0 };

    /// <summary>The textures to switch between; without a stored list, the diffuse texture alone.</summary>
    public IReadOnlyList<Texture> SwapTextures
    {
        get
        {
            if (Source[MaterialSchema.TexturesMember] is List<AktReference> { Count: > 0 } refs)
                return refs.Select(r => Model.Wrap(r) as Texture).OfType<Texture>().ToArray();
            return Diffuse.Texture is { } t ? [t] : [];
        }
    }

    /// <summary>The display name of swap-list entry <paramref name="index"/>: the stored name, else the file name without its extension.</summary>
    public string TextureName(int index)
    {
        if (Source[MaterialSchema.NamesMember] is List<object?> names && index < names.Count
            && names[index] is string { Length: > 0 } name)
            return name;
        var textures = SwapTextures;
        return index >= 0 && index < textures.Count ? DefaultName(textures[index].Name) : "";
    }

    /// <summary>The default display name of a texture file: its name without folder or extension.</summary>
    public static string DefaultName(string? fileName) =>
        Path.GetFileNameWithoutExtension(fileName ?? "") is { Length: > 0 } n ? n : fileName ?? "";

    /// <summary>The rest entry of the swap list (shown where no key applies), clamped to the list; -1 when empty.</summary>
    public int ActiveTextureIndex
    {
        get
        {
            int count = SwapTextures.Count;
            if (count == 0) return -1;
            int i = Source.GetOr(MaterialSchema.ActiveMember, 0);
            return Math.Clamp(i, 0, count - 1);
        }
    }

    /// <summary>Whether the texture's alpha is drawn (the collar); off draws the texture opaque.</summary>
    public bool Transparent => !SceneValues.Flag(Source, MaterialSchema.OpaqueMember);

    /// <summary>Nearest-texel sampling (Blender's "Closest"), for pixel-art textures.</summary>
    public bool NearestFilter => SceneValues.Flag(Source, MaterialSchema.NearestMember);

    /// <summary>
    /// The swap-list index shown at <paramref name="time"/> of <paramref name="animation"/>: the
    /// last key at or before it, else the first key's (a key holds until the next one, and
    /// before the first key the first key holds), else the rest entry.
    /// </summary>
    public int TextureIndexAt(Animation? animation, float time)
    {
        int rest = ActiveTextureIndex;
        if (animation == null || rest < 0) return rest;
        var keys = TextureKeys(animation);
        if (keys.Count == 0) return rest;
        int index = keys[0].Index;
        foreach (var key in keys)
            if (key.Time <= time + 0.0001f) index = key.Index;
        return Math.Clamp(index, 0, SwapTextures.Count - 1);
    }

    /// <summary>The texture shown at <paramref name="time"/> of <paramref name="animation"/> (null animation: the rest texture).</summary>
    public Texture? TextureAt(Animation? animation, float time)
    {
        var textures = SwapTextures;
        int i = TextureIndexAt(animation, time);
        return i >= 0 && i < textures.Count ? textures[i] : null;
    }

    /// <summary>This material's texture keys in <paramref name="animation"/>, by time.</summary>
    public IReadOnlyList<TextureKey> TextureKeys(Animation animation)
    {
        if (animation.Source[MaterialSchema.KeyMaterialsMember] is not List<AktReference> mats
            || animation.Source[MaterialSchema.KeyTimesMember] is not List<object?> times
            || animation.Source[MaterialSchema.KeyIndicesMember] is not List<object?> indices)
            return [];
        var keys = new List<TextureKey>();
        int n = Math.Min(mats.Count, Math.Min(times.Count, indices.Count));
        for (int i = 0; i < n; i++)
            if (ReferenceEquals(mats[i].Target, Source))
                keys.Add(new TextureKey(Convert.ToInt32(times[i]), Convert.ToInt32(indices[i])));
        return keys.OrderBy(k => k.Time).ToArray();
    }

    /// <summary>
    /// The materials with texture keys in <paramref name="animation"/>, in the order their first key
    /// was written: the timeline's texture rows.
    /// </summary>
    public static IReadOnlyList<Material> Keyed(Animation animation)
    {
        if (animation.Source[MaterialSchema.KeyMaterialsMember] is not List<AktReference> mats || mats.Count == 0)
            return [];
        var seen = new HashSet<AktObject>(ReferenceEqualityComparer.Instance);
        var result = new List<Material>();
        foreach (var reference in mats)
            if (reference.Target is { } target && seen.Add(target) && animation.Model.Wrap(target) is Material material)
                result.Add(material);
        return result;
    }

    /// <summary>
    /// Moves this material's key at <paramref name="from"/> to <paramref name="to"/>, replacing a key
    /// already at <paramref name="to"/>. Returns false when there is no key at <paramref name="from"/>.
    /// </summary>
    public bool MoveTextureKey(Animation animation, int from, int to)
    {
        if (from == to || animation.Source[MaterialSchema.KeyMaterialsMember] is not List<AktReference>) return false;
        var (mats, times, _) = KeyArrays(animation);
        int at = -1;
        for (int i = 0; i < mats.Count; i++)
            if (ReferenceEquals(mats[i].Target, Source) && Convert.ToInt32(times[i]) == from) { at = i; break; }
        if (at < 0) return false;
        RemoveTextureKey(animation, to);
        // Removing the key at the target may have shifted the moved one down.
        for (int i = 0; i < mats.Count; i++)
            if (ReferenceEquals(mats[i].Target, Source) && Convert.ToInt32(times[i]) == from) { times[i] = to; return true; }
        return false;
    }

    // ---- editing. An undoable caller runs EnsureMembers first, outside its undo snapshot. ----

    /// <summary>Gives the scene's schema, and every material and animation, the SloppyAnimatsu members.</summary>
    public void EnsureMembers() => MaterialSchema.EnsureFields(Model.Document.Schema, Model.Document.Roots);

    /// <summary>Turns the implicit list (the diffuse texture alone) into a stored one, so it can grow.</summary>
    private List<AktReference> StoredList()
    {
        EnsureMembers();
        if (Source[MaterialSchema.TexturesMember] is not List<AktReference> refs)
        {
            refs = [];
            Source.Set(MaterialSchema.TexturesMember, refs);
        }
        var names = Names();
        if (refs.Count == 0 && Diffuse.Texture is { } diffuse)
        {
            refs.Add(new AktReference(diffuse.Source));
            names.Clear();
            names.Add("");
        }
        while (names.Count < refs.Count) names.Add("");
        if (names.Count > refs.Count) names.RemoveRange(refs.Count, names.Count - refs.Count);
        return refs;
    }

    private List<object?> Names()
    {
        if (Source[MaterialSchema.NamesMember] is List<object?> names) return names;
        names = [];
        Source.Set(MaterialSchema.NamesMember, names);
        return names;
    }

    /// <summary>The scene's <c>__Textures</c> folder, created when missing.</summary>
    public Folder TexturesFolder()
    {
        var scene = Model.Scene;
        if (scene.Child("__Textures") is Folder f) return f;
        var source = Model.Document.Schema.NewObject("CDummy");
        source.Set("Name", "__Textures");
        scene.Source.AddChild(source);
        return (Folder)Model.Wrap(source);
    }

    /// <summary>
    /// Adds an image file to the swap list as a new <c>CTexture</c> under <c>__Textures</c>, shown as
    /// <paramref name="displayName"/> (default: the file name without extension). The first texture
    /// of an empty list becomes the rest texture. Returns the new entry's index.
    /// </summary>
    public int AddTexture(string fileName, byte[] data, string? displayName = null)
    {
        var refs = StoredList();
        var source = Model.Document.Schema.NewObject("CTexture");
        source.Set("Name", Path.GetFileName(fileName));
        source.Set("Data", data);
        TexturesFolder().Source.AddChild(source);
        refs.Add(new AktReference(source));
        Names().Add(displayName != null && displayName != DefaultName(fileName) ? displayName : "");
        if (refs.Count == 1) SetActiveTexture(0);
        return refs.Count - 1;
    }

    /// <summary>Adds an existing <c>CTexture</c> to the swap list (or finds it there); returns its index.</summary>
    public int AddExistingTexture(Texture texture, string? displayName = null)
    {
        var refs = StoredList();
        int existing = refs.FindIndex(r => ReferenceEquals(r.Target, texture.Source));
        if (existing >= 0) return existing;
        refs.Add(new AktReference(texture.Source));
        Names().Add(displayName != null && displayName != DefaultName(texture.Name) ? displayName : "");
        if (refs.Count == 1) SetActiveTexture(0);
        return refs.Count - 1;
    }

    /// <summary>
    /// Removes swap-list entry <paramref name="index"/>: keys showing it are deleted and later
    /// indices shift down in every animation of the scene. The <c>CTexture</c> stays in
    /// <c>__Textures</c> only while another material still uses it.
    /// </summary>
    public bool RemoveTexture(int index)
    {
        var refs = StoredList();
        if (index < 0 || index >= refs.Count) return false;
        var removed = refs[index].Target;
        int rest = ActiveTextureIndex;
        refs.RemoveAt(index);
        var names = Names();
        if (index < names.Count) names.RemoveAt(index);
        foreach (var animation in Model.Scene.Descendants<Animation>())
            ShiftKeys(animation, index);
        if (refs.Count == 0)
        {
            Source.Set(MaterialSchema.ActiveMember, 0);
            SetDiffuseTexture(null);
        }
        else SetActiveTexture(rest > index ? rest - 1 : Math.Min(rest, refs.Count - 1));
        if (removed != null && !IsUsed(removed)) removed.Parent?.RemoveChild(removed);
        return true;
    }

    /// <summary>Whether any material still shows or lists <paramref name="texture"/>.</summary>
    private bool IsUsed(AktObject texture) =>
        Model.Scene.Materials.Any(m =>
            (m.Source[MaterialSchema.TexturesMember] is List<AktReference> list && list.Any(r => ReferenceEquals(r.Target, texture)))
            || ColorChannels.Any(c => m.GetColor(c).Texture?.Source == texture)
            || ScalarChannels.Any(c => m.GetScalar(c).Texture?.Source == texture));

    private void ShiftKeys(Animation animation, int removedIndex)
    {
        if (animation.Source[MaterialSchema.KeyMaterialsMember] is not List<AktReference>) return;
        var (mats, times, indices) = KeyArrays(animation);
        for (int i = mats.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(mats[i].Target, Source)) continue;
            int k = Convert.ToInt32(indices[i]);
            if (k == removedIndex)
            {
                mats.RemoveAt(i);
                times.RemoveAt(i);
                indices.RemoveAt(i);
            }
            else if (k > removedIndex) indices[i] = k - 1;
        }
    }

    /// <summary>Renames swap-list entry <paramref name="index"/> (empty or the file name: back to the default).</summary>
    public bool RenameTexture(int index, string name)
    {
        var refs = StoredList();
        if (index < 0 || index >= refs.Count) return false;
        var names = Names();
        string stored = name.Trim();
        if (refs[index].Target is { } t && stored == DefaultName(t.Name)) stored = "";
        if ((names[index] as string ?? "") == stored) return false;
        names[index] = stored;
        return true;
    }

    /// <summary>Makes entry <paramref name="index"/> the rest texture, and the diffuse texture the app shows.</summary>
    public bool SetActiveTexture(int index)
    {
        var textures = SwapTextures;
        if (index < 0 || index >= textures.Count) return false;
        EnsureMembers();
        bool changed = Source.GetOr(MaterialSchema.ActiveMember, 0) != index || Diffuse.Texture != textures[index];
        Source.Set(MaterialSchema.ActiveMember, index);
        SetDiffuseTexture(textures[index]);
        return changed;
    }

    private void SetDiffuseTexture(Texture? texture) =>
        Source.Get<AktStruct>("Diffuse").Set("Texture", new AktReference(texture?.Source));

    public bool SetTransparent(bool transparent)
    {
        if (Transparent == transparent) return false;
        EnsureMembers();
        Source.Set(MaterialSchema.OpaqueMember, !transparent);
        return true;
    }

    public bool SetNearestFilter(bool nearest)
    {
        if (NearestFilter == nearest) return false;
        EnsureMembers();
        Source.Set(MaterialSchema.NearestMember, nearest);
        return true;
    }

    /// <summary>Keys entry <paramref name="index"/> at <paramref name="frame"/> of <paramref name="animation"/> (replacing a key there).</summary>
    public bool SetTextureKey(Animation animation, int frame, int index)
    {
        if (index < 0 || index >= SwapTextures.Count) return false;
        EnsureMembers();
        var (mats, times, indices) = KeyArrays(animation);
        for (int i = 0; i < mats.Count; i++)
            if (ReferenceEquals(mats[i].Target, Source) && Convert.ToInt32(times[i]) == frame)
            {
                if (Convert.ToInt32(indices[i]) == index) return false;
                indices[i] = index;
                return true;
            }
        mats.Add(new AktReference(Source));
        times.Add(frame);
        indices.Add(index);
        return true;
    }

    /// <summary>Deletes this material's texture key at <paramref name="frame"/>, if any.</summary>
    public bool RemoveTextureKey(Animation animation, int frame)
    {
        if (animation.Source[MaterialSchema.KeyMaterialsMember] is not List<AktReference>) return false;
        var (mats, times, indices) = KeyArrays(animation);
        for (int i = mats.Count - 1; i >= 0; i--)
            if (ReferenceEquals(mats[i].Target, Source) && Convert.ToInt32(times[i]) == frame)
            {
                mats.RemoveAt(i);
                times.RemoveAt(i);
                indices.RemoveAt(i);
                return true;
            }
        return false;
    }

    private static (List<AktReference> Mats, List<object?> Times, List<object?> Indices) KeyArrays(Animation animation)
    {
        var s = animation.Source;
        if (s[MaterialSchema.KeyMaterialsMember] is not List<AktReference> mats) s.Set(MaterialSchema.KeyMaterialsMember, mats = []);
        if (s[MaterialSchema.KeyTimesMember] is not List<object?> times) s.Set(MaterialSchema.KeyTimesMember, times = []);
        if (s[MaterialSchema.KeyIndicesMember] is not List<object?> indices) s.Set(MaterialSchema.KeyIndicesMember, indices = []);
        int n = Math.Min(mats.Count, Math.Min(times.Count, indices.Count));
        if (mats.Count > n) mats.RemoveRange(n, mats.Count - n);
        if (times.Count > n) times.RemoveRange(n, times.Count - n);
        if (indices.Count > n) indices.RemoveRange(n, indices.Count - n);
        return (mats, times, indices);
    }
}
