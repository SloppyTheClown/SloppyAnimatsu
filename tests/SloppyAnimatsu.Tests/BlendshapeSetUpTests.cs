using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The blendshape setup file (<c>.bsu</c>): the exact bytes of a minimal file, the write/read
/// round trip, the import's matching rules and its refusals
/// The CLI row runs the verb end to end on a real scene.
/// </summary>
public class BlendshapeSetUpTests
{
    private static string? Sample() =>
        TestFiles.SceneSamples.FirstOrDefault(p => Path.GetFileName(p).StartsWith("7_AddingIK", StringComparison.Ordinal))
        ?? TestFiles.SceneSamples.FirstOrDefault(p => new FileInfo(p).Length < 20_000_000)
        ?? TestFiles.SceneSamples.FirstOrDefault();

    private static (Character Character, Mesh Mesh)? Pick(SceneModel model)
    {
        foreach (var mesh in model.Scene.Meshes)
        {
            var character = mesh.BindPose?.Character ?? mesh.Ancestor<Character>();
            if (character != null && mesh.ControlPoints.Length > 0) return (character, mesh);
        }
        return null;
    }

    /// <summary>The minimal setup's bytes: one controller "Jaw" (plain), direction 2, attenuation 0.5, "Nose"@1.</summary>
    private static byte[] MinimalBytes() =>
    [
        0x14, 0x00, 0x03, 0x00, 0x0D, 0x00, 0x01, 0x00,   // the header: 20, 3, 13, 1
        0x01, 0x00, 0x00, 0x00,                           // 1 controller
        0x4A, 0x00, 0x61, 0x00, 0x77, 0x00, 0x00, 0x00,   // "Jaw" + NUL (a 3-character name adds 8 bytes)
        0xFF, 0xFF, 0xFF, 0xFF,                           // click -1: a plain controller
        0x01, 0x00, 0x00, 0x00,                           // 1 channel
        0x02, 0x00, 0x00, 0x00,                           // Direction 2
        0x00, 0x00, 0x00, 0x3F,                           // Attenuation 0.5
        0x01, 0x00, 0x00, 0x00,                           // 1 info
        0x4E, 0x00, 0x6F, 0x00, 0x73, 0x00, 0x65, 0x00, 0x00, 0x00, // "Nose" + NUL
        0x00, 0x00, 0x80, 0x3F,                           // Position 1
    ];

    private static BlendshapeSetUpController[] MinimalSetup() =>
    [
        new("Jaw", -1, [new BlendshapeSetUpChannel(2, 0.5f, [new BlendshapeSetUpInfo("Nose", 1f)])]),
    ];

    /// <summary>The layout is pinned byte for byte, so a change to the container is a test failure.</summary>
    [Fact]
    public void Write_PinsTheMinimalFilesBytes()
    {
        Assert.Equal(MinimalBytes(), BlendshapeSetUp.Write(MinimalSetup()));
        // A name is its UTF-16 bytes plus a two-byte NUL: a 10-character name is 22 bytes, so the
        // same file with that name instead of "Jaw" (3 characters, 8 bytes) is 14 bytes longer.
        byte[] bare = BlendshapeSetUp.Write([new BlendshapeSetUpController("Jaw", -1, [])]);
        Assert.Equal(28, bare.Length);
        byte[] wide = BlendshapeSetUp.Write([new BlendshapeSetUpController("ABCDEFGHIJ", -1, [])]);
        Assert.Equal(28 + 14, wide.Length);
    }

    /// <summary>Both controller kinds survive a write and a read, with the field order the writer uses.</summary>
    [Fact]
    public void RoundTrip_KeepsAFaceAndAPlainController()
    {
        var setup = new[]
        {
            new BlendshapeSetUpController("Smile_Ctl", -1,
                [new BlendshapeSetUpChannel(2, 0.5f, [new BlendshapeSetUpInfo("Smile", 1f), new BlendshapeSetUpInfo("Brow", 0.25f)])]),
            new BlendshapeSetUpController("Zone_Ctl", 0,
                [new BlendshapeSetUpChannel(0, 1f, [new BlendshapeSetUpInfo("Eye", 0.75f)])]),
        };
        var read = BlendshapeSetUp.Read(BlendshapeSetUp.Write(setup));
        Assert.Equal(setup.Length, read.Count);
        Assert.Equal("Smile_Ctl", read[0].Name);
        Assert.Equal(-1, read[0].Click);
        Assert.Equal(2u, read[0].Channels[0].Direction);
        Assert.Equal(0.5f, read[0].Channels[0].Attenuation);
        Assert.Equal(["Smile", "Brow"], read[0].Channels[0].Infos.Select(i => i.Target));
        Assert.Equal([1f, 0.25f], read[0].Channels[0].Infos.Select(i => i.Position));
        Assert.Equal(0, read[1].Click);
        Assert.Equal(0u, read[1].Channels[0].Direction);
        Assert.Equal(1f, read[1].Channels[0].Attenuation);
        Assert.True(BlendshapeSetUp.TryRead(BlendshapeSetUp.Write(setup), out var tried, out var error));
        Assert.Null(error);
        Assert.Equal(setup.Length, tried.Count);
        Assert.Equal("Zone_Ctl", tried[1].Name);
        Assert.Equal(0.75f, tried[1].Channels[0].Infos[0].Position);
    }

    /// <summary>An import creates every object the file names; a second import only updates.</summary>
    [Fact]
    public void Apply_CreatesWhatTheFileNames_AndTheSecondImportOnlyUpdates()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        var setup = new[]
        {
            new BlendshapeSetUpController("Smile_Ctl", -1,
                [new BlendshapeSetUpChannel(2, 0.5f, [new BlendshapeSetUpInfo("Smile", 1f), new BlendshapeSetUpInfo("Brow", 0.25f)])]),
            new BlendshapeSetUpController("Zone_Ctl", 0,
                [new BlendshapeSetUpChannel(0, 1f, [new BlendshapeSetUpInfo("Eye", 1f)])]),
        };

        var first = BlendshapeSetUp.Apply(mesh, setup);
        Assert.Equal(2, first.ControllersCreated);
        Assert.Equal(2, first.ChannelsCreated);
        Assert.Equal(3, first.InfosCreated);
        Assert.Equal(3, first.TargetsCreated);
        var controllers = character.Controllers.OfType<BlendshapeController>()
            .Where(c => ReferenceEquals(c.Mesh, mesh)).ToList();
        Assert.Equal(2, controllers.Count);
        var smile = controllers.Single(c => c.Name == "Smile_Ctl");
        Assert.IsNotType<BlendshapeFaceController>(smile);
        var smileChannel = Assert.Single(smile.Channels);
        Assert.Equal(2u, smileChannel.Direction);
        Assert.Equal(0.5f, smileChannel.Attenuation);
        Assert.Equal(["Smile", "Brow"], smileChannel.Infos.Select(i => i.Blendshape?.Name));
        Assert.Equal([1f, 0.25f], smileChannel.Infos.Select(i => i.Position));
        var zone = Assert.IsType<BlendshapeFaceController>(controllers.Single(c => c.Name == "Zone_Ctl"));
        Assert.Equal(0u, zone.Click);
        Assert.Equal(["Smile", "Brow", "Eye"], mesh.BlendshapeTargets.Select(t => t.Name));

        var second = BlendshapeSetUp.Apply(mesh, setup);
        Assert.Equal(0, second.ControllersCreated);
        Assert.Equal(0, second.ChannelsCreated);
        Assert.Equal(0, second.InfosCreated);
        Assert.Equal(0, second.TargetsCreated);
        Assert.Equal(2, second.ControllersUpdated);
        Assert.Equal(2, second.ChannelsUpdated);
        Assert.Equal(3, second.InfosUpdated);
        Assert.Equal(2, character.Controllers.OfType<BlendshapeController>().Count(c => ReferenceEquals(c.Mesh, mesh)));
        Assert.Single(smile.Channels);
        Assert.Equal(3, mesh.BlendshapeTargets.Count);
    }

    /// <summary>A file naming a controller, channel and target that exist updates them by name/direction/reference.</summary>
    [Fact]
    public void Apply_UpdatesAnExistingNameInsteadOfDuplicatingIt()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        var editor = new BlendshapeEditor(character);
        var target = editor.AddTarget(mesh, "Jaw");
        var controller = editor.AddController(mesh, "Smile_Ctl");
        var channel = editor.AddChannel(controller, 2, attenuation: 1f);
        editor.AddInfo(channel, target, 1f);

        var result = BlendshapeSetUp.Apply(mesh, [new BlendshapeSetUpController("Smile_Ctl", -1,
            [new BlendshapeSetUpChannel(2, 0.5f, [new BlendshapeSetUpInfo("Jaw", 0.5f)])])]);

        Assert.Equal(0, result.ControllersCreated);
        Assert.Equal(0, result.ChannelsCreated);
        Assert.Equal(0, result.InfosCreated);
        Assert.Equal(0, result.TargetsCreated);
        Assert.Equal(1, result.ControllersUpdated);
        Assert.Equal(1, result.ChannelsUpdated);
        Assert.Equal(1, result.InfosUpdated);
        Assert.Same(controller, Assert.Single(character.Controllers.OfType<BlendshapeController>().Where(c => ReferenceEquals(c.Mesh, mesh))));
        var only = Assert.Single(controller.Channels);
        Assert.Equal(2u, only.Direction);
        Assert.Equal(0.5f, only.Attenuation);
        Assert.Equal("Jaw", Assert.Single(only.Infos).Blendshape?.Name);
        Assert.Equal(0.5f, only.Infos[0].Position);
        Assert.Equal(["Jaw"], mesh.BlendshapeTargets.Select(t => t.Name));

        // Names match case-insensitively, as the QString::compare does.
        var lower = BlendshapeSetUp.Apply(mesh, [new BlendshapeSetUpController("smile_ctl", -1, [])]);
        Assert.Equal(0, lower.ControllersCreated);
        Assert.Equal(1, lower.ControllersUpdated);
        Assert.Single(character.Controllers.OfType<BlendshapeController>().Where(c => ReferenceEquals(c.Mesh, mesh)));
    }

    /// <summary>A created channel collides with the editor's "Channel" name and is uniqued.</summary>
    [Fact]
    public void Apply_GivesACreatedChannelAUniqueName()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (character, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        var editor = new BlendshapeEditor(character);
        var controller = editor.AddController(mesh, "Smile_Ctl");
        editor.AddChannel(controller, 3, 1f);

        var result = BlendshapeSetUp.Apply(mesh, [new BlendshapeSetUpController("Smile_Ctl", -1,
            [new BlendshapeSetUpChannel(0, 1f, [new BlendshapeSetUpInfo("Jaw", 1f)])])]);

        Assert.Equal(0, result.ControllersCreated);
        Assert.Equal(1, result.ChannelsCreated);
        Assert.Equal(2, controller.Channels.Count);
        Assert.Equal(["Channel", "Channel_02"], controller.Channels.Select(c => c.Name));
        Assert.Equal(controller.Channels.Select(c => c.Name).Distinct().Count(), controller.Channels.Count);
        Assert.Equal(3u, controller.Channels[0].Direction);
        Assert.Equal(0u, controller.Channels[1].Direction);
    }

    [Fact]
    public void Read_RefusesATruncatedFileAWrongHeaderANegativeCountAndAnUnterminatedName()
    {
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Read([0x14, 0x00, 0x03]));
        var wrongHeader = MinimalBytes();
        wrongHeader[0] = 0x15;
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Read(wrongHeader));
        Assert.False(BlendshapeSetUp.TryRead(wrongHeader, out var none, out var error));
        Assert.Empty(none);
        Assert.Contains("header", error);

        // The channel's attenuation is missing: the file ends inside the channel list.
        var truncated = MinimalBytes()[..32];
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Read(truncated));

        var negative = new byte[12];
        MinimalBytes().AsSpan(0, 8).CopyTo(negative);
        BitConverter.TryWriteBytes(negative.AsSpan(8), -1);
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Read(negative));

        // The controller name ends at the end of the file, without its NUL terminator.
        var unterminated = MinimalBytes()[..18];
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Read(unterminated));
    }

    /// <summary>The string codec cannot store a NUL: the writer refuses one.</summary>
    [Fact]
    public void Write_RefusesANulInAName() =>
        Assert.Throws<FormatException>(() => BlendshapeSetUp.Write(
            [new BlendshapeSetUpController("Ja\0w", -1, [new BlendshapeSetUpChannel(0, 1f, [new BlendshapeSetUpInfo("A\0B", 1f)])])]));

    /// <summary>An empty name has nothing to match or create.</summary>
    [Fact]
    public void Apply_RefusesAnEmptyName()
    {
        if (Sample() is not { } path) return;
        var model = TestFiles.LoadScene(path);
        var (_, mesh) = Pick(model) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        Assert.Throws<InvalidDataException>(() => BlendshapeSetUp.Apply(mesh,
            [new BlendshapeSetUpController("", -1, [])]));
        Assert.Throws<InvalidDataException>(() => BlendshapeSetUp.Apply(mesh,
            [new BlendshapeSetUpController("Ctl", -1, [new BlendshapeSetUpChannel(0, 1f, [new BlendshapeSetUpInfo("", 1f)])])]));
    }

    /// <summary>Export, import into a copy and export again: the two files are identical.</summary>
    [Fact]
    public void CliExportImportExport_IsByteIdentical()
    {
        if (Sample() is not { } path) return;
        var (character, mesh) = Pick(TestFiles.LoadScene(path)) ?? throw new Xunit.Sdk.XunitException("The sample has no character mesh.");
        string stem = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-bsu-{Guid.NewGuid():N}");
        string scene = stem + ".akt", built = stem + ".built.akt", imported = stem + ".imported.akt";
        string first = stem + ".1.bsu", again = stem + ".2.bsu", refused = stem + ".refused.akt";
        try
        {
            File.Copy(path, scene);
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main([
                "blendshape-add", scene, character.Name!, mesh.Name!, "--target=Jaw", "--controller=Smile_Ctl",
                "--direction=2", "--attenuation=0.5", "--position=1", $"--out={built}",
            ]));
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main(["blendshape-setup", "export", built, mesh.Name!, first]));
            Assert.True(File.Exists(first));
            byte[] written = File.ReadAllBytes(first);
            Assert.Equal(new byte[] { 0x14, 0x00, 0x03, 0x00, 0x0D, 0x00, 0x01, 0x00 }, written[..8]);
            var captured = Assert.Single(BlendshapeSetUp.Read(written));
            Assert.Equal("Smile_Ctl", captured.Name);
            Assert.Equal(-1, captured.Click);
            var capturedChannel = Assert.Single(captured.Channels);
            Assert.Equal(2u, capturedChannel.Direction);
            Assert.Equal(0.5f, capturedChannel.Attenuation);
            Assert.Equal("Jaw", Assert.Single(capturedChannel.Infos).Target);
            Assert.Equal(1f, capturedChannel.Infos[0].Position);

            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main(["blendshape-setup", "import", built, mesh.Name!, first,
                $"--out={imported}"]));
            Assert.Equal(0, SloppyAnimatsu.Cli.Program.Main(["blendshape-setup", "export", imported, mesh.Name!, again]));
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(again));

            var reloaded = TestFiles.LoadScene(imported);
            var reloadedMesh = reloaded.Scene.Meshes.Single(m => m.Name == mesh.Name);
            var controller = Assert.Single(reloaded.Scene.Characters.SelectMany(c => c.Controllers)
                .OfType<BlendshapeController>().Where(c => ReferenceEquals(c.Mesh, reloadedMesh)));
            Assert.Equal("Smile_Ctl", controller.Name);
            var channel = Assert.Single(controller.Channels);
            Assert.Equal(2u, channel.Direction);
            Assert.Equal(0.5f, channel.Attenuation);
            Assert.Equal("Jaw", Assert.Single(channel.Infos).Blendshape?.Name);
            Assert.Equal(1f, channel.Infos[0].Position);

            // A 3-byte "setup" is refused by the verb and writes no scene.
            File.WriteAllBytes(again, [0x14, 0x00, 0x03]);
            Assert.NotEqual(0, SloppyAnimatsu.Cli.Program.Main(["blendshape-setup", "import", built, mesh.Name!, again,
                $"--out={refused}"]));
            Assert.False(File.Exists(refused));
        }
        finally
        {
            foreach (string file in new[] { scene, built, imported, first, again, refused })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
