using System.Numerics;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Audio;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Audio tracks. A real.akt scene with three imported sounds
/// fixes the serialized shape and the embedded
/// PCM form; the fixture builds its own document with those classes, writes it, reloads it and
/// reads the events back.
/// </summary>
public class AudioTests
{
    private const uint Fps = 30;

    // ---- the fixture -------------------------------------------------------------------------

    private sealed record Fixture(SceneModel Model, Animation Animation, AudioStack Stack, AudioTrack Track, AudioEntry Entry, byte[] Bytes, string Path);

    /// <summary>A scene with one audio file and an audio stack: one event per key time (the last key is the loop key).</summary>
    private static Fixture Build(float[] sound, int sampleRate, int[] keyTimes, float opacity = 1f, uint flags = 0x2, int delay = 0, byte[]? embedded = null)
    {
        var schema = AudioSchema();
        var scene = schema.NewObject("class CScene");
        scene.Set("Name", "Audio Scene");
        scene.Set("FrameRate", Fps);

        var audioObject = schema.NewObject("class CAudioObject");
        audioObject.Set("Name", "Audio Object");
        audioObject.Set("Flags", 1u);
        audioObject.Set("LTM", AktMath.ToArray(Matrix4x4.Identity));

        var entry = schema.NewObject("class CAudioEntry");
        entry.Set("Name", "Audio Entry");
        entry.Set("FileName", "step.wav");
        entry.Set("EmbedBufferData", embedded ?? WaveFile.EncodeMono(sound, sampleRate));

        var track = schema.NewObject("class CAnimTrackAudio");
        track.Set("Name", "AudioTrack");
        track.Set("ChannelMask", 0u);
        track.Set("Target", new AktReference(audioObject));
        var keys = new List<object?>();
        foreach (int _ in keyTimes) keys.Add(Key(schema, entry, volume: 0.8f, delay: delay));
        track.Set("KeyList", keys);

        var stack = schema.NewObject("class CAnimStackAudio");
        stack.Set("Name", "Audio");
        stack.Set("Flags", flags);
        stack.Set("FlagsNoModify", 0u);
        stack.Set("Opacity", opacity);
        stack.Set("Tweenage", 0.5f);
        stack.Set("Color", new byte[] { 0x40, 0x80, 0xC0, 0xFF });
        stack.Set("AudioTrack", new AktReference(track));
        stack.Set("TimeArray", keyTimes.Select(t => (object?)StackKey(schema, t)).ToList());

        var animation = schema.NewObject("class CAnimation");
        animation.Set("Name", "Anim");
        animation.Set("StackList", new List<AktReference> { new(stack) });
        animation.Set("CurrentStack", new AktReference(stack));
        animation.Set("CurrentTime", 0);

        scene.AddChild(audioObject);
        scene.AddChild(entry);
        scene.AddChild(animation);
        animation.AddChild(stack);
        stack.AddChild(track);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sloppyanimatsu-audio-{Guid.NewGuid():N}.akt");
        new AktDocument(schema, [scene]).Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        var model = TestFiles.LoadScene(path);
        var anim = model.Scene.Descendants<Animation>().Single();
        var audio = anim.Stacks.OfType<AudioStack>().Single();
        return new Fixture(model, anim, audio, audio.AudioTrack!, model.Scene.AudioEntries.Single(), bytes, path);
    }

    /// <summary>The classes and structs of the audio feature, as describes them.</summary>
    private static AktSchema AudioSchema()
    {
        var schema = new AktSchema();
        void Class(string name, string? parent, params (string Name, string Type)[] members)
        {
            var def = new AktClassDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddClass(def, parent == null ? null : schema.FindClass("class " + parent));
        }
        void Struct(string name, params (string Name, string Type)[] members)
        {
            var def = new AktStructDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddStruct(def);
        }

        Class("CDataObject", null, ("Name", "CAtom"));
        Class("CObject3D", "CDataObject", ("Flags", "uint"), ("LTM", "Mat4f"), ("Constraint", "Reference<class CController3D>"));
        Class("CScene", "CDataObject", ("FrameRate", "uint"));
        Class("CAudioObject", "CObject3D");
        Class("CAudioEntry", "CDataObject", ("FileName", "QString"), ("EmbedBufferData", "vector<uchar>"));
        Class("CAnimation", "CDataObject",
            ("StackList", "RefArray<class CAnimStack>"), ("CurrentStack", "Reference<class CAnimStack>"), ("CurrentTime", "int"));
        Class("CAnimStack", "CDataObject",
            ("TimeArray", "vector<CAnimStackKey>"), ("Flags", "uint"), ("FlagsNoModify", "uint"),
            ("Color", "RGBAi"), ("Opacity", "float"), ("Tweenage", "float"));
        Class("CAnimStackAudio", "CAnimStack", ("AudioTrack", "Reference<class CAnimTrackAudio>"));
        Class("CAnimTrackBase", "CDataObject",
            ("ChannelMask", "uint"), ("Target", "Reference<class CObject3D>"), ("KeyList", "vector<Key>"));
        Class("CAnimTrackAudio", "CAnimTrackBase");

        Struct("CAnimStackKey", ("Time", "int"), ("Flags", "uint"), ("FadeIn", "ushort"), ("FadeOut", "ushort"));
        Struct("CKeyBase");
        Struct("CKeyAudioFrame", ("DelaySinceStartKey", "uint"), ("Audio", "Key"), ("Volume", "float"));
        Struct("CKeyAudioAnim", ("AudioFile", "Reference<class CAudioEntry>"), ("Volume", "float"), ("Mute", "bool"));
        return schema;
    }

    // ---- the WAV decoder ---------------------------------------------------------------------

    [Fact]
    public void WaveFile_ReadsPcm16Mono()
    {
        var samples = new float[800];
        for (int i = 0; i < samples.Length; i++) samples[i] = MathF.Sin(i * 0.05f) * 0.5f;
        var wave = WaveFile.TryDecode(WaveFile.EncodeMono(samples, 8000));

        Assert.NotNull(wave);
        Assert.Equal(8000, wave!.SampleRate);
        Assert.Equal(1, wave.Channels);
        Assert.Equal(800, wave.FrameCount);
        Assert.Equal(0.1, wave.Duration, 3);
        for (int i = 0; i < samples.Length; i += 37)
            Assert.Equal(samples[i], wave.Sample(i, 0), 3);
    }

    [Fact]
    public void WaveFile_RejectsNonWavData()
    {
        Assert.Null(WaveFile.TryDecode(null));
        Assert.Null(WaveFile.TryDecode(System.Text.Encoding.ASCII.GetBytes("ID3 this is an mp3, not a wav file at all")));
    }

    [Fact]
    public void WaveFile_ReadsEmbeddedRawPcm()
    {
        // 0.1 s of a two-channel ramp, written the way the app embeds a decoded sound.
        var interleaved = new float[4410 * 2];
        for (int i = 0; i < interleaved.Length; i++) interleaved[i] = (i % 9 - 4) * 0.1f;
        var wave = WaveFile.TryDecodeEmbedded(WaveFile.EncodePcm16Stereo(interleaved));

        Assert.NotNull(wave);
        Assert.Equal(44100, wave!.SampleRate);
        Assert.Equal(2, wave.Channels);
        Assert.Equal(4410, wave.FrameCount);
        Assert.Equal(0.1, wave.Duration, 4);
        Assert.Equal("PCM s16le stereo", wave.Format);
        for (int i = 0; i < interleaved.Length; i += 97)
            Assert.Equal(interleaved[i], wave.Samples[i], 3);
    }

    [Fact]
    public void WaveFile_LeavesMediaContainersAlone()
    {
        // A scene that stored the original file must not be read as raw PCM noise.
        Assert.Null(WaveFile.TryDecodeEmbedded([.. "OggS"u8, 1, 2, 3, 4]));
        Assert.Null(WaveFile.TryDecodeEmbedded([.. "ID3 "u8, 1, 2, 3, 4]));
        Assert.Null(WaveFile.TryDecodeEmbedded([1, 2, 3]));   // not a whole stereo frame
    }

    [Fact]
    public void AudioEntry_DecodesEmbeddedRawPcm()
    {
        var interleaved = Constant(0.25f, 44100 * 2);   // 1 s at the embedded rate
        var fixture = Build(interleaved, 44100, [0, 60], embedded: WaveFile.EncodePcm16Stereo(interleaved));
        try
        {
            Assert.Equal(44100, fixture.Entry.Audio!.SampleRate);
            Assert.Equal(1.0, fixture.Entry.DurationSeconds, 3);
            Assert.Single(fixture.Stack.Events);
            Assert.Equal(1.0, fixture.Stack.Events[0].DurationSeconds, 3);
            Assert.True(fixture.Stack.Events[0].IsAudibleAt(15, 30));   // half a second in
            Assert.False(fixture.Stack.Events[0].IsAudibleAt(31, 30));  // past the end
        }
        finally { File.Delete(fixture.Path); }
    }

    // ---- loading -----------------------------------------------------------------------------

    [Fact]
    public void SceneWithAudio_LoadsAndRoundTripsByteExact()
    {
        var fixture = Build(Constant(0.5f, 4000), 8000, [0, 40]);
        try
        {
            Assert.Equal("CScene", fixture.Model.Scene.ClassName);
            Assert.Single(fixture.Model.Scene.AudioObjects);
            Assert.Equal(fixture.Bytes, AktWriter.Write(AktDocument.Load(fixture.Path)));
        }
        finally { File.Delete(fixture.Path); }
    }

    [Fact]
    public void AudioStack_ReadsOneEventPerKeyExceptTheLoopKey()
    {
        var fixture = Build(Constant(0.5f, 24000), 48000, [0, 20, 40]);
        try
        {
            var events = fixture.Stack.Events;
            Assert.Equal(2, events.Count);
            Assert.All(events, e => Assert.Equal("step.wav", e.Entry?.DisplayName));
            Assert.Equal(new[] { 0, 20 }, events.Select(e => e.StartFrame));
            Assert.Equal(0.5, events[0].DurationSeconds, 3);
            Assert.Equal(0.8f, events[0].Volume);
            Assert.False(events[0].Mute);
            Assert.Same(fixture.Track, events[0].Track);
        }
        finally { File.Delete(fixture.Path); }
    }

    // ---- the playback schedule ---------------------------------------------------------------

    [Fact]
    public void Schedule_PlaysAnEventOnlyInsideItsDuration()
    {
        var fixture = Build(Constant(0.5f, 12000), 48000, [0, 40]);  // 0.25 s = 7.5 frames at 30 fps
        try
        {
            for (int frame = 0; frame < 8; frame++)
                Assert.Single(AudioSchedule.At(fixture.Animation, frame / (double)Fps, frame, Fps));
            Assert.Empty(AudioSchedule.At(fixture.Animation, 8 / (double)Fps, 8, Fps));
            Assert.Empty(AudioSchedule.At(fixture.Animation, -2 / (double)Fps, -2, Fps));
        }
        finally { File.Delete(fixture.Path); }
    }

    [Fact]
    public void Schedule_UsesTheStackOpacityAndSkipsADisabledStack()
    {
        var quiet = Build(Constant(0.5f, 12000), 48000, [0, 40], opacity: 0.25f);
        try
        {
            var clip = Assert.Single(AudioSchedule.At(quiet.Animation, 0.1, 3, Fps));
            Assert.Equal(0.25f, clip.Volume);
        }
        finally { File.Delete(quiet.Path); }

        var disabled = Build(Constant(0.5f, 12000), 48000, [0, 40], flags: 0u);
        try
        {
            Assert.Empty(AudioSchedule.At(disabled.Animation, 0.1, 3, Fps));
        }
        finally { File.Delete(disabled.Path); }
    }

    [Fact]
    public void Schedule_TakesTheLatestStartedEvent()
    {
        var fixture = Build(Constant(0.5f, 24000), 48000, [0, 5, 40]);
        try
        {
            var early = Assert.Single(AudioSchedule.At(fixture.Animation, 2 / (double)Fps, 2, Fps));
            Assert.Equal(0, Assert.IsType<AudioEvent>(early.Key).StartFrame);
            var late = Assert.Single(AudioSchedule.At(fixture.Animation, 6 / (double)Fps, 6, Fps));
            Assert.Equal(5, Assert.IsType<AudioEvent>(late.Key).StartFrame);
        }
        finally { File.Delete(fixture.Path); }
    }

    [Fact]
    public void Schedule_CycleWrapsThePlayheadOverTheStackSpan()
    {
        var fixture = Build(Constant(0.5f, 24000), 48000, [0, 40], flags: 0x2 | 0x1);
        try
        {
            // The span is 40 frames; 45 wraps to 5, which is 5 frames after the event started.
            double frame = AudioSchedule.FrameAt(0, 45 / (double)Fps, Fps, 0, 40, loop: true);
            Assert.Equal(5, frame, 6);
            var clip = Assert.Single(AudioSchedule.At(fixture.Animation, 45 / (double)Fps, frame, Fps));
            Assert.Equal(45 / (double)Fps - 5 / (double)Fps, clip.StartSeconds, 6);
        }
        finally { File.Delete(fixture.Path); }
    }

    [Fact]
    public void Schedule_HonoursTheKeyDelay()
    {
        var fixture = Build(Constant(0.5f, 24000), 48000, [0, 40], delay: 3);
        try
        {
            Assert.Empty(AudioSchedule.At(fixture.Animation, 2 / (double)Fps, 2, Fps));
            var clip = Assert.Single(AudioSchedule.At(fixture.Animation, 3 / (double)Fps, 3, Fps));
            Assert.Equal(3 / (double)Fps, clip.StartSeconds, 6);
        }
        finally { File.Delete(fixture.Path); }
    }

    // ---- the mixer ---------------------------------------------------------------------------

    [Fact]
    public void Mixer_RendersTheClipFromThePlayheadPosition()
    {
        const int rate = 8000;
        var ramp = new float[rate / 2];                    // a 0.5 s ramp from 0 to 1
        for (int i = 0; i < ramp.Length; i++) ramp[i] = i / (float)(ramp.Length - 1);
        var fixture = Build(ramp, rate, [0, 40]);
        try
        {
            var animation = fixture.Animation;
            var mixer = new AudioMixer(rate, 2, seconds =>
            {
                double frame = AudioSchedule.FrameAt(0, seconds, Fps, 0, 40, loop: false);
                return AudioSchedule.At(animation, seconds, frame, Fps);
            });

            // 0.2 s into a 0.5 s ramp: ~0.4, on both channels.
            mixer.Seek(0.2);
            var block = mixer.Render(100);
            Assert.Equal(0.4f, block[0], 3);
            Assert.Equal(block[0], block[1], 6);
            Assert.Equal(0.4f + 99 / (float)rate / 0.5f, block[^2], 3);

            // Past the end of the clip: silence.
            mixer.Seek(0.6);
            Assert.All(mixer.Render(100), s => Assert.Equal(0f, s));

            // Two blocks in a row are the same as one long block.
            mixer.Seek(0.1);
            var two = mixer.Render(50).Concat(mixer.Render(50)).ToArray();
            mixer.Seek(0.1);
            Assert.Equal(mixer.Render(100), two);
        }
        finally { File.Delete(fixture.Path); }
    }

    [Fact]
    public void Mixer_MixesOverlappingStacksAndClamps()
    {
        // Two audio stacks, each a full-scale clip: 0.75 + 0.75, clamped to 1.
        var schema = AudioSchema();
        var scene = schema.NewObject("class CScene");
        scene.Set("Name", "Scene");
        scene.Set("FrameRate", Fps);
        byte[] bytes = WaveFile.EncodeMono(Constant(0.75f, 8000), 8000);
        var animation = schema.NewObject("class CAnimation");
        animation.Set("Name", "Anim");
        var stackRefs = new List<AktReference>();
        for (int i = 0; i < 2; i++)
        {
            var entry = schema.NewObject("class CAudioEntry");
            entry.Set("Name", $"Entry{i}");
            entry.Set("FileName", "two.wav");
            entry.Set("EmbedBufferData", bytes);
            var track = schema.NewObject("class CAnimTrackAudio");
            track.Set("Name", "T");
            track.Set("KeyList", new List<object?> { Key(schema, entry), Key(schema, entry) });
            var stack = schema.NewObject("class CAnimStackAudio");
            stack.Set("Name", $"S{i}");
            stack.Set("Flags", 0x2u);
            stack.Set("Opacity", 1f);
            stack.Set("AudioTrack", new AktReference(track));
            stack.Set("TimeArray", new List<object?> { StackKey(schema, 0), StackKey(schema, 100) });
            animation.AddChild(stack);
            stack.AddChild(track);
            scene.AddChild(entry);
            stackRefs.Add(new(stack));
        }
        animation.Set("StackList", stackRefs);
        scene.AddChild(animation);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sloppyanimatsu-audio-{Guid.NewGuid():N}.akt");
        new AktDocument(schema, [scene]).Save(path);
        try
        {
            var model = TestFiles.LoadScene(path);
            var anim = model.Scene.Descendants<Animation>().Single();
            var mixer = new AudioMixer(8000, 2, seconds =>
                AudioSchedule.At(anim, seconds, seconds * Fps, Fps));
            var block = mixer.Render(64);
            Assert.Equal(1f, block[0], 3);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// An audio stack added to a real scene's animation leaves the posed characters alone: the
    /// audio track's keys are not transform keys, so the evaluator contributes nothing for it.
    /// </summary>
    [SceneTheory, SceneSamples]
    public void AudioStack_DoesNotChangeThePosedAnimations(string path)
    {
        var model = TestFiles.LoadScene(path);
        static bool HasKeys(Animation a) => a.Stacks.Any(s => s.Tracks.Any(t => t.Target != null));
        var character = model.Scene.Characters.FirstOrDefault(c => c.Animations.Any(HasKeys) || (c.RigAnimation is { } rig && HasKeys(rig)));
        if (character == null) return;
        var animation = character.Animations.FirstOrDefault(HasKeys) ?? character.RigAnimation!;
        int time = animation.TimeRange.Start + 1;
        var before = new PoseEvaluator(character).LocalTransforms(animation, time).ToArray();

        var schema = model.Document.Schema;
        AddAudioClasses(schema);
        var audioObject = schema.NewObject("class CAudioObject");
        audioObject.Set("Name", "Audio Object");
        var entry = schema.NewObject("class CAudioEntry");
        entry.Set("Name", "Audio Entry");
        entry.Set("FileName", "step.wav");
        entry.Set("EmbedBufferData", WaveFile.EncodeMono(Constant(0.5f, 12000), 48000));
        var track = schema.NewObject("class CAnimTrackAudio");
        track.Set("Name", "AudioTrack");
        track.Set("Target", new AktReference(audioObject));
        track.Set("KeyList", new List<object?> { Key(schema, entry) });
        var stack = schema.NewObject("class CAnimStackAudio");
        stack.Set("Name", "Audio");
        stack.Set("Flags", 0x2u);
        stack.Set("Opacity", 1f);
        stack.Set("TimeArray", new List<object?> { StackKey(schema, time), StackKey(schema, time + 40) });
        stack.Set("AudioTrack", new AktReference(track));
        animation.Source.AddChild(stack);
        stack.AddChild(track);
        animation.Source.AddChild(audioObject);
        ((List<AktReference>)animation.Source["StackList"]!).Add(new AktReference(stack));

        var after = new PoseEvaluator(character).LocalTransforms(animation, time).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++) AssertClose(before[i], after[i]);

        var audio = animation.Stacks.OfType<AudioStack>().Single();
        var e = Assert.Single(audio.Events);
        Assert.Equal(time, e.StartFrame);
        Assert.True(e.IsAudibleAt(time, Fps));
        Assert.Single(AudioSchedule.At(animation, time / (double)Fps, time, Fps));
    }

    private static void AssertClose(Transform expected, Transform actual)
    {
        Assert.Equal(expected.Translation.X, actual.Translation.X, 4);
        Assert.Equal(expected.Translation.Y, actual.Translation.Y, 4);
        Assert.Equal(expected.Translation.Z, actual.Translation.Z, 4);
        Assert.Equal(expected.Scale.X, actual.Scale.X, 4);
        Assert.Equal(expected.Scale.Y, actual.Scale.Y, 4);
        Assert.Equal(expected.Scale.Z, actual.Scale.Z, 4);
        Assert.Equal(expected.Rotation.X, actual.Rotation.X, 4);
        Assert.Equal(expected.Rotation.Y, actual.Rotation.Y, 4);
        Assert.Equal(expected.Rotation.Z, actual.Rotation.Z, 4);
        Assert.Equal(expected.Rotation.W, actual.Rotation.W, 4);
    }

    /// <summary>Adds the audio classes to a loaded file's schema when they are missing.</summary>
    private static void AddAudioClasses(AktSchema schema)
    {
        AktClassDef Parent(string name) => schema.FindClass("class " + name)
            ?? throw new InvalidOperationException($"The sample's schema has no class {name}.");
        void Class(string name, string parent, params (string Name, string Type)[] members)
        {
            if (schema.FindClass("class " + name) != null) return;
            var def = new AktClassDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddClass(def, Parent(parent));
        }
        void Struct(string name, params (string Name, string Type)[] members)
        {
            if (schema.FindStruct("class " + name) != null) return;
            var def = new AktStructDef("class " + name);
            foreach (var (member, type) in members) def.Members.Add(new AktMember(member, type));
            schema.AddStruct(def);
        }

        Class("CAudioObject", "CObject3D");
        Class("CAudioEntry", "CDataObject", ("FileName", "QString"), ("EmbedBufferData", "vector<uchar>"));
        Class("CAnimStackAudio", "CAnimStack", ("AudioTrack", "Reference<class CAnimTrackAudio>"));
        Class("CAnimTrackAudio", "CAnimTrackBase");
        Struct("CKeyBase");
        Struct("CKeyAudioFrame", ("DelaySinceStartKey", "uint"), ("Audio", "Key"), ("Volume", "float"));
        Struct("CKeyAudioAnim", ("AudioFile", "Reference<class CAudioEntry>"), ("Volume", "float"), ("Mute", "bool"));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static float[] Constant(float value, int count) => Enumerable.Repeat(value, count).ToArray();

    private static AktStruct Key(AktSchema schema, AktObject entry, float volume = 1f, int delay = 0)
    {
        // A real.akt scene stores the key
        // itself as the CKeyAudioAnim: flat AudioFile, Volume and Mute. A delay member is not
        // written by the sample files; the wrapper below keeps SloppyAnimatsu's tolerant read covered.
        var key = schema.NewPolymorphic("class CKeyAudioAnim");
        key.Set("AudioFile", new AktReference(entry));
        key.Set("Volume", volume);
        key.Set("Mute", false);
        if (delay == 0) return key;
        var carrier = schema.NewPolymorphic("class CKeyAudioFrame");
        carrier.Set("DelaySinceStartKey", (uint)delay);
        carrier.Set("Audio", key);
        return carrier;
    }

    private static AktStruct StackKey(AktSchema schema, int time)
    {
        var k = schema.NewStruct("class CAnimStackKey");
        k.Set("Time", time);
        k.Set("Flags", 0u);
        k.Set("FadeIn", (ushort)0);
        k.Set("FadeOut", (ushort)0);
        return k;
    }
}
