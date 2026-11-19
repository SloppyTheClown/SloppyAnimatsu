using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// What the renderer, the picking code and the gizmo need from a view camera: a view and a
/// projection, the eye (to know which side of a handle faces the viewer), the world height the
/// view covers (so handles keep their screen size) and the aspect a photo view is framed to.
/// <see cref="OrbitCamera"/> is the editor's own orbit view; <see cref="SceneCameraView"/> is one
/// of the scene's cameras.
/// </summary>
public interface IViewCamera
{
    Matrix4x4 View { get; }
    Matrix4x4 Projection(float aspect);
    Vector3 Eye { get; }
    /// <summary>The direction the view looks along: the normal of its image plane.</summary>
    Vector3 Forward { get; }
    float ViewHeightAt(Vector3 world);
    Vector2? Project(Vector3 world, Vector2 viewport);
    /// <summary>The width / height the view is framed to (a photo view), or null to fill the viewport.</summary>
    float? FrameAspect { get; }
}

/// <summary>
/// One of the scene's cameras as a render view:
/// the fixed Perspective and Photo cameras (their <c>LTM</c> is the camera pose, with
/// <c>_matrix</c> as the fallback), the six orthographic views (their <c>LTM</c> holds the axis
/// and centre, <c>CentimetersInHeight</c> the height), and an animated camera at one frame (its
/// evaluated world matrix and FOV).
/// </summary>
public sealed class SceneCameraView : IViewCamera
{
    /// <summary>The vertical FOV of the fixed perspective cameras.</summary>
    public const float DefaultFov = 30f;

    private readonly float _fov;
    private readonly float _orthoHeight;
    private readonly bool _ortho;
    private readonly Vector3 _forward;
    private float _near = 0.01f, _far = 1e6f;

    private SceneCameraView(Matrix4x4 view, Vector3 eye, Vector3 forward, float fov, float orthoHeight, bool ortho, float? frameAspect)
    {
        View = view;
        Eye = eye;
        _forward = forward;
        _fov = fov;
        _orthoHeight = orthoHeight;
        _ortho = ortho;
        FrameAspect = frameAspect;
    }

    public Matrix4x4 View { get; }
    public Vector3 Eye { get; }
    public Vector3 Forward => _forward;
    public float? FrameAspect { get; }

    /// <summary>An orthographic view: it cannot be orbited, but it is panned and zoomed.</summary>
    public bool IsOrthographic => _ortho;

    /// <summary>The world height an orthographic camera shows when it stores none.</summary>
    public const float DefaultOrthoHeight = 500f;

    /// <summary>The smallest and largest world height a zoom leaves an orthographic view.</summary>
    public const float MinOrthoHeight = 1f, MaxOrthoHeight = 1e6f;

    /// <summary>
    /// The world offset of an orthographic view after a pointer drag of <paramref name="delta"/>
    /// DIP in a view <paramref name="viewHeight"/> DIP tall showing <paramref name="height"/>
    /// world units: the scene follows the pointer, so the centre moves against the drag (and the
    /// screen's y runs down).
    /// </summary>
    public static Vector2 PannedOffset(Vector2 offset, float height, Vector2 delta, float viewHeight)
    {
        if (height <= 0) height = DefaultOrthoHeight;
        float scale = height / MathF.Max(viewHeight, 1f);
        return new Vector2(offset.X - delta.X * scale, offset.Y + delta.Y * scale);
    }

    /// <summary>The world height of an orthographic view after a zoom by <paramref name="factor"/> (below 1 zooms in).</summary>
    public static float ZoomedHeight(float height, float factor)
    {
        if (height <= 0) height = DefaultOrthoHeight;
        return Math.Clamp(height * factor, MinOrthoHeight, MaxOrthoHeight);
    }

    /// <summary>The fixed Perspective camera, from its stored view pose.</summary>
    public static SceneCameraView Perspective(Camera camera) => FromPose(camera.ViewPose, DefaultFov, null);

    /// <summary>The Photo camera: the stored pose, framed to the camera's ratio.</summary>
    public static SceneCameraView Photo(PerspectiveCamera camera) =>
        FromPose(camera.ViewPose, DefaultFov, camera.AspectRatio > 1e-4f ? camera.AspectRatio : 16f / 9f);

    /// <summary>
    /// An animated camera at one frame: its evaluated world matrix is the camera pose and
    /// <paramref name="fov"/> its vertical FOV; the frame is its Mode/Ratio aspect.
    /// </summary>
    public static SceneCameraView Animated(CameraAnimated camera, Matrix4x4 world, float fov)
    {
        var view = FromPose(world, fov, camera.AspectRatio);
        // The camera's own clip planes (Near Clip / Far Clip in the Properties panel).
        float near = camera.NearClipPlane, far = camera.FarClipPlane;
        if (near > 0f && far > near && float.IsFinite(far)) (view._near, view._far) = (near, far);
        return view;
    }

    private static SceneCameraView FromPose(Matrix4x4 world, float fov, float? frameAspect)
    {
        if (!Matrix4x4.Invert(world, out var view)) view = Matrix4x4.Identity;
        // The third row of a camera pose is its backward axis (right x up), so forward is its negation.
        var backward = new Vector3(world.M31, world.M32, world.M33);
        var forward = backward.LengthSquared() > 1e-12f ? -Vector3.Normalize(backward) : -Vector3.UnitZ;
        return new SceneCameraView(view, world.Translation, forward, fov, 0f, false, frameAspect);
    }

    /// <summary>
    /// An orthographic view of <paramref name="camera"/>: its <c>LTM</c> is the view pose (the
    /// 180° Back rotation, the -X look of Right, the centre the camera was last framed on), the
    /// view is its inverse, <c>CentimetersInHeight</c> is the world height and the world offsets
    /// pan it. The samples store garbage or zero in <c>CameraType</c>,
    /// so it is not used.
    /// </summary>
    public static SceneCameraView Ortho(OrthoCamera camera)
    {
        var pose = camera.WorldMatrix;
        if (pose == default) pose = camera.CameraMatrix;
        if (!Matrix4x4.Invert(pose, out var view)) view = Matrix4x4.Identity;
        var backward = new Vector3(pose.M31, pose.M32, pose.M33);
        var forward = backward.LengthSquared() > 1e-12f ? -Vector3.Normalize(backward) : -Vector3.UnitZ;
        if (camera.WorldOffset != Vector2.Zero)
            view *= Matrix4x4.CreateTranslation(-camera.WorldOffset.X, -camera.WorldOffset.Y, 0);
        float height = camera.CentimetersInHeight <= 0 ? 500f : camera.CentimetersInHeight;
        // Put the gizmo's eye behind the view centre along the look direction.
        return new SceneCameraView(view, pose.Translation - forward * 10000f, forward, DefaultFov, height, true, null);
    }

    /// <summary>
    /// An orthographic view from the standard axis conventions (front, back, right, left, top,
    /// bottom), used by the tests; the scene cameras use <see cref="Ortho(OrthoCamera)"/>.
    /// </summary>
    public static SceneCameraView Ortho(int cameraType, float centimetersInHeight, Vector2 worldOffset)
    {
        // The axis the view looks along and its up vector, for synthetic views and tests; the
        // scene's own orthographic cameras use their LTM shape (Ortho(OrthoCamera)).
        var (eye, up) = cameraType switch
        {
            0 => (new Vector3(0, 0, 10000), Vector3.UnitY),   // Front: from +Z, looking -Z
            1 => (new Vector3(0, 0, -10000), Vector3.UnitY),  // Back: from -Z, looking +Z
            2 => (new Vector3(10000, 0, 0), Vector3.UnitY),   // Right: from +X, looking -X
            3 => (new Vector3(-10000, 0, 0), Vector3.UnitY),  // Left: from -X, looking +X
            4 => (new Vector3(0, 10000, 0), -Vector3.UnitZ),  // Top: from +Y, looking -Y, +X right
            _ => (new Vector3(0, -10000, 0), Vector3.UnitZ),  // Bottom: from -Y, looking +Y, +X right
        };
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, up);
        // The world offset moves the view's centre (a positive X offset looks further right).
        view *= Matrix4x4.CreateTranslation(-worldOffset.X, -worldOffset.Y, 0);
        return new SceneCameraView(view, eye, Vector3.Normalize(-eye), DefaultFov, centimetersInHeight, true, null);
    }

    public Matrix4x4 Projection(float aspect)
    {
        if (_ortho)
        {
            float width = _orthoHeight * Math.Max(aspect, 0.01f);
            return Matrix4x4.CreateOrthographicOffCenter(-width / 2, width / 2, -_orthoHeight / 2, _orthoHeight / 2, -1e6f, 1e6f);
        }
        return Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(_fov, 0.01f, 179f) * MathF.PI / 180f, Math.Max(aspect, 0.01f), _near, _far);
    }

    public float ViewHeightAt(Vector3 world)
    {
        if (_ortho) return _orthoHeight;
        float depth = Math.Max(Vector3.Dot(world - Eye, _forward), 0.01f);
        return 2 * depth * MathF.Tan(_fov * MathF.PI / 360f);
    }

    public Vector2? Project(Vector3 world, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), View * Projection(viewport.X / Math.Max(viewport.Y, 1e-3f)));
        if (clip.W <= 1e-6f) return null;
        return new Vector2((clip.X / clip.W + 1) / 2 * viewport.X, (1 - clip.Y / clip.W) / 2 * viewport.Y);
    }
}


