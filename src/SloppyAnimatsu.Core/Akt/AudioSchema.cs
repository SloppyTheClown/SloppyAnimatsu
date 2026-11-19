namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Adds the audio classes and structs a scene written before audio does not declare
/// the scene's sound entries, the character's audio anchor, the
/// audio stack, its track and the key struct. The member lists and their order are the ones the
/// <c>audio_sample.akt</c> sample declares.
/// </summary>
public static class AudioSchema
{
    public const string EntryClass = "CAudioEntry";
    public const string ObjectClass = "CAudioObject";
    public const string StackClass = "CAnimStackAudio";
    public const string TrackClass = "CAnimTrackAudio";
    public const string KeyStruct = "CKeyAudioAnim";

    /// <summary>
    /// Adds the audio classes and structs the scene lacks. Returns whether anything was added. A
    /// class the scene cannot derive from (no <c>CDataObject</c>/<c>CAnimStack</c>/
    /// <c>CAnimTrackBase</c>) is left out rather than invented.
    /// </summary>
    public static bool Ensure(AktSchema schema)
    {
        bool added = false;
        var data = schema.FindClass("CDataObject");
        var object3D = schema.FindClass("CObject3D");
        if (data == null) return false;

        if (schema.FindClass(EntryClass) == null)
        {
            var def = new AktClassDef(EntryClass);
            // The order, read from its audio_sample.akt: the embedded sound comes first.
            def.Members.Add(new AktMember("EmbedBufferData", "vector<uchar>"));
            def.Members.Add(new AktMember("FileName", "QString"));
            schema.AddClass(def, data);
            added = true;
        }

        if (object3D != null && schema.FindClass(ObjectClass) == null)
        {
            schema.AddClass(new AktClassDef(ObjectClass), object3D);
            added = true;
        }

        var stack = schema.FindClass("CAnimStack");
        if (stack != null && schema.FindClass(StackClass) == null)
        {
            var def = new AktClassDef(StackClass);
            def.Members.Add(new AktMember("AudioTrack", $"Reference<class {TrackClass}>"));
            schema.AddClass(def, stack);
            added = true;
        }

        var track = schema.FindClass("CAnimTrackBase");
        if (track != null && schema.FindClass(TrackClass) == null)
        {
            schema.AddClass(new AktClassDef(TrackClass), track);
            added = true;
        }

        if (schema.FindStruct(KeyStruct) == null && schema.FindStruct("CKeyBase") != null)
        {
            var def = new AktStructDef(KeyStruct);
            def.Members.Add(new AktMember("AudioFile", $"Reference<class {EntryClass}>"));
            def.Members.Add(new AktMember("Volume", "float"));
            def.Members.Add(new AktMember("Mute", "bool"));
            schema.AddStruct(def);
            added = true;
        }
        return added;
    }
}