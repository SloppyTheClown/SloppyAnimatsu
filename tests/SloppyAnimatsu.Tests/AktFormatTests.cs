using System.Text;
using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Tests;

/// <summary>Format rules checked on small synthetic documents (no sample files needed).</summary>
public class AktFormatTests
{
    /// <summary>A scene with one keyed track, exercising references, vectors, structs and polymorphic values.</summary>
    private static AktDocument BuildDocument()
    {
        var schema = new AktSchema();
        var dataObject = new AktClassDef("class CDataObject");
        dataObject.Members.Add(new("Name", "CAtom"));
        schema.AddClass(dataObject, null);

        var scene = new AktClassDef("class CScene");
        scene.Members.Add(new("FrameRate", "uint"));
        scene.Members.Add(new("Current", "Reference<class CDataObject>"));
        schema.AddClass(scene, dataObject);

        var track = new AktClassDef("class CAnimTrackBase");
        track.Members.Add(new("Target", "Reference<class CObject3D>"));
        track.Members.Add(new("KeyList", "vector<Key>"));
        track.Members.Add(new("Visible", "bool"));
        schema.AddClass(track, dataObject);

        var unused = new AktClassDef("class CUnused");
        schema.AddClass(unused, dataObject);

        schema.AddStruct(new AktStructDef("CKeyBase"));
        var keyMrs = new AktStructDef("CKeyMRS");
        keyMrs.Members.Add(new("Key", "CMRSKey"));
        schema.AddStruct(keyMrs);
        var mrs = new AktStructDef("CMRSKey");
        mrs.Members.Add(new("TranslationX", "float"));
        mrs.Members.Add(new("Rotation", "Quatf"));
        schema.AddStruct(mrs);

        var root = new AktObject("class CScene");
        root.Fields.Add(new("class CDataObject", "Name", "CAtom", "Scene"));
        root.Fields.Add(new("class CScene", "FrameRate", "uint", 30u));

        var t = new AktObject("class CAnimTrackBase");
        t.Fields.Add(new("class CDataObject", "Name", "CAtom", "Track"));
        t.Fields.Add(new("class CAnimTrackBase", "Target", "Reference<class CObject3D>", new AktReference(root)));
        var key = new AktStruct("class CKeyMRS");
        var value = new AktStruct("CMRSKey");
        value.Fields.Add(new("CMRSKey", "TranslationX", "float", 1.5f));
        value.Fields.Add(new("CMRSKey", "Rotation", "Quatf", new[] { 0f, 0f, 0f, 1f }));
        key.Fields.Add(new("CKeyMRS", "Key", "CMRSKey", value));
        t.Fields.Add(new("class CAnimTrackBase", "KeyList", "vector<Key>", new List<object?> { key, null }));
        t.Fields.Add(new("class CAnimTrackBase", "Visible", "bool", true));
        root.AddChild(t);

        root.Fields.Add(new("class CScene", "Current", "Reference<class CDataObject>", new AktReference(t)));
        return new AktDocument(schema, new[] { root });
    }

    [Fact]
    public void SyntheticDocumentSurvivesWriteAndRead()
    {
        byte[] bytes = AktWriter.Write(BuildDocument());
        var doc = AktReader.Read(bytes);

        Assert.Equal(AktVersion.Current, doc.Version);
        var root = doc.Root;
        var track = Assert.Single(root.Children);
        Assert.Equal(30u, root.Get<uint>("FrameRate"));
        Assert.Same(track, root.Get<AktReference>("Current").Target);
        Assert.Same(root, track.Get<AktReference>("Target").Target);

        var keys = track.Get<IList<object?>>("KeyList");
        Assert.Equal(2, keys.Count);
        Assert.Null(keys[1]);
        var key = Assert.IsAssignableFrom<AktStruct>(keys[0]);
        Assert.Equal("class CKeyMRS", key.TypeName);
        var mrs = key.Get<AktStruct>("Key");
        Assert.Equal(1.5f, mrs.Get<float>("TranslationX"));
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, mrs.Get<float[]>("Rotation"));

        // Re-writing what was read is stable.
        Assert.Equal(bytes, AktWriter.Write(doc));
    }

    [Fact]
    public void ClassSchemaOmitsUninstantiatedClassesAndIsSorted()
    {
        var doc = AktReader.Read(AktWriter.Write(BuildDocument()));
        var names = doc.Schema.AllClasses.Select(c => c.Key).Order().ToArray();
        Assert.Equal(new[] { "CAnimTrackBase", "CDataObject", "CScene" }, names);
        Assert.Equal(new[] { "class CAnimTrackBase", "class CScene" },
                     doc.Schema.RootClasses[0].Derived.Select(c => c.Name));
    }

    [Fact]
    public void AtomsAreNumberedInFirstUseOrder()
    {
        var doc = AktReader.Read(AktWriter.Write(BuildDocument()));
        // Hierarchy first (class, then values in member order), then schemas.
        Assert.Equal(new[] { "class CScene", "Scene", "class CAnimTrackBase", "Track", "class CKeyMRS", "class CDataObject", "Name", "CAtom" },
                     doc.SourceAtoms.Take(8));
    }

    [Fact]
    public void InsertingAnObjectRenumbersReferences()
    {
        var doc = BuildDocument();
        var track = doc.Root.Children[0];
        var extra = new AktObject("class CAnimTrackBase");
        foreach (var f in track.Fields)
            extra.Fields.Add(new(f.Owner, f.Name, f.TypeName, f.Value));
        doc.Root.Children.Insert(0, extra);

        var reread = AktReader.Read(AktWriter.Write(doc));
        Assert.Equal(2, reread.Root.Get<AktReference>("Current").RawIndex); // root 0, extra 1, track 2
    }

    [Fact]
    public void NonBooleanBoolBytesArePreserved()
    {
        var doc = BuildDocument();
        doc.Root.Children[0].FindField("Visible")!.Value = (byte)0xD0;
        byte[] bytes = AktWriter.Write(doc);
        var reread = AktReader.Read(bytes);
        Assert.Equal((byte)0xD0, reread.Root.Children[0]["Visible"]);
        Assert.Equal(bytes, AktWriter.Write(reread));
    }

    [Fact]
    public void InvalidHeaderMeansLegacyFileWithoutHeader()
    {
        var doc = BuildDocument();
        doc.HasHeader = false;
        byte[] bytes = AktWriter.Write(doc);
        // The first u16 is the low half of the atom count (8+), the second its high half (0),
        // then the first atom string's characters: 'c' = 99 > 30, so validation fails.
        var reread = AktReader.Read(bytes);
        Assert.False(reread.HasHeader);
        Assert.Equal(AktVersion.Legacy, reread.Version);
        Assert.Equal("Scene", reread.Root.Name);
    }

    [Fact]
    public void TruncatedFileFailsWithOffset()
    {
        byte[] bytes = AktWriter.Write(BuildDocument());
        var ex = Assert.Throws<AktFormatException>(() => AktReader.Read(bytes[..^3]));
        Assert.True(ex.Offset > 0);
    }

    [Theory]
    [InlineData("class std::__1::vector<struct CPolygon>", "vector<CPolygon>")]
    [InlineData("CSmartPointer<class CKeyBase>", "CSmartPointer<CKeyBase>")]
    [InlineData("CMRSKey", "CMRSKey")]
    public void NamesNormaliseAcrossCompilers(string spelling, string key) =>
        Assert.Equal(key, AktNames.Key(spelling));

    [Theory]
    [InlineData("Reference<class CMesh>", AktTypeKind.Reference, "CMesh")]
    [InlineData("RefArray<class CJoint>", AktTypeKind.RefArray, "CJoint")]
    [InlineData("Key", AktTypeKind.Polymorphic, "CKeyBase")]
    [InlineData("TangentInfoList", AktTypeKind.Polymorphic, "CTangentInfoListBase")]
    [InlineData("IKBaseRefInfo", AktTypeKind.Struct, "IKBaseRefInfo")]
    [InlineData("map<uint,CAtom>", AktTypeKind.Map, null)]
    public void TypeNamesParse(string spelling, AktTypeKind kind, string? target)
    {
        var t = AktType.Parse(spelling);
        Assert.Equal(kind, t.Kind);
        Assert.Equal(target, t.Target);
        if (kind == AktTypeKind.Map)
        {
            Assert.Equal(AktTypeKind.UInt, t.KeyElement!.Kind);
            Assert.Equal(AktTypeKind.Atom, t.Element!.Kind);
        }
    }

    [Fact]
    public void NestedTemplatesParse()
    {
        var t = AktType.Parse("vector<Tuple<SpinnerModeOptions,9>>");
        Assert.Equal(AktTypeKind.Vector, t.Kind);
        Assert.Equal(AktTypeKind.Tuple, t.Element!.Kind);
        Assert.Equal(9, t.Element.Count);
        Assert.Equal(16, AktType.Parse("Mat4f").Count);
    }

    [Fact]
    public void PolymorphicChainsAreRootFirst() =>
        Assert.Equal(new[] { "CKeyBase", "CKeyMRS", "CKeyIK", "CKeyReverseFoot" }, AktNames.PolymorphicChain("CKeyReverseFoot"));

    [Fact]
    public void StringsAreNulTerminatedUtf16()
    {
        var doc = BuildDocument();
        doc.Root.FindField("Name")!.Value = "Scène ✓";
        var bytes = AktWriter.Write(doc);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes("Scène ✓\0")) > 0);
        Assert.Equal("Scène ✓", AktReader.Read(bytes).Root.Name);
    }

    // ---- SloppyAnimatsu container and packed field storage ----

    [Theory]
    [InlineData(AktCodec.Stored)]
    [InlineData(AktCodec.Zlib)]
    [InlineData(AktCodec.Brotli)]
    public void ContainerRoundTripsEveryCodec(AktCodec codec)
    {
        byte[] akt = AktWriter.Write(BuildDocument());
        var format = AktFileFormat.SloppyAnimatsu with { Codec = codec };
        byte[] wrapped = AktContainer.Wrap(akt, format);

        Assert.True(AktContainer.IsContainer(wrapped));
        var (payload, read) = AktContainer.Unwrap(wrapped);
        Assert.Equal(akt, payload);
        Assert.Equal(format, read);

        var doc = AktReader.Read(wrapped);
        Assert.Equal(format, doc.SourceFormat);
        Assert.Equal(akt, AktWriter.Write(doc));
        Assert.Equal(wrapped, doc.Write(format));
    }

    [Fact]
    public void ContainerIsWrittenWithTheSlopaMagicAndTheLegacyMagicStillReads()
    {
        byte[] akt = AktWriter.Write(BuildDocument());
        byte[] wrapped = AktContainer.Wrap(akt, AktFileFormat.SloppyAnimatsu);
        Assert.Equal("SLOPA\r\n\u001A"u8.ToArray(), wrapped[..8]);
        Assert.False(AktContainer.HasLegacyMagic(wrapped));

        byte[] legacy = (byte[])wrapped.Clone();
        "MYAKT\r\n\u001A"u8.CopyTo(legacy);
        Assert.True(AktContainer.IsContainer(legacy));
        Assert.True(AktContainer.HasLegacyMagic(legacy));
        var doc = AktReader.Read(legacy);
        Assert.Equal(AktFileFormat.SloppyAnimatsu, doc.SourceFormat);
        Assert.Equal(wrapped, doc.Write(AktFileFormat.SloppyAnimatsu));
    }

    [Fact]
    public void SceneFilesAcceptSlopaAndAkt()
    {
        Assert.True(SceneFiles.HasSceneExtension(@"C:\a\b.slopa"));
        Assert.True(SceneFiles.HasSceneExtension(@"C:\a\b.AKT"));
        Assert.False(SceneFiles.HasSceneExtension(@"C:\a\b.fbx"));
        Assert.Equal(["*.slopa", "*.akt"], SceneFiles.Patterns);
    }

    [Fact]
    public void PlainAktBytesAreDetectedAndKeptRaw()
    {
        byte[] akt = AktWriter.Write(BuildDocument());
        Assert.False(AktContainer.IsContainer(akt));
        Assert.Equal(AktFileFormat.Akeytsu, AktReader.Read(akt).SourceFormat);
        Assert.Same(akt, AktContainer.Wrap(akt, AktFileFormat.Akeytsu));
        Assert.Equal(akt, BuildDocument().Write(AktFileFormat.Akeytsu));
    }

    [Fact]
    public void ContainerFromANewerVersionIsRefused()
    {
        byte[] wrapped = AktContainer.Wrap(AktWriter.Write(BuildDocument()), AktFileFormat.SloppyAnimatsu);
        wrapped[8] = (byte)(AktContainer.CurrentVersion + 1);
        var e = Assert.Throws<AktFormatException>(() => AktReader.Read(wrapped));
        Assert.Contains("newer", e.Message);
    }

    [Fact]
    public void TruncatedContainerIsRefused()
    {
        byte[] wrapped = AktContainer.Wrap(AktWriter.Write(BuildDocument()), AktFileFormat.SloppyAnimatsu);
        Assert.Throws<AktFormatException>(() => AktContainer.Unwrap(wrapped[..^1]));
    }

    [Fact]
    public void ObjectsReadFromOneClassShareTheirLayout()
    {
        var doc = AktReader.Read(AktWriter.Write(BuildDocument()));
        var a = doc.Root.Children[0];
        var b = a.CloneHierarchy();
        Assert.Same(a.MemberAt(0), b.MemberAt(0));

        // A write to the clone's layout (copy-on-write) leaves the original untouched.
        b.Fields.Add(new("class CAnimTrackBase", "Extra", "uint", 7u));
        Assert.False(a.Has("Extra"));
        Assert.Equal(7u, b.Get<uint>("Extra"));
    }

    [Fact]
    public void FieldViewsWriteThroughAndFollowTheirMember()
    {
        var doc = AktReader.Read(AktWriter.Write(BuildDocument()));
        var root = doc.Root;
        var frameRate = root.FindField("FrameRate")!;
        frameRate.Value = 60u;
        Assert.Equal(60u, root.Get<uint>("FrameRate"));

        // Removing an earlier member moves the slot; the view follows it.
        root.Fields.RemoveAt(0);
        Assert.Equal(60u, frameRate.Value);
        root.Set("FrameRate", 24u);
        Assert.Equal(24u, frameRate.Value);
    }

    [Fact]
    public void SmallValuesAreSharedBoxes()
    {
        Assert.Same(AktBox.Float(0f), AktBox.Float(0f));
        Assert.Same(AktBox.UInt(256u), AktBox.UInt(256u));
        Assert.Same(AktBox.Bool(true), AktBox.Bool(true));
        Assert.NotSame(AktBox.Float(-0f), AktBox.Float(0f));
        Assert.Equal(-0f, (float)AktBox.Float(-0f));
    }
}
