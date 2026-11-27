using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Core.Plugins;
using SloppyAnimatsu.Core.Plugins.EzExports;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The plugin registry, the Plugins menu, plugin storage in a scene and the EZ Exports presets.</summary>
public class PluginTests
{
    // ---- registry and menu ---------------------------------------------------------------------

    private sealed class FakePlugin(string id) : IPlugin
    {
        public int Enabled, Disabled;
        public readonly List<string> Ran = [];
        public PluginInfo Info { get; } = new(id, "Fake " + id, "1.0", "A test plugin.");
        public void Enable(IPluginHost host) => Enabled++;
        public void Disable() => Disabled++;
        public IReadOnlyList<PluginCommand> Commands { get; } =
            [new("go", "_Go"), new("off", "_Greyed", "Not now", Enabled: false)];
        public void Run(string commandId) => Ran.Add(commandId);
    }

    private sealed class FakeHost : IPluginHost
    {
        public SceneModel? Scene => null;
        public string? ScenePath => null;
        public event EventHandler? SceneChanged { add { } remove { } }
        public event EventHandler? SceneSaving { add { } remove { } }
        public event EventHandler? AnimBankSelectionChanged { add { } remove { } }
        public IReadOnlyList<SceneObject> AnimBankSelection => [];
        public IReadOnlyList<SceneObject> SceneSelection => [];
        public Character? ActiveCharacter => null;
        public void ShowStatus(string message) { }
        public void MarkSceneDirty() { }
        public void RefreshMenu() { }
        public Task<FbxExportSettings?> EditExportSettings(FbxExportSettings initial) => Task.FromResult<FbxExportSettings?>(null);
        public Task RunWithProgress(string title, string message, Action<IProgress<double>> work) { work(new Progress<double>()); return Task.CompletedTask; }
    }

    [Fact]
    public void Registry_SavesTheEnabledState_AndStartsOnlyEnabledPlugins()
    {
        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-plugins-{Guid.NewGuid():N}.json");
        try
        {
            var a = new FakePlugin("a");
            var b = new FakePlugin("b");
            var registry = new PluginRegistry(file);
            registry.Add(a);
            registry.Add(b);
            Assert.Throws<InvalidOperationException>(() => registry.Add(new FakePlugin("a")));
            Assert.True(registry.IsEnabled("a"));

            registry.Start(new FakeHost());
            Assert.Equal(1, a.Enabled);
            Assert.True(registry.IsRunning("b"));
            registry.SetEnabled("b", false);
            Assert.Equal(1, b.Disabled);
            Assert.False(registry.IsRunning("b"));
            Assert.False(registry.Run("b", "go"));
            Assert.True(registry.Run("a", "go"));
            Assert.Equal(["go"], a.Ran);
            registry.Stop();
            Assert.Equal(1, a.Disabled);
            Assert.Null(registry.LastError);

            // A new registry reads the saved choice: b stays off, a (and an unknown id) on.
            var again = new PluginRegistry(file);
            var a2 = new FakePlugin("a");
            var b2 = new FakePlugin("b");
            again.Add(a2);
            again.Add(b2);
            again.Start(new FakeHost());
            Assert.Equal(1, a2.Enabled);
            Assert.Equal(0, b2.Enabled);
            Assert.False(again.IsEnabled("b"));
            Assert.True(again.IsEnabled("never-seen"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CommandArgument_RoundTrips()
    {
        string arg = PluginRegistry.CommandArgument("sloppy.x", "run-all");
        Assert.Equal(("sloppy.x", "run-all"), PluginRegistry.ParseCommandArgument(arg));
        Assert.Null(PluginRegistry.ParseCommandArgument("no-bar"));
        Assert.Null(PluginRegistry.ParseCommandArgument(null));
    }

    [Fact]
    public void PluginsMenu_HasASubmenuPerPlugin_ThenManagePlugins()
    {
        var registry = new PluginRegistry(null);
        registry.Add(new FakePlugin("a"));
        registry.Add(new FakePlugin("b"));
        registry.Start(new FakeHost());
        registry.SetEnabled("b", false);

        var menu = MenuModel.Build(new MenuState { Plugins = registry.MenuState() }).Single(e => e.Header == "_Plugins");
        var headers = menu.Children.Select(e => e.Header).ToList();
        Assert.Equal("Fake a", headers[0]);
        Assert.Equal("Fake b", headers[1]);
        Assert.Equal("_Manage plugins…", headers[^1]);
        Assert.Equal(MenuCommand.ManagePlugins, menu.Children[^1].Command);

        var a = menu.Children[0];
        Assert.Equal(MenuEntryKind.Submenu, a.Kind);
        var enabled = a.Children[0];
        Assert.Equal(MenuCommand.TogglePlugin, enabled.Command);
        Assert.Equal("a", enabled.Argument);
        Assert.True(enabled.Checked);
        var go = a.Children.Single(e => e.Header == "_Go");
        Assert.Equal(MenuCommand.PluginCommand, go.Command);
        Assert.Equal("a|go", go.Argument);
        Assert.False(a.Children.Single(e => e.Header == "_Greyed").Enabled);

        // A disabled plugin shows only its Enabled box, unchecked.
        var b = Assert.Single(menu.Children[1].Children);
        Assert.False(b.Checked);
    }

    // ---- EZ Exports: folders and names ---------------------------------------------------------

    [Fact]
    public void ResolveFolder_RelativeToTheScene_AbsoluteAsIs()
    {
        string scene = Path.Combine(Path.GetTempPath(), "proj", "hero.slopa");
        string sceneFolder = Path.GetDirectoryName(scene)!;
        Assert.Equal(sceneFolder, EzExportPlanner.ResolveFolder("", scene, out _));
        Assert.Equal(Path.Combine(sceneFolder, "exports", "fbx"), EzExportPlanner.ResolveFolder("exports/fbx", scene, out _));
        Assert.Equal(Path.GetFullPath(Path.Combine(sceneFolder, "..", "out")), EzExportPlanner.ResolveFolder("../out", scene, out _));
        string absolute = Path.Combine(Path.GetTempPath(), "elsewhere");
        Assert.Equal(absolute, EzExportPlanner.ResolveFolder(absolute, null, out string? none));
        Assert.Null(none);
        Assert.Null(EzExportPlanner.ResolveFolder("exports", null, out string? error));
        Assert.NotNull(error);
        Assert.Null(EzExportPlanner.ResolveFolder("", null, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Stem_IsTheKey_ElseTheName_AndFileNamesAreMadeSafe()
    {
        Assert.Equal("Cat", EzExportPlanner.Stem(new EzPreset { Name = "Preset", Key = " Cat " }));
        Assert.Equal("Locomotion", EzExportPlanner.Stem(new EzPreset { Name = "Locomotion", Key = "" }));
        Assert.Equal("export", EzExportPlanner.Stem(new EzPreset { Name = " ", Key = "" }));
        Assert.Equal("a-b-c", EzExportPlanner.SafeFileName("a/b\\c"));
        Assert.Equal("unnamed", EzExportPlanner.SafeFileName(""));
    }

    // ---- EZ Exports: presets in a scene --------------------------------------------------------

    private static Character? FirstCharacter(SceneModel model) =>
        model.Scene.Characters.FirstOrDefault(c => c.AnimBank != null && c.Controllers.Count > 0);

    /// <summary>A folder "EzFolder" holding two new animations, and a third new one at the bank root.</summary>
    private static (Animation Root, Folder Folder, Animation InFolder1, Animation InFolder2) Build(Character character)
    {
        var bank = new AnimationBankEditor(character);
        var folder = bank.CreateFolder("EzFolder");
        var one = bank.CreateAnimation(folder, "EzOne");
        var two = bank.CreateAnimation(folder, "EzTwo");
        var root = bank.CreateAnimation(null, "EzRoot");
        return (root, folder, one, two);
    }

    [SceneTheory, SceneSamples]
    public void Plan_ExpandsFolders_NamesFilesFromTheKey_AndListsEachAnimationOnce(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var (root, folder, one, two) = Build(character);
        string scene = Path.Combine(Path.GetTempPath(), "proj", "hero.slopa");

        var preset = new EzPreset { Name = "Set", Key = "Cat", Folder = "exports" };
        preset.Items.AddRange([root, folder, one]);
        var plan = EzExportPlanner.Plan(model, scene, preset);
        Assert.Null(plan.Error);
        string expectedFolder = Path.Combine(Path.GetDirectoryName(scene)!, "exports");
        Assert.Equal(expectedFolder, plan.Folder);
        Assert.Equal([root, one, two], plan.Files.Select(f => f.Animation).ToList());
        foreach (var file in plan.Files)
        {
            Assert.Equal(expectedFolder, Path.GetDirectoryName(file.Path));
            Assert.Equal("Cat_" + EzExportPlanner.SafeFileName(preset.Settings.AnimationFileName(file.Animation)) + ".fbx",
                Path.GetFileName(file.Path));
        }

        // One animation: the key alone.
        var single = new EzPreset { Name = "One", Key = "Solo" };
        single.Items.Add(two);
        var singlePlan = EzExportPlanner.Plan(model, scene, single);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(scene)!, "Solo.fbx"), Assert.Single(singlePlan.Files).Path);

        // Nothing to export, or nowhere to write: an error and no files.
        Assert.NotNull(EzExportPlanner.Plan(model, scene, new EzPreset()).Error);
        var unsaved = EzExportPlanner.Plan(model, null, preset);
        Assert.NotNull(unsaved.Error);
        Assert.Empty(unsaved.Files);
    }

    [SceneTheory, SceneSamples]
    public void ModelPresets_WriteTheCharacterWithoutAnimations(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var (root, folder, one, two) = Build(character);
        string sceneFolder = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ezmodel-{Guid.NewGuid():N}");
        string scene = Path.Combine(sceneFolder, "hero.slopa");

        // A character item alone: one file named from the key, holding the character and no animation.
        var modelOnly = new EzPreset { Name = "Model", Key = "Hero" };
        modelOnly.Items.Add(character);
        var plan = EzExportPlanner.Plan(model, scene, modelOnly);
        Assert.Null(plan.Error);
        var file = Assert.Single(plan.Files);
        Assert.Equal(Path.Combine(sceneFolder, "Hero.fbx"), file.Path);
        Assert.Same(character, file.Character);
        Assert.Null(file.Animation);
        Assert.Same(character, Assert.Single(file.Options.Characters!));
        Assert.Empty(file.Options.Animations!);
        Assert.Equal(character.Name + EzExportPlanner.ModelSuffix, EzExportPlanner.ItemPath(character));

        // With animations as well: the model and each animation get their own suffixed file.
        var mixed = new EzPreset { Name = "Mixed", Key = "K" };
        mixed.Items.AddRange([character, root, one]);
        var mixedPlan = EzExportPlanner.Plan(model, scene, mixed);
        Assert.Equal([null, root, one], mixedPlan.Files.Select(f => f.Animation).ToList());
        Assert.Equal("K_" + EzExportPlanner.SafeFileName(character.Name!) + ".fbx", Path.GetFileName(mixedPlan.Files[0].Path));

        // "No animations": the animation items stand for their character's model, written once.
        var noAnims = new EzPreset { Name = "NoAnims", Key = "M", Settings = modelOnly.Settings with { WithoutAnimations = true } };
        noAnims.Items.AddRange([folder, two]);
        var noAnimsFile = Assert.Single(EzExportPlanner.Plan(model, scene, noAnims).Files);
        Assert.Null(noAnimsFile.Animation);
        Assert.Equal("M.fbx", Path.GetFileName(noAnimsFile.Path));

        // Running it writes the file (the reported bug: a model preset wrote nothing), and the
        // character item survives a save and reload.
        try
        {
            var result = Assert.Single(EzExportRunner.Run(model, scene, [modelOnly]));
            Assert.Null(result.Error);
            Assert.True(new FileInfo(Path.Combine(sceneFolder, "Hero.fbx")).Length > 0);

            Assert.True(EzPresetStore.Save(model, [modelOnly, mixed]));
            model.Save(scene);
            var reloaded = SceneModel.Load(scene);
            var presets = EzPresetStore.Load(reloaded, out string? error);
            Assert.Null(error);
            Assert.Equal(EzExportPlanner.ItemPath(character), EzExportPlanner.ItemPath(Assert.Single(presets[0].Items)));
            Assert.IsType<Character>(presets[1].Items[0]);
        }
        finally
        {
            if (Directory.Exists(sceneFolder)) Directory.Delete(sceneFolder, recursive: true);
        }
    }

    [SceneTheory, SceneSamples]
    public void Presets_SurviveSaveAndReload_AndFollowRenamesAndMoves(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var (root, folder, one, _) = Build(character);
        var first = new EzPreset { Name = "Folder", Key = "Cat", Folder = "exports", Checked = false };
        first.Items.AddRange([folder, root]);
        first.Settings = FbxExportSettings.For(FbxExportTarget.Unity, first.Settings) with { ScaleFactor = 2f };
        var second = new EzPreset { Name = "Single", Key = "One", Folder = @"C:\abs\out" };
        second.Items.Add(one);
        Assert.True(EzPresetStore.Save(model, [first, second]));

        // Rename and move after the presets were written: the references follow the objects.
        AnimationBankEditor.Rename(root, "EzRenamed");
        new AnimationBankEditor(character).Move(root, folder);

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ezexport-{Guid.NewGuid():N}.slopa");
        try
        {
            model.Save(temp);
            var reloaded = SceneModel.Load(temp);
            Assert.Contains(EzPresetStore.PluginId, PluginSceneData.PluginIds(reloaded));
            var presets = EzPresetStore.Load(reloaded, out string? error);
            Assert.Null(error);
            Assert.Equal(["Folder", "Single"], presets.Select(p => p.Name).ToList());
            var a = presets[0];
            Assert.False(a.Checked);
            Assert.Equal(("Cat", "exports"), (a.Key, a.Folder));
            Assert.Equal(FbxExportTarget.Unity, a.Settings.Target);
            Assert.Equal(2f, a.Settings.ScaleFactor);
            Assert.Equal(@"C:\abs\out", presets[1].Folder);

            string characterName = character.Name ?? "";
            Assert.Equal([EzExportPlanner.ItemPath(folder), EzExportPlanner.ItemPath(root)],
                a.Items.Select(EzExportPlanner.ItemPath).ToList());
            Assert.EndsWith("EzFolder/EzRenamed", EzExportPlanner.ItemPath(a.Items[1]));
            Assert.IsType<Animation>(a.Items[1]);
            Assert.Equal("EzOne", Assert.Single(presets[1].Items)!.Name);
        }
        finally { File.Delete(temp); }
    }

    [SceneTheory, SceneSamples]
    public void DeletedItems_ShowAsMissing_AndAreSkipped(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var (root, _, one, _) = Build(character);
        var preset = new EzPreset { Name = "Set", Key = "K" };
        preset.Items.AddRange([root, one]);
        Assert.True(EzPresetStore.Save(model, [preset]));
        new AnimationBankEditor(character).DeleteAnimation(root);

        string scene = Path.Combine(Path.GetTempPath(), "proj", "hero.slopa");
        var plan = EzExportPlanner.Plan(model, scene, preset);
        Assert.Equal(1, plan.MissingItems);
        Assert.Single(plan.Warnings);
        Assert.Equal(one, Assert.Single(plan.Files).Animation);

        // Saved and reloaded, the deleted item keeps its place as a missing row.
        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ezexport-{Guid.NewGuid():N}.slopa");
        try
        {
            EzPresetStore.Save(model, [preset]);
            model.Save(temp);
            var reloaded = EzPresetStore.Load(SceneModel.Load(temp));
            var items = Assert.Single(reloaded).Items;
            Assert.Equal(2, items.Count);
            Assert.Null(items[0]);
            Assert.Equal("(missing)", EzExportPlanner.ItemName(items[0]));
            Assert.Equal("EzOne", items[1]!.Name);
        }
        finally { File.Delete(temp); }
    }

    [SceneTheory, SceneSamples]
    public void AnAktSave_CarriesNoPluginMembers(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (FirstCharacter(model) is not { } character) return;
        var (root, _, _, _) = Build(character);
        var preset = new EzPreset { Name = "Set" };
        preset.Items.Add(root);
        Assert.True(EzPresetStore.Save(model, [preset]));
        Assert.NotEmpty(PluginSceneData.PluginIds(model));

        string temp = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-ezexport-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(temp);
            var reloaded = SceneModel.Load(temp);
            Assert.Empty(PluginSceneData.PluginIds(reloaded));
            Assert.Empty(EzPresetStore.Load(reloaded));
            foreach (string member in new[] { MaterialSchema.PluginIdsMember, MaterialSchema.PluginDataMember,
                         MaterialSchema.PluginRefsMember, MaterialSchema.PluginRefOwnersMember })
                Assert.Null(reloaded.Scene.Source[member]);
        }
        finally { File.Delete(temp); }
    }
}
