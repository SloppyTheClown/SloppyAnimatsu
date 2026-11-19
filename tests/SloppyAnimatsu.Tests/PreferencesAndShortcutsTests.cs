using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The preferences and shortcuts files (also in akeytsu's .akt layout, for compatibility): the
/// map value, the shortcut key encoding, the preference pages and the saved dock layout.
/// </summary>
public class PreferencesAndShortcutsTests
{
    [Fact]
    public void ShortcutKey_UsesTheAktEncoding()
    {
        var undo = ShortcutKey.Parse("CTRL+Z");
        Assert.Equal(0x5A, undo.VirtualKey);
        Assert.Equal(ShortcutModifiers.Control, undo.Modifiers);
        Assert.Equal(0x2D02u, undo.Code);
        Assert.Equal("CTRL+Z", undo.ToString());

        var redo = ShortcutKey.FromCode(0x2D03);
        Assert.Equal(0x5A, redo.VirtualKey);
        Assert.Equal(ShortcutModifiers.Control | ShortcutModifiers.Shift, redo.Modifiers);
        Assert.Equal("CTRL+SHIFT+Z", redo.ToString());

        Assert.Equal("RIGHT", ShortcutKey.Parse("RIGHT").ToString());
        Assert.Equal("CTRL+RIGHT", ShortcutKey.Parse("Control+Right").ToString());
        Assert.Equal("NUMPAD1", ShortcutKey.Parse("NUMPAD1").ToString());
        Assert.Equal("DELETE", ShortcutKey.Parse("Del").ToString());
        Assert.Equal("F12", ShortcutKey.Parse("F12").ToString());
        Assert.Equal("SHIFT+V", ShortcutKey.Parse("shift+v").ToString());
        Assert.True(ShortcutKey.Parse("").IsEmpty);
        Assert.True(ShortcutKey.Parse("none").IsEmpty);
        Assert.Throws<FormatException>(() => ShortcutKey.Parse("HYPER+Z"));
        Assert.Throws<FormatException>(() => ShortcutKey.Parse("CTRL"));
    }

    [Fact]
    public void ShortcutDisplay_UsesTheLayoutsPrintableCharacter()
    {
        try
        {
            // What the viewer installs on Windows: one printable character per key, else null.
            Shortcuts.LayoutKeyName = vk => vk switch { 0xBA => "ù", 0x0D => "Entrée", _ => null };

            // The app upper-cases the layout's character.
            Assert.Equal("Ù", new ShortcutKey(0xBA, ShortcutModifiers.None).ToString());
            Assert.Equal("CTRL+Ù", new ShortcutKey(0xBA, ShortcutModifiers.Control).ToString());
            // A named key keeps the spelling even when the layout names it.
            Assert.Equal("RETURN", new ShortcutKey(0x0D, ShortcutModifiers.None).ToString());
            // Parsing and the stored code do not depend on the display hook.
            Assert.Equal(0xBA, ShortcutKey.Parse("OEM_1").VirtualKey);
            Assert.Equal(0xBA, ShortcutKey.Parse("CTRL+OEM_1").VirtualKey);
        }
        finally
        {
            Shortcuts.LayoutKeyName = null;
        }
        Assert.Equal("OEM_1", new ShortcutKey(0xBA, ShortcutModifiers.None).ToString());
    }

    [Fact]
    public void MapValue_RoundTripsEntriesInFileOrder()
    {
        var schema = new AktSchema();
        var holder = new AktClassDef("class CMapHolder");
        holder.Members.Add(new AktMember("Entries", "map<uint,CAtom>"));
        schema.AddClass(holder, null);
        var root = schema.NewObject("class CMapHolder");
        var map = (AktMap)root["Entries"]!;
        map.Add(0x20u, "Space");
        map.Add(0u, "None");
        map.Add(0x2D03u, "Redo");
        root.Set("Entries", map);

        string first = TempFile("map");
        string second = TempFile("map");
        try
        {
            new AktDocument(schema, new[] { root }).Save(first);
            var reloaded = AktDocument.Load(first);
            var entries = ((AktMap)reloaded.Root["Entries"]!).Entries;
            Assert.Equal(new object?[] { 0x20u, 0u, 0x2D03u }, entries.Select(e => e.Key));
            Assert.Equal(new object?[] { "Space", "None", "Redo" }, entries.Select(e => e.Value));
            reloaded.Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Shortcuts_CreatesTheShippedDefaultMap()
    {
        var shortcuts = Shortcuts.CreateDefault();
        Assert.Equal(80, shortcuts.Bindings.Count);
        Assert.Equal("ResetAndFreeze", shortcuts.Bindings[0].Action);
        Assert.True(shortcuts.Bindings[0].Key.IsEmpty);
        Assert.Equal("CTRL+Z", shortcuts.KeyFor("Undo")!.Value.ToString());
        Assert.Equal("CTRL+SHIFT+Z", shortcuts.KeyFor("Redo")!.Value.ToString());
        Assert.Equal("W", shortcuts.KeyFor("SetSpinnerModeMove")!.Value.ToString());
        Assert.Equal("SPACE", shortcuts.KeyFor("MiscToggleCurveboard")!.Value.ToString());
        Assert.Equal("Undo", shortcuts.ActionFor(ShortcutKey.Parse("CTRL+Z")));

        string first = TempFile("shortcuts");
        string second = TempFile("shortcuts");
        try
        {
            shortcuts.SaveAs(first);
            if (TestFiles.Shortcuts is { } shipped)
                Assert.Equal(File.ReadAllBytes(shipped), File.ReadAllBytes(first));
            var reloaded = Shortcuts.Load(first);
            Assert.Equal(80, reloaded.Bindings.Count);
            Assert.Equal("CTRL+Z", reloaded.KeyFor("Undo")!.Value.ToString());
            reloaded.SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Shortcuts_LoadsTheShippedFileByteExact()
    {
        if (TestFiles.Shortcuts is not { } path) return;

        var shortcuts = Shortcuts.Load(path);
        Assert.Equal(80, shortcuts.Bindings.Count);
        Assert.Equal(0x0000u, shortcuts.Bindings[0].Code);
        Assert.Equal("ResetAndFreeze", shortcuts.Bindings[0].Action);
        Assert.Equal("CTRL+Z", shortcuts.KeyFor("Undo")!.Value.ToString());
        Assert.Equal("CTRL+RIGHT", shortcuts.KeyFor("NextFrame")!.Value.ToString());
        Assert.Equal("CTRL+SHIFT+C", ShortcutKey.Parse("CTRL+SHIFT+C").ToString());
        Assert.Equal("DeleteObject", shortcuts.ActionFor(ShortcutKey.Parse("DELETE")));

        string copy = TempFile("shortcuts");
        try
        {
            shortcuts.SaveAs(copy);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void DefaultBindings_MatchTheShippedFile()
    {
        if (TestFiles.Shortcuts is not { } path) return;

        var shipped = Shortcuts.Load(path).Bindings;
        Assert.Equal(shipped.Count, Shortcuts.DefaultBindings.Count);
        foreach (var binding in shipped)
            Assert.Equal(binding, Shortcuts.DefaultBindings.Single(b => b.Action == binding.Action));
    }

    [Fact]
    public void Shortcuts_AssignsRefusesDuplicatesAndResets()
    {
        var shortcuts = Shortcuts.CreateDefault();
        Assert.True(shortcuts.TrySet("Undo", ShortcutKey.Parse("CTRL+Y"), out string? reason), reason);
        Assert.Equal("CTRL+Y", shortcuts.KeyFor("Undo")!.Value.ToString());
        Assert.Equal("Undo", shortcuts.ActionFor(ShortcutKey.Parse("CTRL+Y")));

        // The app refuses a key that another action already uses instead of stealing it.
        Assert.False(shortcuts.TrySet("PasteKeys", ShortcutKey.Parse("CTRL+Y"), out reason));
        Assert.NotNull(reason);
        Assert.Contains("Undo", reason!);
        Assert.Equal("CTRL+V", shortcuts.KeyFor("PasteKeys")!.Value.ToString());

        Assert.True(shortcuts.TrySet("Undo", ShortcutKey.None, out _));
        Assert.Null(shortcuts.KeyFor("Undo"));
        Assert.True(shortcuts.Reset("Undo"));
        Assert.Equal("CTRL+Z", shortcuts.KeyFor("Undo")!.Value.ToString());
        Assert.True(shortcuts.Reset("ResetAndFreeze"));
        Assert.True(shortcuts.KeyFor("ResetAndFreeze")!.Value.IsEmpty);

        shortcuts.ResetAll();
        Assert.Equal(80, shortcuts.Bindings.Count);
        Assert.Equal("CTRL+Z", shortcuts.KeyFor("Undo")!.Value.ToString());
        Assert.Equal("SPACE", shortcuts.KeyFor("MiscToggleCurveboard")!.Value.ToString());
    }

    [Fact]
    public void Preferences_ExposesPagesValuesAndSnappingDefaults()
    {
        if (TestFiles.Preferences is not { } path) return;

        var preferences = Preferences.Load(path);
        Assert.Equal(22, preferences.Pages.Count);
        Assert.Equal(60, preferences.DefaultFrameRate);
        Assert.Equal(10u, preferences.AutoSaveMinutes);

        var general = Assert.IsType<PreferencePage>(preferences.Page("CGeneralPreferences"));
        Assert.Equal(10u, (uint)general.Get("AutoSaveTime")!);
        Assert.Equal("Default name_09", general.Name);

        var spinner = Assert.IsType<PreferencePage>(preferences.Page("CSpinnerPreferences"));
        Assert.Equal(0.1f, (float)spinner.Get("MoveStep")!);
        Assert.Equal(45f, (float)spinner.Get("RotateStep")!);
        Assert.Equal(0.5f, (float)spinner.Get("ScaleStep")!);
        Assert.Equal(10u, (uint)spinner.Get("PivotMask")!);
        Assert.True(preferences.KeepChildrenFixed(2));
        Assert.True(preferences.KeepChildrenFixed(4));
        Assert.False(preferences.KeepChildrenFixed(1));

        Assert.Equal("Pelvis", preferences.Get("CAutoRiggerPreferences", "HipsName") as string);
        Assert.Equal("Left|Right,left|right", preferences.Get("CMirrorPreferences", "PatternList") as string);

        Assert.True(spinner.TrySetFromString("RotateStep", "30", out string? error), error);
        Assert.Equal(30f, (float)spinner.Get("RotateStep")!);
        Assert.False(spinner.TrySetFromString("RotateStep", "not a number", out error));
        Assert.NotNull(error);
        Assert.Contains("RotateStep", error!);
        Assert.False(spinner.TrySetFromString("NoSuchMember", "1", out error));
        Assert.NotNull(error);
        Assert.Contains("NoSuchMember", error!);
    }

    [Fact]
    public void Preferences_ExposesEveryEditableLeafAndReadsItsTextBack()
    {
        if (TestFiles.Preferences is not { } path) return;

        var preferences = Preferences.Load(path);
        var general = Assert.IsType<PreferencePage>(preferences.Page("CGeneralPreferences"));
        var fields = general.Fields;
        Assert.Contains(fields, f => f.Path == "AutoSaveTime" && f.TypeName == "uint");
        Assert.Contains(fields, f => f.Path == "PreferredPaths.SavePath" && f.TypeName == "QString");
        Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(fields.Single(f => f.Path == "PreferredPaths.LoadPath").Value)));
        Assert.Equal(6, fields.Count(f => f.Path.StartsWith("PreferredPaths.", StringComparison.Ordinal)));
        Assert.DoesNotContain(fields, f => f.Path == "PreferredPaths");

        var spinner = Assert.IsType<PreferencePage>(preferences.Page("CSpinnerPreferences"));
        Assert.Contains(spinner.Fields, f => f.Path == "OptionsArray[0].Flags" && f.TypeName == "uint");
        // The spinner's flag bits are the user's own state (the app rewrites them when the dialog is
        // used), so only the type is asserted; the byte round-trip below proves the value is read
        // and written back unchanged.
        Assert.IsType<uint>(spinner.Fields.Single(f => f.Path == "OptionsArray[0].Flags").Value);
        Assert.Equal(9, spinner.Fields.Count(f => f.Path.StartsWith("OptionsArray[", StringComparison.Ordinal)));

        // Every leaf shows a text form that reads back as the same value, so the dialog can save an
        // untouched page without changing a byte.
        foreach (var page in preferences.Pages)
        {
            foreach (var field in page.Fields)
                Assert.True(field.TrySetFromString(field.Format(), out string? error),
                    $"{page.ClassName}.{field.Path}: {error}");
        }

        string copy = TempFile("prefs");
        try
        {
            preferences.SaveAs(copy);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Preferences_SetsNestedMembers()
    {
        if (TestFiles.Preferences is not { } path) return;

        var preferences = Preferences.Load(path);
        Assert.IsType<AktStruct>(preferences.Get("CGeneralPreferences", "PreferredPaths"));
        Assert.True(preferences.TrySet("CGeneralPreferences", "PreferredPaths.SavePath", "Z:/somewhere/", out string? error), error);
        Assert.Equal("Z:/somewhere/", preferences.Get("CGeneralPreferences", "PreferredPaths.SavePath"));
        Assert.True(preferences.TrySet("CSpinnerPreferences", "OptionsArray[3].Flags", "6", out error), error);
        Assert.Equal(6u, preferences.Get("CSpinnerPreferences", "OptionsArray[3].Flags"));

        Assert.False(preferences.TrySet("CSpinnerPreferences", "OptionsArray[9].Flags", "1", out error));
        Assert.Contains("element [9]", error!);
        Assert.False(preferences.TrySet("CSpinnerPreferences", "OptionsArray[3]", "1", out error));
        Assert.Contains("leaf", error!);
        Assert.False(preferences.TrySet("CGeneralPreferences", "PreferredPaths", "x", out error));
        Assert.Contains("leaf", error!);
        Assert.False(preferences.TrySet("CSpinnerPreferences", "OptionsArray[3].NoSuchMember", "1", out error));
        Assert.Contains("NoSuchMember", error!);

        string copy = TempFile("prefs");
        try
        {
            preferences.SaveAs(copy);
            var reloaded = Preferences.Load(copy);
            Assert.Equal("Z:/somewhere/", reloaded.Get("CGeneralPreferences", "PreferredPaths.SavePath"));
            Assert.Equal(6u, reloaded.Get("CSpinnerPreferences", "OptionsArray[3].Flags"));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Shortcuts_ListsEveryShippedActionEvenWhenUnbound()
    {
        var shortcuts = Shortcuts.CreateDefault();
        Assert.Equal(80, shortcuts.ActionNames.Count);
        Assert.Contains("Undo", shortcuts.ActionNames);
        Assert.Contains("ResetAndFreeze", shortcuts.ActionNames);

        Assert.True(shortcuts.Unbind("Undo"));
        Assert.DoesNotContain("Undo", shortcuts.Actions);
        Assert.Contains("Undo", shortcuts.ActionNames);

        Assert.True(shortcuts.TrySet("Undo", ShortcutKey.Parse("CTRL+Y"), out _));
        Assert.Contains("Undo", shortcuts.ActionNames);
        Assert.Equal("CTRL+Y", shortcuts.KeyFor("Undo")!.Value.ToString());

        // A custom binding is listed too, and the list stays alphabetical.
        Assert.True(shortcuts.TrySet("MyCustomAction", ShortcutKey.Parse("CTRL+ALT+SHIFT+F12"), out _));
        Assert.Contains("MyCustomAction", shortcuts.ActionNames);
        var names = shortcuts.ActionNames;
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void Preferences_ReadsTheDockLayout()
    {
        if (TestFiles.Preferences is not { } path) return;

        // The values are the frozen TestData profile's (TestFiles.Preferences), not a live one.
        var preferences = Preferences.Load(path);
        var transform = Assert.IsType<DialogPlacement>(preferences.DialogPosition("Rigging", "CTransformDialog"));
        Assert.Equal("Rigging", transform.Context);
        Assert.Equal("CTransformDialog", transform.DialogClass);
        Assert.Equal("class CPickerDialog", transform.DockDown);
        Assert.Equal(new ushort[] { 350 }, transform.SizeBarPositions.ToArray());
        Assert.Equal(-1f, transform.X);
        Assert.Equal(0.24975f, transform.Y, 5);

        var stacker = Assert.IsType<DialogPlacement>(preferences.DialogPosition("Animation", "CStackerDialog"));
        Assert.Equal("class CTransformDialog", stacker.DockDown);
        Assert.Equal("DockScreenRight", stacker.DockRight);
        Assert.Equal(new ushort[] { 136 }, stacker.SizeBarPositions.ToArray());

        var spinner = Assert.IsType<DialogPlacement>(preferences.DialogPosition("", "CSpinnerDialog"));
        Assert.Equal("", spinner.Context);

        Assert.Null(preferences.DialogPosition("Rigging", "CNoSuchDialog"));
        Assert.Contains(preferences.DialogPlacements, p => p.DialogClass == "CShortcutDialog" && p.Context == "Animation");
    }

    [Fact]
    public void Preferences_SavesEditedValuesAndRoundTripsByteExact()
    {
        if (TestFiles.Preferences is not { } path) return;

        string first = TempFile("prefs");
        string second = TempFile("prefs");
        try
        {
            var preferences = Preferences.Load(path);
            Assert.True(preferences.TrySet("CSpinnerPreferences", "RotateStep", "30", out string? error), error);
            Assert.True(preferences.TrySet("CGeneralPreferences", "AutoSaveTime", "5", out error), error);
            preferences.SaveAs(first);

            var reloaded = Preferences.Load(first);
            Assert.Equal(30f, reloaded.Get("CSpinnerPreferences", "RotateStep"));
            Assert.Equal(5u, reloaded.AutoSaveMinutes);
            reloaded.SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static string TempFile(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}.akt");
}
