using System.Globalization;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The preferences dialog's own code,
/// moved into <see cref="PreferencesSession"/>: which rows the left list has, what writing the
/// visible widgets does to the document, which page a refused value leaves open, what Cancel puts
/// back and what Save writes. The user's live <c>preferences.akt</c> is read, and written only as a
/// copy in TEMP; no assertion depends on a value in it, because that file drifts as the user edits
/// it.
/// </summary>
public class PreferencesSessionTests
{
    [Fact]
    public void Session_ListsTheCuratedRowsAndTheirEditableValues()
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        var session = new PreferencesSession(preferences);

        Assert.Equal(PreferenceCatalogue.For(preferences).Select(entry => entry.Title).ToList(),
            session.Entries.Select(entry => entry.Title).ToList());
        Assert.Equal(0, session.Page);
        Assert.Equal("", session.Status);
        Assert.False(session.SavedOnClose);

        for (int i = 0; i < session.Entries.Count; i++)
        {
            var entry = session.Entries[i];
            var rows = session.Rows(i);
            // The nodes' own Name is not a value the dialog edits, and the curated rows plus the
            // plain rows for the members it does not draw cover every other leaf exactly once (Rig
            // Atelier's skeleton page inherits the IK page's members, which are drawn once).
            Assert.DoesNotContain(rows, row => row.Member == "Name");
            var drawn = rows.Where(row => row.Member != null).Select(row => row.Member!).ToList();
            // One member keeps one row, except Stacker's flags member, whose two bits are two check boxes.
            var repeated = drawn.GroupBy(member => member, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            Assert.All(repeated, member => Assert.Equal("Flags", member));
            var expected = entry.Classes
                .Select(className => preferences.Page(className)!)
                .SelectMany(page => page.Fields)
                .Where(field => field.Path != "Name" && !Preferences.HiddenMembers.Contains(field.Path))
                .Select(field => field.Path)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(field => field, StringComparer.Ordinal);
            Assert.Equal(expected, drawn.Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal));
            // The dialog lists the same rows every time it shows the row: that is what the restore
            // target was captured from.
            Assert.Same(rows, session.Rows(i));
            Assert.Equal(entry.Title, session.Title(i));
        }
        Assert.Equal("", session.Title(-1));
        Assert.Equal("", session.Title(session.Entries.Count));
        Assert.Empty(session.Rows(-1));
        Assert.Empty(session.Rows(session.Entries.Count));
    }

    [Fact]
    public void Catalogue_ListsThePagesInOrderAndMergesRigAtelier()
    {
        // The 18 titles 2020-3-13 lists, alphabetical.
        Assert.Equal(
        [
            "3D Rendering preferences",
            "Anim Bank preferences",
            "Autorigger preferences",
            "Color preferences",
            "Curveboard preferences",
            "Data protection preferences",
            "Decorators preferences",
            "General preferences",
            "Picker preferences",
            "Rendering preferences",
            "Rig Atelier preferences",
            "Skin Atelier preferences",
            "Spinner preferences",
            "Stacker preferences",
            "Timebar preferences",
            "Transform preferences",
            "Tree preferences",
            "Unit & grid preferences",
        ], PreferenceCatalogue.Curated.Select(entry => entry.Title).ToList());

        var rig = Assert.Single(PreferenceCatalogue.Curated.Where(entry => entry.Title == "Rig Atelier preferences"));
        Assert.Contains("CSkeletonPreferences", rig.Classes);
        Assert.Contains("CIKPreferences", rig.Classes);
        // Decorators draws CDisplayPreferences, not CFacePreferences.
        var decorators = Assert.Single(PreferenceCatalogue.Curated.Where(entry => entry.Title == "Decorators preferences"));
        Assert.Equal(["CDisplayPreferences"], decorators.Classes);
        Assert.Equal("Decorators preferences", PreferenceCatalogue.TitleFor("CDisplayPreferences"));
        Assert.Equal("Rig Atelier preferences", PreferenceCatalogue.TitleFor("CIKPreferences"));
        Assert.Equal("Skinning preferences", PreferenceCatalogue.TitleFor("CSkinningPreferences"));
        Assert.Equal("Contrived preferences", PreferenceCatalogue.TitleFor("CContrivedPreferences"));
    }

    [Fact]
    public void Catalogue_ForAFileCoversEveryPageExactlyOnce()
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        var entries = PreferenceCatalogue.For(preferences);

        // No page is lost and none is listed twice; only the hidden pages (data protection) are left out.
        var listed = entries.SelectMany(entry => entry.Classes).ToList();
        var shown = preferences.Pages.Where(page => !PreferenceCatalogue.IsHidden(page.ClassName)).ToList();
        Assert.Equal(shown.Count, listed.Count);
        Assert.Equal(listed.Distinct().Count(), listed.Count);
        Assert.All(shown, page => Assert.Contains(page.ClassName, listed));
        Assert.DoesNotContain("CDataProtectionPreferences", listed);

        // The curated rows keep the order at the top of the list; a page the catalogue does
        // not know is appended rather than dropping its values from the dialog.
        var measured = PreferenceCatalogue.Curated
            .Where(entry => entries.Any(listed => listed.Title == entry.Title))
            .Select(entry => entry.Title)
            .ToList();
        Assert.True(measured.Count >= 17);
        Assert.Equal(measured, entries.Take(measured.Count).Select(entry => entry.Title).ToList());
    }

    [Fact]
    public void Fields_UseTheExpectedWidgetKinds()
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        var general = preferences.Page("CGeneralPreferences")!;
        var colors = preferences.Page("CColorsPreferences")!;

        var flag = Assert.Single(general.Fields.Where(field => field.Path == "RotateAroundSelection"));
        Assert.Equal(PreferenceWidget.Check, flag.Widget);
        var color = Assert.Single(colors.Fields.Where(field => field.Path == "JointsColor"));
        Assert.Equal(PreferenceWidget.Color, color.Widget);
        var number = Assert.Single(general.Fields.Where(field => field.Path == "AutoSaveTime"));
        Assert.Equal(PreferenceWidget.Text, number.Widget);
    }

    [Fact]
    public void Commit_WritesTheGoodValuesAndReportsTheBadOnes()
    {
        if (TestFiles.Preferences is not { } path) return;
        var session = new PreferencesSession(Preferences.Load(path));
        int general = session.IndexOf("CGeneralPreferences");
        Assert.True(general >= 0);
        var rows = session.Rows(general);
        var autoSave = Assert.Single(rows.Where(row => row.Member == "AutoSaveTime")).Field!;
        var loadPath = Assert.Single(rows.Where(row => row.Member == "PreferredPaths.LoadPath")).Field!;
        string before = (string)loadPath.Value!;

        var commit = PreferencesSession.Commit(
        [
            new PreferenceEdit(autoSave, "not a number"),
            new PreferenceEdit(loadPath, "D:/a/fresh/load/path"),
        ]);

        Assert.False(commit.Ok);
        string error = Assert.Single(commit.Errors);
        Assert.Contains("AutoSaveTime", error);
        Assert.Equal(error, commit.Status);
        // The good value was written even though the page was refused, and the bad one was not.
        Assert.Equal("D:/a/fresh/load/path", loadPath.Value);
        Assert.NotEqual("D:/a/fresh/load/path", before);
        // Cancel puts the file back.
        session.Restore();
        Assert.Equal(before, loadPath.Value);
    }

    [Fact]
    public void StatusText_ListsFourReasonsThenCountsTheRest()
    {
        Assert.Equal("", PreferencesSession.StatusText([]));
        var four = new[] { "a", "b", "c", "d" };
        Assert.Equal("a\nb\nc\nd", PreferencesSession.StatusText(four));
        var six = new[] { "a", "b", "c", "d", "e", "f" };
        Assert.Equal("a\nb\nc\nd\n... and 2 more.", PreferencesSession.StatusText(six));
    }

    [Fact]
    public void SelectPage_RefusesABadValueAndMovesOnAGoodPage()
    {
        if (TestFiles.Preferences is not { } path) return;
        var session = new PreferencesSession(Preferences.Load(path));
        int general = session.IndexOf("CGeneralPreferences");
        int spinner = session.IndexOf("CSpinnerPreferences");
        Assert.True(general > 0);
        Assert.True(spinner > 0);
        Assert.True(spinner != general);
        Assert.Equal(PreferencePageChange.Moved, session.SelectPage(general, []));

        var numeric = Assert.Single(session.Rows(general).Where(row => row.Member == "AutoSaveTime")).Field!;
        var text = Assert.Single(session.Rows(general).Where(row => row.Member == "PreferredPaths.LoadPath")).Field!;

        var refused = session.SelectPage(spinner,
            [new PreferenceEdit(numeric, "not a number"), new PreferenceEdit(text, "a fresh name")]);

        Assert.Equal(PreferencePageChange.Refused, refused);
        Assert.Equal(general, session.Page);
        Assert.Contains(numeric.Path, session.Status);
        // The page's other values were written before the refusal, so the edit is not lost.
        Assert.Equal("a fresh name", text.Value);
        session.Restore();

        Assert.Equal(PreferencePageChange.Moved, session.SelectPage(spinner, []));
        Assert.Equal(spinner, session.Page);
        Assert.Equal("", session.Status);
        Assert.Equal(PreferencePageChange.Same, session.SelectPage(spinner, []));
        Assert.Equal(PreferencePageChange.Unchanged, session.SelectPage(-1, []));
        Assert.Equal(PreferencePageChange.Unchanged, session.SelectPage(session.Entries.Count, []));
        Assert.Equal(spinner, session.Page);
    }

    [Fact]
    public void Save_WritesTheValuesAndMakesThemTheNewCancelTarget()
    {
        if (TestFiles.Preferences is not { } path) return;
        string copy = TempFile("prefs-session");
        try
        {
            File.Copy(path, copy);
            var session = new PreferencesSession(Preferences.Load(copy));
            int spinner = session.IndexOf("CSpinnerPreferences");
            Assert.True(spinner >= 0);
            Assert.Equal(PreferencePageChange.Moved, session.SelectPage(spinner, []));
            var field = Assert.Single(session.Rows(spinner).Where(row => row.Member == "RotateStep")).Field!;
            float current = (float)field.Value!;
            string typed = (current + 3.5f).ToString(CultureInfo.InvariantCulture);

            Assert.True(session.TrySave([new PreferenceEdit(field, typed)]), session.Status);
            Assert.True(session.SavedOnClose);
            Assert.Equal("", session.Status);
            Assert.Equal(current + 3.5f, field.Value);

            var reloaded = Preferences.Load(copy);
            Assert.Equal(current + 3.5f, reloaded.Get("CSpinnerPreferences", "RotateStep"));

            // The written value replaced the value Cancel restores to.
            session.Restore();
            Assert.Equal(current + 3.5f, field.Value);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Save_WithABadValueWritesNothingAndStaysOpen()
    {
        if (TestFiles.Preferences is not { } path) return;
        string copy = TempFile("prefs-refused");
        try
        {
            File.Copy(path, copy);
            var session = new PreferencesSession(Preferences.Load(copy));
            byte[] before = File.ReadAllBytes(copy);
            int general = session.IndexOf("CGeneralPreferences");
            Assert.True(general >= 0);
            Assert.Equal(PreferencePageChange.Moved, session.SelectPage(general, []));
            var numeric = Assert.Single(session.Rows(general).Where(row => row.Member == "AutoSaveTime")).Field!;

            Assert.False(session.TrySave([new PreferenceEdit(numeric, "not a number")]));

            Assert.False(session.SavedOnClose);
            Assert.Contains(numeric.Path, session.Status);
            Assert.Equal(before, File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void Edit_WritesAtOnceAndUndoesAndRedoes()
    {
        if (TestFiles.Preferences is not { } path) return;
        string copy = TempFile("prefs-live");
        try
        {
            File.Copy(path, copy);
            var session = new PreferencesSession(Preferences.Load(copy));
            int spinner = session.IndexOf("CSpinnerPreferences");
            var row = Assert.Single(session.Rows(spinner).Where(r => r.Member == "RotateStep"));
            float current = (float)row.Field!.Value!;
            string Text(float value) => value.ToString(CultureInfo.InvariantCulture);

            // Each applied value is saved at once and is its own undo step.
            Assert.NotNull(session.Edit(row, Text(current + 1f)));
            Assert.NotNull(session.Edit(row, Text(current + 2f)));
            Assert.Equal(current + 2f, Preferences.Load(copy).Get("CSpinnerPreferences", "RotateStep"));
            // A value that does not parse writes nothing and says why.
            Assert.Null(session.Edit(row, "not a number"));
            Assert.NotEqual("", session.Status);
            Assert.Equal(current + 2f, row.Field.Value);

            Assert.True(session.CanUndo);
            Assert.NotNull(session.Undo());
            Assert.Equal(current + 1f, row.Field.Value);
            Assert.NotNull(session.Undo());
            Assert.False(session.CanUndo);
            Assert.Equal(current, row.Field.Value);
            Assert.Equal(current, Preferences.Load(copy).Get("CSpinnerPreferences", "RotateStep"));

            Assert.NotNull(session.Redo());
            Assert.NotNull(session.Redo());
            Assert.Equal(current + 2f, Preferences.Load(copy).Get("CSpinnerPreferences", "RotateStep"));
            // A new edit drops the redo branch.
            Assert.NotNull(session.Undo());
            Assert.NotNull(session.Edit(row, Text(current + 5f)));
            Assert.False(session.CanRedo);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void RestoreDefaults_IsOneUndoableStepForThePage()
    {
        if (TestFiles.Preferences is not { } path) return;
        string copy = TempFile("prefs-defaults");
        try
        {
            File.Copy(path, copy);
            var session = new PreferencesSession(Preferences.Load(copy));
            int spinner = session.IndexOf("CSpinnerPreferences");
            var row = Assert.Single(session.Rows(spinner).Where(r => r.Member == "RotateStep"));
            object? defaultValue = Preferences.Defaults().Get("CSpinnerPreferences", "RotateStep");
            Assert.NotNull(defaultValue);
            float edited = (float)defaultValue! + 7f;
            Assert.NotNull(session.Edit(row, edited.ToString(CultureInfo.InvariantCulture)));

            var step = session.RestoreDefaults(spinner);
            Assert.NotNull(step);
            Assert.Equal(defaultValue, row.Field!.Value);
            Assert.Equal(defaultValue, Preferences.Load(copy).Get("CSpinnerPreferences", "RotateStep"));
            // Every changed row is in that one step; a second restore has nothing to do.
            Assert.Null(session.RestoreDefaults(spinner));

            Assert.Same(step, session.Undo());
            Assert.Equal(edited, row.Field.Value);
            Assert.Same(step, session.Redo());
            Assert.Equal(defaultValue, row.Field.Value);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void History_OutlivesTheSessionForTheSameFileOnly()
    {
        if (TestFiles.Preferences is not { } path) return;
        string copy = TempFile("prefs-history");
        try
        {
            File.Copy(path, copy);
            var preferences = Preferences.Load(copy);
            var history = new PreferenceHistory();
            var first = new PreferencesSession(preferences, history: history);
            int spinner = first.IndexOf("CSpinnerPreferences");
            var row = Assert.Single(first.Rows(spinner).Where(r => r.Member == "RotateStep"));
            float current = (float)row.Field!.Value!;
            Assert.NotNull(first.Edit(row, (current + 4f).ToString(CultureInfo.InvariantCulture)));

            // Closing the dialog and opening it again: the step is still there to undo.
            var second = new PreferencesSession(preferences, history: history);
            Assert.True(second.CanUndo);
            Assert.NotNull(second.Undo());
            Assert.Equal(current, preferences.Get("CSpinnerPreferences", "RotateStep"));
            Assert.Equal(1, history.RedoCount);

            // Another preferences document does not inherit it.
            var other = new PreferencesSession(Preferences.Load(copy), history: history);
            Assert.False(other.CanUndo);
            Assert.False(other.CanRedo);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private static string TempFile(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}.akt");
}