using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The transform-space handles and drags (Local / World / Screen) and the Spinner button's cycle
/// No sample is needed.
/// </summary>
public class TransformSpaceTests
{
    private static OrbitCamera Tilted() => new() { Target = Vector3.Zero, Distance = 8, Yaw = 0.7f, Pitch = 0.35f };

    [Fact]
    public void SpaceButton_CyclesLocalScreenWorld_LikeTheNumberKeys()
    {
        Assert.Equal(TransformSpace.Screen, SpinnerDial.NextSpace(TransformSpace.Local));
        Assert.Equal(TransformSpace.World, SpinnerDial.NextSpace(TransformSpace.Screen));
        Assert.Equal(TransformSpace.Local, SpinnerDial.NextSpace(TransformSpace.World));
    }

    [Fact]
    public void Gizmo_WorldSpace_DrawsTheWorldAxes_WhateverTheFrameIs()
    {
        var camera = Tilted();
        var world = new TransformGizmo
        {
            Visible = true, Mode = GizmoMode.Translate, CanTranslate = true, Space = TransformSpace.World,
            Frame = Matrix4x4.CreateRotationY(1.1f), ParentFrame = Matrix4x4.CreateRotationZ(0.4f),
        };
        for (int axis = 0; axis < 3; axis++)
        {
            var unit = axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
            Assert.True(Vector3.Distance(unit, world.Axis(axis, camera)) < 1e-4f, $"world axis {axis}");
        }

        var local = new TransformGizmo
        {
            Visible = true, Mode = GizmoMode.Translate, CanTranslate = true,
            ParentFrame = Matrix4x4.CreateRotationZ(0.4f),
        };
        Assert.True(Vector3.Distance(Vector3.UnitX, local.Axis(0, camera)) > 0.1f, "Local X is the parent's X");
    }

    [Fact]
    public void Gizmo_ScreenSpace_UsesTheViewsRightUpAndBackwardAxes()
    {
        var camera = Tilted();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Rotate, Space = TransformSpace.Screen };
        var right = gizmo.Axis(0, camera);
        var up = gizmo.Axis(1, camera);
        var backward = gizmo.Axis(2, camera);
        Assert.Equal(0f, Vector3.Dot(right, up), 4);
        Assert.Equal(0f, Vector3.Dot(right, backward), 4);
        Assert.Equal(0f, Vector3.Dot(up, backward), 4);
        // The depth handle points at the viewer (the gizmo sits at the origin here).
        Assert.True(Vector3.Dot(backward, Vector3.Normalize(camera.Eye)) > 0.999f, $"depth handle {backward}");
        // Screen and world handles are one world unit long; local ones are one parent unit.
        Assert.Equal(gizmo.Size(camera), gizmo.HandleUnits(camera, 0), 3);
        gizmo.Space = TransformSpace.Local;
        gizmo.ParentFrame = Matrix4x4.CreateScale(4);
        Assert.Equal(gizmo.Size(camera) / 4, gizmo.HandleUnits(camera, 0), 3);
    }

    [Fact]
    public void WorldTranslate_MovesAlongTheWorldAxis_WhateverTheParentsTurn()
    {
        var rig = NewRig();
        var drag = rig.Session.Begin(rig.Animation, rig.Child, 10, out string? reason);
        Assert.NotNull(drag);
        Assert.Null(reason);
        drag!.Space = TransformSpace.World;
        drag.SpaceFrame = Matrix4x4.Identity;
        var before = WorldOf(rig, rig.Child);
        var startLocal = ((EditDrag)drag).BaseLocal;

        drag.Translate(0, 2f);

        var after = WorldOf(rig, rig.Child);
        var moved = after.Translation - before.Translation;
        Assert.True(Vector3.Distance(new Vector3(2f, 0f, 0f), moved) < 1e-3f, $"moved {moved}");
        var local = rig.Session.Evaluator.LocalTransforms(rig.Animation, 10)[rig.Session.Evaluator.IndexOf(rig.Child)];
        Assert.True(MathF.Abs(local.Translation.X - startLocal.Translation.X) > 1e-3f
                    && MathF.Abs(local.Translation.Z - startLocal.Translation.Z) > 1e-3f,
            $"the parent is turned about Y, so local X and Z both change ({startLocal.Translation} -> {local.Translation})");
    }

    [Fact]
    public void WorldRotate_TurnsAboutAWorldAxis_ThroughTheControllersOrigin()
    {
        var rig = NewRig();
        var drag = rig.Session.Begin(rig.Animation, rig.Child, 10, out _)!;
        drag.Space = TransformSpace.World;
        drag.SpaceFrame = Matrix4x4.Identity;
        var before = WorldOf(rig, rig.Child);

        drag.Rotate(1, MathF.PI / 2);   // a quarter turn about world +Y

        var after = WorldOf(rig, rig.Child);
        var expected = before * Matrix4x4.CreateRotationY(MathF.PI / 2);
        expected.Translation = before.Translation;
        AssertClose(expected, after);
        Assert.True(Vector3.Distance(before.Translation, after.Translation) < 1e-4f, "the origin moved");
    }

    [Fact]
    public void ScreenTranslate_MovesAlongTheViewsRightAxis_WithNoDepth()
    {
        var rig = NewRig();
        var camera = Tilted();
        var gizmo = new TransformGizmo { Visible = true, Mode = GizmoMode.Translate, Space = TransformSpace.Screen };
        var drag = rig.Session.Begin(rig.Animation, rig.Child, 10, out _)!;
        drag.Space = TransformSpace.Screen;
        drag.SpaceFrame = gizmo.SpaceFrame(camera);
        var before = WorldOf(rig, rig.Child);

        drag.Translate(0, 3f);

        var moved = WorldOf(rig, rig.Child).Translation - before.Translation;
        Assert.True(Vector3.Distance(gizmo.Axis(0, camera) * 3f, moved) < 1e-3f, $"moved {moved}");
        Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(moved), gizmo.Axis(2, camera)), 4);
    }

    [Fact]
    public void WorldUniformScale_DoublesTheWorldBasis_AndKeepsTheOrigin()
    {
        var rig = NewRig();
        var drag = rig.Session.Begin(rig.Animation, rig.Child, 10, out _)!;
        drag.Space = TransformSpace.World;
        drag.SpaceFrame = Matrix4x4.Identity;
        var before = WorldOf(rig, rig.Child);

        drag.Scale(TransformGizmo.Uniform, 2f);

        var after = WorldOf(rig, rig.Child);
        var expected = before;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                expected[r, c] = before[r, c] * 2;
        AssertClose(expected, after);
    }

    [Fact]
    public void GroupWorldTranslate_GivesEverySelectedControllerTheSameWorldMove()
    {
        var rig = NewRig();
        var drag = Assert.IsType<GroupDrag>(rig.Session.Begin(rig.Animation, [rig.Sibling, rig.Child], 10, out var reason));
        Assert.Null(reason);
        drag.Space = TransformSpace.World;
        drag.SpaceFrame = Matrix4x4.Identity;
        var childBefore = WorldOf(rig, rig.Child);
        var siblingBefore = WorldOf(rig, rig.Sibling);

        drag.Translate(1, 5f);

        Assert.True(Vector3.Distance(new Vector3(0f, 5f, 0f), WorldOf(rig, rig.Child).Translation - childBefore.Translation) < 1e-3f);
        Assert.True(Vector3.Distance(new Vector3(0f, 5f, 0f), WorldOf(rig, rig.Sibling).Translation - siblingBefore.Translation) < 1e-3f);
    }

    private sealed record Rig(PoseEditSession Session, Animation Animation, Joint Child, Joint Sibling);

    private static Rig NewRig()
    {
        var model = SceneFactory.NewSceneModel(60);
        var character = model.Scene.Characters.First();
        // A new scene's schema carries no skeleton; add the CJoint class a rig edit expects
        // (FbxImportSchema defines it the same way: CObject3D plus SmoothMethod).
        var schema = model.Document.Schema;
        if (schema.FindClass("CJoint") == null)
        {
            var jointClass = new AktClassDef("class CJoint");
            jointClass.Members.Add(new AktMember("SmoothMethod", "int"));
            schema.AddClass(jointClass, schema.FindClass("class CObject3D"));
        }
        var editor = new RigEditor(character);
        var parent = editor.CreateJoint(null, "Parent", Matrix4x4.CreateRotationY(0.6f));
        var child = editor.CreateJoint(parent, "Child", Matrix4x4.CreateTranslation(10f, 0f, 0f));
        var sibling = editor.CreateJoint(parent, "Sibling", Matrix4x4.CreateTranslation(0f, 4f, 0f));
        var session = new PoseEditSession(new ScenePose(model), character);
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        // A stack keys up to its penultimate time; give it an editable middle key.
        session.SetKeyTime(stack, 1, 20);
        session.CreateKey(stack, 10);
        session.SetKeyTime(stack, 2, 20);
        return new Rig(session, animation, child, sibling);
    }

    private static Matrix4x4 WorldOf(Rig rig, Object3D controller) =>
        rig.Session.Evaluator.WorldMatrices(rig.Animation, 10)[rig.Session.Evaluator.IndexOf(controller)];

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        float size = MathF.Max(1, expected.Translation.Length());
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) < 2e-3f * size, $"[{r},{c}] {expected} != {actual}");
    }
}