using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Retargeting between two characters. No scene
/// retargeted from a source scene that is on this machine is available, so the tests duplicate a
/// real character in memory, retarget a real animation onto the copy, and check the properties the
/// conversion implies plus save/reload.
/// </summary>
public sealed class RetargetingTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void The_default_map_pairs_controllers_by_name_and_can_strip_namespace_prefixes()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");

        // Same rig: every retargetable controller pairs (shoe locators and blendshape controllers do not).
        var map = RetargetingMap.Build(source, target);
        Assert.Equal(RetargetingMap.Candidates(source).Count, map.Pairs.Count);
        Assert.Empty(map.UnmatchedSources);
        Assert.Empty(map.UnmatchedTargets);
        Assert.DoesNotContain(map.Pairs, p => p.Source is ReverseFootLocator || p.Target is ReverseFootLocator);
        Assert.DoesNotContain(map.Pairs, p => p.Source.ClassName == "CBlendshapeController");

        // A namespace prefix on the target is stripped for a joint...
        target.Controllers.First(c => c.Name == "Pelvis").Name = "mixamorig:Pelvis";
        target.Controllers.First(c => c.Name == "Chest").Name = "Armature|Chest";
        // ...but not for an IK controller (the shipped Retargeting_AvoidCommonPrefixSearch).
        target.Controllers.First(c => c.Name == "IK_Hand_R").Name = "mixamorig:IK_Hand_R";

        map = RetargetingMap.Build(source, target);
        Assert.Contains(map.Pairs, p => p.Source.Name == "Pelvis" && p.Target.Name == "mixamorig:Pelvis");
        Assert.Contains(map.Pairs, p => p.Source.Name == "Chest" && p.Target.Name == "Armature|Chest");
        Assert.DoesNotContain(map.Pairs, p => p.Source.Name == "IK_Hand_R");
        Assert.Contains(map.UnmatchedSources, c => c.Name == "IK_Hand_R");
        Assert.DoesNotContain(map.Pairs, p => p.Target.Name == "mixamorig:IK_Hand_R");

        // An explicit pair overrides the automatic matching.
        var pelvis = source.Controllers.First(c => c.Name == "Pelvis");
        var head = target.Controllers.First(c => c.Name == "Head");
        var forced = RetargetingMap.Build(source, target, [new RetargetingPair(pelvis, head)]);
        Assert.Contains(forced.Pairs, p => p.Source == pelvis && p.Target == head);
    }

    [Fact]
    public void A_same_rig_retarget_reproduces_the_source_pose()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [run] });
        Assert.Single(result.Animations);
        Assert.Equal(1f, result.Ratio, 4);
        Assert.Equal(RetargetingMap.Candidates(source).Count, result.Map.Pairs.Count);

        var copy = result.Animations[0];
        Assert.Equal("Run_3", copy.Name);
        Assert.Equal(new[] { "Retargeted From Dup", "Retargeted From DupTheClown", "Common" }, copy.FolderPath);
        Assert.Equal(run.Stacks.Count, copy.Stacks.Count);
        Assert.Equal(run.Stacks.Sum(s => s.Keys.Count), copy.Stacks.Sum(s => s.Keys.Count));

        var before = new PoseEvaluator(source);
        var after = new PoseEvaluator(target);
        var pairs = result.Map.Pairs.Select(p => (From: before.IndexOf(p.Source), To: after.IndexOf(p.Target))).ToList();
        Assert.All(pairs, p => Assert.True(p.From >= 0 && p.To >= 0));
        foreach (int frame in new[] { 0, 1, 3, 7, 9, 15, 25, 40 })
        {
            var a = before.LocalTransforms(run, frame);
            var b = after.LocalTransforms(copy, frame);
            foreach (var (from, to) in pairs)
            {
                Assert.True(Vector3.Distance(a[from].Translation, b[to].Translation) < 1e-3f,
                    $"frame {frame}, {before.Controllers[from].Name}: translation {a[from].Translation} vs {b[to].Translation}");
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(a[from].Rotation, b[to].Rotation)) - 1f) < 1e-5f,
                    $"frame {frame}, {before.Controllers[from].Name}: rotation differs");
                Assert.True(Vector3.Distance(a[from].Scale, b[to].Scale) < 1e-4f,
                    $"frame {frame}, {before.Controllers[from].Name}: scale differs");
            }
        }
    }

    [Fact]
    public void The_stack_and_key_layout_is_copied()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        var copy = result.Animations[0];
        Assert.Equal("Retargeted From Dup", result.Destination.Name);

        for (int s = 0; s < run.Stacks.Count; s++)
        {
            var from = run.Stacks[s];
            var to = copy.Stacks[s];
            Assert.Equal(from.Name, to.Name);
            Assert.Equal(from.Flags, to.Flags);
            Assert.Equal(from.Opacity, to.Opacity);
            Assert.Equal(from.Keys, to.Keys);
            Assert.Equal(from.Tracks.Count(t => t.Target != null && result.Map.TargetOf(t.Target) != null), to.Tracks.Count());
        }

        // Every copied track targets a mapped controller or a functional-group object (the shoe
        // locators of a matched reverse foot), and an unmatched controller gets none.
        var mappedTargets = result.Map.Pairs.Concat(result.Map.FunctionalGroup)
            .Select(p => p.Target).ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.All(copy.Stacks.SelectMany(s => s.Tracks), t => Assert.Contains(t.Target!, mappedTargets));
        var functional = result.Map.FunctionalGroup.Select(p => p.Target).ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.All(copy.Stacks.SelectMany(s => s.Tracks).Where(t => t.Target is ReverseFootLocator),
            t => Assert.Contains(t.Target!, functional));
        Assert.Same(copy, target.CurrentAnimation);
    }

    [Fact]
    public void A_scaled_reference_pose_scales_the_translations_and_the_ratio()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        ScaleReferencePose(target, 2f);
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        Assert.Equal(2f, result.Ratio, 2);

        var before = new PoseEvaluator(source);
        var after = new PoseEvaluator(target);
        var copy = result.Animations[0];
        foreach (int frame in new[] { 0, 3, 9, 25 })
        {
            var a = before.LocalTransforms(run, frame);
            var b = after.LocalTransforms(copy, frame);
            foreach (var pair in result.Map.Pairs)
            {
                int from = before.IndexOf(pair.Source), to = after.IndexOf(pair.Target);
                Assert.True(Vector3.Distance(a[from].Translation * 2f, b[to].Translation) < 2e-2f,
                    $"frame {frame}, {pair.Source.Name}: expected twice the source translation");
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(a[from].Rotation, b[to].Rotation)) - 1f) < 1e-5f,
                    $"frame {frame}, {pair.Source.Name}: rotation must not scale");
            }
        }
    }

    [Fact]
    public void A_different_reference_pose_keeps_each_controllers_local_delta()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        RotateReferencePose(target, "Thigh_R", Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.35f));
        var run = source.Animations.First(a => a.Name == "Run_3");

        // The Local method on purpose: Auto picks World when the reference poses differ.
        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run], Method = RetargetMethod.Local });
        var copy = result.Animations[0];
        var before = new PoseEvaluator(source) { SolveIK = false };
        var after = new PoseEvaluator(target) { SolveIK = false };
        foreach (int frame in new[] { 0, 5, 15 })
        {
            var sourceLocal = before.LocalTransforms(run, frame);
            var targetLocal = after.LocalTransforms(copy, frame);
            foreach (var pair in result.Map.Pairs)
            {
                int from = before.IndexOf(pair.Source), to = after.IndexOf(pair.Target);
                var sourceDelta = PoseEvaluator.Decompose(before.RestTransforms[from], sourceLocal[from]);
                var targetDelta = PoseEvaluator.Decompose(after.RestTransforms[to], targetLocal[to]);
                Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(sourceDelta.Rotation, targetDelta.Rotation)) - 1f) < 1e-5f,
                    $"frame {frame}, {pair.Source.Name}: the local rotation delta must be kept");
                float ratio = result.Ratio;
                Assert.True(Vector3.Distance(sourceDelta.Translation * ratio, targetDelta.Translation) < 1e-3f,
                    $"frame {frame}, {pair.Source.Name}: local translation delta {sourceDelta.Translation * ratio} vs {targetDelta.Translation}");
            }
        }
    }

    [Fact]
    public void IK_and_reverse_foot_keys_keep_their_flags_and_ref_arrays()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        var copy = result.Animations[0];
        int checkedKeys = 0;
        foreach (var sourceTrack in run.Stacks.SelectMany(s => s.Tracks))
        {
            if (sourceTrack.Target is not ControllerIK controller) continue;
            var targetController = result.Map.TargetOf(controller) as ControllerIK;
            if (targetController == null) continue;
            var targetTrack = copy.Stacks
                .First(s => s.Name == sourceTrack.Stack!.Name)
                .Tracks.First(t => t.Target == targetController);
            for (int k = 0; k < sourceTrack.Keys.Count; k++)
            {
                if (sourceTrack.Keys[k] is not IKKey sourceKey) continue;
                var targetKey = Assert.IsAssignableFrom<IKKey>(targetTrack.Keys[k]);
                Assert.Equal(sourceKey.Flags, targetKey.Flags);
                Assert.Equal(sourceKey.PoleAngle, targetKey.PoleAngle);
                int expected = controller.Chain.Count + (controller is ControllerReverseFoot ? 1 : 0);
                Assert.Equal(expected, targetKey.ChainTransforms.Count);
                Assert.Same(targetController, targetKey.Controller);
                checkedKeys++;
            }
        }
        Assert.True(checkedKeys > 0, "Run_3 must have IK or reverse-foot keys");
    }

    [Fact]
    public void The_loop_key_replays_key_zero()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        var copy = result.Animations[0];
        float ratio = result.Ratio;
        foreach (var sourceStack in run.Stacks.Where(s => s.IsCycle && s.Keys.Count > 2))
        {
            var targetStack = copy.Stacks.First(s => s.Name == sourceStack.Name);
            foreach (var sourceTrack in sourceStack.Tracks)
            {
                if (sourceTrack.Target == null || result.Map.TargetOf(sourceTrack.Target) == null) continue;
                var targetTrack = targetStack.Tracks.First(t => t.Target == result.Map.TargetOf(sourceTrack.Target));
                // Every value is the source's, with translations scaled.
                for (int k = 0; k < sourceTrack.Keys.Count; k++)
                {
                    if (sourceTrack.Keys[k] is not TransformKey from || targetTrack.Keys[k] is not TransformKey to) continue;
                    Assert.True(Vector3.Distance(from.Value.Translation * ratio, to.Value.Translation) < 1e-4f,
                        $"{sourceTrack.Target.Name}, key {k}: translation");
                    Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(from.Value.Rotation, to.Value.Rotation)) - 1f) < 1e-5f,
                        $"{sourceTrack.Target.Name}, key {k}: rotation");
                }
                // Where the source's loop key replays key 0, so does the copy's.
                if (sourceTrack.Keys[0] is TransformKey sourceFirst && sourceTrack.Keys[^1] is TransformKey sourceLast
                    && Vector3.Distance(sourceFirst.Value.Translation, sourceLast.Value.Translation) < 1e-4f
                    && targetTrack.Keys[0] is TransformKey targetFirst && targetTrack.Keys[^1] is TransformKey targetLast)
                    Assert.True(Vector3.Distance(targetFirst.Value.Translation, targetLast.Value.Translation) < 1e-4f,
                        $"{sourceTrack.Target.Name}: the loop key must replay key 0");
            }
        }
    }

    [Fact]
    public void A_retargeted_scene_saves_and_reloads_and_round_trips()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var run = source.Animations.First(a => a.Name == "Run_3");

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [run] });
        int frame = 9;
        var after = new PoseEvaluator(target);
        var before = after.LocalTransforms(result.Animations[0], frame);

        string saved = TempName(".akt");
        model.Save(saved);
        var reloaded = TestFiles.LoadScene(saved);
        var reloadedTarget = reloaded.Scene.Characters.First(c => c.Name == "Copy");
        var reloadedAnimation = reloadedTarget.Animations.First(a => a.Name == "Run_3" && a.FolderPath.Contains("Retargeted From Dup"));
        var afterReload = new PoseEvaluator(reloadedTarget).LocalTransforms(reloadedAnimation, frame);
        Assert.Equal(before.Length, afterReload.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.True(Vector3.Distance(before[i].Translation, afterReload[i].Translation) < 1e-4f,
                $"controller {i} {target.Controllers[i].Name}: {before[i].Translation} vs {afterReload[i].Translation}");
            Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(before[i].Rotation, afterReload[i].Rotation)) - 1f) < 1e-5f,
                $"controller {i} {target.Controllers[i].Name}: rotation differs after reload");
        }

        string again = TempName(".akt");
        reloaded.Save(again);
        var first = File.ReadAllBytes(saved);
        var second = File.ReadAllBytes(again);
        if (!first.SequenceEqual(second))
        {
            int at = 0;
            while (at < Math.Min(first.Length, second.Length) && first[at] == second[at]) at++;
            Assert.Fail($"round trip differs at byte {at}: {first.Length} vs {second.Length} bytes");
        }
    }

    /// <summary>
    /// The ground truth this feature could not find at first: `1_InitialImport` holds the source
    /// characters (`z_DupTheClown`, `z_Citro_Knight`) *and* the retarget of
    /// `Common/Idle` from the former onto the latter (`Retargeted From DupTheClown/Common/Idle`).
    /// The two rigs have the same reference pose, so the comparison exercises the whole transfer
    /// (values, tangents, IK flags and RefArrays, the shoe-locator functional-group tracks); it is
    /// the same-rig case of the different-rig branch.
    /// </summary>
    [Fact]
    public void The_result_matches_the_reference_retarget_when_the_reference_poses_agree()
    {
        if (Load("1_InitialImport.akt") is not { } model) return;
        var source = model.Scene.Characters.First(c => c.Name == "z_DupTheClown");
        var target = model.Scene.Characters.First(c => c.Name == "z_Citro_Knight");
        var original = source.Animations.First(a => a.Name == "Idle" && a.FolderPath.SequenceEqual(["Common"]));
        var expected = target.Animations.First(a => a.Name == "Idle"
            && a.FolderPath.SequenceEqual(["Retargeted From DupTheClown", "Common"]));

        // The plain per-key reading alone: SloppyAnimatsu's locked-key solve deliberately departs from it.
        var result = Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [original], SolveLockedKeys = false });
        var copy = result.Animations[0];
        Assert.Equal(expected.Stacks[0].Keys, copy.Stacks[0].Keys);
        Assert.Equal(expected.Stacks[0].Flags, copy.Stacks[0].Flags);

        Assert.Equal(expected.Stacks[0].Tracks.Count(), copy.Stacks[0].Tracks.Count());
        int compared = 0, missing = 0;
        float maxT = 0, maxR = 0, maxS = 0, maxChainT = 0, maxChainR = 0, maxTanT = 0, maxTanR = 0;
        foreach (var expectedTrack in expected.Stacks[0].Tracks)
        {
            var copyTrack = copy.Stacks[0].Tracks.FirstOrDefault(t => t.Target == expectedTrack.Target);
            if (copyTrack == null) { missing++; continue; }
            for (int k = 0; k < expected.Stacks[0].Keys.Count; k++)
            {
                if (expectedTrack.Keys[k] is not TransformKey a || copyTrack.Keys[k] is not TransformKey b) continue;
                maxT = Math.Max(maxT, Vector3.Distance(a.Value.Translation, b.Value.Translation));
                maxR = Math.Max(maxR, Angle(a.Value.Rotation, b.Value.Rotation));
                maxS = Math.Max(maxS, Vector3.Distance(a.Value.Scale, b.Value.Scale));
                if (a is IKKey expectedIk && b is IKKey copyIk)
                {
                    Assert.Equal(expectedIk.Flags, copyIk.Flags);
                    Assert.Equal(expectedIk.PoleAngle, copyIk.PoleAngle, 3);
                    var expectedChain = expectedIk.ChainTransforms;
                    var copyChain = copyIk.ChainTransforms;
                    Assert.Equal(expectedChain.Count, copyChain.Count);
                    for (int j = 0; j < expectedChain.Count; j++)
                    {
                        maxChainT = Math.Max(maxChainT, Vector3.Distance(expectedChain[j].Translation, copyChain[j].Translation));
                        maxChainR = Math.Max(maxChainR, Angle(expectedChain[j].Rotation, copyChain[j].Rotation));
                    }
                }
                compared++;
            }
            foreach (var (expectedTangents, copyTangents) in new[]
                     { (expectedTrack.TangentsIn, copyTrack.TangentsIn), (expectedTrack.TangentsOut, copyTrack.TangentsOut) })
                for (int k = 0; k < Math.Min(expectedTangents.Count, copyTangents.Count); k++)
                {
                    if (expectedTangents[k] is not TransformKey ta || copyTangents[k] is not TransformKey tb) continue;
                    maxTanT = Math.Max(maxTanT, Vector3.Distance(ta.Value.Translation, tb.Value.Translation));
                    // A tangent key's rotation is a slope vector with w = 0, not a unit quaternion.
                    var qa = ta.Value.Rotation;
                    var qb = tb.Value.Rotation;
                    maxTanR = Math.Max(maxTanR, MathF.Max(
                        MathF.Max(MathF.Abs(qa.X - qb.X), MathF.Abs(qa.Y - qb.Y)),
                        MathF.Max(MathF.Abs(qa.Z - qb.Z), MathF.Abs(qa.W - qb.W))));
                }
        }

        Assert.Equal(0, missing);
        Assert.True(compared > 400, "every key of every track must be compared");
        Assert.True(maxT < 1e-2f, $"translations differ by up to {maxT}");
        Assert.True(maxR < 0.1f, $"rotations differ by up to {maxR} degrees");
        Assert.True(maxS < 1e-4f, $"scales differ by up to {maxS}");
        Assert.True(maxChainT < 1e-2f, $"RefArray translations differ by up to {maxChainT}");
        Assert.True(maxChainR < 0.1f, $"RefArray rotations differ by up to {maxChainR} degrees");
        Assert.True(maxTanT < 1e-2f, $"tangent translations differ by up to {maxTanT}");
        Assert.True(maxTanR < 1e-2f, $"tangent rotation components differ by up to {maxTanR}");
    }

    /// <summary>
    /// The stored matching: retargeting keeps
    /// The mapping in the scene, one <c>CRetargetingMatching</c> per character, with the shape
    /// - the owner's candidates in <c>Sources</c> order, the chosen targets compact in
    /// <c>Targets</c> (a "none" row adds none), and those rows' indices in <c>NullIndices</c> - mirrored.
    /// </summary>
    [Fact]
    public void The_stored_matching_has_the_akt_shape_and_is_mirrored_on_the_target()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var candidates = RetargetingMap.Candidates(source);
        var automatic = RetargetingMap.Build(source, target);
        var dropped = candidates[0];
        var droppedTarget = automatic.Pairs.First(pair => ReferenceEquals(pair.Source, dropped)).Target;

        Retargeting.Transfer(model, source, target, new RetargetingOptions
        {
            Animations = [source.Animations.First()],
            Pairs = automatic.Pairs.Where(pair => !ReferenceEquals(pair.Source, dropped)).ToList(),
            NoneSources = [dropped],
        });

        var matching = RetargetingMatching.Find(source, target);
        Assert.NotNull(matching);
        Assert.Equal("Default name", matching!.Name);
        Assert.Same(target, matching.TargetCharacter);
        Assert.Equal(candidates.Select(candidate => candidate.Name), matching.Sources.Select(o => o!.Name));
        // The app stores the targets compactly: a "none" row adds only its NullIndices entry.
        Assert.Equal(candidates.Count - 1, matching.Targets.Count);
        Assert.Equal([0u], matching.NullIndices);
        Assert.Equal(automatic.Pairs.Count - 1, matching.Matches().Count(row => row.Target != null));

        // The mirrored matching on the target: its own candidates as sources, the reverse pairs.
        var mirror = RetargetingMatching.Find(target, source);
        Assert.NotNull(mirror);
        Assert.Equal("Default name_02", mirror!.Name);
        Assert.Same(source, mirror.TargetCharacter);
        var targetCandidates = RetargetingMap.Candidates(target);
        Assert.Equal(targetCandidates.Select(candidate => candidate.Name), mirror.Sources.Select(o => o!.Name));
        Assert.Equal(targetCandidates.Count - 1, mirror.Targets.Count);
        var mirrorSources = mirror.Sources.ToList();
        int index = mirrorSources.FindIndex(o => ReferenceEquals(o, droppedTarget));
        Assert.True(index >= 0);
        Assert.Contains((uint)index, mirror.NullIndices);
        var kept = automatic.Pairs.First(pair => !ReferenceEquals(pair.Source, dropped));
        Assert.Same(kept.Target, mirror.Matches().First(row => ReferenceEquals(row.Target, kept.Source)).Source);
    }

    /// <summary>
    /// The grid survives a save: a scene that never declared <c>CRetargetingMatching</c> gets the
    /// class in its schema, the mapping as an ordinary child on each character, and the dialog reads
    /// the same rows back after a reload.
    /// </summary>
    [Fact]
    public void A_saved_scene_reopens_the_grid_the_dialog_had()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        var session = new RetargetSession(model) { Source = source, Target = target };

        Assert.True(session.SetTarget(session.Rows[0].Source, null));
        var chosen = session.Rows[2].Target;
        Assert.NotNull(chosen);
        Assert.True(session.SetTarget(session.Rows[1].Source, chosen));
        var rows = session.Rows.ToList();
        var map = session.Map!;

        Retargeting.Transfer(model, source, target, new RetargetingOptions
        {
            Animations = [source.Animations.First()],
            Pairs = map.Pairs,
            NoneSources = map.UnmatchedSources,
        });

        string path = TempName(".akt");
        model.Save(path);
        var reloaded = SceneModel.Load(path);
        Assert.NotNull(reloaded.Document.Schema.FindClass("CRetargetingMatching"));
        var reloadedSource = reloaded.Scene.Characters.First(c => c.Name == source.Name);
        var reloadedTarget = reloaded.Scene.Characters.First(c => c.Name == target.Name);
        var reopened = new RetargetSession(reloaded) { Source = reloadedSource, Target = reloadedTarget };

        Assert.Equal(rows.Select(row => row.Source.Name), reopened.Rows.Select(row => row.Source.Name));
        Assert.Equal(rows.Select(row => row.Target?.Name), reopened.Rows.Select(row => row.Target?.Name));
        var stored = RetargetingMatching.Find(reloadedSource, reloadedTarget);
        Assert.NotNull(stored);
        Assert.Equal(rows.Count(row => row.Target == null), stored!.NullIndices.Count);
        Assert.Equal(rows.Count, stored.Sources.Count);
    }

    /// <summary>
    /// <c>1_InitialImport.akt</c> is the evidence for the stored object: two
    /// <c>CRetargetingMatching</c> children, one per character, 58 entries each (every retargetable
    /// controller of the owner), empty <c>NullIndices</c>, and they round-trip byte-exactly.
    /// </summary>
    [Fact]
    public void The_two_stored_matchings_of_1_InitialImport_are_read()
    {
        if (TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == "1_InitialImport.akt") is not { } path)
            return;
        byte[] original = File.ReadAllBytes(path);
        var document = AktReader.Read(original);
        var model = new SceneModel(document);
        var citro = model.Scene.Characters.First(c => c.Name == "z_Citro_Knight");
        var dup = model.Scene.Characters.First(c => c.Name == "z_DupTheClown");

        var fromDup = RetargetingMatching.Find(dup, citro);
        var fromCitro = RetargetingMatching.Find(citro, dup);
        Assert.NotNull(fromDup);
        Assert.NotNull(fromCitro);
        Assert.Equal("Default name", fromDup!.Name);
        Assert.Equal("Default name_02", fromCitro!.Name);

        foreach (var (matching, owner, other) in new[]
                 { (fromDup, dup, citro), (fromCitro, citro, dup) })
        {
            Assert.Same(other, matching.TargetCharacter);
            Assert.Equal(58, matching.Sources.Count);
            Assert.Equal(58, matching.Targets.Count);
            Assert.Empty(matching.NullIndices);
            var candidates = RetargetingMap.Candidates(owner);
            Assert.Equal(candidates.Select(candidate => candidate.Name), matching.Sources.Select(o => o!.Name));
            // The app paired the two lists by index, and both characters are the same rig, so every
            // target is the counterpart of its source and belongs to the other character.
            Assert.Equal(matching.Sources.Select(o => o!.Name), matching.Targets.Select(o => o!.Name));
            var otherCandidates = new HashSet<Object3D>(RetargetingMap.Candidates(other), ReferenceEqualityComparer.Instance);
            Assert.All(matching.Targets, target => Assert.True(target != null && otherCandidates.Contains(target)));
            Assert.Equal(matching.Sources.Count, matching.Matches().Count(row => row.Target != null));
        }

        // The schema the class needs is in the file, with the member spellings we synthesize.
        var def = document.Schema.FindClass("CRetargetingMatching");
        Assert.NotNull(def);
        Assert.Equal(
            new[]
            {
                ("TargetCharacter", "Reference<class CCharacter>"),
                ("Sources", "RefArray<class CDataObject>"),
                ("Targets", "RefArray<class CDataObject>"),
                ("NullIndices", "vector<uint>"),
                ("IsLastUsedRetargetingMatching", "bool"),
            },
            def!.Members.Select(member => (member.Name, member.TypeName)));

        byte[] written = AktWriter.Write(document);
        Assert.True(original.AsSpan().SequenceEqual(written),
            $"the scene with its two matchings no longer round-trips (first difference at 0x{original.AsSpan().CommonPrefixLength(written):X})");
    }

    /// <summary>
    /// The per-key reading switch: when the
    /// two reference poses agree the two readings are the same value, and that value is the source
    /// key's own delta; rotate the target's reference pose and they part - <c>local</c> keeps the
    /// node's local rotation delta, <c>world</c> conjugates it by the two nodes' reference-pose world
    /// rotations.
    /// </summary>
    [Fact]
    public void The_method_switch_conjugates_the_delta_only_when_the_reference_poses_differ()
    {
        var local = TransferOne(RetargetMethod.Local, null);
        var world = TransferOne(RetargetMethod.World, null);
        var reference = TransferOne(RetargetMethod.Reference, null);
        Assert.Equal(0f, Angle(local.Source, local.Converted), 3);
        Assert.Equal(0f, Angle(world.Source, world.Converted), 3);
        Assert.Equal(0f, Angle(reference.Source, reference.Converted), 3);
        // With the reference poses agreeing every reading is the local one - bit for bit.
        Assert.Equal(0f, Angle(local.Converted, world.Converted), 6);
        Assert.Equal(0f, Angle(local.Converted, reference.Converted), 6);
        Assert.Equal(0f, Vector3.Distance(local.ConvertedT, reference.ConvertedT), 6);

        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        var rotatedLocal = TransferOne(RetargetMethod.Local, rotation);
        var rotatedWorld = TransferOne(RetargetMethod.World, rotation);
        var rotatedReference = TransferOne(RetargetMethod.Reference, rotation);
        Assert.Equal(0f, Angle(rotatedLocal.Source, rotatedLocal.Converted), 3);
        // q_world = Qtarget * inverse(Qsource); the world reading conjugates the delta by it and the
        // local one does not. The reference reading applies the same conjugation to the rotation...
        Assert.True(Angle(rotatedLocal.Converted, rotatedWorld.Converted) > 5f,
            "a rotated reference pose must separate the two readings");
        Assert.Equal(0f, Angle(rotatedWorld.Expected, rotatedWorld.Converted), 2);
        Assert.Equal(0f, Angle(rotatedWorld.Expected, rotatedReference.Converted), 2);
        Assert.True(Angle(rotatedLocal.Converted, rotatedReference.Converted) > 5f,
            "the reference reading must turn the rotation like the world one");
        // ... but leaves the translation delta in the source's frame, scaled by the ratio alone
        Assert.Equal(0f, Vector3.Distance(rotatedReference.SourceT * rotatedReference.Ratio, rotatedReference.ConvertedT), 3);
        Assert.True(Vector3.Distance(rotatedLocal.ConvertedT, rotatedReference.ConvertedT) < 1e-3f,
            "the reference reading must not turn the translation");
    }

    /// <summary>
    /// The reference reading's scale channel: the app multiplies the key's scale by the two nodes'
    /// reference-pose world scale ratio, normalized so that the product is preserved (the cube-root
    /// guard of <c></c>). The expectation is computed from the rule, not a stored value.
    /// </summary>
    [Fact]
    public void The_reference_reading_applies_a_volume_preserving_scale_from_the_two_reference_poses()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        ScaleReferencePoseAxes(target, "Chest", new Vector3(2f, 1.3f, 0.5f));

        var animation = source.Animations.First(a => a.Name == "Run_3");
        var local = Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [animation], Method = RetargetMethod.Local });
        var copyLocal = ChestTrack(local);
        var reference = Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [animation], Method = RetargetMethod.Reference });
        var copyReference = ChestTrack(reference);
        var copyAutomatic = ChestTrack(Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [animation], Method = RetargetMethod.Auto }));
        var copyWorld = ChestTrack(Retargeting.Transfer(model, source, target,
            new RetargetingOptions { Animations = [animation], Method = RetargetMethod.World }));

        var sourceEvaluator = new PoseEvaluator(source);
        var targetEvaluator = new PoseEvaluator(target);
        int from = sourceEvaluator.IndexOf(source.Controllers.First(c => c.Name == "Chest"));
        int to = targetEvaluator.IndexOf(target.Controllers.First(c => c.Name == "Chest"));
        var sourceWorld = sourceEvaluator.WorldMatrices(null, 0)[from];
        var targetWorld = targetEvaluator.WorldMatrices(null, 0)[to];
        Assert.True(Matrix4x4.Invert(targetWorld, out var inverseTarget));
        var factor = Object3D.LocalScale(sourceWorld * inverseTarget);
        factor /= MathF.Cbrt(factor.X * factor.Y * factor.Z);
        Assert.True(Vector3.Distance(factor, Vector3.One) > 1e-2f, "the reference poses must have a non-uniform ratio");

        int compared = 0;
        for (int k = 0; k < Math.Min(copyReference.Keys.Count, copyLocal.Keys.Count); k++)
        {
            if (copyReference.Keys[k] is not TransformKey referenceKey || copyLocal.Keys[k] is not TransformKey localKey) continue;
            Assert.True(Vector3.Distance(referenceKey.Value.Scale, localKey.Value.Scale * factor) < 1e-3f,
                $"key {k}: scale {referenceKey.Value.Scale} vs the decoded {localKey.Value.Scale * factor}");
            float expectedProduct = localKey.Value.Scale.X * localKey.Value.Scale.Y * localKey.Value.Scale.Z;
            float product = referenceKey.Value.Scale.X * referenceKey.Value.Scale.Y * referenceKey.Value.Scale.Z;
            Assert.True(MathF.Abs(product - expectedProduct) < 1e-4f,
                $"key {k}: the scale guard must preserve volume ({product} vs {expectedProduct})");
            if (copyAutomatic.Keys[k] is TransformKey automaticKey && copyWorld.Keys[k] is TransformKey worldKey)
            {
                Assert.True(Vector3.Distance(automaticKey.Value.Scale, worldKey.Value.Scale) < 1e-6f
                    && Angle(automaticKey.Value.Rotation, worldKey.Value.Rotation) < 1e-3f,
                    $"key {k}: Auto must pick the world reading when the reference poses differ");
            }
            compared++;
        }
        Assert.True(compared > 0);
    }

    /// <summary>
    /// End to end between two different rigs (the user's DupTheClown_OLD and Dup: joint frames up to
    /// 180 deg apart, and an extra animated spine joint, BustN, that Dup has no counterpart for).
    /// The default transfer must give every matched target joint the same world rotation change as
    /// its source, at keys and between them. IK re-solving is off: inside IK override segments the
    /// target legitimately re-solves its own limbs.
    /// </summary>
    [Fact]
    public void A_transfer_between_different_rigs_keeps_every_joints_world_rotation_change()
    {
        if (Load("25_retargetRedo.akt") is not { } model) return;
        var characters = model.Scene.Descendants<Character>().ToList();
        if (characters.FirstOrDefault(c => c.Name == "DupTheClown_OLD") is not { } source
            || characters.FirstOrDefault(c => c.Name == "Dup") is not { } target) return;
        var rows = RetargetingMatching.Find(source, target)!.Matches().Where(r => r.Target != null).ToList();
        var animation = source.Animations.First(a => a.Name == "Dash");
        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions
        {
            Animations = [animation],
            Pairs = rows.GroupBy(r => r.Source).Select(g => new RetargetingPair(g.Key, g.First().Target!)).ToList(),
            ExtraPairs = rows.GroupBy(r => r.Source).SelectMany(g => g.Skip(1)).Select(r => new RetargetingPair(r.Source, r.Target!)).ToList(),
            // The FK conversion alone: SolveLockedKeys re-keys locked chains to the target's own
            // solve by design (Solving_locked_keys_removes_the_key_frame_jumps_of_a_cross_rig_retarget).
            SolveLockedKeys = false,
        });
        var copy = result.Animations[0];

        var sourceEvaluator = new PoseEvaluator(source) { SolveIK = false };
        var targetEvaluator = new PoseEvaluator(target) { SolveIK = false };
        var sourceRest = sourceEvaluator.WorldMatrices(null, 0);
        var targetRest = targetEvaluator.WorldMatrices(null, 0);
        static Quaternion Rotation(Matrix4x4 m)
        {
            Matrix4x4.Decompose(m, out _, out var q, out _);
            return Quaternion.Normalize(q);
        }
        float worst = 0f;
        string worstAt = "";
        for (float time = animation.TimeRange.Start; time <= animation.TimeRange.End; time += 1.5f)
        {
            var sourceWorld = sourceEvaluator.WorldMatrices(animation, time);
            var targetWorld = targetEvaluator.WorldMatrices(copy, time);
            foreach (var row in rows)
            {
                int si = sourceEvaluator.IndexOf(row.Source), ti = targetEvaluator.IndexOf(row.Target!);
                if (si < 0 || ti < 0) continue;
                var sourceDelta = Rotation(sourceWorld[si]) * Quaternion.Inverse(Rotation(sourceRest[si]));
                var targetDelta = Rotation(targetWorld[ti]) * Quaternion.Inverse(Rotation(targetRest[ti]));
                float error = Angle(sourceDelta, targetDelta);
                if (error > worst) (worst, worstAt) = (error, $"{row.Source.Name} at {time}");
            }
        }
        Assert.True(worst < 0.5f, $"worst world rotation change mismatch {worst:0.00} deg ({worstAt})");
    }

    /// <summary>
    /// The user's report: in <c>26_retargetRedo.akt</c>, Dup's retarget of
    /// Citro_Knight_OLD's <c>WpnCitroCannon/SpecialAirLand</c> drops the feet into the ground on every
    /// key (0, 5, 17, 26) while the frames between are right. <see cref="KeyEditor.SolveLockedKeys"/>
    /// (what a cross-rig transfer now ends with) must bring each locked key's chain end to where
    /// playback has it just after the key
    /// Measured 23.3 units before the fix, 0.011 after.
    /// </summary>
    [Fact]
    public void Solving_locked_keys_removes_the_key_frame_jumps_of_a_cross_rig_retarget()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "Dup") is not { } dup) return;
        var animation = dup.Animations.FirstOrDefault(a => a.Name == "SpecialAirLand"
            && a.FolderPath.Any(f => f.StartsWith("Retargeted From Citro_Knight"))
            && a.FolderPath.Contains("WpnCitroCannon"));
        if (animation == null) return;

        float before = WorstLockedKeyJump(dup, animation);
        var editor = new KeyEditor(dup);
        foreach (var stack in animation.Stacks) editor.SolveLockedKeys(stack);
        float after = WorstLockedKeyJump(dup, animation);
        Assert.True(after < 0.1f, $"worst jump at a locked key {after:0.###} (was {before:0.###})");
    }

    /// <summary>
    /// Open question 42: Dup's retarget of DupTheClown_OLD's <c>AttackDashStrong(Cartwheel)</c> kept a
    /// 94-unit jump at IK_Hand_R after solving. Its additive <c>TransN</c> stack (listed first) keys
    /// arm rotations; writing the played pose into <c>All</c> with a plain <c>SetKey</c> counted that
    /// delta twice. Keying the <em>fully mixed</em> value removes it.
    /// </summary>
    [Fact]
    public void Solving_locked_keys_counts_an_additive_stack_above_once()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "Dup") is not { } dup) return;
        var animation = dup.Animations.FirstOrDefault(a => a.Name == "AttackDashStrong(Cartwheel)"
            && a.FolderPath.Any(f => f == "Retargeted From DupTheClown_OLD"));
        if (animation == null) return;

        float before = WorstLockedKeyJump(dup, animation);
        var editor = new KeyEditor(dup);
        foreach (var stack in animation.Stacks) editor.SolveLockedKeys(stack);
        float after = WorstLockedKeyJump(dup, animation);
        Assert.True(after < 3f, $"worst jump at a locked key {after:0.###} (was {before:0.###})");
    }

    /// <summary>
    /// Solving must not damage an animation that already plays smoothly: Citro_Knight_OLD's own
    /// <c>WpnCitroCannon/SpecialAirLand</c> (0.05 units) went to 25 units when its already-matching
    /// foot keys were rewritten on that rig, whose joints do not compensate their parent's scale.
    /// A key whose jump is already settled is left as it is.
    /// </summary>
    [Fact]
    public void Solving_locked_keys_leaves_a_smooth_animation_as_it_plays()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "Citro_Knight_OLD") is not { } citro) return;
        var animation = citro.Animations.FirstOrDefault(a => a.Name == "SpecialAirLand" && a.FolderPath.Contains("WpnCitroCannon"));
        if (animation == null) return;

        float before = WorstLockedKeyJump(citro, animation);
        var editor = new KeyEditor(citro);
        foreach (var stack in animation.Stacks) editor.SolveLockedKeys(stack);
        float after = WorstLockedKeyJump(citro, animation);
        Assert.True(after <= before + 0.05f, $"worst jump at a locked key {after:0.###} (was {before:0.###})");
    }

    /// <summary>
    /// A locked IK key with no override segment beside it plays FK on both sides, so it cannot jump,
    /// but the IK key means the chain reaches its controller there. Dup's retarget of
    /// Citro_Knight_OLD's <c>Sentai_Style/SpecialAirLand</c> had such a hand key at frame 35 missing by
    /// 60 units; solving now reaches it.
    /// </summary>
    [Fact]
    public void Solving_locked_keys_reaches_the_controller_at_a_lone_locked_key()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "Dup") is not { } dup) return;
        var animation = dup.Animations.FirstOrDefault(a => a.Name == "SpecialAirLand"
            && a.FolderPath.Any(f => f.StartsWith("Retargeted From Citro_Knight"))
            && a.FolderPath.Contains("Sentai_Style"));
        if (animation == null) return;

        float before = WorstOwnStackMiss(dup, animation);
        var editor = new KeyEditor(dup);
        foreach (var stack in animation.Stacks) editor.SolveLockedKeys(stack);
        float after = WorstOwnStackMiss(dup, animation);
        Assert.True(after < 1f, $"worst end-to-controller miss at a locked key {after:0.###} (was {before:0.###})");
    }

    /// <summary>
    /// Open question 42: an IK chain answers only to the stacks it is on. DupTheClown_OLD's
    /// <c>Common/Walk</c> keeps its root motion on a separate <c>TransN</c> stack, so in the full mix
    /// the hand sits 57 units from its controller - the root motion, not a miss. Solving the lone
    /// locked keys in the full mix pulled the chain toward the controller without that offset; they
    /// are solved in the IK stack's own world, where they reach, and the full-mix offset stays.
    /// </summary>
    [Fact]
    public void Solving_locked_keys_ignores_a_root_motion_stack()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "DupTheClown_OLD") is not { } clown) return;
        var animation = clown.Animations.FirstOrDefault(a => a.Name == "Walk" && a.FolderPath.Contains("Common"));
        if (animation == null || !animation.Stacks.Any(s => s.Name == "TransN")) return;

        float before = WorstOwnStackMiss(clown, animation);
        var editor = new KeyEditor(clown);
        foreach (var stack in animation.Stacks) editor.SolveLockedKeys(stack);
        float after = WorstOwnStackMiss(clown, animation);
        Assert.True(after < 1f, $"worst own-stack miss at a locked key {after:0.###} (was {before:0.###})");
        Assert.True(after <= before + 0.05f, $"solving moved a chain that already reached: {after:0.###} (was {before:0.###})");
    }

    /// <summary>
    /// The IK controllers are drawn where the chain is, through the TransN root motion: in
    /// DupTheClown_OLD's <c>Common/Walk</c> the controllers sit under the character, so the mixed
    /// pose leaves them behind the hands by the root motion, while
    /// <see cref="PoseEvaluator.DisplayWorldMatrices"/> carries each one along by its chain's leaf
    /// joint.
    /// </summary>
    [Fact]
    public void Display_worlds_carry_ik_controllers_through_a_root_motion_stack()
    {
        if (Load("26_retargetRedo.akt") is not { } model) return;
        if (model.Scene.Descendants<Character>().FirstOrDefault(c => c.Name == "DupTheClown_OLD") is not { } clown) return;
        var animation = clown.Animations.FirstOrDefault(a => a.Name == "Walk" && a.FolderPath.Contains("Common"));
        if (animation == null || !animation.Stacks.Any(s => s.Name == "TransN")) return;

        var evaluator = new PoseEvaluator(clown);
        float worstMixed = 0f, worstDisplay = 0f;
        int checkedKeys = 0;
        foreach (var stack in animation.Stacks.Where(s => s.IsEnabled))
            foreach (var track in stack.Tracks)
            {
                if (track.Target is not ControllerIK ik || ik is ControllerReverseFoot) continue;
                int ci = evaluator.IndexOf(ik), li = evaluator.IndexOf(ik.Chain[^1]);
                for (int k = 0; k < Math.Max(1, stack.Keys.Count - 1) && k < track.Keys.Count; k++)
                {
                    if (track.Keys[k] is not IKKey key || (key.Flags & 1) == 0) continue;
                    float t = stack.Keys[k].Time;
                    if (evaluator.OwnStackPositions(animation, ik, t, ik.Chain[^1], ik) is not { } p) continue;
                    float own = Vector3.Distance(p[0], p[1]);
                    var mixed = evaluator.WorldMatrices(animation, t);
                    var display = evaluator.DisplayWorldMatrices(animation, t);
                    worstMixed = MathF.Max(worstMixed, Vector3.Distance(mixed[ci].Translation, mixed[li].Translation) - own);
                    worstDisplay = MathF.Max(worstDisplay, MathF.Abs(Vector3.Distance(display[ci].Translation, display[li].Translation) - own));
                    Assert.Equal(mixed[li].Translation, display[li].Translation);
                    checkedKeys++;
                }
            }
        Assert.True(checkedKeys > 0, "no locked IK key to check");
        Assert.True(worstMixed > 5f, $"the root motion no longer separates the mixed controller ({worstMixed:0.###})");
        Assert.True(worstDisplay < 0.5f, $"a displayed controller is {worstDisplay:0.###} off its own-stack offset from the hand");
    }

    /// <summary>
    /// The largest distance between an IK chain's end and its controller at a locked key, in the
    /// single-stack world of the stack that holds the IK track (<see cref="PoseEvaluator.OwnStackPositions"/>):
    /// what the chain answers to, without what other stacks (a root-motion layer) add.
    /// </summary>
    private static float WorstOwnStackMiss(Character character, Animation animation)
    {
        var evaluator = new PoseEvaluator(character);
        float worst = 0f;
        foreach (var stack in animation.Stacks.Where(s => s.IsEnabled))
            foreach (var track in stack.Tracks)
            {
                if (track.Target is not ControllerIK ik || ik is ControllerReverseFoot) continue;
                for (int k = 0; k < Math.Max(1, stack.Keys.Count - 1) && k < track.Keys.Count; k++)
                {
                    if (track.Keys[k] is not IKKey key || (key.Flags & 1) == 0) continue;
                    if (evaluator.OwnStackPositions(animation, ik, stack.Keys[k].Time, ik.Chain[^1], ik) is not { } p) continue;
                    worst = MathF.Max(worst, Vector3.Distance(p[0], p[1]));
                }
            }
        return worst;
    }

    /// <summary>
    /// The largest one-frame jump of an IK chain's end (a reverse foot's ball) between a locked key
    /// and 0.01 frame after it, as <c>cli keyjump</c> reports it. A fresh evaluator per measurement:
    /// one built before an edit keeps its cached stack data.
    /// </summary>
    private static float WorstLockedKeyJump(Character character, Animation animation)
    {
        var evaluator = new PoseEvaluator(character);
        float worst = 0f;
        foreach (var stack in animation.Stacks)
        {
            if (!stack.IsEnabled) continue;
            int n = stack.Keys.Count;
            foreach (var track in stack.Tracks)
            {
                if (track.Target is not ControllerIK ik) continue;
                var end = ik is ControllerReverseFoot && ik.Chain.Count >= 4 ? ik.Chain[3] : ik.Chain[^1];
                int ei = evaluator.IndexOf(end);
                if (ei < 0) continue;
                for (int k = 0; k < Math.Max(1, n - 1) && k < track.Keys.Count; k++)
                {
                    if (track.Keys[k] is not IKKey key || (key.Flags & 1) == 0) continue;
                    float t = stack.Keys[k].Time;
                    worst = MathF.Max(worst, Vector3.Distance(
                        evaluator.WorldMatrices(animation, t)[ei].Translation,
                        evaluator.WorldMatrices(animation, t + 0.01f)[ei].Translation));
                }
            }
        }
        return worst;
    }

    /// <summary>The converted Chest track of a transfer's first animation (the stack that has one).</summary>
    private static AnimTrack ChestTrack(RetargetingResult result) =>
        result.Animations[0].Stacks
            .SelectMany(stack => stack.Tracks)
            .First(track => track.Target?.Name == "Chest");

    /// <summary>
    /// Runs one animation through the transfer with a reading, optionally rotating the target's Chest
    /// reference pose first, and returns the source Chest key, the converted key's rotation and the
    /// rotation the world reading predicts.
    /// </summary>
    private (Quaternion Source, Quaternion Converted, Quaternion Expected, Vector3 SourceT, Vector3 ConvertedT, float Ratio) TransferOne(
        RetargetMethod method, Quaternion? targetRotation)
    {
        if (Load("19_run.akt") is not { } model)
            return (Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, Vector3.Zero, Vector3.Zero, 1f);
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        if (targetRotation is { } rotation) RotateReferencePose(target, "Chest", rotation);
        var animation = source.Animations.FirstOrDefault(a => a.Name == "Run_3") ?? source.Animations.First();

        var stacks = animation.Stacks.ToList();
        int stackIndex = stacks.FindIndex(stack => stack.Tracks.Any(track => track.Target?.Name == "Chest"));
        var sourceTrack = stacks[stackIndex].Tracks.First(track => track.Target?.Name == "Chest");
        int index = 0;
        float widest = -1f;
        for (int k = 0; k < sourceTrack.Keys.Count; k++)
        {
            if (sourceTrack.Keys[k] is not TransformKey key) continue;
            float angle = Angle(Quaternion.Identity, key.Value.Rotation);
            if (angle <= widest) continue;
            widest = angle;
            index = k;
        }
        var sourceKey = (TransformKey)sourceTrack.Keys[index]!;

        var result = Retargeting.Transfer(model, source, target, new RetargetingOptions
        {
            Animations = [animation],
            Method = method,
            SolveLockedKeys = false, // the per-key reading alone
        });
        var copyTrack = result.Animations[0].Stacks[stackIndex].Tracks.First(track => track.Target?.Name == "Chest");
        var copyKey = (TransformKey)copyTrack.Keys[index]!;

        // The world reading's offset: the rotation from the source node's reference-pose world
        // orientation to the target's (Retargeting's world-method offset).
        var sourceEvaluator = new PoseEvaluator(source);
        var targetEvaluator = new PoseEvaluator(target);
        var sourceWorld = sourceEvaluator.WorldMatrices(null, 0)[sourceEvaluator.IndexOf(sourceTrack.Target!)];
        var targetWorld = targetEvaluator.WorldMatrices(null, 0)[targetEvaluator.IndexOf(copyTrack.Target!)];
        Matrix4x4.Decompose(sourceWorld, out _, out var sourceRotation, out _);
        Matrix4x4.Decompose(targetWorld, out _, out var targetRotationWorld, out _);
        // A key is a delta in the node's own frame, so it changes basis by C = Rsource^-1 * Rtarget.
        var basis = Quaternion.Normalize(Quaternion.Inverse(sourceRotation) * targetRotationWorld);
        var expected = Quaternion.Inverse(basis) * sourceKey.Value.Rotation * basis;
        return (sourceKey.Value.Rotation, copyKey.Value.Rotation, expected,
            sourceKey.Value.Translation, copyKey.Value.Translation, result.Ratio);
    }

    /// <summary>
    /// The last used pair: a transfer keeps it
    /// on the receiving character's matching - the one pointing at the source - and clears every
    /// other matching, which is what <c>CScene::SetLastAKNMCharactersUsed</c> writes; the dialog then
    /// opens on that pair in the direction it was used.
    /// </summary>
    [Fact]
    public void A_transfer_marks_the_receiving_characters_matching_as_the_last_used_pair()
    {
        if (Load("19_run.akt") is not { } model) return;
        var source = Dup(model);
        var target = Clone(model, source, "Copy");
        Retargeting.Transfer(model, source, target, new RetargetingOptions { Animations = [source.Animations.First()] });

        var receiving = RetargetingMatching.Find(target, source);
        var giving = RetargetingMatching.Find(source, target);
        Assert.NotNull(receiving);
        Assert.NotNull(giving);
        Assert.True(receiving!.IsLastUsedRetargetingMatching);
        Assert.False(giving!.IsLastUsedRetargetingMatching);
        var last = RetargetingMatching.LastUsed(model);
        Assert.NotNull(last);
        Assert.Same(target, last!.Value.Owner);
        Assert.Same(source, last.Value.Target);

        var session = new RetargetSession(model);
        Assert.Same(source, session.Source);
        Assert.Same(target, session.Target);
    }

    /// <summary>
    /// The name-pair cache: a transfer writes
    /// The two files - one per direction of the pair - in the
    /// <c>$Source$</c> / <c>$Target$</c> form, sorted by the source name, and
    /// a scene without a usable stored matching reopens the grid from it by name.
    /// </summary>
    [Fact]
    public void The_name_pair_cache_is_written_and_restores_the_grid()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            if (Load("19_run.akt") is not { } model) return;
            var source = Dup(model);
            var target = Clone(model, source, "Copy");
            var animation = source.Animations.First();
            var result = Retargeting.Transfer(model, source, target, new RetargetingOptions
            {
                Animations = [animation],
                CacheDirectory = directory,
            });

            string forward = RetargetingPreferences.PathFor("Dup", "Copy", directory);
            string reverse = RetargetingPreferences.PathFor("Copy", "Dup", directory);
            Assert.True(File.Exists(forward), $"no cache at {forward}");
            Assert.True(File.Exists(reverse), $"no mirrored cache at {reverse}");
            var cache = RetargetingPreferences.Load(forward);
            var expected = RetargetingPreferences.PairsOf(result.Map);
            Assert.Equal(expected, cache.Pairs);
            Assert.All(cache.Pairs, pair => Assert.True(pair.Source.Length > 0 && pair.Target.Length > 0));
            Assert.Equal(cache.Pairs.OrderBy(pair => pair.Source, StringComparer.Ordinal), cache.Pairs);

            // The expected string form, and the file's class.
            const string measured = "$Source$Chest$Target$Chest" +
                                    "$Source$Nose$Target$nose";
            Assert.Equal([("Chest", "Chest"), ("Nose", "nose")], RetargetingPreferences.Parse(measured));
            Assert.Equal(measured, RetargetingPreferences.Format([("Nose", "nose"), ("Chest", "Chest")]));
            Assert.Equal("CRetargetingPreferences", AktDocument.Load(forward).Root.ClassKey);

            // A fresh scene of the same pair has no stored matching: the dialog restores the cache.
            if (Load("19_run.akt") is not { } freshModel) return;
            var freshSource = Dup(freshModel);
            var freshTarget = Clone(freshModel, freshSource, "Copy");
            Assert.Null(RetargetingMatching.Restore(freshSource, freshTarget));
            var session = new RetargetSession(freshModel, cacheDirectory: directory)
            {
                Source = freshSource,
                Target = freshTarget,
            };
            var rows = session.Rows;
            Assert.Equal(expected.Count, rows.Count(row => row.Target != null));
            foreach (var (pairSource, pairTarget) in expected)
                Assert.Equal(pairTarget, rows.First(row => row.Source.Name == pairSource).Target?.Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180f / MathF.PI;

    // ---- helpers --------------------------------------------------------------------------

    private static SceneModel? Load(string fileName) =>
        TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == fileName) is { } path
            ? TestFiles.LoadScene(path)
            : null;

    private static Character Dup(SceneModel model) => model.Scene.Characters.First(c => c.Name == "Dup");

    /// <summary>
    /// A second character in the scene: a deep copy of <paramref name="source"/> with only its rig
    /// and poses (meshes and the Anim Bank are dropped, so the copy reads as a fresh character).
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

    /// <summary>Multiplies every reference-pose translation by <paramref name="scale"/> (world positions scale).</summary>
    private static void ScaleReferencePose(Character character, float scale)
    {
        var pose = character.RefPose ?? throw new InvalidOperationException("no reference pose");
        for (int i = 0; i < pose.Count; i++)
        {
            var m = pose.Matrix(i);
            m.M41 *= scale; m.M42 *= scale; m.M43 *= scale;
            pose.SetMatrix(i, m);
        }
    }

    /// <summary>Rotates one controller's reference-pose matrix about its own origin.</summary>
    private static void RotateReferencePose(Character character, string name, Quaternion rotation)
    {
        var pose = character.RefPose ?? throw new InvalidOperationException("no reference pose");
        var evaluator = new PoseEvaluator(character);
        int index = evaluator.IndexOf(character.Controllers.First(c => c.Name == name));
        var m = pose.Matrix(index);
        var t = m.Translation;
        m.Translation = Vector3.Zero;
        m = Matrix4x4.CreateFromQuaternion(rotation) * m;
        m.Translation = t;
        pose.SetMatrix(index, m);
    }

    /// <summary>Scales one controller's reference-pose matrix basis rows, i.e. a non-uniform pose scale.</summary>
    private static void ScaleReferencePoseAxes(Character character, string name, Vector3 axes)
    {
        var pose = character.RefPose ?? throw new InvalidOperationException("no reference pose");
        var evaluator = new PoseEvaluator(character);
        int index = evaluator.IndexOf(character.Controllers.First(c => c.Name == name));
        var m = pose.Matrix(index);
        m.M11 *= axes.X; m.M12 *= axes.X; m.M13 *= axes.X; m.M14 *= axes.X;
        m.M21 *= axes.Y; m.M22 *= axes.Y; m.M23 *= axes.Y; m.M24 *= axes.Y;
        m.M31 *= axes.Z; m.M32 *= axes.Z; m.M33 *= axes.Z; m.M34 *= axes.Z;
        pose.SetMatrix(index, m);
    }

    private string TempName(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-retarget-{Guid.NewGuid():N}{extension}");
        _temporaryFiles.Add(path);
        return path;
    }
}
