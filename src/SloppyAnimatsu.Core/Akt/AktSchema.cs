namespace SloppyAnimatsu.Core.Akt;

/// <summary>A (name, type) member declaration from the class or struct schema.</summary>
public sealed record AktMember(string Name, string TypeName)
{
    public AktType Type => AktType.Parse(TypeName);
}

/// <summary>An object class: its own members only; inherited members live on <see cref="Parent"/>.</summary>
public sealed class AktClassDef
{
    public string Name { get; }
    public string Key { get; }
    public AktClassDef? Parent { get; internal set; }
    public List<AktMember> Members { get; } = new();
    public List<AktClassDef> Derived { get; } = new();

    public AktClassDef(string name)
    {
        Name = AktNames.Definition(name);
        Key = AktNames.Key(Name);
    }

    /// <summary>Classes from the root base down to this one.</summary>
    public IReadOnlyList<AktClassDef> Chain()
    {
        var chain = new List<AktClassDef>();
        for (var c = this; c != null; c = c.Parent)
            chain.Add(c);
        chain.Reverse();
        return chain;
    }

    public bool IsA(string classKey)
    {
        for (var c = this; c != null; c = c.Parent)
            if (c.Key == classKey) return true;
        return false;
    }

    public override string ToString() => Name;
}

/// <summary>A value struct from the struct schema.</summary>
public sealed class AktStructDef
{
    public string Name { get; }
    public string Key { get; }
    public List<AktMember> Members { get; } = new();

    public AktStructDef(string name)
    {
        Name = AktNames.Definition(name);
        Key = AktNames.Key(Name);
    }

    public override string ToString() => Name;
}

/// <summary>The class and struct schemas of one file.</summary>
public sealed class AktSchema
{
    /// <summary>Root classes (normally just CDataObject).</summary>
    public List<AktClassDef> RootClasses { get; } = new();
    public List<AktStructDef> Structs { get; } = new();

    private readonly Dictionary<string, AktClassDef> _classesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AktStructDef> _structsByKey = new(StringComparer.Ordinal);

    public IEnumerable<AktClassDef> AllClasses => _classesByKey.Values;

    public AktClassDef? FindClass(string name) =>
        _classesByKey.GetValueOrDefault(AktNames.Key(name));

    public AktStructDef? FindStruct(string name) =>
        _structsByKey.GetValueOrDefault(AktNames.Key(name));

    public void AddClass(AktClassDef def, AktClassDef? parent)
    {
        def.Parent = parent;
        (parent?.Derived ?? RootClasses).Add(def);
        _classesByKey[def.Key] = def;
    }

    public void AddStruct(AktStructDef def)
    {
        Structs.Add(def);
        _structsByKey[def.Key] = def;
    }

    /// <summary>
    /// A copy whose classes and structs can be extended (a feature adding schema entries) without
    /// touching this one. Members are immutable, so they are shared.
    /// </summary>
    public AktSchema Clone()
    {
        var copy = new AktSchema();
        foreach (var root in RootClasses) CloneClass(root, null);
        foreach (var s in Structs)
        {
            var def = new AktStructDef(s.Name);
            def.Members.AddRange(s.Members);
            copy.AddStruct(def);
        }
        return copy;

        void CloneClass(AktClassDef def, AktClassDef? parent)
        {
            var cloned = new AktClassDef(def.Name);
            cloned.Members.AddRange(def.Members);
            copy.AddClass(cloned, parent);
            foreach (var derived in def.Derived) CloneClass(derived, cloned);
        }
    }

    /// <summary>
    /// A new object of class <paramref name="className"/> with every member of its chain set to
    /// <see cref="DefaultValue"/>. The class must be in this schema.
    /// </summary>
    public AktObject NewObject(string className)
    {
        var def = FindClass(className) ?? throw new InvalidOperationException($"Class '{className}' is not in the schema.");
        var obj = new AktObject(def.Name);
        foreach (var c in def.Chain())
            foreach (var m in c.Members)
                obj.Fields.Add(new AktField(c.Name, m.Name, m.TypeName, DefaultValue(m.Type)));
        return obj;
    }

    /// <summary>A new struct value with every member set to <see cref="DefaultValue"/>.</summary>
    public AktStruct NewStruct(string structName)
    {
        var def = FindStruct(structName) ?? throw new InvalidOperationException($"Struct '{structName}' is not in the schema.");
        var s = new AktStruct(def.Name);
        AddDefaultMembers(s, def);
        return s;
    }

    /// <summary>
    /// A new polymorphic value (a <c>Key</c> or <c>TangentInfoList</c>) of the concrete struct
    /// <paramref name="concreteName"/>, holding the members of its whole chain.
    /// </summary>
    public AktStruct NewPolymorphic(string concreteName)
    {
        var concrete = FindStruct(concreteName) ?? throw new InvalidOperationException($"Struct '{concreteName}' is not in the schema.");
        var s = new AktStruct(concrete.Name);
        foreach (string key in AktNames.PolymorphicChain(concrete.Key))
            if (FindStruct(key) is { } def)
                AddDefaultMembers(s, def);
        return s;
    }

    private void AddDefaultMembers(AktStruct s, AktStructDef def)
    {
        foreach (var m in def.Members)
            s.Fields.Add(new AktField(def.Name, m.Name, m.TypeName, DefaultValue(m.Type)));
    }

    /// <summary>
    /// The zero value of a member type, in the representation the reader produces: numbers 0,
    /// float groups all 0, empty strings and lists, null references and polymorphic values, and
    /// structs with zero members. Callers set the meaningful defaults (identity rotations etc.).
    /// </summary>
    public object? DefaultValue(AktType t) => t.Kind switch
    {
        AktTypeKind.Bool => false,
        AktTypeKind.UChar => (byte)0,
        AktTypeKind.Char => (sbyte)0,
        AktTypeKind.UShort => (ushort)0,
        AktTypeKind.Short => (short)0,
        AktTypeKind.UInt or AktTypeKind.ULong => 0u,
        AktTypeKind.Int or AktTypeKind.Long => 0,
        AktTypeKind.Float => 0f,
        AktTypeKind.Double => 0d,
        AktTypeKind.FloatGroup => new float[t.Count],
        AktTypeKind.RGBAi => new byte[4],
        AktTypeKind.Atom => null,
        AktTypeKind.WString => "",
        AktTypeKind.Reference => new AktReference(null),
        AktTypeKind.RefArray => new List<AktReference>(),
        AktTypeKind.Vector => t.Element!.Kind == AktTypeKind.UChar ? Array.Empty<byte>() : new List<object?>(),
        AktTypeKind.Tuple => Enumerable.Range(0, t.Count).Select(_ => DefaultValue(t.Element!)).ToArray(),
        AktTypeKind.Map => new AktMap(),
        AktTypeKind.Struct => NewStruct(t.Target!),
        AktTypeKind.Polymorphic => null,
        _ => throw new NotSupportedException($"No default value for member type '{t.Spelling}'."),
    };

    /// <summary>
    /// The structs a file lists for these classes and objects, sorted as written
    /// <see cref="Structs"/> acts as the catalog
    /// of definitions and may hold more than is needed.
    /// </summary>
    /// <param name="classes">The classes written to the class schema (instantiated plus ancestors).</param>
    /// <param name="objects">The objects written; their polymorphic values add their chains.</param>
    public List<AktStructDef> SelectStructs(IEnumerable<AktClassDef> classes, IEnumerable<AktObject> objects)
    {
        var selected = new Dictionary<string, AktStructDef>(StringComparer.Ordinal);

        void AddStruct(string key)
        {
            if (selected.ContainsKey(key)) return;
            // The smart-pointer wrapper behind Key/TangentInfoList is always an empty struct, so a
            // catalog may leave it out.
            var def = FindStruct(key)
                      ?? (key.StartsWith("CSmartPointer<", StringComparison.Ordinal)
                          ? new AktStructDef($"class CSmartPointer<class {key["CSmartPointer<".Length..^1]}>")
                          : throw new InvalidOperationException($"No definition for struct '{key}'."));
            selected[key] = def;
            foreach (var m in def.Members)
                VisitType(m.Type);
        }

        void VisitType(AktType t)
        {
            switch (t.Kind)
            {
                case AktTypeKind.Vector:
                case AktTypeKind.Tuple:
                    VisitType(t.Element!);
                    break;
                case AktTypeKind.Map:
                    VisitType(t.KeyElement!);
                    VisitType(t.Element!);
                    break;
                case AktTypeKind.Struct:
                    AddStruct(t.Target!);
                    break;
                case AktTypeKind.Polymorphic:
                    AddStruct($"CSmartPointer<{t.Target}>");
                    AddStruct(t.Target!);
                    break;
            }
        }

        // Polymorphic values actually present add their whole chain. Only values whose type can
        // hold one are walked.
        var concreteSeen = new HashSet<string>(StringComparer.Ordinal);
        var mayHold = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool MayHoldPolymorphic(AktType t)
        {
            if (mayHold.TryGetValue(t.Spelling, out bool known)) return known;
            mayHold[t.Spelling] = false; // guards against cycles
            bool result = t.Kind switch
            {
                AktTypeKind.Polymorphic => true,
                AktTypeKind.Vector or AktTypeKind.Tuple => MayHoldPolymorphic(t.Element!),
                AktTypeKind.Map => MayHoldPolymorphic(t.KeyElement!) || MayHoldPolymorphic(t.Element!),
                AktTypeKind.Struct => FindStruct(t.Target!)?.Members.Any(m => MayHoldPolymorphic(m.Type)) ?? false,
                _ => false,
            };
            return mayHold[t.Spelling] = result;
        }

        void VisitValue(AktType t, object? v)
        {
            if (v == null || !MayHoldPolymorphic(t)) return;
            switch (t.Kind)
            {
                case AktTypeKind.Vector:
                    foreach (var e in (List<object?>)v) VisitValue(t.Element!, e);
                    break;
                case AktTypeKind.Tuple:
                    foreach (var e in (object?[])v) VisitValue(t.Element!, e);
                    break;
                case AktTypeKind.Map:
                    foreach (var e in ((AktMap)v).Entries)
                    {
                        VisitValue(t.KeyElement!, e.Key);
                        VisitValue(t.Element!, e.Value);
                    }
                    break;
                case AktTypeKind.Struct:
                case AktTypeKind.Polymorphic:
                {
                    var s = (AktStruct)v;
                    if (t.Kind == AktTypeKind.Polymorphic && concreteSeen.Add(s.TypeName))
                        foreach (string key in AktNames.PolymorphicChain(AktNames.Key(s.TypeName)))
                            AddStruct(key);
                    for (int i = 0; i < s.FieldCount; i++)
                        VisitValue(s.MemberAt(i).Type, s.ValueAt(i));
                    break;
                }
            }
        }

        foreach (var c in classes)
            foreach (var m in c.Members)
                VisitType(m.Type);
        foreach (var o in objects)
            for (int i = 0; i < o.FieldCount; i++)
                VisitValue(o.MemberAt(i).Type, o.ValueAt(i));

        return selected.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }
}
