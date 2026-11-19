using System.Numerics;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Skinned Mesh Vertices Selection: the modifier rule, the
/// rectangle and lasso tests, and the Skin Workshop session's pick, region, grow, shrink, select
/// all, invert, front-only and hidden rules, the unlock of the selection's influences and the brush
/// limited to the selection. Projection here is a plain orthographic drop of Z.
/// </summary>
public class VertexSelectionTests
{
    private static readonly Func<Vector3, Vector2?> Project = p => new Vector2(p.X, p.Y);

    [Fact]
    public void OpFor_ShiftReplacesCtrlAddsAltRemoves()
    {
        Assert.Null(VertexSelectRegion.OpFor(false, false, false));
        Assert.Equal(VertexSelectOp.Replace, VertexSelectRegion.OpFor(true, false, false));
        Assert.Equal(VertexSelectOp.Add, VertexSelectRegion.OpFor(false, true, false));
        Assert.Equal(VertexSelectOp.Remove, VertexSelectRegion.OpFor(false, false, true));
        // Alt wins over Ctrl, Ctrl over Shift.
        Assert.Equal(VertexSelectOp.Remove, VertexSelectRegion.OpFor(true, true, true));
        Assert.Equal(VertexSelectOp.Add, VertexSelectRegion.OpFor(true, true, false));
    }

    [Fact]
    public void Rectangle_IgnoresCornerOrder()
    {
        var a = VertexSelectRegion.Rectangle(new Vector2(10, 20), new Vector2(0, 0));
        Assert.True(a(new Vector2(5, 5)));
        Assert.True(a(new Vector2(0, 20)));
        Assert.False(a(new Vector2(11, 5)));
        Assert.False(a(new Vector2(5, -1)));
    }

    [Fact]
    public void Lasso_IsAnEvenOddPolygonAndNeedsThreePoints()
    {
        var tri = VertexSelectRegion.Lasso([new(0, 0), new(10, 0), new(0, 10)]);
        Assert.True(tri(new Vector2(2, 2)));
        Assert.False(tri(new Vector2(8, 8)));
        Assert.False(VertexSelectRegion.Lasso([new(0, 0), new(10, 0)])(new Vector2(1, 0)));
        // A concave U: the notch is outside.
        var u = VertexSelectRegion.Lasso([new(0, 0), new(30, 0), new(30, 30), new(20, 30), new(20, 10), new(10, 10), new(10, 30), new(0, 30)]);
        Assert.True(u(new Vector2(5, 20)));
        Assert.False(u(new Vector2(15, 20)));
    }

    [SceneTheory, SceneSamples]
    public void Session_RegionAndPickReplaceAddAndRemove(string path)
    {
        if (Setup(path) is not { } s) return;
        var (session, mesh, positions, normals) = s;
        int visible = Visible(mesh);
        var all = VertexSelectRegion.Rectangle(new Vector2(-1e9f), new Vector2(1e9f));

        Assert.Equal(visible, session.SelectRegion(positions, normals, Project, all, VertexSelectOp.Replace));
        Assert.Equal(visible, session.Selection.SelectedCount);
        Assert.True(session.Selection.IsSelected(session.CurrentVertex));

        session.SelectRegion(positions, normals, Project, all, VertexSelectOp.Remove);
        Assert.Equal(0, session.Selection.SelectedCount);
        Assert.Equal(-1, session.CurrentVertex);

        int target = FirstVisible(mesh);
        var at = Project(positions[target])!.Value;
        int picked = session.PickVertex(positions, normals, Project, at, VertexSelectOp.Replace);
        Assert.True(picked >= 0);
        Assert.Equal(0f, Vector2.Distance(Project(positions[picked])!.Value, at));
        Assert.Equal(1, session.Selection.SelectedCount);
        Assert.Equal(picked, session.CurrentVertex);

        // Ctrl adds a second point, Alt removes the first, Shift on nothing clears.
        int other = Enumerable.Range(0, positions.Length).First(v => !mesh.IsVertexHidden(v) &&
            Vector2.Distance(Project(positions[v])!.Value, at) > VertexSelectRegion.PickTolerance * 2);
        var otherAt = Project(positions[other])!.Value;
        int second = session.PickVertex(positions, normals, Project, otherAt, VertexSelectOp.Add);
        Assert.Equal(2, session.Selection.SelectedCount);
        Assert.Equal(second, session.CurrentVertex);
        session.PickVertex(positions, normals, Project, at, VertexSelectOp.Remove);
        Assert.Equal(1, session.Selection.SelectedCount);
        Assert.True(session.Selection.IsSelected(second));
        Assert.Equal(-1, session.PickVertex(positions, normals, Project, new Vector2(1e9f), VertexSelectOp.Replace));
        Assert.Equal(0, session.Selection.SelectedCount);
    }

    [SceneTheory, SceneSamples]
    public void Session_GrowShrinkSelectAllAndInvert(string path)
    {
        if (Setup(path) is not { } s) return;
        var (session, mesh, positions, normals) = s;
        int visible = Visible(mesh);
        var neighbours = SkinWorkshop.Neighbours(mesh);
        int seed = Enumerable.Range(0, positions.Length).First(v => !mesh.IsVertexHidden(v) && neighbours[v].Length > 0);
        session.Selection.Set(seed, true);
        session.CurrentVertex = seed;

        int grown = session.GrowSelection();
        Assert.True(grown > 0);
        Assert.Equal(1 + grown, session.Selection.SelectedCount);
        foreach (int v in session.Selection.SelectedVertices())
            Assert.True(v == seed || neighbours[seed].Contains(v));

        session.ShrinkSelection();
        Assert.True(session.Selection.IsSelected(seed));
        Assert.True(session.Selection.SelectedCount <= 1 + grown);

        session.Selection.Clear();
        Assert.Equal(visible, session.SelectAllVertices());

        session.Selection.Clear();
        session.Selection.Set(seed, true);
        Assert.Equal(visible - 1, session.InvertSelection());
        Assert.False(session.Selection.IsSelected(seed));
    }

    [SceneTheory, SceneSamples]
    public void Session_SkipsHiddenAndBackFacingPoints(string path)
    {
        if (Setup(path) is not { } s) return;
        var (session, mesh, positions, normals) = s;
        var all = VertexSelectRegion.Rectangle(new Vector2(-1e9f), new Vector2(1e9f));
        int hidden = FirstVisible(mesh);
        mesh.SetVerticesHidden([hidden], true);

        session.SelectRegion(positions, normals, Project, all, VertexSelectOp.Replace);
        Assert.False(session.Selection.IsSelected(hidden));
        session.Selection.Clear();
        session.InvertSelection();
        Assert.False(session.Selection.IsSelected(hidden));

        // Full Paint & Vertex Select Mode off: only points facing the eye (+Z, far away) are taken.
        session.FrontBackSelect = false;
        session.Eye = new Vector3(0, 0, 1e7f);
        session.SelectRegion(positions, normals, Project, all, VertexSelectOp.Replace);
        int back = 0;
        for (int v = 0; v < positions.Length; v++)
        {
            bool front = SkinPaintStroke.FrontFacing(positions[v], normals[v], session.Eye);
            if (session.Selection.IsSelected(v)) Assert.True(front);
            else if (!front) back++;
        }
        Assert.True(session.Selection.SelectedCount + back + 1 >= Visible(mesh));
    }

    [SceneTheory, SceneSamples]
    public void Session_SelectingUnlocksInfluencesAndLimitsTheBrush(string path)
    {
        if (Setup(path) is not { } s) return;
        var (session, mesh, positions, normals) = s;
        session.SetLock(Enumerable.Range(0, session.Character.Joints.Count), true);
        int vertex = Enumerable.Range(0, positions.Length).First(v => !mesh.IsVertexHidden(v) &&
            SkinWorkshop.Weights(mesh, v, session.Rigid).Any(w => w.Weight > 1e-5f));
        session.PickVertex(positions, normals, Project, Project(positions[vertex])!.Value, VertexSelectOp.Replace, tolerance: 0f);
        var picked = session.CurrentVertex;
        foreach (var (bone, weight) in SkinWorkshop.Weights(mesh, picked, session.Rigid))
            if (weight > 1e-5f) Assert.False(session.IsLocked(bone));

        // The brush paints only the selection: a dab away from it changes nothing.
        Assert.Same(session.Selection, session.Brush.LimitTo);
        int far = Enumerable.Range(0, positions.Length)
            .OrderByDescending(v => Vector3.Distance(positions[v], positions[picked])).First();
        session.SetLock(Enumerable.Range(0, session.Character.Joints.Count), false);
        float before = SkinWorkshop.Weight(mesh, far, session.Joint);
        session.Radius = Vector3.Distance(positions[far], positions[picked]) * 0.1f;
        Assert.True(session.BeginStroke());
        int painted = session.Dab(positions[far], positions, JointPositions(session.Character, mesh));
        session.EndStroke();
        Assert.Equal(0, painted);
        Assert.Equal(before, SkinWorkshop.Weight(mesh, far, session.Joint));
    }

    private static int Visible(Mesh mesh) =>
        Enumerable.Range(0, mesh.ControlPoints.Length).Count(v => !mesh.IsVertexHidden(v));

    private static int FirstVisible(Mesh mesh) =>
        Enumerable.Range(0, mesh.ControlPoints.Length).First(v => !mesh.IsVertexHidden(v));

    private static (SkinPaintSession Session, Mesh Mesh, Vector3[] Positions, Vector3[] Normals)? Setup(string path)
    {
        var model = TestFiles.LoadScene(path);
        foreach (var character in model.Scene.Characters)
        {
            if (character.Joints.Count == 0) continue;
            foreach (var mesh in character.Meshes)
            {
                if (!mesh.Skinned || mesh.ControlPoints.Length < 8 || mesh.SkinWeights.Length < mesh.ControlPoints.Length) continue;
                var skin = new MeshSkin(mesh);
                var worlds = new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0);
                var session = new SkinPaintSession(character, mesh) { Joint = 0 };
                return (session, mesh, skin.Positions(worlds), skin.Normals(worlds));
            }
        }
        return null;
    }

    private static Vector3[] JointPositions(Character character, Mesh mesh)
    {
        var worlds = new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0);
        var index = new Dictionary<Object3D, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < character.Controllers.Count; i++) index.TryAdd(character.Controllers[i], i);
        var result = new Vector3[character.Joints.Count];
        for (int j = 0; j < result.Length; j++)
            result[j] = index.TryGetValue(character.Joints[j], out int controller) && controller < worlds.Length
                ? worlds[controller].Translation
                : character.Joints[j].WorldMatrix.Translation;
        return result;
    }
}
