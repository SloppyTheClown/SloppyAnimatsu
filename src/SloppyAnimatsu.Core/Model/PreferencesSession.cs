namespace SloppyAnimatsu.Core.Model;

/// <summary>One text box of the preferences dialog: the member it edits and the text in it.</summary>
public readonly record struct PreferenceEdit(PreferenceField Field, string? Text)
{
    /// <summary>An untouched text box, holding the member's current text.</summary>
    public static PreferenceEdit Of(PreferenceField field) => new(field, field.Format());
}

/// <summary>
/// What writing a page's text boxes into the document decided. Every value that parses is written,
/// so one bad row does not throw the other edits away; the failures come back as reasons plus the
/// multi-line status the dialog shows.
/// </summary>
public readonly record struct PreferenceCommit(bool Ok, IReadOnlyList<string> Errors, string Status)
{
    public static readonly PreferenceCommit Accepted = new(true, Array.Empty<string>(), "");
}

/// <summary>How <see cref="PreferencesSession.SelectPage"/> left the dialog.</summary>
public enum PreferencePageChange
{
    /// <summary>Nothing was asked for (no such page): the dialog stays as it is.</summary>
    Unchanged,
    /// <summary>The page was already showing.</summary>
    Same,
    /// <summary>The page's values were written and the dialog shows <see cref="PreferencesSession.Page"/>.</summary>
    Moved,
    /// <summary>A value did not parse: the page stays, and the values that did parse are already written.</summary>
    Refused,
}

/// <summary>
/// The preferences dialog's own code, moved out of the window so it can be tested without Avalonia
/// It owns the three decisions the window
/// used to make: which rows a page draws (the curated rows, <see cref="PreferenceLayout"/>), which
/// of them a commit sends (<see cref="Changed"/>), and when a page's text boxes are written into the
/// document - on a page switch, on Save, and on Cancel/Cancel-by-close, which puts the file back
/// the way it was when the dialog opened.
/// </summary>
public sealed class PreferencesSession
{
    /// <summary>Shown when the file has no preference page at all.</summary>
    public const string NoPagesText = "This preferences file has no preference pages.";

    /// <summary>Shown for a page without a writable leaf.</summary>
    public const string NoValuesText = "This page has no editable values.";

    /// <summary>How many failed values a commit lists before the "... and N more." line.</summary>
    public const int MaxReportedErrors = 4;

    private readonly List<IReadOnlyList<PreferenceRow>> _rows = new();
    private readonly Dictionary<PreferenceField, string> _originals = new();
    private readonly List<PreferenceField> _sceneFields = new();

    /// <param name="preferences">The file the dialog edits; nothing is written until <see cref="TrySave"/>.</param>
    /// <param name="sceneFrameRate">
    /// The open scene's frame rate, which Timebar's *Scene Frame Rate* row follows instead of a profile
    /// value; null when no scene is open.
    /// </param>
    /// <param name="scene">
    /// The open scene, whose <c>CScene</c> values the scene-backed rows draw and write
    /// Null when no scene is open: those rows then fall back to the profile.
    /// </param>
    public PreferencesSession(Preferences preferences, int? sceneFrameRate = null, Scene? scene = null)
    {
        Preferences = preferences;
        // The left list is the catalogue's rows (the flat, alphabetical titles), not the file's
        // node order: one row can show two pages' members (Rig Atelier shows the skeleton and IK
        // pages), and a page the catalogue does not know keeps a row of its own.
        Entries = PreferenceCatalogue.For(preferences);
        var pages = preferences.Pages;
        foreach (var page in pages)
            foreach (var field in page.Fields)
                _originals[field] = field.Format();
        foreach (var entry in Entries)
        {
            var rows = PreferenceLayout.Build(entry, pages, sceneFrameRate, scene);
            foreach (var row in rows)
                if (row.IsSceneBacked && row.Field is { } sceneField) _sceneFields.Add(sceneField);
            _rows.Add(rows);
        }
        // Cancel puts the scene back too; Save makes the written values the new restore target.
        foreach (var field in _sceneFields) _originals[field] = field.Format();
        Page = Entries.Count > 0 ? 0 : -1;
        Status = Entries.Count > 0 ? "" : NoPagesText;
    }

    public Preferences Preferences { get; }

    /// <summary>The left list: the catalogue's rows for this file, in dialog order.</summary>
    public IReadOnlyList<PreferenceCatalogue.Entry> Entries { get; }

    /// <summary>The row the dialog shows, or -1 when the file has none.</summary>
    public int Page { get; private set; }

    /// <summary>The row's title (the name for it), or empty for an index outside the list.</summary>
    public string Title(int pageIndex) =>
        pageIndex >= 0 && pageIndex < Entries.Count ? Entries[pageIndex].Title : "";

    /// <summary>The row that shows a page class (<c>CTransformPreferences</c>), or -1.</summary>
    public int IndexOf(string className)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (Entries[i].Shows(className)) return i;
        return -1;
    }

    /// <summary>The line under the values: a refused commit, a failed save, or nothing.</summary>
    public string Status { get; private set; }

    /// <summary>Whether the dialog saved, so closing it must not put the old values back.</summary>
    public bool SavedOnClose { get; private set; }

    /// <summary>
    /// Whether the last <see cref="TrySave"/> wrote a scene-backed row, so the window must mark the open
    /// document dirty - the scene, not <c>preferences.akt</c>, holds those values (open-questions 27).
    /// </summary>
    public bool SceneEdited { get; private set; }

    /// <summary>
    /// One row's rows as the page draws them: the curated labels, widgets and combo options in
    /// catalogue order, then a plain row for every member the page does not draw.
    /// </summary>
    public IReadOnlyList<PreferenceRow> Rows(int pageIndex) =>
        pageIndex >= 0 && pageIndex < _rows.Count ? _rows[pageIndex] : Array.Empty<PreferenceRow>();

    /// <summary>
    /// The edits a commit sends: only rows whose text is not the text the page draws. A row drawn as a
    /// substituted default therefore leaves the stored 0 alone on an untouched Save, and a row with no
    /// member (Timebar's scene frame rate, a link) never writes.
    /// </summary>
    public static IReadOnlyList<PreferenceEdit> Changed(IEnumerable<(PreferenceRow Row, string? Text)> shown)
    {
        var edits = new List<PreferenceEdit>();
        foreach (var (row, text) in shown)
            if (row.Editable && row.Field is { } field && text is not null && !row.IsUnchanged(text))
                edits.Add(new PreferenceEdit(field, text));
        return edits;
    }

    /// <summary>The list a growing commit is reported against, in the order the boxes were written.</summary>
    public static PreferenceCommit Commit(IEnumerable<PreferenceEdit> editors)
    {
        var errors = new List<string>();
        foreach (var editor in editors)
            if (!editor.Field.TrySetFromString(editor.Text ?? "", out string? error))
                errors.Add(error!);
        return errors.Count == 0
            ? PreferenceCommit.Accepted
            : new PreferenceCommit(false, errors, StatusText(errors));
    }

    /// <summary>The multi-line status a refused commit shows: four reasons, then a count.</summary>
    public static string StatusText(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0) return "";
        string text = string.Join("\n", errors.Take(MaxReportedErrors));
        int more = errors.Count - MaxReportedErrors;
        return more > 0 ? $"{text}\n... and {more} more." : text;
    }

    /// <summary>
    /// The user picked a page: the visible boxes are written first, and a value that does not parse
    /// keeps the dialog where it was. The page's other values are already written either way, as
    /// The dialog does; Cancel puts them back.
    /// </summary>
    public PreferencePageChange SelectPage(int index, IEnumerable<PreferenceEdit> editors)
    {
        if (index < 0 || index >= Entries.Count) return PreferencePageChange.Unchanged;
        if (index == Page) return PreferencePageChange.Same;
        var commit = Commit(editors);
        if (!commit.Ok)
        {
            Status = commit.Status;
            return PreferencePageChange.Refused;
        }
        Page = index;
        Status = "";
        return PreferencePageChange.Moved;
    }

    /// <summary>
    /// Save: writes the visible page, then the file, and makes the written values the new restore
    /// target. False leaves the dialog open with <see cref="Status"/> saying why.
    /// </summary>
    public bool TrySave(IEnumerable<PreferenceEdit> editors)
    {
        SceneEdited = false;
        var commit = Commit(editors);
        if (!commit.Ok)
        {
            Status = commit.Status;
            return false;
        }
        SceneEdited = SceneChanged();
        try
        {
            Preferences.Save();
            foreach (var field in _originals.Keys.ToList()) _originals[field] = field.Format();
            SavedOnClose = true;
            Status = "";
            return true;
        }
        catch (Exception e)
        {
            Status = "Could not save the preferences: " + e.Message;
            return false;
        }
    }

    /// <summary>Whether a scene-backed row now differs from what the dialog opened with.</summary>
    private bool SceneChanged()
    {
        foreach (var field in _sceneFields)
            if (_originals.TryGetValue(field, out string? original) &&
                !string.Equals(field.Format(), original, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Cancel (also the title bar's X): every value of the file returns to what it held when the dialog opened.</summary>
    public void Restore()
    {
        foreach (var (field, text) in _originals) field.TrySetFromString(text, out _);
    }
}
