namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Adds the IK and reverse-foot classes and structs that a scene written before those features does
/// not declare: the chain
/// controllers derive from <c>CController3D</c>, their tracks from <c>CAnimTrackMRS</c> through
/// <c>CAnimTrackController3D_IK_RF</c>, and their keys are <c>CKeyIK</c> / <c>CKeyReverseFoot</c>
/// over <c>CKeyMRS</c> with <c>IKBaseRefInfo</c> chains.
/// </summary>
public static class ControllerSchema
{
    public const string Controller3DClass = "CController3D";
    public const string IkBaseClass = "CController3D_IK_RF";
    public const string IkClass = "CControllerIK";
    public const string ReverseFootClass = "CControllerReverseFoot";
    public const string LocatorClass = "CReverseFootLocator_VK";
    public const string IkTrackBaseClass = "CAnimTrackController3D_IK_RF";
    public const string IkTrackClass = "CAnimTrackIK_VK";
    public const string ReverseFootTrackClass = "CAnimTrackReverseFoot_VK";
    public const string IkKeyStruct = "CKeyIK";
    public const string ReverseFootKeyStruct = "CKeyReverseFoot";
    public const string IkTangentListStruct = "CTangentInfoListIK";
    public const string ReverseFootTangentListStruct = "CTangentInfoListReverseFoot";
    public const string RefInfoStruct = "IKBaseRefInfo";

    /// <summary>
    /// Adds the classes and structs needed to write IK controllers and their tracks, and, with
    /// <paramref name="reverseFoot"/>, the reverse-foot controller, its locator and its own track and
    /// key. Returns whether anything was added. A scene with joints is required: its schema must
    /// carry <c>CObject3D</c> and, for the tracks, <c>CAnimTrackMRS</c> with the MRS key structs.
    /// </summary>
    public static bool Ensure(AktSchema schema, bool reverseFoot = false)
    {
        bool added = false;
        var objectClass = schema.FindClass("CObject3D")
                          ?? throw new InvalidOperationException("The scene schema has no CObject3D to derive a controller from.");
        if (schema.FindClass(Controller3DClass) == null)
        {
            var def = new AktClassDef(Controller3DClass);
            def.Members.Add(new AktMember("TargetRenderSizeRatio", "float"));
            schema.AddClass(def, objectClass);
            added = true;
        }
        var controller3D = schema.FindClass(Controller3DClass)!;
        if (schema.FindClass(IkBaseClass) == null)
        {
            schema.AddClass(new AktClassDef(IkBaseClass), controller3D);
            added = true;
        }
        var ikBase = schema.FindClass(IkBaseClass)!;
        if (schema.FindClass(IkClass) == null)
        {
            var def = new AktClassDef(IkClass);
            def.Members.Add(new AktMember("JointsString", "RefArray<class CObject3D>"));
            def.Members.Add(new AktMember("Parents", "RefArray<class CObject3D>"));
            def.Members.Add(new AktMember("DecoratorAxis", "uchar"));
            schema.AddClass(def, ikBase);
            added = true;
        }
        var ik = schema.FindClass(IkClass)!;

        // The track class chain: only a scene that can animate has the MRS track base, and only such
        // a scene needs the IK track and key structs.
        if (schema.FindClass(IkTrackBaseClass) == null && schema.FindClass("CAnimTrackMRS") is { } trackBase)
        {
            schema.AddClass(new AktClassDef(IkTrackBaseClass), trackBase);
            added = true;
        }
        if (schema.FindClass(IkTrackClass) == null && schema.FindClass(IkTrackBaseClass) is { } ikTrackBase)
        {
            schema.AddClass(new AktClassDef(IkTrackClass), ikTrackBase);
            added = true;
        }

        if (schema.FindStruct(RefInfoStruct) == null)
        {
            var def = new AktStructDef(RefInfoStruct);
            def.Members.Add(new AktMember("Translation", "Vec4f"));
            def.Members.Add(new AktMember("Rotation", "Quatf"));
            def.Members.Add(new AktMember("Scale", "Vec3f"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct(IkKeyStruct) == null && schema.FindStruct("CKeyMRS") != null)
        {
            var def = new AktStructDef(IkKeyStruct);
            def.Members.Add(new AktMember("RefArray", "vector<IKBaseRefInfo>"));
            def.Members.Add(new AktMember("Flags", "uint"));
            def.Members.Add(new AktMember("PoleAngle", "float"));
            def.Members.Add(new AktMember("RefPoleAngle", "float"));
            def.Members.Add(new AktMember("RenderKey", "CMRSKey"));
            def.Members.Add(new AktMember("TargetControllerIK", "Reference<class CControllerIK>"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct(IkTangentListStruct) == null && schema.FindStruct("CTangentInfoListMRS") != null)
        {
            schema.AddStruct(new AktStructDef(IkTangentListStruct));
            added = true;
        }

        if (!reverseFoot) return added;
        if (schema.FindClass(ReverseFootClass) == null)
        {
            var def = new AktClassDef(ReverseFootClass);
            def.Members.Add(new AktMember("Shoe", "RefArray<class CReverseFootLocator_VK>"));
            def.Members.Add(new AktMember("RollAxisToes", "Vec4f"));
            def.Members.Add(new AktMember("RollAxisPlant", "Vec4f"));
            schema.AddClass(def, ik);
            added = true;
        }
        if (schema.FindClass(LocatorClass) == null)
        {
            var def = new AktClassDef(LocatorClass);
            def.Members.Add(new AktMember("Type", "uint"));
            def.Members.Add(new AktMember("Category", "uint"));
            def.Members.Add(new AktMember("Index", "uint"));
            def.Members.Add(new AktMember("Controller", "Reference<class CControllerReverseFoot>"));
            schema.AddClass(def, objectClass);
            added = true;
        }
        if (schema.FindClass(ReverseFootTrackClass) == null && schema.FindClass(IkTrackClass) is { } ikTrack)
        {
            schema.AddClass(new AktClassDef(ReverseFootTrackClass), ikTrack);
            added = true;
        }
        if (schema.FindStruct(ReverseFootKeyStruct) == null && schema.FindStruct(IkKeyStruct) != null)
        {
            var def = new AktStructDef(ReverseFootKeyStruct);
            def.Members.Add(new AktMember("RollAngle", "float"));
            def.Members.Add(new AktMember("TwistAngle", "float"));
            def.Members.Add(new AktMember("BankAngle", "float"));
            def.Members.Add(new AktMember("LocalDrivingRefPoleAngle", "float"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct(ReverseFootTangentListStruct) == null && schema.FindStruct(IkTangentListStruct) != null)
        {
            schema.AddStruct(new AktStructDef(ReverseFootTangentListStruct));
            added = true;
        }
        return added;
    }
}
