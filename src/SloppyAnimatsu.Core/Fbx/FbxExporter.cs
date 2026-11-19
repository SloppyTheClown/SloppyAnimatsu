using System.Numerics;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// Who reads the FBX. <see cref="Plain"/> writes the plain layout
/// an .akt-based tool expects; the engine presets write a bake that Unity, Godot and Blender read 1:1.
/// </summary>
public enum FbxExportPreset { Plain, Unity, Godot, Blender }

/// <summary>What an FBX export contains.</summary>
public sealed class FbxExportOptions
{
    private readonly bool? _baked, _plainHierarchy, _linearBakedKeys;
    private readonly float? _minimumScale;

    /// <summary>The preset the other settings default from; each can still be set on its own.</summary>
    public FbxExportPreset Preset { get; init; }

    private bool ForEngine => Preset != FbxExportPreset.Plain;

    /// <summary>Bake one key per frame from the evaluator instead of writing the stacks' own keys
    /// (default: on for the engine presets).</summary>
    public bool Baked { get => _baked ?? ForEngine; init => _baked = value; }

    /// <summary>
    /// Bake against a plain hierarchy: every model InheritType 1 (RSrs), each joint's local
    /// re-derived from its evaluated world and its FBX parent's reconstructed world, so joint
    /// positions are exact in any reader that composes the FBX chain. Only applies to a bake.
    /// </summary>
    public bool PlainHierarchy { get => _plainHierarchy ?? ForEngine; init => _plainHierarchy = value; }

    /// <summary>Baked keys are linear (0x0104) with continuous Euler angles instead of step (0x0102).</summary>
    public bool LinearBakedKeys { get => _linearBakedKeys ?? ForEngine; init => _linearBakedKeys = value; }

    /// <summary>Smallest baked scale magnitude in a plain bake (0 = no floor); a zero scale makes
    /// readers produce non-normalised quaternions and NaN children.</summary>
    public float MinimumScale { get => _minimumScale ?? (ForEngine ? 1e-3f : 0f); init => _minimumScale = value; }

    private readonly bool? _uniqueAnimationNames;

    /// <summary>Give animations that share a name a numbered one (<c>Walk</c>, <c>Walk_2</c>), as
    /// engines key clips by name (default: on for the engine presets; the app writes them as they are).</summary>
    public bool UniqueAnimationNames { get => _uniqueAnimationNames ?? ForEngine; init => _uniqueAnimationNames = value; }

    /// <summary>The animations to export; null = every user animation of the exported characters.</summary>
    public IReadOnlyCollection<Animation>? Animations { get; init; }

    /// <summary>The characters to export; null = every character of the scene.</summary>
    public IReadOnlyCollection<Character>? Characters { get; init; }

    /// <summary>Folder the texture file names point into (the image bytes are embedded either way).</summary>
    public string? TextureDirectory { get; init; }

    /// <summary>The path written into <c>SceneInfo</c>; null = no path.</summary>
    public string? DocumentPath { get; init; }

    /// <summary>Creation time written into the header.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

/// <summary>
/// Writes a scene as binary FBX, the way the exporter does
/// models, geometry, skins and bind poses, materials with
/// embedded diffuse textures, and the animations either unbaked (one layer per stack, the stack's
/// own keys) or baked (one key per frame from <see cref="PoseEvaluator"/>).
/// </summary>
public static class FbxExporter
{
    public static void Export(SceneModel model, string path, FbxExportOptions? options = null) =>
        File.WriteAllBytes(path, Export(model, options));

    /// <summary>Exports to <paramref name="path"/>, reporting 0 to 1: the scene and each animation, the encoding, the file.</summary>
    public static void Export(SceneModel model, string path, FbxExportOptions? options, IProgress<double>? progress)
    {
        var nodes = new Builder(model, options ?? new FbxExportOptions()).Build(progress.Slice(0, 0.75));
        var bytes = FbxWriter.Write(nodes, progress.Slice(0.75, 0.95));
        WriteProgress.WriteFile(path, bytes, progress.Slice(0.95, 1));
    }

    public static byte[] Export(SceneModel model, FbxExportOptions? options = null) =>
        FbxWriter.Write(new Builder(model, options ?? new FbxExportOptions()).Build());

    /// <summary>FBX model property names.</summary>
    public const string LclTranslation = "Lcl Translation";
    public const string LclRotation = "Lcl Rotation";
    public const string LclScaling = "Lcl Scaling";

    /// <summary>Degrees per radian, for the Euler conversion.</summary>
    private const float RadToDeg = 180f / MathF.PI;

    /// <summary>
    /// Euler XYZ in degrees whose composition <c>Rx · Ry · Rz</c> is <paramref name="rotation"/>,
    /// the order <see cref="FbxScene.LocalMatrix"/> builds back (row-vector convention, so this
    /// reproduces the stored <c>Lcl Rotation</c> values).
    /// </summary>
    public static Vector3 EulerDegrees(Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        float cy = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        float x, y, z;
        if (cy > 1e-4f)
        {
            x = MathF.Atan2(m.M23, m.M33);
            y = MathF.Atan2(-m.M13, cy);
            z = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            x = m.M13 < 0 ? MathF.Atan2(m.M21, m.M22) : MathF.Atan2(-m.M21, m.M22);
            y = MathF.Atan2(-m.M13, cy);
            z = 0;
        }
        return new Vector3(x * RadToDeg, y * RadToDeg, z * RadToDeg);
    }

    /// <summary>Euler XYZ in degrees of a matrix's rotation part.</summary>
    public static Vector3 EulerDegrees(Matrix4x4 matrix) =>
        EulerDegrees(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(WithoutScale(matrix))));

    /// <summary>
    /// Distinct names for animations, in the order given: the first animation of a name keeps it,
    /// a later one takes <c>name_2</c>, <c>name_3</c>... skipping any name an animation already has
    /// </summary>
    public static Dictionary<Animation, string> UniqueNames(IEnumerable<Animation> animations)
    {
        var list = animations.ToList();
        var taken = list.Select(a => a.Name ?? "").ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<Animation, string>(ReferenceEqualityComparer.Instance);
        foreach (var animation in list)
        {
            string name = animation.Name ?? "";
            if (!used.Add(name))
            {
                int n = 2;
                while (taken.Contains($"{name}_{n}") || used.Contains($"{name}_{n}")) n++;
                name = $"{name}_{n}";
                used.Add(name);
            }
            result[animation] = name;
        }
        return result;
    }

    /// <summary>The last frame an animation exports.</summary>
    public static int StopOf(Animation animation) =>
        animation.Stacks.Count == 0 ? 0 : animation.Stacks.Max(Builder.StopOf);

    /// <summary>
    /// Translation, rotation and scale of a matrix that may carry shear or a zero axis
    /// scale is the row lengths, a
    /// negative determinant negates one axis (the one <paramref name="fallback"/> has most
    /// negative, else X), the rotation is the Gram-Schmidt orthonormalisation of the rows, and a
    /// degenerate matrix keeps the fallback's rotation. Scale magnitudes below
    /// <paramref name="minScale"/> are raised to it, keeping their sign.
    /// </summary>
    public static Transform DecomposeLoose(Matrix4x4 m, Transform fallback, float minScale)
    {
        var r0 = new Vector3(m.M11, m.M12, m.M13);
        var r1 = new Vector3(m.M21, m.M22, m.M23);
        var r2 = new Vector3(m.M31, m.M32, m.M33);
        var s = new Vector3(r0.Length(), r1.Length(), r2.Length());
        if (Vector3.Dot(Vector3.Cross(r0, r1), r2) < 0)
        {
            var f = fallback.Scale;
            if (f.Y < 0 && f.Y <= f.X && f.Y <= f.Z) s.Y = -s.Y;
            else if (f.Z < 0 && f.Z <= f.X) s.Z = -s.Z;
            else s.X = -s.X;
        }

        var rotation = fallback.Rotation;
        const float Degenerate = 1e-6f;
        if (MathF.Abs(s.X) > Degenerate && MathF.Abs(s.Y) > Degenerate && MathF.Abs(s.Z) > Degenerate)
        {
            var x = Vector3.Normalize(r0 / s.X);
            var y = r1 / s.Y;
            y -= Vector3.Dot(y, x) * x;
            if (y.Length() > Degenerate)
            {
                y = Vector3.Normalize(y);
                var z = Vector3.Cross(x, y);
                var r = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
                rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
            }
        }

        static float Floor(float v, float min) =>
            MathF.Abs(v) >= min ? v : v < 0 ? -min : min;
        if (minScale > 0) s = new Vector3(Floor(s.X, minScale), Floor(s.Y, minScale), Floor(s.Z, minScale));
        return new Transform(new Vector3(m.M41, m.M42, m.M43), rotation, s);
    }

    /// <summary>
    /// The Euler triple equal to <paramref name="euler"/> that is closest to
    /// <paramref name="previous"/>: either (x, y, z) or (x+180, 180-y, z+180), each angle moved
    /// by whole turns, so linear keys between frames turn the short way.
    /// </summary>
    public static Vector3 ContinuousEuler(Vector3 previous, Vector3 euler)
    {
        float Near(float value, float target) =>
            value + 360f * MathF.Round((target - value) / 360f);
        Vector3 Closest(Vector3 e) => new(Near(e.X, previous.X), Near(e.Y, previous.Y), Near(e.Z, previous.Z));
        static float Distance(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) + MathF.Abs(a.Y - b.Y) + MathF.Abs(a.Z - b.Z);
        var a = Closest(euler);
        var b = Closest(new Vector3(euler.X + 180f, 180f - euler.Y, euler.Z + 180f));
        return Distance(b, previous) < Distance(a, previous) ? b : a;
    }

    private static Matrix4x4 WithoutScale(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    private sealed class Builder(SceneModel model, FbxExportOptions options)
    {
        private const string PropertyTranslation = "Lcl Translation";
        private const string PropertyRotation = "Lcl Rotation";
        private const string PropertyScaling = "Lcl Scaling";

        private readonly SceneModel _model = model;
        private readonly FbxExportOptions _options = options;

        private long _nextId = 1_000_000;
        private long _lastReference;
        private readonly List<FbxOutNode> _attributes = [];
        private readonly List<FbxOutNode> _geometries = [];
        private readonly List<FbxOutNode> _models = [];
        private readonly List<FbxOutNode> _poses = [];
        private readonly List<FbxOutNode> _materials = [];
        private readonly List<FbxOutNode> _deformers = [];
        private readonly List<FbxOutNode> _videos = [];
        private readonly List<FbxOutNode> _textures = [];
        private readonly List<FbxOutNode> _stacks = [];
        private readonly List<FbxOutNode> _layers = [];
        private readonly List<FbxOutNode> _curveNodes = [];
        private readonly List<FbxOutNode> _curves = [];
        private readonly List<FbxOutNode> _connections = [];
        private readonly List<FbxOutNode> _takes = [];

        private readonly Dictionary<Object3D, long> _modelIds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Object3D, (bool T, bool R, bool S)> _animated = new(ReferenceEqualityComparer.Instance);
        private readonly List<(Object3D Child, Object3D? Parent)> _modelParents = [];
        private readonly Dictionary<Character, PoseEvaluator> _evaluators = new(ReferenceEqualityComparer.Instance);

        private Dictionary<Animation, string>? _stackNames;
        private readonly Dictionary<Character, Transform[]> _plainRest = new(ReferenceEqualityComparer.Instance);

        /// <summary>The plain-hierarchy bake is on.</summary>
        private bool Plain => _options.Baked && _options.PlainHierarchy;

        private uint FrameRate => _model.Scene.FrameRate;

        private long NextId() => _nextId++;

        private long Ticks(int frame) => (long)Math.Round(frame * (double)FbxWriter.TicksPerSecond / FrameRate);



        public IReadOnlyList<FbxOutNode> Build(IProgress<double>? progress = null)
        {
            var characters = (_options.Characters ?? _model.Scene.Characters.ToList()).ToList();
            var animations = (_options.Animations ?? characters.SelectMany(c => c.Animations).ToList()).ToList();

            MarkAnimatedProperties(animations);
            foreach (var character in characters) AddCharacter(character);
            foreach (var mesh in _model.Scene.Meshes)
                if (!_modelIds.ContainsKey(mesh))
                    AddMesh(mesh);
            var animationsInOrder = characters.SelectMany(c => c.Animations).Where(animations.Contains).ToList();
            if (_options.UniqueAnimationNames)
                _stackNames = UniqueNames(animationsInOrder.Where(a => a.Character != null && a.Stacks.Count > 0));
            progress?.Report(0.1);
            for (int i = 0; i < animationsInOrder.Count; i++)
            {
                AddAnimation(animationsInOrder[i]);
                progress?.Report(0.1 + 0.85 * (i + 1) / animationsInOrder.Count);
            }
            foreach (var exported in _modelIds.Keys.ToList()) AddContainerModels(exported);
            foreach (var (child, parent) in _modelParents)
            {
                // The nearest exported ancestor: objects in between (controllers, folders) are not models.
                var exported = parent;
                while (exported != null && !_modelIds.ContainsKey(exported)) exported = exported.Parent3D;
                Connect(_modelIds[child], exported is { } p ? _modelIds[p] : 0, null);
            }

            return TopLevel();
        }

        // -- scene objects -------------------------------------------------------------------

        private void AddCharacter(Character character)
        {
            AddModel(character, "Null");
            foreach (var joint in character.Descendants<Joint>())
                if (!_modelIds.ContainsKey(joint))
                    AddModel(joint, "LimbNode");
            foreach (var mesh in character.Meshes)
                if (!_modelIds.ContainsKey(mesh))
                    AddMesh(mesh);
        }

        /// <summary>
        /// The plain <c>CObject3D</c> groups an imported scene keeps between a character and its
        /// meshes (`Skin`, `Skin_CitroKnight`, `Props_L`, `Props_R`) are models of their own: the
        /// export writes them, and folding them away would move every mesh up to the character.
        /// Controllers are not models.
        /// </summary>
        private void AddContainerModels(Object3D exported)
        {
            var chain = new List<Object3D>();
            for (var parent = exported.Parent3D;
                 parent is not null && !_modelIds.ContainsKey(parent);
                 parent = parent.Parent3D)
                chain.Add(parent);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (chain[i] is Controller3D || _modelIds.ContainsKey(chain[i])) continue;
                AddModel(chain[i], "Null");
            }
        }

        private long AddModel(Object3D o, string kind)
        {
            long id = NextId();
            _modelIds[o] = id;
            string fullName = $"{o.Name}\0\u0001Model";
            var node = new FbxOutNode("Model").With(id, fullName, kind);
            var properties = node.Add("Properties70");
            properties.Add("P", "InheritType", "enum", "", "", !Plain && o.CompensatesParentScale ? 2 : 1);
            properties.Add("P", "ScalingMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
            properties.Add("P", "DefaultAttributeIndex", "int", "Integer", "", 0);
            AddLocalTransform(properties, o);
            node.Add("Shading", true);
            node.Add("Culling", "CullingOff");
            _models.Add(node);
            _modelParents.Add((o, o.Parent3D));
            if (kind is "Null" or "LimbNode") AddNodeAttribute(o, id, kind);
            return id;
        }

        private void AddLocalTransform(FbxOutNode properties, Object3D o)
        {
            var local = LocalTransform(o);
            (bool T, bool R, bool S) animated = _animated.GetValueOrDefault(o);
            var euler = EulerDegrees(local.Rotation);
            AddProperty(properties, PropertyTranslation, PropertyTranslation, animated.T, local.Translation, Vector3.Zero);
            AddProperty(properties, PropertyRotation, PropertyRotation, animated.R, euler, Vector3.Zero);
            AddProperty(properties, PropertyScaling, PropertyScaling, animated.S, local.Scale, Vector3.One);
        }

        /// <summary>
        /// A joint's model transform comes from the character's **reference pose**, not from its
        /// stored <c>LTM</c>: a constrained joint's <c>LTM</c> holds the last solved pose, while the
        /// FBX model transform is the animation's base.
        /// Meshes and character roots use their <c>LTM</c>.
        /// </summary>
        private Transform LocalTransform(Object3D o)
        {
            if (o is Joint joint && joint.Ancestor<Character>() is { } character)
            {
                var evaluator = Evaluator(character);
                int index = evaluator.IndexOf(joint);
                if (index >= 0)
                {
                    if (!Plain) return evaluator.RestTransforms[index];
                    if (!_plainRest.TryGetValue(character, out var rest))
                        _plainRest[character] = rest = PlainLocals(character, null, 0);
                    return rest[index];
                }
            }
            return Transform.FromMatrixLoose(o.LocalMatrix);
        }

        /// <summary>Writes a <c>P</c> entry when <paramref name="value"/> differs from <paramref name="identity"/>.</summary>
        private static void AddProperty(FbxOutNode properties, string name, string type, bool animated, Vector3 value, Vector3 identity)
        {
            if (value == identity) return;
            properties.Add("P", name, type, "", animated ? "A+" : "A", (double)value.X, (double)value.Y, (double)value.Z);
        }

        private void AddNodeAttribute(Object3D o, long modelId, string kind)
        {
            long id = NextId();
            string fullName = $"{o.Name}\0\u0001NodeAttribute";
            var node = new FbxOutNode("NodeAttribute").With(id, fullName, kind);
            if (kind == "LimbNode")
                node.Add("Properties70").Add("P", "Size", "double", "Number", "", 0.666666);
            node.Add("TypeFlags", kind == "LimbNode" ? "Skeleton" : "Null");
            _attributes.Add(node);
            Connect(id, modelId, null);
        }

        private void AddMesh(Mesh mesh)
        {
            long modelId = AddModel(mesh, "Mesh");
            long geometryId = AddGeometry(mesh);
            Connect(geometryId, modelId, null);
            AddMaterials(mesh, modelId);
            if (mesh.BindPose is { } bindPose) AddSkin(mesh, bindPose, geometryId, modelId);
        }

        private long AddGeometry(Mesh mesh)
        {
            long id = NextId();
            string fullName = $"{mesh.Name}\0\u0001Geometry";
            var node = new FbxOutNode("Geometry").With(id, fullName, "Mesh");
            var points = mesh.ControlPoints;
            var vertices = new double[points.Length * 3];
            for (int i = 0; i < points.Length; i++)
            {
                vertices[i * 3] = points[i].X;
                vertices[i * 3 + 1] = points[i].Y;
                vertices[i * 3 + 2] = points[i].Z;
            }
            node.Add("Vertices", vertices);

            var corners = mesh.CornerControlPoints;
            var polygons = mesh.Polygons;
            var indices = new List<int>(corners.Length);
            var materials = new List<int>(polygons.Count);
            foreach (var polygon in polygons)
            {
                int n = polygon.Corners.Length;
                for (int c = 0; c < n; c++)
                {
                    int point = (int)corners[polygon.Corners[c]];
                    indices.Add(c == n - 1 ? -point - 1 : point);
                }
                materials.Add((int)polygon.MaterialIndex);
            }
            node.Add("PolygonVertexIndex", indices.ToArray());
            node.Add("GeometryVersion", 124);

            int normalLayers = 0, colorLayers = 0, uvLayers = 0;
            var normals = mesh.Normals;
            if (normals.Length > 0)
            {
                var values = new double[normals.Length * 3];
                for (int i = 0; i < normals.Length; i++)
                {
                    values[i * 3] = normals[i].X;
                    values[i * 3 + 1] = normals[i].Y;
                    values[i * 3 + 2] = normals[i].Z;
                }
                normalLayers = Math.Max(1, mesh.NormalLayerCount);
                for (int layer = 0; layer < normalLayers; layer++)
                {
                    var element = node.Add("LayerElementNormal");
                    element.Add("Version", 102);
                    element.Add("Name", "");
                    element.Add("MappingInformationType", "ByPolygonVertex");
                    element.Add("ReferenceInformationType", "IndexToDirect");
                    element.Add("Normals", values);
                    element.Add("NormalsW", new double[normals.Length]);
                    element.Add("NormalsIndex", ToInts(mesh.CornerNormalsOf(layer)));
                }
            }

            var colors = mesh.Colors;
            if (colors.Length > 0)
            {
                var values = new double[colors.Length * 4];
                for (int i = 0; i < colors.Length; i++)
                {
                    values[i * 4] = colors[i].X;
                    values[i * 4 + 1] = colors[i].Y;
                    values[i * 4 + 2] = colors[i].Z;
                    values[i * 4 + 3] = colors[i].W;
                }
                colorLayers = Math.Max(1, mesh.ColorLayerCount);
                for (int layer = 0; layer < colorLayers; layer++)
                {
                    var element = node.Add("LayerElementColor");
                    element.Add("Version", 101);
                    element.Add("Name", "");
                    element.Add("MappingInformationType", "ByPolygonVertex");
                    element.Add("ReferenceInformationType", "IndexToDirect");
                    element.Add("Colors", values);
                    element.Add("ColorIndex", ToInts(mesh.CornerColorsOf(layer)));
                }
            }

            var uvs = mesh.UVs;
            if (uvs.Length > 0)
            {
                var values = new double[uvs.Length * 2];
                for (int i = 0; i < uvs.Length; i++)
                {
                    values[i * 2] = uvs[i].X;
                    // The .akt's v runs down from the texture's top row; FBX's v runs up
                    values[i * 2 + 1] = 1.0 - uvs[i].Y;
                }
                uvLayers = Math.Max(1, mesh.UVLayerCount);
                for (int layer = 0; layer < uvLayers; layer++)
                {
                    var element = node.Add("LayerElementUV");
                    element.Add("Version", 101);
                    string name = mesh.UVLayerName(layer);
                    // The app names the first set ("UVMap" from its own import) and keeps any name.
                    element.Add("Name", name.Length > 0 || layer == 0 ? name : $"UVSet{layer}");
                    element.Add("MappingInformationType", "ByPolygonVertex");
                    element.Add("ReferenceInformationType", "IndexToDirect");
                    element.Add("UV", values);
                    element.Add("UVIndex", ToInts(mesh.CornerUVsOf(layer)));
                }
            }

            var materialElement = node.Add("LayerElementMaterial");
            materialElement.Add("Version", 101);
            materialElement.Add("Name", "");
            materialElement.Add("MappingInformationType", "ByPolygon");
            materialElement.Add("ReferenceInformationType", "IndexToDirect");
            materialElement.Add("Materials", materials.ToArray());

            // The first Layer holds the first element of each type, and each extra UV, normal or
            // colour layer repeats in its own Layer node with TypedIndex = its element index
            int layerCount = Math.Max(1, Math.Max(normalLayers, Math.Max(colorLayers, uvLayers)));
            for (int index = 0; index < layerCount; index++)
            {
                var layer = node.Add("Layer");
                layer.Add("Version", 100);
                // The app lists the elements as Normal, Material, Color, UV.
                if (index < normalLayers) AddLayerEntry(layer, "LayerElementNormal", index);
                if (index == 0) AddLayerEntry(layer, "LayerElementMaterial", 0);
                if (index < colorLayers) AddLayerEntry(layer, "LayerElementColor", index);
                if (index < uvLayers) AddLayerEntry(layer, "LayerElementUV", index);
            }

            _geometries.Add(node);
            return id;
        }

        private static void AddLayerEntry(FbxOutNode layer, string type, int typedIndex)
        {
            var element = layer.Add("LayerElement");
            element.Add("Type", type);
            element.Add("TypedIndex", typedIndex);
        }

        private static int[] ToInts(uint[] values) => values.Select(v => (int)v).ToArray();

        // -- materials -----------------------------------------------------------------------

        private void AddMaterials(Mesh mesh, long modelId)
        {
            foreach (var material in mesh.Materials)
            {
                long id = NextId();
                string fullName = $"{material.Name}\0\u0001Material";
                var node = new FbxOutNode("Material").With(id, fullName, "");
                node.Add("Version", 102);
                node.Add("ShadingModel", "phong");
                node.Add("MultiLayer", 0);
                AddMaterialProperties(node.Add("Properties70"), material);
                _materials.Add(node);
                Connect(id, modelId, null);
                AddTexture(material, id);
            }
        }

        private static void AddMaterialProperties(FbxOutNode properties, Material material)
        {
            var emissive = material.GetColor("Emissive");
            var ambient = material.GetColor("Ambient");
            var specular = material.GetColor("Specular");
            var reflection = material.GetColor("Reflection");
            AddColor(properties, "EmissiveColor", emissive.Value);
            properties.Add("P", "EmissiveFactor", "Number", "", "A", (double)material.GetScalar("EmissiveFactor").Value);
            AddColor(properties, "AmbientColor", ambient.Value);
            properties.Add("P", "AmbientFactor", "Number", "", "A", (double)material.GetScalar("AmbientFactor").Value);
            properties.Add("P", "BumpFactor", "double", "Number", "", (double)material.GetScalar("BumpFactor").Value);
            AddColor(properties, "SpecularColor", specular.Value);
            properties.Add("P", "SpecularFactor", "Number", "", "A", (double)material.GetScalar("SpecularFactor").Value);
            properties.Add("P", "ShininessExponent", "Number", "", "A", (double)material.GetScalar("Shininess").Value);
            AddColor(properties, "ReflectionColor", reflection.Value);
            properties.Add("P", "ReflectionFactor", "Number", "", "A", (double)material.GetScalar("ReflectionFactor").Value);
            // The SDK's legacy properties, at their defaults.
            properties.Add("P", "Emissive", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
            properties.Add("P", "Ambient", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
            properties.Add("P", "Diffuse", "Vector3D", "Vector", "", 0.8, 0.8, 0.8);
            properties.Add("P", "Specular", "Vector3D", "Vector", "", 0.2, 0.2, 0.2);
            properties.Add("P", "Shininess", "double", "Number", "", 25.0);
            properties.Add("P", "Opacity", "double", "Number", "", 1.0);
            properties.Add("P", "Reflectivity", "double", "Number", "", 0.0);
        }

        private static void AddColor(FbxOutNode properties, string name, Vector4 value) =>
            properties.Add("P", name, "Color", "", "A", (double)value.X, (double)value.Y, (double)value.Z);

        private void AddTexture(Material material, long materialId)
        {
            if (material.GetColor("Diffuse").Texture is not { } texture) return;
            string fileName = texture.Name ?? "texture";
            if (_options.TextureDirectory is { Length: > 0 } directory) fileName = Path.Combine(directory, fileName);
            string relative = texture.Name ?? fileName;

            long videoId = NextId();
            string videoName = "Diffuse Texture\0\u0001Video";
            var video = new FbxOutNode("Video").With(videoId, videoName, "Clip");
            video.Add("Type", "Clip");
            video.Add("Properties70").Add("P", "Path", "KString", "XRefUrl", "", fileName);
            video.Add("UseMipMap", 0);
            video.Add("Filename", fileName);
            video.Add("RelativeFilename", relative);
            if (texture.Data.Length > 0) video.Add("Content", texture.Data);
            _videos.Add(video);

            long textureId = NextId();
            string textureName = "Diffuse Texture\0\u0001Texture";
            var node = new FbxOutNode("Texture").With(textureId, textureName, "");
            node.Add("Type", "TextureVideoClip");
            node.Add("Version", 202);
            node.Add("TextureName", textureName);
            node.Add("Properties70").Add("P", "UseMaterial", "bool", "", "", true);
            node.Add("Media", videoName);
            node.Add("FileName", fileName);
            node.Add("RelativeFilename", relative);
            node.Add("ModelUVTranslation", 0.0, 0.0);
            node.Add("ModelUVScaling", 1.0, 1.0);
            node.Add("Texture_Alpha_Source", "None");
            node.Add("Cropping", 0.0, 0.0, 0.0, 0.0);
            _textures.Add(node);

            Connect(textureId, materialId, "DiffuseColor");
            Connect(videoId, textureId, null);
        }

        // -- skinning ------------------------------------------------------------------------

        private void AddSkin(Mesh mesh, CharacterPose bindPose, long geometryId, long meshModelId)
        {
            var character = bindPose.Character;
            if (character == null) return;
            var skin = new MeshSkin(mesh);
            if (!skin.IsSkinned) return;

            var weights = mesh.SkinWeights.Length > 0 ? mesh.SkinWeights : mesh.RigidSkinWeights;
            var joints = character.Joints;
            var byBone = new Dictionary<int, List<(int Point, double Weight)>>();
            for (int point = 0; point < weights.Length; point++)
                foreach (var (bone, weight) in weights[point])
                {
                    if (bone < 0 || bone >= joints.Count || weight == 0) continue;
                    if (!byBone.TryGetValue(bone, out var list)) byBone[bone] = list = [];
                    list.Add((point, weight));
                }
            if (byBone.Count == 0) return;

            // The skin's joints are the weighted ones plus their ancestors up to the character
            // (weightless Root/XRot/YRot are in every bind pose; unattached helpers are not).
            var needed = new HashSet<Object3D>(byBone.Keys.Select(b => (Object3D)joints[b]), ReferenceEqualityComparer.Instance);
            foreach (var joint in needed.ToList())
                for (var ancestor = joint.Parent3D; ancestor != null && ancestor is not Character; ancestor = ancestor.Parent3D)
                    if (ancestor is Joint)
                        needed.Add(ancestor);

            long skinId = NextId();
            var skinNode = new FbxOutNode("Deformer").With(skinId, "\0\u0001Deformer", "Skin");
            skinNode.Add("Version", 101);
            skinNode.Add("Link_DeformAcuracy", 50);
            _deformers.Add(skinNode);
            Connect(skinId, geometryId, null);

            var bindWorldOf = new Dictionary<Joint, Matrix4x4>(ReferenceEqualityComparer.Instance);
            var boneOf = new Dictionary<Joint, int>(ReferenceEqualityComparer.Instance);
            for (int bone = 0; bone < joints.Count; bone++)
            {
                bindWorldOf[joints[bone]] = skin.BindWorlds[bone];
                boneOf[joints[bone]] = bone;
            }

            var poseNodes = new List<(long Node, Matrix4x4 Matrix)>();
            foreach (var joint in character.Descendants<Joint>())
            {
                if (!needed.Contains(joint)) continue;
                if (!_modelIds.TryGetValue(joint, out long jointId)) continue;
                int bone = boneOf.GetValueOrDefault(joint, -1);
                var bindWorld = bindWorldOf.TryGetValue(joint, out var world) ? world : Matrix4x4.Identity;
                Matrix4x4.Invert(bindWorld, out var inverse);
                var transform = skin.MeshWorld * inverse;
                var influences = bone >= 0 && byBone.TryGetValue(bone, out var list)
                    ? list
                    : [];

                long clusterId = NextId();
                var cluster = new FbxOutNode("Deformer").With(clusterId, "\0\u0001SubDeformer", "Cluster");
                cluster.Add("Version", 100);
                cluster.Add("UserData", "", "");
                cluster.Add("Indexes", influences.Select(i => i.Point).ToArray());
                cluster.Add("Weights", influences.Select(i => i.Weight).ToArray());
                cluster.Add("Transform", ToDoubles(transform));
                cluster.Add("TransformLink", ToDoubles(bindWorld));
                _deformers.Add(cluster);
                Connect(clusterId, skinId, null);
                Connect(jointId, clusterId, null);
                poseNodes.Add((jointId, bindWorld));
            }

            long poseId = NextId();
            string poseName = "Bind Pose\0\u0001Pose";
            var pose = new FbxOutNode("Pose").With(poseId, poseName, "BindPose");
            pose.Add("Type", "BindPose");
            pose.Add("Version", 100);
            pose.Add("NbPoseNodes", poseNodes.Count);
            foreach (var (node, matrix) in poseNodes)
            {
                var poseNode = pose.Add("PoseNode");
                poseNode.Add("Node", node);
                poseNode.Add("Matrix", ToDoubles(matrix));
            }
            _poses.Add(pose);
            Connect(poseId, meshModelId, null);
        }

        private static double[] ToDoubles(Matrix4x4 m)
        {
            float[] values = AktMath.ToArray(m);
            var result = new double[16];
            for (int i = 0; i < 16; i++) result[i] = values[i];
            return result;
        }

        // -- animation -----------------------------------------------------------------------

        /// <summary>
        /// Marks every model property that any exported animation writes, which makes the model's
        /// <c>Lcl</c> property flag <c>"A+"</c> instead of <c>"A"</c>.
        /// </summary>
        private void MarkAnimatedProperties(IReadOnlyList<Animation> animations)
        {
            foreach (var animation in animations)
                foreach (var stack in animation.Stacks)
                    foreach (var track in stack.Tracks)
                    {
                        if (track.Target is not { } target) continue;
                        var (t, r, s) = _animated.GetValueOrDefault(target);
                        var channels = track.Channels;
                        t |= (channels & AnimChannels.Translate) != 0;
                        r |= (channels & AnimChannels.Rotate) != 0;
                        s |= (channels & AnimChannels.Scale) != 0;
                        _animated[target] = (t, r, s);
                    }
        }

        /// <summary>The export range: the last key of a cycle stack, the penultimate one otherwise.</summary>
        internal static int StopOf(AnimStack stack)
        {
            var keys = stack.Keys;
            if (keys.Count == 0) return 0;
            if (stack.IsCycle) return Math.Max(0, keys[^1].Time - 1);
            return keys.Count >= 2 ? keys[^2].Time : keys[0].Time;
        }

        private void AddAnimation(Animation animation)
        {
            var character = animation.Character;
            if (character == null || animation.Stacks.Count == 0) return;
            long stackId = NextId();
            string stackName = $"{_stackNames?.GetValueOrDefault(animation) ?? animation.Name}\0\u0001AnimStack";
            var stackNode = new FbxOutNode("AnimationStack").With(stackId, stackName, "");
            _stacks.Add(stackNode);

            var evaluator = Evaluator(character);
            int stop = _options.Baked
                ? AddBakedLayers(animation, evaluator, stackId)
                : AddUnbakedLayers(animation, evaluator, stackId);

            stackNode.Add("Properties70").Add("P", "LocalStop", "KTime", "Time", "", Ticks(stop));
            AddTake(animation.Name ?? "", stop);
        }

        private PoseEvaluator Evaluator(Character character)
        {
            if (!_evaluators.TryGetValue(character, out var evaluator))
                _evaluators[character] = evaluator = new PoseEvaluator(character);
            return evaluator;
        }

        private int AddBakedLayers(Animation animation, PoseEvaluator evaluator, long stackId)
        {
            int stop = animation.Stacks.Max(StopOf);
            var joints = JointModels(animation.Character!);
            int frames = stop + 1;
            var translations = new Vector3[joints.Count][];
            var rotations = new Vector3[joints.Count][];
            var scales = new Vector3[joints.Count][];
            for (int j = 0; j < joints.Count; j++)
            {
                translations[j] = new Vector3[frames];
                rotations[j] = new Vector3[frames];
                scales[j] = new Vector3[frames];
            }
            var indices = joints.Select(j => evaluator.IndexOf(j.Joint)).ToArray();
            for (int frame = 0; frame < frames; frame++)
            {
                var local = Plain
                    ? PlainLocals(animation.Character!, animation, frame)
                    : evaluator.LocalTransforms(animation, frame);
                for (int j = 0; j < joints.Count; j++)
                {
                    int index = indices[j];
                    var value = index >= 0 && index < local.Length ? local[index] : Transform.Identity;
                    translations[j][frame] = value.Translation;
                    var euler = EulerDegrees(value.Rotation);
                    rotations[j][frame] = _options.LinearBakedKeys && frame > 0
                        ? ContinuousEuler(rotations[j][frame - 1], euler)
                        : euler;
                    scales[j][frame] = value.Scale;
                }
            }

            // A one-frame pose gives a zero-length take, which Unity imports as an empty clip (the
            // rest pose). Engine presets hold the pose for one more frame instead.
            if (_options.LinearBakedKeys && frames == 1)
            {
                for (int j = 0; j < joints.Count; j++)
                {
                    translations[j] = [translations[j][0], translations[j][0]];
                    rotations[j] = [rotations[j][0], rotations[j][0]];
                    scales[j] = [scales[j][0], scales[j][0]];
                }
                frames = 2;
                stop = 1;
            }

            long layerId = NextId();
            string layerName = "Baked frames\0\u0001AnimLayer";
            var layer = new FbxOutNode("AnimationLayer").With(layerId, layerName, "");
            layer.Add("Properties70").Add("P", "BlendMode", "enum", "", "", 2);
            _layers.Add(layer);
            Connect(layerId, stackId, null);

            var keyTimes = Enumerable.Range(0, frames).Select(f => Ticks(f)).ToArray();
            var frameNumbers = Enumerable.Range(0, frames).ToArray();
            for (int j = 0; j < joints.Count; j++)
            {
                var defaults = LocalTransform(joints[j].Joint);
                AddCurveNode(layerId, joints[j].Model, PropertyTranslation, keyTimes, frameNumbers, translations[j], 0, null, null, defaults.Translation);
                AddCurveNode(layerId, joints[j].Model, PropertyRotation, keyTimes, frameNumbers, rotations[j], 3, null, null, EulerDegrees(defaults.Rotation));
                AddCurveNode(layerId, joints[j].Model, PropertyScaling, keyTimes, frameNumbers, scales[j], 6, null, null, defaults.Scale);
            }
            return stop;
        }

        private int AddUnbakedLayers(Animation animation, PoseEvaluator evaluator, long stackId)
        {
            int baseStop = animation.Stacks.Max(StopOf);
            int last = baseStop;
            var stacks = animation.Stacks;
            // The first stack in StackList is the top layer; FBX layers override in order, so the
            // bottom stack goes first.
            for (int s = stacks.Count - 1; s >= 0; s--)
            {
                var stack = stacks[s];
                var keys = stack.Keys;
                var times = ExportTimes(stack, baseStop);
                if (times.Count == 0) continue;
                last = Math.Max(last, times[^1]);

                long layerId = NextId();
                string layerName = $"{stack.Name}\0\u0001AnimLayer";
                var layer = new FbxOutNode("AnimationLayer").With(layerId, layerName, "");
                if (!stack.IsAdditive)
                    layer.Add("Properties70").Add("P", "BlendMode", "enum", "", "", 2);
                _layers.Add(layer);
                Connect(layerId, stackId, null);

                // One evaluation per exported time, shared by every track of the (override) stack.
                // An additive layer writes the layer's own delta, which FBX adds to the layers below.
                var values = stack.IsAdditive
                    ? []
                    : times.Select(t => evaluator.StackLocalTransforms(stack, t, out _)).ToArray();
                foreach (var track in stack.Tracks)
                {
                    if (track.Target is not Joint joint) continue;
                    if (!_modelIds.TryGetValue(joint, out long modelId)) continue;
                    int index = evaluator.IndexOf(joint);
                    if (index < 0) continue;

                    var translations = new Vector3[times.Count];
                    var rotations = new Vector3[times.Count];
                    var scales = new Vector3[times.Count];
                    for (int k = 0; k < times.Count; k++)
                    {
                        var value = stack.IsAdditive
                            ? evaluator.LayerValue(stack, track, times[k], out _)
                            : index < values[k].Length ? values[k][index] : Transform.Identity;
                        translations[k] = value.Translation;
                        rotations[k] = EulerDegrees(value.Rotation);
                        scales[k] = value.Scale;
                    }
                    var frames = times.Select(Ticks).ToArray();
                    var defaults = stack.IsAdditive ? Transform.Identity : LocalTransform(joint);
                    AddCurveNode(layerId, modelId, PropertyTranslation, frames, times, translations, 0, keys, track.TangentModes, defaults.Translation);
                    AddCurveNode(layerId, modelId, PropertyRotation, frames, times, rotations, 3, keys, track.TangentModes, EulerDegrees(defaults.Rotation));
                    AddCurveNode(layerId, modelId, PropertyScaling, frames, times, scales, 6, keys, track.TangentModes, defaults.Scale);
                }
            }
            return last;
        }

        /// <summary>
        /// The times an unbaked layer keys: a normal stack's keys without its end marker; a cycle
        /// stack's keys repeated every period until the layer covers the export range
        /// </summary>
        private static List<int> ExportTimes(AnimStack stack, int stop)
        {
            var keys = stack.Keys;
            var times = new List<int>();
            if (keys.Count == 0) return times;
            if (!stack.IsCycle)
            {
                int count = keys.Count >= 2 ? keys.Count - 1 : 1;
                for (int i = 0; i < count; i++) times.Add(keys[i].Time);
                return times;
            }
            int first = keys[0].Time;
            int period = keys[^1].Time - first;
            if (period <= 0)
            {
                times.AddRange(keys.Select(k => k.Time));
                return times;
            }
            for (int cycle = 0; cycle < 10_000; cycle++)
            {
                foreach (var key in keys)
                {
                    int time = key.Time + cycle * period;
                    if (times.Count > 0 && time <= times[^1]) continue;
                    times.Add(time);
                    // Stop at the first key that reaches the range; the rest of the cycle is not added.
                    if (time >= stop || times[^1] >= stop) return times;
                }
            }
            return times;
        }

        /// <summary>The FBX key flag of one channel of a key (0x2108 spline, 0x0104 linear, 0x0102 step).</summary>
        private static int KeyFlags(TangentMode[]? modes, int channel)
        {
            if (modes is null || channel < 0 || channel >= modes.Length) return 0x2108;
            return KeyFlag(modes[channel]);
        }

        private static int IndexOfKey(IReadOnlyList<StackKey> keys, int time)
        {
            for (int i = 0; i < keys.Count; i++) if (keys[i].Time == time) return i;
            if (keys.Count < 2) return 0;
            int first = keys[0].Time, period = keys[^1].Time - first;
            if (period <= 0) return 0;
            int wrapped = first + ((time - first) % period + period) % period;
            for (int i = 0; i < keys.Count; i++) if (keys[i].Time == wrapped) return i;
            return 0;
        }

        private static int KeyFlag(TangentMode mode)
        {
            if (mode.In == TangentTypes.Step || mode.Out == TangentTypes.Step) return 0x0102;
            if (mode.In == TangentTypes.Linear || mode.Out == TangentTypes.Linear) return 0x0104;
            return 0x2108;
        }

        /// <param name="keyTimes">The key times in FBX ticks.</param>
        /// <param name="frames">The same keys in frames, for the tangent mode lookup.</param>
        /// <param name="channelBase">First channel of the property: 0 translation, 3 rotation, 6 scaling.</param>
        /// <param name="keys">The stack's keys, for the tangent mode of each exported time; null for a bake.</param>
        /// <param name="modes">The track's tangent modes per key; null for a bake (every key is step).</param>
        /// <param name="defaults">The model's static value, written as the curve's <c>Default</c>.</param>
        private void AddCurveNode(long layerId, long modelId, string property, IReadOnlyList<long> keyTimes, IReadOnlyList<int> frames,
            Vector3[] values, int channelBase, IReadOnlyList<StackKey>? keys, IReadOnlyList<TangentMode[]>? modes, Vector3 defaults)
        {
            long id = NextId();
            string shortName = property switch
            {
                PropertyTranslation => "T",
                PropertyRotation => "R",
                _ => "S",
            };
            string curveName = shortName + "\0\u0001AnimCurveNode";
            var node = new FbxOutNode("AnimationCurveNode").With(id, curveName, "");
            var properties = node.Add("Properties70");
            foreach (var axis in new[] { "d|X", "d|Y", "d|Z" })
                properties.Add("P", axis, "Number", "", "A", 0.0);
            _curveNodes.Add(node);
            Connect(id, layerId, null);
            Connect(id, modelId, property);

            var axes = new[] { "d|X", "d|Y", "d|Z" };
            for (int axis = 0; axis < 3; axis++)
            {
                var flags = new int[frames.Count];
                for (int i = 0; i < flags.Length; i++)
                {
                    if (modes is null) { flags[i] = _options.LinearBakedKeys ? 0x0104 : 0x0102; continue; }
                    int keyIndex = IndexOfKey(keys!, frames[i]);
                    flags[i] = keyIndex >= 0 && keyIndex < modes.Count ? KeyFlags(modes[keyIndex], channelBase + axis) : 0x2108;
                }
                long curveId = NextId();
                string name = "\0\u0001AnimCurve";
                var curve = new FbxOutNode("AnimationCurve").With(curveId, name, "");
                curve.Add("Default", (double)(axis == 0 ? defaults.X : axis == 1 ? defaults.Y : defaults.Z));
                curve.Add("KeyVer", 4008);
                curve.Add("KeyTime", keyTimes.ToArray());
                curve.Add("KeyValueFloat", values.Select(v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z).ToArray());
                curve.Add("KeyAttrFlags", flags);
                curve.Add("KeyAttrDataFloat", new float[frames.Count * 4]);
                curve.Add("KeyAttrRefCount", Enumerable.Repeat(1, frames.Count).ToArray());
                _curves.Add(curve);
                Connect(curveId, id, axes[axis]);
            }
        }

        /// <summary>
        /// The joints' locals against a plain FBX hierarchy:
        /// each joint's evaluated world times the inverse of its FBX parent's world as a reader
        /// rebuilds it from the exported locals (RSrs, no scale compensation). Translation is
        /// exact, so every joint lands where the app puts it; only the shear a non-uniform
        /// ancestor scale carries is dropped, locally. Indexed like the evaluator's controllers.
        /// </summary>
        private Transform[] PlainLocals(Character character, Animation? animation, float time)
        {
            var evaluator = Evaluator(character);
            var locals = evaluator.LocalTransforms(animation, time, out var worlds);
            var result = (Transform[])locals.Clone();
            var rebuilt = new Dictionary<Object3D, Matrix4x4>(ReferenceEqualityComparer.Instance);
            float minScale = _options.MinimumScale;

            Matrix4x4 World(Object3D? o)
            {
                if (o is null) return Matrix4x4.Identity;
                if (rebuilt.TryGetValue(o, out var cached)) return cached;
                var parentWorld = World(FbxParent(o));
                int index = o is Joint ? evaluator.IndexOf(o) : -1;
                Transform local;
                if (index >= 0 && index < worlds.Length && !IsFinite(worlds[index]))
                {
                    // A scene can store NaN keys; when it does,
                    // each non-finite channel takes the rest value instead, against
                    // the rebuilt parent, so readers get no NaN or unnormalised quaternion.
                    local = Finite(locals[index], evaluator.RestTransforms[index]);
                    result[index] = local;
                }
                else if (index >= 0 && index < worlds.Length && Matrix4x4.Invert(parentWorld, out var inverse))
                {
                    // Floor a collapsed *world* scale before taking the local, so the children of a
                    // zero-scaled joint keep well-conditioned locals (scale ~1) instead of a chain
                    // of 1e-3 factors that readers turn into NaN (fbx-export-engines.md).
                    var world0 = worlds[index];
                    if (minScale > 0 && MinRowLength(world0) < minScale)
                    {
                        var parentRotation = DecomposeLoose(parentWorld, Transform.Identity, 0).Rotation;
                        var guess = locals[index] with { Rotation = Quaternion.Concatenate(locals[index].Rotation, parentRotation) };
                        world0 = DecomposeLoose(world0, guess, minScale).ToMatrix();
                    }
                    local = DecomposeLoose(world0 * inverse, locals[index], minScale);
                    result[index] = local;
                }
                else local = Transform.FromMatrixLoose(o.LocalMatrix);
                var world = local.ToMatrix() * parentWorld;
                rebuilt[o] = world;
                return world;
            }

            foreach (var joint in character.Joints) World(joint);
            return result;
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static bool IsFinite(Matrix4x4 m) =>
            IsFinite(new Vector3(m.M11, m.M12, m.M13)) && IsFinite(new Vector3(m.M21, m.M22, m.M23)) &&
            IsFinite(new Vector3(m.M31, m.M32, m.M33)) && IsFinite(new Vector3(m.M41, m.M42, m.M43));

        /// <summary><paramref name="value"/> with each non-finite channel replaced by <paramref name="rest"/>'s.</summary>
        private static Transform Finite(Transform value, Transform rest)
        {
            var q = value.Rotation;
            bool rotationOk = float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W) && q.LengthSquared() > 1e-12f;
            return new Transform(
                IsFinite(value.Translation) ? value.Translation : rest.Translation,
                rotationOk ? Quaternion.Normalize(q) : rest.Rotation,
                IsFinite(value.Scale) ? value.Scale : rest.Scale);
        }

        private static float MinRowLength(Matrix4x4 m) => MathF.Min(
            new Vector3(m.M11, m.M12, m.M13).Length(),
            MathF.Min(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));

        /// <summary>The object's parent model in the FBX: the nearest ancestor that is not a
        /// controller (controllers are not models; plain groups become container models).</summary>
        private static Object3D? FbxParent(Object3D o)
        {
            var parent = o.Parent3D;
            while (parent is Controller3D) parent = parent.Parent3D;
            return parent;
        }

        /// <summary>The character's joints that have a model, in controller order.</summary>
        private List<(Joint Joint, long Model)> JointModels(Character character) =>
            character.Joints.Where(j => _modelIds.ContainsKey(j)).Select(j => (j, _modelIds[j])).ToList();

        private void AddTake(string name, int stop)
        {
            var take = new FbxOutNode("Take").With(name);
            take.Add("FileName", $"{name}.tak");
            take.Add("LocalTime", 0L, Ticks(stop));
            long reference = _takes.Count == 0 ? Ticks(stop) : Math.Max(_lastReference, Ticks(stop));
            take.Add("ReferenceTime", 0L, reference);
            _lastReference = reference;
            _takes.Add(take);
        }

        // -- file ----------------------------------------------------------------------------

        private void Connect(long child, long parent, string? property) =>
            _connections.Add(property is null
                ? new FbxOutNode("C").With("OO", child, parent)
                : new FbxOutNode("C").With("OP", child, parent, property));

        private IReadOnlyList<FbxOutNode> TopLevel()
        {
            var nodes = new List<FbxOutNode> { Header(), new FbxOutNode("FileId").With(FileId()) };
            nodes.Add(new FbxOutNode("CreationTime").With(FbxWriter.CreationTime));
            nodes.Add(new FbxOutNode("Creator").With("FBX SDK/FBX Plugins version 2016.1.1 build=20150824"));

            var globalSettings = new FbxOutNode("GlobalSettings");
            globalSettings.Add("Version", 1000);
            AddGlobalSettings(globalSettings.Add("Properties70"));
            nodes.Add(globalSettings);
            nodes.Add(Documents());
            nodes.Add(new FbxOutNode("References"));
            nodes.Add(Definitions());
            var objects = new FbxOutNode("Objects");
            foreach (var list in new[] { _attributes, _geometries, _models, _poses, _materials, _deformers, _videos, _textures, _stacks, _layers, _curveNodes, _curves })
                objects.Children.AddRange(list);
            nodes.Add(objects);
            var connections = new FbxOutNode("Connections");
            connections.Children.AddRange(_connections);
            nodes.Add(connections);
            var takes = new FbxOutNode("Takes");
            takes.Add("Current", "");
            takes.Children.AddRange(_takes);
            nodes.Add(takes);
            return nodes;
        }

        private FbxOutNode Header()
        {
            var header = new FbxOutNode("FBXHeaderExtension");
            header.Add("FBXHeaderVersion", 1003);
            header.Add("FBXVersion", FbxWriter.Version);
            header.Add("EncryptionType", 0);
            var stamp = header.Add("CreationTimeStamp");
            stamp.Add("Version", 1000);
            stamp.Add("Year", _options.Timestamp.Year);
            stamp.Add("Month", _options.Timestamp.Month);
            stamp.Add("Day", _options.Timestamp.Day);
            stamp.Add("Hour", _options.Timestamp.Hour);
            stamp.Add("Minute", _options.Timestamp.Minute);
            stamp.Add("Second", _options.Timestamp.Second);
            stamp.Add("Millisecond", _options.Timestamp.Millisecond);
            header.Add("Creator", "FBX SDK/FBX Plugins version 2016.1.1");

            string documentPath = _options.DocumentPath ?? "";
            var info = header.Add("SceneInfo", "GlobalInfo\0\u0001SceneInfo", "UserData");
            info.Add("Type", "UserData");
            info.Add("Version", 100);
            var meta = info.Add("MetaData");
            meta.Add("Version", 100);
            meta.Add("Title", documentPath.Replace('\\', '/'));
            meta.Add("Subject", "");
            meta.Add("Author", "");
            meta.Add("Keywords", "");
            meta.Add("Revision", "");
            meta.Add("Comment", "");
            var properties = info.Add("Properties70");
            properties.Add("P", "DocumentUrl", "KString", "Url", "", documentPath);
            properties.Add("P", "SrcDocumentUrl", "KString", "Url", "", documentPath);
            properties.Add("P", "Original", "Compound", "", "");
            properties.Add("P", "Original|ApplicationVendor", "KString", "", "", "SloppyAnimatsu");
            properties.Add("P", "Original|ApplicationName", "KString", "", "", "SloppyAnimatsu");
            properties.Add("P", "Original|ApplicationVersion", "KString", "", "", "20.3.13.0");
            properties.Add("P", "Original|DateTime_GMT", "DateTime", "", "", "");
            properties.Add("P", "Original|FileName", "KString", "", "", "");
            properties.Add("P", "LastSaved", "Compound", "", "");
            properties.Add("P", "LastSaved|ApplicationVendor", "KString", "", "", "SloppyAnimatsu");
            properties.Add("P", "LastSaved|ApplicationName", "KString", "", "", "SloppyAnimatsu");
            properties.Add("P", "LastSaved|ApplicationVersion", "KString", "", "", "20.3.13.0");
            properties.Add("P", "LastSaved|DateTime_GMT", "DateTime", "", "", "");
            return header;
        }

        private static byte[] FileId() => (byte[])FbxWriter.FileId.Clone();

        private void AddGlobalSettings(FbxOutNode properties)
        {
            properties.Add("P", "UpAxis", "int", "Integer", "", 1);
            properties.Add("P", "UpAxisSign", "int", "Integer", "", 1);
            properties.Add("P", "FrontAxis", "int", "Integer", "", 2);
            properties.Add("P", "FrontAxisSign", "int", "Integer", "", 1);
            properties.Add("P", "CoordAxis", "int", "Integer", "", 0);
            properties.Add("P", "CoordAxisSign", "int", "Integer", "", 1);
            properties.Add("P", "OriginalUpAxis", "int", "Integer", "", -1);
            properties.Add("P", "OriginalUpAxisSign", "int", "Integer", "", 1);
            properties.Add("P", "UnitScaleFactor", "double", "Number", "", 1.0);
            properties.Add("P", "OriginalUnitScaleFactor", "double", "Number", "", 1.0);
            properties.Add("P", "AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0);
            properties.Add("P", "DefaultCamera", "KString", "", "", "Producer Perspective");
            properties.Add("P", "TimeMode", "enum", "", "", TimeMode);
            properties.Add("P", "TimeProtocol", "enum", "", "", 2);
            properties.Add("P", "SnapOnFrameMode", "enum", "", "", 0);
            properties.Add("P", "TimeSpanStart", "KTime", "Time", "", 0L);
            properties.Add("P", "TimeSpanStop", "KTime", "Time", "", FbxWriter.TicksPerSecond);
            properties.Add("P", "CustomFrameRate", "double", "Number", "", -1.0);
            properties.Add("P", "TimeMarker", "Compound", "", "");
            properties.Add("P", "CurrentTimeMarker", "int", "Integer", "", -1);
        }

        /// <summary>FbxTime::EMode for the scene's frame rate (3 = 60, 6 = 30).</summary>
        private int TimeMode => FrameRate switch
        {
            120 => 1, 100 => 2, 60 => 3, 50 => 4, 48 => 5, 30 => 6, 25 => 10, 24 => 11,
            23 => 13, 96 => 15, 72 => 16,
            _ => 14,
        };

        private FbxOutNode Documents()
        {
            var documents = new FbxOutNode("Documents");
            documents.Add("Count", 1);
            var document = documents.Add("Document", NextId(), "My Scene", "Scene");
            var properties = document.Add("Properties70");
            properties.Add("P", "SourceObject", "object", "", "");
            properties.Add("P", "ActiveAnimStackName", "KString", "", "", "");
            document.Add("RootNode", 0);
            return documents;
        }

        private FbxOutNode Definitions()
        {
            var definitions = new FbxOutNode("Definitions");
            definitions.Add("Version", 100);
            var types = new (string Name, int Count, string? Template)[]
            {
                ("GlobalSettings", 1, null),
                ("Material", _materials.Count, "FbxSurfacePhong"),
                ("Texture", _textures.Count, "FbxFileTexture"),
                ("Model", _models.Count, "FbxNode"),
                ("NodeAttribute", _attributes.Count, "FbxNull"),
                ("Geometry", _geometries.Count, "FbxMesh"),
                ("Deformer", _deformers.Count, "FbxCluster"),
                ("Pose", _poses.Count, null),
                ("AnimationStack", _stacks.Count, "FbxAnimStack"),
                ("AnimationLayer", _layers.Count, "FbxAnimLayer"),
                ("AnimationCurveNode", _curveNodes.Count, "FbxAnimCurveNode"),
                ("AnimationCurve", _curves.Count, null),
                ("SceneInfo", 1, null),
                ("Video", _videos.Count, "FbxVideo"),
            };
            definitions.Add("Count", types.Sum(t => t.Count));
            foreach (var (name, count, template) in types)
            {
                var type = definitions.Add("ObjectType", name);
                type.Add("Count", count);
                if (template is not null)
                    type.Add("PropertyTemplate", template).Add("Properties70");
            }
            return definitions;
        }
    }
}













