using System.Numerics;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Builds a brand-new scene, File > New Scene:
/// one <c>CScene</c> named "Scene" with the eight fixed cameras, one empty character named
/// "Default character", that character's <c>__BlendshapeControllers</c>, <c>__Anims</c>,
/// <c>__BindPoses</c>, reference and old-reference poses, and an empty <c>__RigAnim</c> holding a
/// single "New Stack". The value of every member is the one the new scene writes.
/// </summary>
/// <remarks>
/// The app leaves a few members of a new scene uninitialised, so two scenes made by it differ in
/// them (for example <c>CCameraBase.CameraType</c>, read as 0 or 12928 or 6881396 in the "New_N"
/// autosaves). SloppyAnimatsu writes the meaningful value instead: -1 for the perspective and photo
/// cameras, 0-5 for the six orthographic ones, and 0 for <c>CScene.FrameRateIndex</c>.
/// </remarks>
public static class SceneFactory
{
    public const uint DefaultFrameRate = 60;
    public const string SceneName = "Scene";
    public const string CharacterName = "Default character";

    /// <summary>
    /// The name the app gives an unsaved scene — <c>New_1</c>, <c>New_2</c>,... from a counter
    /// that starts at 1 for each run of the application. It is the window title and the base name
    /// of the scene's autosave files; it is not stored in the scene.
    /// </summary>
    public static string UntitledName(int number)
    {
        if (number < 1) throw new ArgumentOutOfRangeException(nameof(number), "Untitled scenes are numbered from 1.");
        return $"New_{number}";
    }

    private static readonly Matrix4x4 Identity = Matrix4x4.Identity;

    private static readonly string[] CameraNames =
        ["Perspective", "Front", "Back", "Right", "Left", "Top", "Bottom", "Photo"];

    /// <summary>
    /// The default camera transforms of a new scene, in <see cref="CameraNames"/> order. The
    /// perspective and photo cameras share the default three-quarter view; the six orthographic
    /// cameras look down the axes (the tiny epsilons are the original's own, not SloppyAnimatsu's).
    /// </summary>
    private static readonly float[][] CameraMatrices =
    [
        [0.9800666f, 0f, -0.19866933f, 0f, -0.099334665f, 0.8660254f, -0.4900333f, 0f, 0.17205268f, 0.5f, 0.8487626f, 0f, 120.43688f, 350f, 594.1338f, 1f],
        [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
        [-1f, 0f, 8.742278E-08f, 0f, 0f, 1f, 0f, 0f, -8.742278E-08f, 0f, -1f, 0f, 0f, 0f, 0f, 1f],
        [-4.371139E-08f, 0f, -1f, 0f, 0f, 0.99999994f, 0f, 0f, 1f, 0f, -4.371139E-08f, 0f, 0f, 0f, 0f, 1f],
        [-4.371139E-08f, 0f, 1f, 0f, 0f, 0.99999994f, 0f, 0f, -1f, 0f, -4.371139E-08f, 0f, 0f, 0f, 0f, 1f],
        [0.99999994f, 0f, 0f, 0f, 0f, -4.371139E-08f, -1f, 0f, 0f, 1f, -4.371139E-08f, 0f, 0f, 0f, 0f, 1f],
        [0.99999994f, 0f, 0f, 0f, 0f, -4.371139E-08f, 1f, 0f, 0f, -1f, -4.371139E-08f, 0f, 0f, 0f, 0f, 1f],
        [0.9800666f, 0f, -0.19866933f, 0f, -0.099334665f, 0.8660254f, -0.4900333f, 0f, 0.17205268f, 0.5f, 0.8487626f, 0f, 120.43688f, 350f, 594.1338f, 1f],
    ];

    /// <summary>New scene with <paramref name="frameRate"/> frames per second (the preference's default is 60).</summary>
    public static AktDocument NewScene(uint frameRate = DefaultFrameRate)
    {
        if (frameRate == 0) throw new ArgumentOutOfRangeException(nameof(frameRate), "The frame rate must be positive.");

        var schema = SceneSchema.Create();
        var scene = schema.NewObject("class CScene");
        scene.Set("Name", SceneName);
        scene.Set("InterfaceContextIndex", (byte)0);
        scene.Set("FrameRate", frameRate);
        scene.Set("FrameRateIndex", 0);
        scene.Set("SizeUnit", 3);
        scene.Set("GridSize", 4u);
        scene.Set("UnitSubdivision", 5u);
        // SloppyAnimatsu starts new scenes at the user's factors (an FBX
        // import writes 5 and 6).
        scene.Set("JointRenderSize", 5f);
        scene.Set("JointRenderSizeHighDensity", 6f);
        scene.Set("PivotRenderSize", 3f);
        scene.Set("UnselectedPivotRenderSize", 0.4f);
        scene.Set("TargetRenderSize", 8f);
        scene.Set("BankAngleMax", 60f);
        scene.Set("TwistAngleMax", 60f);
        scene.Set("RollAngleBackMax", 60f);
        scene.Set("RollAnglePreMax", 60f);
        scene.Set("RollAngleToesMax", 75f);
        scene.Set("RollAngleFullMax", 85f);
        scene.Set("SnapAngleThreshold", 5f);
        scene.Set("LayoutIndex", 0);
        scene.Set("ToggledLayoutIndex", 3);
        scene.Set("ToggledLocalViewIndex", -1);
        scene.Set("ViewCamIndices", new List<object?> { 0, 0, 0, 1, 0, 1, 0, 4, 5 });
        scene.Set("SavePath", "");
        scene.Set("FBXPath", "");
        scene.Set("AKNMPath", "");
        scene.Set("Time", 0);

        var cameras = new List<AktReference>();
        for (int i = 0; i < CameraNames.Length; i++)
        {
            bool perspective = i is 0 or 7;
            var camera = schema.NewObject(perspective ? "class CCamera" : "class CCameraOrtho");
            camera.Set("Name", CameraNames[i]);
            camera.Set("Flags", 1025u);
            camera.Set("LTM", (float[])CameraMatrices[i].Clone());
            camera.Set("CameraType", perspective ? -1 : i - 1);
            camera.Set("RenderMode", (byte)0);
            camera.Set("RenderFlags", 4152u);
            camera.Set("_matrix", AktMath.ToArray(Identity));
            camera.Set("_targetDistance", 700f);
            if (perspective)
            {
                camera.Set("_ratio", 1f);
            }
            else
            {
                camera.Set("CentimetersInHeight", 500f);
                camera.Set("WorldOffsetX", 0f);
                camera.Set("WorldOffsetY", 0f);
            }
            scene.AddChild(camera);
            cameras.Add(new AktReference(camera));
        }
        scene.Set("CameraList", cameras);

        var character = NewCharacter(schema, CharacterName);
        scene.AddChild(character);
        scene.Set("CurrentCharacter", new AktReference(character));

        return new AktDocument(schema, [scene]);
    }

    /// <summary>The scene model of a new scene, ready for an editor.</summary>
    public static SceneModel NewSceneModel(uint frameRate = DefaultFrameRate) => new(NewScene(frameRate));

    /// <summary>
    /// A new, empty character as File > New Scene builds it (and the CHAR BANK's Create Character,
    ///): the <c>__BlendshapeControllers</c>, <c>__Anims</c> and
    /// <c>__BindPoses</c> holders, the <c>__RefPose</c> / <c>__RefPoseOld</c> poses and a
    /// <c>__RigAnim</c> with one stack. <c>CurrentPose</c> and <c>RefPose</c> both point at
    /// <c>__RefPose</c>, which acts as the Build Pose until a mesh is bound. The caller adds it to
    /// the scene. Throws <see cref="InvalidOperationException"/> when the schema lacks a class.
    /// </summary>
    public static AktObject NewCharacter(AktSchema schema, string name)
    {
        var character = schema.NewObject("class CCharacter");
        character.Set("Name", name);
        character.Set("Flags", 1025u);
        character.Set("LTM", AktMath.ToArray(Identity));

        var blendshapeControllers = schema.NewObject("class CObject3DHiddenInTree");
        blendshapeControllers.Set("Name", "__BlendshapeControllers");
        blendshapeControllers.Set("Flags", 1273u);
        blendshapeControllers.Set("LTM", AktMath.ToArray(Identity));
        character.AddChild(blendshapeControllers);

        var animDummy = schema.NewObject("class CDummy");
        animDummy.Set("Name", "__Anims");
        character.AddChild(animDummy);

        var bindPoses = schema.NewObject("class CDummy");
        bindPoses.Set("Name", "__BindPoses");
        character.AddChild(bindPoses);

        var refPose = schema.NewObject("class CCharacterPose");
        refPose.Set("Name", "__RefPose");
        character.AddChild(refPose);

        var refPoseOld = schema.NewObject("class CCharacterPose");
        refPoseOld.Set("Name", "__RefPoseOld");
        character.AddChild(refPoseOld);

        var rigAnim = schema.NewObject("class CAnimation");
        rigAnim.Set("Name", "__RigAnim");
        rigAnim.Set("CurrentTime", 0);

        var stack = schema.NewObject("class CAnimStack");
        stack.Set("Name", "New Stack");
        stack.Set("Flags", AnimationBankEditor.EnabledFlag);
        stack.Set("FlagsNoModify", 0u);
        stack.Set("Color", new byte[] { 0x99, 0x99, 0x99, 0xFF });
        stack.Set("Opacity", 1f);
        stack.Set("Tweenage", 0.5f);
        var times = (List<object?>)stack["TimeArray"]!;
        times.Add(schema.NewStruct("CAnimStackKey"));
        times.Add(schema.NewStruct("CAnimStackKey"));
        rigAnim.AddChild(stack);
        rigAnim.Set("StackList", new List<AktReference> { new(stack) });
        rigAnim.Set("CurrentStack", new AktReference(stack));
        character.AddChild(rigAnim);

        character.Set("CurrentPose", new AktReference(refPose));
        character.Set("RefPose", new AktReference(refPose));
        character.Set("RefPoseOld", new AktReference(refPoseOld));
        return character;
    }
}
