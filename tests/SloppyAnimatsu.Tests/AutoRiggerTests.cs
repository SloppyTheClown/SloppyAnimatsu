using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The AutoRigger's first slice: a biped skeleton with the three
/// naming sets, its IK and reverse-foot controllers, nearest-bone weights and Bind Skin, on a
/// character that has no joints yet. There is no reference AutoRigger scene to compare with;
/// these tests check the structure SloppyAnimatsu promises and a byte-exact save/reload.
/// </summary>
public class AutoRiggerTests
{
    [Fact]
    public void Build_CreatesThePreferencesBiped()
    {
        if (Sample() is not var (model, character, mesh)) return;
        Assert.Empty(character.Joints);
        var world = mesh.WorldMatrix;
        var points = HotPoints(mesh);

        var result = new AutoRigger(character).Build([mesh], points);

        Assert.Equal(51, result.Joints);
        Assert.Equal(69, result.Controllers);
        Assert.Equal(2, result.IkControllers);
        Assert.Equal(2, result.ReverseFootControllers);
        Assert.Equal(mesh.ControlPoints.Length, result.WeightedPoints);
        var names = character.Joints.Select(j => j.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "Root", "Pelvis", "Stomach", "Chest", "UpperChest", "Neck", "Head",
                     "Shoulder_L", "Arm_L", "Forearm_L", "Hand_L",
                     "Shoulder_R", "Arm_R", "Forearm_R", "Hand_R",
                     "Thigh_L", "Calf_L", "Foot_L", "Toe_L",
                     "Thigh_R", "Calf_R", "Foot_R", "Toe_R",
                     "Thumb_01_L", "Thumb_02_L", "Index_01_L", "Index_02_L", "Index_03_L",
                     "Middle_01_L", "Ring_01_L", "Little_01_L",
                 })
            Assert.Contains(name, names);
        Assert.DoesNotContain("Thumb_03_L", names);
        Assert.DoesNotContain("Index_04_L", names);

        Joint J(string name) => character.Joints.Single(j => j.Name == name);
        Assert.Same(character, J("Root").Parent);
        Assert.Same(J("Root"), J("Pelvis").Parent);
        Assert.Same(J("Pelvis"), J("Stomach").Parent);
        Assert.Same(J("Stomach"), J("Chest").Parent);
        Assert.Same(J("Chest"), J("UpperChest").Parent);
        Assert.Same(J("UpperChest"), J("Neck").Parent);
        Assert.Same(J("Neck"), J("Head").Parent);
        Assert.Same(J("UpperChest"), J("Shoulder_L").Parent);
        Assert.Same(J("Shoulder_L"), J("Arm_L").Parent);
        Assert.Same(J("Arm_L"), J("Forearm_L").Parent);
        Assert.Same(J("Forearm_L"), J("Hand_L").Parent);
        Assert.Same(J("Hand_L"), J("Index_01_L").Parent);
        Assert.Same(J("Pelvis"), J("Thigh_L").Parent);
        Assert.Same(J("Thigh_L"), J("Calf_L").Parent);
        Assert.Same(J("Calf_L"), J("Foot_L").Parent);
        Assert.Same(J("Foot_L"), J("Toe_L").Parent);

        // Every pose and the rig animation know the new controllers.
        var editor = new RigEditor(character);
        Assert.All(editor.Poses(), pose => Assert.Equal(character.Controllers.Count, pose.Count));
        var rig = character.RigAnimation!.Stacks.First();
        Assert.Equal(character.Controllers.Count, rig.Tracks.Count());

        var arms = character.Controllers.OfType<ControllerIK>().Where(c => c is not ControllerReverseFoot).ToList();
        Assert.Equal(2, arms.Count);
        Assert.All(arms, c => Assert.Equal(3, c.Chain.Count));
        var feet = character.Controllers.OfType<ControllerReverseFoot>().ToList();
        Assert.Equal(2, feet.Count);
        Assert.All(feet, c => Assert.Equal(4, c.Chain.Count));

        // The weights cover every control point and reference the new joints.
        var weights = mesh.SkinWeights;
        Assert.Equal(mesh.ControlPoints.Length, weights.Length);
        foreach (var entry in weights)
        {
            Assert.InRange(entry.Sum(w => w.Weight), 0.999f, 1.001f);
            Assert.All(entry, w => Assert.InRange(w.Bone, 0, character.Joints.Count - 1));
        }
        Assert.Equal(mesh.ControlPoints.Length, mesh.RigidSkinWeights.Length);

        // The mesh is attached and bound to this character's bind pose, keeping its world.
        Assert.Same(character, mesh.Ancestor<Character>());
        Assert.True(mesh.Skinned);
        var bind = Assert.Single(character.BindPoses);
        Assert.Same(bind, mesh.BindPose);
        Assert.Equal(character.Controllers.Count, bind.Count);
        AssertSameWorld(world, mesh.WorldMatrix);

        // At the bind pose the skin is the identity, so the mesh keeps the shape it was bound in.
        var skin = new MeshSkin(mesh);
        Assert.True(skin.IsSkinned);
        var bindWorlds = new PoseEvaluator(character, restPose: mesh.BindPose).WorldMatrices(null, 0);
        var skinned = skin.Positions(bindWorlds);
        var stored = mesh.ControlPoints.Select(p => Vector3.Transform(p, mesh.WorldMatrix)).ToArray();
        Assert.Equal(stored.Length, skinned.Length);
        for (int i = 0; i < stored.Length; i++)
            Assert.True(Vector3.Distance(stored[i], skinned[i]) < 1e-3f, $"point {i}");

        AssertRoundtrip(model);
    }

    [Fact]
    public void Build_HonoursTheNamingSetAndTheCounts()
    {
        if (Sample() is not var (model, character, mesh)) return;
        var points = HotPoints(mesh);
        var settings = new AutoRigSettings
        {
            Naming = AutoRigNaming.Default,
            SpineJoints = 2,
            Fingers = 3,
            PhalanxJoints = 2,
            AddIK = false,
            AddReverseFoot = false,
            BindSkin = false,
            GenerateWeights = false,
        };

        var result = new AutoRigger(character).Build([mesh], points, settings);

        Assert.Equal(32, result.Joints);
        Assert.Equal(32, result.Controllers);
        Assert.Equal(0, result.IkControllers);
        Assert.Equal(0, result.ReverseFootControllers);
        var names = character.Joints.Select(j => j.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "root", "pelvis", "spine_01", "spine_02", "neck_01", "head",
                     "clavicle_L", "upperArm_L", "lowerArm_L", "hand_L",
                     "thigh_L", "calf_L", "foot_L", "ball_L",
                     "thumb_01_L", "index_01_L", "index_02_L",
                     "middle_01_L", "middle_02_L",
                 })
            Assert.Contains(name, names);
        Assert.Empty(character.Controllers.OfType<ControllerIK>());
        Assert.False(mesh.Skinned);
        AssertRoundtrip(model);
    }

    [Fact]
    public void Build_UsesTheMixamoNames()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var settings = new AutoRigSettings
        {
            Naming = AutoRigNaming.Mixamo,
            AddIK = false,
            AddReverseFoot = false,
            BindSkin = false,
            GenerateWeights = false,
        };
        new AutoRigger(character).Build([mesh], HotPoints(mesh), settings);
        var names = character.Joints.Select(j => j.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "Root", "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head",
                     "Shoulder_L", "UpperArm_L", "LowerArm_L", "Hand_L",
                     "UpperLeg_L", "LowerLeg_L", "Foot_L", "Toes_L",
                     "Index_Proximal_L", "Index_Intermediate_L", "Index_Distal_L",
                 })
            Assert.Contains(name, names);
    }

    [Fact]
    public void Build_RefusesACharacterThatAlreadyHasJoints()
    {
        var path = TestFiles.AllSceneSamples.FirstOrDefault(p => Path.GetFileName(p) == "7_AddingIK.akt");
        if (path == null) return;
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.First(c => c.Name == "Dup");
        var error = Assert.Throws<InvalidOperationException>(
            () => new AutoRigger(character).Build([], new Dictionary<HotPoint, Vector3>()));
        Assert.Contains("already has", error.Message);
    }

    [Fact]
    public void Build_SymmetricPointsGiveSymmetricJoints()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var points = HotPoints(mesh);
        var settings = new AutoRigSettings { Fingers = 3, PhalanxJoints = 2 };
        new AutoRigger(character).Build([mesh], points, settings);
        var editor = new RigEditor(character);
        var pose = character.RefPose!;
        float plane = editor.WorldInPose(pose, character.Joints.Single(j => j.Name == "Pelvis")).M41;
        foreach (var left in character.Joints.Where(j => j.Name?.EndsWith("_L", StringComparison.Ordinal) == true))
        {
            string rightName = left.Name![..^2] + "_R";
            var right = character.Joints.SingleOrDefault(j => j.Name == rightName);
            Assert.NotNull(right);
            var l = editor.WorldInPose(pose, left).Translation;
            var r = editor.WorldInPose(pose, right).Translation;
            Assert.True(MathF.Abs((2f * plane - l.X) - r.X) < 1e-3f, $"{left.Name} / {rightName} X");
            Assert.True(MathF.Abs(l.Y - r.Y) < 1e-3f, $"{left.Name} / {rightName} Y");
            Assert.True(MathF.Abs(l.Z - r.Z) < 1e-3f, $"{left.Name} / {rightName} Z");
        }
    }

    [Fact]
    public void Build_KeepsTheMeshWorldWhenItMovesToTheCharacter()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var before = mesh.WorldMatrix;
        var settings = new AutoRigSettings
        {
            AddIK = false, AddReverseFoot = false, BindSkin = false, GenerateWeights = false,
        };
        new AutoRigger(character).Build([mesh], HotPoints(mesh), settings);
        Assert.Same(character, mesh.Ancestor<Character>());
        AssertSameWorld(before, mesh.WorldMatrix);
    }

    [Fact]
    public void Build_SmoothMapsTheKeptWeightIndicesOntoTheNewRigByName()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var oldRig = mesh.BindPose?.Character;
        if (oldRig == null) return;
        var oldJoints = oldRig.Joints.Select(joint => joint.Name ?? "").ToList();
        var smooth = mesh.SkinWeights;
        var rigid = mesh.RigidSkinWeights;
        int weighted = smooth.Count(weights => weights.Length > 0);
        Assert.True(weighted > 0);

        var result = new AutoRigger(character).Build([mesh], HotPoints(mesh), new AutoRigSettings
        {
            Naming = AutoRigNaming.Mixamo,
            KeepWeights = true,
        });

        // The "Smooth" answer solves nothing: the same influence weights and the same weighted
        // point count, only the joint indices moved
        Assert.Equal(0, result.WeightedPoints);
        Assert.Equal(weighted, mesh.SkinWeights.Count(weights => weights.Length > 0));
        AssertSameWeightValues(smooth, mesh.SkinWeights);
        AssertSameWeightValues(rigid, mesh.RigidSkinWeights);

        var newJoints = character.Joints;
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < newJoints.Count; index++)
            byName.TryAdd(newJoints[index].Name ?? "", index);
        int mapped = 0, kept = 0;
        for (int point = 0; point < smooth.Length; point++)
            for (int influence = 0; influence < smooth[point].Length; influence++)
            {
                int old = smooth[point][influence].Bone;
                Assert.InRange(old, 0, oldJoints.Count - 1);
                int now = mesh.SkinWeights[point][influence].Bone;
                if (byName.TryGetValue(oldJoints[old], out int target))
                {
                    // A joint the new rig has by name is the one the influence now drives.
                    Assert.Equal(target, now);
                    Assert.Equal(oldJoints[old], newJoints[now].Name);
                    mapped++;
                }
                else
                {
                    // No counterpart: the documented fallback keeps the old index rather than
                    // dropping an influence the user authored.
                    Assert.Equal(old, now);
                    kept++;
                }
            }
        Assert.True(mapped > 0);
        Assert.True(kept > 0);

        // The sample's old rig is Blender-named: "Neck" exists on the Mixamo rig and its influences
        // move to it, while "DEF-head" has no counterpart and stays on its old index.
        int oldNeck = oldJoints.IndexOf("Neck");
        int oldHead = oldJoints.IndexOf("DEF-head");
        Assert.True(oldNeck >= 0 && oldHead >= 0);
        int newNeck = Enumerable.Range(0, newJoints.Count).First(index => newJoints[index].Name == "Neck");
        Assert.NotEqual(oldNeck, newNeck);
        Assert.Contains(Enumerable.Range(0, smooth.Length),
            point => smooth[point].Any(w => w.Bone == oldNeck) && mesh.SkinWeights[point].Any(w => w.Bone == newNeck));
        Assert.Contains(Enumerable.Range(0, smooth.Length),
            point => smooth[point].Any(w => w.Bone == oldHead) && mesh.SkinWeights[point].Any(w => w.Bone == oldHead));
    }

    [Fact]
    public void Build_PlacesTheInBetweenJointsByTheRecoveredRules()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var points = HotPoints(mesh);
        new AutoRigger(character).Build([mesh], points, new AutoRigSettings
        {
            AddIK = false, AddReverseFoot = false, BindSkin = false, GenerateWeights = false,
        });
        var editor = new RigEditor(character);
        var pose = character.RefPose!;
        Vector3 W(string name) => editor.WorldInPose(pose, character.Joints.Single(j => j.Name == name)).Translation;

        // The clavicle: a quarter of the way from the chest to the shoulder hot point
        AssertNear(Vector3.Lerp(W("UpperChest"), W("Arm_L"), 0.25f), W("Shoulder_L"));

        // The elbow and the knee: the exact midpoint of their two ends (the 0.5 that
        // and both use, with no invented bend).
        AssertNear((W("Arm_L") + W("Hand_L")) * 0.5f, W("Forearm_L"));
        AssertNear((W("Thigh_L") + W("Foot_L")) * 0.5f, W("Calf_L"));

        // The head: half way between the neck and the highest mesh point
        var world = mesh.WorldMatrix;
        var highest = mesh.ControlPoints.Select(p => Vector3.Transform(p, world)).Max(p => p.Y);
        var top = mesh.ControlPoints.Select(p => Vector3.Transform(p, world)).First(p => p.Y == highest);
        AssertNear(Vector3.Lerp(W("Neck"), top, 0.5f), W("Head"));

        // The ball: 15% of the way from the ankle towards the foot's far end along the leg's
        // snapped direction.
        var ankle = W("Foot_L");
        var knee = W("Calf_L");
        var leg = ankle - knee;
        var axis = Vector3.Normalize(new Vector3(
            MathF.Abs(leg.X) > 1e-6f ? MathF.Sign(leg.X) : 0f,
            MathF.Abs(leg.Y) > 1e-6f ? MathF.Sign(leg.Y) : 0f,
            MathF.Abs(leg.Z) > 1e-6f ? MathF.Sign(leg.Z) : 0f));
        float legLength = Vector3.Distance(W("Thigh_L"), ankle);
        var far = mesh.ControlPoints
            .Select(p => Vector3.Transform(p, world))
            .Where(p => Vector3.Distance(p, ankle) <= legLength * 0.35f)
            .OrderByDescending(p => Vector3.Dot(p - ankle, axis))
            .First();
        // The walk is a ray down the anchor's column: the joint stays on the line from the ankle.
        AssertNear(ankle + axis * (Vector3.Dot(far - ankle, axis) * 0.15f), W("Toe_L"));
    }

    [Fact]
    public void Build_SpacesThePhalanxJointsToTheTipHotPoint()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var points = HotPoints(mesh);
        new AutoRigger(character).Build([mesh], points, new AutoRigSettings
        {
            AddIK = false, AddReverseFoot = false, BindSkin = false, GenerateWeights = false,
        });
        var editor = new RigEditor(character);
        var pose = character.RefPose!;
        var hand = editor.WorldInPose(pose, character.Joints.Single(j => j.Name == "Hand_L")).Translation;
        // The last phalanx joint is the tip hot point: the three phalanges span the whole
        // hand to tip hot point.
        float span = Vector3.Distance(hand, points[HotPoint.IndexLeft]);
        for (int joint = 1; joint <= 3; joint++)
        {
            var phalanx = character.Joints.Single(j => j.Name == $"Index_0{joint}_L");
            Assert.Equal(span / 3f, phalanx.LocalMatrix.Translation.Length(), 1);
        }
    }

    [Fact]
    public void Build_GivesTheThumbOneFewerPhalanxAtEveryCount()
    {
        if (Sample() is not var (_, character, mesh)) return;
        // The PhalanxNumber counts the phalanx joints of a long finger; the shipped rigs' thumb
        // has one fewer
        foreach (int count in new[] { 2, 3, 4 })
        {
            var model = TestFiles.LoadScene(TestFiles.AllSceneSamples.First(p =>
                Path.GetFileName(p) == "4_CenteredRootAddedHipsRenamedSome.akt"));
            var target = model.Scene.Characters.First(c => c.Name == "Default character" && c.Joints.Count == 0);
            var targetMesh = model.Scene.Meshes.First(m => m.Name == "Dup_b");
            new AutoRigger(target).Build([targetMesh], HotPoints(targetMesh), new AutoRigSettings
            {
                AddIK = false, AddReverseFoot = false, BindSkin = false, GenerateWeights = false,
                PhalanxJoints = count,
            });
            var names = target.Joints.Select(j => j.Name!).ToHashSet(StringComparer.Ordinal);
            for (int joint = 1; joint <= count; joint++)
            {
                Assert.Contains($"Index_0{joint}_L", names);
                Assert.Contains($"Little_0{joint}_R", names);
            }
            for (int joint = 1; joint <= count - 1; joint++)
                Assert.Contains($"Thumb_0{joint}_L", names);
            Assert.DoesNotContain($"Index_0{count + 1}_L", names);
            Assert.DoesNotContain($"Thumb_0{count}_L", names);
        }
    }

    [Fact]
    public void SkinWeights_SmoothAndBendATwoBoneLimbWithoutASample()
    {
        // A straight two-bone strip: 7 control points along Y, two bones, quads between neighbours.
        // No scene, no hot points - the decoded rule alone
        var points = new List<Vector3>();
        for (int i = 0; i < 7; i++) points.Add(new Vector3(0, i * 10f, 0));
        var bones = new[]
        {
            new SkinWeightGenerator.Bone(0, new Vector3(0, 0, 0), new Vector3(0, 30, 0)),
            new SkinWeightGenerator.Bone(1, new Vector3(0, 30, 0), new Vector3(0, 60, 0)),
        };
        var polygons = new List<MeshPolygon>();
        for (uint i = 0; i + 1 < 7; i++) polygons.Add(new MeshPolygon([i, i + 1], 0));
        var neighbours = SkinWeightGenerator.PolygonNeighbours(polygons, points.Count);
        var methods = new[] { 0, 0 }; // body smooth on both joints

        var rigid = SkinWeightGenerator.NearestBones(points, Matrix4x4.Identity, bones);
        var smooth = SkinWeightGenerator.Solve(points, Matrix4x4.Identity, bones, methods, neighbours);

        // The seed: one influence of weight 1, the nearest bone's start joint. The boundary is at
        // y = 30: points 0-3 belong to bone 0, points 4-6 to bone 1.
        Assert.All(rigid, entry => Assert.Single(entry));
        Assert.All(rigid, entry => Assert.Equal(1f, entry[0].Weight));
        Assert.Equal([0, 0, 0, 0, 1, 1, 1], rigid.Select(entry => entry[0].Bone).ToArray());

        // The smooth set: each end keeps its own bone on top, the joint at the boundary mixes both,
        // and every entry still sums to 1 in at most NbMaxJoints influences. (The decoded smoothing
        // runs BodyIteration = 5 units, so it diffuses several rings along a seven point strip and a
        // far end can pick up a little of the other bone - that is the rule, not a defect.)
        Assert.Equal(0, smooth[0][0].Bone);
        Assert.Equal(1, smooth[6][0].Bone);
        Assert.Equal(2, smooth[3].Length);
        Assert.Equal([0, 1], smooth[3].Select(w => w.Bone).OrderBy(b => b).ToArray());
        foreach (var entry in smooth)
        {
            Assert.InRange(entry.Length, 1, SkinWeightGenerator.MaxInfluences);
            Assert.InRange(entry.Sum(w => w.Weight), 0.999f, 1.001f);
            Assert.All(entry, w => Assert.True(w.Weight > SkinWeightGenerator.AutoSweepValue));
        }
        // Body smoothing runs BodyIteration units, so the boundary point is a real blend, not a copy.
        Assert.InRange(smooth[3].Max(w => w.Weight), 0.5f, 0.98f);
    }

    [Fact]
    public void SkinWeights_LeaveARigidJointAndHalfSmoothAFaceJoint()
    {
        // The same strip with the per-joint SmoothMethod the generator reads: 0 body, 1 face (only
        // the first half-strength pass), 2 rigid (no pass at all).
        var points = new List<Vector3>();
        for (int i = 0; i < 7; i++) points.Add(new Vector3(0, i * 10f, 0));
        var bones = new[]
        {
            new SkinWeightGenerator.Bone(0, new Vector3(0, 0, 0), new Vector3(0, 30, 0)),
            new SkinWeightGenerator.Bone(1, new Vector3(0, 30, 0), new Vector3(0, 60, 0)),
        };
        var polygons = new List<MeshPolygon>();
        for (uint i = 0; i + 1 < 7; i++) polygons.Add(new MeshPolygon([i, i + 1], 0));
        var neighbours = SkinWeightGenerator.PolygonNeighbours(polygons, points.Count);

        var rigidJoint = SkinWeightGenerator.Solve(points, Matrix4x4.Identity, bones, [2, 2], neighbours);
        foreach (var entry in rigidJoint)
        {
            Assert.Single(entry);
            Assert.Equal(1f, entry[0].Weight);
        }

        var body = SkinWeightGenerator.Solve(points, Matrix4x4.Identity, bones, [0, 0], neighbours);
        var face = SkinWeightGenerator.Solve(points, Matrix4x4.Identity, bones, [1, 1], neighbours);
        // The face strength is one half pass, so its blend is shallower than the body's.
        float bodyBlend = body[3].Min(w => w.Weight);
        float faceBlend = face[3].Length > 1 ? face[3].Min(w => w.Weight) : 0f;
        Assert.True(faceBlend < bodyBlend, $"face {faceBlend} vs body {bodyBlend}");
    }

    [Fact]
    public void Build_WritesTheGeneratorsTwoArrays()
    {
        if (Sample() is not var (_, character, mesh)) return;
        var points = HotPoints(mesh);
        var settings = new AutoRigSettings { AddIK = false, AddReverseFoot = false };
        new AutoRigger(character).Build([mesh], points, settings);
        Assert.True(mesh.SkinWeights.Length > 0);
        Assert.Equal(mesh.ControlPoints.Length, mesh.SkinWeights.Length);
        Assert.Equal(mesh.ControlPoints.Length, mesh.RigidSkinWeights.Length);
        // The rigid array is the bind's: one influence, weight 1.
        foreach (var entry in mesh.RigidSkinWeights)
        {
            Assert.Single(entry);
            Assert.Equal(1f, entry[0].Weight);
        }
        // The smooth array is the pruned, normalised smoothing of that seed.
        foreach (var entry in mesh.SkinWeights)
        {
            Assert.InRange(entry.Length, 1, settings.MaxInfluences);
            Assert.InRange(entry.Sum(w => w.Weight), 0.999f, 1.001f);
            Assert.All(entry, w => Assert.InRange(w.Bone, 0, character.Joints.Count - 1));
        }
        // Smoothing really blends: some control point carries two influences.
        Assert.Contains(mesh.SkinWeights, entry => entry.Length == 2);
        // The seed's joint is still the strongest influence wherever the point is not on a joint.
        int sameAsRigid = 0;
        for (int p = 0; p < mesh.SkinWeights.Length; p++)
            if (mesh.SkinWeights[p].Max(w => w.Weight) == mesh.SkinWeights[p][0].Weight &&
                mesh.SkinWeights[p][0].Bone == mesh.RigidSkinWeights[p][0].Bone) sameAsRigid++;
        Assert.True(sameAsRigid > mesh.SkinWeights.Length / 2,
            $"only {sameAsRigid} of {mesh.SkinWeights.Length} points keep their seed's joint on top");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-2f, $"{expected} vs {actual}");
    }

    private static (SceneModel Model, Character Character, Mesh Mesh)? Sample()
    {
        var path = TestFiles.AllSceneSamples.FirstOrDefault(
            p => Path.GetFileName(p) == "4_CenteredRootAddedHipsRenamedSome.akt");
        if (path == null) return null;
        var model = TestFiles.LoadScene(path);
        var character = model.Scene.Characters.FirstOrDefault(c => c.Name == "Default character" && c.Joints.Count == 0);
        var mesh = model.Scene.Meshes.FirstOrDefault(m => m.Name == "Dup_b");
        if (character == null || mesh == null) return null;
        return (model, character, mesh);
    }

    /// <summary>Plausible hot points from the mesh bounds; the tests do not claim they are the.</summary>
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

    /// <summary>The same influence weights per point, whatever joint each now names.</summary>
    private static void AssertSameWeightValues(VertexWeight[][] expected, VertexWeight[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int point = 0; point < expected.Length; point++)
        {
            Assert.Equal(expected[point].Length, actual[point].Length);
            for (int influence = 0; influence < expected[point].Length; influence++)
                Assert.Equal(expected[point][influence].Weight, actual[point][influence].Weight);
        }
    }

    private static void AssertSameWorld(Matrix4x4 expected, Matrix4x4 actual)
    {
        float scale = Math.Max(1f, new Vector3(expected.M41, expected.M42, expected.M43).Length());
        Assert.True(AktMath.MaxDifference(expected, actual) < 1e-4f * scale,
            $"{expected.Translation} vs {actual.Translation}");
    }

    private static void AssertRoundtrip(SceneModel model)
    {
        string first = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-autorig-{Guid.NewGuid():N}.akt");
        string second = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-autorig-{Guid.NewGuid():N}.akt");
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
