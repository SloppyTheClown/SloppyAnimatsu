using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Paint &amp; Check's pose stays with its character: a new scene pose (switching context) finds it
/// again, a SloppyAnimatsu save keeps it and an akeytsu-format save leaves it out; reverse feet keep
/// their roll, twist and bank the same way.
/// </summary>
public class CheckPoseStoreTests
{
    [SceneTheory, SceneSamples]
    public void CheckPose_SurvivesANewScenePose(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Posed(model) is not var (character, controller, expected)) return;

        var evaluator = Rigging(model, character).Evaluator;

        Assert.True(evaluator.HasCheckPose);
        AssertX(expected, evaluator, controller);
    }

    [SceneTheory, SceneSamples]
    public void CheckPose_IsKeptInSlopaFiles_AndLeftOutOfAktFiles(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (Posed(model) is not var (character, controller, expected)) return;
        string own = Temporary(".slopa");
        string plain = Temporary(".akt");
        try
        {
            CheckPoseStore.Store(model);
            model.Save(own, AktFileFormat.SloppyAnimatsu);
            model.Save(plain, AktFileFormat.Akeytsu);

            var reopened = SceneModel.Load(own);
            var again = reopened.Scene.Characters.First(c => c.Name == character.Name);
            var evaluator = Rigging(reopened, again).Evaluator;
            Assert.True(evaluator.HasCheckPose);
            AssertX(expected, evaluator, again.Controllers.First(c => c.Name == controller.Name));

            var stripped = SceneModel.Load(plain);
            Assert.False(stripped.Scene.Source.Has(MaterialSchema.CheckPoseTargetsMember));
            Assert.False(stripped.Scene.Source.Has(MaterialSchema.CheckPoseFootAnglesMember));
        }
        finally
        {
            File.Delete(own);
            File.Delete(plain);
        }
    }

    [SceneTheory, SceneSamples]
    public void ReverseFoot_RollInPaintAndCheck_TurnsTheShoe_UndoesAndSaves(string path)
    {
        var model = TestFiles.LoadScene(path);
        var found = model.Scene.Characters
            .SelectMany(c => c.Controllers.OfType<ControllerReverseFoot>().Select(f => (c, f)))
            .FirstOrDefault(p => p.f.Shoe.Count >= 7);
        // 7_AddingIK carries RF_Toes_L/R with full shoes.
        if (path.Contains("7_AddingIK")) Assert.NotNull(found.f);
        if (found.f == null) return;
        var (character, foot) = found;
        var session = Rigging(model, character);
        var locator = foot.Shoe[0];
        int at = session.Evaluator.IndexOf(locator);
        Assert.True(at >= 0);

        int written = session.SetScalarChannel(null, [foot], 0, 10, 0.4f, out var reason, checkPose: true);

        Assert.True(written == 1, reason);
        Assert.Equal(0.4f, session.Evaluator.CheckFootAngles(foot).X, 4);
        bool moved = foot.Shoe.Take(7).Select(session.Evaluator.IndexOf).Any(i =>
            !Near(session.Evaluator.RestTransforms[i], session.Evaluator.LocalTransforms(null, 0)[i]));
        Assert.True(moved, "no shoe locator moved");

        string own = Temporary(".slopa");
        try
        {
            CheckPoseStore.Store(model);
            model.Save(own, AktFileFormat.SloppyAnimatsu);
            var reopened = SceneModel.Load(own);
            var again = reopened.Scene.Characters.First(c => c.Name == character.Name);
            var foot2 = again.Controllers.OfType<ControllerReverseFoot>().First(f => f.Name == foot.Name);
            Assert.Equal(0.4f, Rigging(reopened, again).Evaluator.CheckFootAngles(foot2).X, 4);
        }
        finally
        {
            File.Delete(own);
        }

        Assert.True(session.Undo());
        Assert.Equal(0f, session.Evaluator.CheckFootAngles(foot).X, 4);
    }

    /// <summary>Poses a plain controller's rotation X to 25 degrees in Paint &amp; Check.</summary>
    private static (Character, Object3D, float)? Posed(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
        {
            var session = Rigging(model, character);
            var controller = character.Controllers.FirstOrDefault(c =>
                c is not ControllerIK && c is not ReverseFootLocator
                && !character.IKControllers.Any(ik => ik.Chain.Contains(c)) && session.Evaluator.IndexOf(c) >= 0);
            if (controller == null) continue;
            int written = session.SetChannelValue(null, [controller], 0, 3, 25, out var reason, checkPose: true);
            Assert.True(written == 1, reason);
            return (character, controller, 25f);
        }
        return null;
    }

    private static PoseEditSession Rigging(SceneModel model, Character character)
    {
        CharacterBank.Select(character, CharacterBank.Rows(character).First(r => r.Kind == CharacterBankRowKind.PaintAndCheck));
        return new(new ScenePose(model) { RiggingContext = true }, character);
    }

    private static void AssertX(float expected, PoseEvaluator evaluator, Object3D controller)
    {
        var local = evaluator.LocalTransforms(null, 0)[evaluator.IndexOf(controller)];
        float x = Curves.ToEulerDegrees(local.Rotation).X;
        Assert.True(MathF.Abs(x - expected) < 0.05f, $"{controller.Name} X = {x}");
    }

    private static bool Near(Transform a, Transform b) =>
        Vector3.Distance(a.Translation, b.Translation) < 1e-3f && MathF.Abs(Quaternion.Dot(a.Rotation, b.Rotation)) > 0.99999f;

    private static string Temporary(string extension) =>
        Path.Combine(Path.GetTempPath(), "sloppyanimatsu-checkpose-" + Guid.NewGuid().ToString("N") + extension);
}
