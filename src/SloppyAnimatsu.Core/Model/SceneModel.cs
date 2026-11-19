using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Typed view of a scene document. Every <see cref="SceneObject"/> reads and writes the fields of
/// its <see cref="AktObject"/> directly, so the model holds no copies: saving the
/// <see cref="Document"/> saves all edits made through the model, and untouched files still
/// round-trip byte for byte.
/// </summary>
public sealed class SceneModel
{
    public AktDocument Document { get; }
    public Scene Scene { get; }

    /// <summary>The file the model was loaded from, when it came from disk (audio files beside it resolve against it).</summary>
    public string? Path { get; }

    private readonly Dictionary<AktObject, SceneObject> _wrappers = new(ReferenceEqualityComparer.Instance);

    public SceneModel(AktDocument document) : this(document, null) { }

    /// <summary>Wraps <paramref name="document"/>, remembering the <paramref name="path"/> its files resolve against.</summary>
    public SceneModel(AktDocument document, string? path)
    {
        Document = document;
        Path = path;
        Scene = Wrap(document.Root) as Scene
                ?? throw new InvalidDataException($"The root object is a {document.Root.ClassKey}, not a CScene.");
    }

    public static SceneModel Load(string path) => new(AktDocument.Load(path), path);

    /// <summary>
    /// The format scenes that are not named <c>.akt</c> are saved in: the SloppyAnimatsu container
    /// unless a caller (the CLI's <c>--akeytsu-format</c>, a test) asks for the plain .akt layout. An
    /// <c>.akt</c> path is always the.akt layout (<see cref="SceneFiles.FormatFor"/>).
    /// </summary>
    public static AktFileFormat SaveFormat { get; set; } = AktFileFormat.SloppyAnimatsu;

    public void Save(string path) => Document.Save(path, SceneFiles.FormatFor(path, SaveFormat));

    public void Save(string path, AktFileFormat format) => Document.Save(path, format);

    /// <summary>Saves as <see cref="Save(string)"/> does, reporting 0 to 1 (the viewer's save progress bar).</summary>
    public void Save(string path, IProgress<double>? progress) =>
        Document.Save(path, SceneFiles.FormatFor(path, SaveFormat), progress);

    /// <summary>The wrapper for an object; the same instance is returned on every call.</summary>
    public SceneObject Wrap(AktObject source)
    {
        if (!_wrappers.TryGetValue(source, out var wrapper))
        {
            wrapper = Create(source);
            _wrappers[source] = wrapper;
        }
        return wrapper;
    }

    public SceneObject? Wrap(AktReference? reference) =>
        reference?.Target is { } target ? Wrap(target) : null;

    private static readonly Dictionary<string, Func<SceneModel, AktObject, SceneObject>> Factories = new(StringComparer.Ordinal)
    {
        ["CScene"] = (m, o) => new Scene(m, o),
        ["CDummy"] = (m, o) => new Folder(m, o),
        ["CMaterial"] = (m, o) => new Material(m, o),
        ["CTexture"] = (m, o) => new Texture(m, o),
        ["CCharacterPose"] = (m, o) => new CharacterPose(m, o),
        ["CAnimation"] = (m, o) => new Animation(m, o),
        ["CAnimStack"] = (m, o) => new AnimStack(m, o),
        ["CAnimStackAudio"] = (m, o) => new AudioStack(m, o),
        ["CAnimTrackBase"] = (m, o) => new AnimTrack(m, o),
        ["CAnimTrackAudio"] = (m, o) => new AudioTrack(m, o),
        ["CAudioObject"] = (m, o) => new AudioObject(m, o),
        ["CAudioEntry"] = (m, o) => new AudioEntry(m, o),
        ["CObject3D"] = (m, o) => new Object3D(m, o),
        ["CCameraBase"] = (m, o) => new Camera(m, o),
        ["CCamera"] = (m, o) => new PerspectiveCamera(m, o),
        ["CCameraAnimated"] = (m, o) => new CameraAnimated(m, o),
        ["CCameraOrtho"] = (m, o) => new OrthoCamera(m, o),
        ["CCharacter"] = (m, o) => new Character(m, o),
        ["CJoint"] = (m, o) => new Joint(m, o),
        ["CMesh"] = (m, o) => new Mesh(m, o),
        ["CController3D"] = (m, o) => new Controller3D(m, o),
        ["CControllerIK"] = (m, o) => new ControllerIK(m, o),
        ["CLooker"] = (m, o) => new CameraTarget(m, o),
        ["CControllerReverseFoot"] = (m, o) => new ControllerReverseFoot(m, o),
        ["CReverseFootLocator_VK"] = (m, o) => new ReverseFootLocator(m, o),
        ["CBlendshape"] = (m, o) => new Blendshape(m, o),
        ["CBlendshapeChannel"] = (m, o) => new BlendshapeChannel(m, o),
        ["CBlendshapeInfoBlendshape"] = (m, o) => new BlendshapeInfo(m, o),
        ["CBlendshapeController"] = (m, o) => new BlendshapeController(m, o),
        ["CBlendshapeSimpleController"] = (m, o) => new BlendshapeSimpleController(m, o),
        ["CBlendshapeFaceController"] = (m, o) => new BlendshapeFaceController(m, o),
        ["CBlendshapeFace"] = (m, o) => new BlendshapeFace(m, o),
        ["CBlendShapesSight"] = (m, o) => new BlendshapeSight(m, o),
        ["CAnimTrackBlendshape"] = (m, o) => new BlendshapeTrack(m, o),
        ["CRetargetingMatching"] = (m, o) => new RetargetingMatching(m, o),
    };

    /// <summary>
    /// Picks the wrapper of the nearest known class in the object's inheritance chain (taken from
    /// the file's schema), so subclasses we have no wrapper for still get their base's features.
    /// </summary>
    private SceneObject Create(AktObject source)
    {
        for (var c = Document.Schema.FindClass(source.TypeName); c != null; c = c.Parent)
            if (Factories.TryGetValue(c.Key, out var factory))
                return factory(this, source);
        if (Factories.TryGetValue(source.ClassKey, out var direct))
            return direct(this, source);
        return new SceneObject(this, source);
    }
}
