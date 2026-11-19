using System.Globalization;
using System.Numerics;

namespace SloppyAnimatsu.Core.Fbx;

/// <summary>How strictly <see cref="FbxCompare"/> compares two files.</summary>
public sealed class FbxCompareOptions
{
    /// <summary>Key times may differ by this many frames (open.ak's frame-to-tick conversion rounds).</summary>
    public double TimeToleranceFrames { get; init; } = 0.01;

    /// <summary>Largest accepted difference of a scalar, a vector component or a matrix element.</summary>
    public float ValueTolerance { get; init; } = 1e-3f;

    /// <summary>Largest accepted angle between two rotations, in degrees.</summary>
    public float RotationToleranceDegrees { get; init; } = 0.1f;

    /// <summary>How many differences to list (all are counted).</summary>
    public int MaxReported { get; init; } = 40;
}

/// <summary>The result of comparing two FBX files.</summary>
public sealed class FbxComparison
{
    private readonly List<string> _differences = [];
    private int _differenceCount;

    public FbxCompareOptions Options { get; init; } = new();

    /// <summary>Description of the first <see cref="FbxCompareOptions.MaxReported"/> differences.</summary>
    public IReadOnlyList<string> Differences => _differences;

    /// <summary>Total number of differences found, including those not listed.</summary>
    public int DifferenceCount => _differenceCount;

    public bool IsMatch => _differenceCount == 0;

    public List<string> Checked { get; } = [];

    internal void Differ(string message)
    {
        _differenceCount++;
        if (_differences.Count < Options.MaxReported) _differences.Add(message);
    }

    internal void Add(string message) => Differ(message);

    public override string ToString()
    {
        var lines = new List<string>(Checked.Select(c => "  = " + c));
        lines.AddRange(Differences.Select(d => "  ! " + d));
        if (_differenceCount > Differences.Count)
            lines.Add($"  ... {_differenceCount - Differences.Count} more");
        lines.Add(IsMatch ? "match" : $"{_differenceCount} difference(s)");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Compares an FBX file SloppyAnimatsu wrote with a reference FBX.
/// Names, structure and values are compared; ids, creation time
/// and file layout are not. Geometry, skins, bind poses, models and animation curves are checked
/// in detail; materials and textures by name and count.
/// </summary>
public static class FbxCompare
{
    private const long TicksPerSecond = FbxWriter.TicksPerSecond;

    public static FbxComparison Compare(FbxDocument actual, FbxDocument expected, FbxCompareOptions? options = null)
    {
        var comparison = new FbxComparison { Options = options ?? new FbxCompareOptions() };
        var a = new Side(actual);
        var e = new Side(expected);

        comparison.Checked.Add($"{e.Models.Count} models, {e.Geometries.Count} geometries, {e.Stacks.Count} animation stacks");
        comparison.Checked.Add($"{a.ParentLinkCount} / {e.ParentLinkCount} parent links");
        CompareModels(comparison, a, e);
        CompareGeometries(comparison, a, e);
        CompareSkins(comparison, a, e);
        CompareAnimation(comparison, a, e);
        CompareMaterials(comparison, a, e);
        return comparison;
    }

    public static FbxComparison CompareFiles(string actualPath, string expectedPath, FbxCompareOptions? options = null) =>
        Compare(FbxDocument.Load(actualPath), FbxDocument.Load(expectedPath), options);

    private static void CompareModels(FbxComparison c, Side a, Side e)
    {
        var actual = a.ModelsByPath();
        var expected = e.ModelsByPath();
        foreach (var name in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            var en = expected.GetValueOrDefault(name) ?? [];
            var an = actual.GetValueOrDefault(name) ?? [];
            if (en.Count != an.Count)
            {
                c.Differ($"model '{name}': {an.Count} in the export, {en.Count} in the reference");
                continue;
            }
            for (int i = 0; i < en.Count; i++)
            {
                var em = en[i];
                var am = an[i];
                if (am.Kind != em.Kind) c.Differ($"model '{name}': kind '{am.Kind}' != '{em.Kind}'");
                CompareVector(c, $"model '{name}' translation", FbxScene.Vector(em.Node, "Lcl Translation") ?? Vector3.Zero,
                    FbxScene.Vector(am.Node, "Lcl Translation") ?? Vector3.Zero);
                // Rotations are compared as orientations: the app may write the same rotation as
                // (82, 0, 180) or (82, 0, -180), which are the same matrix.
                var er = FbxScene.Vector(em.Node, "Lcl Rotation") ?? Vector3.Zero;
                var ar = FbxScene.Vector(am.Node, "Lcl Rotation") ?? Vector3.Zero;
                if (Angle(ar, er) > c.Options.RotationToleranceDegrees)
                    c.Differ($"model '{name}' rotation: ({ar.X:0.####}, {ar.Y:0.####}, {ar.Z:0.####}) != ({er.X:0.####}, {er.Y:0.####}, {er.Z:0.####})");
                CompareVector(c, $"model '{name}' scaling", FbxScene.Vector(em.Node, "Lcl Scaling") ?? Vector3.One,
                    FbxScene.Vector(am.Node, "Lcl Scaling") ?? Vector3.One);
                if (am.InheritType != em.InheritType)
                    c.Differ($"model '{name}': InheritType {am.InheritType} != {em.InheritType}");
                if (a.ParentName(am) != e.ParentName(em))
                    c.Differ($"model '{name}': parent '{a.ParentName(am)}' != '{e.ParentName(em)}'");
            }
        }
    }

    private static readonly string[] Arrays = ["Vertices", "PolygonVertexIndex"];

    private static void CompareGeometries(FbxComparison c, Side a, Side e)
    {
        var actual = a.GeometryByMesh();
        var expected = e.GeometryByMesh();
        foreach (var name in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(name, out var an)) { c.Differ($"geometry '{name}' is missing"); continue; }
            if (!expected.TryGetValue(name, out var en)) { c.Differ($"geometry '{name}' is extra"); continue; }
            foreach (var array in Arrays)
            {
                var ev = en.Child(array)?.Properties.ElementAtOrDefault(0) as Array;
                var av = an.Child(array)?.Properties.ElementAtOrDefault(0) as Array;
                CompareArray(c, $"geometry '{name}' {array}", av, ev);
            }
            var expectedLayers = en.ChildrenNamed("Layer").SelectMany(l => l.ChildrenNamed("LayerElement"))
                .Select(le => le.Value<string>("Type") ?? "").ToList();
            var actualLayers = an.ChildrenNamed("Layer").SelectMany(l => l.ChildrenNamed("LayerElement"))
                .Select(le => le.Value<string>("Type") ?? "").ToList();
            if (!expectedLayers.SequenceEqual(actualLayers))
                c.Differ($"geometry '{name}' layer elements [{string.Join(", ", actualLayers)}] != [{string.Join(", ", expectedLayers)}]");
            CompareAttributeLayers(c, name, an, en, "LayerElementNormal", "Normals", "NormalsIndex", 3, "normal");
            CompareAttributeLayers(c, name, an, en, "LayerElementColor", "Colors", "ColorIndex", 4, "colour");
            CompareAttributeLayers(c, name, an, en, "LayerElementUV", "UV", "UVIndex", 2, "UV set");
            var expectedUvSets = en.ChildrenNamed("LayerElementUV").ToList();
            var actualUvSets = an.ChildrenNamed("LayerElementUV").ToList();
            for (int i = 0; i < Math.Min(expectedUvSets.Count, actualUvSets.Count); i++)
            {
                string expectedUvName = expectedUvSets[i].Value<string>("Name") ?? "";
                string actualUvName = actualUvSets[i].Value<string>("Name") ?? "";
                if (expectedUvName != actualUvName)
                    c.Differ($"geometry '{name}' UV set {i}: '{actualUvName}' != '{expectedUvName}'");
            }
            CompareArray(c, $"geometry '{name}' material slots",
                an.Child("LayerElementMaterial")?.Child("Materials")?.Properties.ElementAtOrDefault(0) as Array,
                en.Child("LayerElementMaterial")?.Child("Materials")?.Properties.ElementAtOrDefault(0) as Array);
        }
    }

    /// <summary>
    /// Compares every layer element of one attribute by the value each corner resolves to: a
    /// writer is free to order and deduplicate its value array differently (the app compacts by
    /// first use), which is what the raw arrays of the two files differ in.
    /// </summary>
    private static void CompareAttributeLayers(FbxComparison c, string geometry, FbxNode actual, FbxNode expected,
        string elementName, string valueName, string indexName, int components, string label)
    {
        var actualSets = actual.ChildrenNamed(elementName).ToList();
        var expectedSets = expected.ChildrenNamed(elementName).ToList();
        if (actualSets.Count != expectedSets.Count)
            c.Differ($"geometry '{geometry}': {actualSets.Count} {label} layers != {expectedSets.Count}");
        for (int i = 0; i < Math.Min(actualSets.Count, expectedSets.Count); i++)
            CompareArray(c, $"geometry '{geometry}' {label} layer {i}",
                ResolveAttribute(actualSets[i], valueName, indexName, components),
                ResolveAttribute(expectedSets[i], valueName, indexName, components));
    }

    /// <summary>One attribute tuple per corner from a layer element (its value array is deduplicated).</summary>
    private static double[] ResolveAttribute(FbxNode element, string valueName, string indexName, int components)
    {
        var values = element.Child(valueName)?.Properties.ElementAtOrDefault(0) as double[];
        var indices = element.Child(indexName)?.Properties.ElementAtOrDefault(0) as int[];
        if (values == null) return [];
        if (indices == null) return values;
        var resolved = new double[indices.Length * components];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            if (index < 0 || index * components + components > values.Length) continue;
            for (int part = 0; part < components; part++)
                resolved[i * components + part] = values[index * components + part];
        }
        return resolved;
    }

    private static void CompareSkins(FbxComparison c, Side a, Side e)
    {
        var actual = a.Skins();
        var expected = e.Skins();
        foreach (var mesh in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(mesh, out var askin)) { c.Differ($"skin of '{mesh}' is missing"); continue; }
            if (!expected.TryGetValue(mesh, out var eskin)) { c.Differ($"skin of '{mesh}' is extra"); continue; }
            if (askin.Clusters.Count != eskin.Clusters.Count)
                c.Differ($"skin of '{mesh}': {askin.Clusters.Count} clusters != {eskin.Clusters.Count}");
            var actualClusters = askin.Clusters.ToDictionary(x => x.Joint);
            var expectedClusters = eskin.Clusters.ToDictionary(x => x.Joint);
            foreach (var joint in expectedClusters.Keys.Union(actualClusters.Keys).Order(StringComparer.Ordinal))
            {
                if (!actualClusters.TryGetValue(joint, out var ac)) { c.Differ($"skin of '{mesh}': cluster '{joint}' is missing"); continue; }
                if (!expectedClusters.TryGetValue(joint, out var ec)) { c.Differ($"skin of '{mesh}': cluster '{joint}' is extra"); continue; }
                CompareArray(c, $"skin of '{mesh}' cluster '{joint}' indexes", ac.Indexes, ec.Indexes);
                CompareArray(c, $"skin of '{mesh}' cluster '{joint}' weights", ac.Weights, ec.Weights);
                CompareMatrix(c, $"skin of '{mesh}' cluster '{joint}' Transform * TransformLink", ec.Transform * ec.TransformLink, ac.Transform * ac.TransformLink);
                CompareMatrix(c, $"skin of '{mesh}' cluster '{joint}' TransformLink", ec.TransformLink, ac.TransformLink);
            }
            ComparePoses(c, a.Poses(), e.Poses());
        }
    }

    /// <summary>Bind poses are unconnected in the reference files, so they are paired by their file order.</summary>
    private static void ComparePoses(FbxComparison c, List<Dictionary<string, Matrix4x4>> actual, List<Dictionary<string, Matrix4x4>> expected)
    {
        if (actual.Count != expected.Count)
            c.Differ($"{actual.Count} bind poses != {expected.Count}");
        for (int i = 0; i < Math.Min(actual.Count, expected.Count); i++)
        {
            var ap = actual[i];
            var ep = expected[i];
            if (ap.Count != ep.Count) c.Differ($"bind pose {i + 1}: {ap.Count} nodes != {ep.Count}");
            foreach (var joint in ep.Keys.Union(ap.Keys).Order(StringComparer.Ordinal))
            {
                if (!ap.TryGetValue(joint, out var am)) { c.Differ($"bind pose {i + 1}: node '{joint}' is missing"); continue; }
                if (!ep.TryGetValue(joint, out var em)) { c.Differ($"bind pose {i + 1}: node '{joint}' is extra"); continue; }
                CompareMatrix(c, $"bind pose {i + 1} '{joint}'", em, am);
            }
        }
    }

    private static void CompareAnimation(FbxComparison c, Side a, Side e)
    {
        var actual = a.Stacks;
        var expected = e.Stacks;
        if (actual.Count != expected.Count)
            c.Differ($"{actual.Count} animation stacks != {expected.Count}");
        for (int i = 0; i < Math.Min(actual.Count, expected.Count); i++)
        {
            var as_ = actual[i];
            var es = expected[i];
            if (as_.Name != es.Name)
            {
                c.Differ($"animation {i}: '{as_.Name}' != '{es.Name}'");
                continue;
            }
            double actualStop = as_.Stop / (double)TicksPerSecond * a.FrameRate;
            double expectedStop = es.Stop / (double)TicksPerSecond * e.FrameRate;
            if (Math.Abs(actualStop - expectedStop) > c.Options.TimeToleranceFrames)
                c.Differ($"animation '{es.Name}': stop {actualStop:0.####} != {expectedStop:0.####}");
            if (as_.Layers.Count != es.Layers.Count)
                c.Differ($"animation '{es.Name}': {as_.Layers.Count} layers != {es.Layers.Count}");
            for (int l = 0; l < Math.Min(as_.Layers.Count, es.Layers.Count); l++)
            {
                var al = as_.Layers[l];
                var el = es.Layers[l];
                if (al.Name != el.Name) c.Differ($"animation '{es.Name}' layer {l}: '{al.Name}' != '{el.Name}'");
                if (al.BlendMode != el.BlendMode)
                    c.Differ($"animation '{es.Name}' layer '{el.Name}': blend mode {al.BlendMode} != {el.BlendMode}");
                if (al.CurveNodes.Count != el.CurveNodes.Count)
                    c.Differ($"animation '{es.Name}' layer '{el.Name}': {al.CurveNodes.Count} curve nodes != {el.CurveNodes.Count}");
                var expectedCurves = el.CurveNodes.ToDictionary(x => (x.Model?.Name ?? "?", x.Property));
                foreach (var ac in al.CurveNodes)
                {
                    if (!expectedCurves.TryGetValue((ac.Model?.Name ?? "?", ac.Property), out var ec))
                    {
                        c.Differ($"animation '{es.Name}' layer '{el.Name}': curve node '{ac.Model}' {ac.Property} is extra");
                        continue;
                    }
                    if (ec.Property == FbxExporter.LclRotation)
                        CompareRotation(c, $"animation '{es.Name}' layer '{el.Name}' '{ec.Model}'", ac, ec, a.FrameRate, e.FrameRate);
                    else
                        for (int axis = 0; axis < 3; axis++)
                        {
                            var av = ac.Curves[axis];
                            var ev = ec.Curves[axis];
                            if (av is null && ev is null) continue;
                            if (av is null || ev is null) { c.Differ($"animation '{es.Name}' '{ec.Model}' {ec.Property} axis {axis} is missing"); continue; }
                            CompareCurve(c, $"animation '{es.Name}' layer '{el.Name}' '{ec.Model}' {ec.Property} {"XYZ"[axis]}", av, ev, a.FrameRate, e.FrameRate);
                        }
                }
            }
        }
    }

    /// <summary>
    /// Compares the three Euler channels of a rotation as rotations: the same orientation can be
    /// written with a 360° turn or with another XYZ solution, so the angle between the two
    /// quaternions is compared rather than the channel values.
    /// </summary>
    private static void CompareRotation(FbxComparison c, string label, FbxCurveNode actual, FbxCurveNode expected, double actualRate, double expectedRate)
    {
        var axes = Enumerable.Range(0, 3).ToArray();
        var actualCurves = axes.Select(i => actual.Curves[i]).ToArray();
        var expectedCurves = axes.Select(i => expected.Curves[i]).ToArray();
        if (actualCurves.Any(x => x is null) || expectedCurves.Any(x => x is null)) return;
        var reference = expectedCurves[0]!;
        for (int i = 1; i < 3; i++)
        {
            if (actualCurves[i]!.Times.Length != actualCurves[0]!.Times.Length || expectedCurves[i]!.Times.Length != reference.Times.Length)
            {
                c.Differ($"{label} rotation: key counts differ");
                return;
            }
        }
        CompareTimes(c, label + " rotation", actualCurves[0]!, reference, actualRate, expectedRate);
        float maxAngle = 0;
        int keys = Math.Min(actualCurves[0]!.Values.Length, reference.Values.Length);
        for (int i = 0; i < keys; i++)
        {
            var a = new Vector3(actualCurves[0]!.Values[i], actualCurves[1]!.Values[i], actualCurves[2]!.Values[i]);
            var e = new Vector3(reference.Values[i], expectedCurves[1]!.Values[i], expectedCurves[2]!.Values[i]);
            maxAngle = Math.Max(maxAngle, Angle(a, e));
        }
        if (maxAngle > c.Options.RotationToleranceDegrees)
            c.Differ($"{label} rotation: keys differ by {maxAngle:0.####}°");
    }

    /// <summary>The angle in degrees between two Euler XYZ rotations (row-vector <c>Rx · Ry · Rz</c>).</summary>
    private static float Angle(Vector3 a, Vector3 e)
    {
        var qa = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(EulerMatrix(a)));
        var qe = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(EulerMatrix(e)));
        float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(qa, qe)));
        return 2f * MathF.Acos(dot) * 180f / MathF.PI;
    }

    private static Matrix4x4 EulerMatrix(Vector3 degrees)
    {
        const float rad = MathF.PI / 180f;
        return Matrix4x4.CreateRotationX(degrees.X * rad)
             * Matrix4x4.CreateRotationY(degrees.Y * rad)
             * Matrix4x4.CreateRotationZ(degrees.Z * rad);
    }

    private static void CompareTimes(FbxComparison c, string label, FbxCurve actual, FbxCurve expected, double actualRate, double expectedRate)
    {
        var at = actual.Times;
        var et = expected.Times;
        if (at.Length != et.Length)
        {
            c.Differ($"{label}: {at.Length} keys != {et.Length}");
            return;
        }
        double maxTime = 0;
        for (int i = 0; i < at.Length; i++)
        {
            double actualFrame = at[i] / (double)TicksPerSecond * actualRate;
            double expectedFrame = et[i] / (double)TicksPerSecond * expectedRate;
            maxTime = Math.Max(maxTime, Math.Abs(actualFrame - expectedFrame));
        }
        if (maxTime > c.Options.TimeToleranceFrames)
            c.Differ($"{label}: key times differ by {maxTime:0.####} frames");
    }

    private static void CompareCurve(FbxComparison c, string label, FbxCurve actual, FbxCurve expected, double actualRate, double expectedRate)
    {
        CompareTimes(c, label, actual, expected, actualRate, expectedRate);
        var av = actual.Values;
        var ev = expected.Values;
        float maxValue = 0;
        for (int i = 0; i < Math.Min(av.Length, ev.Length); i++)
            maxValue = Math.Max(maxValue, Math.Abs(av[i] - ev[i]));
        if (maxValue > c.Options.ValueTolerance)
            c.Differ($"{label}: key values differ by {maxValue:0.####}");
        CompareFlags(c, label, actual, expected);
    }

    private static void CompareFlags(FbxComparison c, string label, FbxCurve actual, FbxCurve expected)
    {
        var actualFlags = actual.KeyFlags;
        var expectedFlags = expected.KeyFlags;
        if (!actualFlags.SequenceEqual(expectedFlags))
        {
            int different = 0;
            for (int i = 0; i < Math.Min(actualFlags.Length, expectedFlags.Length); i++) if (actualFlags[i] != expectedFlags[i]) different++;
            c.Differ($"{label}: {different} of {expectedFlags.Length} key flags differ (e.g. 0x{actualFlags.FirstOrDefault():X4} != 0x{expectedFlags.FirstOrDefault():X4})");
        }
    }

    private static void CompareMaterials(FbxComparison c, Side a, Side e)
    {
        var actual = a.MaterialNames();
        var expected = e.MaterialNames();
        if (!actual.SequenceEqual(expected))
            c.Differ($"materials [{string.Join(", ", actual)}] != [{string.Join(", ", expected)}]");
        if (a.TextureCount != e.TextureCount)
            c.Differ($"{a.TextureCount} textures != {e.TextureCount}");
        var actualSizes = a.TextureContentSizes();
        var expectedSizes = e.TextureContentSizes();
        if (!actualSizes.SequenceEqual(expectedSizes))
            c.Differ($"texture content sizes [{string.Join(", ", actualSizes)}] != [{string.Join(", ", expectedSizes)}]");
    }

    private static void CompareVector(FbxComparison c, string label, Vector3 expected, Vector3 actual)
    {
        float difference = MathF.Max(MathF.Abs(expected.X - actual.X), MathF.Max(MathF.Abs(expected.Y - actual.Y), MathF.Abs(expected.Z - actual.Z)));
        if (difference > c.Options.ValueTolerance)
            c.Differ($"{label}: ({actual.X:0.####}, {actual.Y:0.####}, {actual.Z:0.####}) != ({expected.X:0.####}, {expected.Y:0.####}, {expected.Z:0.####})");
    }

    private static void CompareMatrix(FbxComparison c, string label, Matrix4x4 expected, Matrix4x4 actual)
    {
        float difference = MaxDifference(expected, actual);
        if (difference > c.Options.ValueTolerance)
            c.Differ($"{label}: matrices differ by {difference:0.####}");
    }

    private static void CompareArray(FbxComparison c, string label, Array? actual, Array? expected)
    {
        if (actual is null && expected is null) return;
        if (actual is null || expected is null) { c.Differ($"{label}: {(actual is null ? "missing" : "extra")}"); return; }
        if (actual.Length != expected.Length)
        {
            c.Differ($"{label}: {actual.Length} values != {expected.Length}");
            return;
        }
        double max = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            double x = Convert.ToDouble(actual.GetValue(i), CultureInfo.InvariantCulture);
            double y = Convert.ToDouble(expected.GetValue(i), CultureInfo.InvariantCulture);
            max = Math.Max(max, Math.Abs(x - y));
        }
        if (max > c.Options.ValueTolerance) c.Differ($"{label}: values differ by {max:0.####}");
    }

    public static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = SloppyAnimatsu.Core.Model.AktMath.ToArray(a);
        float[] y = SloppyAnimatsu.Core.Model.AktMath.ToArray(b);
        float max = 0;
        for (int i = 0; i < 16; i++) max = Math.Max(max, Math.Abs(x[i] - y[i]));
        return max;
    }

    private static double Distance(Matrix4x4 m) => 0;

    /// <summary>One file's objects, grouped for comparison.</summary>
    private sealed class Side
    {
        public FbxDocument Document { get; }
        public double FrameRate { get; }
        public List<FbxNode> Objects { get; }
        public List<FbxModel> Models { get; }
        public List<FbxNode> Geometries { get; }
        public List<FbxAnimStack> Stacks { get; }
        public int TextureCount { get; }
        public int ParentLinkCount => _parentOf.Values.Sum(v => v.Count);

        private readonly Dictionary<long, FbxNode> _byId = [];
        private readonly Dictionary<long, List<long>> _childrenOf = [];
        private readonly Dictionary<long, List<long>> _parentOf = [];

        public Side(FbxDocument document)
        {
            Document = document;
            var scene = new FbxScene(document);
            FrameRate = scene.FrameRate;
            Objects = document.Node("Objects")?.Children.ToList() ?? [];
            Models = scene.Models.OrderBy(m => IdOf(m.Node)).ToList();
            Geometries = Objects.Where(o => o.Name == "Geometry").ToList();
            Stacks = scene.Stacks.ToList();
            TextureCount = Objects.Count(o => o.Name == "Texture");
            foreach (var o in Objects)
                if (o.PropertyCount > 0 && o[0] is long id) _byId[id] = o;
            foreach (var connection in document.Node("Connections")?.ChildrenNamed("C") ?? [])
                if (connection[1] is long child && connection[2] is long parent)
                {
                    if (!_childrenOf.TryGetValue(parent, out var list)) _childrenOf[parent] = list = [];
                    list.Add(child);
                    if (!_parentOf.TryGetValue(child, out var parents)) _parentOf[child] = parents = [];
                    parents.Add(parent);
                }
        }

        private static long IdOf(FbxNode node) => node.PropertyCount > 0 && node[0] is long id ? id : 0;

        public Dictionary<string, List<FbxModel>> ModelsByPath() =>
            Models.GroupBy(m => (m.Parent?.Name ?? "(root)") + "/" + m.Name).ToDictionary(g => g.Key, g => g.ToList());

        public string ParentName(FbxModel model) =>
            _parentOf.TryGetValue(IdOf(model.Node), out var parents)
                ? parents.Select(id => _byId.GetValueOrDefault(id)).OfType<FbxNode>().FirstOrDefault(p => p.Name == "Model") is { } parent
                    ? FbxScene.ShortName(parent) : "(none)"
                : "(none)";

        public Dictionary<string, FbxNode> GeometryByMesh()
        {
            var result = new Dictionary<string, FbxNode>();
            foreach (var geometry in Geometries)
            {
                var mesh = ParentsOf(geometry, "Model").FirstOrDefault();
                string name = mesh is null ? FbxScene.ShortName(geometry) : FbxScene.ShortName(mesh);
                result.TryAdd(name, geometry);
            }
            return result;
        }

        public Dictionary<string, Skin> Skins()
        {
            var result = new Dictionary<string, Skin>();
            foreach (var skin in Objects.Where(o => o.Name == "Deformer" && o.PropertyCount > 2 && o[2] as string == "Skin"))
            {
                var geometry = ParentsOf(skin, "Geometry").FirstOrDefault();
                var mesh = geometry is null ? null : ParentsOf(geometry, "Model").FirstOrDefault();
                if (mesh is null) continue;
                var skinInfo = new Skin();
                foreach (var child in ChildrenOf(skin))
                {
                    if (child.Name != "Deformer" || child.PropertyCount <= 2 || child[2] as string != "Cluster") continue;
                    var jointNode = ChildrenOf(child).FirstOrDefault(x => x.Name == "Model");
                    skinInfo.Clusters.Add(new Cluster(
                        jointNode is null ? "?" : FbxScene.ShortName(jointNode),
                        child.Child("Indexes")?.Properties.ElementAtOrDefault(0) as int[] ?? [],
                        child.Child("Weights")?.Properties.ElementAtOrDefault(0) as double[] ?? [],
                        Matrix(child, "Transform"),
                        Matrix(child, "TransformLink")));
                }
                var pose = ChildrenOf(mesh).FirstOrDefault(x => x.Name == "Pose");
                if (pose is not null)
                    foreach (var poseNode in pose.ChildrenNamed("PoseNode"))
                    {
                        long nodeId = poseNode.Child("Node") is { PropertyCount: > 0 } n && n[0] is long id ? id : 0;
                        string joint = _byId.TryGetValue(nodeId, out var jointNode) ? FbxScene.ShortName(jointNode) : "?";
                        skinInfo.PoseNodes.Add((joint, Matrix(poseNode, "Matrix")));
                    }
                result.TryAdd(FbxScene.ShortName(mesh), skinInfo);
            }
            return result;
        }

        /// <summary>
        /// The bind poses in file order, each as its joint name → world matrix. The app leaves the
        /// poses unconnected, so they are paired by their order in <c>Objects</c> (one per mesh).
        /// </summary>
        public List<Dictionary<string, Matrix4x4>> Poses()
        {
            var result = new List<Dictionary<string, Matrix4x4>>();
            foreach (var pose in Objects.Where(o => o.Name == "Pose"))
            {
                var nodes = new Dictionary<string, Matrix4x4>();
                foreach (var poseNode in pose.ChildrenNamed("PoseNode"))
                {
                    long nodeId = poseNode.Child("Node") is { PropertyCount: > 0 } n && n[0] is long id ? id : 0;
                    string joint = _byId.TryGetValue(nodeId, out var jointNode) ? FbxScene.ShortName(jointNode) : "?";
                    nodes.TryAdd(joint, Matrix(poseNode, "Matrix"));
                }
                result.Add(nodes);
            }
            return result;
        }

        private IEnumerable<FbxNode> ChildrenOf(FbxNode node) =>
            _childrenOf.TryGetValue(IdOf(node), out var ids) ? ids.Select(id => _byId.GetValueOrDefault(id)).OfType<FbxNode>() : [];

        private IEnumerable<FbxNode> ParentsOf(FbxNode node, string name) =>
            _parentOf.TryGetValue(IdOf(node), out var parents)
                ? parents.Select(id => _byId.GetValueOrDefault(id)).OfType<FbxNode>().Where(p => p.Name == name)
                : [];

        private static Matrix4x4 Matrix(FbxNode node, string name) =>
            node.Child(name)?.Properties.ElementAtOrDefault(0) is double[] values && values.Length == 16
                ? new Matrix4x4(
                    (float)values[0], (float)values[1], (float)values[2], (float)values[3],
                    (float)values[4], (float)values[5], (float)values[6], (float)values[7],
                    (float)values[8], (float)values[9], (float)values[10], (float)values[11],
                    (float)values[12], (float)values[13], (float)values[14], (float)values[15])
                : Matrix4x4.Identity;

        public List<string> MaterialNames() =>
            Objects.Where(o => o.Name == "Material").Select(FbxScene.ShortName).ToList();

        public List<int> TextureContentSizes() =>
            Objects.Where(o => o.Name == "Video")
                .Select(v => v.Child("Content")?.Properties.ElementAtOrDefault(0) is byte[] b ? b.Length : 0)
                .ToList();
    }

    private sealed class Skin
    {
        public List<Cluster> Clusters { get; } = [];
        public List<(string Joint, Matrix4x4 Matrix)> PoseNodes { get; } = [];
    }

    private sealed record Cluster(string Joint, int[] Indexes, double[] Weights, Matrix4x4 Transform, Matrix4x4 TransformLink);
}











