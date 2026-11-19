using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>One controller of the source character and the target controller its animation goes to.</summary>
public sealed record RetargetingPair(Object3D Source, Object3D Target);

/// <summary>
/// One row of the retargeting grid: a source controller and the target it goes to, or
/// <c>null</c> for the explicit "none" (the row is then listed in the stored
/// <c>NullIndices</c> and the controller is not retargeted).
/// </summary>
public sealed record RetargetSelection(Object3D Source, Object3D? Target);

/// <summary>
/// The controller mapping of a retargeting:
/// every source controller that found a target controller, and the two characters' candidates
/// that found none. The mapping is built by name, can be overridden pair by pair, and is what
/// <see cref="Retargeting.Transfer"/> converts every key through.
/// </summary>
public sealed class RetargetingMap
{
    private readonly Dictionary<Object3D, Object3D> _bySource = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Object3D, Object3D> _byTarget = new(ReferenceEqualityComparer.Instance);

    private RetargetingMap(Character source, Character target, IReadOnlyList<RetargetingPair> pairs,
        IReadOnlyList<RetargetingPair> functionalGroup,
        IReadOnlyList<Object3D> unmatchedSources, IReadOnlyList<Object3D> unmatchedTargets,
        IReadOnlyList<Object3D> sourceCandidates, IReadOnlyList<RetargetingPair> extraPairs)
    {
        Source = source;
        Target = target;
        Pairs = pairs;
        FunctionalGroup = functionalGroup;
        ExtraPairs = extraPairs;
        UnmatchedSources = unmatchedSources;
        UnmatchedTargets = unmatchedTargets;
        foreach (var pair in pairs.Concat(functionalGroup).Concat(extraPairs))
        {
            _bySource.TryAdd(pair.Source, pair.Target);
            _byTarget.TryAdd(pair.Target, pair.Source);
        }
        // The row of every source candidate, in the dialog's order: a pair's target or an explicit
        // "none". This is the grid's state and what the stored CRetargetingMatching records; the
        // functional-group pairs do not take part (they are not candidates).
        var chosen = new Dictionary<Object3D, Object3D>(ReferenceEqualityComparer.Instance);
        foreach (var pair in pairs) chosen.TryAdd(pair.Source, pair.Target);
        Selection = sourceCandidates
            .Select(candidate => new RetargetSelection(candidate, chosen.GetValueOrDefault(candidate)))
            .ToList();
    }

    public Character Source { get; }
    public Character Target { get; }
    public IReadOnlyList<RetargetingPair> Pairs { get; }
    /// <summary>
    /// The functional-group tracks: a matched
    /// reverse-foot controller's shoe locators and a matched IK controller's chain joints, paired
    /// by index. They are not candidates and do not take part in the scale ratio, but their tracks
    /// are transferred like any other.
    /// </summary>
    public IReadOnlyList<RetargetingPair> FunctionalGroup { get; }
    /// <summary>
    /// The dialog's copy rows: further targets of a source that already has one. Their tracks are
    /// transferred like a pair's; they are not in <see cref="Pairs"/>, the ratio or the stored matching.
    /// </summary>
    public IReadOnlyList<RetargetingPair> ExtraPairs { get; }
    /// <summary>Source candidates that found no target controller.</summary>
    public IReadOnlyList<Object3D> UnmatchedSources { get; }
    /// <summary>Target candidates that no source controller mapped to.</summary>
    public IReadOnlyList<Object3D> UnmatchedTargets { get; }
    /// <summary>
    /// The dialog's rows: one per source candidate, in <c>Controllers</c> order, carrying the target
    /// it goes to or <c>null</c> for an explicit "none". <see cref="Pairs"/> holds only the matched
    /// rows; this is the whole grid, and what the stored matching records.
    /// </summary>
    public IReadOnlyList<RetargetSelection> Selection { get; }

    public Object3D? TargetOf(Object3D source) => _bySource.GetValueOrDefault(source);
    public Object3D? SourceOf(Object3D target) => _byTarget.GetValueOrDefault(target);

    /// <summary>Every source controller that may be matched: joints, IK and reverse-foot controllers.</summary>
    public static IReadOnlyList<Object3D> Candidates(Character character) =>
        character.Controllers.Where(c => IsCandidate(character.Model, c)).ToList();

    /// <summary>
    /// Which objects take part in the retargeting match: joints and controllers yes, shoe locators and
    /// blendshape controllers no.
    /// </summary>
    public static bool IsCandidate(SceneModel model, Object3D obj)
    {
        if (obj is ReverseFootLocator) return false;
        if (IsA(model.Document, obj.Source, "CBlendshapeController")) return false;
        return obj is Joint or Controller3D;
    }

    /// <summary>
    /// Builds the mapping: explicit pairs first, then exact names, then names with their namespace
    /// prefix removed (except IK/reverse-foot controllers, which keep their exact name), then a
    /// case-insensitive pass. One source and one target are used once. A source listed in
    /// <paramref name="noneSources"/> is never paired: that is the dialog's explicit "none" row
    /// (the app records it in <c>NullIndices</c>), and the source ends up in
    /// <see cref="UnmatchedSources"/> so an edited grid decides the transfer, not the automatic names.
    /// </summary>
    /// <param name="extraPairs">
    /// SloppyAnimatsu's copy rows: a source that already drives a target may drive more targets (an old
    /// three-finger rig's third finger onto both the ring and the pinky). They are kept in
    /// <see cref="ExtraPairs"/>, outside <see cref="Pairs"/>, the scale ratio and the stored matching.
    /// </param>
    /// <param name="extendedNames">
    /// Adds SloppyAnimatsu's body-part pass and hierarchy pass after the exact-name passes (the dialog's
    /// automatic mapping uses them; the command line keeps the exact-name matching).
    /// </param>
    public static RetargetingMap Build(Character source, Character target,
        IEnumerable<RetargetingPair>? explicitPairs = null, IEnumerable<Object3D>? noneSources = null,
        IEnumerable<RetargetingPair>? extraPairs = null, bool extendedNames = false)
    {
        if (ReferenceEquals(source, target))
            throw new InvalidOperationException("A character cannot retarget onto itself.");

        var sourceCandidates = Candidates(source);
        var targetCandidates = Candidates(target);
        var sourceSet = new HashSet<Object3D>(sourceCandidates, ReferenceEqualityComparer.Instance);
        var targetSet = new HashSet<Object3D>(targetCandidates, ReferenceEqualityComparer.Instance);
        var none = noneSources == null
            ? null
            : new HashSet<Object3D>(noneSources, ReferenceEqualityComparer.Instance);
        if (none != null)
            foreach (var excluded in none)
                if (!sourceSet.Contains(excluded))
                    throw new InvalidOperationException($"'{excluded.Name}' is not a retargetable controller of '{source.Name}'.");
        var pairs = new List<RetargetingPair>();
        var usedSources = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        var usedTargets = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);

        void Pair(Object3D from, Object3D to)
        {
            pairs.Add(new RetargetingPair(from, to));
            usedSources.Add(from);
            usedTargets.Add(to);
        }

        // 1. Explicit overrides (the command line's --match, or the dialog's edited grid).
        foreach (var pair in explicitPairs ?? [])
        {
            if (none != null && none.Contains(pair.Source)) continue;
            if (!sourceSet.Contains(pair.Source))
                throw new InvalidOperationException($"'{pair.Source.Name}' is not a retargetable controller of '{source.Name}'.");
            if (!targetSet.Contains(pair.Target))
                throw new InvalidOperationException($"'{pair.Target.Name}' is not a retargetable controller of '{target.Name}'.");
            if (usedSources.Contains(pair.Source) || usedTargets.Contains(pair.Target)) continue;
            Pair(pair.Source, pair.Target);
        }

        // 2. Exact names, in Controllers order; duplicate names pair in order.
        PairBy(sourceCandidates, targetCandidates, usedSources, usedTargets, Pair, n => n, prefixMatch: false);

        // 3. The same name after its namespace prefix (except for IK/reverse-foot controllers).
        PairBy(sourceCandidates, targetCandidates, usedSources, usedTargets, Pair, NamespaceTail, prefixMatch: true);

        // 4. Case-insensitive last pass.
        PairBy(sourceCandidates, targetCandidates, usedSources, usedTargets, Pair,
            n => n.ToLowerInvariant(), prefixMatch: false);

        if (extendedNames)
        {
            // 5. Body-part keys: sides and naming conventions folded together (LeftUpLeg ~ thigh_l).
            var byKey = new Dictionary<string, Queue<Object3D>>(StringComparer.Ordinal);
            foreach (var candidate in targetCandidates)
            {
                if (usedTargets.Contains(candidate) || RetargetNames.BodyKey(target, candidate) is not { } k) continue;
                if (!byKey.TryGetValue(k, out var queue)) byKey[k] = queue = new Queue<Object3D>();
                queue.Enqueue(candidate);
            }
            foreach (var candidate in sourceCandidates)
            {
                if (usedSources.Contains(candidate) || (none != null && none.Contains(candidate))) continue;
                if (RetargetNames.BodyKey(source, candidate) is not { } k
                    || !byKey.TryGetValue(k, out var queue) || queue.Count == 0) continue;
                Pair(candidate, queue.Dequeue());
            }

            // 6. Hierarchy: a matched joint whose only unmatched child joint faces the other side's
            //    only unmatched child joint pairs them, down every chain (finger and spine segments).
            var sourceParents = CandidateParents(sourceCandidates);
            var targetParents = CandidateParents(targetCandidates);
            for (int i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                var fromKids = sourceCandidates.Where(c => c is Joint && !usedSources.Contains(c)
                        && (none == null || !none.Contains(c))
                        && ReferenceEquals(sourceParents.GetValueOrDefault(c), pair.Source)).Take(2).ToList();
                var toKids = targetCandidates.Where(c => c is Joint && !usedTargets.Contains(c)
                        && ReferenceEquals(targetParents.GetValueOrDefault(c), pair.Target)).Take(2).ToList();
                if (fromKids.Count == 1 && toKids.Count == 1) Pair(fromKids[0], toKids[0]);
            }
        }

        var functionalGroup = FunctionalGroupPairs(pairs);
        // 7. Copy rows: a source already paired may drive further targets nothing else holds.
        var extras = new List<RetargetingPair>();
        foreach (var extra in extraPairs ?? [])
        {
            if (!sourceSet.Contains(extra.Source) || !targetSet.Contains(extra.Target)) continue;
            if (!usedTargets.Add(extra.Target)) continue;
            extras.Add(extra);
        }
        var unmatchedSources = sourceCandidates.Where(c => !usedSources.Contains(c)).ToList();
        var unmatchedTargets = targetCandidates.Where(c => !usedTargets.Contains(c)).ToList();
        return new RetargetingMap(source, target, pairs, functionalGroup, unmatchedSources, unmatchedTargets,
            sourceCandidates, extras);

        // Each candidate's nearest ancestor that is a candidate too.
        static Dictionary<Object3D, Object3D> CandidateParents(IReadOnlyList<Object3D> candidates)
        {
            var set = new HashSet<Object3D>(candidates, ReferenceEqualityComparer.Instance);
            var parents = new Dictionary<Object3D, Object3D>(ReferenceEqualityComparer.Instance);
            foreach (var candidate in candidates)
                for (var up = candidate.Parent; up != null; up = up.Parent)
                    if (up is Object3D o && set.Contains(o))
                    {
                        parents[candidate] = o;
                        break;
                    }
            return parents;
        }

        void PairBy(IReadOnlyList<Object3D> sourceList, IReadOnlyList<Object3D> targetList,
            HashSet<Object3D> usedSource, HashSet<Object3D> usedTarget,
            Action<Object3D, Object3D> add, Func<string, string> key, bool prefixMatch)
        {
            var byKey = new Dictionary<string, Queue<Object3D>>(StringComparer.Ordinal);
            foreach (var candidate in targetList)
            {
                if (candidate.Name is not { } name) continue;
                string k = key(name);
                if (!byKey.TryGetValue(k, out var queue)) byKey[k] = queue = new Queue<Object3D>();
                queue.Enqueue(candidate);
            }
            foreach (var candidate in sourceList)
            {
                if (usedSource.Contains(candidate) || candidate.Name is not { } name) continue;
                if (none != null && none.Contains(candidate)) continue;
                if (!byKey.TryGetValue(key(name), out var queue) || queue.Count == 0) continue;
                if (prefixMatch && candidate is ControllerIK) continue;
                Object3D? target = null;
                while (queue.Count > 0)
                {
                    var next = queue.Peek();
                    // Prefix matching never pairs an IK/reverse-foot controller with a plain
                    // joint: those controllers keep their exact name (the shipped virtual).
                    if (prefixMatch && next is ControllerIK) break;
                    if (usedTarget.Contains(next)) { queue.Dequeue(); continue; }
                    target = queue.Dequeue();
                    break;
                }
                if (target != null) add(candidate, target);
            }
        }
    }

    /// <summary>
    /// The part of a name after its last namespace separator (<c>:</c>, <c>|</c>, <c>/</c> or
    /// space), e.g. <c>mixamorig:Pelvis</c> and <c>Character_01 Pelvis</c> both become
    /// <c>Pelvis</c>. Underscores are kept so that side tags stay significant.
    /// </summary>
    public static string NamespaceTail(string name)
    {
        int cut = name.LastIndexOfAny([':', '|', '/', ' ']);
        if (cut < 0 || cut + 1 >= name.Length) return name;
        string tail = name[(cut + 1)..].Trim();
        return tail.Length == 0 ? name : tail;
    }

    /// <summary>
    /// The functional-group objects of the matched controllers, paired by index: a reverse foot's
    /// seven shoe locators (same order on both sides) and an IK controller's chain joints that the
    /// name passes did not already pair. The app creates their tracks with the controller's
    /// (<c>AKNM_CreateSetupFunctionalGroupTracksAndKeys</c>), which is why a retargeted animation
    /// has a track for every controller, shoe locators included.
    /// </summary>
    private static IReadOnlyList<RetargetingPair> FunctionalGroupPairs(IReadOnlyList<RetargetingPair> pairs)
    {
        var extras = new List<RetargetingPair>();
        var used = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        foreach (var pair in pairs)
        {
            used.Add(pair.Source);
            used.Add(pair.Target);
        }
        foreach (var pair in pairs)
        {
            if (pair.Source is ControllerReverseFoot fromFoot && pair.Target is ControllerReverseFoot toFoot)
            {
                var fromShoe = fromFoot.Shoe;
                var toShoe = toFoot.Shoe;
                for (int i = 0; i < Math.Min(fromShoe.Count, toShoe.Count); i++)
                    if (!used.Contains(fromShoe[i]) && !used.Contains(toShoe[i]))
                    {
                        used.Add(fromShoe[i]);
                        used.Add(toShoe[i]);
                        extras.Add(new RetargetingPair(fromShoe[i], toShoe[i]));
                    }
            }
            else if (pair.Source is ControllerIK fromIk && pair.Target is ControllerIK toIk)
            {
                var fromChain = fromIk.Chain;
                var toChain = toIk.Chain;
                for (int i = 0; i < Math.Min(fromChain.Count, toChain.Count); i++)
                    if (!used.Contains(fromChain[i]) && !used.Contains(toChain[i]))
                    {
                        used.Add(fromChain[i]);
                        used.Add(toChain[i]);
                        extras.Add(new RetargetingPair(fromChain[i], toChain[i]));
                    }
            }
        }
        return extras;
    }

    private static bool IsA(AktDocument document, AktObject obj, string classKey)
    {
        var key = AktNames.Key(classKey);
        for (var c = document.Schema.FindClass(obj.TypeName); c != null; c = c.Parent)
            if (c.Key == key) return true;
        return obj.ClassKey == key;
    }
}

/// <summary>
/// <c>CRetargetingMatching</c>: the mapping a retarget stores in the scene
/// One object is a child of a character and
/// names the character that character is retargeted onto; <see cref="Sources"/> and
/// <see cref="Targets"/> are parallel, and a source row set to "none" carries a null target entry and
/// its index in <see cref="NullIndices"/>. The sample stores no null entries, so whether it
/// omits the target entry entirely (compressing <c>Targets</c>) cannot be observed; this class writes
/// the parallel layout the member table describes and reads either.
/// </summary>
public sealed class RetargetingMatching : SceneObject
{
    /// <summary>What the app names a new matching ("Default name", then "Default name_02",...).</summary>
    public const string DefaultName = "Default name";

    internal RetargetingMatching(SceneModel model, AktObject source) : base(model, source) { }

    /// <summary>The character this mapping retargets onto; null when the reference is dangling.</summary>
    public Character? TargetCharacter => Ref<Character>("TargetCharacter");

    /// <summary>The source-side objects, one per row; a null entry is a damaged one, not a "none".</summary>
    public IReadOnlyList<Object3D?> Sources => Objects("Sources");

    /// <summary>The target of each row; a "none" row has a null entry at its position.</summary>
    public IReadOnlyList<Object3D?> Targets => Objects("Targets");

    /// <summary>Indices into <see cref="Sources"/> of the rows without a target.</summary>
    public IReadOnlyList<uint> NullIndices => UInts("NullIndices");

    /// <summary>
    /// The <c>IsLastUsedRetargetingMatching</c>. The scene keeps it on exactly one matching,
    /// the one under the character that <em>received</em> the animations, pointing at the source
    /// character: after a transfer of A onto B, B's matching (whose <see cref="TargetCharacter"/> is
    /// A) carries it and A's carries false
    /// </summary>
    public bool IsLastUsedRetargetingMatching
    {
        get => Flag("IsLastUsedRetargetingMatching");
        set { if (Source.Has("IsLastUsedRetargetingMatching")) Source.Set("IsLastUsedRetargetingMatching", value); }
    }

    /// <summary>
    /// The stored rows, decoded: <see cref="Sources"/> order, each with its target or null. An index
    /// in <see cref="NullIndices"/> wins over any target entry; when <see cref="Targets"/> is shorter
    /// than <see cref="Sources"/> the target list is read as compressed (the
    /// <c>SetMatch(source, null)</c> reading, where a null target appends no target entry).
    /// </summary>
    public IReadOnlyList<RetargetSelection> Matches()
    {
        var sources = Sources;
        var targets = Targets;
        var nulls = new HashSet<uint>(NullIndices);
        bool parallel = targets.Count == sources.Count;
        var rows = new List<RetargetSelection>(sources.Count);
        int nextTarget = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not { } source) continue;
            Object3D? target = null;
            if (!nulls.Contains((uint)i))
            {
                if (parallel) target = i < targets.Count ? targets[i] : null;
                else if (nextTarget < targets.Count) target = targets[nextTarget++];
            }
            rows.Add(new RetargetSelection(source, target));
        }
        return rows;
    }

    /// <summary>The matching on <paramref name="owner"/> whose target character is <paramref name="target"/>.</summary>
    public static RetargetingMatching? Find(Character owner, Character target) =>
        owner.Children.OfType<RetargetingMatching>()
            .FirstOrDefault(matching => ReferenceEquals(matching.TargetCharacter, target));

    /// <summary>
    /// The grid a stored matching gives the pair, or null when there is none or it is stale. Stale
    /// means a destroyed object (a dangling reference), a matching for another pair, a row for an
    /// object outside the two candidate lists, a duplicate, or a character whose controller list
    /// changed so that the rows no longer cover it - the dialog then opens on the automatic mapping.
    /// </summary>
    public static IReadOnlyList<RetargetSelection>? Restore(Character source, Character target)
    {
        if (Find(source, target) is not { } matching) return null;
        var rows = matching.Matches();
        var sourceCandidates = RetargetingMap.Candidates(source);
        var targetCandidates = RetargetingMap.Candidates(target);
        if (rows.Count == 0 || rows.Count != sourceCandidates.Count) return null;
        var sourceSet = new HashSet<Object3D>(sourceCandidates, ReferenceEqualityComparer.Instance);
        var targetSet = new HashSet<Object3D>(targetCandidates, ReferenceEqualityComparer.Instance);
        if (rows.Any(row => !sourceSet.Contains(row.Source))) return null;
        if (rows.Select(row => row.Source).Distinct(ReferenceEqualityComparer.Instance).Count() != rows.Count)
            return null;
        var usedTargets = new HashSet<Object3D>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            if (row.Target is not { } rowTarget) continue;
            if (!targetSet.Contains(rowTarget) || !usedTargets.Add(rowTarget)) return null;
        }
        // The grid lists the candidates in Controllers order, whatever order the matching stores.
        var index = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < sourceCandidates.Count; i++) index[sourceCandidates[i]] = i;
        return rows.OrderBy(row => index[row.Source]).ToList();
    }

    /// <summary>
    /// The transfer step 2: keeps the mapping in
    /// the scene. On the source character it finds or creates the matching whose
    /// <see cref="TargetCharacter"/> is the target, clears it, fills <c>Sources</c> with every source
    /// candidate in order and <c>Targets</c>/<c>NullIndices</c> with its choice; the target character
    /// carries the mirrored mapping (its own candidates as sources, the reverse pairs as targets).
    /// </summary>
    public static void Record(SceneModel model, RetargetingMap map)
    {
        Write(model, map.Source, map.Target, map.Selection);

        var reverse = new Dictionary<Object3D, Object3D>(ReferenceEqualityComparer.Instance);
        foreach (var row in map.Selection)
            if (row.Target is { } target) reverse[target] = row.Source;
        var mirror = RetargetingMap.Candidates(map.Target)
            .Select(candidate => new RetargetSelection(candidate, reverse.GetValueOrDefault(candidate)))
            .ToList();
        Write(model, map.Target, map.Source, mirror);
        // Mark (map.Target, map.Source) as the last used pair: the matching under the character
        // that received the animations is the last used one; every other matching of the scene clears.
        SetLastUsed(model, map.Target, map.Source);
    }

    /// <summary>
    /// Marks the matching of (<paramref name="owner"/>, <paramref name="target"/>) as the scene's last
    /// used pair and clears the flag on every other matching
    /// (it walks the scene's matchings and writes both flags).
    /// </summary>
    public static void SetLastUsed(SceneModel model, Character owner, Character target)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var matching in character.Children.OfType<RetargetingMatching>())
                matching.IsLastUsedRetargetingMatching = ReferenceEquals(character, owner)
                    && ReferenceEquals(matching.TargetCharacter, target);
    }

    /// <summary>
    /// The scene's last used character pair: the matching
    /// whose flag is set, as (the character that owns it, the character it points at). This is the
    /// pair a fresh retargeting dialog opens on.
    /// </summary>
    public static (Character Owner, Character Target)? LastUsed(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var matching in character.Children.OfType<RetargetingMatching>())
                if (matching.IsLastUsedRetargetingMatching && matching.TargetCharacter is { } target)
                    return (character, target);
        return null;
    }

    private static void Write(SceneModel model, Character owner, Character target, IReadOnlyList<RetargetSelection> rows)
    {
        RetargetingMatchingSchema.Ensure(model.Document.Schema);
        var matching = Find(owner, target) ?? Create(model, owner);
        matching.Source.Set("TargetCharacter", new AktReference(target.Source));
        matching.Fill(rows);
    }

    private static RetargetingMatching Create(SceneModel model, Character owner)
    {
        var source = model.Document.Schema.NewObject(RetargetingMatchingSchema.Class);
        source.Set("Name", UniqueName(model));
        owner.Source.AddChild(source);
        return (RetargetingMatching)model.Wrap(source);
    }

    /// <summary>
    /// The generic name, made unique across the scene rather than per parent, because one
    /// retarget creates both objects under different characters and the sample's are
    /// "Default name" (the source's) and "Default name_02" (the mirrored one).
    /// </summary>
    private static string UniqueName(SceneModel model)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in model.Document.AllObjects())
            if (obj.Name is { } name) names.Add(name);
        if (!names.Contains(DefaultName)) return DefaultName;
        var numbered = new Regex("^" + Regex.Escape(DefaultName) + "_([0-9]+)$", RegexOptions.CultureInvariant);
        long highest = 0;
        foreach (string name in names)
            if (numbered.Match(name) is { Success: true } match && long.TryParse(match.Groups[1].Value, out long n))
                highest = Math.Max(highest, n);
        return $"{DefaultName}_{Math.Max(2, highest + 1).ToString("00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Empties the three lists and writes <paramref name="rows"/> (the <c>Clean</c> then
    /// <c>SetMatch</c>). A "none" row appends **no** target entry, only its index in
    /// <c>NullIndices</c>: the different-rig save has 68 sources, 14 targets and 54 null
    /// indices, so <c>Targets</c> is compact,
    /// not parallel.
    /// </summary>
    private void Fill(IReadOnlyList<RetargetSelection> rows)
    {
        var sources = (List<AktReference>)Source["Sources"]!;
        var targets = (List<AktReference>)Source["Targets"]!;
        var nulls = (List<object?>)Source["NullIndices"]!;
        sources.Clear();
        targets.Clear();
        nulls.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            sources.Add(new AktReference(rows[i].Source.Source));
            if (rows[i].Target is { } target)
            {
                targets.Add(new AktReference(target.Source));
                continue;
            }
            nulls.Add((uint)i);
        }
    }

    private IReadOnlyList<Object3D?> Objects(string field) =>
        Source[field] is List<AktReference> refs
            ? refs.Select(reference => Model.Wrap(reference) as Object3D).ToList()
            : [];
}

/// <summary>
/// How <see cref="Retargeting.Transfer"/> reads a source key's delta
/// <see cref="Local"/> applies it in the node's own frame (the local
/// rotation delta is copied), <see cref="World"/> conjugates it by the two nodes'
/// reference-pose world orientations (the delta is a world-space rotation of the node).
/// They coincide when the two reference poses agree, which is why the same-rig sample cannot
/// tell them apart.
/// </summary>
public enum RetargetMethod
{
    /// <summary>
    /// The different-reference-pose reading
    /// the rotation delta changes basis between the two nodes' reference-pose world
    /// rotations (as in <see cref="World"/>), the translation delta keeps its direction and is scaled by the ratio alone, and the key's
    /// scale is multiplied by the nodes' reference-pose scale ratio normalized to preserve volume
    /// (a per-node anisotropic factor). Equal to <see cref="Local"/> when the reference poses agree.
    /// </summary>
    Reference,
    /// <summary>The key's delta from the source reference pose, kept in the node's local frame.</summary>
    Local,
    /// <summary>
    /// The delta re-expressed so the target node gets the source node's world-space change: the
    /// rotation changes basis by C = Rsource^-1 * Rtarget (the rest world rotations), the
    /// translation goes through the two parents' rest world frames and the ratio. Correct for rigs
    /// whose joints are oriented differently.
    /// </summary>
    World,
    /// <summary>
    /// <see cref="World"/> when the two characters' matched reference poses differ,
    /// <see cref="Local"/> when they coincide (the same-rig case, which must stay exact).
    /// </summary>
    Auto,
}

/// <summary>How <see cref="Retargeting.Transfer"/> copies a source character's animations.</summary>
public sealed class RetargetingOptions
{
    /// <summary>
    /// The per-key conversion's reading of a source delta. The default, <see cref="RetargetMethod.Auto"/>,
    /// keeps the same-rig case exact and converts through the world frames when the rigs differ;
    /// the local reading alone breaks a rig whose joints are oriented differently.
    /// </summary>
    public RetargetMethod Method { get; init; } = RetargetMethod.Auto;
    /// <summary>
    /// Unless the Local reading is used, key every IK/reverse-foot locked key's chain to the pose
    /// playback shows beside it (<c>KeyEditor.SolveLockedKeys</c>), so the key frames do not jump.
    /// SloppyAnimatsu's addition (the plain per-key result jumps); tests of the per-key reading turn it off.
    /// </summary>
    public bool SolveLockedKeys { get; init; } = true;
    /// <summary>Only these animations (default: every user animation of the source character).</summary>
    public IReadOnlyList<Animation>? Animations { get; init; }
    /// <summary>Put the copies in a new "Retargeted From &lt;source&gt;" folder.</summary>
    public bool RetargetFolder { get; init; } = true;
    /// <summary>Pairs that override the automatic name matching.</summary>
    public IReadOnlyList<RetargetingPair>? Pairs { get; init; }
    /// <summary>
    /// Source controllers the dialog set to "none": they are never paired, so the transfer leaves
    /// them unmatched and the stored matching records them in its <c>NullIndices</c>.
    /// </summary>
    public IReadOnlyList<Object3D>? NoneSources { get; init; }
    /// <summary>The dialog's copy rows: further targets of a source (<see cref="RetargetingMap.ExtraPairs"/>).</summary>
    public IReadOnlyList<RetargetingPair>? ExtraPairs { get; init; }
    /// <summary>Receives each warning as it is found.</summary>
    public Action<string>? Warning { get; init; }
    /// <summary>
    /// The folder to write the pair's name-pair cache into (the
    /// <c>&lt;source&gt;_&lt;target&gt;.slopa</c>); null leaves the cache alone.
    /// The dialog and the CLI pass the Documents folder.
    /// </summary>
    public string? CacheDirectory { get; init; }
}

/// <summary>What a retargeting did.</summary>
/// <summary>How far a <see cref="Retargeting.TransferSteps"/> run is: stacks converted out of the total.</summary>
public readonly record struct RetargetProgress(int Done, int Total, string? Animation);

/// <summary>Holds the result of a <see cref="Retargeting.TransferSteps"/> run once it has ended.</summary>
public sealed class RetargetRun
{
    public RetargetingResult? Result { get; set; }
}

public sealed class RetargetingResult
{
    public required Character Character { get; init; }
    public required RetargetingMap Map { get; init; }
    public required IReadOnlyList<Animation> Animations { get; init; }
    /// <summary>The folder the animations went into (the "Retargeted From ..." folder, or the bank root).</summary>
    public required SceneObject Destination { get; init; }
    /// <summary>The single translation multiplier computed from the reference poses (1 when none).</summary>
    public required float Ratio { get; init; }
    public int CopiedStacks { get; init; }
    /// <summary>Audio stacks of the source animation (not copied; their events point at source audio).</summary>
    public int SkippedAudioStacks { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Copies one character's animations onto another character of the same scene, converting each key
/// so that the target keeps the source's motion: the source's
/// per-controller local change from its reference pose is applied to the target's reference pose,
/// and translation changes are scaled by the ratio of the two rigs' reference-pose sizes.
/// </summary>
public static class Retargeting
{
    /// <summary>Smallest reference-pose distance a ratio pair may have (the 1e-5).</summary>
    private const float MinRatioDistance = 1e-5f;

    public static RetargetingResult Transfer(SceneModel model, Character source, Character target, RetargetingOptions? options = null)
    {
        var run = new RetargetRun();
        foreach (var _ in TransferSteps(model, source, target, options, run)) { }
        return run.Result!;
    }

    /// <summary>
    /// <see cref="Transfer"/> one animation stack at a time: every step converts one stack and
    /// yields how far the transfer is, so a caller on the UI thread can repaint a progress bar
    /// between steps. The result is in <paramref name="run"/> once the enumeration ends; stopping
    /// early leaves a partial transfer (the caller restores its snapshot).
    /// </summary>
    public static IEnumerable<RetargetProgress> TransferSteps(SceneModel model, Character source, Character target,
        RetargetingOptions? options, RetargetRun run)
    {
        options ??= new RetargetingOptions();
        if (!ReferenceEquals(source.Model, target.Model) || !ReferenceEquals(source.Model, model))
            throw new InvalidOperationException("The source and target characters must belong to the scene being edited.");
        var warnings = new List<string>();
        void Warn(string message)
        {
            warnings.Add(message);
            options.Warning?.Invoke(message);
        }

        var map = RetargetingMap.Build(source, target, options.Pairs, options.NoneSources, options.ExtraPairs);
        if (map.Pairs.Count == 0)
            throw new InvalidOperationException(
                $"No controller of '{source.Name}' matches a controller of '{target.Name}'; retargeting needs at least one pair.");
        // The transfer step 2: the mapping is kept in the scene, one matching on each character.
        RetargetingMatching.Record(model, map);
        // ... and the pair's name pairs in the cache file.
        if (options.CacheDirectory is { } cacheDirectory)
        {
            string sourceName = source.Name ?? "";
            string targetName = target.Name ?? "";
            try
            {
                WriteCache(sourceName, targetName, RetargetingPreferences.PairsOf(map));
                // The app writes the mirrored file too (<target><source>), its pairs swapped and sorted.
                var reverse = map.Selection
                    .Where(row => row.Target?.Name is not null && row.Source.Name is not null)
                    .Select(row => (Source: row.Target!.Name!, Target: row.Source.Name!))
                    .OrderBy(pair => pair.Source, StringComparer.Ordinal)
                    .ToList();
                WriteCache(targetName, sourceName, reverse);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Warn($"could not save the name-pair cache under '{cacheDirectory}': {e.Message}");
            }

            void WriteCache(string from, string to, IEnumerable<(string Source, string Target)> pairs)
            {
                var cache = RetargetingPreferences.Create(from, to, pairs);
                cache.Save(RetargetingPreferences.PathFor(from, to, cacheDirectory));
            }
        }
        float ratio = ComputeRatio(map);
        var method = options.Method;
        if (method == RetargetMethod.Auto)
            method = ReferencePosesDiffer(map) ? RetargetMethod.World : RetargetMethod.Local;
        // Every rig pair that is read through the world frames gets it, a proportions-only pair too:
        // The retargets of z_DupTheClown -> z_Citro_Knight still jump up to 1.8 units at keys
        // and SloppyAnimatsu need not reproduce that. The Local reading
        // (the same rig, or chosen explicitly) keeps each local delta by definition and is left alone.
        bool solveLocked = options.SolveLockedKeys && method != RetargetMethod.Local;
        if (map.UnmatchedSources.Count > 0)
            Warn($"{map.UnmatchedSources.Count} controller(s) of '{source.Name}' have no counterpart in '{target.Name}'; their tracks are dropped.");
        if (map.UnmatchedTargets.Count > 0)
            Warn($"{map.UnmatchedTargets.Count} controller(s) of '{target.Name}' are not targeted; they keep no animation.");

        var bank = target.AnimBank ?? CreateBank(model, target);
        SceneObject destination = bank;
        if (options.RetargetFolder)
            destination = CreateFolder(model, bank, UniqueNames.For(bank.Source, $"Retargeted From {source.Name}"));

        var sourceEvaluator = new PoseEvaluator(source);
        var targetEvaluator = new PoseEvaluator(target);
        var targetRest = targetEvaluator.RestTransforms;

        var animations = (options.Animations ?? source.Animations.ToList())
            .Where(a => a.Source.Parent != null && a.Character == source)
            .ToList();
        var imported = new List<Animation>();
        int copiedStacks = 0, skippedAudio = 0;
        int totalStacks = animations.Sum(a => a.Stacks.Count(s => s is not AudioStack));
        yield return new RetargetProgress(0, totalStacks, null);

        foreach (var sourceAnimation in animations)
        {
            var destinationFolder = ResolvePath(model, destination, sourceAnimation.FolderPath);
            var animationSource = model.Document.Schema.NewObject("CAnimation");
            animationSource.Set("Name", UniqueNames.For(destinationFolder.Source, sourceAnimation.Name ?? "Animation"));
            animationSource.Set("CurrentTime", sourceAnimation.CurrentTime);
            destinationFolder.Source.AddChild(animationSource);
            var animation = (Animation)model.Wrap(animationSource);
            // Compensate Scale first: the locked-key solve below reads it.
            CopyCompensateKeys(sourceAnimation, animation, map, sourceEvaluator, targetEvaluator);

            var stacks = sourceAnimation.Stacks;
            AnimStack? lastStack = null;
            for (int stackIndex = 0; stackIndex < stacks.Count; stackIndex++)
            {
                var sourceStack = stacks[stackIndex];
                if (sourceStack is AudioStack)
                {
                    skippedAudio++;
                    continue;
                }
                var stack = CopyStack(model, animation, sourceStack, stackIndex, stacks.Count);
                CopyStackKeys(sourceStack, stack, map, sourceEvaluator, targetRest, targetEvaluator, ratio,
                    method);
                // Different rigs: the converted leg keys do not reach the converted controllers, so
                // every IK-locked key's chain is re-solved there (else the key frames jump).
                if (solveLocked) new KeyEditor(target, targetEvaluator).SolveLockedKeys(stack);
                lastStack = stack;
                copiedStacks++;
                yield return new RetargetProgress(copiedStacks, totalStacks, sourceAnimation.Name);
            }
            if (lastStack != null) animation.CurrentStack = lastStack;
            imported.Add(animation);
        }

        if (skippedAudio > 0)
            Warn($"{skippedAudio} audio stack(s) were not copied: their events point at the source character's audio entries.");

        if (imported.Count > 0) target.CurrentAnimation = imported[^1];
        run.Result = new RetargetingResult
        {
            Character = target,
            Map = map,
            Animations = imported,
            Destination = destination,
            Ratio = ratio,
            CopiedStacks = copiedStacks,
            SkippedAudioStacks = skippedAudio,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The reference-pose scale ratio:
    /// the transfer state's **character pair** first, then every
    /// matched pair in the **matched tree's pre-order** (a pair hangs under its nearest matched
    /// ancestor; siblings in matching order). With <c>n</c> entries, <c>q = n/3 + 1</c>, and
    /// <c>ratio = mean of |T[(i+q) mod n] - T[i]| / |S[(i+q) mod n] - S[i]|</c> over the entries whose
    /// two reference-pose **world** distances are not degenerate (the 1e-5). Defaults to 1
    /// when no entry is usable. On the q45 pair this reads 0.982412 against the 0.98194.
    /// </summary>
    public static float ComputeRatio(RetargetingMap map)
    {
        var sourceEvaluator = new PoseEvaluator(map.Source);
        var targetEvaluator = new PoseEvaluator(map.Target);
        var sourceMatrices = sourceEvaluator.WorldMatrices(null, 0);
        var targetMatrices = targetEvaluator.WorldMatrices(null, 0);

        // The list: the character pair is the root, then the matched pairs in the matched
        // tree's pre-order. The character world matrices are the characters' own (stored) GTMs.
        var sourcePositions = new List<Vector3> { map.Source.WorldMatrix.Translation };
        var targetPositions = new List<Vector3> { map.Target.WorldMatrix.Translation };
        var pairs = map.Pairs.ToList();
        int pairCount = pairs.Count;
        var children = new List<int>[pairCount];
        var roots = new List<int>();
        for (int i = 0; i < pairCount; i++) children[i] = new List<int>();
        for (int i = 0; i < pairCount; i++)
        {
            int parent = -1;
            for (var ancestor = pairs[i].Source.Parent3D; ancestor != null; ancestor = ancestor.Parent3D)
            {
                int candidate = pairs.FindIndex(p => ReferenceEquals(p.Source, ancestor));
                if (candidate >= 0) { parent = candidate; break; }
            }
            if (parent < 0) roots.Add(i);
            else children[parent].Add(i);
        }
        void Append(int index)
        {
            var pair = pairs[index];
            int si = sourceEvaluator.IndexOf(pair.Source);
            int ti = targetEvaluator.IndexOf(pair.Target);
            if (si >= 0 && ti >= 0)
            {
                sourcePositions.Add(sourceMatrices[si].Translation);
                targetPositions.Add(targetMatrices[ti].Translation);
            }
            foreach (int child in children[index]) Append(child);
        }
        foreach (int root in roots) Append(root);

        int n = sourcePositions.Count;
        if (n < 2) return 1f;
        int shift = n / 3 + 1;
        float sum = 0f;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + shift) % n;
            float ds = Vector3.Distance(sourcePositions[i], sourcePositions[j]);
            float dt = Vector3.Distance(targetPositions[i], targetPositions[j]);
            if (ds <= MinRatioDistance || dt <= MinRatioDistance) continue;
            sum += dt / ds;
            count++;
        }
        return count == 0 ? 1f : sum / count;
    }

    /// <summary>
    /// Whether the two characters' matched reference poses differ (the <see cref="RetargetMethod.Auto"/>
    /// rule): the same-rig case - a character duplicated in memory - must keep the local reading bit
    /// for bit, and a different rig gets the reference-pose one. <paramref name="rotationsOnly"/>
    /// ignores translation and scale (proportions), leaving only differently oriented joint frames.
    /// </summary>
    private static bool ReferencePosesDiffer(RetargetingMap map, bool rotationsOnly = false)
    {
        var sourceEvaluator = new PoseEvaluator(map.Source);
        var targetEvaluator = new PoseEvaluator(map.Target);
        var sourceRest = sourceEvaluator.RestTransforms;
        var targetRest = targetEvaluator.RestTransforms;
        foreach (var pair in map.Pairs)
        {
            int si = sourceEvaluator.IndexOf(pair.Source);
            int ti = targetEvaluator.IndexOf(pair.Target);
            if (si < 0 || ti < 0) continue;
            var from = sourceRest[si];
            var to = targetRest[ti];
            if (MathF.Abs(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(from.Rotation), Quaternion.Normalize(to.Rotation))) - 1f) > 1e-5f)
                return true;
            if (rotationsOnly) continue;
            if (Vector3.Distance(from.Scale, to.Scale) > 1e-4f) return true;
            if (Vector3.Distance(from.Translation, to.Translation) > 1e-3f) return true;
        }
        return false;
    }

    // ---- stacks and keys --------------------------------------------------------------------

    /// <summary>
    /// Carries Compensate Scale over (compensate-scale-keys.md "Retargeting"): every mapped target
    /// gets its source's keys, and when the two rigging values differ, a key at the animation's first
    /// frame holding the source's rigging value, so the target compensates exactly when the source
    /// does. The goal is the same motion on screen, not the same stored values.
    /// </summary>
    private static void CopyCompensateKeys(Animation sourceAnimation, Animation targetAnimation, RetargetingMap map,
        PoseEvaluator sourceEvaluator, PoseEvaluator targetEvaluator)
    {
        int start = 0;
        foreach (var stack in sourceAnimation.Stacks)
            foreach (var key in stack.Keys)
                start = Math.Min(start, key.Time);
        var sourceTable = CompensateScaleKeys.Table(sourceAnimation);
        foreach (var targetObject in targetEvaluator.Controllers)
        {
            if (map.SourceOf(targetObject) is not { } sourceObject || sourceEvaluator.IndexOf(sourceObject) < 0) continue;
            var keys = sourceTable != null && sourceTable.TryGetValue(sourceObject.Source, out var found) ? found : [];
            bool startKeyed = keys.Length > 0 && keys[0].Frame <= start;
            if (!startKeyed && sourceObject.CompensatesParentScale != targetObject.CompensatesParentScale)
                CompensateScaleKeys.SetKey(targetAnimation, targetObject, start, sourceObject.CompensatesParentScale);
            foreach (var key in keys)
                CompensateScaleKeys.SetKey(targetAnimation, targetObject, key.Frame, key.On);
        }
        targetEvaluator.Invalidate();
    }

    private static AnimStack CopyStack(SceneModel model, Animation animation, AnimStack source, int index, int count)
    {
        var schema = model.Document.Schema;
        var sourceObject = schema.NewObject("CAnimStack");
        sourceObject.Set("Name", source.Name ?? "Stack");
        sourceObject.Set("Flags", source.Flags);
        sourceObject.Set("FlagsNoModify", source.FlagsNoModify);
        sourceObject.Set("Color", AnimationBankEditor.StackColor(Math.Max(0, count - index - 1)));
        sourceObject.Set("Opacity", source.Opacity);
        sourceObject.Set("Tweenage", source.Tweenage);
        var times = (List<object?>)sourceObject["TimeArray"]!;
        times.Clear();
        foreach (var key in source.Keys)
        {
            var entry = schema.NewStruct("CAnimStackKey");
            entry.Set("Time", key.Time);
            entry.Set("Flags", key.Flags);
            entry.Set("FadeIn", key.FadeIn);
            entry.Set("FadeOut", key.FadeOut);
            times.Add(entry);
        }
        animation.Source.AddChild(sourceObject);
        animation.StackRefs.Add(new AktReference(sourceObject));
        return (AnimStack)model.Wrap(sourceObject);
    }

    /// <summary>
    /// Creates the target tracks and copies their keys. A key stores a delta from the owning
    /// character's reference pose, so the
    /// source key's delta is applied to the target reference pose and its translation is scaled by
    /// <paramref name="ratio"/>; a null source key stays null.
    /// </summary>
    private static void CopyStackKeys(AnimStack sourceStack, AnimStack targetStack, RetargetingMap map,
        PoseEvaluator sourceEvaluator, Transform[] targetRest, PoseEvaluator targetEvaluator, float ratio,
        RetargetMethod method)
    {
        var keys = targetStack.Keys;
        int keyCount = keys.Count;
        if (keyCount == 0) return;

        int targetCount = targetEvaluator.Controllers.Count;
        var sourceIndex = new int[targetCount];
        // A source may drive several targets: a copy row of the dialog adds a second target.
        var targetBySource = new Dictionary<Object3D, List<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < targetCount; i++)
        {
            var targetObject = targetEvaluator.Controllers[i];
            sourceIndex[i] = -1;
            if (map.SourceOf(targetObject) is not { } sourceObject) continue;
            int si = sourceEvaluator.IndexOf(sourceObject);
            if (si < 0) continue;
            sourceIndex[i] = si;
            if (!targetBySource.TryGetValue(sourceObject, out var targetIndices))
                targetBySource[sourceObject] = targetIndices = [];
            targetIndices.Add(i);
        }

        // An animated source joint with no counterpart sitting between two mapped joints (a spine
        // with one more bone than the target's) would drop its motion and turn everything below
        // it. Its keys are folded into the next mapped joint below it: the chain from the first
        // unmapped ancestor down to the mapped joint is composed and read back as one delta
        // relative to the chain's composed reference pose (FoldValue/FoldTangent).
        var sourceTrackOf = new Dictionary<Object3D, AnimTrack>(ReferenceEqualityComparer.Instance);
        foreach (var t in sourceStack.Tracks)
            if (t.Target is { } o && t.Keys.Any(key => key is TransformKey)) sourceTrackOf.TryAdd(o, t);
        var foldChain = new int[]?[targetCount];
        var foldTracks = new AnimTrack?[]?[targetCount];
        var sourceRestLocals = sourceEvaluator.RestTransforms;
        for (int i = 0; i < targetCount; i++)
        {
            int si = sourceIndex[i];
            if (si < 0) continue;
            var chain = new List<int> { si };
            for (var a = sourceEvaluator.Controllers[si].Parent3D; a != null; a = a.Parent3D)
            {
                if (targetBySource.ContainsKey(a)) break;
                int ai = sourceEvaluator.IndexOf(a);
                if (ai < 0) break;
                chain.Insert(0, ai);
            }
            if (chain.Count < 2) continue;
            var chainTracks = chain.Select(ci => sourceTrackOf.GetValueOrDefault(sourceEvaluator.Controllers[ci])).ToArray();
            if (!chainTracks.Take(chain.Count - 1).Any(t => t != null)) continue;
            foldChain[i] = chain.ToArray();
            foldTracks[i] = chainTracks;
        }

        Transform SourceValue(int i, int k, Transform own) => foldChain[i] is { } chain
            ? FoldValue(chain, foldTracks[i]!, sourceRestLocals, k, own)
            : own;

        // The world reading conjugates a node's delta by the rotation between the two nodes'
        // reference-pose world orientations; the local reading applies it unchanged. The reference
        // reading is the decoded different-reference-pose branch: the same conjugation on the
        // rotation, the translation delta kept in the source's frame and scaled by the ratio alone
        // (the stored direction is preserved to 0.28 deg and its length is the ratio), and a
        // per-node scale factor from the two reference-pose world matrices, normalized so that it
        // preserves volume (the cube-root guard). Every reading is exactly the
        // local one when the reference poses agree, which is why the same-rig case stays exact.
        // A key's rotation is a delta in the node's own local frame (Compose: rest.R * delta.R), so
        // when the two nodes' reference-pose world orientations differ the delta must change basis:
        // with C = Rsource^-1 * Rtarget (the rest world rotations), the target delta is
        // C^-1 * delta * C, which gives the target node the same world-space rotation change as the
        // source node whatever the two rigs' joint orientations (the parents cancel out as long as
        // they follow their own sources). A translation delta lives in the parent's frame, so it is
        // carried through the two parents' rest world frames (source parent to world, scaled by
        // the ratio, world to target parent). Both are the identity when the reference poses agree.
        var basis = new Quaternion?[targetCount];
        var translationMap = new Matrix4x4?[targetCount];
        var scaleFactor = new Vector3?[targetCount];
        if (method is RetargetMethod.World or RetargetMethod.Reference)
        {
            var sourceRestWorlds = Worlds(sourceEvaluator, sourceEvaluator.RestTransforms);
            var targetRestWorlds = Worlds(targetEvaluator, targetEvaluator.RestTransforms);
            for (int i = 0; i < targetCount; i++)
            {
                int si = sourceIndex[i];
                if (si < 0) continue;
                if (!Matrix4x4.Decompose(sourceRestWorlds[si], out _, out var sourceRotation, out _)) continue;
                if (!Matrix4x4.Decompose(targetRestWorlds[i], out _, out var targetRotation, out _)) continue;
                var c = Quaternion.Normalize(Quaternion.Inverse(Quaternion.Normalize(sourceRotation))
                    * Quaternion.Normalize(targetRotation));
                if (MathF.Abs(MathF.Abs(c.W) - 1f) > 1e-7f) basis[i] = c;
                if (method == RetargetMethod.World)
                {
                    // A folded chain's delta lives in the frame of the chain top's parent.
                    var sourceParent = ParentRestWorld(sourceEvaluator, sourceRestWorlds,
                        sourceEvaluator.Controllers[foldChain[i] is { } chain ? chain[0] : si]);
                    var targetParent = ParentRestWorld(targetEvaluator, targetRestWorlds, targetEvaluator.Controllers[i]);
                    if (Matrix4x4.Invert(targetParent, out var inverseTargetParent))
                    {
                        var parentMap = sourceParent * inverseTargetParent;
                        parentMap.Translation = Vector3.Zero;
                        if (!IsIdentity(parentMap)) translationMap[i] = parentMap;
                    }
                }
                if (method != RetargetMethod.Reference) continue;
                if (!Matrix4x4.Invert(targetRestWorlds[i], out var inverseTargetWorld)) continue;
                var relative = sourceRestWorlds[si] * inverseTargetWorld;
                var factor = Object3D.LocalScale(relative);
                float determinant = factor.X * factor.Y * factor.Z;
                if (MathF.Abs(determinant) < 1e-6f) continue;
                scaleFactor[i] = factor / MathF.Cbrt(determinant);
            }
        }

        Transform Convert(int i, Transform value)
        {
            var scaled = ScaleValue(value, ratio);
            if (method == RetargetMethod.Reference && scaleFactor[i] is { } factor)
                scaled = new Transform(scaled.Translation, scaled.Rotation, scaled.Scale * factor);
            // The world reading carries the translation through the parents' frames; the reference
            // reading leaves it in the source's frame (the stored direction is the source's).
            var translation = translationMap[i] is { } toTarget
                ? Vector3.TransformNormal(scaled.Translation, toTarget)
                : scaled.Translation;
            if (basis[i] is not { } c) return new Transform(translation, scaled.Rotation, scaled.Scale);
            var inverse = Quaternion.Inverse(c);
            return new Transform(translation, inverse * scaled.Rotation * c,
                ChangeScaleBasis(scaled.Scale, c));
        }

        // Target tracks: one per source track that has a mapped target. Keep the source track so
        // its per-key IK flags, spaces and camera FOV can be copied alongside the value.
        var tracks = new Dictionary<int, AnimTrack>();
        var sourceTracks = new Dictionary<int, AnimTrack>();
        foreach (var sourceTrack in sourceStack.Tracks)
        {
            if (sourceTrack.Target is not { } sourceTarget) continue;
            if (!targetBySource.TryGetValue(sourceTarget, out var targetIndices)) continue;
            foreach (int ti in targetIndices)
            {
                var targetObject = targetEvaluator.Controllers[ti];
                var track = TrackFactory.Create(targetStack, targetObject, null);
                tracks[ti] = track;
                sourceTracks[ti] = sourceTrack;
                CopyTangentModes(sourceTrack, track);
            }
        }

        // The converted local of every target controller at this key, for the IK keys' RefArray.
        var converted = new Transform[targetCount];
        for (int k = 0; k < keyCount; k++)
        {
            for (int i = 0; i < targetCount; i++) converted[i] = targetRest[i];
            for (int i = 0; i < targetCount; i++)
            {
                if (!sourceTracks.TryGetValue(i, out var sourceTrack)) continue;
                if (k >= sourceTrack.Keys.Count) continue;
                if (sourceTrack.Keys[k] is null) continue;
                int si = sourceIndex[i];
                if (si < 0) continue;
                var sourceKeyValue = (sourceTrack.Keys[k] as TransformKey)?.Value;
                if (sourceKeyValue is not { } value) continue;
                converted[i] = PoseEvaluator.Compose(targetRest[i], Convert(i, SourceValue(i, k, value)));
            }

            foreach (var (ti, track) in tracks)
            {
                if (k >= track.Keys.Count) continue;
                if (!sourceTracks.TryGetValue(ti, out var sourceTrack) || k >= sourceTrack.Keys.Count)
                {
                    SetNullKey(track, k);
                    continue;
                }
                var sourceKey = sourceTrack.Keys[k];
                if (sourceKey is not TransformKey sourceTransform)
                {
                    SetNullKey(track, k);
                    continue;
                }
                if (track.Keys[k] is not TransformKey key) continue;
                key.Value = Convert(ti, SourceValue(ti, k, sourceTransform.Value));
                // An IK or reverse-foot key keeps the source's own angles and flag; the solved
                // chain is written below, once every controller's converted local is known.
                if (sourceTransform is IKKey sourceIk) CopyIkKey(sourceIk, key as IKKey);
                // A camera key keeps its FOV channel; other keys have no FOV.
                if (sourceTransform is CameraKey sourceCamera && key is CameraKey camera)
                {
                    camera.Fov = sourceCamera.Fov;
                    camera.Flags = sourceCamera.Flags;
                }
                // The key's space, through the same map; an unmapped space becomes the parent.
                var space = sourceTransform.Space;
                key.Space = space == null ? null : space == map.Source ? map.Target : map.TargetOf(space);
                // The tangents are converted like the values (the source's translation channel is
                // scaled by the ratio, and a reference conversion turns their rotation the same way),
                // so the curve keeps its shape.
                if (foldChain[ti] is { } chain)
                {
                    var folded = foldTracks[ti]!;
                    if (k < track.TangentsIn.Count && track.TangentsIn[k] is TransformKey targetIn
                        && k < sourceTrack.TangentsIn.Count && sourceTrack.TangentsIn[k] is TransformKey ownIn)
                        targetIn.Value = Convert(ti, FoldTangent(chain, folded, sourceRestLocals, k, sourceTransform.Value, ownIn.Value, tangentIn: true));
                    if (k < track.TangentsOut.Count && track.TangentsOut[k] is TransformKey targetOut
                        && k < sourceTrack.TangentsOut.Count && sourceTrack.TangentsOut[k] is TransformKey ownOut)
                        targetOut.Value = Convert(ti, FoldTangent(chain, folded, sourceRestLocals, k, sourceTransform.Value, ownOut.Value, tangentIn: false));
                }
                else CopyTangent(sourceTrack, track, k, value => Convert(ti, value));
            }

            WriteRefArrays(tracks, converted, targetEvaluator, k, targetStack.Animation, keys[k].Time);
        }
    }

    /// <summary>Key <paramref name="k"/> of a folded ancestor's track; the identity delta when it has none.</summary>
    private static Transform FoldKey(AnimTrack? track, int k) =>
        track != null && k < track.Keys.Count && track.Keys[k] is TransformKey key
            ? key.Value
            : new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>
    /// The delta of a chain of source joints (top first, the mapped joint last, <paramref name="own"/>
    /// its key) read as one joint: the composed rotation relative to the chain's composed reference
    /// rotation, and the mapped joint's position change in the frame of the chain top's parent.
    /// </summary>
    private static Transform FoldValue(int[] chain, AnimTrack?[] tracks, Transform[] rest, int k, Transform own)
    {
        Vector3 p = Vector3.Zero, p0 = Vector3.Zero;
        Quaternion q = Quaternion.Identity, q0 = Quaternion.Identity;
        for (int j = 0; j < chain.Length; j++)
        {
            var d = j == chain.Length - 1 ? own : FoldKey(tracks[j], k);
            var r = rest[chain[j]];
            p += Vector3.Transform(r.Translation + d.Translation, q);
            p0 += Vector3.Transform(r.Translation, q0);
            q = q * r.Rotation * d.Rotation;
            q0 = q0 * r.Rotation;
        }
        return new Transform(p - p0, Quaternion.Inverse(q0) * q, own.Scale);
    }

    /// <summary>
    /// A tangent of the folded chain (see <see cref="FoldValue"/>): the derivative of the composed
    /// rotation in its own body frame (each joint's log tangent turned into the mapped joint's
    /// frame and summed) and of the mapped joint's position (each joint's translation tangent plus
    /// its rotation swinging the rest of the chain, at angular rate 2 x the log tangent).
    /// </summary>
    private static Transform FoldTangent(int[] chain, AnimTrack?[] tracks, Transform[] rest, int k,
        Transform ownValue, Transform ownTangent, bool tangentIn)
    {
        int n = chain.Length;
        var rotation = new Quaternion[n];
        var translation = new Vector3[n];
        var rotationTangent = new Vector3[n];
        var translationTangent = new Vector3[n];
        for (int j = 0; j < n; j++)
        {
            bool own = j == n - 1;
            var d = own ? ownValue : FoldKey(tracks[j], k);
            var r = rest[chain[j]];
            rotation[j] = r.Rotation * d.Rotation;
            translation[j] = r.Translation + d.Translation;
            Transform? tangent = own ? ownTangent
                : tracks[j] is { } t && k < (tangentIn ? t.TangentsIn : t.TangentsOut).Count
                    && (tangentIn ? t.TangentsIn : t.TangentsOut)[k] is TransformKey tk ? tk.Value : null;
            if (tangent is not { } tv) continue;
            rotationTangent[j] = new Vector3(tv.Rotation.X, tv.Rotation.Y, tv.Rotation.Z);
            translationTangent[j] = tv.Translation;
        }

        var swing = new Vector3[n];
        for (int j = n - 2; j >= 0; j--)
            swing[j] = translation[j + 1] + Vector3.Transform(swing[j + 1], rotation[j + 1]);
        Vector3 positionRate = Vector3.Zero, rotationRate = Vector3.Zero;
        var frame = Quaternion.Identity;
        for (int j = 0; j < n; j++)
        {
            var local = translationTangent[j]
                + Vector3.Transform(Vector3.Cross(2 * rotationTangent[j], swing[j]), rotation[j]);
            positionRate += Vector3.Transform(local, frame);
            frame *= rotation[j];
        }
        var below = Quaternion.Identity;
        for (int j = n - 1; j >= 0; j--)
        {
            rotationRate += Vector3.Transform(rotationTangent[j], Quaternion.Inverse(below));
            below = rotation[j] * below;
        }
        return new Transform(positionRate, new Quaternion(rotationRate, ownTangent.Rotation.W), ownTangent.Scale);
    }

    /// <summary>The reference-pose world matrix of <paramref name="node"/>'s parent (identity at the top).</summary>
    private static Matrix4x4 ParentRestWorld(PoseEvaluator evaluator, Matrix4x4[] restWorlds, Object3D node)
    {
        if (node.Parent3D is not { } parent) return Matrix4x4.Identity;
        int index = evaluator.IndexOf(parent);
        return index >= 0 ? restWorlds[index] : parent.WorldMatrix;
    }

    private static bool IsIdentity(Matrix4x4 m)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22)
            + MathF.Abs(d.M23) + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) < 1e-6f;
    }

    /// <summary>
    /// A per-axis scale delta expressed in the source node's frame, read along the target node's
    /// axes: target axis j is the source direction <c>C e_j</c>, so it takes the source axes'
    /// scales weighted by that direction's squared components (an exact permutation when the two
    /// frames differ by quarter turns, and the identity for a uniform scale).
    /// </summary>
    private static Vector3 ChangeScaleBasis(Vector3 scale, Quaternion c)
    {
        float Along(Vector3 axis)
        {
            var d = Vector3.Transform(axis, c);
            return d.X * d.X * scale.X + d.Y * d.Y * scale.Y + d.Z * d.Z * scale.Z;
        }
        return new Vector3(Along(Vector3.UnitX), Along(Vector3.UnitY), Along(Vector3.UnitZ));
    }

    /// <summary>A key value with its translation delta scaled by the rig ratio.</summary>
    private static Transform ScaleValue(Transform value, float ratio) =>
        new(value.Translation * ratio, value.Rotation, value.Scale);

    /// <summary>Replaces target key <paramref name="k"/> with a null key pointer, like the source had.</summary>
    private static void SetNullKey(AnimTrack track, int k)
    {
        var list = SceneValues.Items(track.Source, "KeyList");
        if (k < list.Count) list[k] = null;
    }

    /// <summary>Copies key <paramref name="k"/>'s in and out tangents through the same conversion as the value.</summary>
    private static void CopyTangent(AnimTrack source, AnimTrack target, int k, Func<Transform, Transform> convert)
    {
        if (k < source.TangentsIn.Count && k < target.TangentsIn.Count
            && source.TangentsIn[k] is TransformKey sourceIn && target.TangentsIn[k] is TransformKey targetIn)
            targetIn.Value = convert(sourceIn.Value);
        if (k < source.TangentsOut.Count && k < target.TangentsOut.Count
            && source.TangentsOut[k] is TransformKey sourceOut && target.TangentsOut[k] is TransformKey targetOut)
            targetOut.Value = convert(sourceOut.Value);
    }

    /// <summary>Copies the source key's IK flag, pole angle and (for a reverse-foot key) roll, twist, bank.</summary>
    private static void CopyIkKey(IKKey source, IKKey? target)
    {
        if (target == null) return;
        target.SetFlags(source.Flags);
        target.PoleAngle = source.PoleAngle;
        if (target is ReverseFootKey foot && source is ReverseFootKey sourceFoot)
        {
            foot.RollAngle = sourceFoot.RollAngle;
            foot.TwistAngle = sourceFoot.TwistAngle;
            foot.BankAngle = sourceFoot.BankAngle;
        }
    }

    /// <summary>
    /// Writes every mapped IK/reverse-foot key's <c>RefArray</c> from the converted chain locals at
    /// key <paramref name="k"/>: the chain joints' absolute locals, plus (reverse foot) the
    /// controller relative to the ball, as <c>AnimationBankEditor.ChainOf</c> stores them.
    /// </summary>
    private static void WriteRefArrays(Dictionary<int, AnimTrack> tracks, Transform[] converted,
        PoseEvaluator targetEvaluator, int k, Animation? animation, float time)
    {
        var worlds = default(Matrix4x4[]);
        foreach (var (_, track) in tracks)
        {
            if (track.Keys[k] is not IKKey key) continue;
            if (track.Target is not ControllerIK controller) continue;
            var chain = new List<Transform>();
            bool complete = true;
            foreach (var joint in controller.Chain)
            {
                int ji = targetEvaluator.IndexOf(joint);
                if (ji < 0) { complete = false; break; }
                chain.Add(converted[ji]);
            }
            if (!complete) continue;
            if (controller is ControllerReverseFoot && chain.Count == 4)
            {
                worlds ??= Worlds(targetEvaluator, converted, animation, time);
                int ci = targetEvaluator.IndexOf(controller);
                int ball = targetEvaluator.IndexOf(controller.Chain[3]);
                if (ci >= 0 && ball >= 0 && Matrix4x4.Invert(worlds[ball], out var inverseBall))
                    chain.Add(Transform.FromMatrixLoose(worlds[ci] * inverseBall));
                else
                    chain.Add(Transform.Identity);
            }
            key.InitChainTransforms(chain);
        }
    }

    /// <summary>
    /// World matrices of every controller for a pose given as absolute locals, in hierarchy order.
    /// Compensate Scale is read at <paramref name="time"/> of <paramref name="animation"/> (keyed,
    /// compensate-scale-keys.md); without an animation, the rigging value.
    /// </summary>
    private static Matrix4x4[] Worlds(PoseEvaluator evaluator, Transform[] locals, Animation? animation = null, float time = 0f)
    {
        int n = evaluator.Controllers.Count;
        var world = new Matrix4x4?[n];
        Matrix4x4 WorldOf(int i)
        {
            if (world[i] is { } cached) return cached;
            var local = locals[i].ToMatrix();
            var parentObject = evaluator.Controllers[i].Parent3D;
            int parent = parentObject != null ? evaluator.IndexOf(parentObject) : -1;
            Matrix4x4 result;
            if (parent >= 0)
            {
                var parentWorld = WorldOf(parent);
                result = WorldComposition.Compose(local, parentWorld, locals[parent].Scale,
                    evaluator.Compensates(evaluator.Controllers[i], animation, time));
            }
            else if (parentObject != null)
            {
                result = WorldComposition.Compose(local, parentObject.WorldMatrix,
                    Object3D.LocalScale(parentObject.LocalMatrix), evaluator.Compensates(evaluator.Controllers[i], animation, time));
            }
            else
            {
                result = local;
            }
            world[i] = result;
            return result;
        }
        for (int i = 0; i < n; i++) WorldOf(i);
        return world.Select(m => m ?? Matrix4x4.Identity).ToArray();
    }

    /// <summary>Copies per-channel tangent modes where the two tracks have the same channel count.</summary>
    private static void CopyTangentModes(AnimTrack source, AnimTrack target)
    {
        var sourceModes = source.TangentModes;
        int targetChannels = target.TangentModes.FirstOrDefault()?.Length ?? 0;
        int count = Math.Min(sourceModes.Count, target.Keys.Count);
        for (int k = 0; k < count; k++)
        {
            var modes = sourceModes[k];
            for (int c = 0; c < Math.Min(modes.Length, targetChannels); c++)
                target.SetTangentMode(k, c, modes[c]);
        }
    }

    // ---- Anim Bank and folders ---------------------------------------------------------------

    private static Folder CreateBank(SceneModel model, Character character)
    {
        var source = model.Document.Schema.NewObject("CDummy");
        source.Set("Name", "__Anims");
        character.Source.AddChild(source);
        return (Folder)model.Wrap(source);
    }

    private static Folder CreateFolder(SceneModel model, SceneObject parent, string name)
    {
        var source = model.Document.Schema.NewObject("CDummy");
        source.Set("Name", name);
        parent.Source.AddChild(source);
        return (Folder)model.Wrap(source);
    }

    /// <summary>Walks/creates the named folders under <paramref name="root"/>; a same-named folder is reused.</summary>
    private static SceneObject ResolvePath(SceneModel model, SceneObject root, IReadOnlyList<string> path)
    {
        var current = root;
        foreach (string name in path)
        {
            var existing = current.Children.OfType<Folder>().FirstOrDefault(f => f.Name == name);
            current = existing ?? CreateFolder(model, current, name);
        }
        return current;
    }
}