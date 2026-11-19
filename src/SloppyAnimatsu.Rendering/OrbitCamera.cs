using System.Numerics;

namespace SloppyAnimatsu.Rendering;

/// <summary>A Y-up camera orbiting a target point.</summary>
public sealed class OrbitCamera : IViewCamera
{
    /// <summary>The orbit view always fills the viewport.</summary>
    public float? FrameAspect => null;

    /// <summary>
    /// The pitch limit: straight up and straight down. The view basis is built from yaw and pitch
    /// directly (<see cref="Up"/>), so the poles have no look-at singularity and the axis cube's
    /// Top and Bottom views are exact.
    /// </summary>
    public const float MaxPitch = MathF.PI / 2;

    /// <summary>
    /// A flat (orthographic) view: the same framing as the perspective view at the target's
    /// depth, with parallel projection. Orbiting the camera turns it back into a perspective view;
    /// panning and zooming keep it.
    /// </summary>
    public bool Orthographic { get; set; }

    public Vector3 Target { get; set; }
    public float Distance { get; set; } = 5;

    /// <summary>Rotation about Y in radians; 0 looks down -Z.</summary>
    public float Yaw { get; set; } = 0.6f;

    /// <summary>Elevation in radians; positive looks down on the target.</summary>
    public float Pitch { get; set; } = 0.3f;

    public float FieldOfView { get; set; } = MathF.PI / 4;

    public Vector3 Eye =>
        Target + Distance * new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>The direction the view looks along: from the eye to the target.</summary>
    public Vector3 Forward
    {
        get
        {
            var forward = Target - Eye;
            return forward.LengthSquared() > 1e-12f ? Vector3.Normalize(forward) : -Vector3.UnitZ;
        }
    }

    /// <summary>
    /// The view's up axis on screen, from yaw and pitch alone: the backward axis is the eye
    /// direction and the right axis stays horizontal (<c>(cos yaw, 0, -sin yaw)</c>), so the view
    /// stays defined straight above or below the target (a Top view has -Z up, a Bottom view +Z).
    /// </summary>
    public Vector3 Up
    {
        get
        {
            var back = new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
            var right = new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
            return Vector3.Normalize(Vector3.Cross(back, right));
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Up);

    /// <summary>
    /// The camera's world transform: the inverse of <see cref="View"/>, so its translation is
    /// <see cref="Eye"/> and its local -Z the view direction (the shape a stored blendshape point of
    /// view has).
    /// </summary>
    public Matrix4x4 WorldMatrix =>
        Matrix4x4.Invert(View, out Matrix4x4 world) ? world : Matrix4x4.Identity;

    /// <summary>
    /// Puts the camera where <paramref name="world"/> stands, looking along that transform's -Z with
    /// <paramref name="distance"/> to the target. The yaw and pitch are derived from the direction
    /// <see cref="Eye"/>'s formula builds (<c>(cos pitch sin yaw, sin pitch, cos pitch cos yaw)</c>),
    /// so restoring a camera's own <see cref="WorldMatrix"/> and <see cref="Distance"/> reproduces its
    /// Eye and Target. The field of view is not part of a stored point of view and is left alone.
    /// </summary>
    public void Restore(Matrix4x4 world, float distance)
    {
        Distance = Math.Max(distance, 1e-4f);
        var eye = world.Translation;
        var forward = -Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
        Target = eye + forward * Distance;
        var direction = Vector3.Normalize(eye - Target);
        Pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        Yaw = MathF.Atan2(direction.X, direction.Z);
    }

    /// <summary>A camera restored from a stored point of view (see <see cref="Restore"/>).</summary>
    public static OrbitCamera FromWorldMatrix(Matrix4x4 world, float distance)
    {
        var camera = new OrbitCamera();
        camera.Restore(world, distance);
        return camera;
    }

    public Matrix4x4 Projection(float aspect)
    {
        if (Orthographic)
        {
            // The flat view shows what the perspective view shows at the target's depth; its depth
            // range reaches behind the eye too, so nothing along the axis is clipped.
            float height = FlatHeight;
            float width = height * Math.Max(aspect, 0.01f);
            float depth = FarPlane;
            return Matrix4x4.CreateOrthographic(width, height, -depth, depth);
        }
        float far = FarPlane;
        return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, Math.Max(aspect, 0.01f), NearPlane(far), far);
    }

    /// <summary>
    /// The scene's box, set by the renderer each frame; the clip planes and the closest dolly are
    /// measured from it, so a camera dollied in close (or panned away from its target) still sees
    /// the whole scene behind its target. Empty (min == max) when unknown.
    /// </summary>
    public (Vector3 Min, Vector3 Max) SceneBounds { get; set; }

    private float SceneRadius => (SceneBounds.Max - SceneBounds.Min).Length() / 2;

    /// <summary>
    /// The far clip distance: a hundred times the distance as before, but never short of the far
    /// side of the scene seen from the eye. It was the distance alone, so after a long dolly in the
    /// far plane came closer than the scene and cut it off as if it were too far away.
    /// </summary>
    public float FarPlane
    {
        get
        {
            float far = Distance * 100f;
            float radius = SceneRadius;
            if (radius > 0)
            {
                var centre = (SceneBounds.Min + SceneBounds.Max) / 2;
                far = Math.Max(far, (Vector3.Distance(Eye, centre) + radius) * 1.5f);
            }
            return far;
        }
    }

    /// <summary>The near clip distance: a hundredth of the distance, kept within 1:100000 of <paramref name="far"/>.</summary>
    private float NearPlane(float far) => Math.Max(Distance * 0.01f, far * 1e-5f);

    /// <summary>
    /// The closest the eye comes to its target when dollying in (2% of the scene's radius, or 1e-4
    /// with no scene). Below it a dolly carries the target forward instead of shrinking the distance,
    /// so the camera keeps moving through the scene at a steady pace instead of stalling.
    /// </summary>
    public float MinDistance => SceneRadius > 0 ? Math.Max(SceneRadius * 0.02f, 1e-4f) : 1e-4f;

    /// <summary>The world height a flat view covers: the perspective view's height at the target.</summary>
    public float FlatHeight => 2 * Distance * MathF.Tan(FieldOfView / 2);

    /// <summary>
    /// The half-axis (0 +X .. 5 -Z, as <see cref="AlignTo"/>) the eye sits on when this is a flat
    /// view looking straight along a world axis, else null. Such a view draws its grid in the
    /// plane facing it.
    /// </summary>
    public int? FlatAxis
    {
        get
        {
            if (!Orthographic) return null;
            var dir = Eye - Target;
            if (dir.LengthSquared() < 1e-12f) return null;
            dir = Vector3.Normalize(dir);
            for (int axis = 0; axis < AxisCube.AxisCount; axis++)
                if (Vector3.Dot(dir, AxisCube.Direction(axis)) > 0.9999f) return axis;
            return null;
        }
    }

    /// <summary>
    /// <paramref name="world"/> in viewport coordinates (x right, y down, in the units of
    /// <paramref name="viewport"/>), or null when it is behind the camera.
    /// </summary>
    public Vector2? Project(Vector3 world, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(world, 1), View * Projection(viewport.X / Math.Max(viewport.Y, 1e-3f)));
        if (clip.W <= 1e-6f) return null;
        return new Vector2((clip.X / clip.W + 1) / 2 * viewport.X, (1 - clip.Y / clip.W) / 2 * viewport.Y);
    }

    /// <summary>The height in world units that the whole view covers at <paramref name="world"/>'s depth.</summary>
    public float ViewHeightAt(Vector3 world)
    {
        if (Orthographic) return FlatHeight;
        var forward = Vector3.Normalize(Target - Eye);
        float depth = Math.Max(Vector3.Dot(world - Eye, forward), Distance * 0.01f);
        return 2 * depth * MathF.Tan(FieldOfView / 2);
    }

    /// <summary>Centres on the box and backs off until it fits.</summary>
    public void Frame(Vector3 min, Vector3 max)
    {
        Target = (min + max) / 2;
        float radius = Math.Max((max - min).Length() / 2, 0.01f);
        Distance = radius / MathF.Sin(FieldOfView / 2) * 1.05f;
    }

    /// <summary>
    /// Pans the view so <paramref name="point"/> sits in the middle of it, keeping the yaw, the pitch
    /// and the eye's depth from the point: the eye moves only across the view plane and the point
    /// becomes the orbit target. A point behind the eye
    /// (or nearer than <paramref name="minimalDistance"/>) is looked at from
    /// <paramref name="minimalDistance"/> instead. A flat view keeps its zoom.
    /// </summary>
    public void PanTo(Vector3 point, float minimalDistance)
    {
        _animation = null;
        if (Orthographic)
        {
            Target = point;
            return;
        }
        var forward = Forward;
        float depth = Vector3.Dot(point - Eye, forward);
        Target = point;
        Distance = Math.Max(depth >= minimalDistance ? depth : minimalDistance, 1e-4f);
    }

    /// <summary>
    /// Turns the camera about its target. A flat view becomes a perspective view again, and a
    /// running <see cref="AlignTo"/> turn stops where it is.
    /// </summary>
    public void Orbit(float yaw, float pitch)
    {
        _animation = null;
        Orthographic = false;
        Yaw = WrapAngle(Yaw + yaw);
        Pitch = Math.Clamp(Pitch + pitch, -MaxPitch, MaxPitch);
    }

    /// <summary>
    /// Turns the camera about <paramref name="pivot"/> instead of its target: the eye and the target
    /// both swing about the pivot by the same turn the view makes, so the object at the pivot stays
    /// where it is on screen while the view goes round it (the selection is the pivot in the viewer).
    /// </summary>
    public void OrbitAbout(float yaw, float pitch, Vector3 pivot)
    {
        var (right0, up0, back0) = Frame(Yaw, Pitch);
        var eye = Eye;
        Orbit(yaw, pitch);
        var (right1, up1, back1) = Frame(Yaw, Pitch);
        var d = eye - pivot;
        var turned = right1 * Vector3.Dot(d, right0) + up1 * Vector3.Dot(d, up0) + back1 * Vector3.Dot(d, back0);
        Target = pivot + turned - back1 * Distance;
    }

    /// <summary>The view's right, up and backward axes for a yaw and a pitch.</summary>
    private static (Vector3 Right, Vector3 Up, Vector3 Back) Frame(float yaw, float pitch)
    {
        var back = new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        return (right, Vector3.Normalize(Vector3.Cross(back, right)), back);
    }

    /// <summary>How long (seconds) <see cref="AlignTo"/> takes to turn the camera.</summary>
    public const float AlignSeconds = 0.25f;

    private (float FromYaw, float FromPitch, float ToYaw, float ToPitch, float Elapsed)? _animation;

    /// <summary>Whether an <see cref="AlignTo"/> turn is still running.</summary>
    public bool Animating => _animation != null;

    /// <summary>
    /// The yaw and pitch that look at the target from the side <paramref name="axis"/> names
    /// (<see cref="AxisCube"/> numbering: 0 +X, 1 +Y, 2 +Z, 3 -X, 4 -Y, 5 -Z): the eye moves onto
    /// that half-axis. Straight above or below, the yaw is <paramref name="currentYaw"/> rounded to
    /// a quarter turn, so the image's up stays a world axis.
    /// </summary>
    public static (float Yaw, float Pitch) AxisAngles(int axis, float currentYaw)
    {
        float quarter = WrapAngle(MathF.Round(WrapAngle(currentYaw) / (MathF.PI / 2)) * (MathF.PI / 2));
        return axis switch
        {
            0 => (MathF.PI / 2, 0f),
            3 => (-MathF.PI / 2, 0f),
            2 => (0f, 0f),
            5 => (MathF.PI, 0f),
            1 => (quarter, MaxPitch),
            _ => (quarter, -MaxPitch),
        };
    }

    /// <summary>
    /// Turns the camera about its target until it looks from the side <paramref name="axis"/>
    /// names (<see cref="AxisAngles"/>), over <see cref="AlignSeconds"/> the short way round;
    /// <see cref="Step"/> advances the turn. <paramref name="animate"/> false jumps there.
    /// </summary>
    public void AlignTo(int axis, bool animate = true)
    {
        var (yaw, pitch) = AxisAngles(axis, Yaw);
        if (!animate)
        {
            _animation = null;
            Yaw = yaw;
            Pitch = pitch;
            return;
        }
        // The short way round: the end yaw is placed within half a turn of the current one.
        _animation = (Yaw, Pitch, Yaw + WrapAngle(yaw - Yaw), pitch, 0f);
    }

    /// <summary>Advances a running <see cref="AlignTo"/> turn by <paramref name="seconds"/>; returns whether it still runs.</summary>
    public bool Step(float seconds)
    {
        if (_animation is not { } a) return false;
        float elapsed = a.Elapsed + Math.Max(seconds, 0f);
        float t = Math.Clamp(elapsed / AlignSeconds, 0f, 1f);
        float s = t * t * (3 - 2 * t);
        if (t >= 1)
        {
            Yaw = WrapAngle(a.ToYaw);
            Pitch = a.ToPitch;
            _animation = null;
            return false;
        }
        Yaw = a.FromYaw + (a.ToYaw - a.FromYaw) * s;
        Pitch = a.FromPitch + (a.ToPitch - a.FromPitch) * s;
        _animation = a with { Elapsed = elapsed };
        return true;
    }

    /// <summary>An angle wrapped to (-pi, pi].</summary>
    public static float WrapAngle(float angle)
    {
        float wrapped = MathF.IEEERemainder(angle, 2 * MathF.PI);
        return wrapped <= -MathF.PI ? wrapped + 2 * MathF.PI : wrapped;
    }

    /// <summary>Copies another camera's target, distance, angles, field of view and projection.</summary>
    public void CopyFrom(OrbitCamera other)
    {
        Target = other.Target;
        Distance = other.Distance;
        Yaw = other.Yaw;
        Pitch = other.Pitch;
        FieldOfView = other.FieldOfView;
        Orthographic = other.Orthographic;
        _animation = null;
    }

    /// <summary>Moves the target by a drag of (dx, dy) pixels in a viewport <paramref name="height"/> pixels tall.</summary>
    public void Pan(float dx, float dy, float height)
    {
        float unitsPerPixel = 2 * Distance * MathF.Tan(FieldOfView / 2) / Math.Max(height, 1);
        var view = View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        Target += (-dx * right + dy * up) * unitsPerPixel;
    }

    /// <summary>
    /// Dollies by <paramref name="factor"/> (below 1 is in). The distance shrinks by the factor down
    /// to <see cref="MinDistance"/>; a dolly in past that moves the eye and the target forward
    /// together by at least the step the factor makes at the minimum distance, so zooming in never
    /// stalls. A flat view only changes its scale and stops at the minimum.
    /// </summary>
    public void Zoom(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0) return;
        float min = MinDistance;
        if (factor >= 1 || Orthographic)
        {
            Distance = Math.Max(Distance * factor, Math.Min(min, Distance));
            return;
        }
        float travel = Math.Max(Distance * (1 - factor), min * (1 - factor));
        float distance = Distance - travel;
        if (distance < min)
        {
            var forward = Vector3.Normalize(Target - Eye);
            Target += forward * (min - distance);
            distance = min;
        }
        Distance = distance;
    }

    /// <summary>
    /// The dolly factor of a right drag of <paramref name="delta"/> DIPs: <c>0.998</c> per DIP
    /// shrinks the distance by about a fifth per 100 DIP and about five times over a full viewport
    /// height, so the dolly is fine near zero and still reaches a whole scene in one sweep.
    /// </summary>
    public const float DragZoomPerDip = 0.998f;

    /// <summary>
    /// The factor a right drag of <paramref name="delta"/> dollies by: right and up (a negative y)
    /// zoom in, left and down zoom out, and the two axes add, so an up-right drag zooms fastest.
    /// </summary>
    public static float DragZoomFactor(Vector2 delta) => MathF.Pow(DragZoomPerDip, delta.X - delta.Y);

    /// <summary>Dollies by <see cref="DragZoomFactor"/>: the viewport's right drag.</summary>
    public void DragZoom(Vector2 delta) => Zoom(DragZoomFactor(delta));
}
