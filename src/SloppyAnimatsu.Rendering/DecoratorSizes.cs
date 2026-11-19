using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// The sizes of the skeleton decorators in scene units (.akt scenes are in centimetres), from the
/// open scene's Decorators joint factor (<c>CScene.JointRenderSize</c>;
///). They are fixed sizes in the world, like the
/// body itself, so the dots and bones grow and shrink with the camera distance; the shaders turn
/// them into pixels per vertex (dots at least <see cref="MinJointPixels"/>, bones at least
/// <see cref="MinBonePixels"/>, so a far skeleton stays visible; at most <see cref="MaxBonePixels"/> and
/// <see cref="MaxJointPixels"/>, so a bone ending next to the camera cannot fill the view). The mapping
/// is SloppyAnimatsu's own: at
/// per unit of the factor a joint dot is 1 cm across and a bone 0.2 cm wide (5 cm
/// and 1 cm at factor 5), and the selection dots are 1.5x and 1.25x the joint dot so they still
/// ring it.
/// </summary>
public readonly record struct DecoratorSizes(float Joint, float Selected, float AlsoSelected, float BoneWidth)
{
    /// <summary>The app draws the shoe locators' plus signs 3 px wide at any distance (controller-display.md "Shoe locators").</summary>
    public const float ShoeLocatorWidth = 3f;

    /// <summary>Scene units (centimetres) per unit of the joint factor.</summary>
    public const float JointPerFactor = 1f, BonePerFactor = 0.2f;
    public const float MinJointPixels = 2f, MaxJointPixels = 256f, MinBonePixels = 1f, MaxBonePixels = 64f;

    /// <summary>The sizes for <paramref name="scene"/>'s joint factor (<see cref="Scene.JointRenderSize"/>'s fallback when null).</summary>
    public static DecoratorSizes For(Scene? scene) =>
        FromFactor(scene?.JointRenderSize ?? Scene.DefaultJointRenderSize);

    public static DecoratorSizes FromFactor(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0f) factor = Scene.DefaultJointRenderSize;
        float joint = JointPerFactor * factor;
        return new DecoratorSizes(joint, joint * 1.5f, joint * 1.25f, BonePerFactor * factor);
    }

    /// <summary>Pixels (or DIPs) per scene unit at <paramref name="world"/> in a view <paramref name="viewportHeight"/> high.</summary>
    public static float PixelsPerUnit(IViewCamera camera, float viewportHeight, Vector3 world)
    {
        float height = camera.ViewHeightAt(world);
        return height > 1e-6f && float.IsFinite(height) ? viewportHeight / height : 0f;
    }

    /// <summary>The on-screen diameter of a dot <paramref name="worldSize"/> across, clamped like the shader.</summary>
    public static float JointPixels(float worldSize, float pixelsPerUnit) =>
        Math.Clamp(worldSize * pixelsPerUnit, MinJointPixels, MaxJointPixels);

    /// <summary>
    /// How far (DIPs) a click may land from a joint near <paramref name="world"/> and still pick it:
    /// the selected dot's on-screen radius plus a little, never less than <see cref="ViewportPick.Tolerance"/>.
    /// </summary>
    public float PickTolerance(IViewCamera camera, Vector2 viewport, Vector3 world) =>
        Math.Max(ViewportPick.Tolerance, JointPixels(Selected, PixelsPerUnit(camera, viewport.Y, world)) * 0.5f + 2f);
}
