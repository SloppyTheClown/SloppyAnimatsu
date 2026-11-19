using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The hierarchy panel's row decoration
/// The kind a class takes, the glyph and colour of each kind, and the rule that
/// dims a hidden row unless it is selected.
/// </summary>
public class OutlinerStyleTests
{
    [SceneTheory, SceneSamples]
    public void KindOf_FollowsTheClassAndTheMeshesState(string path)
    {
        var model = TestFiles.LoadScene(path);
        Assert.All(Outliner.Rows(model.Scene), row =>
        {
            var kind = OutlinerStyle.KindOf(row.Object);
            switch (row.Object)
            {
                case Character { IsUnpickable: true }:
                    Assert.Equal(OutlinerKind.UnpickableCharacter, kind);
                    break;
                case Character:
                    Assert.Equal(OutlinerKind.Character, kind);
                    break;
                case Joint:
                    Assert.Equal(OutlinerKind.Joint, kind);
                    break;
                case Mesh { IsUnpickable: true }:
                    Assert.Equal(OutlinerKind.UnpickableMesh, kind);
                    break;
                case Mesh { Skinned: true, SkinnedMeshIsLocked: true }:
                    Assert.Equal(OutlinerKind.LockedSkinnedMesh, kind);
                    break;
                case Mesh { Skinned: true }:
                    Assert.Equal(OutlinerKind.SkinnedMesh, kind);
                    break;
                case Mesh:
                    Assert.Equal(OutlinerKind.Mesh, kind);
                    break;
                case ControllerReverseFoot:
                    Assert.Equal(OutlinerKind.ReverseFootController, kind);
                    break;
                case ControllerIK:
                    Assert.Equal(OutlinerKind.IkController, kind);
                    break;
                case ReverseFootLocator:
                    Assert.Equal(OutlinerKind.ShoeLocator, kind);
                    break;
                case Camera:
                    Assert.Equal(OutlinerKind.Camera, kind);
                    break;
                case CameraTarget:
                    Assert.Equal(OutlinerKind.CameraTarget, kind);
                    break;
                case AudioObject:
                    Assert.Equal(OutlinerKind.Audio, kind);
                    break;
                case Blendshape:
                    Assert.Equal(OutlinerKind.Blendshape, kind);
                    break;
                case BlendshapeController:
                    Assert.Equal(OutlinerKind.BlendshapeController, kind);
                    break;
                default:
                    Assert.Equal(OutlinerKind.Object, kind);
                    break;
            }
        });
    }

    [SceneTheory, SceneSamples]
    public void KindOf_ChangesWithTheMeshesLockAndPickableFlags(string path)
    {
        var model = TestFiles.LoadScene(path);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Skinned);
        var character = model.Scene.Characters.FirstOrDefault();
        if (mesh != null)
        {
            Assert.Equal(OutlinerKind.SkinnedMesh, OutlinerStyle.KindOf(mesh));
            mesh.SkinnedMeshIsLocked = true;
            Assert.Equal(OutlinerKind.LockedSkinnedMesh, OutlinerStyle.KindOf(mesh));
            mesh.IsUnpickable = true;
            Assert.Equal(OutlinerKind.UnpickableMesh, OutlinerStyle.KindOf(mesh));
            mesh.IsUnpickable = false;
            mesh.SkinnedMeshIsLocked = false;
            Assert.Equal(OutlinerKind.SkinnedMesh, OutlinerStyle.KindOf(mesh));
        }
        if (character != null)
        {
            Assert.Equal(OutlinerKind.Character, OutlinerStyle.KindOf(character));
            character.IsUnpickable = true;
            Assert.Equal(OutlinerKind.UnpickableCharacter, OutlinerStyle.KindOf(character));
        }
    }

    [SceneTheory, SceneSamples]
    public void For_DimsAHiddenRowAndKeepsASelectedOne(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = Outliner.Rows(model.Scene).Select(r => r.Object).FirstOrDefault(o => o.IsShown);
        if (target == null) return;
        var shown = OutlinerStyle.For(target);
        Assert.False(shown.Dimmed);
        Assert.Equal(OutlinerStyle.ColorOf(shown.Kind), shown.Color);

        target.IsShown = false;
        var hidden = OutlinerStyle.For(target);
        Assert.True(hidden.Dimmed);
        Assert.Equal(OutlinerStyle.Dim(OutlinerStyle.ColorOf(hidden.Kind)), hidden.Color);
        Assert.NotEqual(shown.Color, hidden.Color);

        // The app leaves a selected row's colour alone, so a hidden selected row does not look hidden
        var selected = OutlinerStyle.For(target, selected: true);
        Assert.False(selected.Dimmed);
        Assert.Equal(shown.Color, selected.Color);
    }

    [Fact]
    public void Dim_MixesHalfAndHalfWithTheDisabledColour()
    {
        // White against the theme's disabled grey: each byte is 127*128/255 + 255*127/255 = 190.
        Assert.Equal(0xFFBEBEBE, OutlinerStyle.Dim(0xFFFFFFFF));
        // The alpha is the sum of the two alphas, clamped, so an opaque base stays opaque.
        Assert.Equal(0xFFu, OutlinerStyle.Dim(0x00FFFFFF) >> 24);
        Assert.Equal(0xFFu, OutlinerStyle.Dim(0xFFFFFFFF) >> 24);
        // A colour is between the base and the disabled one, channel by channel (the integer
        // form floors twice, so a channel equal to the disabled one lands one unit below it).
        foreach (var kind in Enum.GetValues<OutlinerKind>())
        {
            uint baseColor = OutlinerStyle.ColorOf(kind);
            uint dimmed = OutlinerStyle.Dim(baseColor);
            for (int shift = 0; shift <= 16; shift += 8)
            {
                uint b = (baseColor >> shift) & 0xFF;
                uint d = (dimmed >> shift) & 0xFF;
                uint disabled = (OutlinerStyle.DisabledColor >> shift) & 0xFF;
                uint low = Math.Min(b, disabled);
                Assert.InRange(d, low > 2 ? low - 2 : 0, Math.Max(b, disabled));
            }
        }
    }

    [Fact]
    public void GlyphsAndColours_TellEveryKindApart()
    {
        var kinds = Enum.GetValues<OutlinerKind>();
        Assert.All(kinds, kind =>
        {
            string glyph = OutlinerStyle.GlyphOf(kind);
            // Path mini-language, optionally led by its fill rule (F1 = non-zero).
            Assert.Matches("^(F[01] )?M", glyph);
            Assert.EndsWith("Z", glyph);
            Assert.Equal(0xFFu, OutlinerStyle.ColorOf(kind) >> 24);
        });
        Assert.Equal(kinds.Length, kinds.Select(OutlinerStyle.GlyphOf).Distinct().Count());
        Assert.Equal(kinds.Length, kinds.Select(OutlinerStyle.ColorOf).Distinct().Count());
        Assert.Equal(kinds.Length, kinds.Select(OutlinerStyle.Describe).Distinct().Count());
    }

    [Fact]
    public void Channels_AreReadFromTheColour()
    {
        var style = new OutlinerRowStyle(OutlinerKind.Joint, "M0,0 Z", 0x80112233, Dimmed: true);
        Assert.Equal(((byte)0x11, (byte)0x22, (byte)0x33, (byte)0x80), style.Channels);
    }
}
