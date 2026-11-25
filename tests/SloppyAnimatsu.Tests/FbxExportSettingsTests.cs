using System.Text;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Export Settings window's choices (<see cref="FbxExportSettings"/>): the flags word they are
/// kept in, the target presets, the window's rows and hover text, and what each choice makes the
/// exporter write (<see cref="FbxExporter.ExportedObjects"/> is also what the window lights).
/// </summary>
public sealed class FbxExportSettingsTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void The_default_flags_are_baked_with_the_character_node_and_embedded_media()
    {
        var s = FbxExportSettings.FromFlags(FbxExportSettings.DefaultFlags);
        Assert.True(s.Baked);
        Assert.True(s.EmbedMedia);
        Assert.True(s.CharacterNode);
        Assert.False(s.CameraTargets); // 0x40 is stored inverted
        Assert.False(s.Ascii);
        Assert.False(s.WithoutAnimations);
        Assert.False(s.OneFilePerAnimation);
        Assert.False(s.OneFilePerCharacter);
        Assert.Equal(FbxExportTarget.Default, s.Target);
        Assert.Equal(FbxExportScope.Scene, s.Scope);
        Assert.False(s.OnlySelectedAnimations);
        Assert.Equal(FbxExportSettings.DefaultFlags, s.ToFlags());
    }

    [Fact]
    public void Every_flags_word_round_trips()
    {
        for (uint flags = 0; flags < 0x2000; flags++)
            Assert.Equal(flags, FbxExportSettings.FromFlags(flags).ToFlags());
    }

    [Fact]
    public void Per_animation_files_are_named_like_akeytsu_unless_the_folder_path_is_asked_for()
    {
        string[] folders = ["Locomotion", "Run"];
        var plain = FbxExportSettings.For(FbxExportTarget.Default);
        Assert.False(plain.FolderPathInName);
        Assert.Equal("_", plain.FolderSeparator);
        Assert.Equal("fast", plain.AnimationFileName("fast", folders));
        Assert.Equal("unnamed", plain.AnimationFileName("", folders));

        var withFolders = plain with { FolderPathInName = true };
        Assert.Equal("Locomotion_Run_fast", withFolders.AnimationFileName("fast", folders));
        Assert.Equal("Locomotion-Run-fast", (withFolders with { FolderSeparator = "-" }).AnimationFileName("fast", folders));
        Assert.Equal("fast", withFolders.AnimationFileName("fast", []));
        Assert.Equal(0x1000u, withFolders.ToFlags() & 0x1000u);
    }

    [Fact]
    public void A_preset_button_changes_only_the_target()
    {
        var current = FbxExportSettings.FromFlags(0xFF) with
        {
            Scope = FbxExportScope.Selection,
            OnlySelectedAnimations = true,
            FolderPathInName = true,
            FolderSeparator = "-",
        };
        foreach (var (target, _, _) in FbxExportSettings.Targets)
            Assert.Equal(current with { Target = target }, FbxExportSettings.For(target, current));
        // The base settings (a fresh profile): bit 0x01 is Baked, on for Default, off for the engine targets.
        foreach (var (target, _, _) in FbxExportSettings.Targets)
        {
            uint expected = target == FbxExportTarget.Default ? FbxExportSettings.DefaultFlags : FbxExportSettings.DefaultFlags & ~0x01u;
            Assert.Equal(expected | (uint)target << 8, FbxExportSettings.For(target).ToFlags());
        }
        Assert.Equal(FbxExportPreset.Universal, FbxExportSettings.For(FbxExportTarget.Default).Preset);
        Assert.Equal(FbxExportPreset.Blender, FbxExportSettings.For(FbxExportTarget.Blender).Preset);
        Assert.Equal(FbxExportPreset.Unity, FbxExportSettings.For(FbxExportTarget.Unity).Preset);
        Assert.Equal(FbxExportPreset.Godot, FbxExportSettings.For(FbxExportTarget.Godot).Preset);
        // Default bakes; the engine targets write fitted keys instead.
        Assert.True(FbxExportSettings.For(FbxExportTarget.Default).Baked);
        foreach (var target in new[] { FbxExportTarget.Blender, FbxExportTarget.Unity, FbxExportTarget.Godot })
            Assert.False(FbxExportSettings.For(target).Baked);
    }

    [Fact]
    public void Settings_that_do_not_suit_the_target_are_warned_about()
    {
        // Every target's base settings are clean.
        foreach (var (target, _, _) in FbxExportSettings.Targets)
            Assert.Empty(FbxExportSettings.For(target).Warnings());

        // Baked or not suits every target: unbaked Default writes fitted linear keys.
        Assert.Empty((FbxExportSettings.For(FbxExportTarget.Unity) with { Baked = true }).Warnings());
        Assert.Empty((FbxExportSettings.For(FbxExportTarget.Default) with { Baked = false }).Warnings());

        // Blender cannot read ASCII, so neither can a file meant for every program; the others can.
        Assert.Contains((FbxExportSettings.For(FbxExportTarget.Blender) with { Ascii = true }).Warnings(), w => w.Contains("ASCII"));
        Assert.Contains((FbxExportSettings.For(FbxExportTarget.Default) with { Ascii = true }).Warnings(), w => w.Contains("ASCII"));
        Assert.Empty((FbxExportSettings.For(FbxExportTarget.Unity) with { Ascii = true }).Warnings());

        // Options that do nothing in this combination.
        var still = FbxExportSettings.For(FbxExportTarget.Unity) with { WithoutAnimations = true, OneFilePerAnimation = true, OnlySelectedAnimations = true };
        Assert.Equal(2, still.Warnings().Count);
        Assert.Single((FbxExportSettings.For(FbxExportTarget.Unity) with { FolderPathInName = true }).Warnings());
        Assert.Empty((FbxExportSettings.For(FbxExportTarget.Unity) with { FolderPathInName = true, OneFilePerAnimation = true }).Warnings());

        // Exporter options only the CLI can set.
        Assert.Single(new FbxExportOptions { Preset = FbxExportPreset.Unity, FittedKeys = false }.CompatibilityWarnings());
        Assert.Single(new FbxExportOptions { Preset = FbxExportPreset.Blender, FittedCubic = true }.CompatibilityWarnings());
        Assert.Single(new FbxExportOptions { Preset = FbxExportPreset.Godot, Baked = true, LinearBakedKeys = false }.CompatibilityWarnings());
        Assert.Single(new FbxExportOptions { Preset = FbxExportPreset.Unity, Baked = true, MinimumScale = 0 }.CompatibilityWarnings());
        Assert.Empty(new FbxExportOptions { Preset = FbxExportPreset.Plain }.CompatibilityWarnings());
        Assert.Single(new FbxExportOptions { Preset = FbxExportPreset.Universal, FittedCubic = true }.CompatibilityWarnings());

        // Universal takes every engine fix that suits all three readers, with Blender's linear keys.
        var universal = new FbxExportOptions { Preset = FbxExportPreset.Universal };
        Assert.True(universal.PlainHierarchy && universal.LinearBakedKeys && universal.FittedKeys && universal.UniqueAnimationNames);
        Assert.False(universal.FittedCubic);
        Assert.True(universal.MinimumScale > 0);
        Assert.Empty(universal.CompatibilityWarnings());
        Assert.Empty(new FbxExportOptions { Preset = FbxExportPreset.Unity, FittedKeys = false, Animations = [] }.CompatibilityWarnings());
    }

    [Fact]
    public void The_window_lists_the_presets_scopes_and_toggles_in_order_with_hover_text()
    {
        Assert.Equal(new[] { "Default", "Blender", "Unity", "Godot" }, FbxExportSettings.Targets.Select(t => t.Label));
        Assert.Equal(new[] { "Export Scene", "Export Selection" }, FbxExportSettings.Scopes.Select(t => t.Label));
        Assert.Equal(new[]
        {
            "Only Selected Animations", "Baked Animations", "Multi-FBX (one file per animation)",
            "Folder path in file names", "Multi-FBX (one file per character)", "Export without animations", "Export camera targets",
            "Export Character node (as dummy)", "Embed Media", "Use ASCII format",
        }, FbxExportSettings.Options.Select(o => o.Label));
        Assert.All(FbxExportSettings.Targets, t => Assert.False(string.IsNullOrWhiteSpace(t.Tip)));
        Assert.All(FbxExportSettings.Scopes, t => Assert.False(string.IsNullOrWhiteSpace(t.Tip)));
        Assert.All(FbxExportSettings.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Tip)));
    }

    [Fact]
    public void Every_toggle_sets_only_its_own_choice()
    {
        var all = FbxExportSettings.Options;
        foreach (var option in all)
        {
            var off = all.Aggregate(FbxExportSettings.For(FbxExportTarget.Default), (s, o) => o.Set(s, false));
            var on = option.Set(off, true);
            Assert.True(option.Get(on));
            Assert.All(all.Where(o => o != option), o => Assert.False(o.Get(on)));
        }
    }

    [Fact]
    public void The_preferences_keep_the_choices()
    {
        if (TestFiles.Preferences is not { } source) return;
        string copy = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-export-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        try
        {
            var chosen = FbxExportSettings.For(FbxExportTarget.Godot) with
            {
                Ascii = true,
                OneFilePerCharacter = true,
                Scope = FbxExportScope.Selection,
                OnlySelectedAnimations = true,
                FolderPathInName = true,
                FolderSeparator = " - ",
            };
            var preferences = Preferences.Load(source);
            preferences.ExportSettings = chosen;
            preferences.SaveAs(copy);
            Assert.Equal(chosen, Preferences.Load(copy).ExportSettings);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void The_options_say_what_the_exporter_writes()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = model.Scene.Characters.First(c => c.Animations.Any());
        var joint = character.Descendants<Joint>().First();
        var settings = FbxExportSettings.For(FbxExportTarget.Default);

        // The selection scope takes the character that owns a selected joint.
        var selection = settings with { Scope = FbxExportScope.Selection };
        Assert.Equal(new[] { character }, selection.Characters(model, [joint]));

        // Without the character node the character is not a model; with it, it is.
        var withNode = FbxExporter.ExportedObjects(model, settings.ToOptions([character], [], null, null, null));
        var withoutNode = FbxExporter.ExportedObjects(model, (settings with { CharacterNode = false }).ToOptions([character], [], null, null, null));
        Assert.Contains(character, withNode);
        Assert.DoesNotContain(character, withoutNode);
        Assert.Contains(joint, withoutNode);

        // Without animations nothing is exported as a take.
        var still = (settings with { WithoutAnimations = true }).ToOptions([character], [], null, null, null);
        Assert.Empty(FbxExporter.ExportedAnimations(model, still));

        // Only the picked animations.
        var first = character.Animations.First();
        var picked = settings.ToOptions([character], [], [first], null, null);
        Assert.Equal(new[] { first }, FbxExporter.ExportedAnimations(model, picked));
        var fbx = new FbxScene(FbxDocument.Read(FbxExporter.Export(model, picked with { Timestamp = Stamp })));
        Assert.Equal(first.Name, fbx.Stacks.Single().Name);
    }

    [Fact]
    public void The_ascii_option_writes_the_text_form()
    {
        if (Load("19_run.akt") is not { } model) return;
        var character = model.Scene.Characters.First(c => c.Animations.Any());
        var options = (FbxExportSettings.For(FbxExportTarget.Default) with { Ascii = true, WithoutAnimations = true })
            .ToOptions([character], [], null, null, null) with { Timestamp = Stamp };
        var text = Encoding.UTF8.GetString(FbxExporter.Export(model, options));
        Assert.StartsWith("; FBX 7.4.0 project file", text);
        Assert.Contains("Objects:", text);
        Assert.Contains("\"Model::" + character.Name + "\"", text);
    }

    /// <summary>A scene from SLOPPYANIMATSU_SAMPLES by file name.</summary>
    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path) : null;
}
