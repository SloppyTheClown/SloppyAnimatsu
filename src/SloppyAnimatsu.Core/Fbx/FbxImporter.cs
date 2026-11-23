using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>What an FBX import brings in.</summary>
public sealed class FbxImportOptions
{
    /// <summary>Read the animation stacks as animations in the Anim Bank.</summary>
    public bool Animations { get; init; } = true;
    /// <summary>Read the skin clusters as skin weights and bind poses.</summary>
    public bool Skins { get; init; } = true;
    /// <summary>Read materials and their diffuse textures.</summary>
    public bool Materials { get; init; } = true;
    /// <summary>
    /// Re-key spline animation: sample every frame of the file's curves and keep only the frames the
    /// app's own spline playback needs to stay within a small tolerance of them, instead of one key
    /// per file key. Makes baked (one key per frame) and engine-fitted files editable and plays
    /// curves with their own tangents correctly. Layers with stepped or linear keys and additive
    /// layers keep the file's key times.
    /// </summary>
    public bool FitKeys { get; init; } = true;
    /// <summary>Where to look for textures that are not embedded (default: the FBX file's folder).</summary>
    public string? TextureDirectory { get; init; }
    /// <summary>Receives one line per dropped or approximated item.</summary>
    public Action<string>? Warning { get; init; }
}

/// <summary>
/// Reads a binary FBX scene into a new <c>.akt</c> document: characters and their joints, meshes
/// with geometry, skin weights and bind poses, materials with their diffuse textures, and the
/// animation stacks.
/// </summary>
public static class FbxImporter
{
    /// <summary>Imports <paramref name="path"/> into a new scene. The returned model can be saved as a <c>.akt</c>.</summary>
    public static SceneModel Import(string path, FbxImportOptions? options = null) =>
        Import(FbxDocument.Load(path), path, options);

    public static SceneModel Import(FbxDocument document, string? sourcePath, FbxImportOptions? options = null) =>
        new Importer(document, sourcePath, options ?? new FbxImportOptions()).Run();

    private sealed class Importer
    {
        private readonly string? _sourcePath;
        private readonly FbxImportOptions _options;
        private readonly FbxScene _fbx;

        private readonly Dictionary<long, FbxNode> _nodes = [];
        private readonly Dictionary<long, List<Connection>> _byParent = [];
        private readonly Dictionary<long, List<Connection>> _byChild = [];

        private readonly AktSchema _schema = FbxImportSchema.Create();
        private readonly AktDocument _akt;
        private readonly AktObject _scene;
        private SceneModel? _model;

        private readonly Dictionary<long, FbxModel> _models = [];
        private readonly Dictionary<long, AktObject> _objectByModel = [];
        private readonly Dictionary<long, AktObject> _materialByNode = [];
        private readonly Dictionary<long, AktObject> _textureByVideo = [];
        private readonly Dictionary<long, Matrix4x4> _poseWorld = [];
        private readonly List<CharacterBuild> _characters = [];

        private readonly double _frameRate;

        private sealed record Connection(long Child, long Parent, string? Property);

        private sealed class CharacterBuild
        {
            public required FbxModel Root;
            public required AktObject Object;
            public required AktObject Anims;
            public required AktObject BindFolder;
            public required AktObject BindPose;
            public required AktObject RigAnim;
            public required List<(FbxModel Model, AktObject Joint)> Joints;
            public required List<(FbxModel Model, AktObject Mesh)> Meshes;
            public required List<AktObject> Controllers;
            public required Transform[] RefTransforms;
            public required Dictionary<long, int> JointIndex;
        }

        public Importer(FbxDocument document, string? sourcePath, FbxImportOptions options)
        {
            _sourcePath = sourcePath;
            _options = options;
            _fbx = new FbxScene(document);
            _frameRate = _fbx.FrameRate;

            var objects = document.Node("Objects")?.Children ?? [];
            foreach (var node in objects)
            {
                if (node.PropertyCount < 2 || node[0] is not long id) continue;
                _nodes[id] = node;
            }
            foreach (var c in document.Node("Connections")?.ChildrenNamed("C") ?? [])
            {
                if (c.PropertyCount < 3 || c[1] is not long child || c[2] is not long parent) continue;
                var connection = new Connection(child, parent, c.PropertyCount > 3 ? c[3] as string : null);
                if (!_byParent.TryGetValue(parent, out var children)) _byParent[parent] = children = [];
                if (!_byChild.TryGetValue(child, out var parents)) _byChild[child] = parents = [];
                children.Add(connection);
                parents.Add(connection);
            }
            foreach (var model in _fbx.Models) _models[model.Id] = model;

            // The bind pose of the exports is a loose Pose object (fbx-export.md "Skinning").
            foreach (var node in _nodes.Values)
            {
                if (node.Name != "Pose") continue;
                foreach (var poseNode in node.ChildrenNamed("PoseNode"))
                {
                    long modelId = poseNode.Value<long>("Node");
                    if (modelId == 0) continue;
                    if (poseNode.Value<double[]>("Matrix") is not { Length: 16 } matrix) continue;
                    _poseWorld.TryAdd(modelId, AktMath.ToMatrix(ToFloats(matrix)));
                }
            }

            var scene = _schema.NewObject("class CScene");
            _akt = new AktDocument(_schema, [scene]);
            _scene = scene;
        }

        public SceneModel Run()
        {
            CreateScene();
            CreateCameras(SceneBounds());
            if (_options.Materials) CreateMaterials();
            foreach (var root in _fbx.Models.Where(m => m.Parent == null))
            {
                if (HasJoints(root)) BuildCharacter(root);
                else BuildObjectTree(root, _scene);
            }

            _model = new SceneModel(_akt);
            foreach (var build in _characters)
            {
                if (_options.Animations) ImportAnimations(build);
                BuildRigAnimation(build);
            }

            var scene = (Scene)_model.Wrap(_scene);
            scene.CurrentCharacter = _characters.Count > 0 ? (Character)_model.Wrap(_characters[0].Object) : null;
            return _model;
        }

        // ---- scene ------------------------------------------------------------------------

        private void CreateScene()
        {
            int fps = (int)Math.Round(_frameRate);
            if (fps <= 0) fps = 30;
            _scene.Set("Name", "Scene");
            _scene.Set("FrameRate", (uint)fps);
            _scene.Set("FrameRateIndex", 0);
            _scene.Set("InterfaceContextIndex", (byte)1);
            _scene.Set("SizeUnit", 3);
            _scene.Set("GridSize", 40u);
            _scene.Set("UnitSubdivision", 2u);
            _scene.Set("JointRenderSize", 5f);
            _scene.Set("JointRenderSizeHighDensity", 6f);
            _scene.Set("PivotRenderSize", 3f);
            _scene.Set("UnselectedPivotRenderSize", 0.4f);
            _scene.Set("TargetRenderSize", 8f);
            _scene.Set("BankAngleMax", 60f);
            _scene.Set("TwistAngleMax", 60f);
            _scene.Set("RollAngleBackMax", 60f);
            _scene.Set("RollAnglePreMax", 60f);
            _scene.Set("RollAngleToesMax", 75f);
            _scene.Set("RollAngleFullMax", 85f);
            _scene.Set("SnapAngleThreshold", 5f);
            _scene.Set("LayoutIndex", 0);
            _scene.Set("ToggledLayoutIndex", 3);
            _scene.Set("ToggledLocalViewIndex", -1);
            _scene.Set("SavePath", FolderOf(_sourcePath));
            _scene.Set("FBXPath", FolderOf(_sourcePath));
            _scene.Set("AKNMPath", "");
            _scene.Set("Time", 0);
            _scene.Set("ViewCamIndices", new List<object?> { 0, 0, 2, 1, 3, 4, 5, 6, 7 });
        }

        private static string FolderOf(string? path) =>
            string.IsNullOrEmpty(path) ? "" : (Path.GetDirectoryName(path) ?? "").Replace('\\', '/') + "/";

        private static bool HasJoints(FbxModel model) =>
            model.Kind == "LimbNode" || model.Children.Any(HasJoints);

        /// <summary>World bounds of the mesh geometry, used to frame the default cameras.</summary>
        private (Vector3 Center, float Size) SceneBounds()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;
            foreach (var model in _fbx.Models)
            {
                if (model.Kind != "Mesh") continue;
                var geometry = GeometryOf(model.Id).Node;
                if (geometry == null) continue;
                var vertices = ReadDoubles(geometry, "Vertices");
                var world = WorldOf(model);
                for (int i = 0; i + 2 < vertices.Length; i += 3)
                {
                    var p = Vector3.Transform(new Vector3((float)vertices[i], (float)vertices[i + 1], (float)vertices[i + 2]), world);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    any = true;
                }
            }
            if (!any) return (new Vector3(0, 100, 0), 300f);
            var center = (min + max) * 0.5f;
            return (center, MathF.Max(1f, (max - min).Length()));
        }

        private Matrix4x4 WorldOf(FbxModel model)
        {
            var world = model.LocalMatrix;
            for (var p = model.Parent; p != null; p = p.Parent) world *= p.LocalMatrix;
            return world;
        }

        // ---- cameras ----------------------------------------------------------------------

        private void CreateCameras((Vector3 Center, float Size) bounds)
        {
            float cx = bounds.Center.X, cy = bounds.Center.Y, cz = bounds.Center.Z;
            float height = MathF.Max(10f, bounds.Size * 2.5f);
            var cameras = new List<AktObject>
            {
                Camera("Perspective", false, Matrix4x4.CreateTranslation(bounds.Center), height),
                Camera("Front", true, Matrix4x4.CreateTranslation(cx, cy, 0), height),
            };
            // The axes are the ones the eight fixed views use in the samples (data-model.md "Cameras").
            var back = Matrix4x4.CreateScale(-1, 1, -1);
            back.M41 = cx; back.M42 = cy; back.M43 = 0;
            cameras.Add(Camera("Back", true, back, height));

            var right = new Matrix4x4(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1);
            right.M41 = 0; right.M42 = cy; right.M43 = cz;
            cameras.Add(Camera("Right", true, right, height));
            var left = new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1);
            left.M41 = 0; left.M42 = cy; left.M43 = cz;
            cameras.Add(Camera("Left", true, left, height));
            cameras.Add(Camera("Top", true, new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1), height));
            cameras.Add(Camera("Bottom", true, new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1), height));
            // The Photo camera is the same in every shipped scene (fixed view, framed by the UI).
            cameras.Add(Camera("Photo", false, AktMath.ToMatrix(
            [
                0.980067f, 0f, -0.198669f, 0f,
                -0.099335f, 0.866025f, -0.490033f, 0f,
                0.172053f, 0.5f, 0.848763f, 0f,
                120.4369f, 350f, 594.1338f, 1f,
            ]), height));

            _scene.Set("CameraList", cameras.Select(c => new AktReference(c)).ToList());
        }

        private AktObject Camera(string name, bool ortho, Matrix4x4 ltm, float height)
        {
            var camera = New(ortho ? "CCameraOrtho" : "CCamera", name, _scene);
            camera.Set("Flags", 0x401u);
            camera.Set("LTM", AktMath.ToArray(ltm));
            camera.Set("CameraType", 0);
            camera.Set("RenderMode", (byte)0);
            camera.Set("RenderFlags", 4153u);
            camera.Set("_matrix", AktMath.ToArray(Matrix4x4.Identity));
            camera.Set("_targetDistance", 2f * height);
            if (ortho)
            {
                camera.Set("CentimetersInHeight", height);
                camera.Set("WorldOffsetX", 0f);
                camera.Set("WorldOffsetY", 0f);
            }
            else camera.Set("_ratio", 1f);
            return camera;
        }

        // ---- materials --------------------------------------------------------------------

        private void CreateMaterials()
        {
            var materials = _nodes.Where(n => n.Value.Name == "Material").Select(n => n.Key).ToList();
            if (materials.Count == 0) return;
            var folder = New("CDummy", "__Materials", _scene);
            foreach (long id in materials) _materialByNode[id] = Material(id, folder);
        }

        private AktObject Material(long id, AktObject folder)
        {
            var node = _nodes[id];
            var material = New("CMaterial", PlainName(node), folder);
            foreach (var (channel, r, g, b, a) in FbxImportSchema.ColorDefaults)
                Color(material, channel, new Vector4(r, g, b, a));
            foreach (var (channel, value) in FbxImportSchema.ScalarDefaults)
                Scalar(material, channel, value);
            if (node.Child("Properties70") is { } props)
            {
                Color(material, "Ambient", ColorProperty(props, "AmbientColor") ?? ColorProperty(props, "Ambient") ?? new Vector4(0.05088f, 0.05088f, 0.05088f, 1f));
                Scalar(material, "AmbientFactor", ScalarProperty(props, "AmbientFactor") ?? 0f);
                Color(material, "Diffuse", ColorProperty(props, "DiffuseColor") ?? ColorProperty(props, "Diffuse") ?? new Vector4(0.8f, 0.8f, 0.8f, 1f));
                Scalar(material, "DiffuseFactor", ScalarProperty(props, "DiffuseFactor") ?? 1f);
                Color(material, "Specular", ColorProperty(props, "SpecularColor") ?? ColorProperty(props, "Specular") ?? new Vector4(0.8f, 0.8f, 0.8f, 1f));
                Scalar(material, "SpecularFactor", ScalarProperty(props, "SpecularFactor") ?? 0.25f);
                Color(material, "Emissive", ColorProperty(props, "EmissiveColor") ?? ColorProperty(props, "Emissive") ?? new Vector4(1f, 1f, 1f, 1f));
                Scalar(material, "EmissiveFactor", ScalarProperty(props, "EmissiveFactor") ?? 0f);
                Color(material, "Transparency", ColorProperty(props, "TransparentColor") ?? new Vector4(0f, 0f, 0f, 1f));
                Scalar(material, "TransparencyFactor", ScalarProperty(props, "TransparencyFactor") ?? 0f);
                Color(material, "Reflection", ColorProperty(props, "ReflectionColor") ?? new Vector4(0.8f, 0.8f, 0.8f, 1f));
                Scalar(material, "ReflectionFactor", ScalarProperty(props, "ReflectionFactor") ?? 0f);
                Scalar(material, "Shininess", ScalarProperty(props, "ShininessExponent") ?? ScalarProperty(props, "Shininess") ?? 25f);
                Scalar(material, "BumpFactor", ScalarProperty(props, "BumpFactor") ?? 0f);
            }

            // The diffuse texture: a Texture connected to the material's DiffuseColor, through a Video.
            foreach (var connection in ChildrenOf(id))
            {
                if (connection.Property != "DiffuseColor" || _nodes.GetValueOrDefault(connection.Child)?.Name != "Texture") continue;
                if (Texture(connection.Child) is { } texture)
                    ((AktStruct)material["Diffuse"]!).Set("Texture", new AktReference(texture));
                break;
            }
            return material;
        }

        private AktObject? Texture(long textureNodeId)
        {
            long videoId = ChildrenOf(textureNodeId)
                .Select(c => c.Child)
                .FirstOrDefault(c => _nodes.GetValueOrDefault(c)?.Name == "Video");
            if (videoId != 0 && _textureByVideo.TryGetValue(videoId, out var cached)) return cached;

            var textureNode = _nodes[textureNodeId];
            var videoNode = videoId != 0 ? _nodes.GetValueOrDefault(videoId) : null;
            string? rawName = videoNode?.Value<string>("Filename")
                              ?? textureNode.Value<string>("FileName")
                              ?? textureNode.Value<string>("RelativeFilename");
            string fileName = Path.GetFileName(string.IsNullOrEmpty(rawName) ? "texture" : rawName);
            byte[] data = videoNode?.Value<byte[]>("Content") ?? [];
            if (data.Length == 0)
            {
                string? relative = videoNode?.Value<string>("RelativeFilename") ?? textureNode.Value<string>("RelativeFilename");
                string? directory = _options.TextureDirectory ?? (_sourcePath is null ? null : Path.GetDirectoryName(_sourcePath));
                if (relative != null && directory != null && File.Exists(Path.Combine(directory, relative)))
                    data = File.ReadAllBytes(Path.Combine(directory, relative));
                else
                    Warn($"texture '{fileName}' has no embedded image and was not found beside the FBX");
            }

            var folder = _scene.Children.FirstOrDefault(c => c.Name == "__Textures");
            if (folder == null) folder = New("CDummy", "__Textures", _scene);
            var texture = New("CTexture", fileName, folder);
            texture.Set("Data", data);
            if (videoId != 0) _textureByVideo[videoId] = texture;
            return texture;
        }

        private static void Color(AktObject material, string channel, Vector4 value) =>
            ((AktStruct)material[channel]!).Set("Value", AktMath.ToArray(value));

        private static void Scalar(AktObject material, string channel, float value) =>
            ((AktStruct)material[channel]!).Set("Value", value);

        private static Vector4? ColorProperty(FbxNode props, string name)
        {
            var values = FbxScene.P(props, name);
            if (values is not { Length: >= 3 }) return null;
            return new Vector4((float)Convert.ToDouble(values[0]), (float)Convert.ToDouble(values[1]),
                (float)Convert.ToDouble(values[2]), values.Length > 3 ? (float)Convert.ToDouble(values[3]) : 1f);
        }

        private static float? ScalarProperty(FbxNode props, string name) =>
            FbxScene.P(props, name) is { Length: > 0 } values ? (float)Convert.ToDouble(values[0]) : null;

        // ---- characters -------------------------------------------------------------------

        private void BuildCharacter(FbxModel root)
        {
            if (root.HasUnsupportedTransform)
                Warn($"model '{root.Name}' has pivots or a rotation order other than XYZ; only its TRS was imported");

            var characterObj = New("CCharacter", root.Name, _scene);
            SetObject3D(characterObj, root);
            _objectByModel[root.Id] = characterObj;

            var hidden = New("CObject3DHiddenInTree", "__BlendshapeControllers", characterObj);
            hidden.Set("Flags", 0x4F9u);
            hidden.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            hidden.Set("HierarchyChangedNotifier", false);

            var anims = New("CDummy", "__Anims", characterObj);
            var bindFolder = New("CDummy", "__BindPoses", characterObj);
            var refPoseOld = New("CCharacterPose", "__RefPoseOld", characterObj);
            var rigAnim = New("CAnimation", "__RigAnim", characterObj);
            rigAnim.Set("CurrentTime", 0);

            var joints = new List<(FbxModel, AktObject)>();
            var meshes = new List<(FbxModel, AktObject)>();
            foreach (var child in root.Children) BuildModelNode(child, characterObj, joints, meshes);

            var build = new CharacterBuild
            {
                Root = root,
                Object = characterObj,
                Anims = anims,
                BindFolder = bindFolder,
                BindPose = null!,
                RigAnim = rigAnim,
                Joints = joints,
                Meshes = meshes,
                Controllers = joints.Select(j => j.Item2).ToList(),
                RefTransforms = [],
                JointIndex = joints.Select((j, i) => (j.Item1.Id, i)).ToDictionary(p => p.Id, p => p.i),
            };
            _characters.Add(build);

            var refPose = BuildReferencePose(build, refPoseOld);
            build.BindPose = BuildBindPose(build, refPose);

            characterObj.Set("JointArray", joints.Select(j => new AktReference(j.Item2)).ToList());
            characterObj.Set("Controllers", joints.Select(j => new AktReference(j.Item2)).ToList());
            characterObj.Set("RefPose", new AktReference(refPose));
            characterObj.Set("CurrentPose", new AktReference(refPose));
            characterObj.Set("RefPoseOld", new AktReference(refPoseOld));
            characterObj.Set("RigidModified", true);
            characterObj.Set("SmoothModified", true);

            foreach (var (model, mesh) in meshes) FillMesh(model, mesh, build);
        }

        private void BuildModelNode(FbxModel model, AktObject parent,
                                    List<(FbxModel, AktObject)> joints, List<(FbxModel, AktObject)> meshes)
        {
            AktObject obj;
            if (model.Kind == "LimbNode")
            {
                obj = New("CJoint", model.Name, parent);
                SetObject3D(obj, model);
                obj.Set("SmoothMethod", 0);
                joints.Add((model, obj));
            }
            else if (model.Kind == "Mesh")
            {
                obj = New("CMesh", model.Name, parent);
                SetObject3D(obj, model);
                AddBlendshapeScaffold(obj);
                meshes.Add((model, obj));
            }
            else
            {
                obj = New("CObject3D", model.Name, parent);
                SetObject3D(obj, model);
            }
            _objectByModel[model.Id] = obj;
            foreach (var child in model.Children) BuildModelNode(child, obj, joints, meshes);
        }

        private void BuildObjectTree(FbxModel model, AktObject parent)
        {
            AktObject obj;
            if (model.Kind == "Mesh")
            {
                obj = New("CMesh", model.Name, parent);
                SetObject3D(obj, model);
                AddBlendshapeScaffold(obj);
                _objectByModel[model.Id] = obj;
                FillMesh(model, obj, null);
            }
            else
            {
                obj = New("CObject3D", model.Name, parent);
                SetObject3D(obj, model);
                _objectByModel[model.Id] = obj;
            }
            foreach (var child in model.Children) BuildObjectTree(child, obj);
        }

        private void AddBlendshapeScaffold(AktObject mesh)
        {
            var blendshapes = New("CObject3D", "Blendshapes", mesh);
            blendshapes.Set("Flags", 0x4F8u);
            blendshapes.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));
            New("CDummy", "__BlendshapeFaces", mesh);
        }

        private static void SetObject3D(AktObject obj, FbxModel model)
        {
            uint flags = 0x401u;
            if (model.InheritType == 2) flags |= 0x2u;
            obj.Set("Flags", flags);
            obj.Set("LTM", AktMath.ToArray(model.LocalMatrix));
        }

        /// <summary>
        /// Builds the reference pose — the base every animation key is an offset from — out of the
        /// FBX models' own local transforms, which is what the export wrote them from
        /// (fbx-export.md "Models"). A key's local transform is <c>base ∘ delta</c>, so this is
        /// also the matrix a joint's <c>LTM</c> holds right after an import (data-model.md).
        /// </summary>
        private AktObject BuildReferencePose(CharacterBuild build, AktObject refPoseOld)
        {
            var pose = New("CCharacterPose", "Reference Pose", build.BindFolder);
            var matrices = new List<object?>(build.Joints.Count);
            var transforms = new Transform[build.Joints.Count];
            for (int i = 0; i < build.Joints.Count; i++)
            {
                var (model, joint) = build.Joints[i];
                var local = model.LocalMatrix;
                matrices.Add(AktMath.ToArray(local));
                transforms[i] = Transform.FromMatrixLoose(local);
                joint.Set("LTM", AktMath.ToArray(local));
            }

            pose.Set("Matrices", matrices);
            refPoseOld.Set("Matrices", matrices.Select(m => (object?)((float[])m!).ToArray()).ToList());
            build.RefTransforms = transforms;
            return pose;
        }

        /// <summary>
        /// Builds the bind pose the skinned meshes are bound in. The export writes it as the
        /// loose <c>Pose</c> objects (world matrices, fbx-export.md "Skinning"); it differs from the
        /// reference pose in a scene whose rig was edited after binding. Without a <c>Pose</c> the
        /// reference pose is the bind pose.
        /// </summary>
        private AktObject BuildBindPose(CharacterBuild build, AktObject referencePose)
        {
            var pose = New("CCharacterPose", "Bind Pose", build.BindFolder);
            var reference = (IReadOnlyList<object?>)referencePose["Matrices"]!;
            if (_poseWorld.Count == 0)
            {
                pose.Set("Matrices", reference.Select(m => (object?)((float[])m!).Clone()).ToList());
                return pose;
            }

            var characterWorld = WorldOf(build.Root);
            var worlds = new Dictionary<long, Matrix4x4>();

            Matrix4x4 World(FbxModel model)
            {
                if (worlds.TryGetValue(model.Id, out var cached)) return cached;
                Matrix4x4 world;
                if (_poseWorld.TryGetValue(model.Id, out var fromPose)) world = fromPose;
                else
                {
                    var parentJoint = NearestJoint(model.Parent, build.JointIndex);
                    var parentWorld = parentJoint is { } pj ? World(pj) : characterWorld;
                    var parentScale = parentJoint is { } ps ? Object3D.LocalScale(ps.LocalMatrix) : Vector3.One;
                    world = WorldComposition.Compose(model.LocalMatrix, parentWorld, parentScale, model.InheritType == 2);
                }
                worlds[model.Id] = world;
                return world;
            }

            var matrices = new List<object?>(build.Joints.Count);
            for (int i = 0; i < build.Joints.Count; i++)
            {
                var (model, _) = build.Joints[i];
                if (!_poseWorld.ContainsKey(model.Id))
                {
                    // A joint the Pose objects do not list keeps its reference pose matrix.
                    matrices.Add(((float[])reference[i]!).Clone());
                    continue;
                }
                var parentJoint = NearestJoint(model.Parent, build.JointIndex);
                var parentWorld = parentJoint is { } pj ? World(pj) : characterWorld;
                var parentScale = parentJoint is { } ps ? Object3D.LocalScale(ps.LocalMatrix) : Vector3.One;
                var world = World(model);
                if (!WorldComposition.TryDecompose(world, parentWorld, parentScale, model.InheritType == 2, out var local))
                {
                    Warn($"joint '{model.Name}' has a singular parent transform; its local bind matrix was taken from the FBX local transform");
                    local = model.LocalMatrix;
                }
                matrices.Add(AktMath.ToArray(local));
            }

            pose.Set("Matrices", matrices);
            return pose;
        }

        private static FbxModel? NearestJoint(FbxModel? model, Dictionary<long, int> jointIndex)
        {
            for (; model != null; model = model.Parent)
                if (jointIndex.ContainsKey(model.Id)) return model;
            return null;
        }

        // ---- meshes -----------------------------------------------------------------------

        private (long Id, FbxNode? Node) GeometryOf(long modelId)
        {
            foreach (var connection in ChildrenOf(modelId))
                if (_nodes.GetValueOrDefault(connection.Child) is { } node && node.Name == "Geometry")
                    return (connection.Child, node);
            return (0, null);
        }

        private void FillMesh(FbxModel model, AktObject mesh, CharacterBuild? character)
        {
            if (model.HasUnsupportedTransform)
                Warn($"mesh '{model.Name}' has pivots or a rotation order other than XYZ; only its TRS was imported");
            var (geometryId, geometry) = GeometryOf(model.Id);
            if (geometry == null)
            {
                Warn($"mesh '{model.Name}' has no Geometry; it was imported empty");
                return;
            }

            var vertices = ReadDoubles(geometry, "Vertices");
            var indices = ReadInts(geometry, "PolygonVertexIndex");
            int pointCount = vertices.Length / 3;
            var points = new List<object?>(pointCount);
            for (int i = 0; i < pointCount; i++)
                points.Add(new[] { (float)vertices[i * 3], (float)vertices[i * 3 + 1], (float)vertices[i * 3 + 2], 1f });
            mesh.Set("VertexArray", points);

            var corners = new List<uint>(indices.Length);
            var polygons = new List<object?>();
            var polygonSizes = new List<int>();
            var materialOfPolygon = new List<uint>();
            int start = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                int raw = indices[i];
                uint point = raw < 0 ? (uint)(~raw) : (uint)raw;
                corners.Add(point);
                if (raw < 0)
                {
                    var polygon = _schema.NewStruct("CPolygon");
                    var list = (List<object?>)polygon["IndexArray"]!;
                    for (int c = start; c <= i; c++) list.Add((uint)c);
                    polygon.Set("MatIndex", 0u);
                    polygons.Add(polygon);
                    polygonSizes.Add(i - start + 1);
                    materialOfPolygon.Add(0u);
                    start = i + 1;
                }
            }
            if (start != indices.Length)
                Warn($"mesh '{model.Name}' ends in an unterminated polygon");
            mesh.Set("PolygonArray", polygons);
            mesh.Set("VertexIArray", corners.Select(c => (object?)c).ToList());

            int cornerCount = corners.Count;
            var cornerPoints = corners.ToArray();

            // Materials by polygon.
            var materialRefs = ChildrenOf(model.Id)
                .Where(c => _nodes.GetValueOrDefault(c.Child)?.Name == "Material" && _materialByNode.ContainsKey(c.Child))
                .Select(c => new AktReference(_materialByNode[c.Child])).ToList();
            mesh.Set("MaterialArray", materialRefs);
            if (geometry.Child("LayerElementMaterial") is { } materialElement)
            {
                var slots = ReadInts(materialElement, "Materials");
                string mapping = materialElement.Value<string>("MappingInformationType") ?? "ByPolygon";
                if (mapping == "ByPolygon")
                    for (int p = 0; p < materialOfPolygon.Count && p < slots.Length; p++)
                        materialOfPolygon[p] = slots[p] >= 0 && slots[p] < materialRefs.Count ? (uint)slots[p] : 0u;
            }
            for (int p = 0; p < polygons.Count; p++) ((AktStruct)polygons[p]!).Set("MatIndex", materialOfPolygon[p]);

            var normals = ReadLayers(geometry, "LayerElementNormal", "Normals", "NormalsIndex", 3, cornerCount, cornerPoints, polygonSizes);
            if (normals is { } normalLayers)
            {
                var widths = new List<double>();
                foreach (var element in geometry.ChildrenNamed("LayerElementNormal"))
                    widths.AddRange(ReadDoubles(element, "NormalsW"));
                mesh.Set("NormalArray", normalLayers.Values.Select((v, i) =>
                    (object?)new[] { v[0], v[1], v[2], i < widths.Count ? (float)widths[i] : 0f }).ToList());
                mesh.Set("NormalIArray", normalLayers.Indices.Select(i => (object?)i).ToList());
                mesh.Set("NormalLayersCount", (uint)normalLayers.Layers);
            }

            var colors = ReadLayers(geometry, "LayerElementColor", "Colors", "ColorIndex", 4, cornerCount, cornerPoints, polygonSizes);
            if (colors is { } colorLayers)
            {
                mesh.Set("ColorArray", colorLayers.Values.Select(v => (object?)new[] { v[0], v[1], v[2], v[3] }).ToList());
                mesh.Set("ColorIArray", colorLayers.Indices.Select(i => (object?)i).ToList());
                mesh.Set("ColorLayersCount", (uint)colorLayers.Layers);
            }

            var uvs = ReadLayers(geometry, "LayerElementUV", "UV", "UVIndex", 2, cornerCount, cornerPoints, polygonSizes);
            if (uvs is { } uvLayers)
            {
                // FBX's v runs up from the bottom, the .akt's v down from the top
                foreach (var value in uvLayers.Values) value[1] = 1f - value[1];
                mesh.Set("UVArray", uvLayers.Values.Select(v => (object?)new[] { v[0], v[1] }).ToList());
                mesh.Set("UVIArray", uvLayers.Indices.Select(i => (object?)i).ToList());
                mesh.Set("UVLayersCount", (uint)uvLayers.Layers);
                var elements = geometry.ChildrenNamed("LayerElementUV").ToList();
                var names = new List<object?>();
                for (int i = 0; i < uvLayers.Layers; i++)
                {
                    string name = i < elements.Count ? elements[i].Value<string>("Name") ?? "" : "";
                    if (name.Length == 0) name = i == 0 ? "UVMap" : $"UVSet{i}";
                    names.Add(name);
                }
                mesh.Set("UVLayersNameArray", names);
            }

            if (geometry.Child("LayerElementSmoothing") is { } smoothing)
            {
                var groups = ReadInts(smoothing, "Smoothing");
                mesh.Set("SmoothingArray", groups.Select(g => (object?)g).ToList());
                mesh.Set("SmoothingLayersCount", 1u);
            }

            if (_options.Skins && character != null && FillSkin(geometryId, geometry, mesh, character))
            {
                mesh.Set("Skinned", true);
                mesh.Set("SkinnedWithAkeytsu", false);
                mesh.Set("SmoothSkinningProcessed", true);
                mesh.Set("SkinnedMeshIsLocked", false);
                mesh.Set("BindPose", new AktReference(character.BindPose));
            }
            else
            {
                mesh.Set("Skinned", false);
                mesh.Set("SkinnedWithAkeytsu", false);
                mesh.Set("SmoothSkinningProcessed", false);
                mesh.Set("SkinnedMeshIsLocked", false);
            }
        }

        private bool FillSkin(long geometryId, FbxNode geometry, AktObject mesh, CharacterBuild character)
        {
            long skinId = ChildrenOf(geometryId)
                .Where(c => IsDeformer(c.Child, "Skin"))
                .Select(c => c.Child).FirstOrDefault();
            if (skinId == 0 && _byChild.TryGetValue(geometryId, out var parents))
                skinId = parents.Where(c => IsDeformer(c.Parent, "Skin")).Select(c => c.Parent).FirstOrDefault();
            if (skinId == 0) return false;

            var clusters = ChildrenOf(skinId).Where(c => IsDeformer(c.Child, "Cluster")).Select(c => c.Child).ToList();
            if (clusters.Count == 0) return false;

            int pointCount = ReadDoubles(geometry, "Vertices").Length / 3;
            var weights = new List<(uint Bone, float Weight)>?[pointCount];
            bool any = false;
            foreach (long clusterId in clusters)
            {
                // The joint hangs under the cluster (`C: "OO", joint, cluster`).
                long jointModel = ChildrenOf(clusterId)
                    .Select(c => c.Child).FirstOrDefault(id => _models.ContainsKey(id));
                if (jointModel == 0 || !character.JointIndex.TryGetValue(jointModel, out int bone))
                {
                    Warn($"a skin cluster of mesh '{mesh.Name}' has no joint of the character; it was skipped");
                    continue;
                }
                var cluster = _nodes[clusterId];
                var indexes = ReadInts(cluster, "Indexes");
                var values = ReadDoubles(cluster, "Weights");
                for (int i = 0; i < indexes.Length && i < values.Length; i++)
                {
                    int point = indexes[i];
                    if (point < 0 || point >= pointCount) continue;
                    (weights[point] ??= []).Add(((uint)bone, (float)values[i]));
                    any = true;
                }
            }
            if (!any) return false;

            var arrays = new List<object?>(pointCount);
            foreach (var list in weights)
            {
                var array = _schema.NewStruct("CVertexWeightArray");
                var entries = (List<object?>)array["WeightArray"]!;
                foreach (var (bone, weight) in list ?? [])
                {
                    var entry = _schema.NewStruct("CVertexWeight");
                    entry.Set("BoneIndex", bone);
                    entry.Set("Weight", weight);
                    entries.Add(entry);
                }
                arrays.Add(array);
            }
            mesh.Set("SkinWeightArray", arrays);
            return true;
        }

        private bool IsDeformer(long id, string kind) =>
            _nodes.GetValueOrDefault(id) is { } node && node.Name == "Deformer" &&
            (node.PropertyCount > 2 ? node[2] as string : null) == kind;

        /// <summary>
        /// Every element of one attribute type as one shared value array plus layer-major corner
        /// index slices.
        /// </summary>
        private (List<float[]> Values, uint[] Indices, int Layers)? ReadLayers(
            FbxNode geometry, string elementName, string valueName, string indexName, int components,
            int cornerCount, uint[] cornerPoints, List<int> polygonSizes)
        {
            var elements = geometry.ChildrenNamed(elementName).ToList();
            if (elements.Count == 0) return null;
            var values = new List<float[]>();
            var slices = new List<uint[]>(elements.Count);
            foreach (var element in elements)
            {
                if (ReadLayer(element, valueName, indexName, components, cornerCount, cornerPoints, polygonSizes)
                    is not { } layer)
                {
                    Warn($"{elementName} layer {slices.Count} of a mesh has an unsupported mapping");
                    slices.Add(new uint[cornerCount]);
                    continue;
                }
                uint offset = (uint)values.Count;
                values.AddRange(layer.Values);
                slices.Add(layer.Indices.Select(i => i + offset).ToArray());
            }
            if (values.Count == 0) return null;
            return (values, slices.SelectMany(s => s).ToArray(), elements.Count);
        }

        /// <summary>
        /// One FBX vertex layer element (normal, colour or UV) as unique values plus one index per
        /// corner, covering the mapping/reference combinations the app and other writers emit.
        /// </summary>
        private static (List<float[]> Values, uint[] Indices)? ReadLayer(
            FbxNode element, string valueName, string indexName, int components,
            int cornerCount, uint[] cornerPoints, List<int> polygonSizes)
        {
            var raw = ReadDoubles(element, valueName);
            if (raw.Length < components) return null;
            int rawCount = raw.Length / components;
            string mapping = element.Value<string>("MappingInformationType") ?? "ByPolygonVertex";
            string reference = element.Value<string>("ReferenceInformationType") ?? "Direct";
            var indices = ReadInts(element, indexName);
            var values = new List<float[]>(rawCount);

            float[] Slice(int i)
            {
                var v = new float[components];
                for (int c = 0; c < components; c++) v[c] = (float)raw[i * components + c];
                return v;
            }

            switch (mapping)
            {
                case "ByPolygonVertex":
                    if (reference == "IndexToDirect" && indices.Length >= cornerCount)
                    {
                        for (int i = 0; i < rawCount; i++) values.Add(Slice(i));
                        return (values, indices.Take(cornerCount).Select(i => i >= 0 ? (uint)i : 0u).ToArray());
                    }
                    if (rawCount < cornerCount) return null;
                    for (int c = 0; c < cornerCount; c++) values.Add(Slice(c));
                    return (values, Enumerable.Range(0, cornerCount).Select(i => (uint)i).ToArray());

                case "ByVertice":
                case "ByVertex":
                case "ByControlPoint":
                    for (int i = 0; i < rawCount; i++) values.Add(Slice(i));
                    return (values, cornerPoints.Select(p => p < rawCount ? p : 0u).ToArray());

                case "ByPolygon":
                {
                    var result = new uint[cornerCount];
                    int corner = 0;
                    for (int p = 0; p < polygonSizes.Count && corner < cornerCount; p++)
                    {
                        int slot = reference == "IndexToDirect" && p < indices.Length ? indices[p] : p;
                        if (slot < 0 || slot >= rawCount) slot = 0;
                        for (int c = 0; c < polygonSizes[p] && corner < cornerCount; c++, corner++)
                        {
                            result[corner] = (uint)values.Count;
                            values.Add(Slice(slot));
                        }
                    }
                    return (values, result);
                }

                case "AllSame":
                    values.Add(Slice(0));
                    return (values, new uint[cornerCount]);

                default:
                    return null;
            }
        }

        // ---- animation --------------------------------------------------------------------

        private void ImportAnimations(CharacterBuild build)
        {
            var targets = _objectByModel
                .Where(kv => _models.ContainsKey(kv.Key) && IsUnder(build.Root, kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (targets.Count == 0) return;

            var controller = (Character)_model!.Wrap(build.Object);
            var folders = new Dictionary<string, AktObject>(StringComparer.Ordinal);

            foreach (var stack in _fbx.Stacks)
            {
                bool any = stack.Layers.Any(l => l.CurveNodes.Any(cn => cn.Model != null && targets.ContainsKey(cn.Model.Id)));
                if (!any) continue;

                var path = stack.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var parent = build.Anims;
                for (int i = 0; i < path.Length - 1; i++)
                {
                    string key = string.Join('/', path.Take(i + 1));
                    if (!folders.TryGetValue(key, out var folder))
                    {
                        folder = New("CDummy", UniqueNames.For(parent, path[i]), parent);
                        folders[key] = folder;
                    }
                    parent = folder;
                }

                string name = path.Length > 0 ? path[^1] : "Animation";
                var animationObj = New("CAnimation", UniqueNames.For(parent, name), parent);
                animationObj.Set("CurrentTime", 0);

                var stacks = new List<(AktObject Obj, AnimStack Wrapper, List<FbxCurveNode> Targets, bool Fitted)>();
                for (int i = stack.Layers.Count - 1; i >= 0; i--)
                {
                    var layer = stack.Layers[i];
                    var layerTargets = layer.CurveNodes.Where(cn => cn.Model != null && targets.ContainsKey(cn.Model.Id)).ToList();
                    if (layerTargets.Count == 0) continue;
                    var times = new SortedSet<int>();
                    foreach (var cn in layerTargets)
                        foreach (var curve in cn.Curves)
                            if (curve != null)
                                foreach (long tick in curve.Times) times.Add(Frame(tick));
                    if (times.Count == 0) continue;
                    bool fit = _options.FitKeys && layer.BlendMode != 0 && Fittable(layerTargets, times);
                    if (fit)
                    {
                        // A fit that saves less than a quarter of the keys keeps the file's own keys:
                        // they are exact, and on an authored layer they are what the animator set.
                        var fitted = FitTimes(layerTargets, targets, build, times);
                        fit = fitted.Count * 4 <= times.Count * 3;
                        if (fit)
                        {
                            Warn($"animation '{stack.Name}' layer '{layer.Name}': re-keyed {times.Count} key times to {fitted.Count}");
                            times = new SortedSet<int>(fitted);
                        }
                    }
                    times.Add(times.Max + 1);

                    var stackObj = New("CAnimStack", UniqueNames.For(animationObj, layer.Name), animationObj);
                    stackObj.Set("Flags", layer.BlendMode == 0 ? 10u : 2u);
                    stackObj.Set("FlagsNoModify", 0u);
                    stackObj.Set("Opacity", 1f);
                    stackObj.Set("Tweenage", 0.5f);
                    stackObj.Set("Color", StackColor(stacks.Count));
                    var timeArray = new List<object?>();
                    foreach (int t in times) timeArray.Add(StackKey(t));
                    stackObj.Set("TimeArray", timeArray);
                    stacks.Add((stackObj, (AnimStack)_model.Wrap(stackObj), layerTargets, fit));
                }

                if (stacks.Count == 0) continue;
                animationObj.Set("StackList", stacks.Select(s => new AktReference(s.Obj)).ToList());
                animationObj.Set("CurrentStack", new AktReference(stacks[0].Obj));
                foreach (var (_, wrapper, layerTargets, fitted) in stacks) FillStack(wrapper, layerTargets, targets, build, fitted);
            }

            controller.CurrentAnimation = controller.Animations.FirstOrDefault();
        }

        private AktStruct StackKey(int time, uint flags = 0)
        {
            var entry = _schema.NewStruct("CAnimStackKey");
            entry.Set("Time", time);
            entry.Set("Flags", flags);
            entry.Set("FadeIn", (ushort)0);
            entry.Set("FadeOut", (ushort)0);
            return entry;
        }

        private void FillStack(AnimStack stack, List<FbxCurveNode> curveNodes,
                               Dictionary<long, AktObject> targets, CharacterBuild build, bool fitted = false)
        {
            // An additive FBX layer holds the layer's own delta, which is exactly the .akt key value;
            // an override layer holds the absolute local transform (fbx-export.md "Animation").
            bool additive = stack.IsAdditive;
            foreach (var group in curveNodes.GroupBy(cn => cn.Model!.Id))
            {
                if (!targets.TryGetValue(group.Key, out var targetObj)) continue;
                if (_model!.Wrap(targetObj) is not Object3D target) continue;

                var track = TrackFactory.Create(stack, target);
                var nodes = group.Where(cn => cn.Property != null).GroupBy(cn => cn.Property!).ToDictionary(g => g.Key, g => g.First());
                uint mask = 0;
                if (nodes.ContainsKey("Lcl Translation")) mask |= 0x7;
                if (nodes.ContainsKey("Lcl Rotation")) mask |= 0x38;
                if (nodes.ContainsKey("Lcl Scaling")) mask |= 0x1C0;
                track.Source.Set("ChannelMask", mask == 0 ? 0x7u : mask);

                var baseTransform = build.JointIndex.TryGetValue(group.Key, out int index)
                    ? build.RefTransforms[index]
                    : Transform.FromMatrixLoose(target.LocalMatrix);

                var keys = track.Keys;
                var stackKeys = stack.Keys;
                for (int k = 0; k < keys.Count && k < stackKeys.Count; k++)
                {
                    if (keys[k] is not TransformKey key) continue;
                    key.Value = KeyValue(nodes, baseTransform, additive, Ticks(stackKeys[k].Time));
                }

                // The last key replays key 0 (animation-stacks.md "Key marks").
                if (keys.Count > 1 && keys[0] is TransformKey firstKey && keys[^1] is TransformKey lastKey)
                    lastKey.Value = firstKey.Value;

                // The layer's per-key flags say the tangent type (0x0102 step, 0x0104 linear, else
                // spline); the app stores that as one mode per channel (curveboard.md). Fitted keys
                // are always spline keys: the fit chose them for spline playback.
                for (int k = 0; k < keys.Count; k++)
                {
                    int source = fitted || k == keys.Count - 1 || k >= stackKeys.Count ? 0 : KeyIndex(nodes, stackKeys[k].Time);
                    int channels = k < track.TangentModes.Count ? track.TangentModes[k].Length : 0;
                    for (int channel = 0; channel < channels; channel++)
                        track.SetTangentMode(k, channel, fitted ? SplineMode : TangentModeOf(nodes, channel, source));
                }
                TangentRecompute.Recompute(track);
            }
        }

        /// <summary>
        /// The stack key value at <paramref name="ticks"/>: an additive layer's own delta as it is, an
        /// override layer's absolute local transform as an offset from the rest transform.
        /// </summary>
        private static Transform KeyValue(Dictionary<string, FbxCurveNode> nodes, Transform baseTransform, bool additive, long ticks)
        {
            var translation = nodes.TryGetValue("Lcl Translation", out var tn) ? tn.Evaluate(ticks) : baseTransform.Translation;
            var rotation = nodes.TryGetValue("Lcl Rotation", out var rn) ? RotationFromEuler(rn.Evaluate(ticks)) : baseTransform.Rotation;
            var scale = nodes.TryGetValue("Lcl Scaling", out var sn) ? sn.Evaluate(ticks) : baseTransform.Scale;
            return additive
                ? new Transform(translation, Quaternion.Normalize(rotation), scale)
                : new Transform(
                    translation - baseTransform.Translation,
                    Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(baseTransform.Rotation), rotation)),
                    Divide(scale, baseTransform.Scale));
        }

        /// <summary>
        /// True when a layer's keys are re-fitted: it has no stepped key, and its keys sit on (nearly)
        /// every frame, as in a bake, or its curves are spline curves keyed at different times, as an
        /// engine's or another tool's curve reduction writes them. A layer whose curves all share a few
        /// key times holds authored poses and keeps them.
        /// </summary>
        private static bool Fittable(List<FbxCurveNode> curveNodes, SortedSet<int> times)
        {
            var curves = curveNodes.SelectMany(cn => cn.Curves).Where(c => c != null).Select(c => c!).ToList();
            if (curves.Any(c => c.KeyFlags.Any(f => (f & 0xE) == 2))) return false;
            if (times.Count * 10 >= (times.Max - times.Min + 1) * 9) return true;
            bool shared = curves.All(c => c.Times.AsSpan().SequenceEqual(curves[0].Times));
            return !shared && curves.All(c => c.KeyFlags.All(f => (f & 0xE) != 4));
        }

        // Fit tolerances: a tenth of a degree for rotation, a ten-thousandth of the rig's rest size
        // for translation and 1e-3 of a scale factor - below what shows on screen, loose enough that
        // a per-frame bake comes back with a fraction of its keys.
        private const float FitRotationTolerance = 0.1f * MathF.PI / 180f;
        private const float FitTranslationFraction = 1e-4f;
        private const float FitScaleTolerance = 1e-3f;

        /// <summary>
        /// The stack key frames of an override layer: the file's curves sampled at every frame from its
        /// first key to its last, keyed where the app's spline playback needs a key to stay within the
        /// fit tolerances.
        /// </summary>
        private List<int> FitTimes(List<FbxCurveNode> curveNodes, Dictionary<long, AktObject> targets,
                                   CharacterBuild build, SortedSet<int> fileTimes)
        {
            int first = fileTimes.Min, count = fileTimes.Max - first + 1;
            var tracks = new List<Transform[]>();
            foreach (var group in curveNodes.GroupBy(cn => cn.Model!.Id))
            {
                if (!targets.TryGetValue(group.Key, out var targetObj)) continue;
                if (_model!.Wrap(targetObj) is not Object3D target) continue;
                var nodes = group.Where(cn => cn.Property != null).GroupBy(cn => cn.Property!).ToDictionary(g => g.Key, g => g.First());
                var baseTransform = build.JointIndex.TryGetValue(group.Key, out int index)
                    ? build.RefTransforms[index]
                    : Transform.FromMatrixLoose(target.LocalMatrix);
                var samples = new Transform[count];
                for (int f = 0; f < count; f++) samples[f] = KeyValue(nodes, baseTransform, false, Ticks(first + f));
                tracks.Add(samples);
            }

            return StackKeyFit.Fit(first, tracks,
                FitTranslationFraction * RestSize(build.Root), FitRotationTolerance, FitScaleTolerance);
        }

        /// <summary>The diagonal of the box around the rest positions of the character's models, at least 1.</summary>
        private static float RestSize(FbxModel root)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            void Walk(FbxModel model, Matrix4x4 parentWorld)
            {
                var world = model.LocalMatrix * parentWorld;
                min = Vector3.Min(min, world.Translation);
                max = Vector3.Max(max, world.Translation);
                foreach (var child in model.Children) Walk(child, world);
            }
            Walk(root, Matrix4x4.Identity);
            return MathF.Max((max - min).Length(), 1);
        }

        /// <summary>Index of the curve key at <paramref name="time"/>, or 0 when the channel has none there.</summary>
        private int KeyIndex(Dictionary<string, FbxCurveNode> nodes, int time)
        {
            if (nodes.TryGetValue("Lcl Translation", out var node) && node.Curves[0] is { } curve)
            {
                var times = curve.Times;
                long ticks = Ticks(time);
                int index = Array.BinarySearch(times, ticks);
                if (index >= 0) return index;
                // The key times can sit a few ticks either side of the frame time.
                long half = (long)(FbxScene.TicksPerSecond / _frameRate / 2);
                int next = ~index;
                if (next < times.Length && Math.Abs(times[next] - ticks) <= half) return next;
                if (next > 0 && Math.Abs(times[next - 1] - ticks) <= half) return next - 1;
            }
            return 0;
        }

        /// <summary>The spline (auto) tangent mode, the same for both sides.</summary>
        private static readonly TangentMode SplineMode = new(0x100u);

        private static TangentMode TangentModeOf(Dictionary<string, FbxCurveNode> nodes, int channel, int key)
        {
            string property = channel switch
            {
                < 3 => "Lcl Translation",
                < 6 => "Lcl Rotation",
                < 9 => "Lcl Scaling",
                _ => "",
            };
            FbxCurve? curve = property.Length > 0 && nodes.TryGetValue(property, out var node) ? node.Curves[channel % 3] : null;
            int flags = curve?.KeyFlags.ElementAtOrDefault(key) ?? 0x2108;
            uint type = (flags & 0xE) switch { 2 => 5u, 4 => 1u, _ => 0u };
            return new TangentMode(0x100u | type | (type << 4));
        }

        private static Quaternion RotationFromEuler(Vector3 degrees)
        {
            const float rad = MathF.PI / 180f;
            var m = Matrix4x4.CreateRotationX(degrees.X * rad)
                  * Matrix4x4.CreateRotationY(degrees.Y * rad)
                  * Matrix4x4.CreateRotationZ(degrees.Z * rad);
            return Quaternion.CreateFromRotationMatrix(m);
        }

        private static Vector3 Divide(Vector3 a, Vector3 b) =>
            new(SafeDiv(a.X, b.X), SafeDiv(a.Y, b.Y), SafeDiv(a.Z, b.Z));

        private static float SafeDiv(float a, float b) => MathF.Abs(b) < 1e-8f ? 1f : a / b;

        private void BuildRigAnimation(CharacterBuild build)
        {
            var anim = build.RigAnim;
            var stackObj = New("CAnimStack", "Stack", anim);
            stackObj.Set("Flags", 2u);
            stackObj.Set("FlagsNoModify", 0u);
            stackObj.Set("Opacity", 1f);
            stackObj.Set("Tweenage", 0.5f);
            stackObj.Set("Color", new byte[] { 0x40, 0x80, 0xC0, 0xFF });
            stackObj.Set("TimeArray", new List<object?> { StackKey(0), StackKey(0) });
            anim.Set("StackList", new List<AktReference> { new(stackObj) });
            anim.Set("CurrentStack", new AktReference(stackObj));

            var stack = (AnimStack)_model!.Wrap(stackObj);
            foreach (var controller in build.Controllers.AsEnumerable().Reverse())
                TrackFactory.Create(stack, (Object3D)_model.Wrap(controller));
            foreach (var track in stack.Tracks) TangentRecompute.Recompute(track);
        }

        private int Frame(long ticks) => (int)Math.Round(ticks / (double)FbxScene.TicksPerSecond * _frameRate);

        private long Ticks(int frame) => (long)Math.Round(frame / _frameRate * FbxScene.TicksPerSecond);

        private static byte[] StackColor(int index)
        {
            (byte R, byte G, byte B)[] palette =
            [
                (0x40, 0x80, 0xC0), (0xC0, 0x80, 0x40), (0x40, 0xC0, 0x80), (0x80, 0x40, 0xC0),
                (0xC0, 0x40, 0x80), (0x80, 0xC0, 0x40),
            ];
            var (r, g, b) = palette[index % palette.Length];
            return [r, g, b, 0xFF];
        }

        // ---- helpers ----------------------------------------------------------------------

        private AktObject New(string className, string? name, AktObject parent)
        {
            var obj = _schema.NewObject("class " + className);
            obj.Set("Name", name);
            parent.AddChild(obj);
            return obj;
        }

        private IEnumerable<Connection> ChildrenOf(long id) =>
            _byParent.TryGetValue(id, out var list) ? list : [];

        private static string PlainName(FbxNode node) =>
            node.PropertyCount > 1 && node[1] is string s ? s.Split("\0\u0001")[0] : "";

        private bool IsUnder(FbxModel root, long modelId)
        {
            if (!_models.TryGetValue(modelId, out var model)) return false;
            for (; model != null; model = model.Parent)
                if (model.Id == root.Id) return true;
            return false;
        }

        private void Warn(string message) => _options.Warning?.Invoke(message);

        private static float[] ToFloats(double[] values) => values.Select(v => (float)v).ToArray();

        private static double[] ReadDoubles(FbxNode? node, string child)
        {
            if (node?.Child(child) is not { PropertyCount: > 0 } c) return [];
            return c[0] switch
            {
                double[] d => d,
                float[] f => f.Select(v => (double)v).ToArray(),
                long[] l => l.Select(v => (double)v).ToArray(),
                int[] i => i.Select(v => (double)v).ToArray(),
                _ => [],
            };
        }

        private static int[] ReadInts(FbxNode? node, string child)
        {
            if (node?.Child(child) is not { PropertyCount: > 0 } c) return [];
            return c[0] switch
            {
                int[] i => i,
                long[] l => l.Select(v => (int)v).ToArray(),
                uint[] u => u.Select(v => (int)v).ToArray(),
                _ => [],
            };
        }
    }
}

