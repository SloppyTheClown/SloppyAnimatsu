using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Texture = SloppyAnimatsu.Core.Model.Texture;
using Silk.NET.OpenGL;

namespace SloppyAnimatsu.Rendering;

/// <summary>
/// Draws a <see cref="ScenePose"/> with OpenGL 3.3 core or OpenGL ES 3.0 (ANGLE on Windows).
/// Every call must be made with the GL context current; the host owns the context and framebuffer.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private const EnableCap ProgramPointSize = (EnableCap)0x8642; // desktop GL only

    private const string MeshVertex = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_normal;
        layout(location = 2) in vec3 a_color;
        layout(location = 3) in vec2 a_uv;
        uniform mat4 u_viewProjection;
        uniform vec3 u_eye;
        out vec3 v_normal;
        out vec3 v_color;
        out vec2 v_uv;
        out vec3 v_toEye;
        void main()
        {
            v_normal = a_normal;
            v_color = a_color;
            v_uv = a_uv;
            v_toEye = u_eye - a_position;
            gl_Position = u_viewProjection * vec4(a_position, 1.0);
        }
        """;

    // Headlight Lambert, two-sided. A textured range shows the texture in place of the flat
    // colour. A transparent material's texels keep their alpha (blended; nearly clear texels are
    // cut out so they do not hide what is behind them in the depth buffer); an opaque one ignores
    // alpha.
    private const string MeshFragment = """
        in vec3 v_normal;
        in vec3 v_color;
        in vec2 v_uv;
        in vec3 v_toEye;
        uniform sampler2D u_texture;
        uniform bool u_textured;
        uniform bool u_transparent;
        uniform bool u_lit;
        out vec4 o_color;
        void main()
        {
            vec3 albedo = v_color;
            float alpha = 1.0;
            if (u_textured)
            {
                vec4 texel = texture(u_texture, v_uv);
                if (u_transparent)
                {
                    if (texel.a < 0.02) discard;
                    alpha = texel.a;
                }
                albedo = texel.rgb;
            }
            float d = abs(dot(normalize(v_normal), normalize(v_toEye)));
            o_color = vec4(u_lit ? albedo * (0.25 + 0.75 * d) : albedo, alpha);
        }
        """;

    private const string LineVertex = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_color;
        uniform mat4 u_viewProjection;
        uniform float u_pointSize;
        uniform float u_worldSize;
        uniform float u_pixelsPerUnit;
        out vec3 v_color;
        void main()
        {
            v_color = a_color;
            gl_Position = u_viewProjection * vec4(a_position, 1.0);
            // A joint dot has a world size (DecoratorSizes), so it shrinks with distance.
            gl_PointSize = u_worldSize > 0.0
                ? clamp(u_worldSize * u_pixelsPerUnit / max(gl_Position.w, 1e-4), 2.0, 256.0)
                : u_pointSize;
        }
        """;

    private const string LineFragment = """
        in vec3 v_color;
        uniform float u_alpha;
        uniform int u_round;
        out vec4 o_color;
        void main()
        {
            // Joint dots are round; gl_PointCoord is only read for points.
            if (u_round != 0 && length(gl_PointCoord - vec2(0.5)) > 0.5) discard;
            o_color = vec4(v_color, u_alpha);
        }
        """;

    // A wide line: every segment is a screen-space quad (WideLines.Expand), because a core or ES
    // context only guarantees 1 px glLineWidth. Each vertex carries its own end, the other end and
    // a side of +1/-1; the shader pushes it half the width off the segment, in pixels.
    private const string WideLineVertex = """
        layout(location = 0) in vec3 a_position;
        layout(location = 1) in vec3 a_other;
        layout(location = 2) in float a_side;
        layout(location = 3) in vec3 a_color;
        uniform mat4 u_viewProjection;
        uniform vec2 u_viewport;
        uniform float u_width;
        uniform float u_worldWidth;
        uniform float u_pixelsPerUnit;
        out vec3 v_color;
        void main()
        {
            v_color = a_color;
            vec4 a = u_viewProjection * vec4(a_position, 1.0);
            vec4 b = u_viewProjection * vec4(a_other, 1.0);
            // Clip the segment to the near plane (z = 0 for System.Numerics projections) before
            // working in screen space: an end behind the camera has w <= 0, and pushing it by a
            // width divided by that w threw the quad across half the screen. Both vertices of an
            // end clip the same way, so the quad stays whole.
            if (a.z < 0.0 && b.z < 0.0) { gl_Position = vec4(0.0, 0.0, -2.0, 1.0); return; }
            if (a.z < 0.0) a = mix(a, b, a.z / (a.z - b.z));
            else if (b.z < 0.0) b = mix(b, a, b.z / (b.z - a.z));
            vec2 halfSize = 0.5 * u_viewport;
            vec2 sa = a.xy / max(a.w, 1e-4) * halfSize;
            vec2 sb = b.xy / max(b.w, 1e-4) * halfSize;
            vec2 d = sb - sa;
            float len = length(d);
            vec2 n = len > 1e-6 ? vec2(-d.y, d.x) / len : vec2(0.0, 1.0);
            // A bone has a world width (DecoratorSizes), so it thins with distance.
            float width = u_worldWidth > 0.0 ? clamp(u_worldWidth * u_pixelsPerUnit / max(a.w, 1e-4), 1.0, 64.0) : u_width;
            vec2 offset = n * (a_side * 0.5 * width) / halfSize;
            gl_Position = a + vec4(offset * a.w, 0.0, 0.0);
        }
        """;

    private GL? _gl;
    private bool _gles;
    private uint _meshProgram, _lineProgram, _wideProgram;
    private uint _meshVao, _meshVbo, _lineVao, _lineVbo, _wideVao, _wideVbo;
    private int _uploadedVersion = -1;
    private ScenePose? _uploadedPose;
    private float[] _gridLines = [];
    private float _gridFallback;
    private (int Unit, uint Size, uint Subdivision, float Fallback) _gridKey = (int.MinValue, 0, 0, 0);
    private readonly Dictionary<Texture, uint> _textures = new(ReferenceEqualityComparer.Instance);
    private ScenePose? _texturedPose;
    private int _activeViewIndex;
    private float _pixelsPerUnit;

    public OrbitCamera Camera { get; } = new();

    /// <summary>
    /// The scene camera to render through when <see cref="Views"/> is empty, or null for the
    /// editor's <see cref="Camera"/>. A photo
    /// view is letterboxed to its ratio.
    /// </summary>
    public IViewCamera? SceneView { get; set; }

    /// <summary>
    /// A multi-view layout's views. Empty renders the single
    /// <see cref="SceneView"/> (or the orbit camera) over the whole control; otherwise every view
    /// is drawn in its rectangle.
    /// </summary>
    public IReadOnlyList<RenderViewport> Views { get; set; } = [];

    /// <summary>The focused view; the Camera menu and the gizmo hit test work on it.</summary>
    public int ActiveViewIndex
    {
        get => _activeViewIndex;
        set => _activeViewIndex = Views.Count == 0 ? 0 : Math.Clamp(value, 0, Views.Count - 1);
    }

    public bool HasViews => Views.Count > 0;

    /// <summary>The focused view's rectangle (the whole control when there is one view).</summary>
    public ViewRect ActiveRect => HasViews ? Views[Math.Clamp(ActiveViewIndex, 0, Views.Count - 1)].Rect : new(0, 0, 1, 1);

    /// <summary>The view being rendered: the focused view's camera, else <see cref="SceneView"/>, else the orbit camera.</summary>
    public IViewCamera ActiveView => HasViews
        ? Views[Math.Clamp(ActiveViewIndex, 0, Views.Count - 1)].Camera ?? Camera
        : SceneView ?? Camera;

    /// <summary>Whether the focused view is an orbit camera (a scene camera is fixed).</summary>
    public bool ActiveIsOrbit => ActiveOrbit != null;

    /// <summary>The focused view's orbit camera, or null when it looks through a scene camera.</summary>
    public OrbitCamera? ActiveOrbit => ActiveView as OrbitCamera;

    /// <summary>The orbit camera of view <paramref name="index"/>, or null for a scene camera.</summary>
    public OrbitCamera? OrbitOf(int index) => HasViews
        ? (Views[Math.Clamp(index, 0, Views.Count - 1)].Camera ?? Camera) as OrbitCamera
        : (SceneView ?? Camera) as OrbitCamera;

    /// <summary>Every orbit camera drawn (a view without a camera draws <see cref="Camera"/>).</summary>
    public IEnumerable<OrbitCamera> OrbitCameras()
    {
        int count = Math.Max(Views.Count, 1);
        var seen = new HashSet<OrbitCamera>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < count; i++)
            if (OrbitOf(i) is { } orbit && seen.Add(orbit)) yield return orbit;
    }

    /// <summary>Advances every running axis-cube turn; returns whether one still runs.</summary>
    public bool StepCameras(float seconds)
    {
        bool running = false;
        foreach (var orbit in OrbitCameras()) running |= orbit.Step(seconds);
        return running;
    }

    /// <summary>The part of a viewport of <paramref name="size"/> (DIPs or pixels) the active view is drawn in.</summary>
    public (Vector2 Origin, Vector2 Size) RenderArea(Vector2 size)
    {
        var (x, y, w, h) = ActiveRect.Scale(size.X, size.Y);
        var area = RenderArea(new Vector2(w, h), ActiveView.FrameAspect);
        return (new Vector2(x + area.Origin.X, y + area.Origin.Y), area.Size);
    }

    /// <summary>The drawn area of view <paramref name="index"/> in a viewport of <paramref name="size"/>.</summary>
    public (Vector2 Origin, Vector2 Size) ViewArea(int index, Vector2 size)
    {
        if (!HasViews) return RenderArea(size);
        index = Math.Clamp(index, 0, Views.Count - 1);
        var (x, y, w, h) = Views[index].Rect.Scale(size.X, size.Y);
        var area = RenderArea(new Vector2(w, h), (Views[index].Camera ?? Camera).FrameAspect);
        return (new Vector2(x + area.Origin.X, y + area.Origin.Y), area.Size);
    }

    /// <summary>The index of the view under a point in a viewport of <paramref name="size"/>, or -1.</summary>
    public int ViewAt(Vector2 point, Vector2 size)
    {
        if (!HasViews) return 0;
        for (int i = 0; i < Views.Count; i++)
        {
            var (x, y, w, h) = Views[i].Rect.Scale(size.X, size.Y);
            if (point.X >= x && point.Y >= y && point.X < x + w && point.Y < y + h) return i;
        }
        return -1;
    }

    private static (Vector2 Origin, Vector2 Size) RenderArea(Vector2 size, float? aspect)
    {
        if (aspect is not > 0 || size.X <= 0 || size.Y <= 0) return (Vector2.Zero, size);
        float viewport = size.X / size.Y;
        if (viewport > aspect.Value)
        {
            float w = size.Y * aspect.Value;
            return (new Vector2((size.X - w) / 2, 0), new Vector2(w, size.Y));
        }
        float h = size.X / aspect.Value;
        return (new Vector2(0, (size.Y - h) / 2), new Vector2(size.X, h));
    }

    /// <summary>What to draw; call <see cref="ScenePose.Update"/> on it to change the pose.</summary>
    public ScenePose? Pose { get; set; }

    public bool ShowMesh { get; set; } = true;
    public bool ShowSkeleton { get; set; } = true;
    public bool ShowControllers { get; set; } = true;
    public bool ShowGrid { get; set; } = true;

    // View & Preferences: shading and decorators.
    /// <summary>Gray polygon edges over the shaded meshes.</summary>
    public bool Wireframe { get; set; }
    /// <summary>Whether textured material slots show their texture; off shows the flat material colour.</summary>
    public bool Textured { get; set; } = true;
    /// <summary>Headlight shading; off draws the albedo as it is.</summary>
    public bool Lit { get; set; } = true;
    /// <summary>The dot at each joint/controller (with <see cref="ShowSkeleton"/>).</summary>
    public bool ShowJoints { get; set; } = true;
    /// <summary>
    /// Set while an animation plays: the joint dots, the controller handles (IK rings,
    /// reverse-foot soles, shoe locators, looker discs), the selection dots and the gizmo are
    /// not drawn; the mesh and the bone lines still are.
    /// </summary>
    public bool HideHandles { get; set; }
    /// <summary>The bone lines between joints (with <see cref="ShowSkeleton"/>).</summary>
    public bool ShowBones { get; set; } = true;
    /// <summary>An axis triad at each controller's pivot (<see cref="ScenePose.PivotLines"/>).</summary>
    public bool ShowPivots { get; set; }
    /// <summary>
    /// Extra decorator lines (world-space line list, <see cref="ScenePose.LineVertexFloats"/>
    /// floats per vertex), drawn over the mesh: the blendshape interactive faces.
    /// </summary>
    public float[] DecoratorLines { get; set; } = [];

    /// <summary>Wireframe edge colour: the default <c>WireframeFrontColor</c> (C0 C0 C0).</summary>
    public Vector3 WireframeColor { get; set; } = new(0.75f, 0.75f, 0.75f);

    private float[] _wireLines = [];
    private ScenePose? _wirePose;
    private int _wireVersion = -1;
    public Vector3 Background { get; set; } = new(0.16f, 0.17f, 0.19f);

    /// <summary>The band around a photo view (the part of the viewport its aspect does not fill).</summary>
    public Vector3 PhotoBorder { get; set; } = new(0.07f, 0.075f, 0.085f);

    /// <summary>The band between and around the views of a multi-view layout.</summary>
    public Vector3 Separator { get; set; } = new(0.07f, 0.075f, 0.085f);

    /// <summary>The outline between views, lighter than the band so the split is visible.</summary>
    public Vector3 ViewBorder { get; set; } = new(0.30f, 0.31f, 0.34f);

    /// <summary>The outline of the focused view in a multi-view layout.</summary>
    public Vector3 FocusBorder { get; set; } = new(0.95f, 0.62f, 0.2f);

    /// <summary>The selected controller (the current object), drawn as a large white point over the skeleton; null for none.</summary>
    public Object3D? Selected { get; set; }

    /// <summary>The other selected controllers, drawn as smaller grey points.</summary>
    public IReadOnlyList<Object3D> AlsoSelected { get; set; } = [];

    /// <summary>Viewer choice: a selected IK ring/sole is drawn white like a selected joint point; others selected light grey.</summary>
    private static readonly Vector3 SelectedIKColor = new(1f, 1f, 1f);
    private static readonly Vector3 SelectedIKOthersColor = new(0.85f, 0.85f, 0.6f);

    /// <summary>Viewer choice: a selected pinned (active) IK keeps its red, lit; others selected a softer lit red.</summary>
    private static readonly Vector3 SelectedActiveIKColor = new(1f, 0.42f, 0.36f);
    private static readonly Vector3 SelectedActiveIKOthersColor = new(1f, 0.6f, 0.52f);

    /// <summary>
    /// Transient points over the scene, drawn as coloured dots over everything (the AutoRigger
    /// dialog's hot points, which the app also draws as unsaved helpers).
    /// </summary>
    public IReadOnlyList<SceneMarker> Markers { get; set; } = [];

    /// <summary>
    /// The blendshape zone a click picked, as a triangle list of world-space line vertices
    /// (position and colour, <see cref="ScenePose.LineVertexFloats"/> floats each); empty for none
    /// </summary>
    public float[] ZoneTriangles { get; set; } = [];

    /// <summary>
    /// The selected skinned-mesh control points, as world-space
    /// points with their colour (<see cref="ScenePose.LineVertexFloats"/> floats each), drawn over the
    /// scene; the last selected one is white, the others the selection colour.
    /// </summary>
    public float[] VertexPoints { get; set; } = [];

    /// <summary>The edit handles, drawn last, on top of everything.</summary>
    public TransformGizmo Gizmo { get; } = new();

    /// <summary>
    /// Decodes a texture's image file (<see cref="Texture.Data"/>); null when it cannot. Without a
    /// decoder, or for images it rejects, meshes show their flat material colour.
    /// </summary>
    public Func<byte[], DecodedImage?>? ImageDecoder { get; set; }

    public bool IsInitialized => _gl != null;

    /// <param name="gles">Whether the context is OpenGL ES (selects the shader dialect).</param>
    public void Initialize(GL gl, bool gles)
    {
        _gl = gl;
        _gles = gles;
        _meshProgram = Program(MeshVertex, MeshFragment);
        _lineProgram = Program(LineVertex, LineFragment);
        (_meshVao, _meshVbo) = VertexArray([3, 3, 3, 2]);
        (_lineVao, _lineVbo) = VertexArray([3, 3]);
        _wideProgram = Program(WideLineVertex, LineFragment);
        (_wideVao, _wideVbo) = VertexArray([3, 3, 1, 3]);
        _uploadedVersion = -1;
        _uploadedPose = null;
        _textures.Clear();
        _texturedPose = null;
    }

    /// <summary>Points the camera at the pose's bounds and sizes the ground grid to them.</summary>
    public void FrameAll()
    {
        if (Pose == null) return;
        var (min, max) = Pose.Bounds;
        // An empty scene (File > New Scene) has no bounds; frame a default area so the ground
        // grid is visible instead of collapsing the camera onto the origin.
        if ((max - min).LengthSquared() < 1e-6f)
        {
            min = new Vector3(-200f, 0f, -200f);
            max = new Vector3(200f, 100f, 200f);
        }
        Camera.Frame(min, max);
        // Every other orbit view frames the same box from its own direction.
        foreach (var orbit in OrbitCameras())
            if (orbit != Camera) orbit.Frame(min, max);
        _gridFallback = Math.Max(Math.Max(max.X - min.X, max.Z - min.Z), max.Y - min.Y);
    }

    /// <summary>
    /// The viewport's <c>CenterCamera</c> shortcut (F): pans the focused view so the selection sits
    /// in its middle, keeping the view's angle and its depth to the selection
    /// (<see cref="OrbitCamera.PanTo"/>); a selection behind the eye is shown from the distance that
    /// just fits it. With nothing selected, frames the whole scene (<see cref="FrameAll"/> for every
    /// view, each keeping its angle). A camera rig's glyph counts as its camera's or target's
    /// extent. Returns whether there was a selection to focus on.
    /// </summary>
    public bool FocusSelection()
    {
        var pose = Pose;
        if (pose == null) return false;
        var objects = new List<Object3D>();
        if (Selected != null) objects.Add(Selected);
        foreach (var o in AlsoSelected)
            if (!objects.Contains(o)) objects.Add(o);
        if (objects.Count == 0)
        {
            FrameAll();
            return false;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        void Grow(Vector3 p)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        foreach (var o in objects)
        {
            var glyph = pose.Cameras.FirstOrDefault(g => ReferenceEquals(g.Camera, o));
            if (glyph.Camera != null)
            {
                foreach (var (a, b, arrow) in CameraShapes.Segments(glyph))
                    if (!arrow) { Grow(a); Grow(b); }
                continue;
            }
            Grow(pose.WorldOf(o).Translation);
        }
        var (sceneMin, sceneMax) = pose.Bounds;
        float pad = Math.Max((sceneMax - sceneMin).Length() * 0.05f, 1e-2f);
        var target = ActiveOrbit ?? Camera;
        // Pan only: the view keeps its angle and depth; a selection behind the eye is shown from
        // the distance that just fits it (padded).
        float radius = Math.Max(((max - min).Length() / 2) + pad, 0.01f);
        float minimal = radius / MathF.Sin(target.FieldOfView / 2) * 1.05f;
        target.PanTo((min + max) / 2, minimal);
        return true;
    }

    public void Render(int framebuffer, int width, int height)
    {
        var gl = _gl ?? throw new InvalidOperationException("Initialize first.");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
        // Upload the pose once; every view draws the same buffers.
        var pose = Pose;
        if (pose != null && (pose != _uploadedPose || pose.Version != _uploadedVersion))
        {
            Upload(_meshVbo, pose.MeshVertices);
            _uploadedPose = pose;
            _uploadedVersion = pose.Version;
        }
        if (!_gles) gl.Enable(ProgramPointSize);

        // The gaps between views show the separator colour.
        gl.Disable(EnableCap.ScissorTest);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(Separator.X, Separator.Y, Separator.Z, 1);
        gl.ClearDepth(1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (!HasViews)
        {
            DrawView(SceneView ?? Camera, new ViewRect(0, 0, 1, 1), width, height, pose);
        }
        else
        {
            for (int i = 0; i < Views.Count; i++)
                DrawView(Views[i].Camera ?? Camera, Views[i].Rect, width, height, pose);
            if (Views.Count > 1)
            {
                foreach (var view in Views) DrawBorder(view.Rect, ViewBorder, width, height, 1);
                DrawBorder(ActiveRect, FocusBorder, width, height, 2);
            }
        }
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    /// <summary>Draws one view in its rectangle: clears it (letterboxing a photo view) and renders the pose.</summary>
    private void DrawView(IViewCamera view, ViewRect rect, int width, int height, ScenePose? pose)
    {
        var gl = _gl!;
        var (vx, vy, vw, vh) = rect.Scale(width, height);
        int rx = (int)MathF.Round(vx), ry = (int)MathF.Round(vy);
        int rw = Math.Max(1, (int)MathF.Round(vw)), rh = Math.Max(1, (int)MathF.Round(vh));
        int viewGlY = height - ry - rh;
        var area = RenderArea(new Vector2(rw, rh), view.FrameAspect);
        int ax = rx + (int)MathF.Round(area.Origin.X), ay = ry + (int)MathF.Round(area.Origin.Y);
        int aw = Math.Max(1, (int)MathF.Round(area.Size.X)), ah = Math.Max(1, (int)MathF.Round(area.Size.Y));
        int areaGlY = height - ay - ah;

        // A photo view draws in a centred letterbox with a darker band around it.
        bool framed = view.FrameAspect is > 0;
        gl.Viewport(rx, viewGlY, (uint)rw, (uint)rh);
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(rx, viewGlY, (uint)rw, (uint)rh);
        var band = framed ? PhotoBorder : Background;
        gl.ClearColor(band.X, band.Y, band.Z, 1);
        gl.ClearDepth(1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Scissor(ax, areaGlY, (uint)aw, (uint)ah);
        gl.ClearColor(Background.X, Background.Y, Background.Z, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Disable(EnableCap.ScissorTest);
        gl.Viewport(ax, areaGlY, (uint)aw, (uint)ah);

        if (view is OrbitCamera orbit && pose != null) orbit.SceneBounds = pose.Bounds;
        var projection = view.Projection(aw / (float)Math.Max(ah, 1));
        var viewProjection = view.View * projection;
        // Pixels per scene unit at clip w = 1 (perspective: per unit of view depth), for the
        // world-sized joint dots and bones.
        _pixelsPerUnit = projection.M22 * ah * 0.5f;
        var sizes = DecoratorSizes.For(pose?.Model.Scene);

        // A flat view looking along a world axis draws the grid in the plane facing it, behind
        // everything (viewport-axis-cube.md); the edge-on ground grid is then left out.
        int? flatAxis = ShowGrid && view is OrbitCamera orbitView ? orbitView.FlatAxis : null;
        if (flatAxis is { } planeAxis)
        {
            RefreshGrid(pose);
            if (_gridLines.Length > 0)
            {
                gl.Disable(EnableCap.DepthTest);
                gl.UseProgram(_lineProgram);
                SetMatrix(_lineProgram, "u_viewProjection", viewProjection);
                gl.BindVertexArray(_lineVao);
                DrawLines(PrimitiveType.Lines, PlaneGrid(_gridLines, planeAxis), 1);
            }
        }

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        if (pose != null && ShowMesh)
        {
            gl.UseProgram(_meshProgram);
            SetMatrix(_meshProgram, "u_viewProjection", viewProjection);
            var eye = view.Eye;
            gl.Uniform3(gl.GetUniformLocation(_meshProgram, "u_eye"), eye.X, eye.Y, eye.Z);
            gl.Uniform1(gl.GetUniformLocation(_meshProgram, "u_texture"), 0);
            int textured = gl.GetUniformLocation(_meshProgram, "u_textured");
            gl.Uniform1(gl.GetUniformLocation(_meshProgram, "u_lit"), Lit ? 1 : 0);
            if (Wireframe)
            {
                // Pushes the faces back a little so the edge lines drawn on them win the depth test.
                gl.Enable(EnableCap.PolygonOffsetFill);
                gl.PolygonOffset(1f, 1f);
            }
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindVertexArray(_meshVao);
            int transparentLocation = gl.GetUniformLocation(_meshProgram, "u_transparent");
            // Opaque slots first, then the slots whose texture alpha is drawn, blended over them
            bool anyTransparent = false;
            foreach (var range in pose.VisibleRanges)
            {
                uint id = Textured ? TextureOf(pose, range.Texture) : 0;
                if (id != 0 && range.Transparent)
                {
                    anyTransparent = true;
                    continue;
                }
                DrawRange(range, id, false);
            }
            if (anyTransparent)
            {
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                foreach (var range in pose.VisibleRanges)
                {
                    uint id = Textured ? TextureOf(pose, range.Texture) : 0;
                    if (id != 0 && range.Transparent) DrawRange(range, id, true);
                }
                gl.Disable(EnableCap.Blend);
            }
            gl.Uniform1(transparentLocation, 0);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            gl.Disable(EnableCap.PolygonOffsetFill);

            void DrawRange(MeshRange range, uint id, bool transparent)
            {
                gl.BindTexture(TextureTarget.Texture2D, id);
                if (id != 0)
                {
                    // Per material: nearest (Blender's "Closest") or smooth sampling.
                    int min = range.Nearest ? (int)TextureMinFilter.NearestMipmapNearest : (int)TextureMinFilter.LinearMipmapLinear;
                    int mag = range.Nearest ? (int)TextureMagFilter.Nearest : (int)TextureMagFilter.Linear;
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, min);
                    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, mag);
                }
                gl.Uniform1(textured, id != 0 ? 1 : 0);
                gl.Uniform1(transparentLocation, transparent ? 1 : 0);
                gl.DrawArrays(PrimitiveType.Triangles, range.First, (uint)range.Count);
            }
        }

        gl.UseProgram(_lineProgram);
        SetMatrix(_lineProgram, "u_viewProjection", viewProjection);
        gl.BindVertexArray(_lineVao);
        if (ShowGrid && flatAxis == null)
        {
            RefreshGrid(pose);
            if (_gridLines.Length > 0) DrawLines(PrimitiveType.Lines, _gridLines, 1);
        }
        if (pose != null && ShowMesh && Wireframe) DrawLines(PrimitiveType.Lines, WireLines(pose), 1);

        // IK rings and reverse-foot soles are depth-tested against the mesh, so the model hides
        // the parts behind it; the skeleton and the markers below are drawn over the mesh, as in
        // usual.
        bool controllers = ShowControllers && !HideHandles;
        if (pose != null && controllers) DrawLines(PrimitiveType.Triangles, pose.ControllerTriangles, 1);
        gl.Disable(EnableCap.DepthTest);
        // Animated cameras: a wireframe camera with an arrow to its target (SloppyAnimatsu's own shape,
        // in place of the target disc); blue while the target is locked.
        if (pose != null && controllers && pose.Cameras.Count > 0)
        {
            var cameraLines = CameraShapes.Lines(pose.Cameras, view.Eye, Selected, AlsoSelected);
            if (cameraLines.Length > 0) DrawWideLines(cameraLines, 2, viewProjection, aw, ah);
        }
        if (DecoratorLines.Length > 0) DrawLines(PrimitiveType.Lines, DecoratorLines, 1);
        if (ZoneTriangles.Length > 0) DrawLines(PrimitiveType.Lines, ZoneTriangles, 1);
        if (pose != null && ShowSkeleton)
        {
            if (ShowBones) DrawWideLines(pose.SkeletonLines, 0, viewProjection, aw, ah, worldWidth: sizes.BoneWidth);
            if (ShowJoints && !HideHandles) DrawLines(PrimitiveType.Points, pose.SkeletonPoints, 0, round: true, worldSize: sizes.Joint);
        }
        if (pose != null && ShowPivots && pose.PivotLines.Length > 0) DrawWideLines(pose.PivotLines, 2, viewProjection, aw, ah);
        // Shoe locators: yellow plus signs, 3 px wide as the app draws them (controller-display.md
        // "Shoe locators").
        if (pose != null && controllers && pose.ShoeMarkers.Length > 0)
            DrawWideLines(pose.ShoeMarkers, DecoratorSizes.ShoeLocatorWidth, viewProjection, aw, ah);
        // A selected IK controller lights up its own ring or sole, not the joint it sits on.
        if (pose != null && pose.Character != null && controllers && (Selected is ControllerIK || AlsoSelected.Any(o => o is ControllerIK)))
        {
            var evaluator = pose.Evaluator(pose.Character);
            var lit = new List<float>();
            // A pinned (active) one stays red, lit.
            ControllerShapes.WriteSelected(lit, pose.Character, evaluator, pose.ControllerWorlds, AlsoSelected, SelectedIKOthersColor,
                pose.Animation, pose.Time, SelectedActiveIKOthersColor);
            if (Selected != null)
                ControllerShapes.WriteSelected(lit, pose.Character, evaluator, pose.ControllerWorlds, [Selected], SelectedIKColor,
                    pose.Animation, pose.Time, SelectedActiveIKColor);
            if (lit.Count > 0)
            {
                gl.Enable(EnableCap.DepthTest);
                DrawLines(PrimitiveType.Triangles, lit.ToArray(), 1);
                gl.Disable(EnableCap.DepthTest);
            }
        }
        if (pose != null && pose.Character != null && !HideHandles && AlsoSelected.Count > 0)
        {
            var evaluator = pose.Evaluator(pose.Character);
            var points = new List<float>();
            foreach (var o in AlsoSelected)
                if (o is not ControllerIK && !CameraShapes.IsGlyphPart(o) && evaluator.IndexOf(o) >= 0 && pose.WorldOf(o).Translation is var q)
                    points.AddRange([q.X, q.Y, q.Z, 0.7f, 0.7f, 0.7f]);
            if (points.Count > 0) DrawLines(PrimitiveType.Points, points.ToArray(), 0, round: true, worldSize: sizes.AlsoSelected);
        }
        if (pose != null && !HideHandles && Selected != null && Selected is not ControllerIK && !CameraShapes.IsGlyphPart(Selected) && pose.Character != null && pose.Evaluator(pose.Character).IndexOf(Selected) >= 0)
        {
            var p = pose.WorldOf(Selected).Translation;
            DrawLines(PrimitiveType.Points, [p.X, p.Y, p.Z, 1, 1, 1], 0, round: true, worldSize: sizes.Selected);
        }
        if (Markers.Count > 0)
        {
            var points = new List<float>(Markers.Count * 6);
            foreach (SceneMarker marker in Markers)
                points.AddRange([marker.Position.X, marker.Position.Y, marker.Position.Z,
                                 marker.Colour.X, marker.Colour.Y, marker.Colour.Z]);
            DrawLines(PrimitiveType.Points, points.ToArray(), 10);
        }
        if (VertexPoints.Length > 0) DrawLines(PrimitiveType.Points, VertexPoints, 5);
        if (Gizmo.Visible && !HideHandles)
        {
            var handles = new List<float>();
            Gizmo.WriteTriangles(handles, view);
            DrawLines(PrimitiveType.Triangles, handles.ToArray(), 1);
        }
    }

    /// <summary>The edges of every visible triangle as a line list, rebuilt when the pose changes.</summary>
    private float[] WireLines(ScenePose pose)
    {
        if (ReferenceEquals(pose, _wirePose) && pose.Version == _wireVersion) return _wireLines;
        const int F = ScenePose.MeshVertexFloats;
        var source = pose.MeshVertices;
        int count = 0;
        foreach (var range in pose.VisibleRanges) count += range.Count;
        var lines = new float[count * 2 * ScenePose.LineVertexFloats];
        var c = WireframeColor;
        int o = 0;
        foreach (var range in pose.VisibleRanges)
        {
            for (int v = range.First; v + 2 < range.First + range.Count; v += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = (v + k) * F, b = (v + (k + 1) % 3) * F;
                    lines[o++] = source[a]; lines[o++] = source[a + 1]; lines[o++] = source[a + 2];
                    lines[o++] = c.X; lines[o++] = c.Y; lines[o++] = c.Z;
                    lines[o++] = source[b]; lines[o++] = source[b + 1]; lines[o++] = source[b + 2];
                    lines[o++] = c.X; lines[o++] = c.Y; lines[o++] = c.Z;
                }
            }
        }
        if (o != lines.Length) Array.Resize(ref lines, o);
        _wireLines = lines;
        _wirePose = pose;
        _wireVersion = pose.Version;
        return lines;
    }

    /// <summary>Marks a view with an outline of the given colour and thickness.</summary>
    private void DrawBorder(ViewRect rect, Vector3 color, int width, int height, int thickness)
    {
        var gl = _gl!;
        var (x, y, w, h) = rect.Scale(width, height);
        int px = (int)MathF.Round(x), py = (int)MathF.Round(y);
        int pw = (int)MathF.Round(w), ph = (int)MathF.Round(h);
        int t = Math.Max(1, thickness);
        gl.Enable(EnableCap.ScissorTest);
        gl.ClearColor(color.X, color.Y, color.Z, 1);
        (int X, int Y, int W, int H)[] bands =
        [
            (px, height - py - t, pw, t),            // top
            (px, height - py - ph, pw, t),           // bottom
            (px, height - py - ph, t, ph),           // left
            (px + pw - t, height - py - ph, t, ph),  // right
        ];
        foreach (var band in bands)
        {
            if (band.W <= 0 || band.H <= 0) continue;
            gl.Scissor(band.X, band.Y, (uint)band.W, (uint)band.H);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }
        gl.Disable(EnableCap.ScissorTest);
    }

    /// <summary>The bound framebuffer as RGBA rows, top row first.</summary>
    public byte[] ReadPixels(int width, int height)
    {
        var gl = _gl ?? throw new InvalidOperationException("Initialize first.");
        var pixels = new byte[width * height * 4];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        var flipped = new byte[pixels.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Array.Copy(pixels, (height - 1 - y) * stride, flipped, y * stride, stride);
        return flipped;
    }

    public void Dispose()
    {
        if (_gl is not { } gl) return;
        gl.DeleteBuffer(_meshVbo);
        gl.DeleteBuffer(_lineVbo);
        gl.DeleteBuffer(_wideVbo);
        gl.DeleteVertexArray(_meshVao);
        gl.DeleteVertexArray(_lineVao);
        gl.DeleteVertexArray(_wideVao);
        gl.DeleteProgram(_meshProgram);
        gl.DeleteProgram(_lineProgram);
        gl.DeleteProgram(_wideProgram);
        DeleteTextures();
        _gl = null;
    }

    /// <summary>The GL texture for <paramref name="texture"/>, uploaded on first use; 0 when there is none.</summary>
    private uint TextureOf(ScenePose pose, Texture? texture)
    {
        if (pose != _texturedPose)
        {
            DeleteTextures();
            _texturedPose = pose;
        }
        if (texture == null) return 0;
        if (_textures.TryGetValue(texture, out uint id)) return id;

        DecodedImage? image = null;
        try
        {
            var data = texture.Data;
            if (data.Length > 0) image = ImageDecoder?.Invoke(data);
        }
        catch (Exception)
        {
            image = null; // undecodable: fall back to the flat colour
        }
        if (image is { } decoded && decoded.Width > 0 && decoded.Height > 0 && decoded.Rgba.Length >= decoded.Width * decoded.Height * 4)
            id = Upload(decoded);
        _textures[texture] = id;
        return id;
    }

    private uint Upload(DecodedImage image)
    {
        var gl = _gl!;
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)image.Width, (uint)image.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, image.Rgba.AsSpan());
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return id;
    }

    private void DeleteTextures()
    {
        foreach (uint id in _textures.Values)
            if (id != 0) _gl!.DeleteTexture(id);
        _textures.Clear();
    }

    /// <summary>
    /// Draws a <see cref="PrimitiveType.Lines"/> array <paramref name="width"/> pixels wide through the
    /// wide-line program, then puts the plain line program back.
    /// </summary>
    private void DrawWideLines(float[] lines, float width, Matrix4x4 viewProjection, int viewportWidth, int viewportHeight, float worldWidth = 0f)
    {
        if (lines.Length == 0) return;
        if (worldWidth <= 0f && width <= 1f)
        {
            DrawLines(PrimitiveType.Lines, lines, 1);
            return;
        }
        var gl = _gl!;
        float[] quads = WideLines.Expand(lines);
        gl.UseProgram(_wideProgram);
        SetMatrix(_wideProgram, "u_viewProjection", viewProjection);
        gl.Uniform2(gl.GetUniformLocation(_wideProgram, "u_viewport"), (float)viewportWidth, (float)viewportHeight);
        gl.Uniform1(gl.GetUniformLocation(_wideProgram, "u_width"), width);
        gl.Uniform1(gl.GetUniformLocation(_wideProgram, "u_worldWidth"), worldWidth);
        gl.Uniform1(gl.GetUniformLocation(_wideProgram, "u_pixelsPerUnit"), _pixelsPerUnit);
        gl.Uniform1(gl.GetUniformLocation(_wideProgram, "u_alpha"), 1f);
        gl.Uniform1(gl.GetUniformLocation(_wideProgram, "u_round"), 0);
        gl.BindVertexArray(_wideVao);
        Upload(_wideVbo, quads);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(quads.Length / WideLines.VertexFloats));
        gl.UseProgram(_lineProgram);
        gl.BindVertexArray(_lineVao);
    }

    private void DrawLines(PrimitiveType type, float[] vertices, float pointSize, float alpha = 1f, bool round = false, float worldSize = 0f)
    {
        if (vertices.Length == 0) return;
        Upload(_lineVbo, vertices);
        _gl!.Uniform1(_gl.GetUniformLocation(_lineProgram, "u_pointSize"), pointSize);
        _gl.Uniform1(_gl.GetUniformLocation(_lineProgram, "u_worldSize"), worldSize);
        _gl.Uniform1(_gl.GetUniformLocation(_lineProgram, "u_pixelsPerUnit"), _pixelsPerUnit);
        _gl.Uniform1(_gl.GetUniformLocation(_lineProgram, "u_alpha"), alpha);
        _gl.Uniform1(_gl.GetUniformLocation(_lineProgram, "u_round"), round && type == PrimitiveType.Points ? 1 : 0);
        if (alpha < 1f)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        _gl.DrawArrays(type, 0, (uint)(vertices.Length / ScenePose.LineVertexFloats));
        if (alpha < 1f) _gl.Disable(EnableCap.Blend);
    }

    private void Upload(uint buffer, float[] data)
    {
        _gl!.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data.AsSpan(), BufferUsageARB.DynamicDraw);
    }

    private unsafe void SetMatrix(uint program, string name, Matrix4x4 m)
    {
        // Row-vector System.Numerics layout read column-major is the column-vector transpose.
        _gl!.UniformMatrix4(_gl.GetUniformLocation(program, name), 1, false, (float*)&m);
    }

    private unsafe (uint Vao, uint Vbo) VertexArray(int[] sizes)
    {
        var gl = _gl!;
        uint vao = gl.GenVertexArray();
        uint vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = (uint)(sizes.Sum() * sizeof(float));
        int offset = 0;
        for (uint i = 0; i < sizes.Length; i++)
        {
            gl.EnableVertexAttribArray(i);
            gl.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            offset += sizes[i];
        }
        gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private uint Program(string vertex, string fragment)
    {
        var gl = _gl!;
        uint program = gl.CreateProgram();
        uint vs = Shader(ShaderType.VertexShader, vertex);
        uint fs = Shader(ShaderType.FragmentShader, fragment);
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (linked == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(program));
        return program;
    }

    private uint Shader(ShaderType type, string source)
    {
        var gl = _gl!;
        var header = _gles ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, header + source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0) throw new InvalidOperationException($"{type} compile failed: " + gl.GetShaderInfoLog(shader));
        return shader;
    }

    /// <summary>Rebuilds the ground grid when the scene's grid settings or the framing changed.</summary>
    private void RefreshGrid(ScenePose? pose)
    {
        var scene = pose?.Model.Scene;
        var key = (scene?.SizeUnit ?? -1, scene?.GridSize ?? 0u, scene?.UnitSubdivision ?? 0u, _gridFallback);
        if (key != _gridKey) { _gridKey = key; _gridLines = GroundGrid(key.Item1, key.Item2, key.Item3, _gridFallback); _planeGrids = new float[3][]; }
    }

    private float[]?[] _planeGrids = new float[3][];

    /// <summary>The ground grid turned into the plane through the origin facing <paramref name="axis"/> (cached per plane).</summary>
    private float[] PlaneGrid(float[] ground, int axis) => _planeGrids[axis % 3] ??= PlaneGridLines(ground, axis);

    /// <summary>
    /// <paramref name="ground"/> (vertices x, y, z, r, g, b on y = 0) moved into the plane through
    /// the origin facing <paramref name="axis"/>: X takes (0, x, z), Y keeps it, Z takes (x, z, 0).
    /// </summary>
    public static float[] PlaneGridLines(float[] ground, int axis)
    {
        var lines = (float[])ground.Clone();
        if (axis % 3 == 1) return lines;
        for (int i = 0; i + 5 < lines.Length; i += 6)
        {
            float a = ground[i], b = ground[i + 2];
            if (axis % 3 == 0) { lines[i] = 0; lines[i + 1] = a; lines[i + 2] = b; }
            else { lines[i] = a; lines[i + 1] = b; lines[i + 2] = 0; }
        }
        return lines;
    }

    /// <summary>The most lines drawn each way; a finer grid drops its subdivisions first.</summary>
    public const int MaxGridLines = 801;

    /// <summary>
    /// The ground grid (y = 0) as line vertices (x, y, z, r, g, b) in pairs. With the scene's
    /// Unit &amp; grid values a major line falls on every size
    /// unit and dimmer lines on each of its <paramref name="subdivision"/>s, and the grid spans
    /// <paramref name="gridSize"/> units each way from the origin (an assumption: what the
    /// GridSize counts is open-questions.md). A scene without them gets a power-of-ten grid
    /// covering about <paramref name="fallbackSize"/>.
    /// </summary>
    public static float[] GroundGrid(int sizeUnit, uint gridSize, uint subdivision, float fallbackSize)
    {
        float unit = Scene.UnitLength(sizeUnit);
        float step, extent;
        int perMajor;
        if (unit > 0 && gridSize > 0)
        {
            perMajor = (int)Math.Max(1u, subdivision);
            extent = gridSize * unit;
            step = unit / perMajor;
            if (2 * gridSize * perMajor + 1 > MaxGridLines) { perMajor = 1; step = unit; }
            if (2 * (long)gridSize + 1 > MaxGridLines) return [];
        }
        else
        {
            if (!(fallbackSize > 0) || !float.IsFinite(fallbackSize)) return [];
            step = MathF.Pow(10, MathF.Floor(MathF.Log10(fallbackSize / 4)));
            perMajor = 1;
            extent = MathF.Ceiling(fallbackSize / step) * step;
        }
        int n = (int)MathF.Round(extent / step);
        var axis = new Vector3(0.45f, 0.46f, 0.5f);
        var major = new Vector3(0.32f, 0.33f, 0.36f);
        var minor = new Vector3(0.25f, 0.26f, 0.28f);
        var lines = new List<float>((2 * n + 1) * 24);
        for (int i = -n; i <= n; i++)
        {
            float t = i * step;
            var c = i == 0 ? axis : i % perMajor == 0 ? major : minor;
            lines.AddRange([t, 0, -extent, c.X, c.Y, c.Z, t, 0, extent, c.X, c.Y, c.Z]);
            lines.AddRange([-extent, 0, t, c.X, c.Y, c.Z, extent, 0, t, c.X, c.Y, c.Z]);
        }
        return lines.ToArray();
    }
}

/// <summary>One view of a multi-view layout: its camera (null = the orbit camera) and its rectangle.</summary>
public sealed record RenderViewport(IViewCamera? Camera, ViewRect Rect);

/// <summary>A transient point over the scene, drawn as a coloured dot (a hot point).</summary>
public sealed record SceneMarker(Vector3 Position, Vector3 Colour);

/// <summary>A decoded image: <see cref="Width"/> x <see cref="Height"/> RGBA bytes, not premultiplied, top row first.</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Rgba);
