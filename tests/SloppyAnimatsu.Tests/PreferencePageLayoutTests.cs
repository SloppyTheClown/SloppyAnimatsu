using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The preferences pages as the dialog draws them
/// the curated rows and their hand-written labels, the widget kinds, the combo boxes' option
/// names and stored values, the "a stored 0 is drawn as the tool's default" and "lengths in metres" rules,
/// flags bits, and the plain rows that keep every other member reachable. Every test works on a copy in
/// TEMP, so no assertion depends on the user's live values.
/// </summary>
public class PreferencePageLayoutTests
{
    [Fact]
    public void Pages_CoverTheCuratedRowSet()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            Assert.Equal(1, Curated(session, "C3DRenderingPreferences"));
            Assert.Equal(2, Curated(session, "CAnimBankPreferences"));
            Assert.Equal(22, Curated(session, "CAutoRiggerPreferences"));
            Assert.Equal(16, Curated(session, "CColorsPreferences"));
            Assert.Equal(1, Curated(session, "CCurveboardPreferences"));
            Assert.Equal(3, Curated(session, "CDataProtectionPreferences"));
            Assert.Equal(8, Curated(session, "CDisplayPreferences"));
            Assert.Equal(9, Curated(session, "CGeneralPreferences"));
            Assert.Equal(1, Curated(session, "CPickerPreferences"));
            Assert.Equal(1, Curated(session, "CRenderingPreferences"));
            Assert.Equal(10, Curated(session, "CIKPreferences"));
            Assert.Equal(13, Curated(session, "CSkinWorkshopPreferences"));
            Assert.Equal(7, Curated(session, "CSpinnerPreferences"));
            Assert.Equal(7, Curated(session, "CStackerPreferences"));
            Assert.Equal(6, Curated(session, "CTimelinePreferences"));
            Assert.Equal(2, Curated(session, "CTransformPreferences"));
            Assert.Equal(1, Curated(session, "CHierarchyPreferences"));
            Assert.Equal(3, Curated(session, "CUnitAndGridPreferences"));
        });
    }

    [Fact]
    public void Pages_DrawTheCuratedRowsUnderTheirLabels()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            foreach (var entry in session.Entries)
            {
                if (entry.Rows is not { Count: > 0 } specs) continue;
                var expected = specs
                    .Where(spec => spec.Member == null ||
                                   entry.Classes.Any(className => preferences.Page(className)?.Has(spec.Member) == true))
                    .ToList();
                var rows = RowsOf(session, entry.Classes[0]);
                Assert.Equal(expected.Select(spec => spec.Label).ToList(),
                    rows.Take(expected.Count).Select(row => row.Label).ToList());
                Assert.Equal(expected.Select(spec => spec.Widget).ToList(),
                    rows.Take(expected.Count).Select(row => row.Widget).ToList());
            }
        });
    }

    [Fact]
    public void Pages_GeneralDrawsNineOfItsSixteenMembersAndKeepsTheRestReachable()
    {
        WithCopy(preferences =>
        {
            var rows = RowsOf(new PreferencesSession(preferences), "CGeneralPreferences");
            Assert.Equal(
            [
                "Rotate and zoom view on selection",
                "Skip welcome window at startup",
                "Do not show update window until next update",
                "Automatically check for updates at startup",
                "Autosaving files rate (minutes)",
                "Undo/Redo max actions count",
                "Reverse RMB Zoom View",
                "Suppr button delete keyframes in Animation mode",
                "Deselect by single click in empty space",
            ], rows.Take(9).Select(row => row.Label).ToList());
            var appended = rows.Skip(9).ToList();
            Assert.NotEmpty(appended);
            Assert.All(appended, row => Assert.Equal(row.Member, row.Label));
            // The preferred paths and the play mode are not drawn by 2020-3-13's page, so they keep their
            // own rows; the members kept only for akeytsu's layout are not listed.
            Assert.Contains(appended, row => row.Member == "PreferredPaths.LoadPath");
            Assert.Equal(PreferenceRowWidget.Text, Row(rows, "PreferredPaths.LoadPath").Widget);
            Assert.DoesNotContain(appended, row => Preferences.HiddenMembers.Contains(row.Member));
            Assert.Contains(appended, row => row.Member == "PlayAnimMode");
        });
    }

    [Fact]
    public void Pages_DrawAStoredZeroAsTheToolsDefault()
    {
        WithCopy(preferences =>
        {
            foreach (string member in new[]
                     {
                         "JointRenderSize", "JointRenderSizeHighDensity", "PivotRenderSize",
                         "UnselectedPivotRenderSize",
                     })
                preferences.Page("CDisplayPreferences")!.Set(member, 0f);
            foreach (string member in new[]
                     {
                         "TargetRenderSize", "BankAngleMax", "TwistAngleMax", "RollAngleBackMax",
                         "RollAnglePreMax", "RollAngleToesMax", "RollAngleFullMax", "SnapAngleThreshold",
                     })
                preferences.Page("CIKPreferences")!.Set(member, 0f);
            preferences.Page("CUnitAndGridPreferences")!.Set("GridSize", 0u);
            preferences.Page("CUnitAndGridPreferences")!.Set("UnitSubdivision", 0u);
            var session = new PreferencesSession(preferences);

            var decorators = RowsOf(session, "CDisplayPreferences");
            Assert.Equal("5.000", Row(decorators, "JointRenderSize").Text);
            Assert.Equal("6.000", Row(decorators, "JointRenderSizeHighDensity").Text);
            Assert.Equal("3.000", Row(decorators, "PivotRenderSize").Text);
            Assert.Equal("0.400", Row(decorators, "UnselectedPivotRenderSize").Text);
            Assert.True(Row(decorators, "JointRenderSize").IsDefault);
            // The live profile holds denormal garbage instead of 0 in two of the four factors
            // (1.1E-33, 9E-43) and the app still draws its default, so "unset" cannot be an exact == 0.
            preferences.Page("CDisplayPreferences")!.Set("PivotRenderSize", 1.1043035E-33f);
            Assert.Equal("3.000", Row(decorators, "PivotRenderSize").Text);
            Assert.True(Row(decorators, "PivotRenderSize").IsDefault);

            var rig = RowsOf(session, "CIKPreferences");
            Assert.Equal("m 0.080", Row(rig, "TargetRenderSize").Text);
            Assert.Equal("60.000", Row(rig, "BankAngleMax").Text);
            Assert.Equal("60.000", Row(rig, "RollAnglePreMax").Text);
            Assert.Equal("75.000", Row(rig, "RollAngleToesMax").Text);
            Assert.Equal("85.000", Row(rig, "RollAngleFullMax").Text);
            Assert.Equal("5.000", Row(rig, "SnapAngleThreshold").Text);
            Assert.True(Row(rig, "TargetRenderSize").IsDefault);

            var units = RowsOf(session, "CUnitAndGridPreferences");
            // The new-scene grid is 4 (its CScene constructor and SceneFactory); 10 was the
            // probe scene's value, which the page drew because the row follows the open scene.
            Assert.Equal("4", Row(units, "GridSize").Text);
            Assert.Equal("5", Row(units, "UnitSubdivision").Text);
            Assert.True(Row(units, "GridSize").IsDefault);

            // A stored value other than 0 is drawn as itself.
            preferences.Page("CUnitAndGridPreferences")!.Set("GridSize", 12u);
            Assert.Equal("12", Row(units, "GridSize").Text);
            Assert.False(Row(units, "GridSize").IsDefault);

            // The two roll limits are not interchangeable: by patching the scene's
            // RollAnglePreMax/RollAngleToesMax to 111/222, the page put 111 on bending and 222 on
            // unbending. The defaults alone (60 and 75) would pass even with the two rows swapped.
            preferences.Page("CIKPreferences")!.Set("RollAnglePreMax", 111f);
            preferences.Page("CIKPreferences")!.Set("RollAngleToesMax", 222f);
            Assert.Equal("111.000",
                rig.Single(row => row.Label == "RF roll angle bending limit value").Text);
            Assert.Equal("222.000",
                rig.Single(row => row.Label == "RF roll angle unbending limit value").Text);
        });
    }

    [Fact]
    public void Pages_DrawLengthsInMetresAndWriteThemBackInCentimetres()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CSpinnerPreferences")!.Set("MoveStep", 0.1f);
            var move = Row(RowsOf(new PreferencesSession(preferences), "CSpinnerPreferences"), "MoveStep");
            Assert.Equal("m 0.001", move.Text);
            Assert.True(move.TrySetFromText("m 0.050", out string? error), error);
            Assert.Equal(5f, (float)preferences.Page("CSpinnerPreferences")!.Get("MoveStep")!);
            Assert.Equal("m 0.050", move.Text);
            Assert.True(move.TrySetFromText("0.020", out error), error);
            Assert.Equal(2f, (float)preferences.Page("CSpinnerPreferences")!.Get("MoveStep")!);
        });
    }

    [Fact]
    public void Pages_DrawTheCuratedComboOptions()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CAnimBankPreferences")!.Set("ResizementMode", 7);
            preferences.Page("CColorsPreferences")!.Set("SingleOrGradientColor", 1);
            preferences.Page("CTimelinePreferences")!.Set("TimeUnit", 1);
            preferences.Page("CUnitAndGridPreferences")!.Set("SizeUnit", 3);
            preferences.Page("CDisplayPreferences")!.Set("JointDisplayMode", 0);
            preferences.Page("CRenderingPreferences")!.Set("PerformancesUserChoice", 1);
            preferences.Page("CSkinWorkshopPreferences")!.Set("RigidSkinningAlgorithm", 1);
            preferences.Page("CSkinWorkshopPreferences")!.Set("VoxelAccuracy", 1);
            preferences.Page("CStackerPreferences")!.Set("GhostsType", 0);
            var session = new PreferencesSession(preferences);

            var windowSize = Row(RowsOf(session, "CAnimBankPreferences"), "ResizementMode");
            Assert.Equal(
            [
                "Standard", "(Hovering) x1.5", "(Hovering) x2", "(Hovering) x2.5", "(Hovering) x3",
                "(Hovering) x3.5", "(Hovering) x4", "(Frozen) x1.5", "(Frozen) x2", "(Frozen) x2.5",
                "(Frozen) x3", "(Frozen) x3.5", "(Frozen) x4",
            ], windowSize.OptionNames);
            Assert.Equal(7, windowSize.OptionIndex);
            Assert.Equal("(Frozen) x1.5", windowSize.Text);

            var gradient = Row(RowsOf(session, "CColorsPreferences"), "SingleOrGradientColor");
            Assert.Equal(["Two colors with gradient", "Single color"], gradient.OptionNames);
            Assert.Equal(0, gradient.OptionIndex);
            Assert.Equal("Two colors with gradient", gradient.Text);

            var timeUnit = Row(RowsOf(session, "CTimelinePreferences"), "TimeUnit");
            Assert.Equal(["Frames", "Milliseconds"], timeUnit.OptionNames);
            Assert.Equal(0, timeUnit.OptionIndex);
            Assert.Equal("Frames", timeUnit.Text);

            var sizeUnit = Row(RowsOf(session, "CUnitAndGridPreferences"), "SizeUnit");
            Assert.Equal(["millimeter", "centimeter", "decimeter", "meter", "inch", "foot", "yard"],
                sizeUnit.OptionNames);
            Assert.Equal(3, sizeUnit.OptionIndex);
            Assert.Equal("meter", sizeUnit.Text);
            // The combo draws its options in value order: meter is 3, not 0
            preferences.Page("CUnitAndGridPreferences")!.Set("SizeUnit", 0);
            Assert.Equal(0, sizeUnit.OptionIndex);
            Assert.Equal("millimeter", sizeUnit.Text);

            Assert.Equal(["Light", "Classic"],
                Row(RowsOf(session, "CDisplayPreferences"), "JointDisplayMode").OptionNames);
            Assert.Equal(["Fast", "Balanced", "Beautiful"],
                Row(RowsOf(session, "CRenderingPreferences"), "PerformancesUserChoice").OptionNames);
            var skin = RowsOf(session, "CSkinWorkshopPreferences");
            Assert.Equal(["Joint proximity", "Geodesic voxel"], Row(skin, "RigidSkinningAlgorithm").OptionNames);
            Assert.Equal(["Rough", "Balanced", "Accurate"], Row(skin, "VoxelAccuracy").OptionNames);
            Assert.Equal(["Keys", "Frames"],
                Row(RowsOf(session, "CStackerPreferences"), "GhostsType").OptionNames);
        });
    }

    [Fact]
    public void Pages_WriteAComboByNameAndRefuseAnUnknownOption()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            var joint = Row(RowsOf(session, "CDisplayPreferences"), "JointDisplayMode");
            Assert.True(joint.TrySetFromText("Classic", out _));
            Assert.Equal(1, (int)preferences.Page("CDisplayPreferences")!.Get("JointDisplayMode")!);
            Assert.True(joint.TrySetFromText("Light", out _));
            Assert.Equal(0, (int)preferences.Page("CDisplayPreferences")!.Get("JointDisplayMode")!);

            // Time unit draws Frames (1) first and Milliseconds (0) second.
            var timeUnit = Row(RowsOf(session, "CTimelinePreferences"), "TimeUnit");
            Assert.True(timeUnit.TrySetFromText("Milliseconds", out _));
            Assert.Equal(0, (int)preferences.Page("CTimelinePreferences")!.Get("TimeUnit")!);

            // The Size unit combo's options are its values in drawing order (0 = millimeter, 3 = meter).
            var sizeUnit = Row(RowsOf(session, "CUnitAndGridPreferences"), "SizeUnit");
            Assert.True(sizeUnit.TrySetFromText("inch", out string? error), error);
            Assert.Equal(4, (int)preferences.Page("CUnitAndGridPreferences")!.Get("SizeUnit")!);
            Assert.True(sizeUnit.TrySetFromText("meter", out _));
            Assert.Equal(3, (int)preferences.Page("CUnitAndGridPreferences")!.Get("SizeUnit")!);
            Assert.True(sizeUnit.TrySetFromText("millimeter", out _));
            Assert.Equal(0, (int)preferences.Page("CUnitAndGridPreferences")!.Get("SizeUnit")!);
            Assert.False(sizeUnit.TrySetFromText("furlong", out error));
            Assert.Contains("not one of its options", error);
        });
    }

    [Fact]
    public void Pages_DrawNumbersWithTheCuratedDecimals()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CSkinWorkshopPreferences")!.Set("WeightStep", 0.1f);
            preferences.Page("CGeneralPreferences")!.Set("AutoSaveTime", 10u);
            var session = new PreferencesSession(preferences);
            var skin = RowsOf(session, "CSkinWorkshopPreferences");
            var weight = Row(skin, "WeightStep");
            Assert.Equal("0.10", weight.Text);
            Assert.True(weight.TrySetFromText("0.25", out _));
            Assert.Equal(0.25f, (float)preferences.Page("CSkinWorkshopPreferences")!.Get("WeightStep")!);
            Assert.Equal("0.25", weight.Text);
            // 0.004 is written as typed; only the integer fields round.
            Assert.True(Row(skin, "PruneLimit").TrySetFromText("0.004", out _));
            Assert.Equal(0.004f, (float)preferences.Page("CSkinWorkshopPreferences")!.Get("PruneLimit")!);

            var general = RowsOf(session, "CGeneralPreferences");
            var autoSave = Row(general, "AutoSaveTime");
            Assert.Equal("10", autoSave.Text);
            Assert.True(autoSave.TrySetFromText("12", out _));
            Assert.Equal(12u, (uint)preferences.Page("CGeneralPreferences")!.Get("AutoSaveTime")!);
            Assert.False(autoSave.TrySetFromText("12.5", out string? error));
            Assert.Contains("AutoSaveTime", error);
            Assert.False(autoSave.TrySetFromText("twelve", out error));
            Assert.Contains("AutoSaveTime", error);
        });
    }

    [Fact]
    public void Pages_EditAFlagsMemberBitByBit()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CSkeletonPreferences")!.Set("Flags", (byte)4);
            preferences.Page("CStackerPreferences")!.Set("Flags", 1u);
            var session = new PreferencesSession(preferences);

            var autoOrient = Row(RowsOf(session, "CSkeletonPreferences"), "Flags");
            Assert.Equal(PreferenceRowWidget.Check, autoOrient.Widget);
            Assert.Equal(4, autoOrient.BitMask);
            Assert.Equal("True", autoOrient.Text);
            Assert.True(autoOrient.TrySetFromText("False", out _));
            Assert.Equal((byte)0, (byte)preferences.Page("CSkeletonPreferences")!.Get("Flags")!);
            Assert.True(autoOrient.TrySetFromText("True", out _));
            Assert.Equal((byte)4, (byte)preferences.Page("CSkeletonPreferences")!.Get("Flags")!);

            var stacker = RowsOf(session, "CStackerPreferences");
            var playing = Labelled(stacker, "Ghosts on playing");
            var wire = Labelled(stacker, "Ghosts always wire");
            Assert.Equal("True", playing.Text);
            Assert.Equal("False", wire.Text);
            Assert.Equal("Ghosts on playing", wire.EnabledBy);
            Assert.True(wire.TrySetFromText("True", out _));
            Assert.Equal(3u, (uint)preferences.Page("CStackerPreferences")!.Get("Flags")!);
            Assert.True(playing.TrySetFromText("False", out _));
            Assert.Equal(2u, (uint)preferences.Page("CStackerPreferences")!.Get("Flags")!);
            Assert.Equal("True", wire.Text);
        });
    }

    [Fact]
    public void Pages_TakeTheSceneFrameRateFromTheOpenScene()
    {
        WithCopy(preferences =>
        {
            var scene = Labelled(RowsOf(new PreferencesSession(preferences, 24), "CTimelinePreferences"),
                "Scene Frame Rate");
            Assert.Equal("24", scene.Text);
            Assert.Equal(PreferenceRowWidget.Number, scene.Widget);
            Assert.Null(scene.Field);
            Assert.False(scene.Editable);

            var noScene = Labelled(RowsOf(new PreferencesSession(preferences), "CTimelinePreferences"),
                "Scene Frame Rate");
            Assert.Equal("", noScene.Text);
        });
    }

    [Fact]
    public void Pages_DrawTheSceneBackedRowsFromTheOpenScene()
    {
        WithCopy(preferences =>
        {
            // Profile values that must lose to the scene's: the app draws these rows from CScene.
            preferences.Page("CUnitAndGridPreferences")!.Set("SizeUnit", 0);
            preferences.Page("CUnitAndGridPreferences")!.Set("GridSize", 1u);
            preferences.Page("CUnitAndGridPreferences")!.Set("UnitSubdivision", 2u);
            preferences.Page("CIKPreferences")!.Set("TargetRenderSize", 1f);
            preferences.Page("CIKPreferences")!.Set("BankAngleMax", 1f);
            preferences.Page("CIKPreferences")!.Set("TwistAngleMax", 2f);
            preferences.Page("CIKPreferences")!.Set("RollAngleBackMax", 3f);
            preferences.Page("CIKPreferences")!.Set("RollAnglePreMax", 4f);
            preferences.Page("CIKPreferences")!.Set("RollAngleToesMax", 5f);
            preferences.Page("CIKPreferences")!.Set("RollAngleFullMax", 6f);
            preferences.Page("CIKPreferences")!.Set("SnapAngleThreshold", 0f);

            var model = SceneFactory.NewSceneModel(frameRate: 24);
            var scene = model.Scene;
            scene.Source.Set("SizeUnit", 5);
            scene.Source.Set("GridSize", 12u);
            scene.Source.Set("UnitSubdivision", 7u);
            scene.Source.Set("TargetRenderSize", 11f);
            scene.Source.Set("BankAngleMax", 70f);
            scene.Source.Set("TwistAngleMax", 71f);
            scene.Source.Set("RollAngleBackMax", 72f);
            scene.Source.Set("RollAnglePreMax", 73f);
            scene.Source.Set("RollAngleToesMax", 74f);
            scene.Source.Set("RollAngleFullMax", 75f);
            scene.Source.Set("SnapAngleThreshold", 33f);

            var session = new PreferencesSession(preferences, 24, scene);
            var units = RowsOf(session, "CUnitAndGridPreferences");
            var size = Row(units, "SizeUnit");
            Assert.True(size.IsSceneBacked);
            Assert.Equal("CScene", size.ClassName);
            Assert.Equal("foot", size.Text);
            Assert.Equal(5, size.OptionIndex);
            Assert.Equal("12", Row(units, "GridSize").Text);
            Assert.Equal("7", Row(units, "UnitSubdivision").Text);

            var rig = RowsOf(session, "CIKPreferences");
            Assert.Equal("m 0.110", Row(rig, "TargetRenderSize").Text);
            Assert.Equal("70.000", Row(rig, "BankAngleMax").Text);
            Assert.Equal("71.000", Row(rig, "TwistAngleMax").Text);
            Assert.Equal("72.000", Row(rig, "RollAngleBackMax").Text);
            Assert.Equal("73.000", Row(rig, "RollAnglePreMax").Text);
            Assert.Equal("74.000", Row(rig, "RollAngleToesMax").Text);
            Assert.Equal("75.000", Row(rig, "RollAngleFullMax").Text);
            // The snap threshold is in the scene-backed set: with the scene patched to
            // 9.5 the page drew 9.500, and an edit to 12.5 landed in CScene.
            var snap = Row(rig, "SnapAngleThreshold");
            Assert.True(snap.IsSceneBacked);
            Assert.Equal("CScene", snap.ClassName);
            Assert.Equal("33.000", snap.Text);

            // The member a scene-bound row hides keeps no duplicate plain row.
            Assert.DoesNotContain(units, row => row.Member == "SizeUnit" && !row.IsSceneBacked);
            Assert.DoesNotContain(rig, row => row.Member == "RollAnglePreMax" && !row.IsSceneBacked);
        });
    }

    [Fact]
    public void Pages_WriteTheSceneBackedRowsBackIntoTheScene()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CUnitAndGridPreferences")!.Set("SizeUnit", 0);
            var model = SceneFactory.NewSceneModel();
            var scene = model.Scene;
            var session = new PreferencesSession(preferences, scene: scene);
            Assert.False(session.SceneEdited);

            var units = RowsOf(session, "CUnitAndGridPreferences");
            Assert.True(Row(units, "SizeUnit").TrySetFromText("yard", out string? error), error);
            Assert.Equal(6, scene.Source.Get<int>("SizeUnit"));
            // The profile's copy stays alone: the scene is where the value is drawn from and written.
            Assert.Equal(0, (int)preferences.Page("CUnitAndGridPreferences")!.Get("SizeUnit")!);

            var rig = RowsOf(session, "CIKPreferences");
            Assert.Equal("m 0.080", Row(rig, "TargetRenderSize").Text);
            Assert.True(Row(rig, "TargetRenderSize").TrySetFromText("m 0.120", out error), error);
            Assert.Equal(12f, scene.Source.Get<float>("TargetRenderSize"));

            object? profileRoll = preferences.Page("CIKPreferences")!.Get("RollAnglePreMax");
            Assert.True(Row(rig, "RollAnglePreMax").TrySetFromText("120", out error), error);
            Assert.Equal(120f, scene.Source.Get<float>("RollAnglePreMax"));
            Assert.Equal(profileRoll, preferences.Page("CIKPreferences")!.Get("RollAnglePreMax"));

            // The snap threshold is scene-backed too, so it writes CScene and
            // leaves the profile's own copy alone.
            object? profileSnap = preferences.Page("CIKPreferences")!.Get("SnapAngleThreshold");
            Assert.True(Row(rig, "SnapAngleThreshold").TrySetFromText("7.500", out error), error);
            Assert.Equal(7.5f, scene.Source.Get<float>("SnapAngleThreshold"));
            Assert.Equal(profileSnap, preferences.Page("CIKPreferences")!.Get("SnapAngleThreshold"));
        });
    }

    [Fact]
    public void Session_SavesSceneBackedRowsIntoTheSceneAndRestoresThemOnCancel()
    {
        WithCopy(preferences =>
        {
            var model = SceneFactory.NewSceneModel();
            var scene = model.Scene;
            var session = new PreferencesSession(preferences, scene: scene);
            var grid = Row(RowsOf(session, "CUnitAndGridPreferences"), "GridSize");
            Assert.False(session.SceneEdited);

            // The dialog's path: only the changed rows are sent, and Save commits them.
            var edits = PreferencesSession.Changed([(grid, "9")]);
            Assert.Single(edits);
            Assert.True(session.TrySave(edits));
            Assert.True(session.SceneEdited);
            Assert.Equal(9u, scene.Source.Get<uint>("GridSize"));

            // Cancel after a save puts back the saved value, not the one from when the dialog opened.
            Assert.True(grid.TrySetFromText("11", out string? error), error);
            Assert.Equal(11u, scene.Source.Get<uint>("GridSize"));
            session.Restore();
            Assert.Equal(9u, scene.Source.Get<uint>("GridSize"));
        });
    }

    [Fact]
    public void Pages_DrawTheLinkAndTheUntracedBoxWithoutEditingThem()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            var link = Labelled(RowsOf(session, "CDataProtectionPreferences"), "Read privacy policy online");
            Assert.Equal(PreferenceRowWidget.Link, link.Widget);
            Assert.Equal("PrivacyPolicyHyperlink", link.Member);
            Assert.False(link.Editable);
            Assert.False(link.TrySetFromText("True", out string? error));
            Assert.Contains("link", error);

            var hints = Labelled(RowsOf(session, "CSpinnerPreferences"), "Show axis hints");
            Assert.Equal(PreferenceRowWidget.Check, hints.Widget);
            Assert.Null(hints.Member);
            Assert.False(hints.Editable);
            Assert.Contains("not traced", hints.Note);
        });
    }

    [Fact]
    public void Pages_KeepEveryMemberOfAnUnlistedPage()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            int index = session.IndexOf("CMirrorPreferences");
            Assert.True(index >= 0);
            Assert.Equal("Mirror preferences", session.Title(index));
            var rows = session.Rows(index);
            var fields = preferences.Page("CMirrorPreferences")!.Fields.Where(field => field.Path != "Name").ToList();
            Assert.Equal(fields.Count, rows.Count);
            Assert.All(rows, row => Assert.Equal(row.Member, row.Label));
            Assert.All(rows, row => Assert.True(row.Editable));
        });
    }

    [Fact]
    public void Changed_SendsOnlyTheRowsWhoseTextWasEdited()
    {
        WithCopy(preferences =>
        {
            preferences.Page("CDisplayPreferences")!.Set("JointRenderSize", 0f);
            var session = new PreferencesSession(preferences);
            var rows = RowsOf(session, "CDisplayPreferences");
            var joint = Row(rows, "JointRenderSize");
            var alpha = Row(rows, "SkinningZoneAlpha");
            Assert.True(joint.IsDefault);

            // A row drawn as a substituted default leaves the stored 0 alone.
            Assert.Empty(PreferencesSession.Changed([(joint, joint.Text), (alpha, alpha.Text)]));

            var edit = Assert.Single(PreferencesSession.Changed([(joint, "2.500"), (alpha, alpha.Text)]));
            Assert.Same(joint.Field, edit.Field);
            Assert.Equal("2.500", edit.Text);

            // Rows with no member never write.
            var link = Labelled(RowsOf(session, "CDataProtectionPreferences"), "Read privacy policy online");
            Assert.Empty(PreferencesSession.Changed([(link, "True")]));
            var fps = Labelled(RowsOf(session, "CTimelinePreferences"), "Scene Frame Rate");
            Assert.Empty(PreferencesSession.Changed([(fps, "48")]));
        });
    }

    [Fact]
    public void Pages_ReadTheirOwnTextBack()
    {
        WithCopy(preferences =>
        {
            var session = new PreferencesSession(preferences);
            for (int index = 0; index < session.Entries.Count; index++)
            {
                foreach (var row in session.Rows(index))
                {
                    if (!row.Editable) continue;
                    string before = row.Text;
                    Assert.True(row.TrySetFromText(before, out string? error), $"{row.Label}: {error}");
                    Assert.True(string.Equals(before, row.Text, StringComparison.Ordinal),
                        $"{row.ClassName}.{row.Member} ({row.Label}): drew '{before}', read back '{row.Text}'");
                }
            }
        });
    }

    /// <summary>How many curated rows a page draws before the plain rows for the members it hides.</summary>
    private static int Curated(PreferencesSession session, string className) =>
        RowsOf(session, className).TakeWhile(row => !string.Equals(row.Label, row.Member, StringComparison.Ordinal)).Count();

    private static IReadOnlyList<PreferenceRow> RowsOf(PreferencesSession session, string className)
    {
        int index = session.IndexOf(className);
        Assert.True(index >= 0, $"no dialog row shows {className}");
        return session.Rows(index);
    }

    private static PreferenceRow Row(IReadOnlyList<PreferenceRow> rows, string member) =>
        Assert.Single(rows.Where(row => row.Member == member));

    private static PreferenceRow Labelled(IReadOnlyList<PreferenceRow> rows, string label) =>
        Assert.Single(rows.Where(row => row.Label == label));

    /// <summary>Runs a test on a copy of the live preferences file; a machine without one runs nothing.</summary>
    private static void WithCopy(Action<Preferences> body)
    {
        if (TestFiles.Preferences is not { } source) return;
        string copy = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-prefpages-{Guid.NewGuid():N}.akt");
        try
        {
            File.Copy(source, copy);
            body(Preferences.Load(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }
}
