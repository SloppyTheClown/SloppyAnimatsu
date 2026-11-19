using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Skin Workshop on the user's samples: the Add/Aim
/// brush, Set and Spinner weight, Prune, Fix, the Flooder and Mirror Skin. Every edit is saved and
/// reloaded byte-exact; the weights stay normalized and index real joints.
/// </summary>
public class SkinWorkshopTests
{
    [SceneTheory, SceneSamples]
    public void Paint_Add_RaisesTheWeightAndKeepsEveryListNormalized(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, joint) = target.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0 || positions.Length == 0) return;
        float before = SkinWorkshop.Weight(mesh, vertex, joint);
        float diagonal = Diagonal(positions);

        var stroke = SkinWorkshop.BeginStroke(mesh, joint, character: character);
        var brush = new SkinPaintBrush
        {
            Mode = SkinPaintMode.Add,
            Radius = diagonal * 0.02f,
            Value = 0.5f,
        };
        int painted = stroke.Apply(brush, positions[vertex], positions);
        stroke.Commit();

        Assert.True(painted >= 1);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > before,
            $"{before} did not grow at control point {vertex}");
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Paint_Aim_ConvergesToTheValueAcrossDabs(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, joint) = target.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0 || positions.Length == 0) return;
        float diagonal = Diagonal(positions);

        var stroke = SkinWorkshop.BeginStroke(mesh, joint, character: character);
        var brush = new SkinPaintBrush
        {
            Mode = SkinPaintMode.Aim,
            Radius = diagonal * 0.02f,
            Value = 1f,
            Hard = true,
            Strength = 1f,
        };
        for (int dab = 0; dab < 8; dab++) stroke.Apply(brush, positions[vertex], positions);
        stroke.Commit();

        Assert.True(MathF.Abs(SkinWorkshop.Weight(mesh, vertex, joint) - 1f) < 1e-3f,
            $"{SkinWorkshop.Weight(mesh, vertex, joint)} did not reach 1");
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void SetWeight_GivesTheJointTheValueAndRedistributesTheOthers(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, joint) = target.Value;
        int vertex = FindVertexWithTwoInfluences(mesh, joint);
        if (vertex < 0) return;
        var positions = BindPositions(character, mesh);

        SkinWorkshop.SetWeight(mesh, vertex, joint, 0.9f, character: character,
            jointPositions: JointPositions(character), vertexPosition: positions[vertex]);

        Assert.True(MathF.Abs(SkinWorkshop.Weight(mesh, vertex, joint) - 0.9f) < 1e-3f);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void AddWeight_MovesTheJointByTheDeltaAndClamps(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, joint) = target.Value;
        int vertex = FirstWeightedVertex(mesh, joint);
        if (vertex < 0) return;
        var positions = BindPositions(character, mesh);
        float before = SkinWorkshop.Weight(mesh, vertex, joint);

        SkinWorkshop.AddWeight(mesh, vertex, joint, 5f, character: character,
            jointPositions: JointPositions(character), vertexPosition: positions[vertex]);

        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) >= before);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) <= 1f);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Prune_RemovesInfluencesBelowTheLimitAndKeepsTheSum(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, joint) = target.Value;
        int vertex = FindVertexWithTwoInfluences(mesh, joint);
        if (vertex < 0) return;
        var positions = BindPositions(character, mesh);

        SkinWorkshop.SetWeight(mesh, vertex, joint, 0.0015f, character: character,
            jointPositions: JointPositions(character), vertexPosition: positions[vertex]);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > 0f);
        int removed = SkinWorkshop.Prune(mesh, limit: 0.002f);

        Assert.True(removed >= 1);
        Assert.Equal(0f, SkinWorkshop.Weight(mesh, vertex, joint));
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Fix_AveragesTheSelectedControlPointsNeighbours(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (character, mesh, _) = target.Value;
        var neighbours = SkinWorkshop.Neighbours(mesh);
        int vertex = -1;
        for (int v = 0; v < mesh.ControlPoints.Length; v++)
        {
            if (neighbours[v].Length < 2) continue;
            vertex = v;
            break;
        }
        if (vertex < 0) return;

        var selected = SkinSelection.From(mesh, neighbours[vertex].Append(vertex));
        var contributors = neighbours[vertex];
        var expected = new Dictionary<int, float>();
        foreach (int q in contributors)
            foreach (var (bone, weight) in SkinWorkshop.Weights(mesh, q))
                expected[bone] = expected.GetValueOrDefault(bone) + weight;
        foreach (int bone in expected.Keys.ToList()) expected[bone] /= contributors.Length;

        SkinWorkshop.Fix(mesh, selected, passes: 1);

        foreach (var (bone, weight) in expected)
            Assert.True(MathF.Abs(SkinWorkshop.Weight(mesh, vertex, bone) - weight) < 1e-3f,
                $"joint {bone}: {SkinWorkshop.Weight(mesh, vertex, bone)} vs {weight}");
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Flood_GrowsTheSelectionToItsConnectedComponent(string path)
    {
        var model = TestFiles.LoadScene(path);
        var target = FindPaintable(model);
        if (target == null) return;
        var (_, mesh, _) = target.Value;
        if (mesh.ControlPoints.Length < 2) return;
        var neighbours = SkinWorkshop.Neighbours(mesh);
        int seed = Array.FindIndex(neighbours, n => n.Length > 0);
        if (seed < 0) return;

        var expected = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
            foreach (int q in neighbours[queue.Dequeue()])
                if (expected.Add(q)) queue.Enqueue(q);

        var selection = SkinSelection.From(mesh, [seed]);
        int count = SkinWorkshop.Flood(mesh, selection);

        Assert.Equal(expected.Count, count);
        Assert.All(expected, v => Assert.True(selection.IsSelected(v)));
        Assert.Equal(count, SkinWorkshop.Flood(mesh, selection));
    }

    [SceneTheory, SceneSamples]
    public void Mirror_CopiesTheSourceJointsWeightOntoTheSymmetricControlPoint(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindMirrorPair(model);
        if (found == null) return;
        var (character, mesh, source, partner) = found.Value;
        var positions = BindPositions(character, mesh);
        var bindJoints = BindJointPositions(character, mesh);
        float plane = (bindJoints[source].X + bindJoints[partner].X) * 0.5f;
        float diagonal = Diagonal(positions);
        float tolerance = diagonal * 0.01f;

        // Pick a control point whose bind-pose mirror has a counterpart; no partner, no test.
        int vertex = -1, mirroredVertex = -1;
        for (int v = 0; v < positions.Length; v++)
        {
            if (mesh.SkinWeights[v].Length == 0) continue;
            var point = new Vector3(2f * plane - positions[v].X, positions[v].Y, positions[v].Z);
            int best = Nearest(positions, point, tolerance);
            if (best < 0 || best == v) continue;
            vertex = v;
            mirroredVertex = best;
            break;
        }
        if (vertex < 0) return;

        var jointPositions = JointPositions(character);
        SkinWorkshop.SetWeight(mesh, vertex, source, 1f, character: character,
            jointPositions: jointPositions, vertexPosition: positions[vertex]);

        int written = SkinMirror.Mirror(character, [character.Joints[source]], [mesh], tolerance);

        Assert.True(written >= 1);
        Assert.True(SkinWorkshop.Weight(mesh, mirroredVertex, partner) > 0.9f,
            $"control point {mirroredVertex} has {SkinWorkshop.Weight(mesh, mirroredVertex, partner)} on '{character.Joints[partner].Name}'");
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    // ---- Mirror Skin's three-nearest blend on synthetic asymmetric control points -----------------
    // A synthetic scene cannot be built here (the model classes are only constructible from AKT
    // objects and the core has no mesh factory), so the decoded blend is exercised on its own seam:
    // reflected source control points at known distances and weights. The sample test above covers
    // the whole Mirror pass end to end.

    [Fact]
    public void MirrorBlend_AveragesTheThreeNearestCandidatesByInverseDistance()
    {
        // The mirrored point sits between three source control points at distances 1, 2 and 4, each
        // carrying weights on the source side's joints; a fourth point is further out and must be
        // ignored (the app keeps a three-entry list).
        var candidates = new SkinMirror.MirrorCandidate[]
        {
            Candidate(distance: 4f, (10, 1f)),
            Candidate(distance: 1f, (10, 0.6f), (12, 0.4f)),
            Candidate(distance: 2f, (11, 1f)),
            Candidate(distance: 16f, (13, 1f)),
        };
        var partners = new Dictionary<int, int> { [10] = 20, [11] = 21, [12] = 22, [13] = 23 };

        var blended = SkinMirror.BlendCandidates(candidates, partners);

        // 1/d weights: 1, 1/2, 1/4; total 1.75.
        const float Total = 1.75f;
        var weights = blended.ToDictionary(w => w.Bone, w => w.Weight);
        Assert.Equal(3, weights.Count);
        Assert.Equal((0.6f + 0.25f) / Total, weights[20], 4);
        Assert.Equal(0.5f / Total, weights[21], 4);
        Assert.Equal(0.4f / Total, weights[22], 4);
        Assert.DoesNotContain(23, weights.Keys);
        Assert.Equal(1f, blended.Sum(w => w.Weight), 4);
    }

    [Fact]
    public void MirrorBlend_CopiesACoincidentCandidateDirectly()
    {
        var candidates = new SkinMirror.MirrorCandidate[]
        {
            Candidate(distance: 1f, (10, 0.25f), (11, 0.75f)),
            Candidate(distance: 0.0001f, (10, 0.6f), (12, 0.4f)),
        };
        var partners = new Dictionary<int, int> { [10] = 20, [11] = 21, [12] = 22 };

        // The near-zero candidate is copied as it is (mapped), not blended with the further one.
        var blended = SkinMirror.BlendCandidates(candidates, partners, coincidentDistance: 0.001f);

        var weights = blended.ToDictionary(w => w.Bone, w => w.Weight);
        Assert.Equal(2, weights.Count);
        Assert.Equal(0.6f, weights[20], 5);
        Assert.Equal(0.4f, weights[22], 5);
        Assert.DoesNotContain(21, weights.Keys);
    }

    [Fact]
    public void MirrorBlend_KeepsAnUnmappedJointWhenThePartnerMapHasNoEntry()
    {
        var candidates = new SkinMirror.MirrorCandidate[]
        {
            Candidate(distance: 2f, (10, 0.5f), (11, 0.5f)),
        };

        var blended = SkinMirror.BlendCandidates(candidates, new Dictionary<int, int> { [10] = 20 });

        var weights = blended.ToDictionary(w => w.Bone, w => w.Weight);
        Assert.Equal(0.5f, weights[20], 5);
        Assert.Equal(0.5f, weights[11], 5);
    }

    private static SkinMirror.MirrorCandidate Candidate(float distance, params (int Bone, float Weight)[] weights) =>
        new(Mesh: 0, Vertex: (int)(distance * 100f), Distance: distance, Weights: weights);

    [SceneTheory, SceneSamples]
    public void CopyPaste_WritesTheSourceWeightsOntoTheTargetSelection(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, _) = found.Value;

        var weighted = new List<int>();
        for (int v = 0; v < mesh.SkinWeights.Length; v++)
            if (mesh.SkinWeights[v].Length > 0) weighted.Add(v);
        if (weighted.Count < 4) return;

        // Three copied control points onto two target ones: the paste stops at the shorter list.
        var sourceVertices = weighted.Take(3).ToArray();
        var targetVertices = weighted.Skip(3).Take(2).ToArray();

        var clipboard = SkinWeightClipboard.Copy(new SkinWeightTable(mesh, character: character),
            SkinSelection.From(mesh, sourceVertices), character, out string? refusal);
        Assert.True(clipboard != null, refusal);

        var expected = sourceVertices
            .Select(v => mesh.SkinWeights[v]
                .Select(w => (Name: character.Joints[w.Bone].Name ?? "", w.Weight)).ToArray())
            .ToArray();
        var jointOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < character.Joints.Count; i++) jointOf.TryAdd(character.Joints[i].Name ?? "", i);

        var targetTable = new SkinWeightTable(mesh, character: character);
        var paste = clipboard!.Paste(targetTable, SkinSelection.From(mesh, targetVertices), character, out refusal);
        int written = paste.Points;

        Assert.True(written == targetVertices.Length, $"{written} control points written: {refusal}");
        Assert.False(paste.Remapped, "Every copied joint has a same-named joint on the target.");
        for (int i = 0; i < written; i++)
        {
            var weights = mesh.SkinWeights[targetVertices[i]];
            foreach (var (name, weight) in expected[i])
            {
                float actual = weights.Where(w => w.Bone == jointOf[name]).Sum(w => w.Weight);
                Assert.True(MathF.Abs(actual - weight) < 1e-4f,
                    $"control point {targetVertices[i]} has {actual} on '{name}', copied {weight}");
            }
        }
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Copy_RefusesFewerThanTwoControlPoints(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        int vertex = FirstWeightedVertex(mesh, joint);
        if (vertex < 0) return;

        var clipboard = SkinWeightClipboard.Copy(new SkinWeightTable(mesh, character: character),
            SkinSelection.From(mesh, [vertex]), character, out string? refusal);

        Assert.Null(clipboard);
        Assert.Contains("two", refusal);
    }

    [SceneTheory, SceneSamples]
    public void Slough_MapsAJointWithoutACounterpartOntoItsNearestNamedAncestor(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, _) = found.Value;

        var weighted = Enumerable.Range(0, mesh.SkinWeights.Length)
            .Where(v => mesh.SkinWeights[v].Length > 0).ToList();
        if (weighted.Count < 4) return;

        // A joint whose parent is also a joint: renaming only the child leaves the parent's name
        // as the slough's fallback.
        int child = -1, parent = -1;
        for (int j = 0; j < character.Joints.Count && child < 0; j++)
        {
            if (character.Joints[j].Parent3D is not Joint p) continue;
            int index = Enumerable.Range(0, character.Joints.Count)
                .FirstOrDefault(i => ReferenceEquals(character.Joints[i], p), -1);
            if (index < 0) continue;
            if (!Enumerable.Range(0, mesh.SkinWeights.Length)
                    .Any(v => SkinWorkshop.Weight(mesh, v, j) > 1e-4f)) continue;
            child = j;
            parent = index;
        }
        if (child < 0) return;

        var sourceVertices = weighted.Where(v => SkinWorkshop.Weight(mesh, v, child) > 1e-4f).Take(2).ToList();
        if (sourceVertices.Count < 2) return;
        var targetVertices = weighted.Skip(sourceVertices.Count).Take(2).ToList();
        if (targetVertices.Count < 2) return;

        var clipboard = SkinWeightClipboard.Copy(new SkinWeightTable(mesh, character: character),
            SkinSelection.From(mesh, sourceVertices), character, out string? refusal);
        Assert.True(clipboard != null, refusal);
        string childName = character.Joints[child].Name ?? "";

        // Copy first, then make the child's name unresolvable on the target: the clipboard keeps
        // the names and the ancestor chain it saw at copy time.
        character.Joints[child].Name = childName + "@missing";

        var target = new SkinWeightTable(mesh, character: character);
        var paste = clipboard!.Paste(target, SkinSelection.From(mesh, targetVertices), character, out refusal);

        Assert.True(paste.Points == targetVertices.Count, $"{paste.Points} written: {refusal}");
        Assert.True(paste.Remapped);
        Assert.Contains(paste.Warnings, warning => warning.Contains("ancestor") && warning.Contains(childName));

        // Every copied influence is written through the map: the child's weight lands on the parent
        // (the nearest mapped ancestor) and nothing keeps the child.
        for (int i = 0; i < paste.Points; i++)
        {
            var expected = new Dictionary<int, float>();
            foreach (var entry in clipboard.Vertices[i])
            {
                int joint = entry.JointName == childName ? parent : JointIndex(character, entry.JointName);
                if (joint < 0) continue;
                expected[joint] = expected.GetValueOrDefault(joint) + entry.Weight;
            }
            float sum = expected.Values.Sum();
            foreach (var influence in mesh.SkinWeights[targetVertices[i]])
            {
                Assert.True(influence.Bone != child,
                    $"control point {targetVertices[i]} kept '{childName}'");
                if (!expected.TryGetValue(influence.Bone, out float weight)) continue;
                Assert.Equal(weight / sum, influence.Weight, 3);
            }
        }
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Slough_FallsBackToTheRootJointWhenNoAncestorIsNamed(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, _) = found.Value;
        if (character.Joints.Count == 0) return;

        var weighted = Enumerable.Range(0, mesh.SkinWeights.Length)
            .Where(v => mesh.SkinWeights[v].Length > 0).ToList();
        if (weighted.Count < 4) return;

        var clipboard = SkinWeightClipboard.Copy(new SkinWeightTable(mesh, character: character),
            SkinSelection.From(mesh, weighted.Take(2)), character, out string? refusal);
        Assert.True(clipboard != null, refusal);

        // No copied name nor ancestor survives: the last resort is the target's first joint.
        foreach (var joint in character.Joints) joint.Name = joint.Name + "@missing";
        var root = character.Joints[0].Name;
        var target = new SkinWeightTable(mesh, character: character);
        var paste = clipboard!.Paste(target, SkinSelection.From(mesh, weighted.Skip(2).Take(2)), character, out refusal);

        Assert.True(paste.Points == 2, $"{paste.Points} written: {refusal}");
        Assert.Contains(paste.Warnings, warning => warning.Contains("root joint") && warning.Contains(root ?? ""));
        foreach (int vertex in weighted.Skip(2).Take(2))
        {
            var influences = mesh.SkinWeights[vertex];
            Assert.Single(influences);
            Assert.Equal(0, influences[0].Bone);
            Assert.Equal(1f, influences[0].Weight, 3);
        }
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [Fact]
    public void Paste_RefusesAnEmptyClipboard()
    {
        // The refusal is the first thing the paste checks, so the table never has to exist.
        int written = new SkinWeightClipboard().Paste(null!, new SkinSelection(0), null!, out string? refusal).Points;
        Assert.Equal(0, written);
        Assert.Equal("The clipboard is empty; copy some weights first.", refusal);
    }

    [SceneTheory, SceneSamples]
    public void Paste_RefusesWhenNothingIsSelectedToPasteOnto(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, _) = found.Value;
        var weighted = Enumerable.Range(0, mesh.SkinWeights.Length)
            .Where(v => mesh.SkinWeights[v].Length > 0).ToList();
        if (weighted.Count < 3) return;

        var clipboard = SkinWeightClipboard.Copy(new SkinWeightTable(mesh, character: character),
            SkinSelection.From(mesh, weighted.Take(2)), character, out string? refusal);
        Assert.True(clipboard != null, refusal);
        var paste = clipboard!.Paste(new SkinWeightTable(mesh, character: character), new SkinSelection(mesh),
            character, out refusal);
        Assert.Equal(0, paste.Points);
        Assert.Equal("Select the control points to paste onto.", refusal);
    }

    // ---- the panel's session and weight list ---------------------------------------------------

    [SceneTheory, SceneSamples]
    public void WeightList_ReportsTheMinAndMaxWeightOverTheSelection(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        int vertex = FirstWeightedVertex(mesh, joint);
        if (vertex < 0) return;
        var influenced = Enumerable.Range(0, mesh.SkinWeights.Length)
            .Where(v => mesh.SkinWeights[v].Any(w => w.Bone == joint && w.Weight >= 1e-5f))
            .ToList();

        var selection = SkinSelection.From(mesh, [vertex]);
        var rows = SkinWorkshop.WeightList(character, mesh, currentVertex: vertex, selection: selection);

        Assert.Equal(character.Joints.Count, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i, rows[i].Joint);
            Assert.Equal(character.Joints[i].Name, rows[i].Name);
        }

        // One selected control point: both fractions are its weight on the joint
        // and a joint the selection carries is
        // what enables the editable field.
        Assert.Equal(SkinWorkshop.Weight(mesh, vertex, joint), rows[joint].MinWeight, 3);
        Assert.Equal(SkinWorkshop.Weight(mesh, vertex, joint), rows[joint].MaxWeight, 3);
        Assert.True(rows[joint].Editable);
        Assert.All(rows, row => Assert.InRange(row.MinWeight, 0f, 1f));
        Assert.All(rows, row => Assert.InRange(row.MaxWeight, 0f, 1f));

        // Two selected points with different weights: the fractions are that pair's minimum and
        // maximum, not their mean, and the edit field still shows the current control point.
        int other = influenced.FirstOrDefault(v =>
            MathF.Abs(SkinWorkshop.Weight(mesh, v, joint) - SkinWorkshop.Weight(mesh, vertex, joint)) > 1e-3f, -1);
        if (other >= 0)
        {
            var two = SkinSelection.From(mesh, [vertex, other]);
            var multi = SkinWorkshop.WeightList(character, mesh, currentVertex: vertex, selection: two);
            float mine = SkinWorkshop.Weight(mesh, vertex, joint);
            float theirs = SkinWorkshop.Weight(mesh, other, joint);
            Assert.Equal(MathF.Min(mine, theirs), multi[joint].MinWeight, 3);
            Assert.Equal(MathF.Max(mine, theirs), multi[joint].MaxWeight, 3);
            Assert.Equal(mine, multi[joint].Weight, 5);
        }

        // A joint the selection carries nowhere still gets a row in SloppyAnimatsu, and the
        // routine's last pass leaves the pair at 0/0 rather than the 1/0 default.
        int absent = Enumerable.Range(0, character.Joints.Count).FirstOrDefault(j => rows[j].MaxWeight == 0f, -1);
        if (absent >= 0)
        {
            Assert.Equal(0f, rows[absent].MinWeight);
            Assert.Equal(0f, rows[absent].MaxWeight);
            Assert.False(rows[absent].Editable);
        }

        // With nothing selected every row reads 0/0: the added later pass zeroes the 1 default of
        // +0x8, so the current control point does not stand in for the selection (as it used to).
        var none = SkinWorkshop.WeightList(character, mesh, currentVertex: vertex);
        Assert.All(none, row =>
        {
            Assert.Equal(0f, row.MinWeight);
            Assert.Equal(0f, row.MaxWeight);
            Assert.False(row.Editable);
        });
    }

    [SceneTheory, SceneSamples]
    public void Session_PaintsThroughTheBrushAndNormalizes(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0) return;
        float before = SkinWorkshop.Weight(mesh, vertex, joint);
        var session = new SkinPaintSession(character, mesh) { Joint = joint };
        session.Radius = Diagonal(positions) * 0.02f;

        Assert.True(session.BeginStroke());
        int painted = session.Dab(positions[vertex], positions, JointPositions(character));
        session.EndStroke();

        Assert.True(painted >= 1);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > before);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Session_SetWeightWritesTheSelectionAndFloodGrowsIt(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = FindVertexWithTwoInfluences(mesh, joint);
        if (vertex < 0) return;
        var session = new SkinPaintSession(character, mesh) { Joint = joint };
        session.Selection.Set(vertex, true);

        int written = session.SetWeight(0.9f, positions, JointPositions(character));
        int flooded = session.Flood();

        Assert.Equal(1, written);
        Assert.True(MathF.Abs(SkinWorkshop.Weight(mesh, vertex, joint) - 0.9f) < 1e-3f);
        Assert.True(flooded > 1);
        Assert.True(session.Selection.SelectedCount >= flooded);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Session_CopiesAndPastesBetweenItsOwnSelections(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var weighted = Enumerable.Range(0, mesh.ControlPoints.Length)
            .Where(v => mesh.SkinWeights[v].Length > 0).ToList();
        if (weighted.Count < 4) return;
        var session = new SkinPaintSession(character, mesh) { Joint = joint };
        session.Selection.Set(weighted[0], true);
        session.Selection.Set(weighted[1], true);

        var clipboard = session.Copy(out string? refusal);
        Assert.True(clipboard != null, refusal);

        session.Selection.Clear();
        session.Selection.Set(weighted[2], true);
        session.Selection.Set(weighted[3], true);
        int pasted = session.Paste(clipboard!, out refusal).Points;

        Assert.Equal(2, pasted);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void WeightSnapshot_RestoresTheArraysOfThePaintedMesh(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0) return;
        var before = mesh.SkinWeights;
        var snapshot = EditSnapshot.TakeWeights(character, [mesh]);
        var session = new SkinPaintSession(character, mesh) { Joint = joint };
        session.Radius = Diagonal(positions) * 0.05f;
        session.BeginStroke();
        session.Dab(positions[vertex], positions);
        session.EndStroke();

        snapshot.Restore();

        var after = mesh.SkinWeights;
        Assert.Equal(before.Length, after.Length);
        for (int v = 0; v < before.Length; v++)
        {
            Assert.Equal(before[v].Length, after[v].Length);
            for (int i = 0; i < before[v].Length; i++)
            {
                Assert.Equal(before[v][i].Bone, after[v][i].Bone);
                Assert.Equal(before[v][i].Weight, after[v][i].Weight, 6);
            }
        }
        AssertRoundtrip(model);
    }

    // ---- hidden vertices, the Scanner, the front/back test and the weight list ----

    [SceneTheory, SceneSamples]
    public void HiddenVertices_AreSavedAndSkippedByPaintFloodAndPrune(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0 || positions.Length == 0) return;
        var session = new SkinPaintSession(character, mesh) { Joint = joint };
        session.Selection.Set(vertex, true);
        uint counter = character.HiddenVerticesCounter;

        Assert.Equal(1, session.HideSelection());
        Assert.True(mesh.IsVertexHidden(vertex));
        Assert.Equal(counter + 1, character.HiddenVerticesCounter);

        // The brush and Set Weight leave a hidden control point alone.
        float before = SkinWorkshop.Weight(mesh, vertex, joint);
        session.Radius = Diagonal(positions) * 0.05f;
        session.BeginStroke();
        session.Dab(positions[vertex], positions);
        session.EndStroke();
        Assert.Equal(before, SkinWorkshop.Weight(mesh, vertex, joint), 5);
        Assert.Equal(0, session.SetJointWeight(joint, Math.Min(1f, before + 0.5f), positions));

        // Prune skips it even when its weight is under the limit.
        var table = new SkinWeightTable(mesh, character: character);
        table.SetWeight(vertex, joint, 0.0015f);
        table.Write();
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > 0f);
        SkinWorkshop.Prune(mesh, limit: 0.002f);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > 0f);

        // The Flooder stops at hidden points.
        int neighbour = SkinWorkshop.Neighbours(mesh)[vertex].FirstOrDefault(q => !mesh.IsVertexHidden(q), -1);
        if (neighbour >= 0)
        {
            var selection = SkinSelection.From(mesh, [neighbour]);
            SkinWorkshop.Flood(mesh, selection);
            Assert.False(selection.IsSelected(vertex));
        }

        Assert.Equal(1, session.ShowAllVertices());
        Assert.False(mesh.IsVertexHidden(vertex));
        Assert.Equal(counter + 2, character.HiddenVerticesCounter);

        // The bits and the counter survive a save and reload.
        session.HideSelection();
        string file = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-hidden-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(file);
            var reloaded = TestFiles.LoadScene(file);
            var mesh2 = reloaded.Scene.Characters.SelectMany(c => c.Meshes).FirstOrDefault(m => m.Name == mesh.Name);
            Assert.NotNull(mesh2);
            Assert.True(mesh2!.IsVertexHidden(vertex));
            Assert.Equal(counter + 3, reloaded.Scene.Characters.First(c => c.Name == character.Name).HiddenVerticesCounter);
        }
        finally
        {
            File.Delete(file);
        }
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void WeightList_ReportsTheCurrentPointsWeightAndTheLockProtectsJoints(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        int vertex = FindVertexWithTwoInfluences(mesh, joint);
        if (vertex < 0) return;
        var positions = BindPositions(character, mesh);
        var session = new SkinPaintSession(character, mesh) { Joint = joint, CurrentVertex = vertex };
        session.Selection.Set(vertex, true);

        var rows = session.WeightList();
        Assert.Equal(character.Joints.Count, rows.Count);
        Assert.Equal(SkinWorkshop.Weight(mesh, vertex, joint), rows[joint].Weight, 3);
        // One selected control point: both fractions are its weight on the painted joint, and a
        // joint that carries the selection is what enables the editable field.
        Assert.Equal(SkinWorkshop.Weight(mesh, vertex, joint), rows[joint].MinWeight, 3);
        Assert.True(rows[joint].Editable);
        Assert.False(rows[joint].Locked);

        int other = mesh.SkinWeights[vertex].Select(w => w.Bone).FirstOrDefault(b => b != joint, -1);
        if (other < 0) return;
        var otherInfluences = mesh.SkinWeights[vertex].Select(w => w.Bone).Where(b => b != joint).Distinct().ToList();
        float jointBefore = SkinWorkshop.Weight(mesh, vertex, joint);
        float otherBefore = SkinWorkshop.Weight(mesh, vertex, other);
        session.SetLock(otherInfluences, true);
        Assert.True(session.WeightList()[other].Locked);

        // With every other influence locked, the writer has nothing to take from and leaves the
        // painted joint alone; unlocking lets it grow again.
        session.SetJointWeight(joint, Math.Min(1f, jointBefore + 0.2f), positions);
        Assert.Equal(jointBefore, SkinWorkshop.Weight(mesh, vertex, joint), 3);
        Assert.Equal(otherBefore, SkinWorkshop.Weight(mesh, vertex, other), 3);
        session.SetLock(otherInfluences, false);
        session.SetJointWeight(joint, Math.Min(1f, jointBefore + 0.2f), positions);
        Assert.True(SkinWorkshop.Weight(mesh, vertex, joint) > jointBefore + 1e-3f);
        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    [SceneTheory, SceneSamples]
    public void Scanner_FindsTheNearestPointAndReadsItsWeights(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = FirstWeightedVertex(mesh, joint);
        if (vertex < 0 || positions.Length == 0) return;
        var session = new SkinPaintSession(character, mesh) { Joint = joint };

        var hit = session.Scan(positions[vertex], positions, radius: Diagonal(positions) * 0.02f);

        Assert.NotNull(hit);
        Assert.Equal(vertex, hit.Value.Vertex);
        Assert.Equal(vertex, session.CurrentVertex);
        Assert.Contains(hit.Value.Weights, w => w.Joint == joint);
        // The readout lists the point's non-zero joints only, sorted by descending weight.
        Assert.All(hit.Value.Weights, w => Assert.True(w.Weight > 0f));
        Assert.Equal(hit.Value.Weights.OrderByDescending(w => w.Weight).Select(w => w.Joint),
            hit.Value.Weights.Select(w => w.Joint));
        Assert.Equal(SkinWorkshop.Weights(mesh, vertex)
                .Where(w => w.Weight >= 1e-5f).Select(w => w.Bone).OrderBy(b => b),
            hit.Value.Weights.Select(w => w.Joint).OrderBy(b => b));
    }

    [SceneTheory, SceneSamples]
    public void FrontBack_RejectsPointsFacingAwayFromTheEye(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = FindPaintable(model);
        if (found == null) return;
        var (character, mesh, joint) = found.Value;
        var positions = BindPositions(character, mesh);
        int vertex = VertexWithRoom(mesh, joint);
        if (vertex < 0 || positions.Length == 0) return;
        var normals = Enumerable.Repeat(Vector3.UnitZ, positions.Length).ToArray();
        var brush = new SkinPaintBrush
        {
            Mode = SkinPaintMode.Add,
            Radius = Diagonal(positions) * 0.05f,
            Value = 0.5f,
            FrontBack = false,
        };

        // Every control point faces +Z: an eye behind them sees no front face.
        var back = SkinWorkshop.BeginStroke(mesh, joint, character: character);
        Assert.Equal(0, back.Apply(brush, positions[vertex], positions, null, normals, new Vector3(0, 0, -1e6f)));
        back.Commit();
        var front = SkinWorkshop.BeginStroke(mesh, joint, character: character);
        Assert.True(front.Apply(brush, positions[vertex], positions, null, normals, new Vector3(0, 0, 1e6f)) >= 1);
        front.Commit();

        AssertNormalized(character, mesh);
        AssertRoundtrip(model);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static int JointIndex(Character character, string name) =>
        Enumerable.Range(0, character.Joints.Count)
            .FirstOrDefault(i => character.Joints[i].Name == name, -1);

    private static (Character Character, Mesh Mesh, int Joint)? FindPaintable(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
        {
            if (character.Joints.Count == 0) continue;
            foreach (var mesh in character.Meshes)
            {
                if (!mesh.Skinned || mesh.ControlPoints.Length == 0) continue;
                var weights = mesh.SkinWeights;
                if (weights.Length < mesh.ControlPoints.Length) continue;
                for (int joint = 0; joint < character.Joints.Count; joint++)
                {
                    int vertex = FirstWeightedVertex(mesh, joint);
                    if (vertex >= 0) return (character, mesh, joint);
                }
            }
        }
        return null;
    }

    private static (Character Character, Mesh Mesh, int Source, int Partner)? FindMirrorPair(SceneModel model)
    {
        var match = MirrorMatch.Default;
        foreach (var character in model.Scene.Characters)
        {
            var joints = character.Joints;
            for (int i = 0; i < joints.Count; i++)
            {
                if (joints[i].Name is not { } name) continue;
                if (match.SideOf(name) is not (MirrorSide.Left or MirrorSide.Up)) continue;
                if (!match.TryMirror(name, out string mirrored, out _)) continue;
                int partner = -1;
                for (int j = 0; j < joints.Count; j++)
                    if (joints[j].Name == mirrored) { partner = j; break; }
                if (partner < 0) continue;
                foreach (var mesh in character.Meshes)
                {
                    if (!mesh.Skinned || mesh.ControlPoints.Length == 0) continue;
                    if (FirstWeightedVertex(mesh, i) < 0) continue;
                    return (character, mesh, i, partner);
                }
            }
        }
        return null;
    }

    private static Vector3[] BindPositions(Character character, Mesh mesh)
    {
        var skin = new MeshSkin(mesh);
        var worlds = new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0);
        return skin.Positions(worlds);
    }

    private static Vector3[] JointPositions(Character character) =>
        JointPositions(character, new PoseEvaluator(character).WorldMatrices(null, 0));

    private static Vector3[] BindJointPositions(Character character, Mesh mesh) =>
        JointPositions(character, new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0));

    private static Vector3[] JointPositions(Character character, Matrix4x4[] worlds)
    {
        var index = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < character.Controllers.Count; i++) index.TryAdd(character.Controllers[i], i);
        var result = new Vector3[character.Joints.Count];
        for (int j = 0; j < result.Length; j++)
            result[j] = index.TryGetValue(character.Joints[j], out int controller) && controller < worlds.Length
                ? worlds[controller].Translation
                : character.Joints[j].WorldMatrix.Translation;
        return result;
    }

    private static int FirstWeightedVertex(Mesh mesh, int joint)
    {
        var weights = mesh.SkinWeights;
        for (int v = 0; v < weights.Length; v++)
            foreach (var weight in weights[v])
                if (weight.Bone == joint && weight.Weight > 0f) return v;
        return -1;
    }

    /// <summary>A control point whose weight on the joint can still grow.</summary>
    private static int VertexWithRoom(Mesh mesh, int joint)
    {
        var weights = mesh.SkinWeights;
        for (int v = 0; v < weights.Length; v++)
            foreach (var weight in weights[v])
                if (weight.Bone == joint && weight.Weight is > 0f and < 0.95f) return v;
        return -1;
    }

    private static int FindVertexWithTwoInfluences(Mesh mesh, int joint)
    {
        var weights = mesh.SkinWeights;
        for (int v = 0; v < weights.Length; v++)
        {
            if (weights[v].Length < 2) continue;
            if (weights[v].Any(w => w.Bone == joint)) return v;
        }
        return FirstWeightedVertex(mesh, joint);
    }

    private static void AssertNormalized(Character character, Mesh mesh)
    {
        var weights = mesh.SkinWeights;
        Assert.Equal(mesh.ControlPoints.Length, weights.Length);
        for (int v = 0; v < weights.Length; v++)
        {
            if (weights[v].Length == 0) continue;
            float sum = weights[v].Sum(w => w.Weight);
            Assert.True(MathF.Abs(sum - 1f) < 1e-3f, $"control point {v} sums to {sum}");
            Assert.All(weights[v], w => Assert.InRange(w.Bone, 0, character.Joints.Count - 1));
        }
    }

    private static float Diagonal(Vector3[] points)
    {
        if (points.Length == 0) return 1f;
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return MathF.Max(1e-6f, Vector3.Distance(min, max));
    }

    private static int Nearest(Vector3[] points, Vector3 point, float tolerance)
    {
        int best = -1;
        float bestDistance = tolerance;
        for (int i = 0; i < points.Length; i++)
        {
            float distance = Vector3.Distance(points[i], point);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>The edited document writes a file that reads and writes back byte-exact.</summary>
    private static void AssertRoundtrip(SceneModel model)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-skinworkshop-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-skinworkshop-{Guid.NewGuid():N}.akt");
        try
        {
            model.Save(first);
            TestFiles.LoadScene(first).Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
