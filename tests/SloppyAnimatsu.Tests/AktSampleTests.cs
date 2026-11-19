using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Tests;

public class AktSampleTests
{
    [Theory]
    [MemberData(nameof(TestFiles.AllSamples), MemberType = typeof(TestFiles))]
    public void RoundTripIsByteExact(string path)
    {
        // A SloppyAnimatsu-container scene (MYAKT) holds the .akt stream compressed: compare that stream.
        byte[] original = AktContainer.Unwrap(File.ReadAllBytes(path)).Payload;
        byte[] written = AktWriter.Write(AktReader.Read(original));
        Assert.Equal(original.Length, written.Length);
        Assert.True(original.AsSpan().SequenceEqual(written), $"{path}: first difference at 0x{original.AsSpan().CommonPrefixLength(written):X}");
    }

    /// <summary>
    /// <see cref="AktReader.ProbeRootClass"/> picks the scene files without building the
    /// hierarchy (the test sample set is classified once per run with it).
    /// </summary>
    [Fact]
    public void ProbeRootClassAgreesWithAFullRead()
    {
        if (TestFiles.Resource3D is not { } scene) return; // reference install not present
        Assert.Equal("CScene", AktReader.ProbeRootClass(File.ReadAllBytes(scene)));
        if (TestFiles.Preferences is { } preferences)
            Assert.Equal(AktDocument.Load(preferences).Root.ClassKey,
                AktReader.ProbeRootClass(File.ReadAllBytes(preferences)));
    }

    [Fact]
    public void Resource3DHasTheDefaultScene()
    {
        if (TestFiles.Resource3D == null) return; // reference install not present
        var doc = AktDocument.Load(TestFiles.Resource3D!);

        Assert.Equal(new AktVersion(20, 1, 4, 12, 0), doc.Version);
        Assert.True(doc.HasHeader);
        Assert.Single(doc.Roots);
        Assert.Equal("CScene", doc.Root.ClassKey);
        Assert.Equal("Scene", doc.Root.Name);
        Assert.Equal(119, doc.Objects().Count);

        var cameras = doc.Root.Get<List<AktReference>>("CameraList");
        Assert.Equal(new[] { "Perspective", "Front", "Back", "Right", "Left", "Top", "Bottom", "Photo" },
                     cameras.Select(r => r.Target?.Name));
        Assert.Same(doc.Root.Children[0], cameras[0].Target);

        // 20.1.4 predates CScene.Time.
        Assert.False(doc.Root.Has("Time"));
        Assert.Equal(60u, doc.Root.Get<uint>("FrameRate"));
    }
}
