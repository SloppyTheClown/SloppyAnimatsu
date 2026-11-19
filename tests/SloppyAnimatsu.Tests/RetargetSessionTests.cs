using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Animation Retargeting dialog's state
/// which characters it offers, the two-character message, the default pair, the
/// mapping summary and the transfer into the "Retargeted From" folder. A real sample supplies the
/// characters: a fresh one is cloned in memory, as <c>RetargetingTests</c> does.
/// </summary>
public sealed class RetargetSessionTests
{
    [Fact]
    public void ASceneWithFewerThanTwoCharactersIsRefused()
    {
        // A new scene has one empty character, so nothing can be retargeted between.
        var session = new RetargetSession(SceneFactory.NewSceneModel(60));
        Assert.Empty(session.Characters);
        Assert.False(session.CanRetarget);
        Assert.Equal(RetargetSession.NeedsTwoCharacters, session.Refusal);
        Assert.Equal(RetargetSession.NeedsTwoCharacters, session.Summary);
        Assert.Null(session.Map);
        Assert.Throws<InvalidOperationException>(() => session.Run());
    }

    [Fact]
    public void TheDefaultPairIsTheFirstAndTheLastCharacter()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        var session = new RetargetSession(model);
        var candidates = model.Scene.Characters
            .Where(character => RetargetingMap.Candidates(character).Count > 0)
            .ToList();
        Assert.Equal(candidates.Count, session.Characters.Count);
        Assert.Same(candidates[0], session.Source);
        Assert.Same(candidates[^1], session.Target);
        Assert.Same(source, session.Source);
        Assert.Same(target, session.Target);
        Assert.True(session.CanRetarget, session.Refusal);
    }

    [Fact]
    public void TheSummaryCountsThePairsAndTheAnimationsToCopy()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        var session = new RetargetSession(model) { Source = source, Target = target };
        RetargetingMap map = Assert.IsType<RetargetingMap>(session.Map);
        Assert.Equal(RetargetingMap.Candidates(source).Count, map.Pairs.Count);
        Assert.Empty(map.UnmatchedSources);
        Assert.Empty(map.UnmatchedTargets);
        Assert.Equal(map.Pairs.Count, session.PairLines.Count);
        Assert.Contains("controller pair(s)", session.Summary);
        Assert.Contains($"{source.Animations.Count()} animation(s) to copy", session.Summary);
        // The pair lines read "source  →  target" for each mapping.
        Assert.Contains(session.PairLines, line => line.Contains("Pelvis") && line.Contains("→"));
    }

    [Fact]
    public void RunCopiesTheAnimationsIntoARetargetedFromFolder()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        int sourceCount = source.Animations.Count();
        Assert.Equal(0, target.Animations.Count());

        var session = new RetargetSession(model) { Source = source, Target = target };
        RetargetingResult result = session.Run();

        Assert.Equal(sourceCount, result.Animations.Count);
        Assert.Equal($"Retargeted From {source.Name}", result.Destination.Name);
        Assert.Equal(sourceCount, target.Animations.Count());
        Assert.Same(result.Animations[^1], target.CurrentAnimation);
        // The source keeps its animations; the copies are the target's.
        Assert.Equal(sourceCount, source.Animations.Count());
        Assert.All(result.Animations, animation => Assert.Same(target, animation.Character));
    }

    [Fact]
    public void TheSameCharacterCannotBeBothSides()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        Clone(model, source, "Copy");

        var session = new RetargetSession(model) { Source = source, Target = source };
        Assert.False(session.CanRetarget);
        Assert.Equal("The source and the target are the same character.", session.Refusal);
        Assert.Null(session.Map);
        Assert.Throws<InvalidOperationException>(() => session.Run());
    }

    [Fact]
    public void ASourceWithoutAnimationsIsRefused()
    {
        if (Load("19_run.akt") is not { } model) return;
        var rigged = Dup(model);
        var clone = Clone(model, rigged, "Copy");

        var session = new RetargetSession(model) { Source = clone, Target = rigged };
        Assert.False(session.CanRetarget);
        Assert.Equal("Copy has no animations to retarget.", session.Refusal);
    }

    [Fact]
    public void TheAdviceIsShownUnlessTheProfileSkipsIt()
    {
        Assert.True(new RetargetSession(SceneFactory.NewSceneModel(60)).ShowAdvice);
        Assert.False(new RetargetSession(SceneFactory.NewSceneModel(60), skipAdvice: true).ShowAdvice);
        Assert.Equal(
            "Reference poses of your characters must be similar to ensure the best retargeting results.",
            RetargetSession.Advice);
    }

    [Fact]
    public void ACharacterOutsideTheSceneIsNotAccepted()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var other = new SceneModel(SceneFactory.NewScene());
        var outsider = other.Scene.Characters.First();

        var session = new RetargetSession(model) { Source = source, Target = outsider };
        Assert.Same(source, session.Source);
        Assert.Same(target, session.Target);
    }

    [Fact]
    public void EditingARowToAnotherTargetChangesThePairAndTheFooterCounts()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        var session = new RetargetSession(model) { Source = source, Target = target };
        int pairs = session.Map!.Pairs.Count;
        Assert.Equal(RetargetingMap.Candidates(source).Count, pairs);

        // Point the first row at the second row's target: a mapping is one to one, so the second
        // row loses its target and the pair count drops by one.
        var first = session.Rows[0];
        var second = session.Rows[1];
        Assert.NotNull(first.Target);
        Assert.NotNull(second.Target);
        Assert.True(session.SetTarget(first.Source, second.Target));

        var map = session.Map!;
        Assert.Same(second.Target, session.Rows[0].Target);
        Assert.Null(session.Rows[1].Target);
        Assert.DoesNotContain(map.Pairs, pair => ReferenceEquals(pair.Source, second.Source));
        Assert.Contains(second.Source, map.UnmatchedSources);
        Assert.Contains(first.Target, map.UnmatchedTargets);
        Assert.Equal(pairs - 1, map.Pairs.Count);
        Assert.Contains($"{pairs - 1} controller pair(s)", session.Summary);
        Assert.Contains("1 source and 1 target controller(s) unmatched", session.Summary);
        Assert.Contains(session.PairLines, line => line == $"{first.Source.Name}  →  {second.Target!.Name}");
    }

    [Fact]
    public void ChoosingNoneDropsTheSourceFromThePairsAndTheTransferLeavesItUnmatched()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        var session = new RetargetSession(model) { Source = source, Target = target };
        int pairs = session.Map!.Pairs.Count;
        var row = session.Rows[0];
        var droppedTarget = row.Target;
        Assert.NotNull(droppedTarget);

        Assert.True(session.SetTarget(row.Source, null));
        var map = session.Map!;
        Assert.DoesNotContain(map.Pairs, pair => ReferenceEquals(pair.Source, row.Source));
        Assert.Contains(row.Source, map.UnmatchedSources);
        Assert.Equal(pairs - 1, map.Pairs.Count);
        Assert.DoesNotContain(session.PairLines,
            line => line.StartsWith(row.Source.Name + "  ", StringComparison.Ordinal));

        RetargetingResult result = session.Run();
        var copiedTracks = result.Animations
            .SelectMany(animation => animation.Stacks)
            .SelectMany(stack => stack.Tracks)
            .ToList();
        Assert.DoesNotContain(copiedTracks, track => ReferenceEquals(track.Target, droppedTarget));

        // The SetMatch(source, null): the source keeps its row in Sources, its index is in
        // NullIndices, and it has no target.
        var matching = RetargetingMatching.Find(source, target);
        Assert.NotNull(matching);
        int index = matching!.Sources.ToList().FindIndex(candidate => ReferenceEquals(candidate, row.Source));
        Assert.True(index >= 0);
        Assert.Contains((uint)index, matching.NullIndices);
        Assert.DoesNotContain(matching.Matches(),
            stored => ReferenceEquals(stored.Source, row.Source) && stored.Target != null);
    }

    [Fact]
    public void TheDialogReopensTheStoredMatchingInsteadOfTheAutomaticMapping()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        var session = new RetargetSession(model) { Source = source, Target = target };
        var chosen = session.Rows[1].Target;
        Assert.NotNull(chosen);
        Assert.True(session.SetTarget(session.Rows[0].Source, chosen));
        Assert.Null(session.Rows[1].Target);
        session.Run();

        var reopened = new RetargetSession(model) { Source = source, Target = target };
        Assert.Equal(session.Rows.Select(row => row.Target?.Name), reopened.Rows.Select(row => row.Target?.Name));
        Assert.Same(chosen, reopened.Rows[0].Target);
        Assert.Null(reopened.Rows[1].Target);
    }

    [Fact]
    public void AStaleOrForeignStoredMatchingFallsBackToTheAutomaticMapping()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var other = Clone(model, source, "Other");
        var automatic = RetargetingMap.Build(source, target);

        // A matching for another pair: the dialog must not restore it for this one.
        RetargetingMatching.Record(model, RetargetingMap.Build(source, other));
        var session = new RetargetSession(model) { Source = source, Target = target };
        Assert.Equal(automatic.Selection.Select(row => row.Target?.Name),
            session.Rows.Select(row => row.Target?.Name));

        // A mapping for the right pair whose row points outside the pair's candidate lists is stale.
        RetargetingMatching.Record(model, automatic);
        var matching = RetargetingMatching.Find(source, target);
        Assert.NotNull(matching);
        var refs = (List<AktReference>)matching!.Source["Sources"]!;
        refs[0] = new AktReference(other.Joints[0].Source);
        var reopened = new RetargetSession(model) { Source = source, Target = target };
        Assert.Equal(automatic.Selection.Select(row => row.Target?.Name),
            reopened.Rows.Select(row => row.Target?.Name));

        // A destroyed character leaves dangling references: stale as well.
        refs[0] = new AktReference(999_999);
        var afterDestroy = new RetargetSession(model) { Source = source, Target = target };
        Assert.Equal(automatic.Selection.Select(row => row.Target?.Name),
            afterDestroy.Rows.Select(row => row.Target?.Name));
    }

    // ---- helpers --------------------------------------------------------------------------

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(path => Path.GetFileName(path) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static Character Dup(SceneModel model) => model.Scene.Characters.First(character => character.Name == "Dup");

    /// <summary>
    /// A character in the scene: a deep copy of <paramref name="source"/> with only its rig and
    /// poses (meshes and the Anim Bank are dropped, so the copy reads as a fresh character).
    /// </summary>
    private static Character Clone(SceneModel model, Character source, string name)
    {
        var copy = source.Source.CloneHierarchy();
        copy.Set("Name", name);
        // Keep __BindPoses: a character's RefPose may point into it, and removing it would leave
        // the reference dangling (the evaluator would then fall back to the joints' LTM).
        foreach (var child in copy.Children.ToList())
            if (child.Name is "__Anims" or "__RigAnim" || child.ClassKey == "CMesh")
                copy.RemoveChild(child);
        model.Scene.Source.AddChild(copy);
        var character = model.Scene.Characters.First(c => c.Name == name);
        character.CurrentAnimation = null;
        return character;
    }
}
