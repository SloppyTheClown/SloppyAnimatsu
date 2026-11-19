using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The struct schema inclusion rule: the writer
/// selects structs from the schema's definitions rather than copying a file's list.
/// </summary>
public class AktStructSchemaTests
{
    /// <summary>
    /// Rebuilds each sample's schema from scratch, with the file's struct definitions mixed into a
    /// larger catalog in a different order, and checks the written file is unchanged.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestFiles.AllSamples), MemberType = typeof(TestFiles))]
    public void SchemaRebuiltFromCatalogWritesIdentically(string path)
    {
        // A SloppyAnimatsu-container scene (MYAKT) holds the .akt stream compressed: compare that stream.
        byte[] original = AktContainer.Unwrap(File.ReadAllBytes(path)).Payload;
        var read = AktReader.Read(original);

        var schema = new AktSchema();
        foreach (var root in read.Schema.RootClasses)
            CopyClass(schema, root, null);

        var catalog = read.Schema.Structs.Select(CopyStruct).ToList();
        catalog.AddRange(ExtraDefinitions().Where(d => read.Schema.FindStruct(d.Name) == null));
        catalog.Reverse();
        foreach (var def in catalog)
            schema.AddStruct(def);

        var doc = new AktDocument(schema, read.Roots) { Version = read.Version, HasHeader = read.HasHeader };
        byte[] written = AktWriter.Write(doc);
        Assert.Equal(original.Length, written.Length);
        Assert.True(original.AsSpan().SequenceEqual(written), $"{path}: first difference at 0x{original.AsSpan().CommonPrefixLength(written):X}");
    }

    [Fact]
    public void StaticStructsAreListedEvenWithoutValues()
    {
        var schema = BaseSchema();
        var doc = new AktDocument(schema, new[] { Track() });

        // vector<CPolygon> brings CPolygon even though the vector is empty; Key brings the
        // smart pointer and the base; TangentInfoList brings its base and CTangentInfo. No
        // concrete key is used, so none of the chains is listed.
        Assert.Equal(new[]
            {
                "class CKeyBase", "class CPolygon", "class CSmartPointer<class CKeyBase>",
                "class CSmartPointer<class CTangentInfoListBase>", "class CTangentInfo", "class CTangentInfoListBase",
            },
            Selected(doc));
    }

    [Fact]
    public void PolymorphicValuesBringTheirWholeChain()
    {
        var schema = BaseSchema();
        var track = Track();
        var key = new AktStruct("class CKeyReverseFoot");
        key.Fields.Add(new("class CKeyMRS", "Key", "CMRSKey", MrsKey()));
        key.Fields.Add(new("class CKeyIK", "RenderKey", "CMRSKey", MrsKey()));
        key.Fields.Add(new("class CKeyReverseFoot", "RollAngle", "float", 0f));
        track.Set("KeyList", new List<object?> { key, null });
        var doc = new AktDocument(schema, new[] { track });

        var selected = Selected(doc);
        Assert.Contains("class CKeyMRS", selected);
        Assert.Contains("class CKeyIK", selected);       // only reached through the chain
        Assert.Contains("class CKeyReverseFoot", selected);
        Assert.Contains("class CMRSKey", selected);      // a member of a chain struct
        Assert.DoesNotContain("class CKeyCamera", selected);
        Assert.DoesNotContain("class CTangentInfoListMRS", selected);
        Assert.Equal(selected.OrderBy(s => s, StringComparer.Ordinal), selected);

        // And the written file reads back with the same list.
        var reread = AktReader.Read(AktWriter.Write(doc));
        Assert.Equal(selected, reread.Schema.Structs.Select(s => s.Name));
    }

    [Fact]
    public void MissingDefinitionIsAnError()
    {
        var schema = BaseSchema();
        var track = Track();
        track.Set("KeyList", new List<object?> { new AktStruct("class CKeyBlendshape") });
        var doc = new AktDocument(schema, new[] { track });
        Assert.Throws<InvalidOperationException>(() => AktWriter.Write(doc));
    }

    /// <summary>
    /// Every synthesized class and struct definition carries the file spelling ("class CKeyIK"), not
    /// The bare name. The app resolves a definition by that exact name and silently drops the whole
    /// scene when it is bare; this guards the AutoRigger IK/reverse-foot refusal.
    /// </summary>
    [Fact]
    public void SynthesizedDefinitionsCarryTheClassPrefix()
    {
        // A schema shaped like a scene with animation but no controller classes yet: CObject3D and a
        // camera to derive from, CAnimTrackMRS and the MRS key structs, so every synthesizer adds its
        // whole set. The object class is named bare on purpose: the schema normalizes the spelling.
        var schema = BaseSchema();
        var objectClass = new AktClassDef("CObject3D");
        objectClass.Members.Add(new("Constraint", "Reference<class CController3D>"));
        schema.AddClass(objectClass, schema.FindClass("CDataObject"));
        schema.AddClass(new AktClassDef("CCamera"), objectClass);
        schema.AddClass(new AktClassDef("CAnimTrackMRS"), schema.FindClass("CAnimTrackBase"));

        Assert.True(ControllerSchema.Ensure(schema, reverseFoot: true));
        CameraSchema.Ensure(schema);
        BlendshapeSchema.Ensure(schema);
        Assert.True(RetargetingMatchingSchema.Ensure(schema));

        foreach (var c in schema.AllClasses)
            Assert.StartsWith("class ", c.Name);
        foreach (var s in schema.Structs)
            Assert.StartsWith("class ", s.Name);

        // The controller chain has to be found by name.
        Assert.Equal("class CControllerIK", schema.FindClass(ControllerSchema.IkClass)!.Name);
        Assert.Equal("class CController3D_IK_RF", schema.FindClass(ControllerSchema.IkBaseClass)!.Name);
        Assert.Equal("class CControllerReverseFoot", schema.FindClass(ControllerSchema.ReverseFootClass)!.Name);
        Assert.Equal("class CReverseFootLocator_VK", schema.FindClass(ControllerSchema.LocatorClass)!.Name);
        Assert.Equal("class CKeyIK", schema.FindStruct(ControllerSchema.IkKeyStruct)!.Name);
        Assert.Equal("class CKeyReverseFoot", schema.FindStruct(ControllerSchema.ReverseFootKeyStruct)!.Name);
        Assert.Equal("class IKBaseRefInfo", schema.FindStruct(ControllerSchema.RefInfoStruct)!.Name);
        Assert.Equal("class CAnimTrackIK_VK", schema.FindClass(ControllerSchema.IkTrackClass)!.Name);
        Assert.Equal("class CKeyCamera", schema.FindStruct(CameraSchema.CameraKeyStruct)!.Name);
        Assert.Equal("class CKeyBlendshape", schema.FindStruct(BlendshapeSchema.KeyStruct)!.Name);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static List<string> Selected(AktDocument doc)
    {
        var classes = new HashSet<AktClassDef>();
        foreach (var o in doc.AllObjects())
            for (var c = doc.Schema.FindClass(o.TypeName); c != null && classes.Add(c); c = c.Parent) { }
        return doc.Schema.SelectStructs(classes, doc.AllObjects()).Select(s => s.Name).ToList();
    }

    private static void CopyClass(AktSchema schema, AktClassDef source, AktClassDef? parent)
    {
        var copy = new AktClassDef(source.Name);
        copy.Members.AddRange(source.Members);
        schema.AddClass(copy, parent);
        foreach (var d in source.Derived)
            CopyClass(schema, d, copy);
    }

    private static AktStructDef CopyStruct(AktStructDef source)
    {
        var copy = new AktStructDef(source.Name);
        copy.Members.AddRange(source.Members);
        return copy;
    }

    private static AktStructDef Def(string name, params (string Name, string Type)[] members)
    {
        var def = new AktStructDef(name);
        foreach (var (n, t) in members)
            def.Members.Add(new(n, t));
        return def;
    }

    /// <summary>Definitions no sample needs, plus the full key and tangent chains.</summary>
    private static IEnumerable<AktStructDef> ExtraDefinitions() => new[]
    {
        Def("class CUnusedStruct", ("Value", "float")),
        Def("class CKeyBase"),
        Def("class CKeyMRS", ("Key", "CMRSKey")),
        Def("class CKeyIK", ("RenderKey", "CMRSKey")),
        Def("class CKeyReverseFoot", ("RollAngle", "float")),
        Def("class CKeyCamera", ("Fov", "float")),
        Def("class CKeyBlendshape", ("Weight", "float")),
        Def("class CMRSKey", ("TranslationX", "float"), ("Rotation", "Quatf")),
        Def("class CTangentInfo", ("TangentMode", "uint")),
        Def("class CTangentInfoListBase", ("TangentInfoChannels", "vector<CTangentInfo>")),
        Def("class CTangentInfoListMRS"),
        Def("class CTangentInfoListCamera"),
        Def("class CPolygon", ("IndexArray", "vector<uint>"), ("MatIndex", "uint")),
    };

    private static AktSchema BaseSchema()
    {
        var schema = new AktSchema();
        var dataObject = new AktClassDef("class CDataObject");
        dataObject.Members.Add(new("Name", "CAtom"));
        schema.AddClass(dataObject, null);
        var track = new AktClassDef("class CAnimTrackBase");
        track.Members.Add(new("KeyList", "vector<Key>"));
        track.Members.Add(new("TangentInfoArray", "vector<TangentInfoList>"));
        track.Members.Add(new("Polygons", "vector<CPolygon>"));
        track.Members.Add(new("Target", "Reference<class CObject3D>"));
        schema.AddClass(track, dataObject);
        foreach (var def in ExtraDefinitions().Where(d => d.Name != "class CKeyBlendshape"))
            schema.AddStruct(def);
        return schema;
    }

    private static AktObject Track()
    {
        var t = new AktObject("class CAnimTrackBase");
        t.Fields.Add(new("class CDataObject", "Name", "CAtom", "Track"));
        t.Fields.Add(new("class CAnimTrackBase", "KeyList", "vector<Key>", new List<object?>()));
        t.Fields.Add(new("class CAnimTrackBase", "TangentInfoArray", "vector<TangentInfoList>", new List<object?>()));
        t.Fields.Add(new("class CAnimTrackBase", "Polygons", "vector<CPolygon>", new List<object?>()));
        t.Fields.Add(new("class CAnimTrackBase", "Target", "Reference<class CObject3D>", new AktReference(null)));
        return t;
    }

    private static AktStruct MrsKey()
    {
        var s = new AktStruct("class CMRSKey");
        s.Fields.Add(new("class CMRSKey", "TranslationX", "float", 0f));
        s.Fields.Add(new("class CMRSKey", "Rotation", "Quatf", new[] { 0f, 0f, 0f, 1f }));
        return s;
    }
}
