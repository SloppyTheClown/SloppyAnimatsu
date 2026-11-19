namespace SloppyAnimatsu.Core.Akt;

/// <summary>
/// Adds the camera-animation classes and structs that a scene written before this feature does not
/// declare. Their member layout comes from the application's
/// registrations: <c>CCameraAnimated</c> is a <c>CCamera</c> with the photo fields,
/// <c>CAnimTrackCamera</c> adds nothing to an MRS track, and <c>CKeyCamera</c> adds
/// <c>Flags</c> and <c>FOV</c> to an MRS key.
/// </summary>
public static class CameraSchema
{
    public const string AnimatedCameraClass = "CCameraAnimated";
    public const string CameraTrackClass = "CAnimTrackCamera";
    public const string CameraKeyStruct = "CKeyCamera";
    public const string CameraTangentListStruct = "CTangentInfoListCamera";

    // The editor's orbit rig: a
    // hidden joint under the camera, a <c>CLooker</c> target under the character, and a looker
    // track. The registrations: <c>CHideJointForLooker</c> is a <c>CJoint</c>,
    // <c>CLooker</c> an IK controller, <c>CCameraTarget</c> a <c>CLooker</c> and
    // <c>CAnimTrackLooker_VK</c> an IK track; none of the four adds a member.
    public const string LookerClass = "CLooker";
    public const string CameraTargetClass = "CCameraTarget";
    public const string HideJointClass = "CHideJointForLooker";
    public const string LookerTrackClass = "CAnimTrackLooker_VK";

    /// <summary>Adds the camera class, and the camera track/key structs when the scene has their bases
    /// (<c>CAnimTrackMRS</c>, <c>CKeyMRS</c>, <c>CTangentInfoListMRS</c>); returns whether anything
    /// was added. A scene without animation gets only <c>CCameraAnimated</c>, which is all it needs;
    /// a scene with cameras is required, and its absence throws.
    /// </summary>
    public static bool Ensure(AktSchema schema)
    {
        bool added = false;
        if (schema.FindClass(AnimatedCameraClass) == null)
        {
            var parent = schema.FindClass("CCamera")
                         ?? throw new InvalidOperationException("The scene schema has no CCamera to derive CCameraAnimated from.");
            var def = new AktClassDef(AnimatedCameraClass);
            def.Members.Add(new AktMember("Mode", "int"));
            def.Members.Add(new AktMember("Ratio", "int"));
            def.Members.Add(new AktMember("NearClipPlane", "float"));
            def.Members.Add(new AktMember("FarClipPlane", "float"));
            schema.AddClass(def, parent);
            added = true;
        }
        // A scene without any animation has no track/key classes, and needs none: only the camera
        // class is added there. A scene that can animate has the bases, and gets the track and the
        // key structs too.
        if (schema.FindClass(CameraTrackClass) == null && schema.FindClass("CAnimTrackMRS") is { } trackBase)
        {
            schema.AddClass(new AktClassDef(CameraTrackClass), trackBase);
            added = true;
        }
        if (schema.FindStruct(CameraKeyStruct) == null && schema.FindStruct("CKeyMRS") != null)
        {
            var def = new AktStructDef(CameraKeyStruct);
            def.Members.Add(new AktMember("Flags", "uint"));
            def.Members.Add(new AktMember("FOV", "float"));
            schema.AddStruct(def);
            added = true;
        }
        if (schema.FindStruct(CameraTangentListStruct) == null && schema.FindStruct("CTangentInfoListMRS") != null)
        {
            schema.AddStruct(new AktStructDef(CameraTangentListStruct));
            added = true;
        }
        return added;
    }

    /// <summary>
    /// Adds everything an animated camera with its editor rig needs, including the animation layer a
    /// brand-new scene lacks (<c>CJoint</c>, the MRS track and key structs, <c>IKBaseRefInfo</c>,
    /// <c>CKeyIK</c>): <see cref="Ensure"/>, <see cref="ControllerSchema.Ensure"/> and the four rig
    /// classes. Returns whether anything was added; a second call adds nothing.
    /// </summary>
    public static bool EnsureRig(AktSchema schema)
    {
        bool added = AnimationSchema.Ensure(schema);
        added |= Ensure(schema);
        added |= ControllerSchema.Ensure(schema);
        if (schema.FindClass(LookerClass) == null && schema.FindClass(ControllerSchema.IkClass) is { } ik)
        {
            schema.AddClass(new AktClassDef(LookerClass), ik);
            added = true;
        }
        if (schema.FindClass(CameraTargetClass) == null && schema.FindClass(LookerClass) is { } looker)
        {
            schema.AddClass(new AktClassDef(CameraTargetClass), looker);
            added = true;
        }
        if (schema.FindClass(HideJointClass) == null && schema.FindClass("CJoint") is { } joint)
        {
            schema.AddClass(new AktClassDef(HideJointClass), joint);
            added = true;
        }
        if (schema.FindClass(LookerTrackClass) == null && schema.FindClass(ControllerSchema.IkTrackClass) is { } ikTrack)
        {
            schema.AddClass(new AktClassDef(LookerTrackClass), ikTrack);
            added = true;
        }
        return added;
    }
}
