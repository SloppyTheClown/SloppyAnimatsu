namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// The animation layer every keyed controller needs: the joint class, the MRS track class and the
/// MRS key and tangent-list structs. A scene made by File > New declares none of them
/// so the first track created in it adds them with the member
/// lists of the .akt schema. Existing scenes have them all, and then this adds
/// nothing.
/// </summary>
public static class AnimationSchema
{
    /// <summary>Adds the joint and MRS track/key classes and structs that are missing; returns whether anything was added.</summary>
    public static bool Ensure(AktSchema schema)
    {
        bool added = false;
        if (schema.FindClass("CJoint") == null && schema.FindClass("CObject3D") is { } object3D)
        {
            var def = new AktClassDef("CJoint");
            def.Members.Add(new AktMember("SmoothMethod", "int"));
            schema.AddClass(def, object3D);
            added = true;
        }
        if (schema.FindClass("CAnimTrackBase") == null && schema.FindClass("CDataObject") is { } dataObject)
        {
            var def = new AktClassDef("CAnimTrackBase");
            def.Members.Add(new AktMember("ChannelMask", "uint"));
            def.Members.Add(new AktMember("Target", "Reference<class CObject3D>"));
            def.Members.Add(new AktMember("KeyList", "vector<Key>"));
            def.Members.Add(new AktMember("TInList", "vector<Key>"));
            def.Members.Add(new AktMember("TOutList", "vector<Key>"));
            def.Members.Add(new AktMember("TangentInfoArray", "vector<TangentInfoList>"));
            schema.AddClass(def, dataObject);
            added = true;
        }
        if (schema.FindClass("CAnimTrackMRS") == null && schema.FindClass("CAnimTrackBase") is { } trackBase)
        {
            schema.AddClass(new AktClassDef("CAnimTrackMRS"), trackBase);
            added = true;
        }
        if (schema.FindStruct("CKeyBase") == null)
        {
            schema.AddStruct(new AktStructDef("CKeyBase"));
            added = true;
        }
        if (schema.FindStruct("CMRSKey") == null)
        {
            var def = new AktStructDef("CMRSKey");
            def.Members.Add(new AktMember("TranslationX", "float"));
            def.Members.Add(new AktMember("TranslationY", "float"));
            def.Members.Add(new AktMember("TranslationZ", "float"));
            def.Members.Add(new AktMember("Rotation", "Quatf"));
            def.Members.Add(new AktMember("ScaleX", "float"));
            def.Members.Add(new AktMember("ScaleY", "float"));
            def.Members.Add(new AktMember("ScaleZ", "float"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct("CKeyMRS") == null && schema.FindStruct("CMRSKey") != null)
        {
            var def = new AktStructDef("CKeyMRS");
            def.Members.Add(new AktMember("Key", "CMRSKey"));
            def.Members.Add(new AktMember("Space", "Reference<class CObject3D>"));
            def.Members.Add(new AktMember("TrackMRS", "Reference<class CAnimTrackMRS>"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct("CTangentInfo") == null)
        {
            var def = new AktStructDef("CTangentInfo");
            def.Members.Add(new AktMember("TangentMode", "uint"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct("CTangentInfoListBase") == null && schema.FindStruct("CTangentInfo") != null)
        {
            var def = new AktStructDef("CTangentInfoListBase");
            def.Members.Add(new AktMember("TangentInfoChannels", "vector<CTangentInfo>"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct("CTangentInfoListMRS") == null && schema.FindStruct("CTangentInfoListBase") != null)
        {
            schema.AddStruct(new AktStructDef("CTangentInfoListMRS"));
            added = true;
        }
        return added;
    }
}