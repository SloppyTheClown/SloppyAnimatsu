using System.Numerics;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>
/// The parts of an FBX scene needed to compare animation: the model hierarchy with static
/// local transforms, and animation stacks with their layers and curves.
/// </summary>
/// <remarks>
/// Pivots, offsets, pre/post-rotation and rotation orders other than XYZ are not handled
/// (the usual exports do not have them); <see cref="FbxModel.HasUnsupportedTransform"/> flags them.
/// </remarks>
public sealed class FbxScene
{
    /// <summary>FBX time units per second.</summary>
    public const long TicksPerSecond = 46_186_158_000;

    public FbxScene(FbxDocument document)
    {
        Document = document;
        var objects = document.Node("Objects")?.Children ?? [];
        var byId = new Dictionary<long, object>();

        foreach (var n in objects)
        {
            if (n.PropertyCount < 2 || n[0] is not long id) continue;
            object? o = n.Name switch
            {
                "Model" => new FbxModel(id, n),
                "AnimationStack" => new FbxAnimStack(id, n),
                "AnimationLayer" => new FbxAnimLayer(id, n),
                "AnimationCurveNode" => new FbxCurveNode(id, n),
                "AnimationCurve" => new FbxCurve(id, n),
                _ => null,
            };
            if (o != null) byId[id] = o;
        }

        foreach (var c in document.Node("Connections")?.ChildrenNamed("C") ?? [])
        {
            if (c.PropertyCount < 3 || c[1] is not long childId || c[2] is not long parentId) continue;
            if (!byId.TryGetValue(childId, out var child)) continue;
            byId.TryGetValue(parentId, out var parent);
            string? property = c.PropertyCount > 3 ? c[3] as string : null;
            switch (child, parent)
            {
                case (FbxModel m, FbxModel p): m.Parent = p; p.Children.Add(m); break;
                case (FbxAnimLayer l, FbxAnimStack s): s.Layers.Add(l); break;
                case (FbxCurveNode cn, FbxAnimLayer l): l.CurveNodes.Add(cn); cn.Layer = l; break;
                case (FbxCurveNode cn, FbxModel m) when property != null: cn.Model = m; cn.Property = property; break;
                case (FbxCurve cv, FbxCurveNode cn) when property is "d|X" or "d|Y" or "d|Z": cn.Curves[property[2] - 'X'] = cv; break;
            }
        }

        Models = byId.Values.OfType<FbxModel>().ToList();
        Stacks = objects.Where(n => n.Name == "AnimationStack" && n[0] is long).Select(n => (FbxAnimStack)byId[(long)n[0]]).ToList();
        FrameRate = document.Node("GlobalSettings")?.Child("Properties70") is { } gs ? FrameRateOf(gs) : 30;
    }

    public FbxDocument Document { get; }
    public IReadOnlyList<FbxModel> Models { get; }
    public IReadOnlyList<FbxAnimStack> Stacks { get; }

    /// <summary>Frames per second from GlobalSettings.TimeMode (custom rates use CustomFrameRate).</summary>
    public double FrameRate { get; }

    public static FbxScene Load(string path) => new(FbxDocument.Load(path));

    public static double ToSeconds(long ticks) => ticks / (double)TicksPerSecond;
    public static long ToTicks(double seconds) => (long)Math.Round(seconds * TicksPerSecond);

    /// <summary>Name part of an FBX object name ("Pelvis\0\x01Model" -> "Pelvis").</summary>
    public static string ShortName(FbxNode n) => n[1] is string s ? s.Split("\0\u0001")[0] : "";

    /// <summary>Properties70 entry <paramref name="name"/> → its values (after the 4 header strings).</summary>
    public static object[]? P(FbxNode node, string name) =>
        node.Child("Properties70")?.ChildrenNamed("P").FirstOrDefault(p => p[0] as string == name)?.Properties.Skip(4).ToArray();

    internal static Vector3? Vector(FbxNode node, string name) =>
        P(node, name) is { Length: >= 3 } v ? new Vector3((float)Convert.ToDouble(v[0]), (float)Convert.ToDouble(v[1]), (float)Convert.ToDouble(v[2])) : null;

    private static double FrameRateOf(FbxNode props)
    {
        var mode = props.ChildrenNamed("P").FirstOrDefault(p => p[0] as string == "TimeMode")?.Properties.ElementAtOrDefault(4);
        // FbxTime::EMode values.
        return Convert.ToInt32(mode ?? 0) switch
        {
            1 => 120, 2 => 100, 3 => 60, 4 => 50, 5 => 48, 6 => 30, 7 => 30, 8 => 29.97, 9 => 29.97,
            10 => 25, 11 => 24, 13 => 23.976, 15 => 96, 16 => 72, 17 => 59.94, 18 => 119.88,
            14 => props.ChildrenNamed("P").FirstOrDefault(p => p[0] as string == "CustomFrameRate")?.Properties.ElementAtOrDefault(4) is double d ? d : 30,
            _ => 30,
        };
    }

    /// <summary>
    /// Local matrix (row-vector, like the rest of SloppyAnimatsu) for translation, Euler XYZ rotation
    /// in degrees and scaling: S · Rx · Ry · Rz · T.
    /// </summary>
    public static Matrix4x4 LocalMatrix(Vector3 translation, Vector3 rotationDegrees, Vector3 scaling)
    {
        const float rad = MathF.PI / 180f;
        return Matrix4x4.CreateScale(scaling)
             * Matrix4x4.CreateRotationX(rotationDegrees.X * rad)
             * Matrix4x4.CreateRotationY(rotationDegrees.Y * rad)
             * Matrix4x4.CreateRotationZ(rotationDegrees.Z * rad)
             * Matrix4x4.CreateTranslation(translation);
    }
}

public sealed class FbxModel
{
    internal FbxModel(long id, FbxNode node)
    {
        Id = id;
        Node = node;
        Name = FbxScene.ShortName(node);
        Kind = node.PropertyCount > 2 ? node[2] as string ?? "" : "";
        Translation = FbxScene.Vector(node, "Lcl Translation") ?? Vector3.Zero;
        Rotation = FbxScene.Vector(node, "Lcl Rotation") ?? Vector3.Zero;
        Scaling = FbxScene.Vector(node, "Lcl Scaling") ?? Vector3.One;
        InheritType = FbxScene.P(node, "InheritType") is [var it, ..] ? Convert.ToInt32(it) : 0;
        string[] unsupported = ["PreRotation", "PostRotation", "RotationPivot", "ScalingPivot", "RotationOffset", "ScalingOffset", "GeometricTranslation", "GeometricRotation", "GeometricScaling"];
        HasUnsupportedTransform = unsupported.Any(p => FbxScene.Vector(node, p) is { } v && v != Vector3.Zero && !(p == "GeometricScaling" && v == Vector3.One))
                                  || FbxScene.P(node, "RotationOrder") is { Length: > 0 } o && Convert.ToInt32(o[0]) != 0;
    }

    public long Id { get; }
    public FbxNode Node { get; }
    public string Name { get; }

    /// <summary>"LimbNode", "Null", "Mesh", ...</summary>
    public string Kind { get; }

    public FbxModel? Parent { get; internal set; }
    public List<FbxModel> Children { get; } = [];

    /// <summary>Static local transform; rotation is Euler XYZ in degrees.</summary>
    public Vector3 Translation { get; }
    public Vector3 Rotation { get; }
    public Vector3 Scaling { get; }

    public bool HasUnsupportedTransform { get; }

    /// <summary>
    /// FBX transform inheritance: 0 = RrSs (plain matrix chain), 1 = RSrs, 2 = Rrs (the parent's
    /// own scale is not inherited, "segment scale compensate").
    /// </summary>
    public int InheritType { get; }

    public Matrix4x4 LocalMatrix => FbxScene.LocalMatrix(Translation, Rotation, Scaling);

    public IEnumerable<FbxModel> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));

    public override string ToString() => $"{Kind} '{Name}'";
}

public sealed class FbxAnimStack
{
    internal FbxAnimStack(long id, FbxNode node)
    {
        Id = id;
        Name = FbxScene.ShortName(node);
        Start = FbxScene.P(node, "LocalStart") is [long s, ..] ? s : 0;
        Stop = FbxScene.P(node, "LocalStop") is [long e, ..] ? e : 0;
    }

    public long Id { get; }
    public string Name { get; }
    public long Start { get; }
    public long Stop { get; }

    /// <summary>Layers in file order; FBX evaluates them first to last.</summary>
    public List<FbxAnimLayer> Layers { get; } = [];

    /// <summary>
    /// Local matrix of <paramref name="model"/> at <paramref name="ticks"/> from the first layer
    /// that animates it (enough for a baked, single-layer stack); static values elsewhere.
    /// </summary>
    public Matrix4x4 LocalMatrix(FbxModel model, long ticks)
    {
        var t = model.Translation;
        var r = model.Rotation;
        var s = model.Scaling;
        foreach (var layer in Layers)
            foreach (var cn in layer.CurveNodes)
            {
                if (cn.Model != model) continue;
                switch (cn.Property)
                {
                    case "Lcl Translation": t = cn.Evaluate(ticks); break;
                    case "Lcl Rotation": r = cn.Evaluate(ticks); break;
                    case "Lcl Scaling": s = cn.Evaluate(ticks); break;
                }
            }
        return FbxScene.LocalMatrix(t, r, s);
    }

    public override string ToString() => $"stack '{Name}' ({Layers.Count} layers)";
}

public sealed class FbxAnimLayer
{
    internal FbxAnimLayer(long id, FbxNode node)
    {
        Id = id;
        Name = FbxScene.ShortName(node);
        BlendMode = FbxScene.P(node, "BlendMode") is [var b, ..] ? Convert.ToInt32(b) : 0;
        Weight = FbxScene.P(node, "Weight") is [var w, ..] ? Convert.ToDouble(w) : 100;
        Mute = FbxScene.P(node, "Mute") is [var m, ..] && Convert.ToInt32(m) != 0;
    }

    public long Id { get; }
    public string Name { get; }

    /// <summary>FbxAnimLayer::EBlendMode: 0 additive, 1 override, 2 override-passthrough.</summary>
    public int BlendMode { get; }

    /// <summary>Percent (0-100).</summary>
    public double Weight { get; }
    public bool Mute { get; }

    public List<FbxCurveNode> CurveNodes { get; } = [];

    public override string ToString() => $"layer '{Name}' blend {BlendMode} weight {Weight}{(Mute ? " muted" : "")} ({CurveNodes.Count} curve nodes)";
}

public sealed class FbxCurveNode
{
    internal FbxCurveNode(long id, FbxNode node)
    {
        Id = id;
        Name = FbxScene.ShortName(node);
        Default = new Vector3(
            FbxScene.P(node, "d|X") is [var x, ..] ? (float)Convert.ToDouble(x) : 0,
            FbxScene.P(node, "d|Y") is [var y, ..] ? (float)Convert.ToDouble(y) : 0,
            FbxScene.P(node, "d|Z") is [var z, ..] ? (float)Convert.ToDouble(z) : 0);
    }

    public long Id { get; }
    public string Name { get; }
    public FbxAnimLayer? Layer { get; internal set; }
    public FbxModel? Model { get; internal set; }

    /// <summary>The animated model property ("Lcl Translation", "Lcl Rotation", "Lcl Scaling").</summary>
    public string? Property { get; internal set; }

    public Vector3 Default { get; }
    public FbxCurve?[] Curves { get; } = new FbxCurve?[3];

    public Vector3 Evaluate(long ticks) => new(
        Curves[0]?.Evaluate(ticks) ?? Default.X,
        Curves[1]?.Evaluate(ticks) ?? Default.Y,
        Curves[2]?.Evaluate(ticks) ?? Default.Z);

    public override string ToString() => $"{Property} of '{Model?.Name}'";
}

/// <summary>
/// One animation curve. <see cref="Evaluate"/> handles constant and linear keys exactly and
/// treats cubic keys as linear: enough to sample baked curves at their key times.
/// </summary>
public sealed class FbxCurve
{
    internal FbxCurve(long id, FbxNode node)
    {
        Id = id;
        Node = node;
        Default = node.Child("Default") is { PropertyCount: > 0 } d ? Convert.ToSingle(d[0]) : 0;
    }

    public long Id { get; }
    public FbxNode Node { get; }
    public float Default { get; }

    public long[] Times => _times ??= Node.Value<long[]>("KeyTime") ?? [];
    public float[] Values => _values ??= Node.Value<float[]>("KeyValueFloat") ?? Node.Value<double[]>("KeyValueDouble")?.Select(d => (float)d).ToArray() ?? [];
    private long[]? _times;
    private float[]? _values;

    /// <summary>Per-key attribute flags (FbxAnimCurveDef), expanded from the run-length KeyAttrRefCount.</summary>
    public int[] KeyFlags => _flags ??= Expand(Node.Value<int[]>("KeyAttrFlags") ?? [], 1).Select(a => a[0]).ToArray();
    private int[]? _flags;

    /// <summary>Per-key attribute data: right slope, next-left slope, packed weights, packed velocities.</summary>
    public float[][] KeyData => Expand(Node.Value<float[]>("KeyAttrDataFloat") ?? [], 4);

    private T[][] Expand<T>(T[] attrs, int stride)
    {
        var counts = Node.Value<int[]>("KeyAttrRefCount") ?? [];
        var result = new List<T[]>();
        for (int a = 0; a < counts.Length && (a + 1) * stride <= attrs.Length; a++)
            for (int k = 0; k < counts[a]; k++)
                result.Add(attrs[(a * stride)..((a + 1) * stride)]);
        return result.ToArray();
    }

    public float Evaluate(long ticks)
    {
        var times = Times;
        var values = Values;
        if (times.Length == 0) return Default;
        int i = Array.BinarySearch(times, ticks);
        if (i >= 0) return values[i];
        int next = ~i;
        if (next == 0) return values[0];
        if (next == times.Length) return values[^1];
        // Keys a fraction of a tick away from a frame time are still that key.
        if (ticks - times[next - 1] < 1000) return values[next - 1];
        if (times[next] - ticks < 1000) return values[next];
        const int constant = 0x2;
        if ((KeyFlags.ElementAtOrDefault(next - 1) & 0xE) == constant) return values[next - 1];
        double u = (ticks - times[next - 1]) / (double)(times[next] - times[next - 1]);
        return (float)(values[next - 1] + u * (values[next] - values[next - 1]));
    }
}
