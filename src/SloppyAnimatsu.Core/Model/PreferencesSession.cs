namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// One text box of the preferences dialog: the member it edits, the text in it, and the page row
/// that drew it. With a row, the text is read the way the row draws it (a combo option's name, a
/// "m 1.000" length, a flags bit's True/False); without one it is the member's own text.
/// </summary>
public readonly record struct PreferenceEdit(PreferenceField Field, string? Text, PreferenceRow? Row = null)
{
    /// <summary>Writes the text into the member through the row's conversion when there is a row.</summary>
    public bool TryApply(out string? error) =>
        Row is { } row ? row.TrySetFromText(Text, out error) : Field.TrySetFromString(Text ?? "", out error);

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
/// of them a commit sends (<see cref="Changed"/>), and when a value is written into the document.
/// The dialog writes live: every row change goes through <see cref="Edit"/> (saved at once), a page's
/// <see cref="RestoreDefaults"/> is one step, and <see cref="Undo"/>/<see cref="Redo"/> walk them back.
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
    /// <param name="history">
    /// The undo history to continue, kept by the caller so it outlives one opening of the dialog;
    /// null starts an empty one.
    /// </param>
    public PreferencesSession(Preferences preferences, int? sceneFrameRate = null, Scene? scene = null,
        PreferenceHistory? history = null)
    {
        Preferences = preferences;
        _sceneFrameRate = sceneFrameRate;
        _scene = scene;
        _history = history ?? new PreferenceHistory();
        _history.Adopt(preferences, scene);
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
                edits.Add(new PreferenceEdit(field, text, row));
        return edits;
    }

    /// <summary>The list a growing commit is reported against, in the order the boxes were written.</summary>
    public static PreferenceCommit Commit(IEnumerable<PreferenceEdit> editors)
    {
        var errors = new List<string>();
        foreach (var editor in editors)
            if (!editor.TryApply(out string? error))
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

    /// <summary>Every value of the file returns to what it held when the session opened (or was last saved).</summary>
    public void Restore()
    {
        foreach (var (field, text) in _originals) field.TrySetFromString(text, out _);
    }

    // ---- Live editing: every change is written at once, saved, and can be undone. ----

    private readonly PreferenceHistory _history;
    private List<PreferenceStep> _undo => _history.UndoSteps;
    private List<PreferenceStep> _redo => _history.RedoSteps;
    private readonly Scene? _scene;
    private Preferences? _defaults;
    private readonly int? _sceneFrameRate;

    /// <summary>Whether there is a change to undo or redo.</summary>
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What the next undo or redo would change, for the buttons' hover text.</summary>
    public string? NextUndo => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? NextRedo => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>
    /// Writes one row's new text at once and saves the file, as one undo step. A value that does not
    /// parse writes nothing and returns null with <see cref="Status"/> saying why. Returns the step
    /// written (null when nothing changed or the text was refused).
    /// </summary>
    public PreferenceStep? Edit(PreferenceRow row, string? text)
    {
        if (!row.Editable || row.Field is not { } field || text is null || row.IsUnchanged(text)) return null;
        string before = field.Format();
        if (!row.TrySetFromText(text, out string? error))
        {
            Status = error ?? $"{row.Label}: not a value.";
            return null;
        }
        string after = field.Format();
        if (string.Equals(before, after, StringComparison.Ordinal)) return null;
        var step = new PreferenceStep($"Set {row.Label}", Page,
            [new PreferenceChange(field, before, after, row.IsSceneBacked)], row.IsSceneBacked ? _scene : null);
        _undo.Add(step);
        _redo.Clear();
        Persist(step);
        return step;
    }

    /// <summary>
    /// Puts every row of <paramref name="pageIndex"/> back to its default value as one undoable step.
    /// The remembered folders (<c>PreferredPaths</c>) are the user's, not settings, and are kept.
    /// Returns the step, or null when the page already holds its defaults.
    /// </summary>
    public PreferenceStep? RestoreDefaults(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Entries.Count) return null;
        var defaults = DefaultTexts(pageIndex);
        var changes = new List<PreferenceChange>();
        var errors = new List<string>();
        foreach (var row in Rows(pageIndex))
        {
            if (!row.Editable || row.Field is not { } field || row.Member is not { } member) continue;
            if (member.StartsWith("PreferredPaths.", StringComparison.Ordinal)) continue;
            if (!defaults.TryGetValue((row.Label, member), out string? text) || row.IsUnchanged(text)) continue;
            string before = field.Format();
            if (!row.TrySetFromText(text, out string? error)) { errors.Add(error!); continue; }
            string after = field.Format();
            if (!string.Equals(before, after, StringComparison.Ordinal))
                changes.Add(new PreferenceChange(field, before, after, row.IsSceneBacked));
        }
        Status = StatusText(errors);
        if (changes.Count == 0) return null;
        var step = new PreferenceStep($"Restore {Title(pageIndex)} defaults", pageIndex, changes,
            changes.Any(change => change.SceneBacked) ? _scene : null);
        _undo.Add(step);
        _redo.Clear();
        Persist(step);
        return step;
    }

    /// <summary>Takes back the last change (it may be on another page: <see cref="Page"/> moves to it).</summary>
    public PreferenceStep? Undo() => Move(_undo, _redo, undo: true);

    /// <summary>Puts back the last undone change.</summary>
    public PreferenceStep? Redo() => Move(_redo, _undo, undo: false);

    private PreferenceStep? Move(List<PreferenceStep> from, List<PreferenceStep> to, bool undo)
    {
        if (from.Count == 0) return null;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        foreach (var change in step.Changes)
            change.Field.TrySetFromString(undo ? change.Before : change.After, out _);
        to.Add(step);
        if (step.Page >= 0 && step.Page < Entries.Count) Page = step.Page;
        Persist(step);
        return step;
    }

    /// <summary>
    /// Saves the file after a change of a profile value; a scene value is saved with the scene, so a
    /// step of only scene values leaves the file alone (<see cref="PreferenceStep.TouchesScene"/>).
    /// </summary>
    private void Persist(PreferenceStep step)
    {
        if (step.TouchesScene) SceneEdited = true;
        if (!step.Changes.Any(change => !change.SceneBacked))
        {
            Status = "";
            return;
        }
        try
        {
            Preferences.Save();
            Status = "";
        }
        catch (Exception e)
        {
            Status = "Could not save the preferences: " + e.Message;
        }
    }

    /// <summary>The default text of each row of a page, keyed by (label, member), from <see cref="Preferences.Defaults"/>.</summary>
    private Dictionary<(string, string), string> DefaultTexts(int pageIndex)
    {
        _defaults ??= Preferences.Defaults();
        var texts = new Dictionary<(string, string), string>();
        // Built without a scene, so a scene-backed row reads the profile default of the same member.
        foreach (var row in PreferenceLayout.Build(Entries[pageIndex], _defaults.Pages, _sceneFrameRate))
            if (row.Editable && row.Member is { } member) texts.TryAdd((row.Label, member), row.Text);
        return texts;
    }
}

/// <summary>One value an undoable preferences step changed, as its text before and after.</summary>
public readonly record struct PreferenceChange(PreferenceField Field, string Before, string After, bool SceneBacked);

/// <summary>
/// One undoable change of the preferences dialog: a row edit, or a page's *Restore defaults*.
/// <paramref name="Scene"/> is the scene its scene-backed values were written into.
/// </summary>
public sealed record PreferenceStep(string Label, int Page, IReadOnlyList<PreferenceChange> Changes, Scene? Scene = null)
{
    /// <summary>Whether the step changed a value the open scene holds, so the document is now dirty.</summary>
    public bool TouchesScene => Changes.Any(change => change.SceneBacked);
}

/// <summary>
/// The preferences dialog's undo and redo steps, kept by the main window so they outlive one opening
/// of the dialog. They belong to one preferences document: a session on another document starts
/// empty. Scene-backed values belong to the scene they were written into, so opening the dialog on
/// another scene drops those values from the steps (and a step left with nothing is dropped).
/// </summary>
public sealed class PreferenceHistory
{
    internal List<PreferenceStep> UndoSteps { get; } = new();
    internal List<PreferenceStep> RedoSteps { get; } = new();
    private Preferences? _owner;

    public int UndoCount => UndoSteps.Count;
    public int RedoCount => RedoSteps.Count;

    public void Clear()
    {
        UndoSteps.Clear();
        RedoSteps.Clear();
    }

    internal void Adopt(Preferences preferences, Scene? scene)
    {
        if (!ReferenceEquals(_owner, preferences))
        {
            Clear();
            _owner = preferences;
            return;
        }
        Prune(UndoSteps, scene);
        Prune(RedoSteps, scene);
    }

    private static void Prune(List<PreferenceStep> steps, Scene? scene)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            if (!step.TouchesScene || ReferenceEquals(step.Scene, scene)) continue;
            var kept = step.Changes.Where(change => !change.SceneBacked).ToList();
            if (kept.Count == 0) steps.RemoveAt(i);
            else steps[i] = step with { Changes = kept, Scene = null };
        }
    }
}
