using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>Any <c>CDataObject</c>: a named node of the scene tree.</summary>
public class SceneObject
{
    public SceneModel Model { get; }
    public AktObject Source { get; }

    protected internal SceneObject(SceneModel model, AktObject source)
    {
        Model = model;
        Source = source;
    }

    /// <summary>Class name without qualifiers, e.g. <c>CJoint</c>.</summary>
    public string ClassName => Source.ClassKey;

    public string? Name
    {
        get => Source.Name;
        set => Source.Set("Name", value);
    }

    /// <summary>Internal containers (<c>__Anims</c>, <c>__RigAnim</c> ...) are named with a <c>__</c> prefix.</summary>
    public bool IsInternal => Name?.StartsWith("__", StringComparison.Ordinal) == true;

    public SceneObject? Parent => Source.Parent is { } p ? Model.Wrap(p) : null;

    public IEnumerable<SceneObject> Children => Source.Children.Select(Model.Wrap);

    /// <summary>Descendants of type <typeparamref name="T"/> in file (pre-)order, excluding this object.</summary>
    public IEnumerable<T> Descendants<T>() where T : SceneObject =>
        Source.PreOrder().Skip(1).Select(Model.Wrap).OfType<T>();

    /// <summary>The closest ancestor of type <typeparamref name="T"/>.</summary>
    public T? Ancestor<T>() where T : SceneObject
    {
        for (var o = Parent; o != null; o = o.Parent)
            if (o is T match) return match;
        return null;
    }

    public SceneObject? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    // ---- field helpers for subclasses ------------------------------------------------------

    protected T? Ref<T>(string field) where T : SceneObject =>
        Model.Wrap(Source[field] as AktReference) as T;

    protected void SetRef(string field, SceneObject? target) =>
        Source.Set(field, new AktReference(target?.Source));

    /// <summary>
    /// A reference array as typed wrappers. Positions matter (e.g. pose matrices are indexed by
    /// <c>Controllers</c>), so a null or mistyped entry is an error rather than being skipped.
    /// </summary>
    protected IReadOnlyList<T> Refs<T>(string field) where T : SceneObject
    {
        if (Source[field] is not List<AktReference> refs)
            return [];
        var result = new T[refs.Count];
        for (int i = 0; i < refs.Count; i++)
            result[i] = Model.Wrap(refs[i]) as T
                        ?? throw new InvalidDataException($"{this}.{field}[{i}] is {refs[i]}, expected a {typeof(T).Name}.");
        return result;
    }

    /// <summary>
    /// Whether the reference array <paramref name="field"/> still names exactly the objects of
    /// <paramref name="list"/>, in order, without building the wrapper array <see cref="Refs{T}"/>
    /// would. Hot per-frame checks use it.
    /// </summary>
    protected bool RefsMatch<T>(string field, IReadOnlyList<T> list) where T : SceneObject
    {
        if (Source[field] is not List<AktReference> refs) return list.Count == 0;
        if (refs.Count != list.Count) return false;
        for (int i = 0; i < refs.Count; i++)
            if (!ReferenceEquals(refs[i].Target, list[i].Source)) return false;
        return true;
    }

    protected bool Flag(string field) => SceneValues.Flag(Source, field);

    protected uint[] UInts(string field) => SceneValues.UInts(Source, field);

    public override string ToString() => $"{ClassName} '{Name}'";
}

/// <summary>Value decoding shared by objects and struct values.</summary>
internal static class SceneValues
{
    /// <summary>Bools may hold a raw byte other than 0/1; any non-zero is true.</summary>
    public static bool Flag(AktFieldContainer c, string field) => c[field] switch
    {
        bool b => b,
        byte raw => raw != 0,
        _ => false,
    };

    public static uint[] UInts(AktFieldContainer c, string field) =>
        c[field] is List<object?> list ? list.Select(v => Convert.ToUInt32(v)).ToArray() : [];

    public static List<object?> Items(AktFieldContainer c, string field) =>
        c[field] as List<object?> ?? [];
}
