using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// A material's texture swap list, its rest texture, transparency and the stepped texture keys
///, on a copy of 19_run's first material.
/// </summary>
public class MaterialTexturesTests
{
    private static readonly byte[] PngA = [0x89, 0x50, 0x4e, 0x47, 1];
    private static readonly byte[] PngB = [0x89, 0x50, 0x4e, 0x47, 2];
    private static readonly byte[] PngC = [0x89, 0x50, 0x4e, 0x47, 3];

    private static SceneModel? Load() =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == "19_run.akt") is { } path
            ? TestFiles.LoadScene(path): null;

    [Fact]
    public void Adding_textures_names_them_after_the_file_and_the_first_becomes_the_rest_texture()
    {
        if (Load() is not { } model) return;
        var m = model.Scene.Materials.First();
        int a = m.AddTexture("smile.png", PngA);
        int b = m.AddTexture("frown.webp", PngB, "Frown face");
        int baseCount = a;

        Assert.Equal(baseCount + 1, b);
        Assert.Equal("smile", m.TextureName(a));
        Assert.Equal("Frown face", m.TextureName(b));
        Assert.True(m.RenameTexture(a, "Smile"));
        Assert.Equal("Smile", m.TextureName(a));
        Assert.True(m.RenameTexture(a, "smile"));       // the file name: back to the default
        Assert.Equal("smile", m.TextureName(a));
        Assert.True(m.SetActiveTexture(b));
        Assert.Equal(b, m.ActiveTextureIndex);
        Assert.Same(m.SwapTextures[b].Source, m.TextureAt(null, 0)!.Source);
    }

    [Fact]
    public void Texture_keys_are_stepped_and_the_first_key_holds_before_it()
    {
        if (Load() is not { } model) return;
        var m = model.Scene.Materials.First();
        var anim = model.Scene.Descendants<Animation>().First();
        int start = m.SwapTextures.Count;
        m.AddTexture("a.png", PngA);
        m.AddTexture("b.png", PngB);
        m.AddTexture("c.png", PngC);
        m.SetActiveTexture(start);
        Assert.Equal(start, m.TextureIndexAt(anim, 10));      // no keys: the rest texture

        Assert.True(m.SetTextureKey(anim, 5, start + 1));
        Assert.True(m.SetTextureKey(anim, 12, start + 2));
        Assert.Equal(start + 1, m.TextureIndexAt(anim, 0));   // before the first key
        Assert.Equal(start + 1, m.TextureIndexAt(anim, 11.9f));
        Assert.Equal(start + 2, m.TextureIndexAt(anim, 12));
        Assert.Equal(start, m.TextureIndexAt(null, 12));      // no animation: the rest texture

        // Removing an entry deletes its keys and shifts the later ones down.
        Assert.True(m.RemoveTexture(start + 1));
        var keys = m.TextureKeys(anim);
        Assert.Single(keys);
        Assert.Equal((12f, start + 1), (keys[0].Time, keys[0].Index));
        Assert.True(m.RemoveTextureKey(anim, 12));
        Assert.Empty(m.TextureKeys(anim));
    }

    [Fact]
    public void The_texture_layer_lists_keyed_materials_and_moves_their_keys()
    {
        if (Load() is not { } model) return;
        var m = model.Scene.Materials.First();
        var anim = model.Scene.Descendants<Animation>().First();
        int start = m.SwapTextures.Count;
        m.AddTexture("a.png", PngA);
        m.AddTexture("b.png", PngB);
        Assert.DoesNotContain(Material.Keyed(anim), k => ReferenceEquals(k.Source, m.Source));
        var layout = new TimelineLayout { Width = 800, Height = 400, Animation = anim };
        int rowsBefore = layout.RowCount;

        Assert.True(m.SetTextureKey(anim, 4, start));
        Assert.True(m.SetTextureKey(anim, 9, start + 1));
        Assert.Single(Material.Keyed(anim), k => ReferenceEquals(k.Source, m.Source));
        Assert.Equal(rowsBefore + 1, layout.RowCount);
        int row = layout.TextureRowStart;
        Assert.Same(m.Source, layout.TextureAt(row)!.Source);
        Assert.Equal(2, layout.KeyCountAt(row));

        // A press on the key's diamond picks it; the stack hit test leaves the row alone.
        var at = new System.Numerics.Vector2((float)layout.X(9), (float)(TimelineLayout.RowTop(row) + TimelineLayout.RowHeight / 2));
        var hit = layout.TextureKeyAt(at);
        Assert.Equal((row, 9), (hit.Row, hit.Key.Time));
        Assert.Equal(-1, layout.KeyAt(at).Key);

        // Moving onto another key replaces it: one key per material per frame.
        Assert.True(m.MoveTextureKey(anim, 9, 4));
        var keys = m.TextureKeys(anim);
        Assert.Single(keys);
        Assert.Equal((4, start + 1), (keys[0].Time, keys[0].Index));
        Assert.False(m.MoveTextureKey(anim, 30, 31));
    }

    [Fact]
    public void Swap_list_keys_and_flags_survive_a_save()
    {
        if (Load() is not { } model) return;
        var m = model.Scene.Materials.First();
        string name = m.Name;
        var anim = model.Scene.Descendants<Animation>().First();
        int start = m.SwapTextures.Count;
        m.AddTexture("a.png", PngA, "Neutral");
        m.AddTexture("b.png", PngB);
        m.SetActiveTexture(start + 1);
        m.SetTransparent(false);
        m.SetNearestFilter(true);
        m.SetTextureKey(anim, 7, start);

        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-mat-{Guid.NewGuid():N}.slopa");
        try
        {
            model.Save(path);
            var back = SceneModel.Load(path);
            Assert.Equal(AktFileKind.SloppyAnimatsu, back.Document.SourceFormat.Kind);
            var m2 = back.Scene.Materials.First(x => x.Name == name);
            var anim2 = back.Scene.Descendants<Animation>().First(x => x.Name == anim.Name);
            Assert.Equal(start + 2, m2.SwapTextures.Count);
            Assert.Equal("Neutral", m2.TextureName(start));
            Assert.Equal("b", m2.TextureName(start + 1));
            Assert.Equal(PngB, m2.SwapTextures[start + 1].Data);
            Assert.Equal(start + 1, m2.ActiveTextureIndex);
            Assert.False(m2.Transparent);
            Assert.True(m2.NearestFilter);
            Assert.Equal(start, m2.TextureIndexAt(anim2, 7));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_akt_save_is_the_plain_layout_without_the_app_only_members()
    {
        if (Load() is not { } model) return;
        var m = model.Scene.Materials.First();
        string name = m.Name;
        var anim = model.Scene.Descendants<Animation>().First();
        int start = m.SwapTextures.Count;
        m.AddTexture("a.png", PngA);
        m.AddTexture("b.png", PngB);
        m.SetTextureKey(anim, 7, start);

        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-mat-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.NotEqual("SLOPA"u8.ToArray(), bytes.Take(5).ToArray());
            var back = SceneModel.Load(path);
            Assert.Equal(AktFileKind.Akeytsu, back.Document.SourceFormat.Kind);
            Assert.DoesNotContain(back.Document.Schema.AllClasses.SelectMany(c => c.Members),
                member => AkeytsuCompatibility.OmittedMembers.Contains(member.Name));
            var m2 = back.Scene.Materials.First(x => x.Name == name);
            var anim2 = back.Scene.Descendants<Animation>().First(x => x.Name == anim.Name);
            Assert.True(m2.SwapTextures.Count < start + 2);
            Assert.Empty(m2.TextureKeys(anim2));
        }
        finally
        {
            File.Delete(path);
        }
    }
}