using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The AutoRigger dialog's headless session: the fifteen
/// hot-point rows, the points a build requires for a given finger count, what it refuses and the
/// build itself. The viewer only draws these rules.
/// </summary>
public class AutoRiggerSessionTests
{
    [Fact]
    public void Parts_AreTheFifteenDragLabels()
    {
        Assert.Equal(15, AutoRiggerParts.Rows.Count);
        Assert.Equal(
            new[] { "Pelvis", "Neck", "Ankle", "Shoulder", "Wrist", "Thumb Tip", "Index Tip",
                    "Middle Tip", "Ring Tip", "Little Tip", "Finger_6 Tip", "Finger_7 Tip",
                    "Finger_8 Tip", "Finger_9 Tip", "Finger_10 Tip" },
            AutoRiggerParts.Rows.Select(row => row.Label));

        // Pelvis and Neck are central; every other row has a left and a right point.
        Assert.Equal(2, AutoRiggerParts.Rows.Count(row => !row.HasSides));
        Assert.All(AutoRiggerParts.Rows.Where(row => !row.HasSides), row => Assert.NotNull(row.Centre));
        Assert.All(AutoRiggerParts.Rows.Where(row => row.HasSides), row =>
        {
            Assert.Null(row.Centre);
            Assert.NotNull(row.Left);
            Assert.NotNull(row.Right);
            Assert.NotEqual(row.Left, row.Right);
        });

        // Every one of the 28 points has a row and a side-bearing name.
        var points = AutoRiggerParts.Rows.SelectMany(row => row.Points).ToList();
        Assert.Equal(28, points.Count);
        Assert.Equal(28, points.Distinct().Count());
        Assert.Equal("Pelvis", AutoRiggerParts.Describe(HotPoint.Pelvis));
        Assert.Equal("Shoulder L", AutoRiggerParts.Describe(HotPoint.ShoulderLeft));
        Assert.Equal("Finger_6 Tip R", AutoRiggerParts.Describe(HotPoint.Finger6Right));
    }

    [Fact]
    public void Required_FollowsTheFingerCount()
    {
        if (Sample(withJoints: true) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character) { Fingers = 5 };

        // The two central points, the three fixed side pairs and both tips of every finger.
        Assert.Equal(8 + 2 * 5, session.Required.Count);
        Assert.Contains(HotPoint.Pelvis, session.Required);
        Assert.Contains(HotPoint.AnkleRight, session.Required);
        Assert.Contains(HotPoint.LittleLeft, session.Required);
        Assert.DoesNotContain(HotPoint.Finger6Left, session.Required);
        Assert.Equal(session.Required.Count, session.Missing.Count);

        session.Fingers = 3;
        Assert.Equal(8 + 6, session.Required.Count);
        Assert.DoesNotContain(HotPoint.RingLeft, session.Required);

        session.Fingers = 7;
        Assert.Equal(8 + 14, session.Required.Count);
        Assert.Contains(HotPoint.Finger7Right, session.Required);
        Assert.DoesNotContain(HotPoint.Finger8Left, session.Required);

        // The counts are the ranges, clamped rather than refused.
        session.Fingers = 99;
        Assert.Equal(10, session.Fingers);
        session.SpineJoints = 0;
        Assert.Equal(1, session.SpineJoints);
        session.PhalanxJoints = 9;
        Assert.Equal(4, session.PhalanxJoints);
        Assert.Equal(10, session.Settings.Fingers);
    }

    [Fact]
    public void PlaceRemoveClear_TrackThePoints()
    {
        if (Sample(withJoints: true) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character);
        Assert.Null(session.PositionOf(HotPoint.Pelvis));

        session.Place(HotPoint.Pelvis, new Vector3(1, 2, 3));
        Assert.Equal(new Vector3(1, 2, 3), session.PositionOf(HotPoint.Pelvis));
        Assert.DoesNotContain(HotPoint.Pelvis, session.Missing);

        // Placing again replaces the point.
        session.Place(HotPoint.Pelvis, new Vector3(4, 5, 6));
        Assert.Equal(new Vector3(4, 5, 6), session.PositionOf(HotPoint.Pelvis));
        Assert.Single(session.Points);

        Assert.True(session.Remove(HotPoint.Pelvis));
        Assert.False(session.Remove(HotPoint.Pelvis));
        Assert.Empty(session.Points);
        session.Place(HotPoint.Neck, Vector3.UnitY);
        session.Clear();
        Assert.Empty(session.Points);
    }

    [Fact]
    public void Build_PlacesTheHotPointsAndRefusesAMissingOne()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 5 };
        Assert.Contains("Place the missing hot points", session.Refusal);
        Assert.False(session.CanBuild);

        foreach (var (point, position) in HotPoints(mesh)) session.Place(point, position);
        Assert.Empty(session.Missing);
        Assert.Null(session.Refusal);
        Assert.True(session.CanBuild);

        AutoRigResult result = session.Build();
        Assert.Equal(51, result.Joints);
        Assert.Equal(69, result.Controllers);
        Assert.Equal(2, result.IkControllers);
        Assert.Equal(2, result.ReverseFootControllers);
        Assert.Equal(51, character.Joints.Count);
        Assert.Same(character, mesh.Ancestor<Character>());
    }

    [Fact]
    public void Build_RefusesACharacterThatAlreadyHasJoints()
    {
        if (Sample(withJoints: true) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]);
        Assert.False(session.CanBuild);
        Assert.Contains("already has", session.Refusal);
        Assert.Contains("unrigged", session.Refusal);
        var error = Assert.Throws<InvalidOperationException>(() => session.Build());
        Assert.Contains("already has", error.Message);
    }

    [Fact]
    public void Meshes_AreKeptDistinctAndMayBeEmptied()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh, mesh]);
        Assert.Single(session.Meshes);
        session.SetMeshes([]);
        Assert.Empty(session.Meshes);
        Assert.Contains("Check at least one mesh", session.Refusal);
    }

    // ---- the finger count trims the hot points
    // -------------------------------------------------------------------------

    [Fact]
    public void Fingers_ShrinkingDropsTheTailingFingerPairs()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 10 };
        foreach (var (point, position) in HotPoints(mesh)) session.Place(point, position);
        Assert.Equal(28, session.Points.Count);

        session.Fingers = 3;

        // The action keeps the first FingerNumber + 4 = 5 + F drag labels: the five fixed
        // ones and one tip per finger. Its labels are the rows, so SloppyAnimatsu keeps both sides of
        // those rows: 8 + 2F points. The trailing finger pairs are the ones that go.
        Assert.Equal(8 + 2 * 3, session.Points.Count);
        Assert.Equal(8 + 2 * 3, session.Required.Count);
        Assert.All(AutoRiggerParts.Rows.Take(5 + 3).SelectMany(row => row.Points),
            point => Assert.NotNull(session.PositionOf(point)));
        Assert.All(AutoRiggerParts.Rows.Skip(5 + 3).SelectMany(row => row.Points),
            point => Assert.Null(session.PositionOf(point)));

        // The central and the fixed side points survive; the ring and little tips do not.
        Assert.NotNull(session.PositionOf(HotPoint.Pelvis));
        Assert.NotNull(session.PositionOf(HotPoint.AnkleRight));
        Assert.NotNull(session.PositionOf(HotPoint.MiddleLeft));
        Assert.Null(session.PositionOf(HotPoint.RingLeft));
        Assert.Null(session.PositionOf(HotPoint.LittleRight));
        Assert.Null(session.PositionOf(HotPoint.Finger10Left));
        Assert.Empty(session.Missing);

        // Growing the count adds no point back: only the configured rows are required again.
        session.Fingers = 5;
        Assert.Equal(8 + 2 * 3, session.Points.Count);
        Assert.Equal(8 + 2 * 5, session.Required.Count);
        Assert.Equal(4, session.Missing.Count);

        // One more shrink trims the now-unplaced rows only; placed ones stay.
        session.Fingers = 2;
        Assert.Equal(8 + 2 * 2, session.Points.Count);
        Assert.Null(session.PositionOf(HotPoint.MiddleLeft));
        Assert.NotNull(session.PositionOf(HotPoint.IndexRight));
    }

    [Fact]
    public void Fingers_GrowingDoesNotTouchThePlacedPoints()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 2 };
        session.Place(HotPoint.Pelvis, Vector3.Zero);
        session.Place(HotPoint.RingLeft, Vector3.One);
        Assert.Equal(2, session.Points.Count);

        session.Fingers = 9;
        Assert.Equal(2, session.Points.Count);
        Assert.NotNull(session.PositionOf(HotPoint.RingLeft));

        // A shrink below the placed point removes it; a shrink that keeps it does not.
        session.Fingers = 4;
        Assert.NotNull(session.PositionOf(HotPoint.RingLeft));
        session.Fingers = 3;
        Assert.Null(session.PositionOf(HotPoint.RingLeft));
        Assert.Equal(Vector3.Zero, session.PositionOf(HotPoint.Pelvis));
    }

    // ---- the hot-point mirroring gesture
    // ----------------------------

    [Fact]
    public void Mirror_CopiesEveryPlacedSidePointOntoItsCounterpart()
    {
        if (Sample(withJoints: false) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character);
        float x = 1f;
        foreach (AutoRigPartRow row in AutoRiggerParts.Rows.Where(row => row.HasSides))
        {
            session.Place(row.Left!.Value, new Vector3(x, x * 2, x * 3));
            x += 1f;
        }
        session.Place(HotPoint.Pelvis, new Vector3(9, 8, 7));
        session.Place(HotPoint.Neck, new Vector3(-1, -2, -3));

        AutoRigMirrorResult result = session.MirrorLeftToRight();

        // Thirteen side rows - the fifteen labels without the two central points - and nothing had
        // a counterpart yet.
        Assert.Equal(AutoRigSide.Left, result.From);
        Assert.Equal(AutoRigSide.Right, result.To);
        Assert.Equal(13, result.Mirrored);
        Assert.Equal(0, result.Replaced);
        float expectedX = 1f;
        foreach (AutoRigPartRow row in AutoRiggerParts.Rows.Where(row => row.HasSides))
        {
            AssertNear(new Vector3(-expectedX, expectedX * 2, expectedX * 3), session.PositionOf(row.Right!.Value));
            expectedX += 1f;
        }

        // The central points are never mirrored, and only the thirteen counterparts were added on
        // top of the thirteen left points and the two central ones.
        AssertNear(new Vector3(9, 8, 7), session.PositionOf(HotPoint.Pelvis));
        AssertNear(new Vector3(-1, -2, -3), session.PositionOf(HotPoint.Neck));
        Assert.Equal(28, session.Points.Count);
    }

    [Fact]
    public void Mirror_LeavesCentralPointsAndUnplacedCounterpartsAlone()
    {
        if (Sample(withJoints: false) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character);
        session.Place(HotPoint.ShoulderLeft, new Vector3(2, 3, 4));
        session.Place(HotPoint.WristLeft, new Vector3(5, 6, 7));
        // A right-only point whose left source is unplaced: mirroring the left side must not touch it.
        session.Place(HotPoint.AnkleRight, new Vector3(-7, -8, -9));
        session.Place(HotPoint.Pelvis, new Vector3(1, 1, 1));
        session.Place(HotPoint.Neck, new Vector3(0, 10, 0));

        AutoRigMirrorResult result = session.MirrorLeftToRight();

        Assert.Equal(2, result.Mirrored);
        Assert.Equal(0, result.Replaced);
        AssertNear(new Vector3(-2, 3, 4), session.PositionOf(HotPoint.ShoulderRight));
        AssertNear(new Vector3(-5, 6, 7), session.PositionOf(HotPoint.WristRight));
        AssertNear(new Vector3(-7, -8, -9), session.PositionOf(HotPoint.AnkleRight));
        Assert.Null(session.PositionOf(HotPoint.IndexRight));
        AssertNear(new Vector3(1, 1, 1), session.PositionOf(HotPoint.Pelvis));
        AssertNear(new Vector3(0, 10, 0), session.PositionOf(HotPoint.Neck));
    }

    [Fact]
    public void Mirror_OverwritesAnAlreadyPlacedCounterpart()
    {
        if (Sample(withJoints: false) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character);
        session.Place(HotPoint.ShoulderLeft, new Vector3(2, 3, 4));
        session.Place(HotPoint.ShoulderRight, new Vector3(-7, -8, -9));

        AutoRigMirrorResult result = session.MirrorLeftToRight();

        // Last write wins: the mirror action writes the counterpart's key, it does not refuse
        // because the counterpart has a value.
        Assert.Equal(1, result.Mirrored);
        Assert.Equal(1, result.Replaced);
        AssertNear(new Vector3(-2, 3, 4), session.PositionOf(HotPoint.ShoulderRight));

        // The other direction mirrors back onto the left point.
        session.MirrorRightToLeft();
        AssertNear(new Vector3(2, 3, 4), session.PositionOf(HotPoint.ShoulderLeft));
    }

    [Fact]
    public void Mirror_TwiceIsIdempotent()
    {
        if (Sample(withJoints: false) is not var (_, character, _)) return;
        var session = new AutoRiggerSession(character);
        float x = 1f;
        foreach (AutoRigPartRow row in AutoRiggerParts.Rows.Where(row => row.HasSides))
        {
            session.Place(row.Left!.Value, new Vector3(x, x * 2, x * 3));
            x += 1f;
        }
        session.MirrorLeftToRight();
        var once = session.Points.ToDictionary(pair => pair.Key, pair => pair.Value);

        AutoRigMirrorResult again = session.MirrorLeftToRight();

        // Reflecting the same source again writes the same positions; every counterpart is replaced.
        Assert.Equal(13, again.Mirrored);
        Assert.Equal(13, again.Replaced);
        Assert.Equal(once.Count, session.Points.Count);
        foreach (var (point, position) in once)
            AssertNear(position, session.Points.TryGetValue(point, out var now) ? now : null);
    }

    [Fact]
    public void Mirror_UsesTheCharactersLocalPlane()
    {
        if (Sample(withJoints: false) is not var (_, character, _)) return;
        // The rule is the character's local YZ plane, not the world's: with the character moved to
        // x = 10 the mirror of x = 11 is x = 9, not x = -11.
        character.LocalMatrix = Matrix4x4.CreateTranslation(10, 0, 0);
        var session = new AutoRiggerSession(character);
        session.Place(HotPoint.WristLeft, new Vector3(11, 2, 3));

        session.MirrorLeftToRight();

        AssertNear(new Vector3(9, 2, 3), session.PositionOf(HotPoint.WristRight));
    }

    [Fact]
    public void Build_FromAMirroredSide()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 5 };
        var points = HotPoints(mesh);
        session.Place(HotPoint.Pelvis, points[HotPoint.Pelvis]);
        session.Place(HotPoint.Neck, points[HotPoint.Neck]);
        foreach (HotPoint point in session.Required)
            if (point != HotPoint.Pelvis && point != HotPoint.Neck && AutoRiggerParts.RowOf(point).Left == point)
                session.Place(point, points[point]);
        Assert.NotEmpty(session.Missing);

        session.MirrorLeftToRight();

        // Only the left side was placed: mirroring completes the point set the build needs.
        Assert.Empty(session.Missing);
        Assert.True(session.CanBuild);
        AutoRigResult result = session.Build();
        Assert.Equal(51, result.Joints);
        Assert.Equal(mesh.ControlPoints.Length, result.WeightedPoints);
    }

    // ---- the smooth-again prompt -------

    [Fact]
    public void MustChooseWeightVersion_NeedsBothFlags()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]);
        Assert.False(session.MustChooseWeightVersion);
        Assert.Equal(AutoRigWeightVersion.Rigid, session.WeightVersion);
        Assert.False(session.Settings.KeepWeights);

        character.RigidModified = true;
        Assert.True(character.RigidModified);
        Assert.False(session.MustChooseWeightVersion);

        character.SmoothModified = true;
        Assert.True(session.MustChooseWeightVersion);

        session.WeightVersion = AutoRigWeightVersion.Smooth;
        Assert.True(session.Settings.KeepWeights);
        Assert.True(session.Settings.GenerateWeights);

        character.RigidModified = false;
        Assert.False(session.MustChooseWeightVersion);

        // The two answers, in its order.
        Assert.Equal(new[] { "Rigid", "Smooth" },
            AutoRigSmoothAgainPrompt.Choices.Select(choice => choice.Label));
        Assert.Equal(new[] { AutoRigWeightVersion.Rigid, AutoRigWeightVersion.Smooth },
            AutoRigSmoothAgainPrompt.Choices.Select(choice => choice.Version));
        Assert.Equal("RIGID AND SMOOTH MODIFIED", AutoRigSmoothAgainPrompt.Title);
    }

    [Fact]
    public void Build_SmoothKeepsTheHandEditedWeightArrays()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 5 };
        foreach (var (point, position) in HotPoints(mesh)) session.Place(point, position);
        var smooth = mesh.SkinWeights;
        var rigid = mesh.RigidSkinWeights;
        Assert.NotEmpty(smooth);
        Assert.NotEmpty(rigid);

        character.RigidModified = true;
        character.SmoothModified = true;
        Assert.True(session.MustChooseWeightVersion);
        session.WeightVersion = AutoRigWeightVersion.Smooth;
        AutoRigResult result = session.Build();

        // The "Smooth" answer skips the generator, and SloppyAnimatsu's Bind Skin must not rewrite
        // the weights: the values the user edited are exactly what the mesh keeps. The joint
        // indices are mapped onto the new rig by name (AutoRiggerTests covers that rule), so only
        // the influence weights and their counts are compared here.
        AssertSameWeightValues(smooth, mesh.SkinWeights);
        AssertSameWeightValues(rigid, mesh.RigidSkinWeights);
        Assert.Equal(0, result.WeightedPoints);

        // The mesh is still rebound to the new rig: the bind worker runs for both answers.
        Assert.True(mesh.Skinned);
        Assert.True(mesh.SkinnedWithAkeytsu);
        Assert.True(mesh.SmoothSkinningProcessed);
        Assert.Same(character, mesh.Ancestor<Character>());
        Assert.NotNull(mesh.BindPose);
        Assert.Same(character, mesh.BindPose!.Character);
        Assert.Equal(character.Controllers.Count, mesh.BindPose.Count);
        Assert.False(character.RigidModified);
        Assert.False(character.SmoothModified);
    }

    [Fact]
    public void Build_RigidSolvesTheWeightsAgain()
    {
        if (Sample(withJoints: false) is not var (_, character, mesh)) return;
        var session = new AutoRiggerSession(character, [mesh]) { Fingers = 5 };
        foreach (var (point, position) in HotPoints(mesh)) session.Place(point, position);
        var rigid = mesh.RigidSkinWeights;
        character.RigidModified = true;
        character.SmoothModified = true;
        Assert.True(session.MustChooseWeightVersion);
        session.WeightVersion = AutoRigWeightVersion.Rigid;

        AutoRigResult result = session.Build();

        Assert.Equal(mesh.ControlPoints.Length, result.WeightedPoints);
        // The "Rigid" answer solves again, and Bind Skin's rigid rule is one influence of
        // weight 1 per point whatever the old array said.
        Assert.All(mesh.RigidSkinWeights, weights =>
        {
            VertexWeight influence = Assert.Single(weights);
            Assert.Equal(1f, influence.Weight);
        });
        // At least one point was relabelled by the new rig's bones, so the array was really solved.
        Assert.Contains(Enumerable.Range(0, rigid.Length),
            i => rigid[i][0].Bone != mesh.RigidSkinWeights[i][0].Bone);
    }

    /// <summary>A placed position is where the rule put it, allowing for a float round trip.</summary>
    private static void AssertNear(Vector3 expected, Vector3? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Vector3.Distance(expected, actual!.Value) < 1e-4f, $"{expected} vs {actual!.Value}");
    }

    /// <summary>The same influence weights per point, whatever joint each now names.</summary>
    private static void AssertSameWeightValues(VertexWeight[][] expected, VertexWeight[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Length, actual[i].Length);
            for (int j = 0; j < expected[i].Length; j++)
                Assert.Equal(expected[i][j].Weight, actual[i][j].Weight);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>`4_...`'s unrigged Default character for a build; any character for the rules.</summary>
    private static (SceneModel Model, Character Character, Mesh Mesh)? Sample(bool withJoints)
    {
        if (!withJoints)
        {
            var path = TestFiles.AllSceneSamples.FirstOrDefault(
                p => Path.GetFileName(p) == "4_CenteredRootAddedHipsRenamedSome.akt");
            if (path == null) return null;
            var model = TestFiles.LoadScene(path);
            var character = model.Scene.Characters.FirstOrDefault(c => c.Name == "Default character" && c.Joints.Count == 0);
            var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Name == "Dup_b") ?? model.Scene.Meshes.FirstOrDefault();
            return character == null || mesh == null ? null : (model, character, mesh);
        }
        var rigged = TestFiles.SceneSamples.FirstOrDefault();
        if (rigged == null) return null;
        var riggedModel = TestFiles.LoadScene(rigged);
        var riggedCharacter = riggedModel.Scene.Characters.FirstOrDefault();
        var riggedMesh = riggedCharacter?.Meshes.FirstOrDefault() ?? riggedModel.Scene.Meshes.FirstOrDefault();
        return riggedCharacter == null || riggedMesh == null ? null : (riggedModel, riggedCharacter, riggedMesh);
    }

    /// <summary>Plausible hot points from the mesh bounds, as AutoRiggerTests uses.</summary>
    private static Dictionary<HotPoint, Vector3> HotPoints(Mesh mesh)
    {
        var world = mesh.ControlPoints.Select(p => Vector3.Transform(p, mesh.WorldMatrix)).ToArray();
        var min = new Vector3(world.Min(p => p.X), world.Min(p => p.Y), world.Min(p => p.Z));
        var max = new Vector3(world.Max(p => p.X), world.Max(p => p.Y), world.Max(p => p.Z));
        var center = (min + max) * 0.5f;
        float height = max.Y - min.Y;
        float width = max.X - min.X;
        var points = new Dictionary<HotPoint, Vector3>
        {
            [HotPoint.Pelvis] = new(center.X, min.Y + 0.54f * height, center.Z),
            [HotPoint.Neck] = new(center.X, min.Y + 0.86f * height, center.Z),
        };
        foreach (bool left in new[] { true, false })
        {
            float side = left ? -1f : 1f;
            float shoulderX = center.X + side * 0.18f * width;
            float wristX = center.X + side * 0.42f * width;
            var shoulder = new Vector3(shoulderX, min.Y + 0.82f * height, center.Z);
            var wrist = new Vector3(wristX, min.Y + 0.60f * height, center.Z);
            points[left ? HotPoint.ShoulderLeft : HotPoint.ShoulderRight] = shoulder;
            points[left ? HotPoint.WristLeft : HotPoint.WristRight] = wrist;
            points[left ? HotPoint.AnkleLeft : HotPoint.AnkleRight] =
                new Vector3(center.X + side * 0.10f * width, min.Y + 0.06f * height, center.Z);
            for (int finger = 0; finger < 10; finger++)
            {
                var tip = wrist + new Vector3(side * 0.05f * width, -0.03f * height,
                    (finger - 4.5f) * 0.012f * height);
                points[AutoRigger.FingerPoint(finger, left)] = tip;
            }
        }
        return points;
    }
}

