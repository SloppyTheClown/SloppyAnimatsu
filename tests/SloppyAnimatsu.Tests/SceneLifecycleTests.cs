using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The scene lifecycle the File menu offers: File > New
/// Scene, the recent-scene list of <c>preferences.akt</c>, and the two-slot autosave.
/// </summary>
public class SceneLifecycleTests
{
    [Fact]
    public void NewScene_HasTheExpectedObjects()
    {
        var model = SceneFactory.NewSceneModel(60);
        var scene = model.Scene;

        Assert.Equal("Scene", scene.Name);
        Assert.Equal(60u, scene.FrameRate);
        Assert.Equal(1, scene.InterfaceContextIndex); // a new scene opens in the Animation context
        Assert.Equal(8, scene.Cameras.Count);
        Assert.Equal(
            ["Perspective", "Front", "Back", "Right", "Left", "Top", "Bottom", "Photo"],
            scene.Cameras.Select(c => c.Name));
        Assert.Equal(
            ["CCamera", "CCameraOrtho", "CCameraOrtho", "CCameraOrtho", "CCameraOrtho", "CCameraOrtho", "CCameraOrtho", "CCamera"],
            scene.Cameras.Select(c => c.ClassName));

        var character = Assert.Single(scene.Characters);
        Assert.Equal("Default character", character.Name);
        Assert.Same(character, scene.CurrentCharacter);
        Assert.Empty(character.Joints);
        Assert.Empty(character.Controllers);
        Assert.Empty(character.Animations);
        Assert.Equal(
            ["__BlendshapeControllers", "__Anims", "__BindPoses", "__RefPose", "__RefPoseOld", "__RigAnim"],
            character.Children.Select(c => c.Name));
        Assert.NotNull(character.RefPose);
        Assert.Same(character.RefPose, character.CurrentPose);
        Assert.NotNull(character.RefPoseOld);
        Assert.Empty(Assert.IsType<CharacterPose>(character.RefPose).Matrices);
        Assert.Equal(0, character.RefPose!.Count);

        var rig = Assert.IsType<Animation>(character.RigAnimation);
        var stack = Assert.Single(rig.Stacks);
        Assert.Equal("New Stack", stack.Name);
        Assert.Equal(2, stack.Keys.Count);
        Assert.All(stack.Keys, k => Assert.Equal(0, k.Time));
        Assert.Equal(AnimationBankEditor.EnabledFlag, stack.Flags);
        Assert.Equal(1f, stack.Opacity);
        Assert.Equal(0.5f, stack.Tweenage);
        Assert.Equal(new byte[] { 0x99, 0x99, 0x99, 0xFF }, stack.Source.Get<byte[]>("Color"));
    }

    [Fact]
    public void NewScene_UsesTheRequestedFrameRate()
    {
        Assert.Equal(30u, SceneFactory.NewSceneModel(30).Scene.FrameRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneFactory.NewScene(0));
    }

    [Fact]
    public void NewScene_SavesAndReloadsByteExact()
    {
        string first = TempFile("new-scene");
        string second = TempFile("new-scene");
        try
        {
            new SceneModel(SceneFactory.NewScene()).Save(first);
            var reloaded = TestFiles.LoadScene(first);
            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    /// <summary>
    /// The autosave of a brand-new scene is the reference output. It matches byte for
    /// byte once the members the app leaves uninitialised are zeroed on both sides (its own
    /// <c>New_1</c> and <c>New_3</c> disagree in them too).
    /// </summary>
    [Fact]
    public void NewScene_MatchesTheReferenceNewScene()
    {
        if (TestFiles.NewSceneAutosave == null) return;

        var reference = AktDocument.Load(TestFiles.NewSceneAutosave);
        uint frameRate = reference.Root.Get<uint>("FrameRate");
        var ours = SceneFactory.NewScene(frameRate);
        // SloppyAnimatsu's one deliberate difference: new scenes start at the user's Decorators joint
        // factors (5/6, what an FBX import writes) instead of 2/1.
        Assert.Equal(5f, ours.Root.Get<float>("JointRenderSize"));
        Assert.Equal(6f, ours.Root.Get<float>("JointRenderSizeHighDensity"));
        ours.Root.Set("JointRenderSize", reference.Root.Get<float>("JointRenderSize"));
        ours.Root.Set("JointRenderSizeHighDensity", reference.Root.Get<float>("JointRenderSizeHighDensity"));
        // The reference is a live autosave: its context is whichever one the user was in when it was
        // written (seen as both 0 and 1). Ours always opens in Animation, checked in NewScene_HasTheExpectedObjects.
        Assert.Equal((byte)1, ours.Root.Get<byte>("InterfaceContextIndex"));
        ours.Root.Set("InterfaceContextIndex", reference.Root.Get<byte>("InterfaceContextIndex"));
        foreach (var doc in new[] { reference, ours }) NormalizeUninitialized(doc);

        Assert.Equal(AktWriter.Write(reference), AktWriter.Write(ours));
    }

    [Fact]
    public void NewScene_FramesADefaultGridInsteadOfCollapsingTheCamera()
    {
        var model = SceneFactory.NewSceneModel();
        var pose = new SloppyAnimatsu.Rendering.ScenePose(model);
        var renderer = new SloppyAnimatsu.Rendering.SceneRenderer { Pose = pose };

        renderer.FrameAll();

        Assert.True(renderer.Camera.Distance > 1f,
            "an empty scene must frame a default area, not the zero-size bounds at the origin");
    }

    [Fact]
    public void NewScene_IsNotAPreferencesFile()
    {
        string path = TempFile("not-preferences");
        try
        {
            new SceneModel(SceneFactory.NewScene()).Save(path);
            Assert.Throws<InvalidDataException>(() => Preferences.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecentFiles_AreNewestFirstCappedAndDeduplicated()
    {
        var list = new List<string>();
        for (int i = 0; i < 12; i++) list = Preferences.Updated(list, $"scene{i}.akt");

        Assert.Equal(Preferences.MaxRecentFiles, list.Count);
        Assert.Equal("scene11.akt", list[0]);
        Assert.Equal("scene2.akt", list[^1]);
        Assert.DoesNotContain("scene0.akt", list);
        Assert.DoesNotContain("scene1.akt", list);

        list = Preferences.Updated(list, "scene5.akt");
        Assert.Equal("scene5.akt", list[0]);
        Assert.Equal(1, list.Count(p => p == "scene5.akt"));
        Assert.Equal(Preferences.MaxRecentFiles, list.Count);

        // Exact strings only: a different spelling of the same path is a different entry.
        list = Preferences.Updated(list, "scene5.akt/");
        Assert.Equal(2, list.Count(p => p.StartsWith("scene5.akt", StringComparison.Ordinal)));
        Assert.Equal(Preferences.MaxRecentFiles, list.Count);
    }

    [Fact]
    public void Preferences_ReadsTheSavedValues()
    {
        if (TestFiles.Preferences == null) return;
        var preferences = Preferences.Load(TestFiles.Preferences);

        Assert.InRange(preferences.DefaultFrameRate, 1, 240);
        Assert.InRange(preferences.AutoSaveMinutes, 1u, 600u);
        Assert.InRange(preferences.RecentFiles.Count, 0, Preferences.MaxRecentFiles);
        Assert.All(preferences.RecentFiles, f => Assert.False(string.IsNullOrWhiteSpace(f)));
    }

    [Fact]
    public void Preferences_RoundTripsEveryOtherMemberByteExact()
    {
        if (TestFiles.Preferences == null) return;
        string copy = TempFile("preferences");
        try
        {
            var preferences = Preferences.Load(TestFiles.Preferences);
            preferences.SaveAs(copy);
            Assert.Equal(File.ReadAllBytes(TestFiles.Preferences), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Preferences_AddRecentFilePersistsInTheCopy()
    {
        if (TestFiles.Preferences == null) return;
        string copy = TempFile("preferences-recent");
        try
        {
            Preferences.Load(TestFiles.Preferences).SaveAs(copy);
            var preferences = Preferences.Load(copy);
            string? moved = preferences.RecentFiles.FirstOrDefault();

            Assert.True(preferences.AddRecentFile(@"Z:\projects\new scene.akt"));
            preferences.Save();

            var reloaded = Preferences.Load(copy);
            Assert.Equal(@"Z:\projects\new scene.akt", reloaded.RecentFiles[0]);
            Assert.Equal(1, reloaded.RecentFiles.Count(p => string.Equals(p, moved, StringComparison.Ordinal)));
            Assert.InRange(reloaded.RecentFiles.Count, 1, Preferences.MaxRecentFiles);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Autosave_AlternatesTwoSlotsAndOverwritesTheOlder()
    {
        string folder = TempFolder("autosave");
        try
        {
            var model = SceneFactory.NewSceneModel();
            string first = Autosave.Save(model, folder, "Walk");
            Assert.Equal(Autosave.SlotPath(folder, "Walk", 1), first);
            Assert.True(File.Exists(Autosave.SlotPath(folder, "Walk", 2)) == false);

            string second = Autosave.Save(model, folder, "Walk");
            Assert.Equal(Autosave.SlotPath(folder, "Walk", 2), second);

            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-20));
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(first, Autosave.ChoosePath(folder, "Walk"));

            string third = Autosave.Save(model, folder, "Walk");
            Assert.Equal(first, third);
            Assert.Equal(2, Directory.GetFiles(folder, "*.slopa").Length);
            var (payload, format) = AktContainer.Unwrap(File.ReadAllBytes(third));
            Assert.Equal(AktFileFormat.SloppyAnimatsu, format);
            Assert.Equal(AktWriter.Write(model.Document), payload);

            // Both slots open as the same scene.
            foreach (string path in Directory.GetFiles(folder, "*.slopa"))
            {
                var reloaded = TestFiles.LoadScene(path);
                Assert.Equal(8, reloaded.Scene.Cameras.Count);
                Assert.Single(reloaded.Scene.Characters);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Autosave_BaseNameIsTheSceneFileName()
    {
        Assert.Equal("19_run", Autosave.BaseName(@"Z:\scenes\19_run.akt"));
        Assert.Equal("Walk cycle", Autosave.BaseName("Walk cycle.akt"));
        Assert.Equal("New_1", SceneFactory.UntitledName(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SceneFactory.UntitledName(0));
    }

    /// <summary>
    /// The members the File > New leaves uninitialised: two runs write different values, so a
    /// comparison has to ignore them.
    /// </summary>
    private static void NormalizeUninitialized(AktDocument document)
    {
        foreach (var o in document.Objects())
        {
            if (o.ClassKey == "CScene") o.Set("FrameRateIndex", 0);
            if (o.ClassKey is "CCamera" or "CCameraOrtho")
            {
                o.Set("CameraType", 0);
                o.Set("RenderMode", (byte)0);
                if (o.Has("WorldOffsetX"))
                {
                    o.Set("WorldOffsetX", 0f);
                    o.Set("WorldOffsetY", 0f);
                }
            }
        }
    }

    private static string TempFile(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}.akt");

    private static string TempFolder(string purpose)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
