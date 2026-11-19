namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The Animation Retargeting dialog's state:
/// the scene's retargetable characters, the source and target pair, the editable matching grid and
/// The transfer. The action picks the pair (the first and last character while the scene's
/// last-used pair is not modelled), opens a dialog with two skeleton combos and a grid of source
/// rows, and runs the same transfer the CLI's <c>retarget</c> command uses. The grid's rules live
/// here, so the window only draws them: one row per source candidate, a target choice among the
/// target character's candidates or the explicit "none" (the <c>SetMatch(source, null)</c>,
/// recorded in the stored matching's <c>NullIndices</c>).
/// </summary>
public sealed class RetargetSession
{
    /// <summary>
    /// The message 0x3e0, logged when the scene cannot offer two characters to retarget
    /// between; the action then stops.
    /// </summary>
    public const string NeedsTwoCharacters =
        "At least two characters with joints are needed on the scene in order to retarget.";

    /// <summary>
    /// The advice popup about aligning the reference poses. It does
    /// not align anything; the window shows it as a line unless the profile's
    /// <c>CAnimBankPreferences.SkipRetargetingAlignRefPosesWindow</c> suppresses it.
    /// </summary>
    public const string Advice =
        "Reference poses of your characters must be similar to ensure the best retargeting results.";

    /// <summary>The grid entry that stands for a row with no target (the null match).</summary>
    public const string NoneChoice = "(none)";

    private readonly List<Character> _characters;
    private readonly bool _skipAdvice;
    private Character? _source;
    private Character? _target;
    private RetargetingMap? _map;
    private List<RetargetSelection>? _rows;

    /// <param name="model">The scene being edited; the transfer writes into it.</param>
    /// <param name="skipAdvice">
    /// The profile's "Skip retargeting ref pose advice popup" (<c>CAnimBankPreferences</c>).
    /// </param>
    public RetargetSession(SceneModel model, bool skipAdvice = false, string? cacheDirectory = null,
        RetargetMappingStore? store = null)
    {
        Model = model;
        _skipAdvice = skipAdvice;
        _cacheDirectory = cacheDirectory;
        _store = store;
        _mirror = store?.Mirror ?? false;
        _characters = model.Scene.Characters
            .Where(character => RetargetingMap.Candidates(character).Count > 0)
            .ToList();
        // The default pair: the scene's last used characters when it has them, else the first
        // and the last character of the list (
        // The last used pair). The flag sits on the matching of the character that
        // received the animations and points at the source, so the used direction is
        // (pointed-at character) -> (owner).
        _source = _characters.FirstOrDefault();
        _target = _characters.Count > 1 ? _characters[^1] : null;
        if (RetargetingMatching.LastUsed(model) is { } last
            && _characters.Contains(last.Owner) && _characters.Contains(last.Target))
        {
            _source = last.Target;
            _target = last.Owner;
        }
    }

    private readonly string? _cacheDirectory;
    private readonly RetargetMappingStore? _store;
    private List<RetargetSelection>? _copies;

    /// <summary>The scene the transfer writes into.</summary>
    public SceneModel Model { get; }

    /// <summary>
    /// The characters that can be retargeted between: those with at least one controller that
    /// matches (a joint, an IK controller or a reverse-foot controller).
    /// </summary>
    public IReadOnlyList<Character> Characters => _characters;

    /// <summary>The character the animations come from; null when the scene has none to offer.</summary>
    public Character? Source
    {
        get => _source;
        set
        {
            if (value != null && !_characters.Contains(value)) return;
            if (ReferenceEquals(_source, value)) return;
            _source = value;
            ResetGrid();
        }
    }

    /// <summary>The character the animations are copied onto.</summary>
    public Character? Target
    {
        get => _target;
        set
        {
            if (value != null && !_characters.Contains(value)) return;
            if (ReferenceEquals(_target, value)) return;
            _target = value;
            ResetGrid();
        }
    }

    /// <summary>Whether the window draws the reference-pose advice line.</summary>
    public bool ShowAdvice => !_skipAdvice;

    /// <summary>Why the transfer cannot run now, or null when it can.</summary>
    public string? Refusal
    {
        get
        {
            if (_characters.Count < 2) return NeedsTwoCharacters;
            if (Source is null || Target is null) return "Choose a source character and a target character.";
            if (ReferenceEquals(Source, Target)) return "The source and the target are the same character.";
            if (!Source.Animations.Any()) return $"{Source.Name} has no animations to retarget.";
            if (Map is { Pairs.Count: 0 })
                return $"No controller of '{Source.Name}' matches a controller of '{Target.Name}'.";
            return null;
        }
    }

    /// <summary>Whether <see cref="Run"/> will run.</summary>
    public bool CanRetarget => Refusal == null;

    /// <summary>
    /// The mapping the transfer will use: exactly the grid's rows, so a row set to another target or
    /// to "none" changes it (and the footer counts follow).
    /// </summary>
    public RetargetingMap? Map
    {
        get
        {
            if (!HasPair) return null;
            if (_map == null)
            {
                var rows = RowsForPair();
                _map = RetargetingMap.Build(_source!, _target!,
                    rows.Where(row => row.Target != null).Select(row => new RetargetingPair(row.Source, row.Target!)),
                    rows.Where(row => row.Target == null).Select(row => row.Source),
                    CopyPairs());
            }
            return _map;
        }
    }

    /// <summary>
    /// The grid: one row per source candidate in <c>Controllers</c> order. When the pair becomes
    /// current the rows are the scene's stored <see cref="RetargetingMatching"/> for it when that is
    /// usable, else the automatic name mapping; edits then stay for as long as the pair does.
    /// </summary>
    public IReadOnlyList<RetargetSelection> Rows => RowsForPair();

    /// <summary>The target character's controllers a row may choose (the same candidate filter).</summary>
    public IReadOnlyList<Object3D> TargetChoices =>
        HasPair ? RetargetingMap.Candidates(_target!) : [];

    /// <summary>
    /// The joint bank: the target candidates no row holds, in the target's <c>Controllers</c> order.
    /// Dropping one on a row is <see cref="SetTarget"/>; dragging a row's target back here is
    /// <c>SetTarget(source, null)</c>.
    /// </summary>
    public IReadOnlyList<Object3D> UnusedTargets
    {
        get
        {
            if (!HasPair) return [];
            var used = new HashSet<Object3D>(RowsForPair().Concat(Copies).Where(row => row.Target != null)
                .Select(row => row.Target!), ReferenceEqualityComparer.Instance);
            return TargetChoices.Where(candidate => !used.Contains(candidate)).ToList();
        }
    }

    /// <summary>The choice list a row draws: <see cref="NoneChoice"/> first, then the target candidate names.</summary>
    public IReadOnlyList<string> ChoiceNames =>
        TargetChoices.Select(candidate => candidate.Name ?? "").Prepend(NoneChoice).ToList();

    /// <summary>The index of a row's target in <see cref="ChoiceNames"/>; 0 is the "none" entry.</summary>
    public int ChoiceIndexOf(Object3D? target)
    {
        if (target == null) return 0;
        var choices = TargetChoices;
        for (int i = 0; i < choices.Count; i++)
            if (ReferenceEquals(choices[i], target)) return i + 1;
        return 0;
    }

    /// <summary>
    /// Sets one row's target; null is the "none" (the source is recorded in <c>NullIndices</c>
    /// and left out of the pairs). A target another row already holds moves to this row, because a
    /// mapping is one to one. Returns whether the grid changed.
    /// </summary>
    public bool SetTarget(Object3D source, Object3D? target)
    {
        if (!HasPair) return false;
        if (target != null && !RetargetingMap.Candidates(_target!).Contains(target)) return false;
        var rows = RowsForPair().ToList();
        int index = rows.FindIndex(row => ReferenceEquals(row.Source, source));
        if (index < 0) return false;
        if (ReferenceEquals(rows[index].Target, target)) return false;
        if (target != null)
        {
            for (int i = 0; i < rows.Count; i++)
                if (i != index && ReferenceEquals(rows[i].Target, target))
                    rows[i] = rows[i] with { Target = null };
            ReleaseFromCopies(target, -1);
        }
        rows[index] = rows[index] with { Target = target };
        _rows = rows;
        _map = null;
        Remember();
        return true;
    }

    /// <summary>
    /// SloppyAnimatsu's copy rows: further rows of
    /// a source that already has one, so one source joint drives several target joints (an old
    /// three-finger rig's third finger onto both the ring and the pinky). Each holds its own target;
    /// a target stays one to one across the rows and the copies.
    /// </summary>
    public IReadOnlyList<RetargetSelection> Copies
    {
        get
        {
            RowsForPair();
            return _copies ?? [];
        }
    }

    /// <summary>Adds an empty copy row of <paramref name="source"/>; returns its index in <see cref="Copies"/>, or -1.</summary>
    public int AddCopy(Object3D source)
    {
        if (!HasPair || !RowsForPair().Any(row => ReferenceEquals(row.Source, source))) return -1;
        _copies ??= [];
        _copies.Add(new RetargetSelection(source, null));
        _map = null;
        Remember();
        return _copies.Count - 1;
    }

    /// <summary>Sets a copy row's target, taking it from whichever row or copy held it. Returns whether anything changed.</summary>
    public bool SetCopyTarget(int index, Object3D? target)
    {
        if (!HasPair || _copies == null || index < 0 || index >= _copies.Count) return false;
        if (target != null && !RetargetingMap.Candidates(_target!).Contains(target)) return false;
        if (ReferenceEquals(_copies[index].Target, target)) return false;
        if (target != null)
        {
            var rows = RowsForPair().ToList();
            for (int i = 0; i < rows.Count; i++)
                if (ReferenceEquals(rows[i].Target, target))
                    rows[i] = rows[i] with { Target = null };
            _rows = rows;
            ReleaseFromCopies(target, index);
        }
        _copies[index] = _copies[index] with { Target = target };
        _map = null;
        Remember();
        return true;
    }

    /// <summary>Deletes a copy row; its target goes back to the joint bank.</summary>
    public bool RemoveCopy(int index)
    {
        if (_copies == null || index < 0 || index >= _copies.Count) return false;
        _copies.RemoveAt(index);
        _map = null;
        Remember();
        return true;
    }

    /// <summary>
    /// Whether an edit made through <see cref="AddTarget"/>, <see cref="ReplaceTarget"/> or
    /// <see cref="RemoveTarget"/> is repeated on the other side: the mirrored source
    /// (<see cref="RetargetNames.MirrorName"/>) gets the mirrored target. It applies only when both
    /// names carry a side and both mirrored controllers exist. Kept in the mapping store.
    /// </summary>
    public bool Mirror
    {
        get => _mirror;
        set
        {
            if (_mirror == value) return;
            _mirror = value;
            _store?.SaveMirror(value);
        }
    }

    private bool _mirror;

    /// <summary>
    /// Every target <paramref name="source"/> drives: its row's target first, then its copy rows'
    /// targets in order.
    /// </summary>
    public IReadOnlyList<Object3D> TargetsOf(Object3D source)
    {
        var targets = new List<Object3D>();
        foreach (var row in RowsForPair())
            if (ReferenceEquals(row.Source, source) && row.Target != null) targets.Add(row.Target);
        foreach (var copy in Copies)
            if (ReferenceEquals(copy.Source, source) && copy.Target != null) targets.Add(copy.Target);
        return targets;
    }

    /// <summary>
    /// Gives <paramref name="source"/> one more target: into its row when the row is empty, else as a
    /// new copy row. The target leaves whichever source held it (a mapping stays one source per
    /// target). With <see cref="Mirror"/> on, the other side gets the same edit. Returns whether the grid changed.
    /// </summary>
    public bool AddTarget(Object3D source, Object3D target)
    {
        bool changed = AddOne(source, target);
        if (changed && Mirror && Mirrored(source, target) is { } m) AddOne(m.Source, m.Target);
        if (changed) Remember();
        return changed;
    }

    /// <summary>
    /// Swaps <paramref name="old"/>, one of <paramref name="source"/>'s targets, for
    /// <paramref name="target"/> in the same slot; null removes it (<see cref="RemoveTarget"/>).
    /// With <see cref="Mirror"/> on, the other side gets the same edit. Returns whether the grid changed.
    /// </summary>
    public bool ReplaceTarget(Object3D source, Object3D old, Object3D? target)
    {
        if (target == null) return RemoveTarget(source, old);
        bool changed = ReplaceOne(source, old, target);
        if (changed && Mirror && Mirrored(source, target) is { } m)
        {
            if (Mirrored(source, old) is { } mo && TargetsOf(m.Source).Contains(mo.Target)) ReplaceOne(m.Source, mo.Target, m.Target);
            else AddOne(m.Source, m.Target);
        }
        if (changed) Remember();
        return changed;
    }

    /// <summary>
    /// Takes <paramref name="target"/> off <paramref name="source"/>; it goes back to the joint bank.
    /// When it was the row's own target, the source's first copy row moves up into the row. With
    /// <see cref="Mirror"/> on, the other side gets the same edit. Returns whether the grid changed.
    /// </summary>
    public bool RemoveTarget(Object3D source, Object3D target)
    {
        if (!TargetsOf(source).Contains(target)) return false;
        Detach(target);
        if (Mirror && Mirrored(source, target) is { } m && TargetsOf(m.Source).Contains(m.Target)) Detach(m.Target);
        _map = null;
        Remember();
        return true;
    }

    private bool AddOne(Object3D source, Object3D target)
    {
        if (!HasPair || !RetargetingMap.Candidates(_target!).Contains(target)) return false;
        if (!RowsForPair().Any(row => ReferenceEquals(row.Source, source))) return false;
        if (TargetsOf(source).Contains(target)) return false;
        Detach(target);
        var rows = RowsForPair().ToList();
        int index = rows.FindIndex(row => ReferenceEquals(row.Source, source));
        if (rows[index].Target == null)
        {
            rows[index] = rows[index] with { Target = target };
            _rows = rows;
        }
        else
        {
            _copies ??= [];
            _copies.Add(new RetargetSelection(source, target));
        }
        _map = null;
        return true;
    }

    private bool ReplaceOne(Object3D source, Object3D old, Object3D target)
    {
        if (!HasPair || ReferenceEquals(old, target) || !RetargetingMap.Candidates(_target!).Contains(target)) return false;
        if (!TargetsOf(source).Contains(old)) return false;
        // Swapping for a target this source already drives elsewhere just drops the old one.
        if (TargetsOf(source).Contains(target))
        {
            Detach(old);
            _map = null;
            return true;
        }
        // Take the new target from its holder without promoting into the slot being filled.
        ClearTarget(target);
        var rows = RowsForPair().ToList();
        int index = rows.FindIndex(row => ReferenceEquals(row.Source, source) && ReferenceEquals(row.Target, old));
        if (index >= 0)
        {
            rows[index] = rows[index] with { Target = target };
            _rows = rows;
        }
        else if (_copies != null)
        {
            int copy = _copies.FindIndex(c => ReferenceEquals(c.Source, source) && ReferenceEquals(c.Target, old));
            if (copy >= 0) _copies[copy] = _copies[copy] with { Target = target };
        }
        PromoteCopies();
        _map = null;
        return true;
    }

    /// <summary>Takes a target off whichever row or copy holds it, then moves copies up into emptied rows.</summary>
    private void Detach(Object3D target)
    {
        ClearTarget(target);
        PromoteCopies();
    }

    /// <summary>Empties the row holding <paramref name="target"/> and deletes the copy row holding it.</summary>
    private void ClearTarget(Object3D target)
    {
        var rows = RowsForPair().ToList();
        for (int i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i].Target, target))
                rows[i] = rows[i] with { Target = null };
        _rows = rows;
        _copies?.RemoveAll(copy => ReferenceEquals(copy.Target, target));
    }

    /// <summary>A row left empty while its source still has a copy row takes that copy's target.</summary>
    private void PromoteCopies()
    {
        if (_copies is not { Count: > 0 }) return;
        var rows = RowsForPair().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Target != null) continue;
            int copy = _copies.FindIndex(c => ReferenceEquals(c.Source, rows[i].Source) && c.Target != null);
            if (copy < 0) continue;
            rows[i] = rows[i] with { Target = _copies[copy].Target };
            _copies.RemoveAt(copy);
        }
        _rows = rows;
    }

    /// <summary>
    /// The other side's source and target for a pair, found by <see cref="RetargetNames.MirrorName"/>
    /// among the two characters' candidates; null when either name has no side or its mirror is missing.
    /// </summary>
    public (Object3D Source, Object3D Target)? Mirrored(Object3D source, Object3D target)
    {
        if (!HasPair) return null;
        if (RetargetNames.MirrorName(source.Name) is not { } sourceName
            || RetargetNames.MirrorName(target.Name) is not { } targetName) return null;
        var ms = RetargetingMap.Candidates(_source!).FirstOrDefault(c => c.Name == sourceName);
        var mt = RetargetingMap.Candidates(_target!).FirstOrDefault(c => c.Name == targetName);
        if (ms == null || mt == null) return null;
        if (ReferenceEquals(ms, source) && ReferenceEquals(mt, target)) return null;
        return (ms, mt);
    }

    private void ReleaseFromCopies(Object3D target, int except)
    {
        if (_copies == null) return;
        for (int i = 0; i < _copies.Count; i++)
            if (i != except && ReferenceEquals(_copies[i].Target, target))
                _copies[i] = _copies[i] with { Target = null };
    }

    private List<RetargetingPair> CopyPairs() =>
        Copies.Where(copy => copy.Target != null).Select(copy => new RetargetingPair(copy.Source, copy.Target!)).ToList();

    /// <summary>Saves the grid for the pair's two skeletons (no-op without a store).</summary>
    private void Remember()
    {
        if (_store == null || !HasPair || _rows == null) return;
        _store.Save(_source!, _target!, _rows, _copies ?? []);
    }

    /// <summary>
    /// What the footer says about the grid: the pairs, the two unmatched sides and the animations
    /// about to be copied.
    /// </summary>
    public string Summary
    {
        get
        {
            if (Map is not { } map) return Refusal ?? "Choose a source character and a target character.";
            int animations = Source?.Animations.Count() ?? 0;
            string unmatched = $"{map.UnmatchedSources.Count} source and {map.UnmatchedTargets.Count} target " +
                               "controller(s) unmatched";
            string copies = map.ExtraPairs.Count > 0 ? $" + {map.ExtraPairs.Count} copy row(s)" : "";
            return $"{map.Pairs.Count} controller pair(s){copies}, {unmatched}; {animations} animation(s) to copy.";
        }
    }

    /// <summary>One line per pair, in <see cref="RetargetingMap.Pairs"/> order, as the dialog lists them.</summary>
    public IReadOnlyList<string> PairLines =>
        Map is not { } map
            ? []
            : map.Pairs.Select(pair => $"{pair.Source.Name}  →  {pair.Target.Name}").ToList();

    /// <summary>
    /// Runs the transfer, the "Retarget Animations": the source's animations are copied into a
    /// new "Retargeted From &lt;source&gt;" folder of the target's Anim Bank, and the mapping is kept
    /// in the scene as a <see cref="RetargetingMatching"/> on each character. The caller wraps the
    /// change in one undo step.
    /// </summary>
    public RetargetingResult Run()
    {
        var run = new RetargetRun();
        foreach (var _ in RunSteps(run)) { }
        return run.Result!;
    }

    /// <summary>
    /// <see cref="Run"/> one animation stack at a time (<see cref="Retargeting.TransferSteps"/>), for a
    /// caller that shows progress between steps; the result is in <paramref name="run"/> at the end.
    /// </summary>
    public IEnumerable<RetargetProgress> RunSteps(RetargetRun run)
    {
        if (Refusal is { } refusal) throw new InvalidOperationException(refusal);
        var rows = RowsForPair();
        Remember();
        return Retargeting.TransferSteps(Model, Source!, Target!, new RetargetingOptions
        {
            RetargetFolder = true,
            Pairs = rows.Where(row => row.Target != null)
                .Select(row => new RetargetingPair(row.Source, row.Target!)).ToList(),
            NoneSources = rows.Where(row => row.Target == null).Select(row => row.Source).ToList(),
            ExtraPairs = CopyPairs(),
            CacheDirectory = _cacheDirectory,
        }, run);
    }

    /// <summary>The saved-mapping store's last write error, or null.</summary>
    public string? StoreError => _store?.LastError;

    private bool HasPair => _source is { } source && _target is { } target && !ReferenceEquals(source, target);

    /// <summary>Drops the grid and the map, so the next read restores or rebuilds them for the pair.</summary>
    private void ResetGrid()
    {
        _rows = null;
        _copies = null;
        _map = null;
    }

    /// <summary>
    /// The rows of the pair: the scene's stored matching, else the pair's name-pair cache restored by
    /// name (<see cref="RetargetingPreferences"/>), else the automatic mapping.
    /// </summary>
    private IReadOnlyList<RetargetSelection> RowsForPair()
    {
        if (!HasPair) return [];
        if (_rows != null) return _rows;
        // SloppyAnimatsu's saved mapping for these two skeletons wins (it follows every edit, for any
        // characters built from the same rigs); then the stored matching, its name-pair cache,
        // and the automatic mapping with SloppyAnimatsu's body-part and hierarchy passes.
        var automatic = RetargetingMap.Build(_source!, _target!, extendedNames: true).Selection;
        IReadOnlyList<RetargetSelection>? rows = null;
        _copies = [];
        if (_store?.Load(_source!, _target!, automatic) is { } saved)
        {
            rows = saved.Rows;
            _copies = saved.Copies.ToList();
        }
        rows ??= RetargetingMatching.Restore(_source!, _target!) ?? CachedRows() ?? automatic;
        // A target object destroyed after the grid was computed falls back to "none" rather than
        // making the whole grid unusable.
        var choices = new HashSet<Object3D>(RetargetingMap.Candidates(_target!), ReferenceEqualityComparer.Instance);
        _rows = rows.Select(row => row.Target != null && !choices.Contains(row.Target)
                ? row with { Target = null }
                : row)
            .ToList();
        return _rows;
    }

    /// <summary>
    /// The pair's rows restored from its name-pair cache: for every source candidate, the target
    /// candidate whose name the cache pairs with it, and "none" for the rest - the grid when
    /// the file is the only thing it knows about the pair. Null when the pair has no cache file or it
    /// names no candidate of either side.
    /// </summary>
    private IReadOnlyList<RetargetSelection>? CachedRows()
    {
        string sourceName = _source!.Name ?? "";
        string targetName = _target!.Name ?? "";
        RetargetingPreferences? cache;
        try
        {
            cache = RetargetingPreferences.LoadFor(sourceName, targetName, _cacheDirectory);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or Akt.AktFormatException)
        {
            return null;
        }
        if (cache == null || cache.Pairs.Count == 0) return null;
        var targetsByName = new Dictionary<string, Object3D>(StringComparer.Ordinal);
        foreach (var candidate in RetargetingMap.Candidates(_target!))
            if (candidate.Name is { } name) targetsByName.TryAdd(name, candidate);
        var chosen = new Dictionary<string, Object3D>(StringComparer.Ordinal);
        foreach (var (cachedSource, cachedTarget) in cache.Pairs)
            if (targetsByName.TryGetValue(cachedTarget, out var targetObject))
                chosen[cachedSource] = targetObject;
        if (chosen.Count == 0) return null;
        return RetargetingMap.Candidates(_source!)
            .Select(candidate => new RetargetSelection(candidate,
                candidate.Name is { } name && chosen.TryGetValue(name, out var targetObject) ? targetObject : null))
            .ToList();
    }
}